using CodeGraph.Core;
using CodeGraph.Core.Live;
using CodeGraph.Mcp;

namespace CodeGraph.Tests;

/// <summary>
/// Rows print an 8-character handle for a node id, from the graph in memory or from the database, and every
/// id-taking tool accepts the handle or the id. Two ids whose handles collide keep their long form.
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
    public void Rows_print_the_handle_with_a_snapshot_and_without_one()
    {
        using var db = new TempDb();
        db.Store.Seeded();
        var snapshot = db.Store.LoadSnapshot();
        using var reader = GraphStore.OpenReader(db.DbPath, snapshot, TimeSpan.FromSeconds(5));
        var handle = NodeHandles.For(snapshot).Handle(Graph.Save);

        // Without a snapshot the call reads through the database, and the handles come from its id list.
        var withSnapshot = Tools(reader, db.DbPath).FindSymbol("Save", nameMatch: "Exact");
        var withoutSnapshot = Tools(db.Store, db.DbPath).FindSymbol("Save", nameMatch: "Exact");

        Assert.NotEqual(Graph.Save, handle);
        Assert.EndsWith($"  [{handle}]\n", withSnapshot.ReplaceLineEndings("\n"));
        Assert.Equal(withSnapshot, withoutSnapshot);
        Assert.Same(NodeHandles.For(snapshot), NodeHandles.For(snapshot));
        Assert.Equal(Graph.Save, NodeHandles.For(snapshot).Resolve(handle));
    }

    [Fact]
    public void An_argument_loses_its_brackets_and_a_known_handle_becomes_its_id()
    {
        var handles = new NodeHandles([Graph.Save, Graph.Handle]);
        var handle = handles.Handle(Graph.Save);

        Assert.Equal(Graph.Save, NodeIds.Normalize($"[{handle}]", handles));
        Assert.Equal(Graph.Save, NodeIds.Normalize(handle, handles));
        Assert.Equal(Graph.Save, NodeIds.Normalize($"[{Graph.Save}]", handles));
        Assert.Equal("aaaaaaaa", NodeIds.Normalize("aaaaaaaa", handles));
        Assert.Equal(handle, NodeIds.Normalize(handle, null));
        Assert.Equal("Service.Save", NodeIds.Normalize("Service.Save", handles));
    }

    [Fact]
    public void The_cache_has_handles_before_the_first_load_and_while_a_reload_runs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"));
        var dbPath = Path.Combine(directory, "graph.db");
        try
        {
            using (var writer = new GraphStore(dbPath))
            {
                writer.Replace(full: true, [], Graph.Nodes, Graph.Edges, [], [], new Dictionary<string, string>());
            }

            // No timer and no call to Current: no snapshot loads, as at the start of a server process.
            using var cache = new GraphSnapshotCache(dbPath);
            var handle = NodeHandles.Encode(NodeHandles.Hash(Graph.Save));
            Assert.Null(cache.Handles());

            NodeHandles? fromDatabase;
            using (var reader = GraphStore.Open(dbPath, readOnly: true, TimeSpan.FromSeconds(5)))
            {
                // A name does not ask for the store. A name with the shape of a handle does, and comes back as it was.
                var asked = 0;
                Assert.Equal("Service.Save", NodeIds.Resolve(" Service.Save ", cache, () => { asked++; return reader; }));
                Assert.Equal(0, asked);
                Assert.True(NodeHandles.LooksLikeHandle("validate"));
                Assert.Equal("validate", NodeIds.Resolve("validate", cache, () => { asked++; return reader; }));
                Assert.Equal(1, asked);

                // The handles were made once from the id list, and now answer with no reader.
                fromDatabase = cache.Handles();
                Assert.NotNull(fromDatabase);
                Assert.Same(fromDatabase, cache.Handles(reader));
                Assert.Equal(handle, fromDatabase.Handle(Graph.Save));
                Assert.Equal(Graph.Save, NodeIds.Resolve($"[{handle}]", cache, () => { asked++; return reader; }));
                Assert.Equal(1, asked);
                Assert.StartsWith("not in memory yet", cache.Describe());
            }

            // After a load, the handles are those of the snapshot.
            var loaded = cache.WaitForCurrent(TimeSpan.FromSeconds(30));
            Assert.NotNull(loaded);
            Assert.Same(NodeHandles.For(loaded), cache.Handles());
            Assert.NotSame(fromDatabase, cache.Handles());

            // The file moves away: its stamp changes and no load can finish, as in a reload that takes a long time.
            // The last snapshot that loaded still resolves the handle, and nothing opens the database.
            File.Move(dbPath, dbPath + ".away");
            Assert.Null(cache.Current);
            Assert.Same(NodeHandles.For(loaded), cache.Handles());
            Assert.Equal(Graph.Save, NodeIds.Resolve($"[{handle}]", cache, () => throw new InvalidOperationException("no store is necessary")));
            Assert.Equal("aaaaaaaa", NodeIds.Resolve("aaaaaaaa", cache, () => throw new InvalidOperationException("no store is necessary")));
        }
        finally
        {
            TempDb.TryDelete(directory);
        }
    }

    [Fact]
    public void Without_a_store_and_before_a_load_a_handle_comes_back_as_it_was()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"));
        try
        {
            using var cache = new GraphSnapshotCache(Path.Combine(directory, "graph.db"));

            Assert.Null(cache.Handles());
            Assert.Equal("validate", NodeIds.Resolve("[validate]", cache, () => null));
        }
        finally
        {
            TempDb.TryDelete(directory);
        }
    }

    private static CodeGraphTools Tools(GraphStore store, string dbPath) =>
        new(store, new IndexFreshness(dbPath, TimeSpan.FromSeconds(3)), new Telemetry(null),
            new LiveClient(dbPath), new SemanticSearch(null), new GraphSnapshotCache(dbPath), new AnswerPaths(null));
}
