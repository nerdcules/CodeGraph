using CodeGraph.Core;

namespace CodeGraph.Tests;

public sealed class GraphStoreTests
{
    [Fact]
    public void Write_then_GetNode_round_trips_every_field()
    {
        using var db = new TempDb();
        var node = Graph.Node("M:X.Y", NodeKind.Method, "Y", "X.Y",
            signature: "public int Y(string s)", filePath: "/repo/X.cs", startLine: 10, endLine: 20, isTest: true);
        db.Store.Write([node], []);

        Assert.Equal(node, db.Store.GetNode("M:X.Y"));
    }

    [Fact]
    public void Write_round_trips_null_signature_and_file_path()
    {
        using var db = new TempDb();
        db.Store.Write([Graph.Node("T:X", NodeKind.Type, "X", "X", signature: null, filePath: null)], []);

        var read = db.Store.GetNode("T:X");

        Assert.NotNull(read);
        Assert.Null(read.Signature);
        Assert.Null(read.FilePath);
    }

    [Fact]
    public void GetNode_returns_null_for_an_unknown_id()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        Assert.Null(db.Store.GetNode("M:Nope.Missing"));
    }

    [Fact]
    public void Count_reports_nodes_and_edges()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        Assert.Equal((Graph.Nodes.Length, Graph.Edges.Length), db.Store.Count());
    }

    [Fact]
    public void Writing_the_same_node_twice_replaces_rather_than_duplicates()
    {
        using var db = new TempDb();
        db.Store.Write([Graph.Node("T:X", NodeKind.Type, "X", "X", signature: "first")], []);
        db.Store.Write([Graph.Node("T:X", NodeKind.Type, "X", "X", signature: "second")], []);

        Assert.Equal(1, db.Store.Count().Nodes);
        Assert.Equal("second", db.Store.GetNode("T:X")!.Signature);
    }

    [Fact]
    public void Writing_the_same_edge_twice_is_ignored()
    {
        using var db = new TempDb();
        var edge = new GraphEdge("A", "B", EdgeKind.Calls);
        db.Store.Write([], [edge]);
        db.Store.Write([], [edge]);

        Assert.Equal(1, db.Store.Count().Edges);
    }

    [Fact]
    public void Clear_empties_both_tables()
    {
        using var db = new TempDb();
        db.Store.Seeded();
        db.Store.Clear();

        Assert.Equal((0, 0), db.Store.Count());
    }

    [Fact]
    public void Constructor_creates_the_database_directory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"), "nested");
        try
        {
            using (new GraphStore(Path.Combine(directory, "graph.db")))
            {
                Assert.True(Directory.Exists(directory));
            }
        }
        finally
        {
            TempDb.TryDelete(directory);
        }
    }

    [Fact]
    public void FindSymbol_matches_a_case_insensitive_substring()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        Assert.Contains(db.Store.FindSymbol("saveall"), n => n.Id == Graph.SaveAll);
    }

    [Fact]
    public void FindSymbol_matches_on_full_name_too()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        Assert.Contains(db.Store.FindSymbol("Demo.Outer"), n => n.Id == Graph.Top);
    }

    [Fact]
    public void FindSymbol_puts_an_exact_name_match_first_then_shorter_names()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        var ids = db.Store.FindSymbol("Save").Select(n => n.Id).ToArray();

        Assert.Equal([Graph.Save, Graph.SaveTest, Graph.SaveAll], ids);
    }

    [Fact]
    public void FindSymbol_filters_by_kind()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        var ids = db.Store.FindSymbol("Service", NodeKind.Type).Select(n => n.Id).OrderBy(id => id).ToArray();

        Assert.Equal([Graph.IService, Graph.Service], ids);
    }

    [Fact]
    public void FindSymbol_honours_the_limit()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        Assert.Single(db.Store.FindSymbol("Save", limit: 1));
    }

    [Fact]
    public void FindSymbol_returns_empty_when_nothing_matches()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        Assert.Empty(db.Store.FindSymbol("NoSuchSymbol"));
    }

    [Fact]
    public void FindSymbol_filters_by_accessibility()
    {
        using var db = new TempDb();
        var pub = Graph.Node("M:X.Pub", NodeKind.Method, "Pub", "X.Pub", accessibility: Accessibility.Public);
        var priv = Graph.Node("M:X.Priv", NodeKind.Method, "Priv", "X.Priv", accessibility: Accessibility.Private);
        db.Store.Write([pub, priv], []);

        var results = db.Store.FindSymbol("", accessibility: Accessibility.Public);
        Assert.Single(results, n => n.Id == "M:X.Pub");
    }

    [Fact]
    public void FindSymbol_filters_by_typeKind()
    {
        using var db = new TempDb();
        var iface = Graph.Node("T:X.I", NodeKind.Type, "I", "X.I", typeKind: TypeKind.Interface);
        var cls = Graph.Node("T:X.C", NodeKind.Type, "C", "X.C", typeKind: TypeKind.Class);
        db.Store.Write([iface, cls], []);

        var results = db.Store.FindSymbol("", typeKind: TypeKind.Interface);
        Assert.Single(results, n => n.Id == "T:X.I");
    }

    [Fact]
    public void FindSymbol_filters_by_isStatic()
    {
        using var db = new TempDb();
        var stat = Graph.Node("M:X.S", NodeKind.Method, "S", "X.S", isStatic: true);
        var inst = Graph.Node("M:X.I", NodeKind.Method, "I", "X.I", isStatic: false);
        db.Store.Write([stat, inst], []);

        var results = db.Store.FindSymbol("", isStatic: true);
        Assert.Single(results, n => n.Id == "M:X.S");
    }

    [Fact]
    public void FindSymbol_filters_by_memberKind_Constructor()
    {
        using var db = new TempDb();
        var ctor = Graph.Node("M:X.C.#ctor", NodeKind.Method, ".ctor", "X.C.#ctor", memberKind: MemberKind.Constructor);
        var meth = Graph.Node("M:X.C.M", NodeKind.Method, "M", "X.C.M", memberKind: MemberKind.Ordinary);
        db.Store.Write([ctor, meth], []);

        var results = db.Store.FindSymbol("", memberKind: MemberKind.Constructor);
        Assert.Single(results, n => n.Id == "M:X.C.#ctor");
    }

    [Fact]
    public void Write_then_GetNode_round_trips_new_fields()
    {
        using var db = new TempDb();
        var node = Graph.Node("T:X.R", NodeKind.Type, "R", "X.R",
            typeKind: TypeKind.Record,
            accessibility: Accessibility.Public,
            isAbstract: true,
            isSealed: false);
        db.Store.Write([node], []);

        var read = db.Store.GetNode("T:X.R");
        Assert.Equal(node, read);
    }

    [Fact]
    public void Children_returns_direct_Contains_targets_only()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        var ids = db.Store.Children(Graph.Service).Select(n => n.Id).OrderBy(id => id).ToArray();

        Assert.Equal([Graph.Save, Graph.SaveAll], ids);
    }

    [Fact]
    public void Children_does_not_follow_non_containment_edges()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        // Service implements IService, but Implements is not containment.
        Assert.DoesNotContain(db.Store.Children(Graph.Service), n => n.Id == Graph.IService);
    }

    [Fact]
    public void Callers_at_depth_1_returns_only_direct_callers()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        var ids = db.Store.Callers(Graph.Save).Select(h => h.Node.Id).OrderBy(id => id).ToArray();

        Assert.Equal([Graph.Handle, Graph.SaveTest], ids);
    }

    [Fact]
    public void Callers_walks_transitively_and_reports_depth()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        var byId = db.Store.Callers(Graph.Save, maxDepth: 3).ToDictionary(h => h.Node.Id, h => h.Depth);

        Assert.Equal(1, byId[Graph.Handle]);
        Assert.Equal(2, byId[Graph.Top]);
    }

    [Fact]
    public void Callers_stops_at_maxDepth()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        Assert.DoesNotContain(db.Store.Callers(Graph.Save, maxDepth: 1), h => h.Node.Id == Graph.Top);
    }

    [Fact]
    public void Callees_walks_the_other_direction()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        var byId = db.Store.Callees(Graph.Top, maxDepth: 3).ToDictionary(h => h.Node.Id, h => h.Depth);

        Assert.Equal(1, byId[Graph.Handle]);
        Assert.Equal(2, byId[Graph.Save]);
    }

    [Fact]
    public void A_traversal_never_includes_its_own_origin()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        Assert.DoesNotContain(db.Store.Callers(Graph.Save, maxDepth: 5), h => h.Node.Id == Graph.Save);
        Assert.DoesNotContain(db.Store.Impact(Graph.Save), h => h.Node.Id == Graph.Save);
    }

    [Fact]
    public void A_call_cycle_terminates_instead_of_looping_forever()
    {
        using var db = new TempDb();
        db.Store.Write(
            [Graph.Node("M:A", NodeKind.Method, "A", "A"), Graph.Node("M:B", NodeKind.Method, "B", "B")],
            [new GraphEdge("M:A", "M:B", EdgeKind.Calls), new GraphEdge("M:B", "M:A", EdgeKind.Calls)]);

        var hits = db.Store.Callees("M:A", maxDepth: 10).Select(h => (h.Node.Id, h.Depth)).ToArray();

        // The origin comes back at depth 2 because the cycle genuinely reaches it; each node is still
        // reported once, at its shortest depth, so the walk converges rather than spinning to maxDepth.
        Assert.Equal([("M:B", 1), ("M:A", 2)], hits);
    }

    [Fact]
    public void A_node_reachable_by_two_paths_reports_its_shortest_depth()
    {
        using var db = new TempDb();
        db.Store.Write(
            [
                Graph.Node("M:Target", NodeKind.Method, "Target", "Target"),
                Graph.Node("M:Near", NodeKind.Method, "Near", "Near"),
                Graph.Node("M:Both", NodeKind.Method, "Both", "Both"),
            ],
            [
                new GraphEdge("M:Near", "M:Target", EdgeKind.Calls),
                new GraphEdge("M:Both", "M:Target", EdgeKind.Calls),
                new GraphEdge("M:Both", "M:Near", EdgeKind.Calls),
            ]);

        var both = db.Store.Callers("M:Target", maxDepth: 5).Single(h => h.Node.Id == "M:Both");

        Assert.Equal(1, both.Depth);
    }

    [Fact]
    public void Impact_includes_transitive_callers()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        var ids = db.Store.Impact(Graph.Save).Select(h => h.Node.Id).ToArray();

        Assert.Contains(Graph.Handle, ids);
        Assert.Contains(Graph.Top, ids);
    }

    [Fact]
    public void Impact_follows_Implements_so_an_interface_reaches_its_implementers()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        Assert.Contains(db.Store.Impact(Graph.IService), h => h.Node.Id == Graph.Service);
    }

    [Fact]
    public void Impact_ignores_containment()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        // Service contains Save, but changing Save does not put its declaring type at risk.
        Assert.DoesNotContain(db.Store.Impact(Graph.Save), h => h.Node.Id == Graph.Service);
    }

    [Fact]
    public void Impact_surfaces_guarding_tests_via_IsTest()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        var tests = db.Store.Impact(Graph.Save).Where(h => h.Node.IsTest).Select(h => h.Node.Id).ToArray();

        Assert.Equal([Graph.SaveTest], tests);
    }

    [Fact]
    public void Impact_returns_empty_for_something_nothing_depends_on()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        Assert.Empty(db.Store.Impact(Graph.Top));
    }

    [Fact]
    public void DirectTests_follows_TestedBy_from_the_subject_to_the_test()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        Assert.Equal([Graph.SaveTest], db.Store.DirectTests(Graph.Save).Select(n => n.Id).ToArray());
    }

    [Fact]
    public void DirectTests_returns_empty_for_an_unguarded_member()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        Assert.Empty(db.Store.DirectTests(Graph.SaveAll));
    }

    [Fact]
    public void Data_survives_reopening_the_same_file()
    {
        using var db = new TempDb();
        db.Store.Seeded();
        var expected = db.Store.Count();

        using var reopened = new GraphStore(db.DbPath);

        Assert.Equal(expected, reopened.Count());
    }
}
