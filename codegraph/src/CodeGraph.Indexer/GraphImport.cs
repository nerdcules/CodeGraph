using System.Globalization;
using System.Text.Json;
using CodeGraph.Core;

namespace CodeGraph.Indexer;

/// <summary>
/// One extractor's output: every node, edge and file of one language, read from newline-delimited JSON. The
/// format is the contract between CodeGraph and an extractor written in another ecosystem (Node for TypeScript,
/// the JVM for Java); EXTRACTORS.md describes each record. An import replaces exactly that language's part of
/// the graph, so a C# solution and an Angular app can share one graph.
/// </summary>
internal sealed class GraphBatch
{
    public string Language { get; internal set; } = "";
    public string? Tool { get; internal set; }
    public string? Target { get; internal set; }
    public string? Root { get; internal set; }
    public List<GraphNode> Nodes { get; } = [];
    public List<GraphEdge> Edges { get; } = [];
    public List<EdgeOrigin> Origins { get; } = [];
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SearchDocument> Search { get; } = [];
    public Dictionary<string, string> EmbeddingTexts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Meta { get; } = new(StringComparer.Ordinal);
    public List<string> Problems { get; } = [];

    /// <summary>Reads a batch. Malformed records are collected in <see cref="Problems"/> rather than thrown, up to a limit.</summary>
    public static GraphBatch Read(TextReader reader)
    {
        var batch = new GraphBatch();
        var pending = new List<(GraphEdge Edge, string? File, int Line)>();
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var record = document.RootElement;
                switch (Str(record, "type"))
                {
                    case "header":
                        batch.Language = Str(record, "language")?.ToLowerInvariant() ?? throw new FormatException("header without language");
                        batch.Tool = Str(record, "tool");
                        batch.Root = Str(record, "root") is { } root ? Path.GetFullPath(root) : null;
                        batch.Target = Str(record, "target") is { } target ? batch.Resolve(target) : null;
                        break;
                    case "node":
                        batch.Nodes.Add(batch.ReadNode(record));
                        break;
                    case "edge":
                        var edge = new GraphEdge(Required(record, "from"), Required(record, "to"), Enum<EdgeKind>(record, "kind", null));
                        pending.Add((edge, Str(record, "file") is { } file ? batch.Resolve(file) : null, lineNumber));
                        break;
                    case "file":
                        batch.Files.Add(batch.Resolve(Required(record, "path")));
                        break;
                    case "search":
                        var parts = record.TryGetProperty("parts", out var list) && list.ValueKind == JsonValueKind.Array
                            ? list.EnumerateArray().Select(p => (Str(p, "text"), p.TryGetProperty("weight", out var w) && w.TryGetInt32(out var weight) ? weight : 1)).ToList()
                            : [(Str(record, "text"), 1)];
                        if (SearchText.Document(Required(record, "node"), parts) is { } searchDocument)
                        {
                            batch.Search.Add(searchDocument);
                        }

                        break;
                    case "embed":
                        batch.EmbeddingTexts[Required(record, "node")] = Required(record, "text");
                        break;
                    case "meta":
                        batch.Meta[Required(record, "key")] = Required(record, "value");
                        break;
                    case var other:
                        throw new FormatException($"unknown record type '{other}'");
                }
            }
            catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
            {
                batch.Problem($"line {lineNumber}: {e.Message}");
            }
        }

        if (batch.Language.Length == 0)
        {
            batch.Problem("no header record naming the language; nothing can be imported");
        }

        // Every edge needs the file that produced it, so re-importing that file can remove it: the record's own,
        // else the file its source node is declared in.
        var fileOf = batch.Nodes.Where(n => n.FilePath is not null).GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.Last().FilePath!, StringComparer.Ordinal);
        foreach (var (edge, file, line) in pending)
        {
            var origin = file ?? fileOf.GetValueOrDefault(edge.FromId);
            if (origin is null)
            {
                batch.Problem($"line {line}: edge {edge.FromId} -> {edge.ToId} has no file and its source node none either; skipped");
                continue;
            }

            batch.Edges.Add(edge);
            batch.Origins.Add(new EdgeOrigin(edge, origin));
        }

        return batch;
    }

    private GraphNode ReadNode(JsonElement record) => new(
        Id: Required(record, "id"),
        Kind: Enum<NodeKind>(record, "kind", null),
        Name: Required(record, "name"),
        FullName: Str(record, "fullName") ?? Required(record, "name"),
        Signature: Str(record, "signature"),
        FilePath: Str(record, "file") is { } file ? Resolve(file) : null,
        StartLine: Int(record, "start"),
        EndLine: Int(record, "end"),
        IsTest: Bool(record, "isTest"),
        TypeKind: Enum<TypeKind>(record, "typeKind", TypeKind.None),
        Accessibility: Enum<Accessibility>(record, "accessibility", Accessibility.None),
        IsStatic: Bool(record, "static"),
        IsAbstract: Bool(record, "abstract"),
        IsVirtual: Bool(record, "virtual"),
        IsSealed: Bool(record, "sealed"),
        IsAsync: Bool(record, "async"),
        IsOverride: Bool(record, "override"),
        MemberKind: Enum<MemberKind>(record, "memberKind", MemberKind.Ordinary),
        Language: Str(record, "language")?.ToLowerInvariant() ?? (Language.Length > 0 ? Language : throw new FormatException("node before the header")));

    private string Resolve(string path) => Path.GetFullPath(Root is null ? path : Path.Combine(Root, path));

    private void Problem(string message)
    {
        if (Problems.Count < 50)
        {
            Problems.Add(message);
        }
        else if (Problems.Count == 50)
        {
            Problems.Add("more problems not shown");
        }
    }

    private static string? Str(JsonElement record, string name) =>
        record.ValueKind == JsonValueKind.Object && record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Required(JsonElement record, string name) =>
        Str(record, name) is { Length: > 0 } value ? value : throw new FormatException($"missing '{name}'");

    private static int Int(JsonElement record, string name) =>
        record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    private static bool Bool(JsonElement record, string name) =>
        record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static T Enum<T>(JsonElement record, string name, T? fallback) where T : struct, Enum
    {
        var text = Str(record, name);
        if (text is null)
        {
            return fallback ?? throw new FormatException($"missing '{name}'");
        }

        return System.Enum.TryParse<T>(text, ignoreCase: true, out var value)
            ? value
            : throw new FormatException($"unknown {name} '{text}' (one of {string.Join(", ", System.Enum.GetNames<T>())})");
    }
}

internal static class GraphImport
{
    /// <summary>Meta keys an extractor's records land under, so two languages' settings never collide.</summary>
    public static string LanguageKey(string language, string key) => $"lang.{language}.{key}";

    /// <summary>
    /// Replaces <paramref name="batch"/>'s languages in the graph at <paramref name="dbPath"/>: the batch goes into a
    /// fresh file, every other language's rows are copied across from the old graph, and the fresh file is swapped
    /// in, so a reader sees the old graph or the new one.
    /// </summary>
    public static async Task<(int Nodes, int Edges)> WriteAsync(GraphBatch batch, string dbPath, Action<string> log, CancellationToken cancellationToken)
    {
        var stamps = batch.Files.Where(File.Exists).Select(FileStamp.Of).ToList();
        var languages = batch.Nodes.Select(n => n.Language).Append(batch.Language).Distinct(StringComparer.Ordinal).ToList();
        IReadOnlyDictionary<string, string> previousMeta = new Dictionary<string, string>();
        if (File.Exists(dbPath))
        {
            using var old = GraphStore.Open(dbPath, readOnly: true, TimeSpan.FromSeconds(30));
            previousMeta = old.ReadMeta();
        }

        // The graph-wide keys of the previous graph (its solution, commit, embedding model) carry over; this
        // language's own keys are rewritten below, and other languages' come across with their rows.
        var meta = previousMeta.Where(m => !m.Key.StartsWith("lang.", StringComparison.Ordinal)).ToDictionary(StringComparer.Ordinal);
        meta[MetaKeys.SchemaVersion] = GraphStore.SchemaVersion;
        meta[MetaKeys.IndexedAt] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        meta[LanguageKey(batch.Language, "indexed_at")] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        foreach (var (key, value) in batch.Meta)
        {
            meta[LanguageKey(batch.Language, key)] = value;
        }

        if (batch.Tool is not null)
        {
            meta[LanguageKey(batch.Language, "tool")] = batch.Tool;
        }

        if (batch.Target is not null)
        {
            meta[LanguageKey(batch.Language, "target")] = batch.Target;
            // The graph's headline target (what freshness warnings and the daemon launcher name) is the first
            // language's; a later one never takes it over.
            if (!previousMeta.ContainsKey(MetaKeys.Solution))
            {
                meta[MetaKeys.Solution] = batch.Target;
            }
        }

        var root = batch.Root ?? (batch.Target is null ? null : Worktree.RootOf(Path.GetDirectoryName(batch.Target)!));
        if (root is not null && Worktree.RootOf(root) is { } worktree)
        {
            meta[MetaKeys.WorktreeRoot] = worktree;
            if (Worktree.Head(worktree) is { } head)
            {
                meta[MetaKeys.Commit] = head;
            }
        }

        IReadOnlyList<NodeEmbedding>? embeddings = null;
        if (EmbeddingSettings.FromEnvironment() is { } settings && batch.EmbeddingTexts.Count > 0)
        {
            var graphModel = previousMeta.GetValueOrDefault(MetaKeys.EmbeddingsModel);
            if (graphModel is not null && graphModel != settings.Model)
            {
                log($"embeddings: the graph's vectors are from {graphModel}, not {settings.Model}; not embedding {batch.Language} (vectors from two models do not compare)");
            }
            else
            {
                embeddings = await EmbeddingStep.RunAsync(new EmbeddingClient(settings), batch.EmbeddingTexts, dbPath, log, cancellationToken);
                if (embeddings is not null)
                {
                    meta[MetaKeys.EmbeddingsModel] = settings.Model;
                }
            }
        }

        // A fresh file with this batch and every other language's rows copied from the old graph, swapped in: as
        // fast as the write itself (deleting one language's rows in place took longer than writing them, and grew
        // the file), compact, and a reader sees the old graph or the new one, never a half-replaced one.
        var fresh = GraphFile.Fresh(dbPath);
        using (var store = GraphStore.Open(fresh, readOnly: false, TimeSpan.FromSeconds(60)))
        {
            store.Replace(full: true, [], batch.Nodes, batch.Edges, batch.Origins, stamps, meta, batch.Search, embeddings, batch.Language);
            if (store.CopyOtherLanguages(dbPath, languages) is > 0 and var kept)
            {
                log($"kept {kept} node(s) of other languages from the previous graph");
            }
        }

        GraphFile.SwapIn(fresh, dbPath);
        return (batch.Nodes.Count, batch.Edges.Count);
    }
}

/// <summary><c>codegraph-index import [file|-] [--db PATH]</c>.</summary>
internal static class ImportCommand
{
    public static async Task<int> RunAsync(string[] args, TextReader stdin, Action<string> log)
    {
        string? source = null;
        string? db = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db" when i + 1 < args.Length:
                    db = args[++i];
                    break;
                case "-h" or "--help":
                    Console.Error.WriteLine("usage: codegraph-index import [records.ndjson | index.scip | -] [--db <path>]");
                    Console.Error.WriteLine("       reads an extractor's newline-delimited JSON (EXTRACTORS.md) from the file or stdin and");
                    Console.Error.WriteLine("       replaces that language's part of the graph; other languages are kept");
                    return 1;
                case var positional when source is null && (positional == "-" || !positional.StartsWith('-')):
                    source = positional;
                    break;
                default:
                    Console.Error.WriteLine($"unexpected argument: {args[i]}");
                    return 1;
            }
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        GraphBatch batch;
        if (source is not null && source.EndsWith(".scip", StringComparison.OrdinalIgnoreCase))
        {
            batch = ScipImport.Read(source, log);
        }
        else if (source is null or "-")
        {
            batch = GraphBatch.Read(stdin);
        }
        else
        {
            using var reader = new StreamReader(source);
            batch = GraphBatch.Read(reader);
        }

        foreach (var problem in batch.Problems)
        {
            log($"import: {problem}");
        }

        if (batch.Language.Length == 0)
        {
            return 1;
        }

        var dbPath = db is not null
            ? Path.GetFullPath(db)
            : Worktree.DefaultDbPath(batch.Root ?? (batch.Target is null ? Directory.GetCurrentDirectory() : Path.GetDirectoryName(batch.Target)!));
        var (nodes, edges) = await GraphImport.WriteAsync(batch, dbPath, log, CancellationToken.None);
        log($"imported {nodes} {batch.Language} nodes, {edges} edges, {batch.Files.Count} files{(batch.Tool is null ? "" : $" from {batch.Tool}")} in {clock.Elapsed.TotalSeconds:F1}s → {dbPath}");
        return batch.Problems.Count == 0 ? 0 : 2;
    }
}
