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

    public FreshnessReport Check()
    {
        lock (_lock)
        {
            if (_report is not null && DateTime.UtcNow - _checkedAt < ttl)
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
