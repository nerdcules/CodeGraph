namespace CodeGraph.Core;

/// <summary>How the graph on disk compares with the working tree right now.</summary>
public sealed record FreshnessReport(
    IReadOnlyDictionary<string, string> Meta,
    string? Head,
    IReadOnlySet<string> ChangedFiles,
    IReadOnlySet<string> MissingFiles)
{
    public string? IndexedCommit => Meta.GetValueOrDefault(MetaKeys.Commit);

    public bool HeadMoved => IndexedCommit is not null && Head is not null && !string.Equals(Head, IndexedCommit, StringComparison.OrdinalIgnoreCase);

    public bool IsStale => HeadMoved || ChangedFiles.Count > 0 || MissingFiles.Count > 0;

    public bool IsDirty(string? path) => path is not null && (ChangedFiles.Contains(path) || MissingFiles.Contains(path));

    /// <summary>One line to put above an answer, or null when the graph matches the working tree.</summary>
    public string? Warning()
    {
        if (!IsStale)
        {
            return null;
        }

        var parts = new List<string>();
        if (HeadMoved)
        {
            parts.Add($"HEAD is {Short(Head)} but the graph was built at {Short(IndexedCommit)}");
        }

        if (ChangedFiles.Count > 0)
        {
            parts.Add($"{ChangedFiles.Count} indexed file(s) edited since ({Names(ChangedFiles)})");
        }

        if (MissingFiles.Count > 0)
        {
            parts.Add($"{MissingFiles.Count} indexed file(s) deleted ({Names(MissingFiles)})");
        }

        var refresh = Meta.GetValueOrDefault(MetaKeys.Solution) is { } solution
            ? $"codegraph-index \"{solution}\""
            : "codegraph-index <solution>";
        return $"⚠ STALE INDEX: {string.Join("; ", parts)}. Rows marked STALE come from changed files, and new files are missing. Refresh with: {refresh}";
    }

    private static string Short(string? commit) => commit is null ? "?" : commit[..Math.Min(7, commit.Length)];

    private static string Names(IReadOnlySet<string> paths)
    {
        var names = paths.Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return names.Count <= 5 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(5))}, +{names.Count - 5} more";
    }
}

public static class MetaKeys
{
    public const string SchemaVersion = "schema_version";
    public const string Solution = "solution";
    public const string WorktreeRoot = "worktree_root";
    public const string Commit = "commit";
    public const string IndexedAt = "indexed_at";
    public const string Mode = "mode";
    public const string EmbeddingsModel = "embeddings_model";

    /// <summary>Per-language rows are <c>lang.&lt;language&gt;.&lt;key&gt;</c>, so two languages' settings never collide.</summary>
    public const string LanguagePrefix = "lang.";

    public static string Language(string language, string key) => $"{LanguagePrefix}{language}.{key}";

    /// <summary>The language of a <c>lang.*</c> key, or null for any other key.</summary>
    public static string? LanguageOf(string key)
    {
        if (!key.StartsWith(LanguagePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var end = key.IndexOf('.', LanguagePrefix.Length);
        return end < 0 ? null : key[LanguagePrefix.Length..end];
    }
}

/// <summary>
/// What every route records about the compiler it read the code with, under <see cref="MetaKeys.Language"/>.
/// These are how a graph says which language versions it understood, so a repo on a newer version than the
/// indexer knows shows up in <c>index_status</c> instead of as silently missing edges.
/// </summary>
public static class LanguageMetaKeys
{
    /// <summary>The compiler or indexer that produced the rows: <c>Roslyn 5.9.0</c>, <c>typescript 6.0.3</c>, <c>scip-java 0.12.3</c>.</summary>
    public const string Compiler = "compiler";

    /// <summary>The newest language version that compiler understands: <c>C# 14</c>.</summary>
    public const string NewestLanguageVersion = "newest_language_version";

    /// <summary>The language versions the indexed projects asked for, comma separated: <c>C# 12, C# 14</c>, <c>ES2022</c>, <c>Java 21</c>.</summary>
    public const string LanguageVersions = "language_versions";

    /// <summary>Syntax and declaration errors the compiler reported while reading the indexed files. Declarations under them may be missing.</summary>
    public const string CompileErrors = "compile_errors";

    /// <summary>The subset of <see cref="CompileErrors"/> that say a feature needs a newer language version than the compiler has.</summary>
    public const string LanguageVersionErrors = "language_version_errors";

    /// <summary>The first such error, file and message, so the status can show what the repo uses that the indexer does not know.</summary>
    public const string FirstLanguageVersionError = "first_language_version_error";

    /// <summary>The first compile error of any kind, for routes that cannot tell version errors apart (TypeScript).</summary>
    public const string FirstCompileError = "first_compile_error";

    /// <summary>What the compiler said that did not stop the index but a reader should know, semicolon separated: a deprecated tsconfig option, a tool below its tested version.</summary>
    public const string Notes = "notes";

    /// <summary>Declaration shapes the compiler produced that this indexer has no mapping for, with counts: <c>TypeKind.Extension x3</c>. Empty when none.</summary>
    public const string Unmapped = "unmapped";

    /// <summary>The program that wrote the rows, as the extractor's header names it (<c>codegraph-ts 0.1.0</c>).</summary>
    public const string Tool = "tool";

    public const string Target = "target";
    public const string IndexedAt = "indexed_at";
}

/// <summary>
/// Checks the graph against the working tree. Size and mtime are compared first and a file is only re-hashed when
/// they differ, so a check over a few thousand files is a few thousand <c>stat</c> calls. Results are cached for
/// <paramref name="ttl"/> so a burst of tool calls pays once.
/// </summary>
public sealed class IndexFreshness(string dbPath, TimeSpan ttl)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (long Size, long Mtime, string Hash)> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, string> _meta = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, FileStamp> _files = new Dictionary<string, FileStamp>();
    private DateTime _loadedDbWrite;
    private FreshnessReport? _report;
    private DateTime _checkedAt;

    public string DbPath => dbPath;

    /// <summary>Forgets the cached report, so the next <see cref="Check"/> re-reads the graph (after a daemon flush rewrote it).</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _report = null;
        }
    }

    public FreshnessReport Check()
    {
        lock (_lock)
        {
            // The TTL spares the working-tree scan, not the graph: a new graph file (one stat) always reloads.
            if (_report is not null && DateTime.UtcNow - _checkedAt < ttl && File.GetLastWriteTimeUtc(dbPath) == _loadedDbWrite)
            {
                return _report;
            }

            Reload();

            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var stamp in _files.Values)
            {
                var info = new FileInfo(stamp.Path);
                if (!info.Exists)
                {
                    missing.Add(stamp.Path);
                }
                else if (info.Length != stamp.Size || info.LastWriteTimeUtc.Ticks != stamp.MtimeTicks)
                {
                    // A checkout or touch moves mtime without changing content, so confirm by hash.
                    if (HashOf(info) != stamp.Hash)
                    {
                        changed.Add(stamp.Path);
                    }
                }
            }

            var root = _meta.GetValueOrDefault(MetaKeys.WorktreeRoot) ?? Worktree.RootOf(Path.GetDirectoryName(dbPath)!);
            var head = root is null ? null : Worktree.Head(root);

            _report = new FreshnessReport(_meta, head, changed, missing);
            _checkedAt = DateTime.UtcNow;
            return _report;
        }
    }

    private void Reload()
    {
        var dbWrite = File.GetLastWriteTimeUtc(dbPath);
        if (dbWrite == _loadedDbWrite && _report is not null)
        {
            return;
        }

        try
        {
            using var store = GraphStore.Open(dbPath, readOnly: true, TimeSpan.FromSeconds(2));
            _meta = store.ReadMeta();
            _files = store.IndexedFiles();
            _loadedDbWrite = dbWrite;
        }
        catch (DuckDB.NET.Data.DuckDBException)
        {
            // The indexer is mid-write; keep what we had and try again next time.
        }
        catch (FileNotFoundException)
        {
            // Nothing indexed yet; the call-tool filter has already said so.
        }
    }

    private string HashOf(FileInfo info)
    {
        var key = (info.Length, info.LastWriteTimeUtc.Ticks);
        if (_hashes.TryGetValue(info.FullName, out var cached) && (cached.Size, cached.Mtime) == key)
        {
            return cached.Hash;
        }

        var hash = FileStamp.HashOf(info.FullName);
        _hashes[info.FullName] = (key.Length, key.Ticks, hash);
        return hash;
    }
}
