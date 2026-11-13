using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using CodeGraph.Core;
using CodeGraph.Core.Live;
using CodeGraph.Indexer;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

if (args is ["--version"])
{
    Console.WriteLine(IndexerVersion());
    return 0;
}

// `codegraph-index import [file|-] [--db PATH]` loads another language's extractor output (EXTRACTORS.md).
// `codegraph-index export [--db PATH] [--language L]` writes the graph back out as those records.
if (args is ["export", .. var exportArgs])
{
    return ExportCommand.Run(exportArgs, Console.Out, Log);
}

if (args is ["import", .. var importArgs])
{
    return await ImportCommand.RunAsync(importArgs, Console.In, Log);
}

// `codegraph-index init [solution|dir]` wires the repo to codegraph (.mcp.json, .gitignore) and then indexes.
var init = args is ["init", ..];
if (init)
{
    args = args[1..];
}

// Flags may come in any order around the one positional argument: `codegraph-index --watch App.sln` works.
string? targetArg = null;
string? dbArg = null;
string? logArg = null;
double? idleExitMinutes = null;
var forceFull = false;
var allTargets = false;
var watch = false;
var live = false;
var noIndex = false;
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
        case "--all-targets":
            allTargets = true;
            break;
        case "--watch":
            watch = true;
            break;
        case "--live" when init:
            live = true;
            break;
        case "--no-index" when init:
            noIndex = true;
            break;
        case "--db" when i + 1 < args.Length:
            dbArg = args[++i];
            break;
        case "--log" when i + 1 < args.Length:
            logArg = args[++i];
            break;
        case "--idle-exit" when i + 1 < args.Length && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) && minutes > 0:
            idleExitMinutes = minutes;
            i++;
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

if (init && !usage)
{
    targetArg = RepoSetup.FindTarget(targetArg ?? ".", out var problem);
    if (targetArg is null)
    {
        Console.Error.WriteLine($"codegraph init: {problem}");
        return 1;
    }
}

if (usage || targetArg is null)
{
    Console.Error.WriteLine("usage: codegraph-index <target> [--db <path>] [--full] [--watch] [--log <file>]");
    Console.Error.WriteLine("       target: a .sln, .slnx or .csproj (Roslyn); a tsconfig.json, angular.json, nx.json or package.json");
    Console.Error.WriteLine("               (TypeScript and Angular, needs Node.js); a pom.xml, build.gradle, pyproject.toml, go.mod or");
    Console.Error.WriteLine("               Cargo.toml (through that language's SCIP indexer); or a folder holding one of them");
    Console.Error.WriteLine("       codegraph-index import <records.ndjson | index.scip | -> [--db <path>]");
    Console.Error.WriteLine("       codegraph-index export [--db <path>] [--language <name>] [--out <file>] [--dead-code]");
    Console.Error.WriteLine("       codegraph-index export obsidian --out <vault>/<folder> [--db <path>] [--language <name>] [--link-root <path>]");
    Console.Error.WriteLine("       codegraph-index init [solution|dir] [--live] [--no-index] [index options]");
    Console.Error.WriteLine("       default db: <git worktree root>/.codegraph/graph.db (solution dir outside a repo)");
    Console.Error.WriteLine("       re-walks only files whose hash changed, plus files that depend on them;");
    Console.Error.WriteLine("       --full rebuilds everything");
    Console.Error.WriteLine("       --all-targets compiles every target framework of a multi-target project, not only the first");
    Console.Error.WriteLine("       --watch keeps running: the solution is loaded once, then every save re-indexes");
    Console.Error.WriteLine("               incrementally without the MSBuild load, and the loaded solution answers");
    Console.Error.WriteLine("               the MCP server's live questions over a local pipe (Ctrl+C to stop)");
    Console.Error.WriteLine("       --log <file>  append the log there instead of stderr (for a daemon started in the background)");
    Console.Error.WriteLine("       --idle-exit <minutes>  with --watch, stop after that long with no save and no live request");
    Console.Error.WriteLine("       init  sets up the repo: finds the solution (default: the current folder), adds the codegraph");
    Console.Error.WriteLine("             server to <worktree root>/.mcp.json, adds .codegraph/ to .gitignore, then indexes");
    Console.Error.WriteLine("             --live      the server starts the live daemon itself on its first call");
    Console.Error.WriteLine("             --no-index  write the config only");
    return 1;
}

if (logArg is not null)
{
    // A daemon spawned by the MCP server has no terminal; its log goes to a file and nothing touches the inherited pipes.
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logArg))!);
    Console.SetError(new StreamWriter(new FileStream(logArg, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true });
}

var target = Path.GetFullPath(targetArg);
target = TypeScriptExtractor.IsTarget(target) ? TypeScriptExtractor.WorkspaceFile(target) : ScipIndexers.For(target)?.Marker ?? target;
if (!File.Exists(target))
{
    Console.Error.WriteLine($"not found: {target}");
    return 1;
}

var dbPath = dbArg is not null
    ? Path.GetFullPath(dbArg)
    : Worktree.DefaultDbPath(Path.GetDirectoryName(target)!);

if (init)
{
    if (!RepoSetup.Configure(target, dbArg is null ? null : dbPath, live, Console.Out))
    {
        return 1;
    }

    if (noIndex)
    {
        return 0;
    }
}

if (TypeScriptExtractor.IsTarget(target))
{
    // TypeScript, JavaScript and Angular: the Node extractor writes the records, the importer writes the graph.
    using var stopping = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stopping.Cancel();
    };
    var code = watch
        ? await TypeScriptExtractor.WatchAsync(target, dbPath, idleExitMinutes, Log)
        : await TypeScriptExtractor.IndexAsync(target, dbPath, Log, stopping.Token);
    if (init && code == 0)
    {
        Console.WriteLine(ReadyMessage(target));
    }

    return code;
}

if (ScipIndexers.For(target) is { } scip)
{
    // Java, Kotlin, Scala, Python, Go, Rust: the language's SCIP indexer writes an index, the importer reads it.
    using var stopping = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stopping.Cancel();
    };
    var code = watch
        ? await ScipIndexers.WatchAsync(scip.Indexer, scip.Marker, dbPath, idleExitMinutes, Log)
        : await ScipIndexers.IndexAsync(scip.Indexer, scip.Marker, dbPath, Log, stopping.Token);
    if (init && code == 0)
    {
        Console.WriteLine(ReadyMessage(target));
    }

    return code;
}

// MSBuildLocator must run before the JIT touches any Microsoft.CodeAnalysis.MSBuild type,
// which is why the actual work lives in a separate, non-inlined method.
MSBuildLocator.RegisterDefaults();
IndexOptions.AllTargets = allTargets;
if (watch)
{
    return await WatchAsync(target, dbPath, forceFull, idleExitMinutes);
}

var exit = await RunAsync(target, dbPath, forceFull);
if (init && exit == 0)
{
    // A front end beside the solution (an Angular ClientApp, a web/ folder) goes into the same graph.
    foreach (var workspace in TypeScriptExtractor.WorkspacesUnder(RepoSetup.RepoRoot(target)))
    {
        Log($"typescript workspace: {workspace}");
        if (await TypeScriptExtractor.IndexAsync(workspace, dbPath, Log, CancellationToken.None) != 0)
        {
            Log($"typescript: {workspace} was not indexed; the C# graph is complete");
        }
    }

    Console.WriteLine(ReadyMessage(target));
}

return exit;

static string ReadyMessage(string target) => $"""

    Ready. Start Claude Code in {RepoSetup.RepoRoot(target)} (or run /mcp in an open session) and approve the codegraph server.
    Keep the graph fresh while you work:  codegraph-index "{target}" --watch
    """;

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
/// solution in memory, and answers the MCP server's live questions over a pipe. The state machine is
/// <see cref="WatchLoop"/>; this wires it to the file watcher, MSBuild, the console and the pipe.
/// </summary>
[MethodImpl(MethodImplOptions.NoInlining)]
static async Task<int> WatchAsync(string target, string dbPath, bool forceFull, double? idleExitMinutes)
{
    using var stopping = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stopping.Cancel();
    };

    // `kill`, a service manager or a closing terminal stop it as cleanly as Ctrl+C: the pipe is taken down,
    // rather than left as a socket file for the next daemon to clear.
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

    // One daemon per graph: a second would fight the first for the write lock and the pipe name.
    if (await new LiveClient(dbPath).ClaimAsync(Log, stopping.Token) is { } running)
    {
        Log($"a daemon (pid {running.Pid}) already serves {dbPath}; not starting another");
        return 0;
    }

    var root = Worktree.RootOf(Path.GetDirectoryName(target)!) ?? Path.GetDirectoryName(target)!;
    MSBuildWorkspace? workspace = null;

    async Task<Solution> Load(CancellationToken cancellationToken)
    {
        workspace?.Dispose();
        workspace = await OpenAsync(target, cancellationToken);
        return workspace.CurrentSolution;
    }

    async Task Index(Solution solution, bool full, CancellationToken cancellationToken)
    {
        try
        {
            await IndexAsync(solution, target, dbPath, full, cancellationToken);
        }
        finally
        {
            ConsoleStatus.Clear();
        }
    }

    var loop = new WatchLoop(Load, Index, ReadOrNull, Log, target, dbPath)
    {
        IdleExit = idleExitMinutes is { } idle ? TimeSpan.FromMinutes(idle) : null,
    };

    using var watcher = new FileSystemWatcher(root)
    {
        IncludeSubdirectories = true,
        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
        InternalBufferSize = 64 * 1024,
    };
    watcher.Changed += (_, e) => loop.Enqueue(e.FullPath);
    watcher.Created += (_, e) => loop.Enqueue(e.FullPath);
    watcher.Deleted += (_, e) => loop.Enqueue(e.FullPath);
    watcher.Renamed += (_, e) =>
    {
        loop.Enqueue(e.OldFullPath);
        loop.Enqueue(e.FullPath);
    };
    watcher.Error += (_, e) =>
    {
        Log($"watcher: {e.GetException().Message}; the next change reloads the solution");
        loop.LostEvents();
    };

    // The pipe answers from the first moment, so live_status can say "loading" during the MSBuild load.
    var service = new LiveService(() => loop.Current, loop.Status, loop.FlushAsync);
    var live = new LiveServer(LivePipe.NameFor(dbPath), (request, cancellationToken) =>
    {
        loop.Touch();
        return service.HandleAsync(request, cancellationToken);
    }, Log);
    var serving = live.RunAsync(stopping.Token);
    Log($"live: answering on pipe {live.PipeName}");

    var idleWatch = loop.IdleExit is null ? Task.CompletedTask : Task.Run(async () =>
    {
        using var tick = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(loop.IdleExit.Value.TotalSeconds / 10, 1, 30)));
        while (await tick.WaitForNextTickAsync(stopping.Token))
        {
            if (loop.IsIdle(DateTime.UtcNow))
            {
                Log($"idle for {loop.IdleExit.Value.TotalMinutes:0.#} min; stopping");
                await stopping.CancelAsync();
            }
        }
    });

    // Watch from before the load: a save during it lands in the first incremental pass instead of being lost.
    watcher.EnableRaisingEvents = true;
    _ = loop.Ready.ContinueWith(_ => Log($"watching {root} (Ctrl+C to stop)"), TaskContinuationOptions.OnlyOnRanToCompletion);
    try
    {
        await loop.RunAsync(forceFull, stopping.Token);
    }
    catch (OperationCanceledException)
    {
        Log("stopped");
    }
    finally
    {
        ConsoleStatus.Clear();
        await stopping.CancelAsync();
        await serving;
        try
        {
            await idleWatch;
        }
        catch (OperationCanceledException)
        {
        }

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

static string IndexerVersion() => typeof(RoslynIndexer).Assembly
    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

/// <summary>One index run over a loaded solution: incremental against what the db holds, or full.</summary>
static async Task IndexAsync(Solution solution, string target, string dbPath, bool forceFull, CancellationToken cancellationToken)
{
    var stopwatch = Stopwatch.StartNew();
    var root = Worktree.RootOf(Path.GetDirectoryName(target)!);

    var embeddingSettings = EmbeddingSettings.FromEnvironment();
    var previous = forceFull ? null : PreviousIndex.Load(dbPath, target, embeddingSettings?.Model);
    if (!forceFull && previous is null && File.Exists(dbPath))
    {
        Log("existing graph is from another solution, schema version or embedding model; rebuilding");
    }

    var full = previous is null;
    var stamps = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
    var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var walked = -1;

    ConsoleStatus.Phase("indexing", "stamping files");
    var indexer = new RoslynIndexer(Log, progress => ConsoleStatus.Detail(progress.ToString())) { AllTargets = IndexOptions.AllTargets };
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

    foreach (var line in indexer.CompilerSummary())
    {
        Log(line);
    }

    // What this graph was read with, for index_status: a repo ahead of this Roslyn shows here, not as missing edges.
    var meta = new Dictionary<string, string>
    {
        [MetaKeys.SchemaVersion] = GraphStore.SchemaVersion,
        [MetaKeys.Solution] = target,
        [MetaKeys.IndexedAt] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        [MetaKeys.Mode] = full ? "full" : "incremental",
        [MetaKeys.Language(Languages.CSharp, LanguageMetaKeys.Tool)] = $"codegraph-index {IndexerVersion()}",
        [MetaKeys.Language(Languages.CSharp, LanguageMetaKeys.Compiler)] = RoslynIndexer.CompilerVersion,
        [MetaKeys.Language(Languages.CSharp, LanguageMetaKeys.NewestLanguageVersion)] = RoslynIndexer.NewestLanguageVersion,
        [MetaKeys.Language(Languages.CSharp, LanguageMetaKeys.LanguageVersions)] = string.Join(", ", indexer.LanguageVersions),
        [MetaKeys.Language(Languages.CSharp, LanguageMetaKeys.CompileErrors)] = indexer.CompileErrors.ToString(CultureInfo.InvariantCulture),
        [MetaKeys.Language(Languages.CSharp, LanguageMetaKeys.LanguageVersionErrors)] = indexer.LanguageVersionErrors.ToString(CultureInfo.InvariantCulture),
        [MetaKeys.Language(Languages.CSharp, LanguageMetaKeys.FirstLanguageVersionError)] = indexer.FirstLanguageVersionError ?? "",
        [MetaKeys.Language(Languages.CSharp, LanguageMetaKeys.FirstCompileError)] = indexer.FirstCompileError ?? "",
        [MetaKeys.Language(Languages.CSharp, LanguageMetaKeys.Notes)] = string.Join("; ", indexer.Notes),
        [MetaKeys.Language(Languages.CSharp, LanguageMetaKeys.Unmapped)] = indexer.UnmappedSummary(),
        [MetaKeys.Language(Languages.CSharp, LanguageMetaKeys.IndexedAt)] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
    };

    IReadOnlyList<NodeEmbedding>? embeddings = null;
    if (embeddingSettings is not null)
    {
        ConsoleStatus.Phase("embedding", $"{indexer.EmbeddingTexts.Count} declarations");
        embeddings = await EmbeddingStep.RunAsync(new EmbeddingClient(embeddingSettings), indexer.EmbeddingTexts, dbPath, Log, cancellationToken);
        if (embeddings is not null)
        {
            meta[MetaKeys.EmbeddingsModel] = embeddingSettings.Model;
        }
    }
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
        // The first index of a repo has no .codegraph/ folder yet, and File.Delete throws for a missing folder.
        Directory.CreateDirectory(Path.GetDirectoryName(writePath)!);
        File.Delete(writePath);
        File.Delete(writePath + ".wal");
    }

    using (var store = GraphStore.Open(writePath, readOnly: false, TimeSpan.FromSeconds(60)))
    {
        store.Replace(full, removed, indexer.Nodes, indexer.Edges, indexer.Origins, stamps.Values, meta, indexer.SearchDocuments, embeddings);
        if (full && store.CopyOtherLanguages(dbPath, Languages.CSharp) is > 0 and var kept)
        {
            Log($"kept {kept} node(s) of other languages from the previous graph");
        }

        counts = store.Count();
    }

    if (full)
    {
        GraphFile.SwapIn(writePath, dbPath);
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
static bool SameStat(FileStamp stamp, string path)
{
    var info = new FileInfo(path);
    return info.Exists && info.Length == stamp.Size && info.LastWriteTimeUtc.Ticks == stamp.MtimeTicks;
}

/// <summary>Command-line choices every index pass of this process shares.</summary>
internal static class IndexOptions
{
    public static bool AllTargets { get; set; }
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
    public static PreviousIndex? Load(string dbPath, string solution, string? embeddingModel = null)
    {
        if (!File.Exists(dbPath))
        {
            return null;
        }

        using var store = GraphStore.Open(dbPath, readOnly: true, TimeSpan.FromSeconds(30));
        var meta = store.ReadMeta();
        if (meta.GetValueOrDefault(MetaKeys.SchemaVersion) != GraphStore.SchemaVersion
            || !string.Equals(meta.GetValueOrDefault(MetaKeys.Solution), solution, StringComparison.OrdinalIgnoreCase)
            // Vectors from two models are not comparable; turning embeddings on, off or to another model re-embeds all.
            || meta.GetValueOrDefault(MetaKeys.EmbeddingsModel) != embeddingModel)
        {
            return null;
        }

        // Only C#'s own rows: files another extractor indexed into the same graph are neither changed nor deleted here.
        return new PreviousIndex(dbPath, store.IndexedFiles(Languages.CSharp), store.ProjectFilePaths(Languages.CSharp));
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
