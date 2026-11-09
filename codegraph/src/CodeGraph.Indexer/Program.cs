using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using CodeGraph.Core;
using CodeGraph.Core.Live;
using CodeGraph.Indexer;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

// Flags may come in any order around the one positional argument: `codegraph-index --watch App.sln` works.
string? targetArg = null;
string? dbArg = null;
string? logArg = null;
var forceFull = false;
var watch = false;
var usage = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-h" or "--help":
            usage = true;
            break;
        case "--full":
            forceFull = true;
            break;
        case "--watch":
            watch = true;
            break;
        case "--db" when i + 1 < args.Length:
            dbArg = args[++i];
            break;
        case "--log" when i + 1 < args.Length:
            logArg = args[++i];
            break;
        case var flag when flag.StartsWith('-'):
            Console.Error.WriteLine($"unknown option: {flag}");
            usage = true;
            break;
        case var positional when targetArg is null:
            targetArg = positional;
            break;
        case var extra:
            Console.Error.WriteLine($"unexpected argument: {extra}");
            usage = true;
            break;
    }
}

if (usage || targetArg is null)
{
    Console.Error.WriteLine("usage: codegraph-index <solution.sln|.slnx|project.csproj> [--db <path>] [--full] [--watch] [--log <file>]");
    Console.Error.WriteLine("       default db: <git worktree root>/.codegraph/graph.db (solution dir outside a repo)");
    Console.Error.WriteLine("       re-walks only files whose hash changed, plus files that depend on them;");
    Console.Error.WriteLine("       --full rebuilds everything");
    Console.Error.WriteLine("       --watch keeps running: the solution is loaded once, then every save re-indexes");
    Console.Error.WriteLine("               incrementally without the MSBuild load, and the loaded solution answers");
    Console.Error.WriteLine("               the MCP server's live questions over a local pipe (Ctrl+C to stop)");
    Console.Error.WriteLine("       --log <file>  append the log there instead of stderr (for a daemon started in the background)");
    return 1;
}

if (logArg is not null)
{
    // A daemon spawned by the MCP server has no terminal; its log goes to a file and nothing touches the inherited pipes.
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logArg))!);
    Console.SetError(new StreamWriter(new FileStream(logArg, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true });
}

var target = Path.GetFullPath(targetArg);
if (!File.Exists(target))
{
    Console.Error.WriteLine($"not found: {target}");
    return 1;
}

var dbPath = dbArg is not null
    ? Path.GetFullPath(dbArg)
    : Worktree.DefaultDbPath(Path.GetDirectoryName(target)!);

// MSBuildLocator must run before the JIT touches any Microsoft.CodeAnalysis.MSBuild type,
// which is why the actual work lives in a separate, non-inlined method.
MSBuildLocator.RegisterDefaults();
return watch ? await WatchAsync(target, dbPath, forceFull) : await RunAsync(target, dbPath, forceFull);

[MethodImpl(MethodImplOptions.NoInlining)]
static async Task<int> RunAsync(string target, string dbPath, bool forceFull)
{
    try
    {
        using var workspace = await OpenAsync(target, CancellationToken.None);
        await IndexAsync(workspace.CurrentSolution, target, dbPath, forceFull, CancellationToken.None);
        return 0;
    }
    finally
    {
        ConsoleStatus.Clear();
    }
}

/// <summary>The MSBuild load, shown as its own phase: it used to be the one step that printed nothing.</summary>
static async Task<MSBuildWorkspace> OpenAsync(string target, CancellationToken cancellationToken)
{
    ConsoleStatus.Phase("loading solution", Path.GetFileName(target));
    return await RoslynIndexer.OpenAsync(target, Log, cancellationToken);
}

/// <summary>
/// Pays the MSBuild load once, then re-indexes on every change to the working tree, applying edits to the loaded
/// solution in memory. A change to a project or build file is the one thing that forces a reload.
/// </summary>
[MethodImpl(MethodImplOptions.NoInlining)]
static async Task<int> WatchAsync(string target, string dbPath, bool forceFull)
{
    using var stopping = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stopping.Cancel();
    };

    var root = Worktree.RootOf(Path.GetDirectoryName(target)!) ?? Path.GetDirectoryName(target)!;
    var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var pendingLock = new Lock();
    var wake = new SemaphoreSlim(0);
    // Set when the watcher's buffer overflowed: some saves were never reported, so the in-memory solution
    // can no longer be trusted and the next batch reloads it from disk.
    var lostEvents = false;
    // Flush requests from the MCP server: skip the debounce and complete these once pending is empty again.
    var flushRequested = false;
    var flushWaiters = new List<TaskCompletionSource<LiveStatus>>();
    var state = new WatchState(target, dbPath);
    Solution? current = null;

    LiveStatus Status()
    {
        lock (pendingLock)
        {
            return state.Snapshot(current, pending.Count);
        }
    }

    Task<LiveStatus> Flush(CancellationToken cancellationToken)
    {
        TaskCompletionSource<LiveStatus> waiter;
        lock (pendingLock)
        {
            if (pending.Count == 0 && !state.Indexing)
            {
                return Task.FromResult(state.Snapshot(current, 0));
            }

            waiter = new TaskCompletionSource<LiveStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            flushWaiters.Add(waiter);
            flushRequested = true;
        }

        wake.Release();
        cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
        return waiter.Task;
    }

    void PassCompleted()
    {
        List<TaskCompletionSource<LiveStatus>> done;
        lock (pendingLock)
        {
            if (pending.Count > 0)
            {
                return; // more arrived during the walk; the loop goes round again and they wait for that
            }

            done = [.. flushWaiters];
            flushWaiters.Clear();
        }

        var status = Status();
        foreach (var waiter in done)
        {
            waiter.TrySetResult(status);
        }
    }

    using var watcher = new FileSystemWatcher(root)
    {
        IncludeSubdirectories = true,
        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
        InternalBufferSize = 64 * 1024,
    };

    void Enqueue(string path)
    {
        if (!WorkspaceSync.IsSourceFile(path) && !WorkspaceSync.IsProjectFile(path))
        {
            return;
        }

        lock (pendingLock)
        {
            pending.Add(Path.GetFullPath(path));
        }

        wake.Release();
    }

    watcher.Changed += (_, e) => Enqueue(e.FullPath);
    watcher.Created += (_, e) => Enqueue(e.FullPath);
    watcher.Deleted += (_, e) => Enqueue(e.FullPath);
    watcher.Renamed += (_, e) =>
    {
        Enqueue(e.OldFullPath);
        Enqueue(e.FullPath);
    };
    watcher.Error += (_, e) =>
    {
        Log($"watcher: {e.GetException().Message}; the next change reloads the solution");
        lock (pendingLock)
        {
            lostEvents = true;
        }

        wake.Release();
    };

    MSBuildWorkspace? workspace = null;
    Solution? solution = null;
    try
    {
        workspace = await OpenAsync(target, stopping.Token);
        solution = workspace.CurrentSolution;
        state.Indexing = true;
        await IndexAsync(solution, target, dbPath, forceFull, stopping.Token);
        state.Indexed(stopwatchSeconds: null);
        current = solution;
        watcher.EnableRaisingEvents = true;
        ConsoleStatus.Clear();
        Log($"watching {root} (Ctrl+C to stop)");

        // The loaded solution now answers the MCP server's live questions until this process ends.
        var live = new LiveServer(LivePipe.NameFor(dbPath), new LiveService(() => current, Status, Flush).HandleAsync, Log);
        _ = live.RunAsync(stopping.Token);
        Log($"live: answering on pipe {live.PipeName}");

        while (!stopping.IsCancellationRequested)
        {
            await wake.WaitAsync(stopping.Token);

            // Debounce: editors and git touch files in bursts; wait for a quiet half second before walking.
            // Anything that lands during the walk wakes the loop again. A flush skips the wait.
            bool urgent;
            lock (pendingLock)
            {
                urgent = flushRequested;
                flushRequested = false;
            }

            if (!urgent)
            {
                await Task.Delay(500, stopping.Token);
            }

            List<string> batch;
            bool reload;
            lock (pendingLock)
            {
                batch = [.. pending];
                pending.Clear();
                reload = lostEvents;
                lostEvents = false;
                state.Indexing = batch.Count > 0 || reload;
            }

            while (wake.CurrentCount > 0)
            {
                wake.Wait(0);
            }

            if (batch.Count == 0 && !reload)
            {
                PassCompleted();
                continue;
            }

            var clock = Stopwatch.StartNew();
            try
            {
                if (reload || batch.Any(WorkspaceSync.IsProjectFile))
                {
                    Log(reload ? "watcher events were lost; reloading the solution" : "project file changed; reloading the solution");
                    workspace.Dispose();
                    workspace = await OpenAsync(target, stopping.Token);
                    solution = workspace.CurrentSolution;
                }
                else
                {
                    solution = WorkspaceSync.Apply(solution, batch, ReadOrNull);
                }

                await IndexAsync(solution, target, dbPath, forceFull: false, stopping.Token);
                current = solution;
                state.Indexed(clock.Elapsed.TotalSeconds);
                ConsoleStatus.Clear();
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One bad batch (a file locked mid-save, a db lock held too long) must not end the watch.
                ConsoleStatus.Clear();
                Log($"re-index failed: {exception.Message}; will retry on the next change");
                foreach (var path in batch)
                {
                    Enqueue(path);
                }
            }
            finally
            {
                lock (pendingLock)
                {
                    state.Indexing = false;
                }

                PassCompleted();
            }
        }
    }
    catch (OperationCanceledException)
    {
        Log("stopped");
    }
    finally
    {
        ConsoleStatus.Clear();
        workspace?.Dispose();
    }

    return 0;
}

static string? ReadOrNull(string path)
{
    if (!File.Exists(path))
    {
        return null;
    }

    // The editor may still hold the file for a moment after the change event.
    for (var attempt = 0; ; attempt++)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException) when (attempt < 5)
        {
            Thread.Sleep(100);
        }
    }
}

static void Log(string message) => ConsoleStatus.Log(message);

/// <summary>One index run over a loaded solution: incremental against what the db holds, or full.</summary>
static async Task IndexAsync(Solution solution, string target, string dbPath, bool forceFull, CancellationToken cancellationToken)
{
    var stopwatch = Stopwatch.StartNew();
    var root = Worktree.RootOf(Path.GetDirectoryName(target)!);

    var previous = forceFull ? null : PreviousIndex.Load(dbPath, target);
    if (!forceFull && previous is null && File.Exists(dbPath))
    {
        Log("existing graph is from another solution or schema version; rebuilding");
    }

    var full = previous is null;
    var stamps = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
    var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var walked = -1;

    ConsoleStatus.Phase("indexing", "stamping files");
    var indexer = new RoslynIndexer(Log, progress => ConsoleStatus.Detail(progress.ToString()));
    await indexer.IndexAsync(solution, documents =>
    {
        foreach (var path in documents)
        {
            // Unchanged size and mtime means unchanged content; only hash what might differ.
            stamps[path] = previous?.Files.GetValueOrDefault(path) is { } old && SameStat(old, path) ? old : FileStamp.Of(path);
        }

        if (previous is null)
        {
            return null;
        }

        var changed = stamps.Values
            .Where(s => previous.Files.GetValueOrDefault(s.Path)?.Hash != s.Hash)
            .Select(s => s.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deleted = previous.Files.Keys.Where(p => !stamps.ContainsKey(p)).ToList();

        var dependents = previous.DependentFiles([.. changed, .. deleted]);
        var walk = changed.Concat(dependents.Where(stamps.ContainsKey)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        removed.UnionWith(walk);
        removed.UnionWith(deleted);
        removed.UnionWith(previous.ProjectPaths); // project nodes and edges are always re-emitted

        walked = walk.Count;
        Log($"incremental: {changed.Count} changed, {deleted.Count} deleted, {walk.Count - changed.Count} dependent → walking {walk.Count} of {stamps.Count} files");
        return walk;
    }, cancellationToken);

    var meta = new Dictionary<string, string>
    {
        [MetaKeys.SchemaVersion] = GraphStore.SchemaVersion,
        [MetaKeys.Solution] = target,
        [MetaKeys.IndexedAt] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        [MetaKeys.Mode] = full ? "full" : "incremental",
    };
    if (root is not null)
    {
        meta[MetaKeys.WorktreeRoot] = root;
        if (Worktree.Head(root) is { } head)
        {
            meta[MetaKeys.Commit] = head;
        }
    }

    // Open for writing only now: DuckDB allows one writer or many readers, so holding the lock during the
    // (long) solution load would block every MCP server reading this graph. A full rebuild goes to a fresh
    // file that is swapped in afterwards: deleting and re-inserting a whole graph in place leaves the file
    // twice the size of the data, and a reader never sees an empty graph mid-write.
    ConsoleStatus.Phase("writing graph", $"{indexer.Nodes.Count} nodes, {indexer.Edges.Count} edges");
    var writePath = full ? dbPath + ".tmp" : dbPath;
    (int Nodes, int Edges) counts;
    if (full)
    {
        File.Delete(writePath);
    }

    using (var store = GraphStore.Open(writePath, readOnly: false, TimeSpan.FromSeconds(60)))
    {
        store.Replace(full, removed, indexer.Nodes, indexer.Edges, indexer.Origins, stamps.Values, meta, indexer.SearchDocuments);
        counts = store.Count();
    }

    if (full)
    {
        SwapIn(writePath, dbPath);
    }

    var (nodes, edges) = counts;
    ConsoleStatus.Clear();
    var scope = walked < 0 ? "" : $", {walked} file(s) walked";
    Log($"indexed {nodes} nodes, {edges} edges ({meta[MetaKeys.Mode]}{scope}) in {stopwatch.Elapsed.TotalSeconds:F1}s → {dbPath}");
}

/// <summary>
/// Replaces the graph with a freshly written one. Readers open the file per call, so on Windows a short retry
/// covers the moment one holds it; a write-ahead log left by an older copy must not be replayed into the new file.
/// </summary>
static void SwapIn(string source, string destination)
{
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
    while (true)
    {
        try
        {
            File.Delete(destination + ".wal");
            File.Move(source, destination, overwrite: true);
            return;
        }
        catch (IOException) when (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);
        }
        catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);
        }
    }
}

static bool SameStat(FileStamp stamp, string path)
{
    var info = new FileInfo(path);
    return info.Exists && info.Length == stamp.Size && info.LastWriteTimeUtc.Ticks == stamp.MtimeTicks;
}

/// <summary>What the watch loop reports over the pipe: how long it has run, when it last indexed, whether it is busy.</summary>
internal sealed class WatchState(string solution, string dbPath)
{
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private DateTime? _lastIndexedAt;
    private double? _lastSeconds;

    public bool Indexing { get; set; }

    public void Indexed(double? stopwatchSeconds)
    {
        _lastIndexedAt = DateTime.UtcNow;
        _lastSeconds = stopwatchSeconds is { } s ? Math.Round(s, 2) : null;
    }

    public LiveStatus Snapshot(Solution? current, int pending) => new(
        Environment.ProcessId,
        solution,
        dbPath,
        current?.Projects.Count(p => p.Language == LanguageNames.CSharp) ?? 0,
        current?.Projects.Where(p => p.Language == LanguageNames.CSharp).Sum(p => p.DocumentIds.Count) ?? 0,
        _startedAt,
        _lastIndexedAt,
        _lastSeconds,
        pending,
        Indexing);
}

/// <summary>What the last run left behind, read through a short-lived read-only connection.</summary>
internal sealed class PreviousIndex
{
    private readonly string _dbPath;

    private PreviousIndex(string dbPath, IReadOnlyDictionary<string, FileStamp> files, IReadOnlyList<string> projectPaths)
    {
        _dbPath = dbPath;
        Files = files;
        ProjectPaths = projectPaths;
    }

    public IReadOnlyDictionary<string, FileStamp> Files { get; }

    public IReadOnlyList<string> ProjectPaths { get; }

    /// <summary>Null when there is nothing usable to build on, which means a full rebuild.</summary>
    public static PreviousIndex? Load(string dbPath, string solution)
    {
        if (!File.Exists(dbPath))
        {
            return null;
        }

        using var store = GraphStore.Open(dbPath, readOnly: true, TimeSpan.FromSeconds(30));
        var meta = store.ReadMeta();
        if (meta.GetValueOrDefault(MetaKeys.SchemaVersion) != GraphStore.SchemaVersion
            || !string.Equals(meta.GetValueOrDefault(MetaKeys.Solution), solution, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new PreviousIndex(dbPath, store.IndexedFiles(), store.ProjectFilePaths());
    }

    public IReadOnlySet<string> DependentFiles(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0)
        {
            return new HashSet<string>();
        }

        using var store = GraphStore.Open(_dbPath, readOnly: true, TimeSpan.FromSeconds(30));
        return store.DependentFiles(paths);
    }
}
