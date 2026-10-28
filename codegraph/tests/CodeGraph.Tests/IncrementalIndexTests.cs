using CodeGraph.Core;

namespace CodeGraph.Tests;

/// <summary>
/// The incremental contract: index v1 in full, then re-walk only the changed files and their dependents, and the
/// stored graph must equal a full index of v2. Each case is an edit that a naive per-file re-index gets wrong.
/// </summary>
public sealed class IncrementalIndexTests
{
    private const string A = "namespace N; class A { public void Run() { new B().Work(); } }";

    public static TheoryData<string, (string Name, string Source)[], (string Name, string Source)[]> Edits => new()
    {
        {
            "body edit adds a call",
            [("A.cs", A), ("B.cs", "namespace N; class B { public void Work() { } void Helper() { } }")],
            [("A.cs", A), ("B.cs", "namespace N; class B { public void Work() { Helper(); } void Helper() { } }")]
        },
        {
            "callee removed: the unchanged caller's edge must go",
            [("A.cs", A), ("B.cs", "namespace N; class B { public void Work() { } }")],
            [("A.cs", A), ("B.cs", "namespace N; class B { public void Other() { } }")]
        },
        {
            "callee renamed back: the unchanged caller re-binds",
            [("A.cs", A), ("B.cs", "namespace N; class B { public void Other() { } }")],
            [("A.cs", A), ("B.cs", "namespace N; class B { public void Work() { } }")]
        },
        {
            "lines shift: spans in the edited file move, others stay",
            [("A.cs", A), ("B.cs", "namespace N; class B { public void Work() { } }")],
            [("A.cs", A), ("B.cs", "namespace N;\n\n\nclass B\n{\n    public void Work() { }\n}")]
        },
        {
            "primary partial removed: the type survives in its other part",
            [("P1.cs", "namespace N; partial class P { void X() { } }"), ("P2.cs", "namespace N; partial class P : I { void Y() { } } interface I { }")],
            [("P1.cs", "namespace N; class Unrelated { }"), ("P2.cs", "namespace N; partial class P : I { void Y() { } } interface I { }")]
        },
        {
            "base type gains a member that a subclass overrides",
            [("Base.cs", "namespace N; class Base { }"), ("D.cs", "namespace N; class D : Base { public override string ToString() => \"\"; }")],
            [("Base.cs", "namespace N; class Base { public override string ToString() => \"b\"; }"), ("D.cs", "namespace N; class D : Base { public override string ToString() => \"\"; }")]
        },
        {
            "referenced type deleted",
            [("A.cs", "namespace N; class A { Model? M; }"), ("Model.cs", "namespace N; class Model { }")],
            [("A.cs", "namespace N; class A { Model? M; }"), ("Model.cs", "namespace N; class Mode1 { }")]
        },
    };

    [Theory]
    [MemberData(nameof(Edits))]
    public async Task Incremental_reindex_matches_a_full_index(string edit, (string Name, string Source)[] before, (string Name, string Source)[] after)
    {
        using var db = new TempDb();
        var v1 = await Indexed.OfAsync(before);
        db.Store.Replace(full: true, [], v1.Nodes, v1.Edges, v1.Origins, [], new Dictionary<string, string>());

        var changed = after.Where(f => before.Single(b => b.Name == f.Name).Source != f.Source)
            .Select(f => Indexed.PathOf(f.Name))
            .ToList();
        var walk = changed.Concat(db.Store.DependentFiles(changed)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var partial = await Indexed.OfAsync(walk.Select(Path.GetFileName).ToList()!, after);
        db.Store.Replace(full: false, [.. walk, .. db.Store.ProjectFilePaths()],
            partial.Nodes, partial.Edges, partial.Origins, [], new Dictionary<string, string>());

        var v2 = await Indexed.OfAsync(after);
        Assert.True(v2.Nodes.OrderBy(n => n.Id).SequenceEqual(db.Store.AllNodes().OrderBy(n => n.Id)), $"{edit}: nodes differ");
        Assert.Equal(v2.Edges.OrderBy(e => e.ToString()), db.Store.AllEdges().OrderBy(e => e.ToString()));
    }

    [Fact]
    public async Task Removing_a_deleted_files_contribution_keeps_edges_another_file_still_produces()
    {
        // Both parts of P emit P : I. Deleting one part must not drop the edge the other still produces.
        using var db = new TempDb();
        var indexed = await Indexed.OfAsync(
            ("P1.cs", "namespace N; partial class P : I { }"),
            ("P2.cs", "namespace N; partial class P : I { } interface I { }"));
        db.Store.Replace(full: true, [], indexed.Nodes, indexed.Edges, indexed.Origins, [], new Dictionary<string, string>());

        db.Store.Replace(full: false, [Indexed.PathOf("P1.cs")], [], [], [], [], new Dictionary<string, string>());

        Assert.Contains(new GraphEdge("T:N.P", "T:N.I", EdgeKind.Implements), db.Store.AllEdges());
    }

    [Fact]
    public async Task Dependent_files_include_callers_of_the_changed_file()
    {
        using var db = new TempDb();
        var indexed = await Indexed.OfAsync(
            ("A.cs", A),
            ("B.cs", "namespace N; class B { public void Work() { } }"),
            ("C.cs", "namespace N; class C { }"));
        db.Store.Replace(full: true, [], indexed.Nodes, indexed.Edges, indexed.Origins, [], new Dictionary<string, string>());

        var dependents = db.Store.DependentFiles([Indexed.PathOf("B.cs")]);

        Assert.Contains(Indexed.PathOf("A.cs"), dependents);
        Assert.DoesNotContain(Indexed.PathOf("C.cs"), dependents);
    }

    [Fact]
    public void Replace_round_trips_file_stamps_and_meta()
    {
        using var db = new TempDb();
        var stamp = new FileStamp("/repo/A.cs", "abc", 12, 34);

        db.Store.Replace(full: true, [], [], [], [], [stamp], new Dictionary<string, string> { [MetaKeys.Commit] = "deadbeef" });

        Assert.Equal(stamp, db.Store.IndexedFiles()["/repo/A.cs"]);
        Assert.Equal("deadbeef", db.Store.ReadMeta()[MetaKeys.Commit]);
    }

    [Fact]
    public void A_read_only_store_releases_the_file_when_disposed()
    {
        // DuckDB allows one writer or many readers. If a disposed reader kept the database open,
        // the MCP server would lock codegraph-index out for good.
        using var temp = new TempDb();
        temp.Store.Write(Graph.Nodes, Graph.Edges);
        temp.Store.Dispose();

        using (var reader = GraphStore.Open(temp.DbPath, readOnly: true, TimeSpan.Zero))
        {
            Assert.Equal(Graph.Nodes.Length, reader.Count().Nodes);
        }

        using var writer = GraphStore.Open(temp.DbPath, readOnly: false, TimeSpan.Zero);
        writer.Write([Graph.Node("T:New", NodeKind.Type, "New", "New")], []);
        Assert.Equal(Graph.Nodes.Length + 1, writer.Count().Nodes);
    }
}
