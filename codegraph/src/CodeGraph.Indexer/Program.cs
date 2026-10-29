using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using CodeGraph.Core;
using CodeGraph.Indexer;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.Error.WriteLine("usage: codegraph-index <solution.sln|.slnx|project.csproj> [--db <path>] [--full] [--watch]");
    Console.Error.WriteLine("       default db: <git worktree root>/.codegraph/graph.db (solution dir outside a repo)");
    Console.Error.WriteLine("       re-walks only files whose hash changed, plus files that depend on them;");
    Console.Error.WriteLine("       --full rebuilds everything");
    Console.Error.WriteLine("       --watch keeps running: the solution is loaded once, then every save re-indexes");
    Console.Error.WriteLine("               incrementally without the MSBuild load (Ctrl+C to stop)");
    return 1;
}

var target = Path.GetFullPath(args[0]);
if (!File.Exists(target))
{
    Console.Error.WriteLine($"not found: {target}");
    return 1;
}

var dbIndex = Array.IndexOf(args, "--db");
var dbPath = dbIndex >= 0 && dbIndex + 1 < args.Length
    ? Path.GetFullPath(args[dbIndex + 1])
    : Worktree.DefaultDbPath(Path.GetDirectoryName(target)!);
var forceFull = args.Contains("--full");
var watch = args.Contains("--watch");

// MSBuildLocator must run before the JIT touches any Microsoft.CodeAnalysis.MSBuild type,
// which is why the actual work lives in a separate, non-inlined method.
MSBuildLocator.RegisterDefaults();
return watch ? await WatchAsync(target, dbPath, forceFull) : await RunAsync(target, dbPath, forceFull);

[MethodImpl(MethodImplOptions.NoInlining)]
static async Task<int> RunAsync(string target, string dbPath, bool forceFull)
{
    using var workspace = await RoslynIndexer.OpenAsync(target, Log, CancellationToken.None);
    await IndexAsync(workspace.CurrentSolution, target, dbPath, forceFull, CancellationToken.None);
    return 0;
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
    watcher.Error += (_, e) => Log($"watcher: {e.GetException().Message}; the next change triggers a full reload");

    MSBuildWorkspace? workspace = null;
    Solution? solution = null;
    try
    {
        workspace = await RoslynIndexer.OpenAsync(target, Log, stopping.Token);
        solution = workspace.CurrentSolution;
        await IndexAsync(solution, target, dbPath, forceFull, stopping.Token);
        watcher.EnableRaisingEvents = true;
        Log($"watching {root} (Ctrl+C to stop)");

        while (!stopping.IsCancellationRequested)
        {
            await wake.WaitAsync(stopping.Token);

            // Debounce: editors and git touch files in bursts; wait for a quiet half second before walking.
            // Anything that lands during the walk wakes the loop again.
            await Task.Delay(500, stopping.Token);
            List<string> batch;
            lock (pendingLock)
            {
                batch = [.. pending];
                pending.Clear();
            }

            while (wake.CurrentCount > 0)
            {
                wake.Wait(0);
            }

            if (batch.Count == 0)
            {
                continue;
            }

            try
            {
                if (batch.Any(WorkspaceSync.IsProjectFile))
                {
                    Log("project file changed; reloading the solution");
                    workspace.Dispose();
                    workspace = await RoslynIndexer.OpenAsync(target, Log, stopping.Token);
                    solution = workspace.CurrentSolution;
                }
                else
                {
                    solution = WorkspaceSync.Apply(solution, batch, ReadOrNull);
                }

                await IndexAsync(solution, target, dbPath, forceFull: false, stopping.Token);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One bad batch (a file locked mid-save, a db lock held too long) must not end the watch.
                Log($"re-index failed: {exception.Message}; will retry on the next change");
                foreach (var path in batch)
                {
                    Enqueue(path);
                }
            }
        }
    }
    catch (OperationCanceledException)
    {
        Log("stopped");
    }
    finally
    {
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

static void Log(string message) => Console.Error.WriteLine(message);

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

    var indexer = new RoslynIndexer(Log);
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
    // (long) solution load would block every MCP server reading this graph.
    using var store = GraphStore.Open(dbPath, readOnly: false, TimeSpan.FromSeconds(60));
    store.Replace(full, removed, indexer.Nodes, indexer.Edges, indexer.Origins, stamps.Values, meta, indexer.SearchDocuments);

    var (nodes, edges) = store.Count();
    var scope = walked < 0 ? "" : $", {walked} file(s) walked";
    Log($"indexed {nodes} nodes, {edges} edges ({meta[MetaKeys.Mode]}{scope}) in {stopwatch.Elapsed.TotalSeconds:F1}s → {dbPath}");
}

static bool SameStat(FileStamp stamp, string path)
{
    var info = new FileInfo(path);
    return info.Exists && info.Length == stamp.Size && info.LastWriteTimeUtc.Ticks == stamp.MtimeTicks;
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
