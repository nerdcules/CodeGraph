using CodeGraph.Indexer;

namespace CodeGraph.Tests;

/// <summary><see cref="GraphFile.LockAsync"/>: one writer per graph, across processes and within one.</summary>
public sealed class GraphFileTests
{
    [Fact]
    public async Task A_second_writer_waits_until_the_first_releases_the_lock()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"));
        var dbPath = Path.Combine(directory, ".codegraph", "graph.db");
        try
        {
            var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var first = await GraphFile.LockAsync(dbPath, log.Enqueue, TestContext.Current.CancellationToken);
            var second = GraphFile.LockAsync(dbPath, log.Enqueue, TestContext.Current.CancellationToken);

            await Task.Delay(600, TestContext.Current.CancellationToken);
            Assert.False(second.IsCompleted);
            Assert.Contains(log, line => line.StartsWith("another index run is writing", StringComparison.Ordinal));

            first.Dispose();
            using (await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))
            {
                Assert.True(File.Exists(dbPath + ".lock"));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_waiting_writer_stops_when_cancelled()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"));
        var dbPath = Path.Combine(directory, "graph.db");
        try
        {
            using var held = await GraphFile.LockAsync(dbPath, _ => { }, TestContext.Current.CancellationToken);
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GraphFile.LockAsync(dbPath, _ => { }, cancel.Token));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
