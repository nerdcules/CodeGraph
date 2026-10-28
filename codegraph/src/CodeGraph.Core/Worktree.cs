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
    public static string? Head(string directory)
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
