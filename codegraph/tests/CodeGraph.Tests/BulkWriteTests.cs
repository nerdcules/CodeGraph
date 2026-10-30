using System.Diagnostics;
using CodeGraph.Core;
using DuckDB.NET.Data;

namespace CodeGraph.Tests;

/// <summary>
/// The graph is written through DuckDB's appender into staging tables and merged with one statement per table.
/// These pin down the merge rules (last node wins, duplicate edges collapse), that staging never leaks into the
/// file or across writes, and that a solution-sized graph loads in seconds rather than minutes.
/// </summary>
public sealed class BulkWriteTests
{
    private const string FileA = "/repo/A.cs";
    private const string FileB = "/repo/B.cs";

    [Fact]
    public void A_solution_sized_graph_is_written_in_seconds()
    {
        using var db = new TempDb();
        var (nodes, edges, origins, files) = Synthetic(nodeCount: 20_000, edgesPerNode: 5);

        var clock = Stopwatch.StartNew();
        db.Store.Replace(full: true, [], nodes, edges, origins, files, Meta(), search: null);
        clock.Stop();

        Assert.Equal((nodes.Length, edges.Length), db.Store.Count());
        Assert.Equal(files.Length, db.Store.IndexedFiles().Count);
        // Per-row inserts took over ten minutes for this many rows; the appender path takes about a second.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"bulk write took {clock.Elapsed}");
    }

    [Fact]
    public void Every_field_survives_the_bulk_path_for_every_row()
    {
        using var db = new TempDb();
        var (nodes, edges, origins, files) = Synthetic(nodeCount: 2_000, edgesPerNode: 3);
        db.Store.Replace(full: true, [], nodes, edges, origins, files, Meta(), search: null);

        var read = db.Store.AllNodes().ToDictionary(n => n.Id);
        Assert.Equal(nodes.Length, read.Count);
        foreach (var node in nodes)
        {
            Assert.Equal(node, read[node.Id]);
        }

        Assert.Equal(edges.ToHashSet(), db.Store.AllEdges().ToHashSet());
        var stamps = db.Store.IndexedFiles();
        foreach (var file in files)
        {
            Assert.Equal(file, stamps[file.Path]);
        }
    }

    [Fact]
    public void The_last_row_for_a_node_id_in_one_write_wins()
    {
        using var db = new TempDb();
        db.Store.Write(
        [
            Graph.Node("T:X", NodeKind.Type, "X", "X", signature: "first", startLine: 1, endLine: 2),
            Graph.Node("T:X", NodeKind.Type, "X", "X", signature: "second", startLine: 5, endLine: 9),
        ], []);

        Assert.Equal(1, db.Store.Count().Nodes);
        var node = db.Store.GetNode("T:X")!;
        Assert.Equal("second", node.Signature);
        Assert.Equal((5, 9), (node.StartLine, node.EndLine));
    }

    [Fact]
    public void Duplicate_edges_and_origins_in_one_write_collapse_to_one_row()
    {
        using var db = new TempDb();
        var edge = new GraphEdge("A", "B", EdgeKind.Calls);
        db.Store.Replace(full: true, [],
            [Graph.Node("A", NodeKind.Method, "A", "A", filePath: FileA), Graph.Node("B", NodeKind.Method, "B", "B", filePath: FileB)],
            [edge, edge, edge],
            [new EdgeOrigin(edge, FileA), new EdgeOrigin(edge, FileA), new EdgeOrigin(edge, FileB)],
            [], Meta(), search: null);

        Assert.Equal(1, db.Store.Count().Edges);
        // Both files produced the edge, so each is a dependent of the other.
        Assert.Equal(new HashSet<string> { FileA, FileB }, db.Store.DependentFiles([FileA]).ToHashSet());
    }

    [Fact]
    public void The_last_stamp_for_a_file_path_in_one_write_wins()
    {
        using var db = new TempDb();
        db.Store.Replace(full: true, [], [], [], [],
            [new FileStamp(FileA, "old", 1, 1), new FileStamp(FileA, "new", 2, 2)], Meta(), search: null);

        var stamp = Assert.Single(db.Store.IndexedFiles()).Value;
        Assert.Equal(("new", 2L, 2L), (stamp.Hash, stamp.Size, stamp.MtimeTicks));
    }

    [Fact]
    public void An_incremental_replace_updates_moved_nodes_in_place_and_refreshes_stamps()
    {
        using var db = new TempDb();
        var before = Graph.Node("T:X", NodeKind.Type, "X", "X", filePath: FileA, startLine: 1, endLine: 2);
        db.Store.Replace(full: true, [], [before], [], [], [new FileStamp(FileA, "h1", 10, 1)], Meta(), search: null);

        var after = before with { StartLine = 3, EndLine = 8 };
        db.Store.Replace(full: false, [FileA], [after], [], [], [new FileStamp(FileA, "h2", 12, 2)], Meta(), search: null);

        Assert.Equal(1, db.Store.Count().Nodes);
        Assert.Equal(after, db.Store.GetNode("T:X"));
        Assert.Equal("h2", db.Store.IndexedFiles()[FileA].Hash);
    }

    [Fact]
    public void Consecutive_writes_on_one_store_each_merge_only_their_own_rows()
    {
        using var db = new TempDb();
        db.Store.Write([Graph.Node("T:X", NodeKind.Type, "X", "X", signature: "first")], [new GraphEdge("A", "B", EdgeKind.Calls)]);
        db.Store.Write([Graph.Node("T:X", NodeKind.Type, "X", "X", signature: "second")], []);
        db.Store.Write([Graph.Node("T:Y", NodeKind.Type, "Y", "Y")], [new GraphEdge("B", "C", EdgeKind.Calls)]);

        Assert.Equal((2, 2), db.Store.Count());
        Assert.Equal("second", db.Store.GetNode("T:X")!.Signature);
    }

    [Fact]
    public void A_full_replace_leaves_nothing_of_the_previous_graph()
    {
        using var db = new TempDb();
        var (nodes, edges, origins, files) = Synthetic(nodeCount: 500, edgesPerNode: 2, idPrefix: "old");
        db.Store.Replace(full: true, [], nodes, edges, origins, files, Meta(), search: null);

        db.Store.Replace(full: true, [], Graph.Nodes, Graph.Edges, [], [], Meta(), search: null);

        Assert.Equal((Graph.Nodes.Length, Graph.Edges.Length), db.Store.Count());
        Assert.Empty(db.Store.IndexedFiles());
    }

    [Fact]
    public void Staging_tables_never_reach_the_database_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"));
        var dbPath = Path.Combine(directory, "graph.db");
        try
        {
            using (var store = new GraphStore(dbPath))
            {
                store.Seeded();
                store.Replace(full: false, [], Graph.Nodes, Graph.Edges, [], [new FileStamp(FileA, "h", 1, 1)], Meta(), search: null);
            }

            using (var connection = new DuckDBConnection($"Data Source={dbPath};ACCESS_MODE=READ_ONLY"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_name LIKE 'stage_%';";
                using var reader = command.ExecuteReader();
                Assert.False(reader.Read(), "a staging table was persisted");
            }

            // A fresh connection recreates its own staging tables and keeps working.
            using (var store = new GraphStore(dbPath))
            {
                store.Write([Graph.Node("T:Z", NodeKind.Type, "Z", "Z")], []);
                Assert.Equal(Graph.Nodes.Length + 1, store.Count().Nodes);
            }
        }
        finally
        {
            TempDb.TryDelete(directory);
        }
    }

    [Fact]
    public void A_read_only_reader_sees_the_whole_graph_after_a_bulk_replace()
    {
        using var db = new TempDb();
        var (nodes, edges, origins, files) = Synthetic(nodeCount: 3_000, edgesPerNode: 4);
        db.Store.Replace(full: true, [], nodes, edges, origins, files, Meta(), search: null);

        using var reader = new GraphStore(db.DbPath, readOnly: true);
        Assert.Equal((nodes.Length, edges.Length), reader.Count());
        Assert.Equal(files.Length, reader.IndexedFiles().Count);
    }

    [Fact]
    public void An_empty_write_is_a_no_op()
    {
        using var db = new TempDb();
        db.Store.Seeded();
        db.Store.Replace(full: false, [], [], [], [], [], Meta(), search: null);

        Assert.Equal((Graph.Nodes.Length, Graph.Edges.Length), db.Store.Count());
    }

    private static Dictionary<string, string> Meta() => new() { ["schema_version"] = GraphStore.SchemaVersion };

    /// <summary>A deterministic graph of the shape the indexer emits: one file per 50 nodes, varied flags, chained edges.</summary>
    private static (GraphNode[] Nodes, GraphEdge[] Edges, EdgeOrigin[] Origins, FileStamp[] Files) Synthetic(
        int nodeCount, int edgesPerNode, string idPrefix = "M")
    {
        var nodes = new GraphNode[nodeCount];
        for (var i = 0; i < nodeCount; i++)
        {
            nodes[i] = new GraphNode(
                $"{idPrefix}:N{i}", i % 50 == 0 ? NodeKind.Type : NodeKind.Method, $"N{i}", $"Ns.T{i / 50}.N{i}",
                i % 7 == 0 ? null : $"public void N{i}(int x)", $"/repo/F{i / 50}.cs", i, i + (i % 13),
                IsTest: i % 11 == 0, TypeKind: i % 50 == 0 ? TypeKind.Class : TypeKind.None,
                Accessibility: (Accessibility)(i % 3), IsStatic: i % 2 == 0, IsAbstract: i % 5 == 0,
                IsVirtual: i % 6 == 0, IsSealed: i % 9 == 0, IsAsync: i % 4 == 0, IsOverride: i % 8 == 0,
                MemberKind: (MemberKind)(i % 3));
        }

        var edges = new List<GraphEdge>(nodeCount * edgesPerNode);
        var origins = new List<EdgeOrigin>(nodeCount * edgesPerNode);
        for (var i = 0; i < nodeCount; i++)
        {
            for (var j = 1; j <= edgesPerNode; j++)
            {
                var edge = new GraphEdge(nodes[i].Id, nodes[(i * 31 + j * 17) % nodeCount].Id, j % 2 == 0 ? EdgeKind.Calls : EdgeKind.References);
                edges.Add(edge);
                origins.Add(new EdgeOrigin(edge, nodes[i].FilePath!));
            }
        }

        var files = Enumerable.Range(0, (nodeCount + 49) / 50)
            .Select(f => new FileStamp($"/repo/F{f}.cs", $"hash{f}", f * 100, f * 1000))
            .ToArray();

        return (nodes, edges.Distinct().ToArray(), origins.ToArray(), files);
    }
}
