namespace CodeGraph.Indexer;

/// <summary>
/// <c>--watch</c> for a language indexed by an external extractor: index, then index again on every relevant
/// save, debounced. These languages have no live daemon, so the MCP server's live tools say so; graph answers
/// stay fresh because every save re-runs the extractor.
/// </summary>
internal static class ReindexWatch
{
    private static readonly HashSet<string> IgnoredFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".codegraph", "dist", ".angular", ".nx", "coverage", "target", "build", ".gradle",
        "bin", "obj", "__pycache__", ".venv", "venv", ".idea", ".vs", "out",
    };

    /// <summary>Whether a change to <paramref name="path"/> can change the graph: a source or build file outside build output.</summary>
    public static bool Relevant(string path, IReadOnlySet<string> extensions)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !segments.Any(IgnoredFolders.Contains) && extensions.Contains(Path.GetExtension(path));
    }

    public static async Task<int> RunAsync(string root, IReadOnlySet<string> extensions, Func<CancellationToken, Task<int>> index, Action<string> log, CancellationToken cancellationToken)
    {
        var exit = await index(cancellationToken);
        using var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName };
        var changed = new SemaphoreSlim(0);
        void OnChange(object _, FileSystemEventArgs e)
        {
            if (Relevant(e.FullPath, extensions))
            {
                changed.Release();
            }
        }

        watcher.Changed += OnChange;
        watcher.Created += OnChange;
        watcher.Deleted += OnChange;
        watcher.Renamed += (s, e) => OnChange(s, e);
        watcher.EnableRaisingEvents = true;
        log($"watching {root} (Ctrl+C to stop)");
        try
        {
            while (true)
            {
                await changed.WaitAsync(cancellationToken);
                // A save often arrives as several events, and editors save several files at once.
                await Task.Delay(500, cancellationToken);
                while (changed.CurrentCount > 0)
                {
                    await changed.WaitAsync(cancellationToken);
                }

                exit = await index(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            return exit;
        }
    }
}
