using System.Diagnostics.CodeAnalysis;

namespace CodeGraph.Core;

/// <summary>
/// File paths in answers, relative to the repository root. The indexer stores absolute paths; an answer with twenty
/// rows repeated the root twenty times, and the root was most of each path. The root is named once instead, in a
/// line the call-tool filter puts at the top of any answer that shortened a path. A path outside the root stays
/// as it is. One instance serves one tool call (scoped), so <see cref="Shortened"/> is per answer.
/// </summary>
public sealed class AnswerPaths
{
    private static readonly bool IgnoreCase = OperatingSystem.IsWindows();

    public AnswerPaths(string? root)
    {
        var trimmed = root?.TrimEnd('/', '\\');
        Root = string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>The repository root without a trailing separator, or null when paths stay absolute.</summary>
    public string? Root { get; }

    /// <summary>True once a path of this answer was shortened.</summary>
    public bool Shortened { get; private set; }

    /// <summary>The line that names the root, or null when no path in this answer was shortened.</summary>
    public string? Header => Shortened ? $"paths are relative to {Root}" : null;

    /// <summary>Forgets that a path was shortened, for the next answer.</summary>
    public void Reset() => Shortened = false;

    /// <summary>The path relative to the root when it is under it, else the path as given. Separators are kept.</summary>
    [return: NotNullIfNotNull(nameof(path))]
    public string? Relative(string? path)
    {
        if (path is null || Root is null || !Under(path, Root))
        {
            return path;
        }

        Shortened = true;
        return path[(Root.Length + 1)..];
    }

    /// <summary>
    /// The root for the graph at <paramref name="dbPath"/>: the git worktree root that holds it; else the folder that
    /// holds its <c>.codegraph</c> folder; else the graph's own folder.
    /// </summary>
    public static string? RootFor(string dbPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (directory is null)
        {
            return null;
        }

        if (Worktree.RootOf(directory) is { } root)
        {
            return root;
        }

        return string.Equals(Path.GetFileName(directory), ".codegraph", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(directory) ?? directory
            : directory;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is inside <paramref name="root"/>. Either separator matches the other, because
    /// the TypeScript and SCIP extractors write forward slashes where Roslyn writes the platform's; on Windows the
    /// drive letter and folder names match in any case.
    /// </summary>
    private static bool Under(string path, string root)
    {
        if (path.Length <= root.Length + 1 || !IsSeparator(path[root.Length]))
        {
            return false;
        }

        for (var i = 0; i < root.Length; i++)
        {
            var p = path[i];
            var r = root[i];
            if (p == r || (IsSeparator(p) && IsSeparator(r)))
            {
                continue;
            }

            if (IgnoreCase && char.ToUpperInvariant(p) == char.ToUpperInvariant(r))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool IsSeparator(char c) => c is '/' or '\\';
}
