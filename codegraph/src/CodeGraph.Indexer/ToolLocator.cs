namespace CodeGraph.Indexer;

/// <summary>
/// Finds an external tool (scip-java, scip-go, jdtls, …): on PATH, else in the folders the usual installers put
/// tools in without adding them to PATH: coursier (<c>cs install</c>), <c>go install</c>, cargo, npm and pip user
/// installs. An MCP client often starts the server with a thinner PATH than a login shell, so this matters.
/// </summary>
internal static class ToolLocator
{
    /// <summary>The tool's full path, or null when it is nowhere to be found.</summary>
    public static string? Find(string tool, IReadOnlyList<string>? extraDirectories = null)
    {
        var names = OperatingSystem.IsWindows() ? new[] { tool + ".exe", tool + ".cmd", tool + ".bat", tool } : [tool];
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in path.Concat(extraDirectories ?? WellKnownDirectories()))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    internal static IEnumerable<string> WellKnownDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(home, ".local", "share");
        if (Environment.GetEnvironmentVariable("COURSIER_BIN_DIR") is { Length: > 0 } coursierBin)
        {
            yield return coursierBin;
        }

        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Coursier", "data", "bin");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(home, "Library", "Application Support", "Coursier", "bin");
        }
        else
        {
            yield return Path.Combine(dataHome, "coursier", "bin");
        }

        if (Environment.GetEnvironmentVariable("GOBIN") is { Length: > 0 } gobin)
        {
            yield return gobin;
        }

        yield return Path.Combine(Environment.GetEnvironmentVariable("GOPATH") is { Length: > 0 } gopath ? gopath.Split(Path.PathSeparator)[0] : Path.Combine(home, "go"), "bin");
        yield return Path.Combine(home, ".cargo", "bin");
        yield return Path.Combine(home, ".local", "bin");
    }
}
