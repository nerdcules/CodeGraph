using System.Diagnostics;
using CodeGraph.Core.Live;
using CodeGraph.Indexer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CodeGraph.Tests;

/// <summary>The pipe between the MCP server and the daemon: one JSON line each way, errors travel as errors, absence is cheap.</summary>
public sealed class LivePipeTests
{
    [Fact]
    public async Task A_request_round_trips_and_a_handler_error_becomes_a_LiveException()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"), "graph.db");
        using var stop = new CancellationTokenSource();
        var server = new LiveServer(LivePipe.NameFor(dbPath), (request, _) => Task.FromResult(request.Method switch
        {
            "ping" => LiveResponse.Ok(new LiveStatus(1, "s.sln", "g.db", 2, 3, DateTime.UnixEpoch, null, null, 0, false)),
            _ => throw new LiveException($"unknown method '{request.Method}'"),
        }));
        var serving = server.RunAsync(stop.Token);

        var client = new LiveClient(dbPath);
        var status = await client.PingAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(status);
        Assert.Equal((2, 3), (status.Projects, status.Documents));

        var error = await Assert.ThrowsAsync<LiveException>(() => client.CallAsync<string>("nope", null, TestContext.Current.CancellationToken));
        Assert.Contains("unknown method", error.Message);

        stop.Cancel();
        await serving;
    }

    [Fact]
    public async Task The_endpoint_stays_visible_between_accepts_under_rapid_calls()
    {
        // The client trusts LivePipe.Exists; a listener that blinks out after each accept would read as "no daemon".
        var dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"), "graph.db");
        using var stop = new CancellationTokenSource();
        var server = new LiveServer(LivePipe.NameFor(dbPath), (_, _) =>
            Task.FromResult(LiveResponse.Ok(new LiveStatus(1, "s", "g", 0, 0, DateTime.UnixEpoch, null, null, 0, false))));
        var serving = server.RunAsync(stop.Token);
        var client = new LiveClient(dbPath);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        var missing = 0;
        for (var i = 0; i < 200; i++)
        {
            if (await client.PingAsync(TestContext.Current.CancellationToken) is null)
            {
                missing++;
            }
        }

        stop.Cancel();
        await serving;
        Assert.Equal(0, missing);
        Assert.False(LivePipe.Exists(LivePipe.NameFor(dbPath)), "the endpoint goes when the server stops");
    }

    [Fact]
    public async Task Pinging_a_graph_with_no_daemon_returns_null_without_waiting_for_a_connect_timeout()
    {
        // The server pings before every tool call, so this is on every call's path when no daemon runs.
        var client = new LiveClient(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"), "graph.db"));
        Assert.Null(await client.PingAsync(TestContext.Current.CancellationToken));

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            Assert.Null(await client.PingAsync(TestContext.Current.CancellationToken));
        }

        Assert.Null(await client.FlushAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(50), $"21 probes took {clock.Elapsed.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public async Task A_socket_left_by_a_dead_daemon_costs_one_short_timeout_then_is_remembered()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Windows pipes vanish with their process; there is no file to leave behind
        }

        var dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"), "graph.db");
        var socket = LivePipe.SocketPath(LivePipe.NameFor(dbPath));
        using (var orphan = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified))
        {
            orphan.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(socket)); // bound, never listening: what a kill -9 leaves
        }

        try
        {
            var client = new LiveClient(dbPath);
            var first = Stopwatch.StartNew();
            Assert.Null(await client.PingAsync(TestContext.Current.CancellationToken));
            Assert.True(first.Elapsed < TimeSpan.FromSeconds(1), $"first probe took {first.Elapsed}");

            var rest = Stopwatch.StartNew();
            for (var i = 0; i < 10; i++)
            {
                Assert.Null(await client.PingAsync(TestContext.Current.CancellationToken));
            }

            Assert.True(rest.Elapsed < TimeSpan.FromMilliseconds(20), $"remembered probes took {rest.Elapsed.TotalMilliseconds:F1} ms");
        }
        finally
        {
            File.Delete(socket);
        }
    }

    [Fact]
    public void Pipe_names_are_stable_short_and_per_graph()
    {
        var a = LivePipe.NameFor("/repo/.codegraph/graph.db");
        Assert.Equal(a, LivePipe.NameFor("/repo/.codegraph/../.codegraph/graph.db"));
        Assert.NotEqual(a, LivePipe.NameFor("/other/.codegraph/graph.db"));
        Assert.StartsWith("codegraph-", a);
        Assert.True(a.Length < 40);
    }
}

/// <summary>The Roslyn answers behind symbol_at, live_references, diagnostics and check_change, over an adhoc solution.</summary>
public sealed class LiveQueriesTests
{
    private const string Lib = """
        namespace N;
        public class Model { public int Size { get; set; } }
        public static class Files
        {
            public static void Clean(string path) { System.IO.File.Delete(path); }
            public static Model Load(string path) { var m = new Model(); m.Size = System.IO.File.ReadAllText(path).Length; return m; }
        }
        """;

    private const string App = """
        namespace N;
        public class Program
        {
            public static void Main() { Files.Clean("a"); var m = Files.Load("b"); System.Console.WriteLine(m.Size); }
        }
        """;

    [Fact]
    public async Task SymbolAt_names_the_symbol_under_a_position_and_its_enclosing_member()
    {
        var solution = Solution();

        // Line 4 of App: `    public static void Main() { Files.Clean("a"); ...` — column of "Clean".
        var text = App.Split('\n')[3];
        var symbol = await LiveQueries.SymbolAtAsync(solution, new LivePosition(Indexed.PathOf("App.cs"), 4, text.IndexOf("Clean", StringComparison.Ordinal) + 1), TestContext.Current.CancellationToken);

        Assert.Equal("M:N.Files.Clean(System.String)", symbol.Id);
        Assert.True(symbol.InSource);
        Assert.Equal("M:N.Program.Main", symbol.EnclosingMemberId);
        Assert.Equal("void", symbol.ExpressionType);
        Assert.EndsWith("Lib.cs", symbol.File);
    }

    [Fact]
    public async Task SymbolAt_reports_a_metadata_symbol_as_not_in_source()
    {
        var text = App.Split('\n')[3];
        var symbol = await LiveQueries.SymbolAtAsync(Solution(), new LivePosition(Indexed.PathOf("App.cs"), 4, text.IndexOf("WriteLine", StringComparison.Ordinal) + 1), TestContext.Current.CancellationToken);

        Assert.False(symbol.InSource);
        Assert.StartsWith("M:System.Console.WriteLine", symbol.Id);
    }

    [Fact]
    public async Task References_finds_uses_of_a_metadata_symbol_by_dotted_name()
    {
        var references = await LiveQueries.ReferencesAsync(Solution(), "System.IO.File.Delete", 50, TestContext.Current.CancellationToken);

        Assert.False(references.InSource);
        var use = Assert.Single(references.References);
        Assert.Equal("M:N.Files.Clean(System.String)", use.EnclosingMemberId);
        Assert.Contains("File.Delete(path)", use.Text);
    }

    [Fact]
    public async Task References_finds_uses_of_a_source_symbol_by_id_across_projects()
    {
        var references = await LiveQueries.ReferencesAsync(Solution(), "P:N.Model.Size", 50, TestContext.Current.CancellationToken);

        Assert.True(references.InSource);
        Assert.Equal(["M:N.Files.Load(System.String)", "M:N.Program.Main"], references.References.Select(r => r.EnclosingMemberId).Order());
    }

    [Fact]
    public async Task References_rejects_an_unknown_symbol()
    {
        var error = await Assert.ThrowsAsync<LiveException>(() => LiveQueries.ReferencesAsync(Solution(), "N.Nope.Missing", 50, TestContext.Current.CancellationToken));
        Assert.Contains("no symbol matches", error.Message);
    }

    [Fact]
    public async Task Diagnostics_reports_errors_in_one_file()
    {
        var solution = Solution(app: App.Replace("Files.Clean(\"a\")", "Files.Clean(1)"));

        var fileScoped = await LiveQueries.DiagnosticsAsync(solution, Indexed.PathOf("App.cs"), includeWarnings: false, 30, TestContext.Current.CancellationToken);
        var whole = await LiveQueries.DiagnosticsAsync(solution, null, includeWarnings: false, 30, TestContext.Current.CancellationToken);

        Assert.Equal(1, fileScoped.Errors);
        Assert.Equal("CS1503", fileScoped.Items[0].Id);
        Assert.Equal(4, fileScoped.Items[0].Line);
        Assert.Equal(1, whole.Errors);
    }

    [Fact]
    public async Task CheckChange_reports_errors_a_proposed_edit_introduces_in_dependent_projects()
    {
        var solution = Solution();

        // Renaming Files.Clean in the library breaks the app that calls it, without touching the library's own errors.
        var check = await LiveQueries.CheckChangeAsync(solution, Indexed.PathOf("Lib.cs"), Lib.Replace("Clean(", "Scrub("), TestContext.Current.CancellationToken);

        Assert.Equal(["App", "Lib"], check.ProjectsCompiled);
        Assert.Equal(0, check.ErrorsBefore);
        var error = Assert.Single(check.NewErrors);
        Assert.Equal("CS0117", error.Id);
        Assert.EndsWith("App.cs", error.File);
        Assert.Empty(check.FixedErrors);
    }

    [Fact]
    public async Task CheckChange_reports_a_clean_edit_and_a_fix()
    {
        var broken = Solution(app: App.Replace("Files.Clean(\"a\")", "Files.Clean(1)"));

        var fix = await LiveQueries.CheckChangeAsync(broken, Indexed.PathOf("App.cs"), App, TestContext.Current.CancellationToken);
        var same = await LiveQueries.CheckChangeAsync(Solution(), Indexed.PathOf("App.cs"), App + "\n// comment\n", TestContext.Current.CancellationToken);

        Assert.Equal(["App"], fix.ProjectsCompiled);
        Assert.Single(fix.FixedErrors);
        Assert.Equal((1, 0), (fix.ErrorsBefore, fix.ErrorsAfter));
        Assert.Empty(same.NewErrors);
        Assert.Equal(0, same.ErrorsAfter);
    }

    [Fact]
    public async Task CheckChange_accepts_a_new_file_inside_a_project_directory()
    {
        var check = await LiveQueries.CheckChangeAsync(Solution(), Indexed.PathOf(Path.Combine("App", "Extra.cs")), "namespace N; public class Extra { public Missing M; }", TestContext.Current.CancellationToken);

        Assert.Equal(["App"], check.ProjectsCompiled);
        Assert.Single(check.NewErrors, e => e.Id == "CS0246");
    }

    [Fact]
    public async Task CheckChange_rejects_a_file_outside_every_project()
    {
        var error = await Assert.ThrowsAsync<LiveException>(() => LiveQueries.CheckChangeAsync(Solution(), Path.Combine(Path.GetTempPath(), "elsewhere", "X.cs"), "class X { }", TestContext.Current.CancellationToken));
        Assert.Contains("not inside any project", error.Message);
    }

    [Fact]
    public async Task RenamePreview_edits_declaration_and_references_across_projects_without_conflicts()
    {
        var rename = await LiveQueries.RenamePreviewAsync(Solution(), "M:N.Files.Clean(System.String)", "Scrub", overloads: false, TestContext.Current.CancellationToken);

        Assert.Equal(["App.cs", "Lib.cs"], rename.Files.Select(f => Path.GetFileName(f.File)));
        Assert.Equal(2, rename.Edits);
        var call = Assert.Single(rename.Files[0].Lines);
        Assert.Equal(4, call.Line);
        Assert.Contains("Files.Scrub(\"a\")", call.After);
        Assert.Contains("Files.Clean(\"a\")", call.Before);
        Assert.Empty(rename.Conflicts);
    }

    [Fact]
    public async Task RenamePreview_follows_an_interface_member_to_its_implementation()
    {
        var workspace = new AdhocWorkspace();
        Add(workspace, "Lib", "Lib", [("Lib.cs", "namespace N; public interface IRun { void Go(); } public class R : IRun { public void Go() { } } public class U { void M(IRun r) => r.Go(); }")], [], OutputKind.DynamicallyLinkedLibrary);

        var rename = await LiveQueries.RenamePreviewAsync(workspace.CurrentSolution, "M:N.IRun.Go", "Start", overloads: false, TestContext.Current.CancellationToken);

        // One line holds the interface member, the implementation and the call: all three are renamed.
        var line = Assert.Single(Assert.Single(rename.Files).Lines);
        Assert.Equal(3, line.After.Split("Start(").Length - 1);
        Assert.DoesNotContain("Go(", line.After);
    }

    [Fact]
    public async Task RenamePreview_reports_a_clash_as_a_conflict()
    {
        // Load already exists on Files, so renaming Clean to Load makes App's call ambiguous or mistyped.
        var rename = await LiveQueries.RenamePreviewAsync(Solution(), "M:N.Files.Clean(System.String)", "Load", overloads: false, TestContext.Current.CancellationToken);

        Assert.NotEmpty(rename.Conflicts);
    }

    [Fact]
    public async Task RenamePreview_rejects_metadata_symbols_and_bad_names()
    {
        await Assert.ThrowsAsync<LiveException>(() => LiveQueries.RenamePreviewAsync(Solution(), "System.IO.File.Delete", "Remove", false, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<LiveException>(() => LiveQueries.RenamePreviewAsync(Solution(), "M:N.Files.Clean(System.String)", "not valid", false, TestContext.Current.CancellationToken));
    }

    /// <summary>Lib (Lib.cs) referenced by App (App.cs), both in the adhoc root so WorkspaceSync can place new files.</summary>
    private static Solution Solution(string? app = null)
    {
        var workspace = new AdhocWorkspace();
        var lib = Add(workspace, "Lib", "Lib", [("Lib.cs", Lib)], [], OutputKind.DynamicallyLinkedLibrary);
        Add(workspace, "App", "App", [("App.cs", app ?? App)], [lib], OutputKind.ConsoleApplication);
        return workspace.CurrentSolution;
    }

    private static ProjectId Add(AdhocWorkspace workspace, string name, string directory, (string Name, string Source)[] files, ProjectId[] references, OutputKind kind)
    {
        var projectId = ProjectId.CreateNewId();
        var info = ProjectInfo.Create(
            projectId, VersionStamp.Default, name, name, LanguageNames.CSharp,
            filePath: Indexed.PathOf(Path.Combine(directory, $"{name}.csproj")),
            documents: files.Select(file => DocumentInfo.Create(
                DocumentId.CreateNewId(projectId), file.Name,
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(file.Source), VersionStamp.Default)),
                filePath: Indexed.PathOf(file.Name))),
            projectReferences: references.Select(r => new ProjectReference(r)),
            metadataReferences: Indexed.FrameworkReferences,
            compilationOptions: new CSharpCompilationOptions(kind));
        workspace.AddProject(info);
        return projectId;
    }
}

public sealed class CheckChangeDeclarationTests
{
    [Fact]
    public async Task Classifies_removed_added_signature_and_body_changes()
    {
        var solution = Solution("namespace N; public class C { public int A() => 1; public int B() => 2; public void D() { } int F; }");

        var check = await LiveQueries.CheckChangeAsync(solution, Indexed.PathOf("C.cs"),
            "namespace N; public class C { public int A() => 10; internal int B() => 2; public void E() { } int F; }",
            TestContext.Current.CancellationToken);

        var byId = check.Declarations!.ToDictionary(d => d.Id, d => d.Change);
        Assert.Equal("body", byId["M:N.C.A"]);
        Assert.Equal("signature", byId["M:N.C.B"]);
        Assert.Equal("removed", byId["M:N.C.D"]);
        Assert.Equal("added", byId["M:N.C.E"]);
        Assert.False(byId.ContainsKey("F:N.C.F"), "an untouched field is not a change");
        Assert.False(byId.ContainsKey("T:N.C"), "the type's own signature did not change");
    }

    [Fact]
    public async Task Several_files_compile_together()
    {
        // Moving Helper from one file to another breaks either file alone and compiles as a pair.
        var solution = Solution(
            ("A.cs", "namespace N; public static class A { public static int Helper() => 1; }"),
            ("B.cs", "namespace N; public static class B { public static int Use() => A.Helper(); }"));

        var alone = await LiveQueries.CheckChangeAsync(solution, Indexed.PathOf("A.cs"), "namespace N; public static class A { }", TestContext.Current.CancellationToken);
        var both = await LiveQueries.CheckChangeAsync(solution,
            [
                new LiveFileText(Indexed.PathOf("A.cs"), "namespace N; public static class A { }"),
                new LiveFileText(Indexed.PathOf("B.cs"), "namespace N; public static class B { public static int Helper() => 1; public static int Use() => Helper(); }"),
            ], TestContext.Current.CancellationToken);

        Assert.NotEmpty(alone.NewErrors);
        Assert.Empty(both.NewErrors);
        Assert.Contains(both.Declarations!, d => d is { Id: "M:N.A.Helper", Change: "removed" });
        Assert.Contains(both.Declarations!, d => d is { Id: "M:N.B.Helper", Change: "added" });
        Assert.Contains(both.Declarations!, d => d is { Id: "M:N.B.Use", Change: "body" });
    }

    private static Solution Solution(string source) => Solution(("C.cs", source));

    private static Solution Solution(params (string Name, string Source)[] files)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        workspace.AddProject(ProjectInfo.Create(
            projectId, VersionStamp.Default, "Demo", "Demo", LanguageNames.CSharp,
            filePath: Indexed.PathOf("Demo.csproj"),
            documents: files.Select(f => DocumentInfo.Create(DocumentId.CreateNewId(projectId), f.Name,
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(f.Source), VersionStamp.Default)),
                filePath: Indexed.PathOf(f.Name))),
            metadataReferences: Indexed.FrameworkReferences,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));
        return workspace.CurrentSolution;
    }
}
