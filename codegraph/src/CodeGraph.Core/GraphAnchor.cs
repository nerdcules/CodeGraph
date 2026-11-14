using DuckDB.NET.Data;

namespace CodeGraph.Core;

/// <summary>
/// Keeps the graph's DuckDB instance loaded between tool calls. Opening a read-only connection costs about 11 ms
/// when nothing holds the database and about 1 ms when another connection in the process does, because DuckDB.NET
/// shares the instance. The anchor is that other connection. Each call still opens its own connection, so calls
/// stay independent and concurrent.
/// <para>
/// A held reader blocks the indexer's write lock, so the anchor lets go: after <see cref="IdleRelease"/> with no
/// call in flight, after <see cref="MaxHold"/> however busy the server is (so a writer retrying every 100 ms gets a
/// window), and on <see cref="Release"/> when the daemon says it is about to write. It also re-opens when a full
/// rebuild has swapped a new file in, since the old instance would keep reading the replaced file.
/// </para>
/// </summary>
public sealed class GraphAnchor : IDisposable
{
    private readonly string _dbPath;
    private readonly Lock _lock = new();
    private readonly Timer _timer;
    private DuckDBConnection? _anchor;
    private (DateTime Write, long Length) _openedStamp;
    private DateTime _openedAt;
    private DateTime _lastUse;
    private int _active;
    private bool _releaseRequested;

    public GraphAnchor(string dbPath, TimeSpan? idleRelease = null, TimeSpan? maxHold = null)
    {
        _dbPath = dbPath;
        IdleRelease = idleRelease ?? TimeSpan.FromSeconds(1.5);
        MaxHold = maxHold ?? TimeSpan.FromSeconds(10);
        var tick = TimeSpan.FromMilliseconds(Math.Clamp(IdleRelease.TotalMilliseconds / 4, 10, 250));
        _timer = new Timer(_ => Sweep(), null, tick, tick);
    }

    public TimeSpan IdleRelease { get; }

    public TimeSpan MaxHold { get; }

    /// <summary>Whether the instance is held right now. For tests and status lines.</summary>
    public bool IsHeld
    {
        get
        {
            lock (_lock)
            {
                return _anchor is not null;
            }
        }
    }

    /// <summary>Marks a call in flight and makes sure the instance is loaded. Dispose the lease when the call ends.</summary>
    public Lease Acquire() => AcquireCore(requireCurrent: false, open: true)!;

    /// <summary>
    /// A lease for the snapshot loader: null when this process may still be reading a file a full rebuild has
    /// replaced, since a connection opened now would share that instance and read the old graph, and a snapshot read
    /// from it would be served as the new one. The loader tries again once the other calls end. It does not open the
    /// anchor either: a load follows every re-index, and an anchor held after it would stand in the next writer's way.
    /// </summary>
    public Lease? AcquireCurrent() => AcquireCore(requireCurrent: true, open: false);

    private Lease? AcquireCore(bool requireCurrent, bool open)
    {
        lock (_lock)
        {
            if (requireCurrent && _active > 0 && (_anchor is null || Stamp() != _openedStamp))
            {
                return null; // calls in flight may hold an instance of a file that has since been swapped
            }

            _active++;
            _lastUse = DateTime.UtcNow;
            if (_anchor is not null && _active == 1 && Stamp() != _openedStamp)
            {
                CloseAnchor(); // a full rebuild swapped the file; the held instance is reading the old one
            }

            if (open && _anchor is null && !_releaseRequested && File.Exists(_dbPath))
            {
                try
                {
                    var connection = new DuckDBConnection(GraphStore.ReadOnlyConnectionString(_dbPath));
                    connection.Open();
                    _anchor = connection;
                    _openedStamp = Stamp();
                    _openedAt = DateTime.UtcNow;
                }
                catch (DuckDBException)
                {
                    // The indexer holds the write lock; this call opens its own connection with the usual retry.
                }
            }

            return new Lease(this);
        }
    }

    /// <summary>
    /// Drops the instance if a full rebuild has swapped the file since it was opened and no call is in flight.
    /// Call before reading anything outside a lease, such as the freshness metadata.
    /// </summary>
    public void ReleaseIfSwapped()
    {
        lock (_lock)
        {
            if (_anchor is not null && _active == 0 && Stamp() != _openedStamp)
            {
                CloseAnchor();
            }
        }
    }

    /// <summary>Lets go as soon as no call is in flight, and stays released until the next <see cref="Acquire"/> after that.</summary>
    public void Release()
    {
        lock (_lock)
        {
            if (_active == 0)
            {
                CloseAnchor();
            }
            else
            {
                _releaseRequested = true;
            }
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_lock)
        {
            CloseAnchor();
        }
    }

    private void End()
    {
        lock (_lock)
        {
            _active--;
            _lastUse = DateTime.UtcNow;
            if (_active == 0 && _releaseRequested)
            {
                CloseAnchor();
                _releaseRequested = false;
            }
        }
    }

    private void Sweep()
    {
        lock (_lock)
        {
            if (_anchor is null || _active > 0)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (now - _lastUse >= IdleRelease || now - _openedAt >= MaxHold)
            {
                CloseAnchor();
            }
        }
    }

    private void CloseAnchor()
    {
        _anchor?.Dispose();
        _anchor = null;
    }

    private (DateTime, long) Stamp()
    {
        var info = new FileInfo(_dbPath);
        return info.Exists ? (info.LastWriteTimeUtc, info.Length) : default;
    }

    /// <summary>One call's hold on the anchor.</summary>
    public sealed class Lease(GraphAnchor owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.End();
            }
        }
    }
}
