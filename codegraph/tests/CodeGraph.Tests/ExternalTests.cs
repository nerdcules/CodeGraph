using CodeGraph.Core;

namespace CodeGraph.Tests;

/// <summary>Framework and NuGet members as External nodes: what gets one, what does not, and how the queries treat them.</summary>
public sealed class ExternalTests
{
    private const string Source = """
        namespace N;
        public class C
        {
            public void Save(string path) { System.IO.File.WriteAllText(path, ""); var sb = new System.Text.StringBuilder(); sb.Append('x'); }
            public void Clean(string path) { System.IO.File.Delete(path); System.Func<int> f = () => 1; f(); }
            public void Both(string path) { Save(path); Clean(path); System.IO.File.Delete(path); }
            [Xunit.Fact] public void Test() { System.IO.File.Delete("x"); }
        }
        """;

    private const string Delete = "M:System.IO.File.Delete(System.String)";

    [Fact]
    public async Task Calls_and_constructions_get_External_targets_but_delegate_invocations_and_TestedBy_do_not()
    {
        var graph = await Indexed.OfAsync(Source);

        Assert.Contains("M:System.Text.StringBuilder.#ctor", graph.TargetsOf("M:N.C.Save(System.String)", EdgeKind.CallsExternal));
        Assert.Equal(MemberKind.Constructor, graph.Node("M:System.Text.StringBuilder.#ctor").MemberKind);
        Assert.DoesNotContain(graph.TargetsOf("M:N.C.Clean(System.String)", EdgeKind.CallsExternal), t => t.Contains("Func"));
        Assert.DoesNotContain(graph.Edges, e => e.Kind == EdgeKind.Calls && e.ToId.StartsWith("M:System."));
        Assert.Empty(graph.TargetsOf(Delete, EdgeKind.TestedBy));
    }

    [Fact]
    public async Task Callers_of_an_external_member_and_the_external_filters()
    {
        using var db = await Store();

        Assert.Equal(["M:N.C.Both(System.String)", "M:N.C.Clean(System.String)", "M:N.C.Test"], db.Store.Callers(Delete).Select(h => h.Node.Id).Order());

        Assert.DoesNotContain(db.Store.Callees("M:N.C.Both(System.String)"), h => h.Node.Kind == NodeKind.External);
        Assert.Contains(db.Store.Callees("M:N.C.Both(System.String)", includeExternal: true), h => h.Node.Id == Delete);

        Assert.DoesNotContain(db.Store.MostCalled(50, 1, 0), r => r.Method.Kind == NodeKind.External);
        var io = db.Store.MostCalled(50, 1, 0, includeExternal: true, nameFilter: "System.IO.File");
        Assert.Equal((Delete, 3), (io[0].Method.Id, io[0].CallerCount));
        Assert.All(io, r => Assert.StartsWith("System.IO.File", r.Method.FullName));
    }

    [Fact]
    public async Task Externals_do_not_count_as_methods_for_coverage_or_dead_code()
    {
        using var db = await Store();

        var (total, _, _) = db.Store.CoverageStats();
        Assert.Equal(3, total); // Save, Clean, Both; the test and the externals are not production methods
        Assert.DoesNotContain(db.Store.UncoveredMethods(), n => n.Kind == NodeKind.External);
        Assert.DoesNotContain(db.Store.DeadCodeReport(includePublicApi: true).Candidates, c => c.Method.Kind == NodeKind.External);
    }

    [Fact]
    public async Task An_external_whose_last_caller_is_removed_leaves_the_graph_on_an_incremental_run()
    {
        using var db = new TempDb();
        var before = await Indexed.OfAsync(("A.cs", "namespace N; class A { void M() { System.IO.File.Delete(\"x\"); } }"), ("B.cs", "namespace N; class B { }"));
        db.Store.Replace(full: true, [], before.Nodes, before.Edges, before.Origins, [], new Dictionary<string, string>());
        Assert.NotNull(db.Store.GetNode(Delete));

        var after = await Indexed.OfAsync(["A.cs"], ("A.cs", "namespace N; class A { void M() { } }"), ("B.cs", "namespace N; class B { }"));
        db.Store.Replace(full: false, [Indexed.PathOf("A.cs"), .. db.Store.ProjectFilePaths()], after.Nodes, after.Edges, after.Origins, [], new Dictionary<string, string>());

        Assert.Null(db.Store.GetNode(Delete));
    }

    private static async Task<TempDb> Store()
    {
        var graph = await Indexed.OfAsync(Source);
        var db = new TempDb();
        db.Store.Write(graph.Nodes, graph.Edges);
        return db;
    }
}

public sealed class ExternalWalkTests
{
    [Fact]
    public async Task Impact_and_tests_of_an_external_member_reach_through_its_callers()
    {
        var graph = await Indexed.OfAsync("""
            namespace N;
            public class Store { public void Purge(string p) { System.IO.File.Delete(p); } }
            public class Api { public void Reset() { new Store().Purge("x"); } }
            public class Tests { [Xunit.Fact] public void Resets() { new Api().Reset(); } }
            """);
        using var db = new TempDb();
        db.Store.Write(graph.Nodes, graph.Edges);
        const string delete = "M:System.IO.File.Delete(System.String)";

        var impact = db.Store.Impact(delete).ToDictionary(h => h.Node.Id, h => h.Depth);
        Assert.Equal(1, impact["M:N.Store.Purge(System.String)"]);
        Assert.Equal(2, impact["M:N.Api.Reset"]);
        Assert.Equal(3, impact["M:N.Tests.Resets"]);
        Assert.Equal(["M:N.Tests.Resets"], db.Store.TransitiveTests(delete).Select(h => h.Node.Id));
        Assert.Equal(["M:N.Store.Purge(System.String)"], db.Store.Callers(delete, 1).Select(h => h.Node.Id));
    }
}
