using CodeGraph.Core.Live;

namespace CodeGraph.Indexer;

/// <summary>
/// <c>--watch</c> for a language indexed by an external extractor (Node for TypeScript, a SCIP indexer for Java and
/// the rest): index, then index again on every relevant save, debounced. It keeps the same state the C# watch
/// loop reports over the pipe, so the MCP server's ping, flush and idle-exit work unchanged: saves pending, an
/// index running, when the last one finished.
/// </summary>
internal sealed class ReindexWatch(string root, IReadOnlySet<string> extensions, Func<CancellationToken, Task<int>> index, Action<string> log)
{
    private static readonly HashSet<string> IgnoredFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".codegraph", "dist", ".angular", ".nx", "coverage", "target", "build", ".gradle",
        "bin", "obj", "__pycache__", ".venv", "venv", ".idea", ".vs", "out",
    };

    private readonly SemaphoreSlim _changed = new(0);
    private readonly object _gate = new();
    private readonly HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource _flushNow = NewSignal();
    private TaskCompletionSource _indexed = NewSignal();
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;
    private volatile bool _indexing;

    public string Root => root;

    public DateTime StartedAt { get; } = DateTime.UtcNow;

    public DateTime? LastIndexedAt { get; private set; }

    public double? LastIndexSeconds { get; private set; }

    public int LastExit { get; private set; } = -1;

    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _pendingPaths.Count;
            }
        }
    }

    public bool Indexing => _indexing;

    /// <summary>Stop after this long with no save and no live request; null runs until stopped.</summary>
    public TimeSpan? IdleExit { get; init; }

    /// <summary>Raised after each re-index with the files saved since the previous one, so a language server can re-read them.</summary>
    public event Func<IReadOnlyList<string>, CancellationToken, Task>? Indexed;

    /// <summary>Whether a change to <paramref name="path"/> can change the graph: a source or build file outside build output.</summary>
    public static bool Relevant(string path, IReadOnlySet<string> extensions)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !segments.Any(IgnoredFolders.Contains) && extensions.Contains(Path.GetExtension(path));
    }

    public void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    public bool IsIdle(DateTime now) =>
        IdleExit is { } idle && !_indexing && Pending == 0 && now - new DateTime(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc) > idle;

    public void Enqueue(string path)
    {
        if (!Relevant(path, extensions))
        {
            return;
        }

        lock (_gate)
        {
            _pendingPaths.Add(path);
        }

        Touch();
        _changed.Release();
    }

    /// <summary>Re-indexes now if saves are waiting (cutting the debounce short) and returns once that pass is done.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        Touch();
        if (Pending == 0 && !_indexing)
        {
            return;
        }

        Task indexed;
        lock (_gate)
        {
            indexed = _indexed.Task;
            _flushNow.TrySetResult();
        }

        await indexed.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken);
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        await IndexOnceAsync([], cancellationToken);
        using var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
            InternalBufferSize = 64 * 1024,
        };
        watcher.Changed += (_, e) => Enqueue(e.FullPath);
        watcher.Created += (_, e) => Enqueue(e.FullPath);
        watcher.Deleted += (_, e) => Enqueue(e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            Enqueue(e.OldFullPath);
            Enqueue(e.FullPath);
        };
        watcher.EnableRaisingEvents = true;
        log($"watching {root} (Ctrl+C to stop)");
        try
        {
            while (true)
            {
                await _changed.WaitAsync(cancellationToken);
                // A save often arrives as several events, and editors save several files at once; a flush skips the wait.
                Task flush;
                lock (_gate)
                {
                    flush = _flushNow.Task;
                }

                await Task.WhenAny(Task.Delay(500, cancellationToken), flush);
                cancellationToken.ThrowIfCancellationRequested();
                while (_changed.CurrentCount > 0)
                {
                    await _changed.WaitAsync(cancellationToken);
                }

                List<string> saved;
                lock (_gate)
                {
                    saved = [.. _pendingPaths];
                }

                await IndexOnceAsync(saved, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            return LastExit;
        }
    }

    private async Task IndexOnceAsync(IReadOnlyList<string> saved, CancellationToken cancellationToken)
    {
        _indexing = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            lock (_gate)
            {
                foreach (var path in saved)
                {
                    _pendingPaths.Remove(path);
                }
            }

            LastExit = await index(cancellationToken);
            LastIndexedAt = DateTime.UtcNow;
            LastIndexSeconds = clock.Elapsed.TotalSeconds;
            if (Indexed is { } handler && saved.Count > 0)
            {
                await handler(saved, cancellationToken);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log($"re-index failed: {e.Message}; the next save tries again");
        }
        finally
        {
            _indexing = false;
            lock (_gate)
            {
                _indexed.TrySetResult();
                _indexed = NewSignal();
                _flushNow = NewSignal();
            }
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// The daemon for an externally extracted language: <see cref="ReindexWatch"/> keeps the graph fresh, and when a
/// language server is available, <see cref="Lsp.LspLiveService"/> answers the live tools through it over the
/// same pipe the C# daemon uses. One daemon per graph, signal handling and idle exit as for C#.
/// </summary>
internal static class ExternalDaemon
{
    public static async Task<int> RunAsync(
        string target, string dbPath, string language, IReadOnlySet<string> extensions,
        Func<CancellationToken, Task<int>> index, Lsp.LspServer? server, double? idleExitMinutes, Action<string> log)
    {
        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping.Cancel();
        };
        using var terminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            stopping.Cancel();
        });
        using var hangup = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGHUP, context =>
        {
            context.Cancel = true;
            stopping.Cancel();
        });

        if (await new LiveClient(dbPath).ClaimAsync(log, stopping.Token) is { } running)
        {
            log($"a daemon (pid {running.Pid}) already serves {dbPath}; not starting another");
            return 0;
        }

        var root = Directory.Exists(target) ? Path.GetFullPath(target) : Path.GetDirectoryName(Path.GetFullPath(target))!;
        var watch = new ReindexWatch(root, extensions, index, log)
        {
            IdleExit = idleExitMinutes is { } idle ? TimeSpan.FromMinutes(idle) : null,
        };
        await using var live = new Lsp.LspLiveService(target, dbPath, language, root, server, watch, log);
        watch.Indexed += live.RefreshAsync;
        var pipe = new LiveServer(LivePipe.NameFor(dbPath), (request, cancellationToken) =>
        {
            watch.Touch();
            return live.HandleAsync(request, cancellationToken);
        }, log);
        var serving = pipe.RunAsync(stopping.Token);
        log($"live: answering on pipe {pipe.PipeName}{(server is null ? " (graph only: no language server for " + language + ")" : $" through {server.Display}")}");

        var idleWatch = watch.IdleExit is null ? Task.CompletedTask : Task.Run(async () =>
        {
            using var tick = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(watch.IdleExit.Value.TotalSeconds / 10, 1, 30)));
            while (await tick.WaitForNextTickAsync(stopping.Token))
            {
                if (watch.IsIdle(DateTime.UtcNow))
                {
                    log($"idle for {watch.IdleExit.Value.TotalMinutes:0.#} min; stopping");
                    await stopping.CancelAsync();
                }
            }
        });

        // The language server starts beside the first index; it answers once both are ready.
        _ = live.StartAsync(stopping.Token);
        int exit;
        try
        {
            exit = await watch.RunAsync(stopping.Token);
        }
        finally
        {
            await stopping.CancelAsync();
            await serving;
            try
            {
                await idleWatch;
            }
            catch (OperationCanceledException)
            {
            }
        }

        log("stopped");
        return exit < 0 ? 0 : exit;
    }
}
