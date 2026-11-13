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
        // The extractor's compiler facts arrive under the same keys the C# route records, so index_status shows them alike.
        var meta = store.ReadMeta();
        Assert.StartsWith("typescript ", meta[MetaKeys.Language(Languages.TypeScript, LanguageMetaKeys.Compiler)]);
        Assert.Equal("ES2022", meta[MetaKeys.Language(Languages.TypeScript, LanguageMetaKeys.LanguageVersions)]);
        Assert.Equal("0", meta[MetaKeys.Language(Languages.TypeScript, LanguageMetaKeys.CompileErrors)]);
        Assert.Contains(LanguageStatus.Lines(meta), line => line.StartsWith("typescript: typescript ") && line.Contains("code uses ES2022"));

        // Re-indexing is idempotent.
        var before = store.Count();
        store.Dispose();
        await Index(fixture, db);
        using var again = GraphStore.Open(db, readOnly: true, TimeSpan.FromSeconds(5));
        Assert.Equal(before, again.Count());
    }
}

/// <summary>
/// The TypeScript daemon end to end: codegraph-index --watch on a copy of the fixture, then live requests over the
/// graph's pipe, answered by typescript-language-server. Skipped where Node or the extractor's packages are missing.
/// </summary>
public sealed class TypeScriptLiveTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"))).FullName;
    private System.Diagnostics.Process? _daemon;

    public void Dispose()
    {
        if (_daemon is { HasExited: false })
        {
            _daemon.Kill(entireProcessTree: true);
            _daemon.WaitForExit(10_000);
        }

        _daemon?.Dispose();
        TempDb.TryDelete(_root);
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.GetDirectories(from))
        {
            Copy(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }

    [Fact]
    public async Task References_rename_and_a_what_if_edit_come_from_the_language_server()
    {
        string? package = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && package is null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "extractors", "typescript");
            if (File.Exists(Path.Combine(candidate, "node_modules", "typescript-language-server", "lib", "cli.mjs")))
            {
                package = candidate;
            }
        }

        Assert.SkipWhen(package is null, "the extractor's npm packages are not installed");
        var workspace = Path.Combine(_root, "shop");
        Copy(Path.Combine(package, "test", "fixtures", "angular-app"), workspace);
        Directory.CreateDirectory(Path.Combine(workspace, ".git"));
        var db = Path.Combine(workspace, ".codegraph", "graph.db");
        var dotnet = Path.Combine(CodeGraph.Core.DotnetRuntime.Root()!, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        _daemon = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dotnet,
            [Path.Combine(AppContext.BaseDirectory, "codegraph-index.dll"), workspace, "--watch", "--idle-exit", "5", "--log", Path.Combine(_root, "daemon.log")])
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

        var live = new CodeGraph.Core.Live.LiveClient(db);
        var ready = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        CodeGraph.Core.Live.LiveStatus? status = null;
        while (DateTime.UtcNow < ready && status is not { Loading: false })
        {
            await Task.Delay(500, TestContext.Current.CancellationToken);
            status = await live.PingAsync(TestContext.Current.CancellationToken);
        }

        Assert.True(status is { Loading: false }, File.Exists(Path.Combine(_root, "daemon.log")) ? File.ReadAllText(Path.Combine(_root, "daemon.log")) : "no daemon log");

        const string FormatName = "ts:src/app/user.service.ts#UserService.formatName";
        var references = await live.CallAsync<CodeGraph.Core.Live.LiveReferences>("references", new { symbol = FormatName, limit = 50 }, TestContext.Current.CancellationToken);
        string Log() => File.Exists(Path.Combine(_root, "daemon.log")) ? File.ReadAllText(Path.Combine(_root, "daemon.log")) : "no daemon log";
        Assert.True(references!.References.Count == 2, $"references: {System.Text.Json.JsonSerializer.Serialize(references)}\n{Log()}");
        Assert.Equal(["ts:src/app/user-card.component.ts#UserCardComponent.display", "ts:src/app/user.service.spec.ts#UserService > formats names"],
            references.References.Select(r => r.EnclosingMemberId).Order(StringComparer.Ordinal));

        var rename = await live.CallAsync<CodeGraph.Core.Live.LiveRename>("rename_preview", new { symbol = FormatName, newName = "displayName" }, TestContext.Current.CancellationToken);
        Assert.True(rename!.Files.Count == 3, $"rename: {System.Text.Json.JsonSerializer.Serialize(rename)}\n{Log()}");
        Assert.Contains(rename.Files.SelectMany(f => f.Lines), l => l.After.Contains("this.service.displayName(this.user)"));

        var service = Path.Combine(workspace, "src", "app", "user.service.ts");
        var text = File.ReadAllText(service).Replace("formatName(user: User): string", "formatName(user: User, upper: boolean): string");
        var check = await live.CallAsync<CodeGraph.Core.Live.LiveCheck>("check_change", new { file = service, text }, TestContext.Current.CancellationToken);
        Assert.True(check!.NewErrors.Count == 2, $"check: {System.Text.Json.JsonSerializer.Serialize(check)}\n{Log()}");
        Assert.All(check.NewErrors, e => Assert.Contains("Expected 2 arguments", e.Message));
        Assert.Contains(check.NewErrors, e => e.File!.EndsWith("user-card.component.ts", StringComparison.Ordinal));
    }
}
