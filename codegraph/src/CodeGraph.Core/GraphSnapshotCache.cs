namespace CodeGraph.Core;

/// <summary>
/// Holds the <see cref="GraphSnapshot"/> of one graph file for a long-lived reader (the MCP server, the web view) and
/// keeps it in step with the file. <see cref="Current"/> never waits: it answers the snapshot when it matches the file
/// as it is now, and otherwise starts loading one in the background and answers null, so the caller reads through
/// DuckDB as it did before snapshots existed. A tool call is therefore never slower than the SQL path, and once the
/// load lands every call after it answers from memory.
/// <para>
/// The file's stamp (write time and length of the database and of its write-ahead log) decides whether a snapshot
/// still matches. A load whose stamp moved while it read is discarded, so a snapshot is never older than the file it
/// claims to be. A timer polls the stamp, so the snapshot is reloaded after a re-index even before the next call.
/// </para>
/// </summary>
public sealed class GraphSnapshotCache : IDisposable
{
    private readonly string _dbPath;
    private readonly GraphAnchor? _anchor;
    private readonly Timer? _timer;
    private readonly Lock _lock = new();
    private (FileStamp Stamp, GraphSnapshot Snapshot)? _entry;
    private Task? _loading;
    private DateTime _retryAfter;
    private string? _lastError;

    /// <param name="dbPath">The graph file.</param>
    /// <param name="anchor">The server's anchor, so a load never reads through an instance of a replaced file.</param>
    /// <param name="poll">How often to check the file for a re-index; null for no timer (loads start on <see cref="Current"/>).</param>
    public GraphSnapshotCache(string dbPath, GraphAnchor? anchor = null, TimeSpan? poll = null)
    {
        _dbPath = Path.GetFullPath(dbPath);
        _anchor = anchor;
        if (poll is { } interval)
        {
            _timer = new Timer(_ => Refresh(), null, TimeSpan.Zero, interval);
        }
    }

    /// <summary>Set CODEGRAPH_SNAPSHOT=off to answer every read from DuckDB, as before snapshots.</summary>
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("CODEGRAPH_SNAPSHOT")?.Trim().ToLowerInvariant() is not ("0" or "off" or "false" or "no");

    /// <summary>The snapshot matching the file right now, or null while one loads (or when there is no file).</summary>
    public GraphSnapshot? Current
    {
        get
        {
            var stamp = FileStamp.Of(_dbPath);
            lock (_lock)
            {
                if (_entry is { } entry && entry.Stamp == stamp)
                {
                    return entry.Snapshot;
                }

                StartLoad(stamp);
                return null;
            }
        }
    }

    /// <summary>Starts a load if the file changed since the last one. Cheap: two <c>stat</c> calls when nothing did.</summary>
    public void Refresh() => _ = Current;

    /// <summary>Waits up to <paramref name="timeout"/> for a snapshot of the file as it is now. For tests, tools and warm-up.</summary>
    public GraphSnapshot? WaitForCurrent(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (Current is { } snapshot)
            {
                return snapshot;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }

            Task? loading;
            lock (_lock)
            {
                loading = _loading;
            }

            if (loading is { IsCompleted: false })
            {
                loading.Wait(deadline - DateTime.UtcNow);
            }
            else
            {
                Thread.Sleep(20);
            }
        }
    }

    /// <summary>One line for <c>index_status</c>: what the in-memory graph holds, or why there is none yet.</summary>
    public string Describe()
    {
        var stamp = FileStamp.Of(_dbPath);
        lock (_lock)
        {
            if (_entry is { } entry && entry.Stamp == stamp)
            {
                var (nodes, edges) = entry.Snapshot.Count();
                return $"in memory: {nodes} nodes, {edges} edges, loaded in {entry.Snapshot.LoadTime.TotalMilliseconds:F0} ms; graph reads answer from memory";
            }

            if (_loading is { IsCompleted: false })
            {
                return "loading into memory; reads go to the database until it is ready";
            }

            return _lastError is null
                ? "not in memory yet; reads go to the database"
                : $"not in memory ({_lastError}); reads go to the database";
        }
    }

    public void Dispose() => _timer?.Dispose();

    private void StartLoad(FileStamp stamp)
    {
        if (_loading is { IsCompleted: false } || DateTime.UtcNow < _retryAfter || !stamp.Exists)
        {
            return;
        }

        _loading = Task.Run(() => Load(stamp));
    }

    private void Load(FileStamp stamp)
    {
        try
        {
            using var lease = _anchor?.AcquireCurrent();
            if (_anchor is not null && lease is null)
            {
                Retry(TimeSpan.FromMilliseconds(200), null); // a call may still be reading a replaced file
                return;
            }

            GraphSnapshot snapshot;
            using (var store = GraphStore.Open(_dbPath, readOnly: true, TimeSpan.FromSeconds(2)))
            {
                snapshot = store.LoadSnapshot();
            }

            if (FileStamp.Of(_dbPath) != stamp)
            {
                return; // written to while we read; the next Current starts over from the new stamp
            }

            lock (_lock)
            {
                _entry = (stamp, snapshot);
                _lastError = null;
            }
        }
        catch (Exception e)
        {
            // Most often the indexer holds the write lock. Whatever it was, reads still have the database; try again
            // shortly rather than on every call.
            Retry(TimeSpan.FromSeconds(1), e.Message.Split('\n')[0]);
        }
    }

    private void Retry(TimeSpan after, string? error)
    {
        lock (_lock)
        {
            _retryAfter = DateTime.UtcNow + after;
            _lastError = error ?? _lastError;
        }
    }

    /// <summary>What a graph file looks like on disk: the database and its write-ahead log.</summary>
    private readonly record struct FileStamp(bool Exists, long Ticks, long Length, long WalTicks, long WalLength)
    {
        public static FileStamp Of(string path)
        {
            var db = new FileInfo(path);
            var wal = new FileInfo(path + ".wal");
            return new FileStamp(
                db.Exists,
                db.Exists ? db.LastWriteTimeUtc.Ticks : 0,
                db.Exists ? db.Length : 0,
                wal.Exists ? wal.LastWriteTimeUtc.Ticks : 0,
                wal.Exists ? wal.Length : 0);
        }
    }
}
