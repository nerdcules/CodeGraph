using System.Diagnostics;
using CodeGraph.Core.Live;
using Microsoft.CodeAnalysis;

namespace CodeGraph.Indexer;

/// <summary>
/// The daemon's state machine, apart from the file watcher, MSBuild and the console so it can be tested: load once,
/// index, then for every batch of changes apply them to the loaded solution in memory (or reload it, for a project
/// file or lost watcher events) and re-index. <see cref="FlushAsync"/> lets the MCP server skip the debounce and
/// wait until everything saved so far is in the graph.
/// </summary>
public sealed class WatchLoop
{
    private readonly Func<CancellationToken, Task<Solution>> _load;
    private readonly Func<Solution, bool, CancellationToken, Task> _index;
    private readonly Func<string, string?> _read;
    private readonly Action<string> _log;
    private readonly TimeSpan _debounce;
    private readonly string _solutionPath;
    private readonly string _dbPath;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TaskCompletionSource<LiveStatus>> _flushWaiters = [];
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private bool _lostEvents;
    private bool _flushRequested;
    private bool _indexing;
    private DateTime? _lastIndexedAt;
    private double? _lastIndexSeconds;
    private Solution? _current;

    /// <param name="load">Loads the solution from disk (MSBuild in the real daemon); called again on a reload.</param>
    /// <param name="index">One incremental index pass; <c>true</c> forces a full rebuild.</param>
    /// <param name="read">Current text of a changed file, null when it no longer exists.</param>
    public WatchLoop(
        Func<CancellationToken, Task<Solution>> load,
        Func<Solution, bool, CancellationToken, Task> index,
        Func<string, string?> read,
        Action<string> log,
        string solutionPath,
        string dbPath,
        TimeSpan? debounce = null)
    {
        _load = load;
        _index = index;
        _read = read;
        _log = log;
        _solutionPath = solutionPath;
        _dbPath = dbPath;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(500);
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>The solution the last successful pass indexed; null while the first load runs.</summary>
    public Solution? Current => Volatile.Read(ref _current);

    /// <summary>When anything last happened: a change, a pass, or a live request (see <see cref="Touch"/>). For idle exit.</summary>
    public DateTime LastActivity { get; private set; }

    /// <summary>Completes after the first load and index.</summary>
    public Task Ready => _ready.Task;

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Records activity from outside the loop, such as a live request.</summary>
    public void Touch()
    {
        lock (_lock)
        {
            LastActivity = DateTime.UtcNow;
        }
    }

    /// <summary>A path the watcher reported. Only source and project files matter.</summary>
    public void Enqueue(string path)
    {
        if (!WorkspaceSync.IsSourceFile(path) && !WorkspaceSync.IsProjectFile(path))
        {
            return;
        }

        lock (_lock)
        {
            _pending.Add(Path.GetFullPath(path));
            LastActivity = DateTime.UtcNow;
        }

        _wake.Release();
    }

    /// <summary>The watcher's buffer overflowed: some saves were never reported, so the next pass reloads from disk.</summary>
    public void LostEvents()
    {
        lock (_lock)
        {
            _lostEvents = true;
        }

        _wake.Release();
    }

    public LiveStatus Status()
    {
        lock (_lock)
        {
            var current = _current;
            return new LiveStatus(
                Environment.ProcessId,
                _solutionPath,
                _dbPath,
                current?.Projects.Count(p => p.Language == LanguageNames.CSharp) ?? 0,
                current?.Projects.Where(p => p.Language == LanguageNames.CSharp).Sum(p => p.DocumentIds.Count) ?? 0,
                _startedAt,
                _lastIndexedAt,
                _lastIndexSeconds,
                _pending.Count,
                _indexing,
                Loading: current is null);
        }
    }

    /// <summary>
    /// Completes once every change seen so far is indexed, skipping the debounce. Returns at once when there is
    /// nothing to do, and while the first load runs (the graph from the last run is the best there is until then).
    /// </summary>
    public Task<LiveStatus> FlushAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<LiveStatus> waiter;
        lock (_lock)
        {
            if (_current is null || (_pending.Count == 0 && !_indexing && !_lostEvents))
            {
                return Task.FromResult(Status());
            }

            waiter = new TaskCompletionSource<LiveStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            _flushWaiters.Add(waiter);
            _flushRequested = true;
        }

        _wake.Release();
        cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
        return waiter.Task;
    }

    /// <summary>Loads, indexes, then re-indexes on every change until cancelled.</summary>
    public async Task RunAsync(bool forceFull, CancellationToken cancellationToken)
    {
        var solution = await _load(cancellationToken);
        SetIndexing(true);
        try
        {
            var clock = Stopwatch.StartNew();
            await _index(solution, forceFull, cancellationToken);
            Indexed(solution, clock.Elapsed.TotalSeconds);
        }
        finally
        {
            SetIndexing(false);
        }

        _ready.TrySetResult();

        while (!cancellationToken.IsCancellationRequested)
        {
            await _wake.WaitAsync(cancellationToken);

            // Debounce: editors and git touch files in bursts; wait for a quiet moment before walking.
            // Anything that lands during the walk wakes the loop again. A flush skips the wait.
            bool urgent;
            lock (_lock)
            {
                urgent = _flushRequested;
                _flushRequested = false;
            }

            if (!urgent)
            {
                await Task.Delay(_debounce, cancellationToken);
            }

            List<string> batch;
            bool reload;
            lock (_lock)
            {
                batch = [.. _pending];
                _pending.Clear();
                reload = _lostEvents;
                _lostEvents = false;
                _indexing = batch.Count > 0 || reload;
            }

            while (_wake.CurrentCount > 0)
            {
                _wake.Wait(0);
            }

            if (batch.Count == 0 && !reload)
            {
                PassCompleted();
                continue;
            }

            var pass = Stopwatch.StartNew();
            try
            {
                if (reload || batch.Any(WorkspaceSync.IsProjectFile))
                {
                    _log(reload ? "watcher events were lost; reloading the solution" : "project file changed; reloading the solution");
                    solution = await _load(cancellationToken);
                }
                else
                {
                    solution = WorkspaceSync.Apply(solution, batch, _read);
                }

                await _index(solution, false, cancellationToken);
                Indexed(solution, pass.Elapsed.TotalSeconds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One bad batch (a file locked mid-save, a db lock held too long) must not end the watch.
                _log($"re-index failed: {exception.Message}; will retry on the next change");
                lock (_lock)
                {
                    _pending.UnionWith(batch);
                    _lostEvents |= reload;
                }

                _wake.Release();
            }
            finally
            {
                SetIndexing(false);
                PassCompleted();
            }
        }
    }

    private void Indexed(Solution solution, double seconds)
    {
        lock (_lock)
        {
            _current = solution;
            _lastIndexedAt = DateTime.UtcNow;
            _lastIndexSeconds = Math.Round(seconds, 2);
            LastActivity = DateTime.UtcNow;
        }
    }

    private void SetIndexing(bool value)
    {
        lock (_lock)
        {
            _indexing = value;
        }
    }

    private void PassCompleted()
    {
        List<TaskCompletionSource<LiveStatus>> done;
        lock (_lock)
        {
            if (_pending.Count > 0 || _lostEvents)
            {
                return; // more arrived during the walk, or it failed; the loop goes round again and they wait for that
            }

            done = [.. _flushWaiters];
            _flushWaiters.Clear();
        }

        var status = Status();
        foreach (var waiter in done)
        {
            waiter.TrySetResult(status);
        }
    }
}
