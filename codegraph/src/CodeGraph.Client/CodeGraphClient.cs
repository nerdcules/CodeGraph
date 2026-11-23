using System.Globalization;
using System.Text.Json;
using CodeGraph.Client.Native;
using CodeGraph.Contracts;
using CodeGraph.Core;

namespace CodeGraph.Client;

/// <summary>
/// The one <see cref="ICodeGraph"/> implementation. Every answer comes from <see cref="GraphStore"/>, the same
/// queries the MCP server runs: the server wraps its per-call store (<see cref="CodeGraphClient(GraphStore, string, ClientOptions?)"/>),
/// while scripts and the CLI call <see cref="Open"/>, which reads the graph through a read-only native DuckDB
/// connection. The methods finish synchronously; they return tasks so the contract can grow a remote implementation.
/// </summary>
public sealed class CodeGraphClient : ICodeGraph
{
    /// <summary>Walks follow at most this many hops, as the MCP tools allow.</summary>
    public const int MaxDepth = 20;

    public const int MaxQueryRows = 10_000;

    private readonly GraphStore _store;
    private readonly string _dbPath;
    private readonly bool _ownsStore;
    private readonly ClientOptions _options;
    private readonly AnswerPaths _paths;
    private NativeConnection? _native;
    private IReadOnlyDictionary<string, string>? _projects;

    /// <summary>
    /// Wraps a store someone else opened and disposes. With default <paramref name="options"/> paths stay absolute
    /// and <see cref="Symbol.Project"/> is left null, as the MCP server needs them.
    /// </summary>
    public CodeGraphClient(GraphStore store, string dbPath, ClientOptions? options = null)
        : this(store, dbPath, options ?? ClientOptions.Server, ownsStore: false, native: null)
    {
    }

    private CodeGraphClient(GraphStore store, string dbPath, ClientOptions options, bool ownsStore, NativeConnection? native)
    {
        _store = store;
        _dbPath = Path.GetFullPath(dbPath);
        _options = options;
        _ownsStore = ownsStore;
        _native = native;
        _paths = new AnswerPaths(options.RelativePaths ? AnswerPaths.RootFor(_dbPath) : null);
    }

    /// <summary>
    /// Opens a graph read-only. With no <paramref name="path"/>: <c>CODEGRAPH_DB</c>, else <c>codegraph.client.json</c>
    /// (<c>{"db": "…"}</c>) in the working directory or a folder above it, else the <c>.codegraph/graph.db</c> of the
    /// worktree at <c>CODEGRAPH_ROOT</c> or the working directory. File paths come back relative to the repo root.
    /// </summary>
    public static CodeGraphClient Open(string? path = null, ClientOptions? options = null)
    {
        var dbPath = Locate(path);
        var native = NativeConnection.OpenReadOnly(dbPath, TimeSpan.FromSeconds(15));
        // GraphStore disposes the connection it reads through; the client keeps the same one for queries.
        var store = GraphStore.OpenWith(dbPath, () => native);
        return new CodeGraphClient(store, dbPath, options ?? ClientOptions.Scripts, ownsStore: true, native);
    }

    /// <summary>The graph <see cref="Open"/> would read for <paramref name="path"/>; it may not exist yet.</summary>
    public static string Locate(string? path = null) =>
        Locate(path, Environment.GetEnvironmentVariable("CODEGRAPH_DB"), Environment.GetEnvironmentVariable("CODEGRAPH_ROOT"), Directory.GetCurrentDirectory());

    /// <summary>Like <see cref="Locate(string?)"/>, from another folder than the current one (a hook's session cwd).</summary>
    public static string LocateFrom(string workingDirectory) =>
        Locate(null, Environment.GetEnvironmentVariable("CODEGRAPH_DB"), Environment.GetEnvironmentVariable("CODEGRAPH_ROOT"), workingDirectory);

    internal static string Locate(string? path, string? environmentDb, string? environmentRoot, string workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            return Path.GetFullPath(path);
        }

        if (!string.IsNullOrWhiteSpace(environmentDb))
        {
            return Path.GetFullPath(environmentDb);
        }

        var start = Worktree.UsableRoot(environmentRoot) ?? workingDirectory;
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            var config = Path.Combine(directory.FullName, ConfigFileName);
            if (File.Exists(config)
                && JsonSerializer.Deserialize(File.ReadAllText(config), ContractsJson.Default.ClientConfig) is { Db: { Length: > 0 } db })
            {
                return Path.GetFullPath(Path.Combine(directory.FullName, db));
            }
        }

        return Worktree.FindDb(start) ?? Worktree.DefaultDbPath(start);
    }

    public const string ConfigFileName = "codegraph.client.json";

    public string? Root => _paths.Root;

    public string DbPath => _dbPath;

    public Task<Symbol?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_store.GetNode(id) is { } node ? ToSymbol(node) : null);

    public Task<Page<Symbol>> FindAsync(SymbolQuery query, CancellationToken cancellationToken = default)
    {
        var limit = Page.Clamp(query.Limit);
        var offset = Math.Max(0, query.Offset);
        var kind = Parse<NodeKind>(query.Kind, "kind");
        var typeKind = Parse<TypeKind>(query.TypeKind, "typeKind");
        var accessibility = Parse<Accessibility>(query.Accessibility, "accessibility");
        var memberKind = Parse<MemberKind>(query.MemberKind, "memberKind");
        IReadOnlyList<GraphNode> Run(int take) => _store.FindSymbol(
            query.Text, kind, take, typeKind, accessibility, query.IsStatic, query.IsAbstract, query.IsVirtual,
            query.IsAsync, query.IsOverride, memberKind, query.Match, offset, query.Language?.ToLowerInvariant());

        // One more than asked says whether there are more; only then is the rest counted.
        var rows = Run(limit == int.MaxValue ? limit : limit + 1);
        var truncated = rows.Count > limit;
        var total = offset + (truncated ? Run(int.MaxValue).Count : rows.Count);
        var items = (truncated ? rows.Take(limit) : rows).Select(ToSymbol).ToArray();
        return Task.FromResult(new Page<Symbol>(items, total, truncated));
    }

    public Task<Outline> OutlineAsync(string symbol, int limit = Page.DefaultLimit, CancellationToken cancellationToken = default)
    {
        var node = Resolve(symbol);
        var children = _store.Children(node.Id).Select(ToSymbol).ToArray();
        return Task.FromResult(new Outline(ToSymbol(node), Page<Symbol>.Of(children, limit)));
    }

    public async Task<Snippet> SnippetAsync(string symbol, int context = 0, CancellationToken cancellationToken = default)
    {
        var node = Resolve(symbol);
        var target = ToSymbol(node);
        if (node.FilePath is null || !File.Exists(node.FilePath))
        {
            return new Snippet(target, target.File, node.StartLine, node.EndLine, null);
        }

        var lines = await File.ReadAllLinesAsync(node.FilePath, cancellationToken);
        var pad = Math.Clamp(context, 0, 50);
        var start = Math.Max(1, node.StartLine - pad);
        var end = Math.Min(lines.Length, node.EndLine + pad);
        var text = end >= start ? lines[(start - 1)..end] : [];
        return new Snippet(target, target.File, start, end, text);
    }

    public Task<Traversal> CallersAsync(string symbol, int depth = 1, int limit = Page.DefaultLimit, CancellationToken cancellationToken = default)
    {
        var node = Resolve(symbol);
        var hops = Math.Clamp(depth, 1, MaxDepth);
        return Task.FromResult(new Traversal(ToSymbol(node), hops, Hits(_store.Callers(node.Id, hops), limit)));
    }

    public Task<Traversal> CalleesAsync(string symbol, int depth = 1, bool includeExternal = false, int limit = Page.DefaultLimit, CancellationToken cancellationToken = default)
    {
        var node = Resolve(symbol);
        var hops = Math.Clamp(depth, 1, MaxDepth);
        return Task.FromResult(new Traversal(ToSymbol(node), hops, Hits(_store.Callees(node.Id, hops, includeExternal), limit)));
    }

    public Task<Impact> ImpactAsync(string symbol, int depth = 5, int limit = Page.DefaultLimit, CancellationToken cancellationToken = default)
    {
        var node = Resolve(symbol);
        var hops = Math.Clamp(depth, 1, MaxDepth);
        var hits = _store.Impact(node.Id, hops);
        return Task.FromResult(new Impact(
            ToSymbol(node),
            hops,
            Hits(hits.Where(h => !h.Node.IsTest).ToList(), limit),
            Hits(hits.Where(h => h.Node.IsTest).ToList(), limit)));
    }

    public Task<TestList> TestsAsync(string symbol, int limit = Page.DefaultLimit, CancellationToken cancellationToken = default)
    {
        var node = Resolve(symbol);
        var tests = _store.DirectTests(node.Id).Select(ToSymbol).ToArray();
        return Task.FromResult(new TestList(ToSymbol(node), Page<Symbol>.Of(tests, limit)));
    }

    public Task<QueryResult> QueryAsync(string sql, int limit = Page.DefaultLimit, int timeoutSeconds = 10, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("the query is empty", nameof(sql));
        }

        var cap = Math.Clamp(limit, 1, MaxQueryRows);
        var connection = Native();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.SelectOnly = true;

        var timedOut = false;
        using var timer = new Timer(_ =>
        {
            timedOut = true;
            connection.Interrupt();
        }, null, TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 60)), Timeout.InfiniteTimeSpan);
        using var cancel = cancellationToken.Register(connection.Interrupt);

        NativeReader reader;
        try
        {
            reader = (NativeReader)command.ExecuteReader();
        }
        catch (NativeDuckDbException) when (timedOut)
        {
            throw new TimeoutException($"the query ran longer than {Math.Clamp(timeoutSeconds, 1, 60)} s and was stopped");
        }
        catch (NativeDuckDbException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        using (reader)
        {
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
            var rows = new List<object?[]>();
            while (rows.Count < cap && reader.Read())
            {
                var row = new object?[columns.Length];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = reader.GetValue(i) switch
                    {
                        DBNull => null,
                        int number => (long)number,
                        var value => value,
                    };
                }

                rows.Add(row);
            }

            var total = checked((int)reader.RowCount);
            return Task.FromResult(new QueryResult(columns, rows, total, total > rows.Count));
        }
    }

    public async Task<Schema> SchemaAsync(CancellationToken cancellationToken = default)
    {
        var columns = await QueryAsync(
            """
            SELECT table_name, column_name, data_type FROM information_schema.columns
            WHERE table_schema = 'main' ORDER BY table_name, ordinal_position
            """, MaxQueryRows, cancellationToken: cancellationToken);
        var tables = columns.Items
            .GroupBy(row => (string)row[0]!)
            .Select(group => new Table(group.Key, group.Select(row => new Column((string)row[1]!, (string)row[2]!)).ToArray()))
            .ToArray();
        return new Schema(tables, Enum.GetNames<NodeKind>(), Enum.GetNames<EdgeKind>(), SchemaNotes);
    }

    /// <summary>What a query writer needs that the column list does not say.</summary>
    public const string SchemaNotes =
        "nodes.id is the node id (M:/T:/P:/F:/E: doc-comment ids for C#); edges(from_id, to_id, kind). "
        + "Calls: caller -> callee. Contains: Project -> File -> Type -> Member. Implements/Inherits: type -> base. "
        + "Overrides: override -> base member. TestedBy: member -> test method. DependsOn: project -> referenced project. "
        + "nodes.kind, type_kind, accessibility and member_kind hold enum names as text; is_test marks test methods.";

    public Task<Status> StatusAsync(CancellationToken cancellationToken = default)
    {
        var meta = _store.ReadMeta();
        var (nodes, edges) = _store.Count();
        return Task.FromResult(new Status(
            _dbPath,
            Root,
            nodes,
            edges,
            meta.GetValueOrDefault(MetaKeys.SchemaVersion),
            meta.GetValueOrDefault(MetaKeys.IndexedAt),
            meta.GetValueOrDefault(MetaKeys.Commit),
            _store.LanguageCounts().Select(l => l.Language).ToArray()));
    }

    /// <summary>
    /// The node a symbol argument names: the node with that id, else the one symbol whose exact name it is. The
    /// same rule as the MCP tools' id arguments, with up to 21 candidates when several match. Throws
    /// <see cref="SymbolNotFoundException"/> or <see cref="AmbiguousSymbolException"/>.
    /// </summary>
    public GraphNode Resolve(string idOrName)
    {
        if (_store.GetNode(idOrName) is { } node)
        {
            return node;
        }

        var matches = _store.FindSymbol(idOrName, null, 21, null, null, null, null, null, null, null, null, "Exact", 0, null);
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new SymbolNotFoundException(idOrName),
            _ => throw new AmbiguousSymbolException(idOrName, matches.Select(ToSymbol).ToArray()),
        };
    }

    private Page<Hit> Hits(IReadOnlyList<GraphHit> hits, int limit) =>
        Page<Hit>.Of(hits.Select(h => new Hit(ToSymbol(h.Node), h.Depth)).ToArray(), limit);

    private Symbol ToSymbol(GraphNode node) => new(
        node.Id,
        node.Kind.ToString(),
        node.Name,
        node.FullName,
        node.Signature,
        _paths.Relative(node.FilePath),
        node.StartLine,
        node.EndLine,
        node.IsTest,
        node.TypeKind == TypeKind.None ? null : node.TypeKind.ToString(),
        node.Accessibility == Accessibility.None ? null : node.Accessibility.ToString(),
        node.IsStatic,
        node.IsAbstract,
        node.IsVirtual,
        node.IsOverride,
        node.IsAsync,
        node.Language,
        _options.Projects && node.FilePath is { } path ? ProjectOf(path) : null);

    private string? ProjectOf(string path) => (_projects ??= _store.FileProjects()).GetValueOrDefault(path);

    private NativeConnection Native() => _native ??= NativeConnection.OpenReadOnly(_dbPath, TimeSpan.FromSeconds(15));

    private static T? Parse<T>(string? text, string name) where T : struct, Enum
    {
        if (text is null)
        {
            return null;
        }

        return Enum.TryParse<T>(text, ignoreCase: true, out var value)
            ? value
            : throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"unknown {name} '{text}'. Use one of: {string.Join(", ", Enum.GetNames<T>())}"));
    }

    public void Dispose()
    {
        if (_ownsStore)
        {
            _store.Dispose();
        }
        else
        {
            _native?.Dispose();
        }
    }
}

/// <summary>How a <see cref="CodeGraphClient"/> shapes symbols.</summary>
public sealed record ClientOptions(bool RelativePaths, bool Projects)
{
    /// <summary>Absolute paths, no project names: what the MCP server formats its rows from.</summary>
    public static readonly ClientOptions Server = new(RelativePaths: false, Projects: false);

    /// <summary>Paths relative to the repo root and each symbol's project: what scripts and the CLI print.</summary>
    public static readonly ClientOptions Scripts = new(RelativePaths: true, Projects: true);
}
