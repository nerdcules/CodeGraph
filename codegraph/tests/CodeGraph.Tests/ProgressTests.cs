using CodeGraph.Indexer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CodeGraph.Tests;

/// <summary>
/// The indexer reports where it is (project n of m, compiling or file k of f) and numbers its project log lines;
/// the console renderer turns that into one line that fits the terminal.
/// </summary>
public sealed class ProgressTests
{
    [Fact]
    public async Task Project_log_lines_are_numbered_and_progress_walks_every_project_and_file()
    {
        var log = new List<string>();
        var progress = new List<IndexProgress>();
        var solution = TwoProjects();

        var indexer = new RoslynIndexer(log.Add, progress.Add);
        await indexer.IndexAsync(solution, selectFiles: null, TestContext.Current.CancellationToken);

        Assert.Equal(["[1/2] project: First", "[2/2] project: Second"], log.Where(l => l.Contains("project:")));

        // Per project: one "compiling" report (file 0), then one per file in order.
        Assert.Equal(
        [
            new IndexProgress(1, 2, "First", 0, 2),
            new IndexProgress(1, 2, "First", 1, 2),
            new IndexProgress(1, 2, "First", 2, 2),
            new IndexProgress(2, 2, "Second", 0, 1),
            new IndexProgress(2, 2, "Second", 1, 1),
        ], progress);
    }

    [Fact]
    public async Task A_project_with_nothing_to_walk_reports_no_progress()
    {
        var progress = new List<IndexProgress>();
        var solution = TwoProjects();

        var indexer = new RoslynIndexer(_ => { }, progress.Add);
        // Only Second's file is selected, so First skips its compilation and says nothing.
        await indexer.IndexAsync(solution, _ => new HashSet<string>([Indexed.PathOf("S.cs")], StringComparer.OrdinalIgnoreCase), TestContext.Current.CancellationToken);

        Assert.All(progress, p => Assert.Equal("Second", p.ProjectName));
        Assert.Equal([0, 1], progress.Select(p => p.File));
    }

    [Fact]
    public async Task Driving_one_project_directly_omits_the_counter()
    {
        // Tests (and anything else walking a single project) get the plain line the log always had.
        var log = new List<string>();
        var solution = TwoProjects();
        var indexer = new RoslynIndexer(log.Add);

        await indexer.IndexProjectAsync(solution.Projects.First(), solution, TestContext.Current.CancellationToken);

        Assert.Contains("project: First", log);
    }

    [Fact]
    public void Progress_reads_as_compiling_then_files()
    {
        Assert.Equal("[3/33] App.Core: compiling", new IndexProgress(3, 33, "App.Core", 0, 340).ToString());
        Assert.Equal("[3/33] App.Core: 120/340 files", new IndexProgress(3, 33, "App.Core", 120, 340).ToString());
    }

    [Theory]
    [InlineData("indexing", "[1/2] First: compiling", 3, 0, "/ indexing: [1/2] First: compiling (3s)")]
    [InlineData("loading solution", "", 14, 0, "/ loading solution (14s)")]
    [InlineData("indexing", "[1/2] First: compiling", 3, 120, "/ indexing: [1/2] First: compiling (3s)")]
    public void The_status_line_shows_spinner_phase_detail_and_elapsed(string phase, string detail, int seconds, int width, string expected)
    {
        Assert.Equal(expected, ConsoleStatus.Line('/', phase, detail, seconds, width));
    }

    [Fact]
    public void The_status_line_is_cut_to_the_console_width_so_it_never_wraps()
    {
        var line = ConsoleStatus.Line('-', "indexing", new string('x', 200), 7, width: 40);

        Assert.Equal(39, line.Length);
        Assert.EndsWith("…", line);
        Assert.StartsWith("- indexing: xxx", line);
    }

    [Fact]
    public void An_unusable_console_width_yields_an_empty_line()
    {
        Assert.Equal("", ConsoleStatus.Line('-', "indexing", "detail", 1, width: 1));
    }

    /// <summary>First (F1.cs, F2.cs) referenced by Second (S.cs), as an adhoc solution.</summary>
    private static Solution TwoProjects()
    {
        var workspace = new AdhocWorkspace();
        var first = AddProject(workspace, "First", [("F1.cs", "namespace N; public class A { public void Run() { } }"), ("F2.cs", "namespace N; public class B { }")], references: []);
        var second = AddProject(workspace, "Second", [("S.cs", "namespace N; class C { void M() { new A().Run(); } }")], references: [first]);
        return workspace.CurrentSolution.GetProject(second)!.Solution;
    }

    private static ProjectId AddProject(AdhocWorkspace workspace, string name, (string Name, string Source)[] files, ProjectId[] references)
    {
        var projectId = ProjectId.CreateNewId();
        var info = ProjectInfo.Create(
            projectId, VersionStamp.Default, name, name, LanguageNames.CSharp,
            filePath: Indexed.PathOf($"{name}.csproj"),
            documents: files.Select(file => DocumentInfo.Create(
                DocumentId.CreateNewId(projectId), file.Name,
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(file.Source), VersionStamp.Default)),
                filePath: Indexed.PathOf(file.Name))),
            projectReferences: references.Select(r => new ProjectReference(r)),
            metadataReferences: Indexed.FrameworkReferences,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        workspace.AddProject(info);
        return projectId;
    }
}

/// <summary>A multi-target project opens once per target framework; only the first is compiled for shared files.</summary>
public sealed class MultiTargetTests
{
    [Fact]
    public async Task A_later_target_walks_only_its_own_files_and_is_not_compiled_when_it_has_none()
    {
        var progress = new List<IndexProgress>();
        var indexer = new RoslynIndexer(_ => { }, progress.Add);

        await indexer.IndexAsync(Solution(extraInSecond: true), null, TestContext.Current.CancellationToken);

        // First target: both shared files. Second: only the file its condition adds. Third: nothing, so no compile.
        Assert.Equal([("Demo(net10.0)", 2), ("Demo(net9.0)", 1)], progress.Where(p => p.File == 0).Select(p => (p.ProjectName, p.Files)));
        var nodes = indexer.Nodes.Select(n => n.Id).ToHashSet();
        Assert.Contains("T:N.A", nodes);
        Assert.Contains("T:N.OnlyNet9", nodes);
    }

    [Fact]
    public async Task All_targets_walks_every_target_in_full()
    {
        var progress = new List<IndexProgress>();
        var indexer = new RoslynIndexer(_ => { }, progress.Add) { AllTargets = true };

        await indexer.IndexAsync(Solution(extraInSecond: true), null, TestContext.Current.CancellationToken);

        Assert.Equal([("Demo(net10.0)", 2), ("Demo(net9.0)", 3), ("Demo(net8.0)", 2)], progress.Where(p => p.File == 0).Select(p => (p.ProjectName, p.Files)));
    }

    [Fact]
    public async Task The_graph_is_the_same_as_walking_every_target_when_the_code_does_not_differ()
    {
        var once = new RoslynIndexer(_ => { });
        var all = new RoslynIndexer(_ => { }) { AllTargets = true };
        await once.IndexAsync(Solution(extraInSecond: false), null, TestContext.Current.CancellationToken);
        await all.IndexAsync(Solution(extraInSecond: false), null, TestContext.Current.CancellationToken);

        Assert.Equal(all.Nodes.Select(n => n.Id).Order(), once.Nodes.Select(n => n.Id).Order());
        Assert.Equal(all.Edges.Select(e => e.ToString()).Order(), once.Edges.Select(e => e.ToString()).Order());
    }

    /// <summary>One csproj opened as three targets, the way MSBuildWorkspace opens a multi-target project.</summary>
    private static Solution Solution(bool extraInSecond)
    {
        var workspace = new AdhocWorkspace();
        (string, string)[] shared =
        [
            ("A.cs", "namespace N; public class A { public void Run() { new B().Work(); } }"),
            ("B.cs", "namespace N; public class B { public void Work() { } }"),
        ];
        foreach (var tfm in new[] { "net10.0", "net9.0", "net8.0" })
        {
            var files = tfm == "net9.0" && extraInSecond ? [.. shared, ("OnlyNet9.cs", "namespace N; public class OnlyNet9 { }")] : shared;
            var projectId = ProjectId.CreateNewId();
            workspace.AddProject(ProjectInfo.Create(
                projectId, VersionStamp.Default, $"Demo({tfm})", "Demo", LanguageNames.CSharp,
                filePath: Indexed.PathOf("Demo.csproj"),
                documents: files.Select(f => DocumentInfo.Create(DocumentId.CreateNewId(projectId), f.Item1,
                    loader: TextLoader.From(TextAndVersion.Create(SourceText.From(f.Item2), VersionStamp.Default)),
                    filePath: Indexed.PathOf(f.Item1))),
                metadataReferences: Indexed.FrameworkReferences,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));
        }

        return workspace.CurrentSolution;
    }
}
