using DuckDB.NET.Data;

namespace CodeGraph.Core;

/// <summary>
/// DuckDB persistence for the code graph. Two tables and recursive CTEs.
/// DuckDB's columnar engine makes it easy to export the data for graph visualisation tools.
/// </summary>
public sealed class GraphStore : IDisposable
{
    public static readonly string[] AllEdgeKinds =
    [
        nameof(EdgeKind.Calls),
        nameof(EdgeKind.Overrides),
        nameof(EdgeKind.Implements),
        nameof(EdgeKind.Inherits),
        nameof(EdgeKind.Contains),
        nameof(EdgeKind.DependsOn),
        nameof(EdgeKind.TestedBy),
        nameof(EdgeKind.InvokedByFramework),
        nameof(EdgeKind.References),
        nameof(EdgeKind.CallsExternal),
        nameof(EdgeKind.Imports),
        nameof(EdgeKind.Injects),
        nameof(EdgeKind.Renders),
    ];

    // References, Injects and Renders only ever point at types, so they widen impact_of(type) without changing
    // member walks: a service's dependents include what injects it, a component's include the templates using it.
    private static readonly string[] ImpactEdgeKinds =
    [
        nameof(EdgeKind.Calls),
        nameof(EdgeKind.Overrides),
        nameof(EdgeKind.Implements),
        nameof(EdgeKind.Inherits),
        nameof(EdgeKind.References),
        nameof(EdgeKind.Injects),
        nameof(EdgeKind.Renders),
    ];

    /// <summary>Bumped when the indexer's output changes shape; a mismatch forces a full rebuild.</summary>
    public const string SchemaVersion = "6";

    private readonly string _dbPath;
    private readonly GraphSnapshot? _snapshot;
    private readonly Func<DuckDBConnection>? _connect;
    private DuckDBConnection? _db;
    private IDisposable? _lease;

    public GraphStore(string dbPath, bool readOnly = false)
    {
        _dbPath = Path.GetFullPath(dbPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connStr = readOnly ? ReadOnlyConnectionString(dbPath) : $"Data Source={dbPath}";
        _db = new DuckDBConnection(connStr);
        _db.Open();
        if (!readOnly)
            EnsureSchema();
    }

    private GraphStore(string dbPath, GraphSnapshot snapshot, Func<IDisposable?>? lease, TimeSpan wait)
    {
        _dbPath = Path.GetFullPath(dbPath);
        _snapshot = snapshot;
        _connect = () =>
        {
            _lease ??= lease?.Invoke();
            return ConnectReadOnly(_dbPath, wait);
        };
    }

    /// <summary>
    /// The in-memory graph this store answers graph reads from, or null when every read goes to DuckDB. A store with
    /// a snapshot opens its database connection only for what the snapshot does not hold (full-text search, vectors,
    /// metadata), so most tool calls never touch the file.
    /// </summary>
    public GraphSnapshot? Snapshot => _snapshot;

    /// <summary>Whether this store has opened its DuckDB connection; a reader with a snapshot may never need one.</summary>
    public bool IsConnected => _db is not null;

    // Opened on first use for a reader with a snapshot; every other store opens it in the constructor.
    private DuckDBConnection Connection => _db ??= _connect?.Invoke() ?? throw new ObjectDisposedException(nameof(GraphStore));

    public void Dispose()
    {
        _db?.Dispose();
        _lease?.Dispose();
    }

    /// <summary>
    /// A read-only store for one tool call. With a <paramref name="snapshot"/>, graph reads answer from memory and the
    /// database is opened only if the call needs it; without one, this is <see cref="Open"/>. <paramref name="lease"/>
    /// runs just before the database is opened (the server's <see cref="GraphAnchor"/> lease) and is disposed with the store.
    /// </summary>
    public static GraphStore OpenReader(string dbPath, GraphSnapshot? snapshot, TimeSpan wait, Func<IDisposable?>? lease = null)
    {
        if (snapshot is not null)
        {
            return new GraphStore(dbPath, snapshot, lease, wait);
        }

        var held = lease?.Invoke();
        try
        {
            var store = Open(dbPath, readOnly: true, wait);
            store._lease = held;
            return store;
        }
        catch
        {
            held?.Dispose();
            throw;
        }
    }

    private static DuckDBConnection ConnectReadOnly(string dbPath, TimeSpan wait)
    {
        if (!File.Exists(dbPath))
        {
            throw new FileNotFoundException("No code graph at this path; run codegraph-index first.", dbPath);
        }

        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            var connection = new DuckDBConnection(ReadOnlyConnectionString(dbPath));
            try
            {
                connection.Open();
                return connection;
            }
            catch (DuckDBException) when (DateTime.UtcNow < deadline)
            {
                connection.Dispose();
                Thread.Sleep(100);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Reads every node and edge into a <see cref="GraphSnapshot"/>. Repeated strings (file paths, languages, edge
    /// kinds) are shared, so the snapshot costs little more than the ids and names themselves.
    /// </summary>
    public GraphSnapshot LoadSnapshot()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var pool = new Dictionary<string, string>(StringComparer.Ordinal);
        string Intern(string text)
        {
            if (pool.TryGetValue(text, out var shared))
            {
                return shared;
            }

            pool[text] = text;
            return text;
        }

        var nodes = new List<GraphNode>();
        using (var command = Connection.CreateCommand())
        {
            command.CommandText = $"SELECT {NodeColumns} FROM nodes;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var node = ReadNode(reader);
                nodes.Add(node with
                {
                    FilePath = node.FilePath is null ? null : Intern(node.FilePath),
                    Language = Intern(node.Language),
                });
            }
        }

        var edges = new List<(string, string, string)>();
        using (var command = Connection.CreateCommand())
        {
            command.CommandText = "SELECT from_id, to_id, kind FROM edges;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                edges.Add((reader.GetString(0), reader.GetString(1), Intern(reader.GetString(2))));
            }
        }

        var searchDocuments = TableExists("search_docs") ? Scalar("SELECT COUNT(*) FROM search_docs;") : 0;
        var embeddings = TableExists("embeddings") ? Scalar("SELECT COUNT(*) FROM embeddings;") : 0;
        var snapshot = GraphSnapshot.Build(nodes, edges, searchDocuments, embeddings);
        snapshot.LoadTime = watch.Elapsed;
        return snapshot;
    }

    private int Scalar(string sql)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The connection string of every read-only open. DuckDB.NET shares one database instance between connections
    /// with the same string, which is what lets <see cref="GraphAnchor"/> make later opens cheap.
    /// </summary>
    internal static string ReadOnlyConnectionString(string dbPath) => $"Data Source={dbPath};ACCESS_MODE=READ_ONLY";

    /// <summary>
    /// Opens a store, retrying while another process holds the DuckDB file lock. DuckDB allows one writer
    /// or many readers per file, so the indexer and the MCP servers take turns.
    /// </summary>
    public static GraphStore Open(string dbPath, bool readOnly, TimeSpan wait)
    {
        // A missing file is not a lock: fail now instead of spinning out the whole wait.
        if (readOnly && !File.Exists(dbPath))
        {
            throw new FileNotFoundException("No code graph at this path; run codegraph-index first.", dbPath);
        }

        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                return new GraphStore(dbPath, readOnly);
            }
            catch (DuckDBException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>Drops all graph data. Called before a full re-index.</summary>
    public void Clear()
    {
        Execute("DELETE FROM search_terms;");
        Execute("DELETE FROM search_docs;");
        Execute("DELETE FROM embeddings;");
        Execute("DELETE FROM edge_origins;");
        Execute("DELETE FROM edges;");
        Execute("DELETE FROM nodes;");
        Execute("DELETE FROM files;");
    }

    public void Write(IEnumerable<GraphNode> nodes, IEnumerable<GraphEdge> edges)
    {
        using var transaction = Connection.BeginTransaction();
        WriteCore(transaction, nodes, edges, []);
        transaction.Commit();
    }

    /// <summary>
    /// Swaps in a re-index in one transaction so readers never see a half-written graph.
    /// <paramref name="removedFiles"/> are source (or project) paths whose nodes and edge origins are dropped
    /// first; an edge is only deleted once no remaining file still produces it.
    /// With <paramref name="full"/> everything is dropped instead. <paramref name="search"/> are the full-text
    /// documents of the nodes being written; a node's old document goes with its file.
    /// </summary>
    public void Replace(
        bool full,
        IReadOnlyCollection<string> removedFiles,
        IEnumerable<GraphNode> nodes,
        IEnumerable<GraphEdge> edges,
        IEnumerable<EdgeOrigin> origins,
        IEnumerable<FileStamp> files,
        IReadOnlyDictionary<string, string> meta,
        IEnumerable<SearchDocument>? search = null,
        IEnumerable<NodeEmbedding>? embeddings = null,
        string language = Languages.CSharp)
    {
        using var transaction = Connection.BeginTransaction();

        if (full)
        {
            foreach (var table in new[] { "search_terms", "search_docs", "embeddings", "edge_origins", "edges", "nodes", "files" })
            {
                Execute($"DELETE FROM {table};", transaction);
            }
        }
        else if (removedFiles.Count > 0)
        {
            var removed = Lines(removedFiles);
            Execute($"DELETE FROM search_terms WHERE node_id IN (SELECT id FROM nodes WHERE file_path IN ({PathList}));", transaction, removed);
            Execute($"DELETE FROM search_docs WHERE node_id IN (SELECT id FROM nodes WHERE file_path IN ({PathList}));", transaction, removed);
            Execute($"DELETE FROM embeddings WHERE node_id IN (SELECT id FROM nodes WHERE file_path IN ({PathList}));", transaction, removed);
            Execute(
                $"""
                 DELETE FROM edges e
                 WHERE EXISTS (SELECT 1 FROM edge_origins o
                               WHERE o.from_id = e.from_id AND o.to_id = e.to_id AND o.kind = e.kind
                                 AND o.file_path IN ({PathList}))
                   AND NOT EXISTS (SELECT 1 FROM edge_origins o
                                   WHERE o.from_id = e.from_id AND o.to_id = e.to_id AND o.kind = e.kind
                                     AND o.file_path NOT IN ({PathList}));
                 """, transaction, removed);
            Execute($"DELETE FROM edge_origins WHERE file_path IN ({PathList});", transaction, removed);
            Execute($"DELETE FROM nodes WHERE file_path IN ({PathList});", transaction, removed);
            Execute($"DELETE FROM files WHERE path IN ({PathList});", transaction, removed);
        }

        // No dangling-edge sweep: edges to node-less targets (implicit constructors) are legitimate and a full
        // index keeps them too. A file pointing at a removed member is a dependent, so it was re-walked above.
        WriteCore(transaction, nodes, edges, origins);

        // External nodes belong to no file, so removing a file never removes them; one whose last caller just went
        // would linger. A full index never has such a node, and incremental must equal full.
        if (!full && removedFiles.Count > 0)
        {
            Execute(
                """
                DELETE FROM nodes WHERE kind = 'External'
                  AND NOT EXISTS (SELECT 1 FROM edges e WHERE e.to_id = nodes.id);
                """, transaction);
        }
        if (search is not null)
        {
            WriteSearch(transaction, search);
        }

        if (embeddings is not null)
        {
            WriteEmbeddings(transaction, embeddings);
        }

        WriteFiles(transaction, files, language);

        using (var command = Connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ($key, $value);";
            foreach (var (key, value) in meta)
            {
                command.Parameters.Clear();
                command.Parameters.Add(P("key", key));
                command.Parameters.Add(P("value", value));
                command.ExecuteNonQuery();
            }
        }

        transaction.Commit();

        // Every re-index deletes and re-inserts rows; without a checkpoint the freed blocks stay in the write-ahead
        // log's shadow and the file grows by a few MB per run, for as long as a --watch session lasts.
        Execute("CHECKPOINT;");
    }

    /// <summary>Index metadata: schema version, solution, git commit, time. Empty for a database from before metadata existed.</summary>
    public IReadOnlyDictionary<string, string> ReadMeta()
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!TableExists("meta"))
        {
            return meta;
        }

        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM meta;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            meta[reader.GetString(0)] = reader.GetString(1);
        }

        return meta;
    }

    /// <summary>The stamp of every source file as of the last index, keyed by path; only one language's with <paramref name="language"/>.</summary>
    public IReadOnlyDictionary<string, FileStamp> IndexedFiles(string? language = null)
    {
        var files = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        if (!TableExists("files"))
        {
            return files;
        }

        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT path, hash, size, mtime_ticks FROM files WHERE $language IS NULL OR language = $language;";
        command.Parameters.Add(P("language", language));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var stamp = new FileStamp(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3));
            files[stamp.Path] = stamp;
        }

        return files;
    }

    /// <summary>Paths of the project files (.csproj, tsconfig.json, pom.xml) behind every Project node of a language.</summary>
    public IReadOnlyList<string> ProjectFilePaths(string? language = Languages.CSharp)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT file_path FROM nodes WHERE kind = 'Project' AND file_path IS NOT NULL AND ($language IS NULL OR language = $language);";
        command.Parameters.Add(P("language", language));
        var paths = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
    }

    /// <summary>
    /// Every path one language's rows hang off: its stamped source files and the file paths of its nodes (project
    /// files included). An importer removes exactly these before writing that language again, leaving the others.
    /// </summary>
    public IReadOnlySet<string> LanguagePaths(string language)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = Connection.CreateCommand();
        command.CommandText =
            """
            SELECT path FROM files WHERE language = $language
            UNION SELECT file_path FROM nodes WHERE language = $language AND file_path IS NOT NULL;
            """;
        command.Parameters.Add(P("language", language));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
    }

    /// <summary>The languages this graph holds, with node counts, most first.</summary>
    public IReadOnlyList<(string Language, int Nodes)> LanguageCounts()
    {
        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT language, COUNT(*) FROM nodes WHERE kind <> 'External' GROUP BY language ORDER BY 2 DESC, 1;";
        var counts = new List<(string, int)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            counts.Add((reader.GetString(0), reader.GetInt32(1)));
        }

        return counts;
    }

    /// <summary>
    /// Copies every other language's rows from the graph at <paramref name="sourceDb"/> into this one: a full C#
    /// rebuild writes a fresh file, and a TypeScript or Java part indexed by another extractor must survive it.
    /// Rows are matched by node language and by the files those nodes and their edge origins hang off, the same
    /// scoping <see cref="LanguagePaths"/> gives an importer. A source from before schema 6 holds no other language.
    /// </summary>
    public int CopyOtherLanguages(string sourceDb, params IReadOnlyCollection<string> exceptLanguages)
    {
        if (!File.Exists(sourceDb))
        {
            return 0;
        }

        Execute($"ATTACH '{sourceDb.Replace("'", "''")}' AS other (READ_ONLY);");
        try
        {
            using (var probe = Connection.CreateCommand())
            {
                probe.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_catalog = 'other' AND table_name = 'files' AND column_name = 'language';";
                if (Convert.ToInt64(probe.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
                {
                    return 0;
                }
            }

            using var transaction = Connection.BeginTransaction();
            // The languages being rewritten, as a list DuckDB can test membership in.
            var rewritten = string.Join("\n", exceptLanguages);
            void Run(string sql)
            {
                using var command = Connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                command.Parameters.Add(P("languages", rewritten));
                command.ExecuteNonQuery();
            }

            const string Kept = "NOT IN (SELECT unnest(string_split($languages, chr(10))))";
            const string ForeignNodes = $"SELECT id FROM other.nodes WHERE language {Kept}";
            const string ForeignPaths =
                $"SELECT path FROM other.files WHERE language {Kept} " +
                $"UNION SELECT file_path FROM other.nodes WHERE language {Kept} AND file_path IS NOT NULL";
            Run("CREATE TEMP TABLE IF NOT EXISTS copy_paths (path VARCHAR);");
            Run("DELETE FROM copy_paths;");
            Run($"INSERT INTO copy_paths {ForeignPaths};");
            Run($"INSERT INTO nodes ({NodeColumns}) SELECT {NodeColumns} FROM other.nodes WHERE language {Kept} ON CONFLICT DO NOTHING;");
            Run("INSERT INTO edge_origins SELECT from_id, to_id, kind, file_path FROM other.edge_origins WHERE file_path IN (SELECT path FROM copy_paths) ON CONFLICT DO NOTHING;");
            Run(
                """
                INSERT INTO edges SELECT DISTINCT e.from_id, e.to_id, e.kind FROM other.edges e
                JOIN other.edge_origins o ON o.from_id = e.from_id AND o.to_id = e.to_id AND o.kind = e.kind
                WHERE o.file_path IN (SELECT path FROM copy_paths)
                ON CONFLICT DO NOTHING;
                """);
            Run($"INSERT INTO files (path, hash, size, mtime_ticks, language) SELECT path, hash, size, mtime_ticks, language FROM other.files WHERE language {Kept} ON CONFLICT DO NOTHING;");
            Run($"INSERT INTO search_docs SELECT node_id, length FROM other.search_docs WHERE node_id IN ({ForeignNodes}) ON CONFLICT DO NOTHING;");
            Run($"INSERT INTO search_terms SELECT node_id, term, tf FROM other.search_terms WHERE node_id IN ({ForeignNodes}) ON CONFLICT DO NOTHING;");
            Run($"INSERT INTO embeddings SELECT node_id, text_hash, vector FROM other.embeddings WHERE node_id IN ({ForeignNodes}) ON CONFLICT DO NOTHING;");
            // Per-language meta (lang.<language>.*) rides along; the rebuilding language writes its own.
            Run($"INSERT INTO meta SELECT key, value FROM other.meta WHERE key LIKE 'lang.%' AND split_part(key, '.', 2) {Kept} ON CONFLICT DO NOTHING;");
            int copied;
            using (var count = Connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = $"SELECT COUNT(*) FROM nodes WHERE language {Kept};";
                count.Parameters.Add(P("languages", rewritten));
                copied = Convert.ToInt32(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }

            transaction.Commit();
            return copied;
        }
        finally
        {
            Execute("DETACH other;");
        }
    }

    /// <summary>
    /// Files whose walk must be redone when <paramref name="paths"/> change: files with an edge into a node declared
    /// in one of them (callers, subclasses, referencers, tests), and files with an edge out of one (other partial
    /// declarations of the same type).
    /// </summary>
    public IReadOnlySet<string> DependentFiles(IReadOnlyCollection<string> paths)
    {
        var dependents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0 || !TableExists("edge_origins"))
        {
            return dependents;
        }

        using var command = Connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT DISTINCT o.file_path FROM edge_origins o
             JOIN nodes n ON n.id = o.to_id OR n.id = o.from_id
             WHERE n.file_path IN ({PathList}) AND n.kind != 'Project';
             """;
        command.Parameters.Add(P("paths", Lines(paths)));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            dependents.Add(reader.GetString(0));
        }

        return dependents;
    }

    // DuckDB executes a parameterized INSERT in milliseconds, which is minutes for a solution's worth of rows
    // (one run burned 12 CPU-minutes on edges alone). The appender loads a table in a second but cannot merge,
    // so rows are appended to a temp table first and one statement per table merges them in with the same
    // conflict rules a per-row insert had: nodes and files update in place, edges and origins ignore duplicates.
    private void WriteCore(DuckDBTransaction transaction, IEnumerable<GraphNode> nodes, IEnumerable<GraphEdge> edges, IEnumerable<EdgeOrigin> origins)
    {
        Stage(transaction, "stage_nodes", StageNodeSchema);
        using (var appender = StageAppender("stage_nodes"))
        {
            foreach (var node in nodes)
            {
                appender.CreateRow()
                    .AppendValue(node.Id)
                    .AppendValue(node.Kind.ToString())
                    .AppendValue(node.Name)
                    .AppendValue(node.FullName)
                    .AppendValue(node.Signature)
                    .AppendValue(node.FilePath)
                    .AppendValue(node.StartLine)
                    .AppendValue(node.EndLine)
                    .AppendValue(node.IsTest)
                    .AppendValue(node.TypeKind.ToString())
                    .AppendValue(node.Accessibility.ToString())
                    .AppendValue(node.IsStatic)
                    .AppendValue(node.IsAbstract)
                    .AppendValue(node.IsVirtual)
                    .AppendValue(node.IsSealed)
                    .AppendValue(node.IsAsync)
                    .AppendValue(node.IsOverride)
                    .AppendValue(node.MemberKind.ToString())
                    .AppendValue(node.Language)
                    .EndRow();
            }
        }

        // The last row for an id wins, as the last per-row upsert used to. ON CONFLICT DO UPDATE rejects a source
        // with duplicate keys, so the staging rows are deduplicated first.
        Execute(
            """
            INSERT INTO nodes
            SELECT * FROM stage_nodes
            QUALIFY row_number() OVER (PARTITION BY id ORDER BY rowid DESC) = 1
            ON CONFLICT (id) DO UPDATE SET
                kind = excluded.kind, name = excluded.name, full_name = excluded.full_name,
                signature = excluded.signature, file_path = excluded.file_path,
                start_line = excluded.start_line, end_line = excluded.end_line,
                is_test = excluded.is_test, type_kind = excluded.type_kind,
                accessibility = excluded.accessibility, is_static = excluded.is_static,
                is_abstract = excluded.is_abstract, is_virtual = excluded.is_virtual,
                is_sealed = excluded.is_sealed, is_async = excluded.is_async,
                is_override = excluded.is_override, member_kind = excluded.member_kind,
                language = excluded.language;
            """, transaction);
        Execute("DELETE FROM stage_nodes;", transaction);

        Stage(transaction, "stage_edges", "from_id VARCHAR, to_id VARCHAR, kind VARCHAR");
        using (var appender = StageAppender("stage_edges"))
        {
            foreach (var edge in edges)
            {
                appender.CreateRow().AppendValue(edge.FromId).AppendValue(edge.ToId).AppendValue(edge.Kind.ToString()).EndRow();
            }
        }

        Execute("INSERT INTO edges SELECT DISTINCT from_id, to_id, kind FROM stage_edges ON CONFLICT DO NOTHING;", transaction);
        Execute("DELETE FROM stage_edges;", transaction);

        Stage(transaction, "stage_edge_origins", "from_id VARCHAR, to_id VARCHAR, kind VARCHAR, file_path VARCHAR");
        using (var appender = StageAppender("stage_edge_origins"))
        {
            foreach (var origin in origins)
            {
                appender.CreateRow()
                    .AppendValue(origin.Edge.FromId)
                    .AppendValue(origin.Edge.ToId)
                    .AppendValue(origin.Edge.Kind.ToString())
                    .AppendValue(origin.FilePath)
                    .EndRow();
            }
        }

        Execute("INSERT INTO edge_origins SELECT DISTINCT from_id, to_id, kind, file_path FROM stage_edge_origins ON CONFLICT DO NOTHING;", transaction);
        Execute("DELETE FROM stage_edge_origins;", transaction);
    }

    private void WriteFiles(DuckDBTransaction transaction, IEnumerable<FileStamp> files, string language)
    {
        Stage(transaction, "stage_files", "path VARCHAR, hash VARCHAR, size BIGINT, mtime_ticks BIGINT");
        using (var appender = StageAppender("stage_files"))
        {
            foreach (var file in files)
            {
                appender.CreateRow().AppendValue(file.Path).AppendValue(file.Hash).AppendValue(file.Size).AppendValue(file.MtimeTicks).EndRow();
            }
        }

        Execute(
            """
            INSERT INTO files (path, hash, size, mtime_ticks, language)
            SELECT path, hash, size, mtime_ticks, $language FROM stage_files
            QUALIFY row_number() OVER (PARTITION BY path ORDER BY rowid DESC) = 1
            ON CONFLICT (path) DO UPDATE SET hash = excluded.hash, size = excluded.size, mtime_ticks = excluded.mtime_ticks,
                language = excluded.language;
            """, transaction, language: language);
        Execute("DELETE FROM stage_files;", transaction);
    }

    private const string StageNodeSchema =
        "id VARCHAR, kind VARCHAR, name VARCHAR, full_name VARCHAR, signature VARCHAR, file_path VARCHAR, " +
        "start_line INTEGER, end_line INTEGER, is_test BOOLEAN, type_kind VARCHAR, accessibility VARCHAR, " +
        "is_static BOOLEAN, is_abstract BOOLEAN, is_virtual BOOLEAN, is_sealed BOOLEAN, is_async BOOLEAN, " +
        "is_override BOOLEAN, member_kind VARCHAR, language VARCHAR";

    /// <summary>A connection-local, unconstrained staging table; it lives in DuckDB's temp catalog, never in the file.</summary>
    private void Stage(DuckDBTransaction transaction, string table, string columns)
    {
        Execute($"CREATE TEMP TABLE IF NOT EXISTS {table} ({columns});", transaction);
        Execute($"DELETE FROM {table};", transaction);
    }

    private DuckDBAppender StageAppender(string table) => Connection.CreateAppender("temp", "main", table);

    private void WriteSearch(DuckDBTransaction transaction, IEnumerable<SearchDocument> documents)
    {
        var list = documents as IReadOnlyCollection<SearchDocument> ?? documents.ToList();
        if (list.Count == 0)
        {
            return;
        }

        // A re-walked node may have lost terms since last time; start every node clean, in one statement.
        var ids = Lines(list.Select(d => d.NodeId));
        Execute($"DELETE FROM search_terms WHERE node_id IN ({PathList});", transaction, ids);
        Execute($"DELETE FROM search_docs WHERE node_id IN ({PathList});", transaction, ids);

        // Term rows run to hundreds of thousands per solution; one statement per row takes minutes in DuckDB,
        // the appender takes a second. It appends within the connection's open transaction.
        using (var appender = Connection.CreateAppender("search_docs"))
        {
            foreach (var document in list)
            {
                appender.CreateRow().AppendValue(document.NodeId).AppendValue(document.Length).EndRow();
            }
        }

        using (var appender = Connection.CreateAppender("search_terms"))
        {
            foreach (var document in list)
            {
                foreach (var (term, tf) in document.Terms)
                {
                    appender.CreateRow().AppendValue(document.NodeId).AppendValue(term).AppendValue(tf).EndRow();
                }
            }
        }
    }

    private void WriteEmbeddings(DuckDBTransaction transaction, IEnumerable<NodeEmbedding> embeddings)
    {
        var list = embeddings as IReadOnlyCollection<NodeEmbedding> ?? embeddings.ToList();
        if (list.Count == 0)
        {
            return;
        }

        Execute($"DELETE FROM embeddings WHERE node_id IN ({PathList});", transaction, Lines(list.Select(e => e.NodeId)));
        using var appender = Connection.CreateAppender("embeddings");
        foreach (var embedding in list.DistinctBy(e => e.NodeId))
        {
            appender.CreateRow().AppendValue(embedding.NodeId).AppendValue(embedding.TextHash).AppendValue(embedding.Vector.ToBytes()).EndRow();
        }
    }

    /// <summary>Number of nodes with a vector; zero when embeddings were never configured.</summary>
    public int EmbeddingCount()
    {
        if (_snapshot is not null)
        {
            return _snapshot.Embeddings;
        }
        if (!TableExists("embeddings"))
        {
            return 0;
        }

        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM embeddings;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>Every stored vector by the hash of its text, so a re-index embeds only declarations whose text changed.</summary>
    public IReadOnlyDictionary<string, QuantizedVector> EmbeddingCache()
    {
        var cache = new Dictionary<string, QuantizedVector>(StringComparer.Ordinal);
        if (!TableExists("embeddings"))
        {
            return cache;
        }

        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT text_hash, vector FROM embeddings;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cache.TryAdd(reader.GetString(0), QuantizedVector.FromBytes(ReadBlob(reader, 1)));
        }

        return cache;
    }

    /// <summary>
    /// The nodes whose vectors are closest to <paramref name="query"/> by cosine similarity. Scoring runs in memory
    /// over all vectors (<see cref="VectorIndex"/>, loaded once per graph file); the kind and file filters apply to
    /// an over-fetched candidate list, which widens until enough survive or every vector has been considered.
    /// </summary>
    public IReadOnlyList<(GraphNode Node, double Similarity)> VectorSearch(float[] query, NodeKind? kind = null, string? filePattern = null, int limit = 20)
    {
        if (_snapshot is { Embeddings: 0 } || !TableExists("embeddings"))
        {
            return [];
        }

        limit = Math.Clamp(limit, 1, 200);
        var index = VectorIndex.For(_dbPath, LoadVectorIndex);
        var quantized = VectorQuantizer.Quantize(query);
        var filtered = kind is not null || filePattern is not null;
        for (var fetch = filtered ? limit * 8 : limit; ; fetch *= 4)
        {
            var top = index.Top(quantized, Math.Min(fetch, index.Count));
            var nodes = NodesById(top.Select(t => t.Id).ToList());
            var hits = top
                .Where(t => nodes.ContainsKey(t.Id))
                .Select(t => (Node: nodes[t.Id], t.Similarity))
                .Where(h => kind is null || h.Node.Kind == kind)
                .Where(h => filePattern is null || h.Node.FilePath?.Contains(filePattern, StringComparison.OrdinalIgnoreCase) == true)
                .Take(limit)
                .ToList();
            if (hits.Count >= limit || fetch >= index.Count)
            {
                return hits;
            }
        }
    }

    private VectorIndex LoadVectorIndex()
    {
        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT node_id, vector FROM embeddings;";
        var rows = new List<(string, QuantizedVector)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), QuantizedVector.FromBytes(ReadBlob(reader, 1))));
        }

        return new VectorIndex(rows);
    }

    private Dictionary<string, GraphNode> NodesById(IReadOnlyList<string> ids)
    {
        if (_snapshot is not null)
        {
            return _snapshot.NodesById(ids);
        }
        using var command = Connection.CreateCommand();
        command.CommandText = $"SELECT {NodeColumns} FROM nodes WHERE id IN ({PathList});";
        command.Parameters.Add(P("paths", Lines(ids)));
        return ReadNodes(command).ToDictionary(n => n.Id, StringComparer.Ordinal);
    }

    private static byte[] ReadBlob(System.Data.Common.DbDataReader reader, int ordinal)
    {
        using var stream = reader.GetStream(ordinal);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Number of nodes with a full-text document; zero for a graph built before search existed.</summary>
    public int SearchDocumentCount()
    {
        if (_snapshot is not null)
        {
            return _snapshot.SearchDocuments;
        }
        if (!TableExists("search_docs"))
        {
            return 0;
        }

        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM search_docs;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>
    /// "Where's the code that does X": BM25 over the names, signatures, doc comments and body identifiers the
    /// indexer tokenized. Nodes matching more of the query's terms rank first, then by score.
    /// </summary>
    public IReadOnlyList<SearchHit> Search(string query, NodeKind? kind = null, string? filePattern = null, int limit = 20, string? language = null)
    {
        var terms = SearchText.QueryTerms(query);
        if (terms.Count == 0 || _snapshot is { SearchDocuments: 0 } || !TableExists("search_terms"))
        {
            return [];
        }

        using var command = Connection.CreateCommand();
        command.CommandText =
            $"""
             WITH q AS ({PathList} AS term),
             stats AS (SELECT COUNT(*) AS n, AVG(length) AS avgdl FROM search_docs),
             df AS (
                 SELECT term, COUNT(*) AS df FROM search_terms
                 WHERE term IN (SELECT term FROM q)
                 GROUP BY term
             ),
             scored AS (
                 SELECT t.node_id,
                        COUNT(DISTINCT t.term) AS matched,
                        SUM(ln(1 + (s.n - df.df + 0.5) / (df.df + 0.5))
                            * t.tf * 2.2 / (t.tf + 1.2 * (0.25 + 0.75 * d.length / NULLIF(s.avgdl, 0)))) AS score
                 FROM search_terms t
                 JOIN df ON df.term = t.term
                 JOIN search_docs d ON d.node_id = t.node_id
                 CROSS JOIN stats s
                 GROUP BY t.node_id
             )
             SELECT {QualifiedNodeColumns}, sc.score, sc.matched
             FROM scored sc
             JOIN nodes n ON n.id = sc.node_id
             WHERE ($kind IS NULL OR n.kind = $kind)
               AND ($file IS NULL OR n.file_path ILIKE $file)
               AND ($language IS NULL OR n.language = $language)
             ORDER BY sc.matched DESC, sc.score DESC, n.full_name, n.id
             LIMIT $limit;
             """;
        command.Parameters.Add(P("paths", Lines(terms)));
        command.Parameters.Add(P("kind", kind?.ToString()));
        command.Parameters.Add(P("file", filePattern is null ? null : $"%{filePattern}%"));
        command.Parameters.Add(P("limit", Math.Clamp(limit, 1, 200)));
        command.Parameters.Add(P("language", language));

        var hits = new List<SearchHit>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            hits.Add(new SearchHit(ReadNode(reader), reader.GetDouble(reader.FieldCount - 2), reader.GetInt32(reader.FieldCount - 1), terms.Count));
        }

        return hits;
    }

    public (int Nodes, int Edges) Count()
    {
        if (_snapshot is not null)
        {
            return _snapshot.Count();
        }
        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM nodes), (SELECT COUNT(*) FROM edges);";
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    public GraphNode? GetNode(string id)
    {
        if (_snapshot is not null)
        {
            return _snapshot.GetNode(id);
        }
        using var command = Connection.CreateCommand();
        command.CommandText = $"SELECT {NodeColumns} FROM nodes WHERE id = $id;";
        command.Parameters.Add(P("id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadNode(reader) : null;
    }

    /// <summary>
    /// Case-insensitive name match on name or full name. Exact name matches sort first,
    /// then shorter names, so <c>FindSymbol("Save")</c> returns <c>Save</c> before <c>SaveAllAsync</c>.
    /// </summary>
    public IReadOnlyList<GraphNode> FindSymbol(
        string query,
        NodeKind? kind = null,
        int limit = 20,
        TypeKind? typeKind = null,
        Accessibility? accessibility = null,
        bool? isStatic = null,
        bool? isAbstract = null,
        bool? isVirtual = null,
        bool? isAsync = null,
        bool? isOverride = null,
        MemberKind? memberKind = null,
        string nameMatch = "Contains",
        int offset = 0,
        string? language = null)
    {
        if (_snapshot is not null)
        {
            return _snapshot.FindSymbol(query, kind, limit, typeKind, accessibility, isStatic, isAbstract, isVirtual, isAsync, isOverride, memberKind, nameMatch, offset, language);
        }
        // `_` and `%` are wildcards to ILIKE but ordinary characters in identifiers (Save_works, <Main>$).
        var literal = EscapeLike(query);
        var pattern = nameMatch.ToLowerInvariant() switch
        {
            "startswith" => $"{literal}%",
            "endswith"   => $"%{literal}",
            "exact"      => literal,
            _            => $"%{literal}%",  // contains
        };
        using var command = Connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT {NodeColumns} FROM nodes
             WHERE (name ILIKE $pattern ESCAPE '\' OR full_name ILIKE $pattern ESCAPE '\')
               AND ($kind IS NULL OR kind = $kind)
               AND ($type_kind IS NULL OR type_kind = $type_kind)
               AND ($accessibility IS NULL OR accessibility = $accessibility)
               AND ($is_static IS NULL OR is_static = $is_static)
               AND ($is_abstract IS NULL OR is_abstract = $is_abstract)
               AND ($is_virtual IS NULL OR is_virtual = $is_virtual)
               AND ($is_async IS NULL OR is_async = $is_async)
               AND ($is_override IS NULL OR is_override = $is_override)
               AND ($member_kind IS NULL OR member_kind = $member_kind)
               AND ($language IS NULL OR language = $language)
             ORDER BY
               CASE WHEN LOWER(name) = LOWER($exact) THEN 0 ELSE 1 END,
               LENGTH(name),
               full_name,
               id
             LIMIT $limit OFFSET $offset;
             """;
        command.Parameters.Add(P("pattern", pattern));
        command.Parameters.Add(P("exact", query));
        command.Parameters.Add(P("offset", Math.Max(0, offset)));
        command.Parameters.Add(P("kind", kind?.ToString()));
        command.Parameters.Add(P("limit", limit));
        command.Parameters.Add(P("type_kind", typeKind?.ToString()));
        command.Parameters.Add(P("accessibility", accessibility?.ToString()));
        command.Parameters.Add(P("is_static", isStatic.HasValue ? (object?)isStatic.Value : null));
        command.Parameters.Add(P("is_abstract", isAbstract.HasValue ? (object?)isAbstract.Value : null));
        command.Parameters.Add(P("is_virtual", isVirtual.HasValue ? (object?)isVirtual.Value : null));
        command.Parameters.Add(P("is_async", isAsync.HasValue ? (object?)isAsync.Value : null));
        command.Parameters.Add(P("is_override", isOverride.HasValue ? (object?)isOverride.Value : null));
        command.Parameters.Add(P("member_kind", memberKind?.ToString()));
        command.Parameters.Add(P("language", language));
        return ReadNodes(command);
    }

    /// <summary>
    /// The innermost declaration spanning <paramref name="line"/> (one-based) of <paramref name="file"/>: the member
    /// a reference sits in, for live answers that arrive as file positions (a language server's locations).
    /// </summary>
    public GraphNode? NodeAt(string file, int line)
    {
        if (_snapshot is not null)
        {
            return _snapshot.NodeAt(file, line);
        }
        using var command = Connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT {NodeColumns} FROM nodes
             WHERE file_path = $file AND start_line <= $line AND end_line >= $line AND kind NOT IN ('File', 'Project', 'External')
             ORDER BY end_line - start_line, start_line DESC, id
             LIMIT 1;
             """;
        command.Parameters.Add(P("file", file));
        command.Parameters.Add(P("line", line));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadNode(reader) : null;
    }

    /// <summary>Direct children via Contains edges (project to files, file to types, type to members).</summary>
    public IReadOnlyList<GraphNode> Children(string id)
    {
        if (_snapshot is not null)
        {
            return _snapshot.Children(id);
        }
        using var command = Connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT {QualifiedNodeColumns} FROM nodes n
             JOIN edges e ON e.to_id = n.id
             WHERE e.from_id = $id AND e.kind = 'Contains'
             ORDER BY n.start_line, n.name, n.id;
             """;
        command.Parameters.Add(P("id", id));
        return ReadNodes(command);
    }

    /// <summary>Direct members of a type, optionally filtered by kind, accessibility and modifier flags.</summary>
    public IReadOnlyList<GraphNode> MembersOf(
        string typeId,
        NodeKind? kind = null,
        Accessibility? accessibility = null,
        bool? isStatic = null,
        bool? isAbstract = null,
        bool? isAsync = null,
        bool? isVirtual = null,
        bool? isOverride = null,
        int limit = 500,
        int offset = 0)
    {
        if (_snapshot is not null)
        {
            return _snapshot.MembersOf(typeId, kind, accessibility, isStatic, isAbstract, isAsync, isVirtual, isOverride, limit, offset);
        }
        using var command = Connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT {QualifiedNodeColumns} FROM nodes n
             JOIN edges e ON e.to_id = n.id
             WHERE e.from_id = $id AND e.kind = 'Contains'
               AND n.kind != 'Type'
               AND ($kind IS NULL OR n.kind = $kind)
               AND ($accessibility IS NULL OR n.accessibility = $accessibility)
               AND ($is_static IS NULL OR n.is_static = $is_static)
               AND ($is_abstract IS NULL OR n.is_abstract = $is_abstract)
               AND ($is_async IS NULL OR n.is_async = $is_async)
               AND ($is_virtual IS NULL OR n.is_virtual = $is_virtual)
               AND ($is_override IS NULL OR n.is_override = $is_override)
             ORDER BY n.start_line, n.name, n.id
             LIMIT $limit OFFSET $offset;
             """;
        command.Parameters.Add(P("id", typeId));
        command.Parameters.Add(P("kind", kind?.ToString()));
        command.Parameters.Add(P("accessibility", accessibility?.ToString()));
        command.Parameters.Add(P("is_static", isStatic.HasValue ? (object?)isStatic.Value : null));
        command.Parameters.Add(P("is_abstract", isAbstract.HasValue ? (object?)isAbstract.Value : null));
        command.Parameters.Add(P("is_async", isAsync.HasValue ? (object?)isAsync.Value : null));
        command.Parameters.Add(P("is_virtual", isVirtual.HasValue ? (object?)isVirtual.Value : null));
        command.Parameters.Add(P("is_override", isOverride.HasValue ? (object?)isOverride.Value : null));
        command.Parameters.Add(P("limit", Math.Max(1, limit)));
        command.Parameters.Add(P("offset", Math.Max(0, offset)));
        return ReadNodes(command);
    }

    /// <summary>Nodes that call <paramref name="id"/>, transitively up to <paramref name="maxDepth"/>.</summary>
    public IReadOnlyList<GraphHit> Callers(string id, int maxDepth = 1) =>
        _snapshot?.Callers(id, maxDepth) ?? Walk(id, maxDepth, inbound: true, [nameof(EdgeKind.Calls)], dispatch: true);

    /// <summary>
    /// Nodes that <paramref name="id"/> calls, transitively up to <paramref name="maxDepth"/>. Framework and NuGet
    /// targets (<see cref="NodeKind.External"/>) only with <paramref name="includeExternal"/>.
    /// </summary>
    public IReadOnlyList<GraphHit> Callees(string id, int maxDepth = 1, bool includeExternal = false)
    {
        if (_snapshot is not null)
        {
            return _snapshot.Callees(id, maxDepth, includeExternal);
        }
        return includeExternal
            ? Walk(id, maxDepth, inbound: false, [nameof(EdgeKind.Calls), nameof(EdgeKind.CallsExternal)])
            : Walk(id, maxDepth, inbound: false, [nameof(EdgeKind.Calls)]);
    }

    /// <summary>
    /// Everything that could be affected by changing <paramref name="id"/>: transitive callers,
    /// overriders, implementers and subclasses. Test methods in the result are the ones that guard it.
    /// </summary>
    public IReadOnlyList<GraphHit> Impact(string id, int maxDepth = 5) =>
        _snapshot?.Impact(id, maxDepth) ?? Walk(id, maxDepth, inbound: true, ImpactEdgeKinds, dispatch: true);

    /// <summary>All nodes, optionally filtered to one kind. Use sparingly - may return thousands of rows.</summary>
    public IReadOnlyList<GraphNode> AllNodes(NodeKind? kind = null)
    {
        if (_snapshot is not null)
        {
            return _snapshot.AllNodes(kind);
        }
        using var command = Connection.CreateCommand();
        if (kind is null)
        {
            command.CommandText = $"SELECT {NodeColumns} FROM nodes ORDER BY kind, full_name, id;";
        }
        else
        {
            command.CommandText = $"SELECT {NodeColumns} FROM nodes WHERE kind = $kind ORDER BY full_name, id;";
            command.Parameters.Add(P("kind", kind.ToString()));
        }
        return ReadNodes(command);
    }

    /// <summary>Every edge. Use sparingly - may return tens of thousands of rows.</summary>
    public IReadOnlyList<GraphEdge> AllEdges()
    {
        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT from_id, to_id, kind FROM edges ORDER BY kind, from_id, to_id;";
        var edges = new List<GraphEdge>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            edges.Add(new GraphEdge(reader.GetString(0), reader.GetString(1), Enum.Parse<EdgeKind>(reader.GetString(2))));
        }

        return edges;
    }

    /// <summary>All edges leaving <paramref name="id"/>, with the target node resolved.</summary>
    public IReadOnlyList<(EdgeKind Kind, GraphNode Target)> EdgesFrom(string id)
    {
        if (_snapshot is not null)
        {
            return _snapshot.EdgesFrom(id);
        }
        using var command = Connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT e.kind, {QualifiedNodeColumns} FROM edges e
             JOIN nodes n ON n.id = e.to_id
             WHERE e.from_id = $id
             ORDER BY e.kind, n.full_name, n.id;
             """;
        command.Parameters.Add(P("id", id));
        return ReadEdgeNodes(command);
    }

    /// <summary>
    /// Members and types that mention type <paramref name="id"/>, nearest file first, with the total before the
    /// cap so the caller can say "showing n of m" without pulling every row of a widely used type.
    /// </summary>
    public (IReadOnlyList<GraphNode> Nodes, int Total) ReferencesTo(string id, int limit)
    {
        if (_snapshot is not null)
        {
            return _snapshot.ReferencesTo(id, limit);
        }
        using var command = Connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT {QualifiedNodeColumns}, COUNT(*) OVER () AS total
             FROM edges e
             JOIN nodes n ON n.id = e.from_id
             WHERE e.to_id = $id AND e.kind = 'References'
             ORDER BY n.file_path, n.start_line, n.full_name, n.id
             LIMIT $limit;
             """;
        command.Parameters.Add(P("id", id));
        command.Parameters.Add(P("limit", Math.Max(1, limit)));

        var nodes = new List<GraphNode>();
        var total = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            nodes.Add(ReadNode(reader));
            total = reader.GetInt32(reader.FieldCount - 1);
        }

        return (nodes, total);
    }

    /// <summary>All edges arriving at <paramref name="id"/>, with the source node resolved.</summary>
    public IReadOnlyList<(EdgeKind Kind, GraphNode Source)> EdgesTo(string id)
    {
        if (_snapshot is not null)
        {
            return _snapshot.EdgesTo(id);
        }
        using var command = Connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT e.kind, {QualifiedNodeColumns} FROM edges e
             JOIN nodes n ON n.id = e.from_id
             WHERE e.to_id = $id
             ORDER BY e.kind, n.full_name, n.id;
             """;
        command.Parameters.Add(P("id", id));
        return ReadEdgeNodes(command);
    }

    /// <summary>Generic walk over any combination of edge kinds, in either direction.</summary>
    public IReadOnlyList<GraphHit> Walk(string id, string[] edgeKinds, bool inbound, int maxDepth) =>
        _snapshot?.Walk(id, edgeKinds, inbound, maxDepth) ?? Walk(id, maxDepth, inbound, edgeKinds);

    /// <summary>Test methods that call <paramref name="id"/> directly.</summary>
    public IReadOnlyList<GraphNode> DirectTests(string id)
    {
        if (_snapshot is not null)
        {
            return _snapshot.DirectTests(id);
        }
        using var command = Connection.CreateCommand();
        command.CommandText =
            $"""
             WITH RECURSIVE up(id) AS (
                 SELECT $id
                 UNION
                 SELECT e.to_id FROM edges e
                 JOIN up ON e.from_id = up.id
                 JOIN nodes m ON m.id = e.from_id AND m.kind <> 'Type'
                 WHERE e.kind IN ('Implements', 'Overrides')
             )
             SELECT DISTINCT {QualifiedNodeColumns} FROM nodes n
             JOIN edges e ON e.to_id = n.id
             WHERE e.from_id IN (SELECT id FROM up) AND e.kind = 'TestedBy'
             ORDER BY n.full_name, n.id;
             """;
        command.Parameters.Add(P("id", id));
        return ReadNodes(command);
    }

    /// <summary>
    /// Static reachability coverage: production methods reachable from tests via Calls edges.
    /// Returns (total production methods, covered by at least one test path, test method count).
    /// </summary>
    public (int Total, int Covered, int TestMethods) CoverageStats()
    {
        if (_snapshot is not null)
        {
            return _snapshot.CoverageStats();
        }
        using var command = Connection.CreateCommand();
        command.CommandText =
            $"""
            WITH RECURSIVE reach(id) AS (
                SELECT id FROM nodes WHERE is_test = true AND kind = 'Method'
                UNION
                SELECT s.next
                FROM reach r
                JOIN (
                    SELECT to_id AS next, from_id AS at FROM edges WHERE kind = 'Calls'
                    UNION ALL {DispatchDown}
                ) s ON s.at = r.id
            )
            SELECT
                (SELECT COUNT(*) FROM nodes WHERE kind = 'Method' AND is_test = false) AS total,
                (SELECT COUNT(*) FROM nodes n JOIN reach r ON n.id = r.id WHERE n.kind = 'Method' AND n.is_test = false) AS covered,
                (SELECT COUNT(*) FROM nodes WHERE kind = 'Method' AND is_test = true) AS test_methods;
            """;
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    /// <summary>
    /// All methods ranked by direct caller count, paged. Ties break by name so pages are stable.
    /// <paramref name="includeExternal"/> ranks framework and NuGet members too; <paramref name="nameFilter"/>
    /// keeps names containing it (<c>System.IO</c>: which file APIs the solution leans on).
    /// </summary>
    public IReadOnlyList<(GraphNode Method, int CallerCount)> MostCalled(int limit, int minCallers, int offset, bool includeExternal = false, string? nameFilter = null, string? language = null)
    {
        if (_snapshot is not null)
        {
            return _snapshot.MostCalled(limit, minCallers, offset, includeExternal, nameFilter, language);
        }
        using var command = Connection.CreateCommand();
        var kinds = includeExternal ? "('Method', 'External')" : "('Method')";
        command.CommandText =
            $"""
             SELECT {QualifiedNodeColumns}, COUNT(e.from_id) AS caller_count
             FROM nodes n
             LEFT JOIN edges e ON e.to_id = n.id AND e.kind IN ('Calls', 'CallsExternal')
             WHERE n.kind IN {kinds}
               AND ($name IS NULL OR n.full_name ILIKE $name ESCAPE '\')
               AND ($language IS NULL OR n.language = $language)
             GROUP BY {GroupByNodeColumns}
             HAVING COUNT(e.from_id) >= $min_callers
             ORDER BY caller_count DESC, n.full_name, n.id
             LIMIT $limit OFFSET $offset;
             """;
        command.Parameters.Add(P("min_callers", minCallers));
        command.Parameters.Add(P("name", nameFilter is null ? null : $"%{EscapeLike(nameFilter)}%"));
        command.Parameters.Add(P("language", language));
        command.Parameters.Add(P("limit", Math.Clamp(limit, 1, 100)));
        command.Parameters.Add(P("offset", Math.Max(0, offset)));
        var results = new List<(GraphNode, int)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add((ReadNode(reader), reader.GetInt32(reader.FieldCount - 1)));
        return results;
    }

    /// <summary>
    /// Production methods not reachable from any test via Calls edges.
    /// Optionally scoped to a file path substring.
    /// </summary>
    public IReadOnlyList<GraphNode> UncoveredMethods(string? filePattern = null, int limit = 200, int offset = 0)
    {
        if (_snapshot is not null)
        {
            return _snapshot.UncoveredMethods(filePattern, limit, offset);
        }
        using var command = Connection.CreateCommand();
        var fileFilter = filePattern is not null ? "AND file_path ILIKE $file" : "";
        command.CommandText =
            $"""
             WITH RECURSIVE reach(id) AS (
                 SELECT id FROM nodes WHERE is_test = true AND kind = 'Method'
                 UNION
                 SELECT s.next
                 FROM reach r
                 JOIN (
                     SELECT to_id AS next, from_id AS at FROM edges WHERE kind = 'Calls'
                     UNION ALL {DispatchDown}
                 ) s ON s.at = r.id
             )
             SELECT {NodeColumns} FROM nodes
             WHERE kind = 'Method' AND is_test = false
             AND id NOT IN (SELECT id FROM reach)
             {fileFilter}
             ORDER BY full_name, id
             LIMIT $limit OFFSET $offset;
             """;
        command.Parameters.Add(P("limit", limit));
        command.Parameters.Add(P("offset", Math.Max(0, offset)));
        if (filePattern is not null)
            command.Parameters.Add(P("file", $"%{filePattern}%"));
        return ReadNodes(command);
    }

    /// <summary>Test methods that transitively reach <paramref name="id"/> via Calls edges.</summary>
    public IReadOnlyList<GraphHit> TransitiveTests(string id, int maxDepth = 10)
    {
        if (_snapshot is not null)
        {
            return _snapshot.TransitiveTests(id, maxDepth);
        }
        using var command = Connection.CreateCommand();
        command.CommandText =
            $"""
             WITH RECURSIVE walk(id, depth, hop) AS (
                 {CallWalkSeed}
                 UNION
                 SELECT s.next, w.depth + s.step, s.hop
                 FROM walk w
                 JOIN (
                     SELECT from_id AS next, to_id AS at, 1 AS step, false AS hop FROM edges WHERE kind = 'Calls'
                     UNION ALL {DispatchUp}
                 ) s ON s.at = w.id
                 WHERE w.depth < $max
             )
             SELECT {QualifiedNodeColumns}, MIN(w.depth) AS depth
             FROM walk w
             JOIN nodes n ON n.id = w.id
             WHERE w.depth > 0 AND NOT w.hop AND n.is_test = true
             GROUP BY {GroupByNodeColumns}
             ORDER BY depth, n.full_name, n.id;
             """;
        command.Parameters.Add(P("id", id));
        command.Parameters.Add(P("max", maxDepth));
        var hits = new List<GraphHit>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            hits.Add(new GraphHit(ReadNode(reader), reader.GetInt32(reader.FieldCount - 1)));
        return hits;
    }

    /// <summary>
    /// Methods that appear never called in source, adjusted for dispatch and framework entrypoints. A member
    /// that implements or overrides a called member counts as called (the indexer's member-level Implements
    /// and Overrides edges, followed transitively), so an explicit interface implementation or an override two
    /// levels down is not reported just because nothing names it directly.
    /// </summary>
    public (DeadCodeSummary Summary, IReadOnlyList<DeadCodeCandidate> Candidates) DeadCodeReport(
        bool includePublicApi = false,
        int limit = 100,
        int offset = 0)
    {
        if (_snapshot is not null)
        {
            return _snapshot.DeadCodeReport(includePublicApi, limit, offset);
        }
        var accessibilityFilter = includePublicApi
            ? string.Empty
            : "AND n.accessibility IN ('Private', 'Internal')";

        var baseCte =
            $"""
             WITH RECURSIVE methods AS (
               SELECT n.id, n.accessibility
               FROM nodes n
               WHERE n.kind = 'Method'
                 AND n.member_kind = 'Ordinary'
                 AND n.is_test = false
                 AND n.name NOT LIKE '<%'
                 {accessibilityFilter}
             ),
             callers AS (
               SELECT to_id AS id, COUNT(*) AS direct_callers
               FROM edges
               WHERE kind = 'Calls'
               GROUP BY to_id
             ),
             framework_invoked AS (
               SELECT DISTINCT to_id AS id
               FROM edges
               WHERE kind = 'InvokedByFramework'
             ),
             dispatch_used(id) AS (
               SELECT id FROM callers
               UNION
               SELECT s.next FROM dispatch_used d
               JOIN ({DispatchDown}) s ON s.at = d.id
             )
             """;

        using var summaryCommand = Connection.CreateCommand();
        summaryCommand.CommandText =
            $"""
             {baseCte}
             SELECT
               COUNT(*) AS total_methods,
               SUM(CASE WHEN COALESCE(c.direct_callers, 0) = 0 THEN 1 ELSE 0 END) AS directly_uncalled,
               SUM(CASE WHEN COALESCE(c.direct_callers, 0) = 0 AND d.id IS NOT NULL THEN 1 ELSE 0 END) AS di_likely_used,
               SUM(CASE WHEN COALESCE(c.direct_callers, 0) = 0 AND f.id IS NOT NULL THEN 1 ELSE 0 END) AS framework_entrypoints,
               SUM(CASE WHEN COALESCE(c.direct_callers, 0) = 0 AND d.id IS NULL AND f.id IS NULL THEN 1 ELSE 0 END) AS likely_dead
             FROM methods m
             LEFT JOIN callers c ON c.id = m.id
             LEFT JOIN dispatch_used d ON d.id = m.id
             LEFT JOIN framework_invoked f ON f.id = m.id;
             """;

        DeadCodeSummary summary;
        using (var reader = summaryCommand.ExecuteReader())
        {
            reader.Read();
            summary = new DeadCodeSummary(
                TotalMethods: reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                DirectlyUncalled: reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                DiLikelyUsed: reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                FrameworkEntrypoints: reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                LikelyDead: reader.IsDBNull(4) ? 0 : reader.GetInt32(4));
        }

        using var candidatesCommand = Connection.CreateCommand();
        candidatesCommand.CommandText =
            $"""
             {baseCte}
             SELECT {QualifiedNodeColumns}
             FROM methods m
             LEFT JOIN callers c ON c.id = m.id
             LEFT JOIN dispatch_used d ON d.id = m.id
             LEFT JOIN framework_invoked f ON f.id = m.id
             JOIN nodes n ON n.id = m.id
             WHERE COALESCE(c.direct_callers, 0) = 0
               AND d.id IS NULL
               AND f.id IS NULL
             ORDER BY n.accessibility, n.file_path, n.start_line, n.id
             LIMIT $limit OFFSET $offset;
             """;
        candidatesCommand.Parameters.Add(P("limit", Math.Clamp(limit, 1, 500)));
        candidatesCommand.Parameters.Add(P("offset", Math.Max(0, offset)));

        var candidates = new List<DeadCodeCandidate>();
        using (var reader = candidatesCommand.ExecuteReader())
        {
            while (reader.Read())
            {
                var method = ReadNode(reader);
                var confidence = method.Accessibility is Accessibility.Private or Accessibility.Internal ? "high" : "medium";
                candidates.Add(new DeadCodeCandidate(
                    Method: method,
                    Confidence: confidence,
                    Reason: "no direct callers, implements or overrides nothing that is called, and no framework-entrypoint edge"));
            }
        }

        return (summary, candidates);
    }

    /// <summary>Methods flagged as framework/runtime entrypoints (for example, function triggers).</summary>
    public IReadOnlyList<GraphNode> RuntimeRoots(int limit = 200)
    {
        if (_snapshot is not null)
        {
            return _snapshot.RuntimeRoots(limit);
        }
        using var command = Connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT {QualifiedNodeColumns} FROM nodes n
             JOIN edges e ON e.to_id = n.id
             WHERE e.kind = 'InvokedByFramework'
             ORDER BY n.full_name, n.id
             LIMIT $limit;
             """;
        command.Parameters.Add(P("limit", Math.Clamp(limit, 1, 500)));
        return ReadNodes(command);
    }

    /// <summary>
    /// Human-readable explanation of why a method is likely reachable, or why it is a dead-code candidate.
    /// </summary>
    public string ExplainReachability(string id)
    {
        if (_snapshot is not null)
        {
            return _snapshot.ExplainReachability(id);
        }
        var node = GetNode(id);
        if (node is null)
            return $"no node with id '{id}'";

        var directCallers = CountInboundEdges(id, nameof(EdgeKind.Calls));
        if (directCallers > 0)
            return $"reachable: {directCallers} direct caller(s) in indexed source";

        if (HasInboundEdge(id, nameof(EdgeKind.InvokedByFramework)))
            return "reachable: marked as framework/runtime entrypoint";

        if (DispatchedThrough(id) is { } via)
            return $"likely reachable via dispatch: implements or overrides {via.FullName}, which has {via.Callers} caller(s)";

        var overrides = CountInboundEdges(id, nameof(EdgeKind.Overrides));
        if (overrides > 0)
            return $"possibly reachable via polymorphism: {overrides} override(s) in source";

        return "no direct callers and no dispatch/framework reachability evidence found";
    }

    private int CountInboundEdges(string id, string kind)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM edges WHERE to_id = $id AND kind = $kind;";
        command.Parameters.Add(P("id", id));
        command.Parameters.Add(P("kind", kind));
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    private bool HasInboundEdge(string id, string kind) => CountInboundEdges(id, kind) > 0;

    /// <summary>
    /// The nearest interface or base member that <paramref name="memberId"/> implements or overrides (transitively)
    /// and that something calls, with its caller count; null when there is none.
    /// </summary>
    private (string FullName, int Callers)? DispatchedThrough(string memberId)
    {
        using var command = Connection.CreateCommand();
        command.CommandText =
            $"""
             WITH RECURSIVE up(id, depth) AS (
                 SELECT $id, 0
                 UNION
                 SELECT e.to_id, up.depth + 1 FROM edges e
                 JOIN up ON e.from_id = up.id
                 WHERE e.kind IN ('Implements', 'Overrides') AND up.depth < 10
             )
             SELECT n.full_name, COUNT(c.from_id) AS callers
             FROM up
             JOIN nodes n ON n.id = up.id
             JOIN edges c ON c.to_id = up.id AND c.kind = 'Calls'
             WHERE up.depth > 0
             GROUP BY n.full_name, up.depth
             ORDER BY up.depth, n.full_name
             LIMIT 1;
             """;
        command.Parameters.Add(P("id", memberId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetInt32(1)) : null;
    }

    /// <summary>
    /// With <paramref name="dispatch"/>, an inbound walk also treats a call to an interface or base member as a
    /// call to every member that implements or overrides it: from each member reached, it hops to the members
    /// it implements or overrides at the same depth and continues from their callers. Those hops are followed
    /// but not reported, so the result holds only real callers.
    /// </summary>
    private IReadOnlyList<GraphHit> Walk(string id, int maxDepth, bool inbound, string[] edgeKinds, bool dispatch = false)
    {
        var (nextColumn, matchColumn) = inbound ? ("from_id", "to_id") : ("to_id", "from_id");
        var kindList = string.Join(", ", edgeKinds.Select((_, i) => $"$k{i}"));
        var dispatchStep = dispatch && inbound ? $"UNION ALL {DispatchUp}" : "";
        var anchor = inbound && edgeKinds.Contains(nameof(EdgeKind.Calls)) ? CallWalkSeed : "SELECT $id, 0, false";

        using var command = Connection.CreateCommand();
        command.CommandText =
            $"""
             WITH RECURSIVE walk(id, depth, hop) AS (
                 {anchor}
                 UNION
                 SELECT s.next, w.depth + s.step, s.hop
                 FROM walk w
                 JOIN (
                     SELECT {nextColumn} AS next, {matchColumn} AS at, 1 AS step, false AS hop
                     FROM edges WHERE kind IN ({kindList})
                     {dispatchStep}
                 ) s ON s.at = w.id
                 WHERE w.depth < $max
             )
             SELECT {QualifiedNodeColumns}, MIN(w.depth) AS depth
             FROM walk w
             JOIN nodes n ON n.id = w.id
             WHERE w.depth > 0 AND NOT w.hop
             GROUP BY {GroupByNodeColumns}
             ORDER BY depth, n.full_name, n.id;
             """;
        command.Parameters.Add(P("id", id));
        command.Parameters.Add(P("max", maxDepth));
        for (var i = 0; i < edgeKinds.Length; i++)
        {
            command.Parameters.Add(P($"k{i}", edgeKinds[i]));
        }

        var hits = new List<GraphHit>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            hits.Add(new GraphHit(ReadNode(reader), reader.GetInt32(reader.FieldCount - 1)));
        }

        return hits;
    }

    /// <summary>
    /// Shortest paths from <paramref name="fromId"/> to <paramref name="toId"/> over the given edge kinds, each ordered
    /// source to target: up to <paramref name="maxPaths"/> of them, all of the shortest length. A breadth-first search
    /// with one indexed query per level and every node visited once, then a walk back over the recorded parents.
    /// Enumerating every simple path in SQL took seconds on a 10,000-method graph; this is bounded by the nodes
    /// within the shortest distance.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<GraphNode>> PathBetween(
        string fromId, string toId, string[] edgeKinds, int maxDepth = 10, int maxPaths = 3)
    {
        if (_snapshot is not null)
        {
            return _snapshot.PathBetween(fromId, toId, edgeKinds, maxDepth, maxPaths);
        }
        if (fromId == toId)
        {
            return GetNode(fromId) is { } self ? [[self]] : [];
        }

        var kindList = string.Join(", ", edgeKinds.Select((_, i) => $"$k{i}"));
        var parents = new Dictionary<string, List<string>>(StringComparer.Ordinal) { [fromId] = [] };
        var frontier = new List<string> { fromId };
        var found = false;
        for (var depth = 0; depth < maxDepth && frontier.Count > 0 && !found; depth++)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = $"SELECT from_id, to_id FROM edges WHERE kind IN ({kindList}) AND from_id IN ({PathList});";
            command.Parameters.Add(P("paths", Lines(frontier)));
            for (var i = 0; i < edgeKinds.Length; i++)
            {
                command.Parameters.Add(P($"k{i}", edgeKinds[i]));
            }

            var level = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var (from, to) = (reader.GetString(0), reader.GetString(1));
                    if (parents.ContainsKey(to))
                    {
                        continue; // reached at a shorter distance already
                    }

                    if (!level.TryGetValue(to, out var list))
                    {
                        level[to] = list = [];
                    }

                    list.Add(from);
                }
            }

            foreach (var (node, from) in level)
            {
                parents[node] = from;
            }

            found = level.ContainsKey(toId);
            frontier = [.. level.Keys];
        }

        if (!found)
        {
            return [];
        }

        // Walk back from the target over every recorded parent, stopping once enough paths are complete.
        var paths = new List<List<string>>();
        var stack = new Stack<List<string>>([[toId]]);
        while (stack.Count > 0 && paths.Count < maxPaths)
        {
            var partial = stack.Pop();
            var head = partial[^1];
            if (head == fromId)
            {
                paths.Add(Enumerable.Reverse(partial).ToList());
                continue;
            }

            foreach (var parent in parents[head].OrderByDescending(p => p, StringComparer.Ordinal))
            {
                stack.Push([.. partial, parent]);
            }
        }

        var nodes = NodesById(paths.SelectMany(p => p).Distinct(StringComparer.Ordinal).ToList());
        return paths
            .Where(p => p.All(nodes.ContainsKey))
            .Select(p => (IReadOnlyList<GraphNode>)p.Select(id => nodes[id]).ToList())
            .ToList();
    }

    /// <summary>
    /// Neighbourhood subgraph: the starting node, everything reachable from it in both directions up to
    /// <paramref name="maxDepth"/> hops, plus all edges between those nodes. Capped at 150 nodes.
    /// </summary>
    public SubGraphResult SubGraph(string id, string[] edgeKinds, int maxDepth)
    {
        if (_snapshot is not null)
        {
            return _snapshot.SubGraph(id, edgeKinds, maxDepth);
        }
        var outHits = Walk(id, Math.Clamp(maxDepth, 1, 5), inbound: false, edgeKinds);
        var inHits  = Walk(id, Math.Clamp(maxDepth, 1, 5), inbound: true,  edgeKinds);
        var center  = GetNode(id);

        // The centre goes first so the cap never drops it; the nearest neighbours follow, since each walk
        // comes back ordered by depth.
        var allNodes = new[] { center }
            .OfType<GraphNode>()
            .Concat(outHits.Concat(inHits).OrderBy(h => h.Depth).Select(h => h.Node))
            .DistinctBy(n => n.Id)
            .Take(150)
            .ToList();

        var edges = GetEdgesBetween(allNodes.Select(n => n.Id).ToList(), edgeKinds);
        return new SubGraphResult(allNodes, edges);
    }

    private IReadOnlyList<SubGraphEdge> GetEdgesBetween(IReadOnlyList<string> nodeIds, string[] edgeKinds)
    {
        if (nodeIds.Count == 0) return [];

        // Named parameters: DuckDB reuses the same value wherever the same $name appears.
        var nodeParams = string.Join(", ", nodeIds.Select((_, i) => $"$n{i}"));
        var kindParams = string.Join(", ", edgeKinds.Select((_, i) => $"$ek{i}"));

        using var command = Connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT from_id, to_id, kind FROM edges
             WHERE from_id IN ({nodeParams})
               AND to_id   IN ({nodeParams})
               AND kind    IN ({kindParams});
             """;

        for (var i = 0; i < nodeIds.Count; i++)
            command.Parameters.Add(P($"n{i}", nodeIds[i]));
        for (var i = 0; i < edgeKinds.Length; i++)
            command.Parameters.Add(P($"ek{i}", edgeKinds[i]));

        var edges = new List<SubGraphEdge>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            edges.Add(new SubGraphEdge(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return edges;
    }

    // Column list for SELECT without table alias.
    private const string NodeColumns =
        "id, kind, name, full_name, signature, file_path, start_line, end_line, is_test, " +
        "type_kind, accessibility, is_static, is_abstract, is_virtual, is_sealed, is_async, is_override, member_kind, language";

    // Column list for JOIN queries where the nodes table is aliased 'n'.
    private const string QualifiedNodeColumns =
        "n.id, n.kind, n.name, n.full_name, n.signature, n.file_path, n.start_line, n.end_line, n.is_test, " +
        "n.type_kind, n.accessibility, n.is_static, n.is_abstract, n.is_virtual, n.is_sealed, n.is_async, n.is_override, n.member_kind, n.language";

    // DuckDB requires every non-aggregated SELECT column to appear in GROUP BY (no PK shortcut).
    private const string GroupByNodeColumns =
        "n.id, n.kind, n.name, n.full_name, n.signature, n.file_path, n.start_line, n.end_line, n.is_test, " +
        "n.type_kind, n.accessibility, n.is_static, n.is_abstract, n.is_virtual, n.is_sealed, n.is_async, n.is_override, n.member_kind, n.language";

    private static IReadOnlyList<GraphNode> ReadNodes(DuckDBCommand command)
    {
        var nodes = new List<GraphNode>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            nodes.Add(ReadNode(reader));
        }

        return nodes;
    }

    private static IReadOnlyList<(EdgeKind Kind, GraphNode Node)> ReadEdgeNodes(DuckDBCommand command)
    {
        var results = new List<(EdgeKind, GraphNode)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var kind = Enum.Parse<EdgeKind>(reader.GetString(0));
            var node = ReadNode(reader, offset: 1); // the edge kind comes first
            results.Add((kind, node));
        }
        return results;
    }

    private static GraphNode ReadNode(System.Data.Common.DbDataReader reader, int offset = 0) => new(
        Id: reader.GetString(offset),
        Kind: Enum.Parse<NodeKind>(reader.GetString(offset + 1)),
        Name: reader.GetString(offset + 2),
        FullName: reader.GetString(offset + 3),
        Signature: reader.IsDBNull(offset + 4) ? null : reader.GetString(offset + 4),
        FilePath: reader.IsDBNull(offset + 5) ? null : reader.GetString(offset + 5),
        StartLine: reader.GetInt32(offset + 6),
        EndLine: reader.GetInt32(offset + 7),
        IsTest: reader.GetBoolean(offset + 8),
        TypeKind: Enum.Parse<TypeKind>(reader.GetString(offset + 9)),
        Accessibility: Enum.Parse<Accessibility>(reader.GetString(offset + 10)),
        IsStatic: reader.GetBoolean(offset + 11),
        IsAbstract: reader.GetBoolean(offset + 12),
        IsVirtual: reader.GetBoolean(offset + 13),
        IsSealed: reader.GetBoolean(offset + 14),
        IsAsync: reader.GetBoolean(offset + 15),
        IsOverride: reader.GetBoolean(offset + 16),
        MemberKind: Enum.Parse<MemberKind>(reader.GetString(offset + 17)),
        Language: reader.GetString(offset + 18));

    private void EnsureSchema()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS nodes (
                id            VARCHAR PRIMARY KEY,
                kind          VARCHAR NOT NULL,
                name          VARCHAR NOT NULL,
                full_name     VARCHAR NOT NULL,
                signature     VARCHAR,
                file_path     VARCHAR,
                start_line    INTEGER NOT NULL,
                end_line      INTEGER NOT NULL,
                is_test       BOOLEAN NOT NULL DEFAULT false,
                type_kind     VARCHAR NOT NULL DEFAULT 'None',
                accessibility VARCHAR NOT NULL DEFAULT 'None',
                is_static     BOOLEAN NOT NULL DEFAULT false,
                is_abstract   BOOLEAN NOT NULL DEFAULT false,
                is_virtual    BOOLEAN NOT NULL DEFAULT false,
                is_sealed     BOOLEAN NOT NULL DEFAULT false,
                is_async      BOOLEAN NOT NULL DEFAULT false,
                is_override   BOOLEAN NOT NULL DEFAULT false,
                member_kind   VARCHAR NOT NULL DEFAULT 'Ordinary',
                language      VARCHAR NOT NULL DEFAULT 'csharp'
            )
            """);
        Execute("CREATE INDEX IF NOT EXISTS ix_nodes_name ON nodes (name)");
        Execute("CREATE INDEX IF NOT EXISTS ix_nodes_file ON nodes (file_path)");
        Execute(
            """
            CREATE TABLE IF NOT EXISTS edges (
                from_id VARCHAR NOT NULL,
                to_id   VARCHAR NOT NULL,
                kind    VARCHAR NOT NULL,
                PRIMARY KEY (from_id, to_id, kind)
            )
            """);
        Execute("CREATE INDEX IF NOT EXISTS ix_edges_to ON edges (to_id, kind)");
        Execute(
            """
            CREATE TABLE IF NOT EXISTS edge_origins (
                from_id   VARCHAR NOT NULL,
                to_id     VARCHAR NOT NULL,
                kind      VARCHAR NOT NULL,
                file_path VARCHAR NOT NULL,
                PRIMARY KEY (from_id, to_id, kind, file_path)
            )
            """);
        Execute("CREATE INDEX IF NOT EXISTS ix_edge_origins_file ON edge_origins (file_path)");
        Execute(
            """
            CREATE TABLE IF NOT EXISTS files (
                path        VARCHAR PRIMARY KEY,
                hash        VARCHAR NOT NULL,
                size        BIGINT NOT NULL,
                mtime_ticks BIGINT NOT NULL,
                language    VARCHAR NOT NULL DEFAULT 'csharp'
            )
            """);
        Execute("CREATE TABLE IF NOT EXISTS meta (key VARCHAR PRIMARY KEY, value VARCHAR NOT NULL)");
        Execute(
            """
            CREATE TABLE IF NOT EXISTS search_docs (
                node_id VARCHAR PRIMARY KEY,
                length  INTEGER NOT NULL
            )
            """);
        Execute(
            """
            CREATE TABLE IF NOT EXISTS search_terms (
                node_id VARCHAR NOT NULL,
                term    VARCHAR NOT NULL,
                tf      INTEGER NOT NULL,
                PRIMARY KEY (node_id, term)
            )
            """);
        Execute("CREATE INDEX IF NOT EXISTS ix_search_terms_term ON search_terms (term)");
        Execute(
            """
            CREATE TABLE IF NOT EXISTS embeddings (
                node_id   VARCHAR PRIMARY KEY,
                text_hash VARCHAR NOT NULL,
                vector    BLOB NOT NULL
            )
            """);

        // Idempotent column additions for databases created before this schema version.
        EnsureColumn("nodes", "type_kind",    "VARCHAR DEFAULT 'None'");
        EnsureColumn("nodes", "accessibility", "VARCHAR DEFAULT 'None'");
        EnsureColumn("nodes", "is_static",    "BOOLEAN DEFAULT false");
        EnsureColumn("nodes", "is_abstract",  "BOOLEAN DEFAULT false");
        EnsureColumn("nodes", "is_virtual",   "BOOLEAN DEFAULT false");
        EnsureColumn("nodes", "is_sealed",    "BOOLEAN DEFAULT false");
        EnsureColumn("nodes", "is_async",     "BOOLEAN DEFAULT false");
        EnsureColumn("nodes", "is_override",  "BOOLEAN DEFAULT false");
        EnsureColumn("nodes", "member_kind",  "VARCHAR DEFAULT 'Ordinary'");
        EnsureColumn("nodes", "language",     "VARCHAR DEFAULT 'csharp'");
        EnsureColumn("files", "language",     "VARCHAR DEFAULT 'csharp'");
    }

    private void EnsureColumn(string table, string column, string definition)
    {
        Execute($"ALTER TABLE {table} ADD COLUMN IF NOT EXISTS {column} {definition};");
    }

    private void Execute(string sql, DuckDBTransaction? transaction = null, string? paths = null, string? language = null)
    {
        using var command = Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (paths is not null)
        {
            command.Parameters.Add(P("paths", paths));
        }

        if (language is not null)
        {
            command.Parameters.Add(P("language", language));
        }

        command.ExecuteNonQuery();
    }

    private bool TableExists(string table)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM duckdb_tables() WHERE table_name = $table;";
        command.Parameters.Add(P("table", table));
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    // Path sets travel as one newline-joined parameter split server-side: works on read-only
    // connections (no temp table) and avoids thousands of positional parameters.
    private const string PathList = "SELECT unnest(string_split($paths, chr(10)))";

    /// <summary>
    /// Step rows (next, at, step, hop) that take a walk from a member up to the interface or base members it
    /// implements or overrides, at the same depth and flagged as a hop. A recursive CTE may reference itself
    /// once per step, so these are unioned with the ordinary edge rows rather than added as a second branch.
    /// Type-level Implements/Inherits edges are excluded: a class is not "called".
    /// </summary>
    private const string DispatchUp =
        """
        SELECT e.to_id AS next, e.from_id AS at, 0 AS step, true AS hop
        FROM edges e
        JOIN nodes m ON m.id = e.from_id AND m.kind <> 'Type'
        WHERE e.kind IN ('Implements', 'Overrides')
        """;

    /// <summary>The forward counterpart (next, at): from an interface or base member down to every implementation or override.</summary>
    private const string DispatchDown =
        """
        SELECT e.from_id AS next, e.to_id AS at
        FROM edges e
        JOIN nodes m ON m.id = e.from_id AND m.kind <> 'Type'
        WHERE e.kind IN ('Implements', 'Overrides')
        """;

    /// <summary>
    /// The anchor row of an inbound call walk. An <see cref="NodeKind.External"/> node's callers arrive over
    /// CallsExternal edges, which the walk's edge scan leaves out to stay as cheap as it was; so they are seeded
    /// here at depth 1 through the to_id index, and the walk continues from them over Calls as usual.
    /// </summary>
    private const string CallWalkSeed =
        """
        SELECT * FROM (
            SELECT $id AS id, 0 AS depth, false AS hop
            UNION ALL
            SELECT from_id, 1, false FROM edges WHERE kind = 'CallsExternal' AND to_id = $id AND $max >= 1
        )
        """;

    private static string Lines(IEnumerable<string> paths) => string.Join('\n', paths);

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static DuckDBParameter P(string name, object? value) =>
        new() { ParameterName = name, Value = value };
}
