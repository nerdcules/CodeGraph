using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Client;
using CodeGraph.Contracts;

namespace CodeGraph.Tests;

/// <summary>
/// The codegraph CLI prints what ICodeGraph returns for the same input: each command's JSON equals the client's
/// result serialised with the contract's JSON. Errors go to stderr with exit 1, usage errors with exit 2. One test
/// runs the published Native AOT binary when it exists (bench/.out/cli/codegraph).
/// </summary>
[Collection(ShopCollection.Name)]
public sealed class CliParityTests(ShopFixture shop)
{
    private static readonly JsonSerializerOptions Relaxed = new(ContractsJson.Default.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly ContractsJson Contract = new(Relaxed);

    private async Task<(int Exit, string Stdout, string Stderr)> Cli(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await CodeGraphCli.RunAsync([.. args, "--db", shop.DbPath], stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, typeof(T), Contract);

    private async Task Same<T>(T expected, params string[] args)
    {
        var (exit, stdout, stderr) = await Cli(args);
        Assert.True(exit == 0, $"codegraph {string.Join(' ', args)} exited {exit}: {stderr}");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Json(expected)), JsonNode.Parse(stdout)), $"codegraph {string.Join(' ', args)}:\n{stdout}\nexpected\n{Json(expected)}");
    }

    [Fact]
    public async Task Every_command_prints_what_the_client_returns()
    {
        using var g = CodeGraphClient.Open(shop.DbPath);
        var cancel = TestContext.Current.CancellationToken;

        await Same(await g.FindAsync(new SymbolQuery("Save"), cancel), "find", "Save");
        await Same(await g.FindAsync(new SymbolQuery("Order") { Kind = "Type", Match = "StartsWith", Limit = 2, Offset = 1 }, cancel),
            "find", "Order", "--kind", "Type", "--match", "StartsWith", "--limit", "2", "--offset", "1");
        await Same(await g.OutlineAsync("T:Shop.SqlRepository", cancellationToken: cancel), "outline", "T:Shop.SqlRepository");
        await Same(await g.SnippetAsync("Validate", 2, cancel), "snippet", "Validate", "--context", "2");
        await Same(await g.CallersAsync("IRepository.Save", 3, int.MaxValue, cancel), "callers", "IRepository.Save", "--depth", "3", "--limit", "0");
        await Same(await g.CallersAsync("IRepository.Save", 3, 1, cancel), "callers", "IRepository.Save", "--depth", "3", "--limit", "1");
        await Same(await g.CalleesAsync("OrderService.Place", 2, includeExternal: true, cancellationToken: cancel), "callees", "OrderService.Place", "--depth", "2", "--external");
        await Same(await g.ImpactAsync("IRepository.Save", cancellationToken: cancel), "impact", "IRepository.Save");
        await Same(await g.TestsAsync("SqlRepository.Save", cancellationToken: cancel), "tests", "SqlRepository.Save");
        await Same(await g.QueryAsync("SELECT kind, count(*) FROM nodes GROUP BY kind ORDER BY kind", 3, cancellationToken: cancel),
            "query", "SELECT kind, count(*) FROM nodes GROUP BY kind ORDER BY kind", "--limit", "3");
        await Same(await g.SchemaAsync(cancel), "schema");
        await Same(await g.StatusAsync(cancel), "status");
    }

    [Fact]
    public async Task Errors_go_to_stderr_with_an_exit_code()
    {
        var ambiguous = await Cli("callers", "Save");
        Assert.Equal(1, ambiguous.Exit);
        Assert.Empty(ambiguous.Stdout);
        Assert.StartsWith("codegraph: 2 symbols are named Save; pass one id:", ambiguous.Stderr);
        Assert.Contains("M:Shop.SqlRepository.Save(Shop.Order)", ambiguous.Stderr);

        Assert.Equal(1, (await Cli("impact", "Nope")).Exit);
        Assert.Equal(1, (await Cli("query", "DELETE FROM nodes")).Exit);
        Assert.Equal(2, (await Cli("bogus")).Exit);
        Assert.Equal(2, (await Cli("callers", "Save", "--colour", "red")).Exit);
        Assert.Equal(2, (await Cli("callers", "Save", "--depth", "two")).Exit);
        Assert.Equal(2, (await Cli("callers")).Exit);
    }

    [Fact]
    public async Task The_published_aot_binary_answers_like_the_client()
    {
        var binary = Path.Combine(BenchDir(), ".out", "cli", OperatingSystem.IsWindows() ? "codegraph.exe" : "codegraph");
        if (!File.Exists(binary))
        {
            Assert.Skip($"no published binary at {binary}; run dotnet publish src/CodeGraph.Cli -c Release -r <rid> -o bench/.out/cli");
        }

        using var g = CodeGraphClient.Open(shop.DbPath);
        foreach (var (args, expected) in new (string[], string)[]
                 {
                     (["status"], Json(await g.StatusAsync(TestContext.Current.CancellationToken))),
                     (["callers", "IRepository.Save", "--depth", "3"], Json(await g.CallersAsync("IRepository.Save", 3, cancellationToken: TestContext.Current.CancellationToken))),
                 })
        {
            var info = new ProcessStartInfo(binary) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args.Concat(["--db", shop.DbPath]))
            {
                info.ArgumentList.Add(arg);
            }

            using var process = Process.Start(info)!;
            var stdout = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(stdout)), stdout);
        }
    }

    private static string BenchDir() => Path.GetDirectoryName(CodeGraph.ModeBench.BenchConfig.DefaultPath)!;
}
