using CodeGraph.Core;

namespace CodeGraph.Tests;

/// <summary>
/// codegraph-index on the extractor's Angular fixture, end to end: Node extractor, records, import, then graph
/// queries as the MCP tools make them. Skipped where Node or the extractor's npm packages are not installed.
/// </summary>
public sealed class TypeScriptIndexTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"))).FullName;

    public void Dispose() => TempDb.TryDelete(_root);

    private static string? Extractor()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var cli = Path.Combine(dir.FullName, "extractors", "typescript", "cli.mjs");
            if (File.Exists(cli))
            {
                return Directory.Exists(Path.Combine(dir.FullName, "extractors", "typescript", "node_modules", "typescript")) ? cli : null;
            }
        }

        return null;
    }

    private static bool NodeAvailable()
    {
        try
        {
            using var node = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("node", ["--version"]) { RedirectStandardOutput = true, UseShellExecute = false });
            node!.WaitForExit();
            return node.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private async Task<int> Index(string target, string db)
    {
        var dotnet = Path.Combine(DotnetRuntime.Root()!, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var info = new System.Diagnostics.ProcessStartInfo(dotnet, [Path.Combine(AppContext.BaseDirectory, "codegraph-index.dll"), target, "--db", db])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, await error);
        return process.ExitCode;
    }

    [Fact]
    public async Task An_Angular_workspace_answers_impact_tests_and_dead_code()
    {
        var cli = Extractor();
        Assert.SkipUnless(cli is not null && NodeAvailable(), "Node.js or the extractor's npm packages are not installed");
        var fixture = Path.Combine(Path.GetDirectoryName(cli)!, "test", "fixtures", "angular-app");
        var db = Path.Combine(_root, "graph.db");

        await Index(fixture, db);

        using var store = GraphStore.Open(db, readOnly: true, TimeSpan.FromSeconds(5));
        const string formatName = "ts:src/app/user.service.ts#UserService.formatName";
        var impact = store.Impact(formatName, 5);
        Assert.Contains(impact, h => h.Node.Id == "ts:src/app/user-card.component.ts#UserCardComponent.display");
        // Through the template, a change to formatName reaches what the component renders.
        Assert.Contains(impact, h => h.Node.Name == "<template>");
        Assert.Contains(store.TransitiveTests(formatName, 30), t => t.Node.Name == "formats names");
        var (_, dead) = store.DeadCodeReport();
        Assert.Equal(["#secret", "unusedHelper"], dead.Select(d => d.Method.Name).Order(StringComparer.Ordinal));
        Assert.All(store.AllNodes(NodeKind.Method), n => Assert.Equal(Languages.TypeScript, n.Language));
        Assert.Equal(Path.Combine(fixture, "angular.json"), store.ReadMeta()[MetaKeys.Solution]);

        // Re-indexing is idempotent.
        var before = store.Count();
        store.Dispose();
        await Index(fixture, db);
        using var again = GraphStore.Open(db, readOnly: true, TimeSpan.FromSeconds(5));
        Assert.Equal(before, again.Count());
    }
}
