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
    public async Task Pinging_a_graph_with_no_daemon_returns_null_quickly()
    {
        var client = new LiveClient(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"), "graph.db"));
        var clock = Stopwatch.StartNew();

        Assert.Null(await client.PingAsync(TestContext.Current.CancellationToken));
        Assert.Null(await client.FlushAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed}");
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
