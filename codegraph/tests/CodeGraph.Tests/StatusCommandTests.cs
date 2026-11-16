using System.Text.Json;
using CodeGraph.Core;
using CodeGraph.Core.Live;
using CodeGraph.Indexer;

namespace CodeGraph.Tests;

/// <summary><c>codegraph-index status</c> and <c>stop</c>: what the VS Code extension reads and calls.</summary>
public sealed class StatusCommandTests
{
    [Fact]
    public async Task A_folder_with_no_graph_reports_it_missing_and_exits_zero()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"));
        var dbPath = Path.Combine(directory, ".codegraph", "graph.db");

        var output = new StringWriter();
        Assert.Equal(0, await StatusCommand.RunAsync(["--db", dbPath, "--json"], output, TextWriter.Null, "9.9.9"));

        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.Equal("9.9.9", root.GetProperty("version").GetString());
        Assert.Equal(dbPath, root.GetProperty("db").GetString());
        Assert.False(root.GetProperty("exists").GetBoolean());
        Assert.False(root.TryGetProperty("graph", out _));
        Assert.False(root.TryGetProperty("daemon", out _));
        Assert.False(root.TryGetProperty("usage", out _));
    }

    [Fact]
    public async Task A_graph_reports_its_counts_per_language_its_freshness_and_its_usage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(directory);
            var csharp = Path.Combine(directory, "Service.cs");
            var typescript = Path.Combine(directory, "app.ts");
            var deleted = Path.Combine(directory, "Gone.cs");
            File.WriteAllText(csharp, "class Service {}");
            File.WriteAllText(typescript, "export const x = 1;");
            File.WriteAllText(deleted, "class Gone {}");
            var dbPath = Path.Combine(directory, ".codegraph", "graph.db");

            var meta = new Dictionary<string, string>
            {
                [MetaKeys.SchemaVersion] = GraphStore.SchemaVersion,
                [MetaKeys.Solution] = Path.Combine(directory, "App.slnx"),
                [MetaKeys.IndexedAt] = "2026-11-16T08:00:00.0000000Z",
                [MetaKeys.Mode] = "full",
                [MetaKeys.Language(Languages.CSharp, LanguageMetaKeys.Compiler)] = "Roslyn 5.9.0",
                [MetaKeys.Language(Languages.CSharp, LanguageMetaKeys.CompileErrors)] = "2",
                [MetaKeys.Language(Languages.CSharp, LanguageMetaKeys.Notes)] = "first note; second note",
            };
            using (var store = GraphStore.Open(dbPath, readOnly: false, TimeSpan.FromSeconds(5)))
            {
                store.Replace(true, [], [Graph.Node("T:Service", NodeKind.Type, "Service", "Service", filePath: csharp), Graph.Node("T:Gone", NodeKind.Type, "Gone", "Gone", filePath: deleted)], [], [],
                    [FileStamp.Of(csharp), FileStamp.Of(deleted)], meta);
                store.Replace(false, [], [Graph.Node("ts:app.ts#x", NodeKind.Field, "x", "x", filePath: typescript) with { Language = Languages.TypeScript }], [], [],
                    [FileStamp.Of(typescript)], new Dictionary<string, string>(), language: Languages.TypeScript);
            }

            File.Delete(deleted);
            File.WriteAllText(csharp, "class Service { void Edited() {} }");
            new Telemetry(UsageCommand.TelemetryPath(dbPath)).Begin("impact_of").Complete(resultChars: 400, isError: false);

            var report = await StatusCommand.ReadAsync(dbPath, "9.9.9");

            Assert.True(report.Exists);
            Assert.Null(report.Error);
            Assert.Null(report.Daemon);
            var graph = Assert.IsType<GraphStatus>(report.Graph);
            Assert.True(graph.SchemaCurrent);
            Assert.Equal(meta[MetaKeys.Solution], graph.Solution);
            Assert.Equal((3, 0), (graph.Nodes, graph.Edges));
            Assert.True(graph.Stale);
            Assert.Equal(1, graph.ChangedCount);
            Assert.Equal([csharp], graph.ChangedFiles);
            Assert.Equal(1, graph.MissingCount);
            Assert.Equal([deleted], graph.MissingFiles);

            Assert.Equal(["csharp", "typescript"], graph.Languages.Select(l => l.Name));
            var cs = graph.Languages[0];
            Assert.Equal((2, 2), (cs.Nodes, cs.Files));
            Assert.Equal("Roslyn 5.9.0", cs.Compiler);
            Assert.Equal(2, cs.CompileErrors);
            Assert.Equal(["first note", "second note"], cs.Notes);
            Assert.Equal((1, 1), (graph.Languages[1].Nodes, graph.Languages[1].Files));

            var usage = Assert.IsType<UsageReport>(report.Usage);
            Assert.Equal(1, usage.Calls);
            Assert.Equal("impact_of", Assert.Single(usage.TopTools).Tool);
            Assert.StartsWith("last 7 days: 1 tool calls answered", usage.Line);

            var text = StatusCommand.Text(report);
            Assert.Contains("STALE (1 edited, 1 deleted)", text);
            Assert.Contains("csharp    : 2 nodes, 2 files, Roslyn 5.9.0, 2 compile error(s)", text);
            Assert.Contains("daemon    : not running", text);
        }
        finally
        {
            TempDb.TryDelete(directory);
        }
    }

    [Fact]
    public async Task A_damaged_graph_is_reported_not_thrown()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"));
        try
        {
            var dbPath = Path.Combine(directory, ".codegraph", "graph.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            File.WriteAllText(dbPath, "this is not a database");

            var report = await StatusCommand.ReadAsync(dbPath, "9.9.9");

            Assert.True(report.Exists);
            Assert.Null(report.Graph);
            Assert.NotNull(report.Error);
        }
        finally
        {
            TempDb.TryDelete(directory);
        }
    }

    [Fact]
    public async Task Status_reports_a_running_daemon_and_stop_asks_it_to_stop()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"), "graph.db");
        using var stopping = new CancellationTokenSource();
        var stopRequests = 0;
        var status = new LiveStatus(4242, "App.slnx", dbPath, 3, 40, DateTime.UnixEpoch, null, null, 0, false);
        var server = new LiveServer(LivePipe.NameFor(dbPath), (request, _) =>
        {
            if (request.Method == "stop")
            {
                // What both daemons do: answer, then cancel their own token.
                Interlocked.Increment(ref stopRequests);
                stopping.CancelAfter(TimeSpan.FromMilliseconds(50));
            }

            return Task.FromResult(LiveResponse.Ok(status));
        });
        var serving = server.RunAsync(stopping.Token);

        var report = await StatusCommand.ReadAsync(dbPath, "9.9.9");
        Assert.Equal(4242, report.Daemon?.Pid);
        Assert.Contains("daemon    : pid 4242, watching, 3 project(s), 40 document(s)", StatusCommand.Text(report));

        var output = new StringWriter();
        Assert.Equal(0, await StatusCommand.StopAsync(["--db", dbPath], output, TextWriter.Null));
        Assert.Equal(1, stopRequests);
        Assert.StartsWith("stopped the daemon (pid 4242)", output.ToString());
        await serving;

        var again = new StringWriter();
        Assert.Equal(0, await StatusCommand.StopAsync(["--db", dbPath], again, TextWriter.Null));
        Assert.StartsWith("no daemon serves", again.ToString());
        Assert.Equal(1, await StatusCommand.StopAsync(["--bogus"], TextWriter.Null, TextWriter.Null));
    }
}
