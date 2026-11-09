using CodeGraph.Core;

namespace CodeGraph.Tests;

/// <summary>
/// The analysis queries behind most_called, dead_code_report, explain_reachability, references_to, neighbors
/// and the web subgraph. These had no tests before; most_called in particular failed at runtime on an
/// ambiguous column once edges gained a kind column of their own.
/// </summary>
public sealed class AnalysisTests
{
    [Fact]
    public void MostCalled_ranks_methods_by_direct_caller_count_and_pages()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        var ranked = db.Store.MostCalled(limit: 10, minCallers: 1, offset: 0);

        // Save has two callers (Handle and the test); Handle has one (Top). SaveAll, Top and the test have none.
        Assert.Equal([(Graph.Save, 2), (Graph.Handle, 1)], ranked.Select(r => (r.Method.Id, r.CallerCount)));
        Assert.Equal([Graph.Handle], db.Store.MostCalled(limit: 10, minCallers: 1, offset: 1).Select(r => r.Method.Id));
        Assert.Equal(5, db.Store.MostCalled(limit: 10, minCallers: 0, offset: 0).Count);
    }

    [Fact]
    public async Task DeadCode_treats_an_implementation_of_a_called_interface_member_as_reachable()
    {
        using var db = await Store("""
            namespace N;
            public interface IRunner { void Run(); void Idle(); }
            public sealed class Runner : IRunner
            {
                public void Run() { }
                void IRunner.Idle() { }
                private void Orphan() { }
            }
            public sealed class Host { public void Go(IRunner r) { r.Run(); r.Idle(); } }
            """);

        var (summary, candidates) = db.DeadCodeReport(includePublicApi: true, limit: 100, offset: 0);
        var ids = candidates.Select(c => c.Method.Id).ToList();

        Assert.DoesNotContain("M:N.Runner.Run", ids);
        // An explicit implementation has a different name from the interface member; the edge, not the name, decides.
        Assert.DoesNotContain("M:N.Runner.N#IRunner#Idle", ids);
        Assert.Contains("M:N.Runner.Orphan", ids);
        Assert.Contains("M:N.Host.Go(N.IRunner)", ids);
        Assert.Equal(2, summary.DiLikelyUsed);
        Assert.Equal(2, summary.LikelyDead);
    }

    [Fact]
    public async Task DeadCode_follows_overrides_of_overrides()
    {
        using var db = await Store("""
            namespace N;
            public abstract class Base { public abstract void Step(); }
            public class Mid : Base { public override void Step() { } }
            public sealed class Leaf : Mid { public override void Step() { } }
            public sealed class Host { public void Go(Base b) { b.Step(); } }
            """);

        var ids = db.DeadCodeReport(includePublicApi: true).Candidates.Select(c => c.Method.Id).ToList();

        Assert.DoesNotContain("M:N.Mid.Step", ids);
        Assert.DoesNotContain("M:N.Leaf.Step", ids);
        Assert.Equal(["M:N.Host.Go(N.Base)"], ids);
    }

    [Fact]
    public async Task ExplainReachability_names_the_dispatched_member_and_its_callers()
    {
        using var db = await Store("""
            namespace N;
            public interface IRunner { void Run(); }
            public sealed class Runner : IRunner { public void Run() { } public void Lonely() { } }
            public sealed class Host { public void Go(IRunner r) { r.Run(); } }
            """);

        Assert.Equal("likely reachable via dispatch: implements or overrides N.IRunner.Run(), which has 1 caller(s)", db.ExplainReachability("M:N.Runner.Run"));
        Assert.StartsWith("reachable: 1 direct caller", db.ExplainReachability("M:N.IRunner.Run"));
        Assert.StartsWith("no direct callers", db.ExplainReachability("M:N.Runner.Lonely"));
        Assert.StartsWith("no node", db.ExplainReachability("M:N.Missing"));
    }

    [Fact]
    public void ReferencesTo_caps_the_rows_but_reports_the_total()
    {
        using var db = new TempDb();
        var nodes = Enumerable.Range(0, 5).Select(i => Graph.Node($"M:N.U{i}", NodeKind.Method, $"U{i}", $"N.U{i}", filePath: $"/repo/U{i}.cs")).ToList();
        nodes.Add(Graph.Node("T:N.Model", NodeKind.Type, "Model", "N.Model"));
        var edges = Enumerable.Range(0, 5).Select(i => new GraphEdge($"M:N.U{i}", "T:N.Model", EdgeKind.References)).ToList();
        edges.Add(new GraphEdge("M:N.U0", "T:N.Model", EdgeKind.Calls)); // a different kind does not count
        db.Store.Write(nodes, edges);

        var (shown, total) = db.Store.ReferencesTo("T:N.Model", limit: 2);

        Assert.Equal(5, total);
        Assert.Equal(["M:N.U0", "M:N.U1"], shown.Select(n => n.Id));
        Assert.Equal((0, 0), (db.Store.ReferencesTo("T:N.Nope", 10).Nodes.Count, db.Store.ReferencesTo("T:N.Nope", 10).Total));
    }

    [Fact]
    public void SubGraph_keeps_the_centre_and_its_nearest_neighbours_when_capped()
    {
        using var db = new TempDb();
        var nodes = new List<GraphNode> { Graph.Node("M:Hub", NodeKind.Method, "Hub", "Hub") };
        var edges = new List<GraphEdge>();
        for (var i = 0; i < 200; i++)
        {
            nodes.Add(Graph.Node($"M:C{i}", NodeKind.Method, $"C{i}", $"C{i}"));
            edges.Add(new GraphEdge($"M:C{i}", "M:Hub", EdgeKind.Calls));
        }

        // One node at depth 2 behind the 200 direct callers; the cap must prefer the direct ones.
        nodes.Add(Graph.Node("M:Far", NodeKind.Method, "Far", "Far"));
        edges.Add(new GraphEdge("M:Far", "M:C0", EdgeKind.Calls));
        db.Store.Write(nodes, edges);

        var graph = db.Store.SubGraph("M:Hub", [nameof(EdgeKind.Calls)], maxDepth: 2);

        Assert.Equal(150, graph.Nodes.Count);
        Assert.Equal("M:Hub", graph.Nodes[0].Id);
        Assert.DoesNotContain(graph.Nodes, n => n.Id == "M:Far");
        Assert.All(graph.Edges, e => Assert.Contains(graph.Nodes, n => n.Id == e.FromId));
    }

    [Fact]
    public void EdgesFrom_and_EdgesTo_resolve_the_other_end()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        Assert.Equal([(EdgeKind.Calls, Graph.Save)], db.Store.EdgesFrom(Graph.Handle).Select(e => (e.Kind, e.Target.Id)));
        Assert.Equal([(EdgeKind.Calls, Graph.Handle), (EdgeKind.Calls, Graph.SaveTest), (EdgeKind.Contains, Graph.Service)],
            db.Store.EdgesTo(Graph.Save).Select(e => (e.Kind, e.Source.Id)));
    }

    [Fact]
    public void MembersOf_filters_by_kind_and_modifier()
    {
        using var db = new TempDb();
        db.Store.Write(
            [
                Graph.Node("T:X", NodeKind.Type, "X", "X"),
                Graph.Node("M:X.A", NodeKind.Method, "A", "X.A", isStatic: true, accessibility: Accessibility.Public),
                Graph.Node("M:X.B", NodeKind.Method, "B", "X.B", accessibility: Accessibility.Private),
                Graph.Node("P:X.P", NodeKind.Property, "P", "X.P", accessibility: Accessibility.Public),
                Graph.Node("T:X.Inner", NodeKind.Type, "Inner", "X.Inner"),
            ],
            [
                new GraphEdge("T:X", "M:X.A", EdgeKind.Contains),
                new GraphEdge("T:X", "M:X.B", EdgeKind.Contains),
                new GraphEdge("T:X", "P:X.P", EdgeKind.Contains),
                new GraphEdge("T:X", "T:X.Inner", EdgeKind.Contains),
            ]);

        Assert.Equal(["M:X.A", "M:X.B", "P:X.P"], db.Store.MembersOf("T:X").Select(n => n.Id).Order());
        Assert.Equal(["M:X.A"], db.Store.MembersOf("T:X", isStatic: true).Select(n => n.Id));
        Assert.Equal(["M:X.A", "P:X.P"], db.Store.MembersOf("T:X", accessibility: Accessibility.Public).Select(n => n.Id).Order());
        Assert.Equal(["P:X.P"], db.Store.MembersOf("T:X", kind: NodeKind.Property).Select(n => n.Id));
    }

    [Fact]
    public void FindSymbol_treats_underscore_and_percent_as_literal_characters()
    {
        using var db = new TempDb();
        db.Store.Write(
            [
                Graph.Node("M:T.Save_works", NodeKind.Method, "Save_works", "T.Save_works"),
                Graph.Node("M:T.SaveXworks", NodeKind.Method, "SaveXworks", "T.SaveXworks"),
                Graph.Node("M:T.Pct", NodeKind.Method, "100%", "T.100%"),
            ], []);

        Assert.Equal(["M:T.Save_works"], db.Store.FindSymbol("Save_works").Select(n => n.Id));
        Assert.Equal(["M:T.Save_works"], db.Store.FindSymbol("Save_works", nameMatch: "Exact").Select(n => n.Id));
        Assert.Equal(["M:T.Pct"], db.Store.FindSymbol("%").Select(n => n.Id));
    }

    [Fact]
    public void Opening_a_missing_graph_read_only_fails_at_once_instead_of_waiting_out_the_lock_retry()
    {
        var path = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"), "graph.db");
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Assert.Throws<FileNotFoundException>(() => GraphStore.Open(path, readOnly: true, TimeSpan.FromSeconds(5)));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed}");
    }

    private static async Task<GraphStore> Store(string source)
    {
        var graph = await Indexed.OfAsync(source);
        var db = new TempDb();
        db.Store.Write(graph.Nodes, graph.Edges);
        return db.Store;
    }
}

public sealed class PathBetweenTests
{
    // A → B → D, A → C → D (two shortest), A → X → Y → D (longer), D → A (a cycle back).
    private static GraphStore Diamond(TempDb db)
    {
        string[] ids = ["A", "B", "C", "D", "X", "Y"];
        db.Store.Write(
            ids.Select(id => Graph.Node($"M:{id}", NodeKind.Method, id, id)),
            [
                new GraphEdge("M:A", "M:B", EdgeKind.Calls), new GraphEdge("M:B", "M:D", EdgeKind.Calls),
                new GraphEdge("M:A", "M:C", EdgeKind.Calls), new GraphEdge("M:C", "M:D", EdgeKind.Calls),
                new GraphEdge("M:A", "M:X", EdgeKind.Calls), new GraphEdge("M:X", "M:Y", EdgeKind.Calls), new GraphEdge("M:Y", "M:D", EdgeKind.Calls),
                new GraphEdge("M:D", "M:A", EdgeKind.Calls),
            ]);
        return db.Store;
    }

    [Fact]
    public void Returns_every_shortest_path_and_only_shortest_ones()
    {
        using var db = new TempDb();
        var paths = Diamond(db).PathBetween("M:A", "M:D", [nameof(EdgeKind.Calls)]);

        Assert.Equal(["A B D", "A C D"], paths.Select(p => string.Join(' ', p.Select(n => n.Name))).Order());
    }

    [Fact]
    public void Honours_the_path_and_depth_limits()
    {
        using var db = new TempDb();
        var store = Diamond(db);

        Assert.Single(store.PathBetween("M:A", "M:D", [nameof(EdgeKind.Calls)], maxPaths: 1));
        Assert.Empty(store.PathBetween("M:A", "M:D", [nameof(EdgeKind.Calls)], maxDepth: 1));
    }

    [Fact]
    public void A_cycle_does_not_trap_the_search_and_other_edge_kinds_are_ignored()
    {
        using var db = new TempDb();
        var store = Diamond(db);

        Assert.Equal(["D", "A", "X"], store.PathBetween("M:D", "M:X", [nameof(EdgeKind.Calls)]).Single().Select(n => n.Name));
        Assert.Empty(store.PathBetween("M:A", "M:D", [nameof(EdgeKind.Inherits)]));
    }
}
