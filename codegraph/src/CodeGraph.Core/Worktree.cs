using System.Diagnostics;

namespace CodeGraph.Core;

/// <summary>
/// Where a graph lives: one <c>.codegraph/graph.db</c> per git worktree root, so a single <c>.mcp.json</c>
/// with no db argument works in every checkout of the same repo.
/// </summary>
public static class Worktree
{
    public static readonly string DbRelativePath = Path.Combine(".codegraph", "graph.db");

    /// <summary>
    /// The nearest directory at or above <paramref name="start"/> holding a <c>.git</c> entry. Linked worktrees
    /// have a <c>.git</c> file rather than a directory, which counts the same. Null outside a repo.
    /// </summary>
    public static string? RootOf(string start)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(start)); dir is not null; dir = dir.Parent)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    /// <summary>The db path the indexer writes by default: under the worktree root, else beside <paramref name="start"/>.</summary>
    public static string DefaultDbPath(string start) =>
        Path.Combine(RootOf(start) ?? Path.GetFullPath(start), DbRelativePath);

    /// <summary>
    /// Finds an existing graph from <paramref name="start"/> upwards, stopping at the worktree root so one
    /// worktree never picks up a sibling's or a parent repo's index. Null when none has been built yet.
    /// </summary>
    public static string? FindDb(string start)
    {
        var root = RootOf(start);
        for (var dir = new DirectoryInfo(Path.GetFullPath(start)); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, DbRelativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (root is null || string.Equals(dir.FullName, root, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        return null;
    }

    /// <summary>The commit checked out in <paramref name="directory"/>, or null when git is unavailable or it is not a repo.</summary>
    public static string? Head(string directory) => HeadFromFiles(directory) ?? HeadFromGit(directory);

    /// <summary>
    /// HEAD read from the repository's files, as git itself resolves it: <c>.git</c> (a directory, or a file naming
    /// the gitdir of a linked worktree), then HEAD, then the ref it points at as a loose file or a line of
    /// <c>packed-refs</c> in the common directory. The freshness check runs on most tool calls, and starting a
    /// <c>git</c> process for it cost a few milliseconds on Linux and far more on Windows. Null whenever anything is
    /// unusual (a reftable repository, a symbolic ref chain), so <see cref="Head"/> falls back to asking git.
    /// </summary>
    public static string? HeadFromFiles(string directory)
    {
        try
        {
            var root = RootOf(directory);
            if (root is null)
            {
                return null;
            }

            var dotGit = Path.Combine(root, ".git");
            var gitDir = dotGit;
            if (File.Exists(dotGit))
            {
                var line = File.ReadAllText(dotGit).Trim();
                if (!line.StartsWith("gitdir:", StringComparison.Ordinal))
                {
                    return null;
                }

                gitDir = Path.GetFullPath(Path.Combine(root, line["gitdir:".Length..].Trim()));
            }

            var commonDir = gitDir;
            var commonFile = Path.Combine(gitDir, "commondir");
            if (File.Exists(commonFile))
            {
                commonDir = Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(commonFile).Trim()));
            }

            if (Directory.Exists(Path.Combine(commonDir, "reftable")))
            {
                return null;
            }

            var head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
            if (!head.StartsWith("ref:", StringComparison.Ordinal))
            {
                return IsObjectId(head) ? head : null;
            }

            var name = head["ref:".Length..].Trim();
            foreach (var dir in new[] { gitDir, commonDir })
            {
                var loose = Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(loose))
                {
                    var id = File.ReadAllText(loose).Trim();
                    return IsObjectId(id) ? id : null;
                }
            }

            var packed = Path.Combine(commonDir, "packed-refs");
            if (File.Exists(packed))
            {
                foreach (var line in File.ReadLines(packed))
                {
                    var space = line.IndexOf(' ');
                    if (space > 0 && line.AsSpan(space + 1).Trim().SequenceEqual(name) && IsObjectId(line[..space]))
                    {
                        return line[..space];
                    }
                }
            }

            return null; // an unborn branch, or a layout this reader does not know
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsObjectId(string text) =>
        text.Length is 40 or 64 && text.All(char.IsAsciiHexDigitLower);

    private static string? HeadFromGit(string directory)
    {
        try
        {
            // Redirect stdin too: inside the MCP server, stdin is the JSON-RPC pipe, and a child that inherits it
            // stalls the server's reads until the client hangs up.
            using var git = Process.Start(new ProcessStartInfo("git", ["-C", directory, "rev-parse", "HEAD"])
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (git is null)
            {
                return null;
            }

            git.StandardInput.Close();
            var output = git.StandardOutput.ReadToEnd().Trim();
            if (!git.WaitForExit(5000))
            {
                git.Kill();
                return null;
            }

            return git.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // git not on PATH
        }
    }
}
