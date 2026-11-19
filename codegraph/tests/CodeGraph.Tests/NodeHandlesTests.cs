using CodeGraph.Core;
using CodeGraph.Core.Live;
using CodeGraph.Mcp;

namespace CodeGraph.Tests;

/// <summary>
/// Rows print an 8-character handle for a node id while the graph is in memory, and every id-taking tool accepts
/// the handle or the id. Two ids whose handles collide keep their long form.
/// </summary>
public sealed class NodeHandlesTests
{
    [Fact]
    public void A_handle_is_8_base32_characters_and_is_the_same_every_time()
    {
        var handles = new NodeHandles([Graph.Save, Graph.Handle, Graph.Top]);
        var handle = handles.Handle(Graph.Save);

        Assert.Equal(8, handle.Length);
        Assert.Matches("^[a-z2-7]{8}$", handle);
        Assert.True(NodeHandles.LooksLikeHandle(handle));
        Assert.Equal(Graph.Save, handles.Resolve(handle));
        Assert.Equal(handle, new NodeHandles([Graph.Save]).Handle(Graph.Save));
        Assert.Equal(NodeHandles.Encode(NodeHandles.Hash(Graph.Save)), handle);
        Assert.Equal(3, handles.Count);
        Assert.Equal(0, handles.Collisions);
    }

    [Fact]
    public void Ids_whose_handles_collide_keep_their_long_form()
    {
        var handles = new NodeHandles(["A", "B", "C"], id => id == "C" ? 7UL : 1UL);

        Assert.Equal("A", handles.Handle("A"));
        Assert.Equal("B", handles.Handle("B"));
        Assert.Null(handles.Resolve(NodeHandles.Encode(1UL)));
        Assert.Equal(NodeHandles.Encode(7UL), handles.Handle("C"));
        Assert.Equal("C", handles.Resolve(NodeHandles.Encode(7UL)));
        Assert.Equal(1, handles.Collisions);
        Assert.Equal(1, handles.Count);
    }

    [Fact]
    public void No_node_id_looks_like_a_handle_and_an_unknown_handle_resolves_to_nothing()
    {
        Assert.False(NodeHandles.LooksLikeHandle("M:Demo.Save"));
        Assert.False(NodeHandles.LooksLikeHandle("ts:a/b#c"));
        Assert.False(NodeHandles.LooksLikeHandle("abcdefg"));
        Assert.False(NodeHandles.LooksLikeHandle("ABCDEFGH"));
        Assert.False(NodeHandles.LooksLikeHandle("abcdefg1"));
        Assert.Equal("aaaaaaaa", NodeHandles.Encode(0));
        Assert.Equal("7aaaaaaa", NodeHandles.Encode(31));
        Assert.Null(new NodeHandles([Graph.Save]).Resolve("aaaaaaaa"));
    }

    [Fact]
    public void Rows_print_the_handle_with_a_snapshot_and_the_id_without_one()
    {
        using var db = new TempDb();
        db.Store.Seeded();
        var snapshot = db.Store.LoadSnapshot();
        using var reader = GraphStore.OpenReader(db.DbPath, snapshot, TimeSpan.FromSeconds(5));
        var handle = NodeHandles.For(snapshot).Handle(Graph.Save);

        var withSnapshot = Tools(reader, db.DbPath).FindSymbol("Save", nameMatch: "Exact");
        var withoutSnapshot = Tools(db.Store, db.DbPath).FindSymbol("Save", nameMatch: "Exact");

        Assert.NotEqual(Graph.Save, handle);
        Assert.EndsWith($"  [{handle}]\n", withSnapshot.ReplaceLineEndings("\n"));
        Assert.EndsWith($"  [{Graph.Save}]\n", withoutSnapshot.ReplaceLineEndings("\n"));
        Assert.Same(NodeHandles.For(snapshot), NodeHandles.For(snapshot));
        Assert.Equal(Graph.Save, NodeHandles.For(snapshot).Resolve(handle));
    }

    private static CodeGraphTools Tools(GraphStore store, string dbPath) =>
        new(store, new IndexFreshness(dbPath, TimeSpan.FromSeconds(3)), new Telemetry(null),
            new LiveClient(dbPath), new SemanticSearch(null), new GraphSnapshotCache(dbPath), new AnswerPaths(null));
}
