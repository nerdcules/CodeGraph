using CodeGraph.Core;

namespace CodeGraph.Mcp;

/// <summary>
/// Lets the first tool call of a server process wait a short time for the graph to load into memory. Rows print
/// 8-character handles only from a snapshot. A client that called at once on a large graph (Jellyfin, 25,336 nodes)
/// got full ids in its first answer and handles after that. Only the first call waits, and only once per process:
/// a reload after a re-index does not make a later call wait.
/// </summary>
public sealed class FirstCallWait(GraphSnapshotCache snapshots, TimeSpan limit)
{
    private int _pending = 1;

    /// <summary>
    /// On the first call: the current snapshot, after a wait of at most the limit if there is none yet. Null on the
    /// first call if the limit ends before the load. On each call after the first: null, at once, and the cache is
    /// not touched.
    /// </summary>
    public GraphSnapshot? Wait()
    {
        if (Interlocked.Exchange(ref _pending, 0) == 0)
        {
            return null;
        }

        return snapshots.Current ?? snapshots.WaitForCurrent(limit);
    }
}
