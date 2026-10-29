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
    ];

    // References only ever points at types, so it widens impact_of(type) without changing member walks.
    private static readonly string[] ImpactEdgeKinds =
    [
        nameof(EdgeKind.Calls),
        nameof(EdgeKind.Overrides),
        nameof(EdgeKind.Implements),
        nameof(EdgeKind.Inherits),
        nameof(EdgeKind.References),
    ];

    /// <summary>Bumped when the indexer's output changes shape; a mismatch forces a full rebuild.</summary>
    public const string SchemaVersion = "3";

    private readonly DuckDBConnection _connection;

    public GraphStore(string dbPath, bool readOnly = false)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connStr = readOnly
            ? $"Data Source={dbPath};ACCESS_MODE=READ_ONLY"
            : $"Data Source={dbPath}";
        _connection = new DuckDBConnection(connStr);
        _connection.Open();
        if (!readOnly)
            EnsureSchema();
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>
    /// Opens a store, retrying while another process holds the DuckDB file lock. DuckDB allows one writer
    /// or many readers per file, so the indexer and the MCP servers take turns.
    /// </summary>
    public static GraphStore Open(string dbPath, bool readOnly, TimeSpan wait)
    {
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
        Execute("DELETE FROM edge_origins;");
        Execute("DELETE FROM edges;");
        Execute("DELETE FROM nodes;");
        Execute("DELETE FROM files;");
    }

    public void Write(IEnumerable<GraphNode> nodes, IEnumerable<GraphEdge> edges)
    {
        using var transaction = _connection.BeginTransaction();
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
        IEnumerable<SearchDocument>? search = null)
    {
        using var transaction = _connection.BeginTransaction();

        if (full)
        {
            foreach (var table in new[] { "search_terms", "search_docs", "edge_origins", "edges", "nodes", "files" })
            {
                Execute($"DELETE FROM {table};", transaction);
            }
        }
        else if (removedFiles.Count > 0)
        {
            var removed = Lines(removedFiles);
            Execute($"DELETE FROM search_terms WHERE node_id IN (SELECT id FROM nodes WHERE file_path IN ({PathList}));", transaction, removed);
            Execute($"DELETE FROM search_docs WHERE node_id IN (SELECT id FROM nodes WHERE file_path IN ({PathList}));", transaction, removed);
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
        if (search is not null)
        {
            WriteSearch(transaction, search);
        }

        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO files (path, hash, size, mtime_ticks) VALUES ($path, $hash, $size, $mtime)
                ON CONFLICT (path) DO UPDATE SET hash = excluded.hash, size = excluded.size, mtime_ticks = excluded.mtime_ticks;
                """;
            foreach (var file in files)
            {
                command.Parameters.Clear();
                command.Parameters.Add(P("path", file.Path));
                command.Parameters.Add(P("hash", file.Hash));
                command.Parameters.Add(P("size", file.Size));
                command.Parameters.Add(P("mtime", file.MtimeTicks));
                command.ExecuteNonQuery();
            }
        }

        using (var command = _connection.CreateCommand())
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
    }

    /// <summary>Index metadata: schema version, solution, git commit, time. Empty for a database from before metadata existed.</summary>
    public IReadOnlyDictionary<string, string> ReadMeta()
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!TableExists("meta"))
        {
            return meta;
        }

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM meta;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            meta[reader.GetString(0)] = reader.GetString(1);
        }

        return meta;
    }

    /// <summary>The stamp of every source file as of the last index, keyed by path.</summary>
    public IReadOnlyDictionary<string, FileStamp> IndexedFiles()
    {
        var files = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        if (!TableExists("files"))
        {
            return files;
        }

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT path, hash, size, mtime_ticks FROM files;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var stamp = new FileStamp(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3));
            files[stamp.Path] = stamp;
        }

        return files;
    }

    /// <summary>Paths of the project files (.csproj) behind every Project node.</summary>
    public IReadOnlyList<string> ProjectFilePaths()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT file_path FROM nodes WHERE kind = 'Project' AND file_path IS NOT NULL;";
        var paths = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
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

        using var command = _connection.CreateCommand();
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

    private void WriteCore(DuckDBTransaction transaction, IEnumerable<GraphNode> nodes, IEnumerable<GraphEdge> edges, IEnumerable<EdgeOrigin> origins)
    {
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO nodes
                    (id, kind, name, full_name, signature, file_path, start_line, end_line, is_test,
                     type_kind, accessibility, is_static, is_abstract, is_virtual, is_sealed, is_async, is_override, member_kind)
                VALUES
                    ($id, $kind, $name, $full_name, $signature, $file_path, $start_line, $end_line, $is_test,
                     $type_kind, $accessibility, $is_static, $is_abstract, $is_virtual, $is_sealed, $is_async, $is_override, $member_kind)
                ON CONFLICT (id) DO UPDATE SET
                    kind = excluded.kind, name = excluded.name, full_name = excluded.full_name,
                    signature = excluded.signature, file_path = excluded.file_path,
                    start_line = excluded.start_line, end_line = excluded.end_line,
                    is_test = excluded.is_test, type_kind = excluded.type_kind,
                    accessibility = excluded.accessibility, is_static = excluded.is_static,
                    is_abstract = excluded.is_abstract, is_virtual = excluded.is_virtual,
                    is_sealed = excluded.is_sealed, is_async = excluded.is_async,
                    is_override = excluded.is_override, member_kind = excluded.member_kind;
                """;

            foreach (var node in nodes)
            {
                command.Parameters.Clear();
                command.Parameters.Add(P("id", node.Id));
                command.Parameters.Add(P("kind", node.Kind.ToString()));
                command.Parameters.Add(P("name", node.Name));
                command.Parameters.Add(P("full_name", node.FullName));
                command.Parameters.Add(P("signature", node.Signature));
                command.Parameters.Add(P("file_path", node.FilePath));
                command.Parameters.Add(P("start_line", node.StartLine));
                command.Parameters.Add(P("end_line", node.EndLine));
                command.Parameters.Add(P("is_test", node.IsTest));
                command.Parameters.Add(P("type_kind", node.TypeKind.ToString()));
                command.Parameters.Add(P("accessibility", node.Accessibility.ToString()));
                command.Parameters.Add(P("is_static", node.IsStatic));
                command.Parameters.Add(P("is_abstract", node.IsAbstract));
                command.Parameters.Add(P("is_virtual", node.IsVirtual));
                command.Parameters.Add(P("is_sealed", node.IsSealed));
                command.Parameters.Add(P("is_async", node.IsAsync));
                command.Parameters.Add(P("is_override", node.IsOverride));
                command.Parameters.Add(P("member_kind", node.MemberKind.ToString()));
                command.ExecuteNonQuery();
            }
        }

        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO edges (from_id, to_id, kind) VALUES ($from, $to, $kind) ON CONFLICT DO NOTHING;";

            foreach (var edge in edges)
            {
                command.Parameters.Clear();
                command.Parameters.Add(P("from", edge.FromId));
                command.Parameters.Add(P("to", edge.ToId));
                command.Parameters.Add(P("kind", edge.Kind.ToString()));
                command.ExecuteNonQuery();
            }
        }

        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO edge_origins (from_id, to_id, kind, file_path) VALUES ($from, $to, $kind, $file) ON CONFLICT DO NOTHING;";

            foreach (var origin in origins)
            {
                command.Parameters.Clear();
                command.Parameters.Add(P("from", origin.Edge.FromId));
                command.Parameters.Add(P("to", origin.Edge.ToId));
                command.Parameters.Add(P("kind", origin.Edge.Kind.ToString()));
                command.Parameters.Add(P("file", origin.FilePath));
                command.ExecuteNonQuery();
            }
        }
    }

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
        using (var appender = _connection.CreateAppender("search_docs"))
        {
            foreach (var document in list)
            {
                appender.CreateRow().AppendValue(document.NodeId).AppendValue(document.Length).EndRow();
            }
        }

        using (var appender = _connection.CreateAppender("search_terms"))
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

    /// <summary>Number of nodes with a full-text document; zero for a graph built before search existed.</summary>
    public int SearchDocumentCount()
    {
        if (!TableExists("search_docs"))
        {
            return 0;
        }

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM search_docs;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>
    /// "Where's the code that does X": BM25 over the names, signatures, doc comments and body identifiers the
    /// indexer tokenized. Nodes matching more of the query's terms rank first, then by score.
    /// </summary>
    public IReadOnlyList<SearchHit> Search(string query, NodeKind? kind = null, string? filePattern = null, int limit = 20)
    {
        var terms = SearchText.QueryTerms(query);
        if (terms.Count == 0 || !TableExists("search_terms"))
        {
            return [];
        }

        using var command = _connection.CreateCommand();
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
             ORDER BY sc.matched DESC, sc.score DESC, n.full_name
             LIMIT $limit;
             """;
        command.Parameters.Add(P("paths", Lines(terms)));
        command.Parameters.Add(P("kind", kind?.ToString()));
        command.Parameters.Add(P("file", filePattern is null ? null : $"%{filePattern}%"));
        command.Parameters.Add(P("limit", Math.Clamp(limit, 1, 200)));

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
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM nodes), (SELECT COUNT(*) FROM edges);";
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    public GraphNode? GetNode(string id)
    {
        using var command = _connection.CreateCommand();
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
        int offset = 0)
    {
        var pattern = nameMatch.ToLowerInvariant() switch
        {
            "startswith" => $"{query}%",
            "endswith"   => $"%{query}",
            "exact"      => query,
            _            => $"%{query}%",  // contains
        };
        using var command = _connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT {NodeColumns} FROM nodes
             WHERE (name ILIKE $pattern OR full_name ILIKE $pattern)
               AND ($kind IS NULL OR kind = $kind)
               AND ($type_kind IS NULL OR type_kind = $type_kind)
               AND ($accessibility IS NULL OR accessibility = $accessibility)
               AND ($is_static IS NULL OR is_static = $is_static)
               AND ($is_abstract IS NULL OR is_abstract = $is_abstract)
               AND ($is_virtual IS NULL OR is_virtual = $is_virtual)
               AND ($is_async IS NULL OR is_async = $is_async)
               AND ($is_override IS NULL OR is_override = $is_override)
               AND ($member_kind IS NULL OR member_kind = $member_kind)
             ORDER BY
               CASE WHEN LOWER(name) = LOWER($exact) THEN 0 ELSE 1 END,
               LENGTH(name),
               full_name
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
        return ReadNodes(command);
    }

    /// <summary>Direct children via Contains edges (project to files, file to types, type to members).</summary>
    public IReadOnlyList<GraphNode> Children(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT {QualifiedNodeColumns} FROM nodes n
             JOIN edges e ON e.to_id = n.id
             WHERE e.from_id = $id AND e.kind = 'Contains'
             ORDER BY n.start_line, n.name;
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
        using var command = _connection.CreateCommand();
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
             ORDER BY n.start_line, n.name
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
        Walk(id, maxDepth, inbound: true, [nameof(EdgeKind.Calls)]);

    /// <summary>Nodes that <paramref name="id"/> calls, transitively up to <paramref name="maxDepth"/>.</summary>
    public IReadOnlyList<GraphHit> Callees(string id, int maxDepth = 1) =>
        Walk(id, maxDepth, inbound: false, [nameof(EdgeKind.Calls)]);

    /// <summary>
    /// Everything that could be affected by changing <paramref name="id"/>: transitive callers,
    /// overriders, implementers and subclasses. Test methods in the result are the ones that guard it.
    /// </summary>
    public IReadOnlyList<GraphHit> Impact(string id, int maxDepth = 5) =>
        Walk(id, maxDepth, inbound: true, ImpactEdgeKinds);

    /// <summary>All nodes, optionally filtered to one kind. Use sparingly - may return thousands of rows.</summary>
    public IReadOnlyList<GraphNode> AllNodes(NodeKind? kind = null)
    {
        using var command = _connection.CreateCommand();
        if (kind is null)
        {
            command.CommandText = $"SELECT {NodeColumns} FROM nodes ORDER BY kind, full_name;";
        }
        else
        {
            command.CommandText = $"SELECT {NodeColumns} FROM nodes WHERE kind = $kind ORDER BY full_name;";
            command.Parameters.Add(P("kind", kind.ToString()));
        }
        return ReadNodes(command);
    }

    /// <summary>Every edge. Use sparingly - may return tens of thousands of rows.</summary>
    public IReadOnlyList<GraphEdge> AllEdges()
    {
        using var command = _connection.CreateCommand();
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
        using var command = _connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT e.kind, {QualifiedNodeColumns} FROM edges e
             JOIN nodes n ON n.id = e.to_id
             WHERE e.from_id = $id
             ORDER BY e.kind, n.full_name;
             """;
        command.Parameters.Add(P("id", id));
        return ReadEdgeNodes(command);
    }

    /// <summary>All edges arriving at <paramref name="id"/>, with the source node resolved.</summary>
    public IReadOnlyList<(EdgeKind Kind, GraphNode Source)> EdgesTo(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT e.kind, {QualifiedNodeColumns} FROM edges e
             JOIN nodes n ON n.id = e.from_id
             WHERE e.to_id = $id
             ORDER BY e.kind, n.full_name;
             """;
        command.Parameters.Add(P("id", id));
        return ReadEdgeNodes(command);
    }

    /// <summary>Generic walk over any combination of edge kinds, in either direction.</summary>
    public IReadOnlyList<GraphHit> Walk(string id, string[] edgeKinds, bool inbound, int maxDepth) =>
        Walk(id, maxDepth, inbound, edgeKinds);

    /// <summary>Test methods that call <paramref name="id"/> directly.</summary>
    public IReadOnlyList<GraphNode> DirectTests(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT {QualifiedNodeColumns} FROM nodes n
             JOIN edges e ON e.to_id = n.id
             WHERE e.from_id = $id AND e.kind = 'TestedBy'
             ORDER BY n.full_name;
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
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            WITH RECURSIVE reach(id) AS (
                SELECT id FROM nodes WHERE is_test = true AND kind = 'Method'
                UNION
                SELECT e.to_id FROM edges e
                JOIN reach r ON e.from_id = r.id
                WHERE e.kind = 'Calls'
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

    /// <summary>All methods ranked by direct caller count, paged.</summary>
    public IReadOnlyList<(GraphNode Method, int CallerCount)> MostCalled(int limit, int minCallers, int offset)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT {NodeColumns}, COUNT(e.from_id) AS caller_count
             FROM nodes
             LEFT JOIN edges e ON e.to_id = id AND e.kind = 'Calls'
             WHERE kind = 'Method'
             GROUP BY {NodeColumns}
             HAVING COUNT(e.from_id) >= $min_callers
             ORDER BY caller_count DESC
             LIMIT $limit OFFSET $offset;
             """;
        command.Parameters.Add(P("min_callers", minCallers));
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
        using var command = _connection.CreateCommand();
        var fileFilter = filePattern is not null ? "AND file_path ILIKE $file" : "";
        command.CommandText =
            $"""
             WITH RECURSIVE reach(id) AS (
                 SELECT id FROM nodes WHERE is_test = true AND kind = 'Method'
                 UNION
                 SELECT e.to_id FROM edges e
                 JOIN reach r ON e.from_id = r.id
                 WHERE e.kind = 'Calls'
             )
             SELECT {NodeColumns} FROM nodes
             WHERE kind = 'Method' AND is_test = false
             AND id NOT IN (SELECT id FROM reach)
             {fileFilter}
             ORDER BY full_name
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
        using var command = _connection.CreateCommand();
        command.CommandText =
            $"""
             WITH RECURSIVE walk(id, depth) AS (
                 SELECT $id, 0
                 UNION
                 SELECT e.from_id, w.depth + 1
                 FROM edges e
                 JOIN walk w ON e.to_id = w.id
                 WHERE e.kind = 'Calls' AND w.depth < $max
             )
             SELECT {QualifiedNodeColumns}, MIN(w.depth) AS depth
             FROM walk w
             JOIN nodes n ON n.id = w.id
             WHERE w.depth > 0 AND n.is_test = true
             GROUP BY {GroupByNodeColumns}
             ORDER BY depth, n.full_name;
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
    /// Methods that appear never called in source, adjusted for interface dispatch and framework entrypoints.
    /// </summary>
    public (DeadCodeSummary Summary, IReadOnlyList<DeadCodeCandidate> Candidates) DeadCodeReport(
        bool includePublicApi = false,
        int limit = 100,
        int offset = 0)
    {
        var accessibilityFilter = includePublicApi
            ? string.Empty
            : "AND n.accessibility IN ('Private', 'Internal')";

        var baseCte =
            $"""
             WITH methods AS (
               SELECT n.id, n.name, n.full_name, n.file_path, n.start_line, n.end_line, n.accessibility,
                      n.kind, n.is_test, n.member_kind, n.type_kind, n.is_static, n.is_abstract,
                      n.is_virtual, n.is_sealed, n.is_async, n.is_override
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
             impl_pairs AS (
               SELECT e.from_id AS class_id, e.to_id AS iface_id
               FROM edges e
               JOIN nodes c ON c.id = e.from_id AND c.kind = 'Type' AND c.type_kind = 'Class'
               JOIN nodes i ON i.id = e.to_id AND i.kind = 'Type' AND i.type_kind = 'Interface'
               WHERE e.kind = 'Implements'
             ),
             class_methods AS (
               SELECT p.class_id, p.iface_id, m.id AS class_method_id, m.name AS method_name
               FROM impl_pairs p
               JOIN edges ce ON ce.kind = 'Contains' AND ce.from_id = p.class_id
               JOIN nodes m ON m.id = ce.to_id AND m.kind = 'Method' AND m.member_kind = 'Ordinary'
             ),
             interface_methods AS (
               SELECT p.iface_id, im.id AS interface_method_id, im.name AS method_name
               FROM impl_pairs p
               JOIN edges ce ON ce.kind = 'Contains' AND ce.from_id = p.iface_id
               JOIN nodes im ON im.id = ce.to_id AND im.kind = 'Method' AND im.member_kind = 'Ordinary'
             ),
             di_dispatch_used AS (
               SELECT DISTINCT cm.class_method_id AS id
               FROM class_methods cm
               JOIN interface_methods im
                 ON im.iface_id = cm.iface_id
                AND im.method_name = cm.method_name
               JOIN callers c ON c.id = im.interface_method_id
             )
             """;

        using var summaryCommand = _connection.CreateCommand();
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
             LEFT JOIN di_dispatch_used d ON d.id = m.id
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

        using var candidatesCommand = _connection.CreateCommand();
        candidatesCommand.CommandText =
            $"""
             {baseCte}
             SELECT {QualifiedNodeColumns}
             FROM methods m
             LEFT JOIN callers c ON c.id = m.id
             LEFT JOIN di_dispatch_used d ON d.id = m.id
             LEFT JOIN framework_invoked f ON f.id = m.id
             JOIN nodes n ON n.id = m.id
             WHERE COALESCE(c.direct_callers, 0) = 0
               AND d.id IS NULL
               AND f.id IS NULL
             ORDER BY n.accessibility, n.file_path, n.start_line
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
                    Reason: "no direct callers, no interface-dispatch evidence, and no framework-entrypoint edge"));
            }
        }

        return (summary, candidates);
    }

    /// <summary>Methods flagged as framework/runtime entrypoints (for example, function triggers).</summary>
    public IReadOnlyList<GraphNode> RuntimeRoots(int limit = 200)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT {QualifiedNodeColumns} FROM nodes n
             JOIN edges e ON e.to_id = n.id
             WHERE e.kind = 'InvokedByFramework'
             ORDER BY n.full_name
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
        var node = GetNode(id);
        if (node is null)
            return $"no node with id '{id}'";

        var directCallers = CountInboundEdges(id, nameof(EdgeKind.Calls));
        if (directCallers > 0)
            return $"reachable: {directCallers} direct caller(s) in indexed source";

        if (HasInboundEdge(id, nameof(EdgeKind.InvokedByFramework)))
            return "reachable: marked as framework/runtime entrypoint";

        if (IsLikelyInterfaceDispatchTarget(id))
            return "likely reachable via interface dispatch (interface method has callers)";

        var overrides = CountInboundEdges(id, nameof(EdgeKind.Overrides));
        if (overrides > 0)
            return $"possibly reachable via polymorphism: {overrides} override(s) in source";

        return "no direct callers and no DI/framework reachability evidence found";
    }

    private int CountInboundEdges(string id, string kind)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM edges WHERE to_id = $id AND kind = $kind;";
        command.Parameters.Add(P("id", id));
        command.Parameters.Add(P("kind", kind));
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    private bool HasInboundEdge(string id, string kind) => CountInboundEdges(id, kind) > 0;

    private bool IsLikelyInterfaceDispatchTarget(string methodId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            WITH containing_type AS (
                SELECT e.from_id AS class_id, m.name AS method_name
                FROM edges e
                JOIN nodes m ON m.id = e.to_id
                WHERE e.kind = 'Contains'
                  AND e.to_id = $id
                  AND m.kind = 'Method'
            ),
            class_ifaces AS (
                SELECT c.class_id, e.to_id AS iface_id, c.method_name
                FROM containing_type c
                JOIN edges e ON e.kind = 'Implements' AND e.from_id = c.class_id
            ),
            iface_methods AS (
                SELECT ci.class_id, im.id AS iface_method_id
                FROM class_ifaces ci
                JOIN edges ce ON ce.kind = 'Contains' AND ce.from_id = ci.iface_id
                JOIN nodes im ON im.id = ce.to_id
                WHERE im.kind = 'Method' AND im.name = ci.method_name
            )
            SELECT COUNT(*)
            FROM iface_methods im
            JOIN edges calls ON calls.kind = 'Calls' AND calls.to_id = im.iface_method_id;
            """;
        command.Parameters.Add(P("id", methodId));
        var value = command.ExecuteScalar();
        return value is not null and not DBNull && Convert.ToInt32(value) > 0;
    }

    private IReadOnlyList<GraphHit> Walk(string id, int maxDepth, bool inbound, string[] edgeKinds)
    {
        var (nextColumn, matchColumn) = inbound ? ("from_id", "to_id") : ("to_id", "from_id");
        var kindList = string.Join(", ", edgeKinds.Select((_, i) => $"$k{i}"));

        using var command = _connection.CreateCommand();
        command.CommandText =
            $"""
             WITH RECURSIVE walk(id, depth) AS (
                 SELECT $id, 0
                 UNION
                 SELECT e.{nextColumn}, w.depth + 1
                 FROM edges e
                 JOIN walk w ON e.{matchColumn} = w.id
                 WHERE e.kind IN ({kindList}) AND w.depth < $max
             )
             SELECT {QualifiedNodeColumns}, MIN(w.depth) AS depth
             FROM walk w
             JOIN nodes n ON n.id = w.id
             WHERE w.depth > 0
             GROUP BY {GroupByNodeColumns}
             ORDER BY depth, n.full_name;
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
    /// BFS shortest paths from <paramref name="fromId"/> to <paramref name="toId"/> via the given edge kinds.
    /// Returns up to <paramref name="maxPaths"/> paths, each ordered source-to-target.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<GraphNode>> PathBetween(
        string fromId, string toId, string[] edgeKinds, int maxDepth = 10, int maxPaths = 3)
    {
        var kindList = string.Join(", ", edgeKinds.Select((_, i) => $"$k{i}"));

        using var command = _connection.CreateCommand();
        command.CommandText =
            $"""
             WITH RECURSIVE bfs(id, path, depth) AS (
                 SELECT $from, $from, 0
                 UNION ALL
                 SELECT e.to_id, b.path || '§' || e.to_id, b.depth + 1
                 FROM edges e
                 JOIN bfs b ON e.from_id = b.id
                 WHERE e.kind IN ({kindList})
                   AND strpos('§' || b.path || '§', '§' || e.to_id || '§') = 0
                   AND b.depth < $max
             )
             SELECT path, depth FROM bfs
             WHERE id = $to
             ORDER BY depth, path
             LIMIT $limit;
             """;
        command.Parameters.Add(P("from", fromId));
        command.Parameters.Add(P("to", toId));
        command.Parameters.Add(P("max", maxDepth));
        command.Parameters.Add(P("limit", maxPaths));
        for (var i = 0; i < edgeKinds.Length; i++)
            command.Parameters.Add(P($"k{i}", edgeKinds[i]));

        var results = new List<IReadOnlyList<GraphNode>>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var ids = reader.GetString(0).Split('§');
            var nodes = ids.Select(id => GetNode(id)).OfType<GraphNode>().ToList();
            if (nodes.Count == ids.Length)
                results.Add(nodes);
        }
        return results;
    }

    /// <summary>
    /// Neighbourhood subgraph: the starting node, everything reachable from it in both directions up to
    /// <paramref name="maxDepth"/> hops, plus all edges between those nodes. Capped at 150 nodes.
    /// </summary>
    public SubGraphResult SubGraph(string id, string[] edgeKinds, int maxDepth)
    {
        var outHits = Walk(id, Math.Clamp(maxDepth, 1, 5), inbound: false, edgeKinds);
        var inHits  = Walk(id, Math.Clamp(maxDepth, 1, 5), inbound: true,  edgeKinds);
        var center  = GetNode(id);

        var allNodes = outHits.Select(h => h.Node)
            .Concat(inHits.Select(h => h.Node))
            .Append(center)
            .OfType<GraphNode>()
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

        using var command = _connection.CreateCommand();
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
        "type_kind, accessibility, is_static, is_abstract, is_virtual, is_sealed, is_async, is_override, member_kind";

    // Column list for JOIN queries where the nodes table is aliased 'n'.
    private const string QualifiedNodeColumns =
        "n.id, n.kind, n.name, n.full_name, n.signature, n.file_path, n.start_line, n.end_line, n.is_test, " +
        "n.type_kind, n.accessibility, n.is_static, n.is_abstract, n.is_virtual, n.is_sealed, n.is_async, n.is_override, n.member_kind";

    // DuckDB requires every non-aggregated SELECT column to appear in GROUP BY (no PK shortcut).
    private const string GroupByNodeColumns =
        "n.id, n.kind, n.name, n.full_name, n.signature, n.file_path, n.start_line, n.end_line, n.is_test, " +
        "n.type_kind, n.accessibility, n.is_static, n.is_abstract, n.is_virtual, n.is_sealed, n.is_async, n.is_override, n.member_kind";

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
            // columns 1..18 are the node columns (offset by 1 for the edge kind column)
            var node = new GraphNode(
                Id: reader.GetString(1),
                Kind: Enum.Parse<NodeKind>(reader.GetString(2)),
                Name: reader.GetString(3),
                FullName: reader.GetString(4),
                Signature: reader.IsDBNull(5) ? null : reader.GetString(5),
                FilePath: reader.IsDBNull(6) ? null : reader.GetString(6),
                StartLine: reader.GetInt32(7),
                EndLine: reader.GetInt32(8),
                IsTest: reader.GetBoolean(9),
                TypeKind: Enum.Parse<TypeKind>(reader.GetString(10)),
                Accessibility: Enum.Parse<Accessibility>(reader.GetString(11)),
                IsStatic: reader.GetBoolean(12),
                IsAbstract: reader.GetBoolean(13),
                IsVirtual: reader.GetBoolean(14),
                IsSealed: reader.GetBoolean(15),
                IsAsync: reader.GetBoolean(16),
                IsOverride: reader.GetBoolean(17),
                MemberKind: Enum.Parse<MemberKind>(reader.GetString(18)));
            results.Add((kind, node));
        }
        return results;
    }

    private static GraphNode ReadNode(System.Data.Common.DbDataReader reader) => new(
        Id: reader.GetString(0),
        Kind: Enum.Parse<NodeKind>(reader.GetString(1)),
        Name: reader.GetString(2),
        FullName: reader.GetString(3),
        Signature: reader.IsDBNull(4) ? null : reader.GetString(4),
        FilePath: reader.IsDBNull(5) ? null : reader.GetString(5),
        StartLine: reader.GetInt32(6),
        EndLine: reader.GetInt32(7),
        IsTest: reader.GetBoolean(8),
        TypeKind: Enum.Parse<TypeKind>(reader.GetString(9)),
        Accessibility: Enum.Parse<Accessibility>(reader.GetString(10)),
        IsStatic: reader.GetBoolean(11),
        IsAbstract: reader.GetBoolean(12),
        IsVirtual: reader.GetBoolean(13),
        IsSealed: reader.GetBoolean(14),
        IsAsync: reader.GetBoolean(15),
        IsOverride: reader.GetBoolean(16),
        MemberKind: Enum.Parse<MemberKind>(reader.GetString(17)));

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
                member_kind   VARCHAR NOT NULL DEFAULT 'Ordinary'
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
                mtime_ticks BIGINT NOT NULL
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
    }

    private void EnsureColumn(string table, string column, string definition)
    {
        Execute($"ALTER TABLE {table} ADD COLUMN IF NOT EXISTS {column} {definition};");
    }

    private void Execute(string sql, DuckDBTransaction? transaction = null, string? paths = null)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (paths is not null)
        {
            command.Parameters.Add(P("paths", paths));
        }

        command.ExecuteNonQuery();
    }

    private bool TableExists(string table)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM duckdb_tables() WHERE table_name = $table;";
        command.Parameters.Add(P("table", table));
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    // Path sets travel as one newline-joined parameter split server-side: works on read-only
    // connections (no temp table) and avoids thousands of positional parameters.
    private const string PathList = "SELECT unnest(string_split($paths, chr(10)))";

    private static string Lines(IEnumerable<string> paths) => string.Join('\n', paths);

    private static DuckDBParameter P(string name, object? value) =>
        new() { ParameterName = name, Value = value };
}
