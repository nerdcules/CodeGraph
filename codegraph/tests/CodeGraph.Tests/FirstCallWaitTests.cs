using CodeGraph.Core;
using CodeGraph.Mcp;

namespace CodeGraph.Tests;

/// <summary>
/// The first tool call of a server process waits for the graph to load into memory, so its answer prints handles.
/// Only that call waits: a later call that finds no snapshot reads through the database at once.
/// </summary>
public sealed class FirstCallWaitTests
{
    [Fact]
    public void The_first_call_waits_for_the_snapshot_and_a_later_call_does_not()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"));
        var dbPath = Path.Combine(directory, "graph.db");
        try
        {
            using (var writer = new GraphStore(dbPath))
            {
                writer.Replace(full: true, [], Graph.Nodes, Graph.Edges, [], [], new Dictionary<string, string>());
            }

            // No timer: nothing loads until a call asks, as at the start of a server process.
            using var cache = new GraphSnapshotCache(dbPath);
            var firstCall = new FirstCallWait(cache, TimeSpan.FromSeconds(30));
            Assert.StartsWith("not in memory yet", cache.Describe());

            var first = firstCall.Wait();
            Assert.NotNull(first);
            Assert.NotNull(first.GetNode(Graph.Save));
            Assert.Same(first, cache.Current);

            // A re-index moves the file. The next call does not wait and does not start a load.
            using (var writer = new GraphStore(dbPath))
            {
                writer.Replace(full: false, [], [Graph.Node("M:Demo.New", NodeKind.Method, "New", "Demo.New")], [], [], [], new Dictionary<string, string>());
            }

            Assert.Null(firstCall.Wait());
            Assert.StartsWith("not in memory yet", cache.Describe());
        }
        finally
        {
            TempDb.TryDelete(directory);
        }
    }
}
