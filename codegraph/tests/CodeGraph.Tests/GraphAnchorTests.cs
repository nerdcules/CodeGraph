using System.Diagnostics;
using CodeGraph.Core;

namespace CodeGraph.Tests;

/// <summary>
/// The anchor keeps the graph loaded between calls and must let go: when idle, when asked, and when the file has
/// been swapped by a full rebuild. A held reader would lock the indexer out, and a stale one would serve old data.
/// </summary>
public sealed class GraphAnchorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"));
    private readonly string _db;

    public GraphAnchorTests()
    {
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "graph.db");
        using var store = new GraphStore(_db);
        store.Seeded();
    }

    public void Dispose() => TempDb.TryDelete(_dir);

    [Fact]
    public async Task Holds_the_instance_between_calls_and_lets_go_when_idle()
    {
        using var anchor = new GraphAnchor(_db, idleRelease: TimeSpan.FromMilliseconds(100), maxHold: TimeSpan.FromSeconds(30));

        using (anchor.Acquire())
        {
            Assert.True(anchor.IsHeld);
        }

        Assert.True(anchor.IsHeld, "still held right after the call, for the next one");
        await WaitUntil(() => !anchor.IsHeld);
    }

    [Fact]
    public void Never_lets_go_while_a_call_is_in_flight_and_honours_a_release_afterwards()
    {
        using var anchor = new GraphAnchor(_db, idleRelease: TimeSpan.FromMilliseconds(20), maxHold: TimeSpan.FromMilliseconds(20));

        var lease = anchor.Acquire();
        anchor.Release();
        Thread.Sleep(200);
        Assert.True(anchor.IsHeld, "released under a running call");

        lease.Dispose();
        Assert.False(anchor.IsHeld);
    }

    [Fact]
    public void A_released_anchor_lets_a_writer_in()
    {
        using var anchor = new GraphAnchor(_db);
        using (anchor.Acquire())
        {
        }

        anchor.Release();

        using var writer = GraphStore.Open(_db, readOnly: false, TimeSpan.Zero);
        writer.Write([Graph.Node("T:New", NodeKind.Type, "New", "New")], []);
    }

    [Fact]
    public void A_swapped_file_is_read_after_the_next_acquire()
    {
        using var anchor = new GraphAnchor(_db, idleRelease: TimeSpan.FromMinutes(1), maxHold: TimeSpan.FromMinutes(1));
        using (anchor.Acquire())
        using (var store = GraphStore.Open(_db, readOnly: true, TimeSpan.Zero))
        {
            Assert.Equal(Graph.Nodes.Length, store.Count().Nodes);
        }

        // What a full rebuild does: write a fresh file beside it and move it over the old one.
        var fresh = _db + ".tmp";
        using (var store = new GraphStore(fresh))
        {
            store.Write([Graph.Node("T:Only", NodeKind.Type, "Only", "Only")], []);
        }

        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow.AddSeconds(5));
        if (OperatingSystem.IsWindows())
        {
            anchor.Release(); // Windows cannot replace an open file; the indexer's swap waits for this
        }

        File.Move(fresh, _db, overwrite: true);

        anchor.ReleaseIfSwapped();
        using (anchor.Acquire())
        using (var store = GraphStore.Open(_db, readOnly: true, TimeSpan.Zero))
        {
            Assert.Equal(1, store.Count().Nodes);
        }
    }

    [Fact]
    public void Opens_are_cheaper_while_held()
    {
        static double Open(string db)
        {
            var clock = Stopwatch.StartNew();
            using var store = GraphStore.Open(db, readOnly: true, TimeSpan.Zero);
            store.Count();
            return clock.Elapsed.TotalMilliseconds;
        }

        var cold = Enumerable.Range(0, 5).Select(_ => Open(_db)).Order().ElementAt(2);
        using var anchor = new GraphAnchor(_db, idleRelease: TimeSpan.FromMinutes(1));
        using var lease = anchor.Acquire();
        var warm = Enumerable.Range(0, 5).Select(_ => Open(_db)).Order().ElementAt(2);

        Assert.True(warm < cold, $"held {warm:F2} ms vs cold {cold:F2} ms");
    }

    [Fact]
    public void A_missing_graph_is_not_an_error()
    {
        using var anchor = new GraphAnchor(Path.Combine(_dir, "none.db"));
        using (anchor.Acquire())
        {
            Assert.False(anchor.IsHeld);
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached within 5s");
            await Task.Delay(20);
        }
    }
}
