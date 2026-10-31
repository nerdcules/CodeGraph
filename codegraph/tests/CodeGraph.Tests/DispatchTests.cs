using CodeGraph.Core;
using CodeGraph.Indexer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CodeGraph.Tests;

/// <summary>
/// Reachability through things a text search sees and a plain call graph does not: a test that only talks to an
/// interface, a property that is read rather than called, a service wired up in top-level statements.
/// </summary>
public sealed class DispatchTests
{
    private const string Staging = """
        namespace N;
        public interface IStaging { bool Enabled { get; } void Reset(); }
        public sealed class Staging : IStaging { public bool Enabled => true; public void Reset() { } }
        public interface IRunner { void Run(); }
        public sealed class Runner(IStaging staging) : IRunner
        {
            public void Run() { if (staging.Enabled) { staging.Reset(); } }
        }
        public abstract class Base { public virtual void Step() { } }
        public sealed class Derived : Base { public override void Step() { } }
        public class Tests
        {
            [Xunit.Fact] public void Through_the_interface() { IRunner r = new Runner(new Staging()); r.Run(); }
            [Xunit.Fact] public void Through_the_base() { Base b = new Derived(); b.Step(); }
            [Xunit.Fact] public void Reads_a_property() { var s = new Staging(); _ = s.Enabled; }
            [Xunit.Fact] public void Only_names_it() { _ = nameof(Staging.Reset); }
        }
        """;

    private const string RunnerRun = "M:N.Runner.Run";
    private const string IRunnerRun = "M:N.IRunner.Run";
    private const string StagingEnabled = "P:N.Staging.Enabled";
    private const string IStagingEnabled = "P:N.IStaging.Enabled";
    private const string StagingReset = "M:N.Staging.Reset";
    private const string DerivedStep = "M:N.Derived.Step";
    private const string ThroughInterface = "M:N.Tests.Through_the_interface";
    private const string ThroughBase = "M:N.Tests.Through_the_base";
    private const string ReadsProperty = "M:N.Tests.Reads_a_property";
    private const string OnlyNames = "M:N.Tests.Only_names_it";

    [Fact]
    public async Task A_member_implementing_an_interface_member_gets_an_Implements_edge()
    {
        var graph = await Indexed.OfAsync(Staging);

        Assert.True(graph.HasEdge(RunnerRun, IRunnerRun, EdgeKind.Implements));
        Assert.True(graph.HasEdge(StagingEnabled, IStagingEnabled, EdgeKind.Implements));
        Assert.True(graph.HasEdge(StagingReset, "M:N.IStaging.Reset", EdgeKind.Implements));
        Assert.True(graph.HasEdge(DerivedStep, "M:N.Base.Step", EdgeKind.Overrides));
    }

    [Fact]
    public async Task Reading_a_property_is_a_call_and_nameof_is_not()
    {
        var graph = await Indexed.OfAsync(Staging);

        Assert.True(graph.HasEdge(RunnerRun, IStagingEnabled, EdgeKind.Calls), "Runner.Run reads staging.Enabled");
        Assert.True(graph.HasEdge(ReadsProperty, StagingEnabled, EdgeKind.Calls));
        Assert.True(graph.HasEdge(StagingEnabled, ReadsProperty, EdgeKind.TestedBy), "a test reading it tests it");
        Assert.False(graph.HasEdge(OnlyNames, StagingReset, EdgeKind.Calls), "nameof reads nothing");
    }

    [Fact]
    public async Task Tests_that_only_see_the_interface_cover_the_implementation()
    {
        using var db = await Store(Staging);

        // Through_the_interface → IRunner.Run ⇒ Runner.Run → IStaging.Enabled ⇒ Staging.Enabled.
        Assert.Contains(ThroughInterface, db.TransitiveTests(RunnerRun).Select(h => h.Node.Id));
        Assert.Contains(ThroughInterface, db.TransitiveTests(StagingEnabled).Select(h => h.Node.Id));
        Assert.Contains(ThroughInterface, db.TransitiveTests(StagingReset).Select(h => h.Node.Id));
        Assert.Contains(ThroughBase, db.TransitiveTests(DerivedStep).Select(h => h.Node.Id));

        // The property test reads the class directly and never runs Runner.
        Assert.DoesNotContain(ReadsProperty, db.TransitiveTests(RunnerRun).Select(h => h.Node.Id));
        Assert.DoesNotContain(OnlyNames, db.TransitiveTests(StagingReset).Select(h => h.Node.Id));
    }

    [Fact]
    public async Task Callers_of_an_implementation_include_callers_of_its_interface_member()
    {
        using var db = await Store(Staging);

        var callers = db.Callers(RunnerRun, maxDepth: 1);
        Assert.Contains(ThroughInterface, callers.Select(h => h.Node.Id));
        // The interface member itself was only hopped through; it is not a caller.
        Assert.DoesNotContain(IRunnerRun, callers.Select(h => h.Node.Id));
        Assert.All(callers, h => Assert.Equal(1, h.Depth));

        Assert.Contains(ThroughBase, db.Callers(DerivedStep).Select(h => h.Node.Id));
    }

    [Fact]
    public async Task Direct_tests_see_through_the_interface_and_impact_lists_them()
    {
        using var db = await Store(Staging);

        Assert.Contains(ThroughInterface, db.DirectTests(RunnerRun).Select(n => n.Id));
        Assert.Contains(ThroughBase, db.DirectTests(DerivedStep).Select(n => n.Id));
        Assert.Contains(ThroughInterface, db.Impact(RunnerRun).Select(h => h.Node.Id));
    }

    [Fact]
    public async Task Coverage_counts_implementations_reached_only_through_their_interface()
    {
        using var db = await Store(Staging);

        var uncovered = db.UncoveredMethods().Select(n => n.Id).ToList();
        Assert.DoesNotContain(RunnerRun, uncovered);
        Assert.DoesNotContain(StagingReset, uncovered);
        Assert.DoesNotContain(DerivedStep, uncovered);
        Assert.DoesNotContain("M:N.Base.Step", uncovered);

        var (total, covered, _) = db.CoverageStats();
        Assert.Equal(total, covered);
    }

    [Fact]
    public async Task Impact_of_a_class_does_not_leak_to_sibling_implementations()
    {
        // Dispatch hops are for members. A type must not hop to its interface and come back down to another class.
        var graph = await Indexed.OfAsync("""
            namespace N;
            public interface IPort { }
            public sealed class PortA : IPort { }
            public sealed class PortB : IPort { }
            """);
        using var db = new TempDb();
        db.Store.Write(graph.Nodes, graph.Edges);

        Assert.DoesNotContain("T:N.PortB", db.Store.Impact("T:N.PortA").Select(h => h.Node.Id));
    }

    [Fact]
    public async Task Top_level_statements_are_the_entry_point_and_their_calls_are_attributed_to_it()
    {
        var graph = await ConsoleAppAsync(
            ("Program.cs", """
                using N;
                var registry = new Registry();
                registry.Add(sp => new Runner(registry.Flag));
                registry.Start();
                """),
            ("Lib.cs", """
                namespace N;
                public sealed class Runner(bool flag) { public bool Flag => flag; }
                public sealed class Registry
                {
                    public bool Flag { get; set; }
                    public void Add(System.Func<Registry, Runner> factory) { }
                    public void Start() { }
                }
                """));

        var entry = Assert.Single(graph.Nodes, n => n.Kind == NodeKind.Method && n.Name.Contains("Main"));
        Assert.EndsWith("Program.cs", entry.FilePath);
        Assert.Equal((2, 4), (entry.StartLine, entry.EndLine));

        Assert.True(graph.HasEdge(entry.Id, "M:N.Registry.Add(System.Func{N.Registry,N.Runner})", EdgeKind.Calls));
        Assert.True(graph.HasEdge(entry.Id, "M:N.Registry.Start", EdgeKind.Calls));
        Assert.True(graph.HasEdge(entry.Id, "M:N.Runner.#ctor(System.Boolean)", EdgeKind.Calls), "the lambda's construction belongs to the entry point");
        Assert.True(graph.HasEdge(entry.Id, "P:N.Registry.Flag", EdgeKind.Calls));
        Assert.Contains(graph.Edges, e => e.ToId == entry.Id && e.Kind == EdgeKind.InvokedByFramework);
        Assert.Contains(graph.Edges, e => e.ToId == entry.Id && e.Kind == EdgeKind.Contains && e.FromId.StartsWith("F:"));
    }

    [Fact]
    public async Task Ambiguous_candidates_from_different_types_are_not_guessed()
    {
        // Neither call compiles: the first fails overload resolution within one method family (kept, it still names
        // the member), the second is ambiguous between two extension classes (dropped).
        var graph = await Indexed.OfAsync("""
            namespace N;
            public static class Ext1 { public static void Go(this string s) { } }
            public static class Ext2 { public static void Go(this string s) { } }
            public sealed class Api { public void Save(int x) { } public void Save(string s) { } }
            public sealed class User
            {
                public void A() { new Api().Save(1.5); }
                public void B() { "x".Go(); }
            }
            """);

        Assert.True(graph.HasEdge("M:N.User.A", "M:N.Api.Save(System.Int32)", EdgeKind.Calls)
                    || graph.HasEdge("M:N.User.A", "M:N.Api.Save(System.String)", EdgeKind.Calls));
        Assert.Empty(graph.TargetsOf("M:N.User.B", EdgeKind.Calls));
    }

    private static async Task<GraphStore> Store(string source)
    {
        var graph = await Indexed.OfAsync(source);
        var db = new TempDb();
        db.Store.Write(graph.Nodes, graph.Edges);
        return db.Store;
    }

    /// <summary>An executable project, so the compilation has an entry point for the top-level statements.</summary>
    private static async Task<Indexed> ConsoleAppAsync(params (string Name, string Source)[] files)
    {
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var info = ProjectInfo.Create(
            projectId, VersionStamp.Default, "App", "App", LanguageNames.CSharp,
            filePath: Indexed.PathOf("App.csproj"),
            documents: files.Select(file => DocumentInfo.Create(
                DocumentId.CreateNewId(projectId), file.Name,
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(file.Source), VersionStamp.Default)),
                filePath: Indexed.PathOf(file.Name))),
            metadataReferences: Indexed.FrameworkReferences,
            compilationOptions: new CSharpCompilationOptions(OutputKind.ConsoleApplication));
        var solution = workspace.AddProject(info).Solution;
        var indexer = new RoslynIndexer(_ => { });
        await indexer.IndexAsync(solution, selectFiles: null, TestContext.Current.CancellationToken);
        return Indexed.From(indexer);
    }
}
