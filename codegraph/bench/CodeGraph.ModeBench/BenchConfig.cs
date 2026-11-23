using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeGraph.ModeBench;

/// <summary>
/// bench.config.json: the pinned model, the target repo and its commit, the prebuilt index, run limits, and where
/// the tools live. Relative paths are relative to the config file; a leading ~ is the home folder.
/// </summary>
internal sealed record BenchConfig(
    string Model,
    string RepoPath,
    string Commit,
    string Db,
    int Repetitions,
    int MaxTurns,
    int TimeoutSeconds,
    string Claude,
    string Dotnet,
    string McpDotnet,
    string McpServer,
    string CliDir,
    string ClientProject,
    string WorkRoot,
    string BenchDir,
    string ConfigPath)
{
    public string ProfilesDir => Path.Combine(BenchDir, "profiles");

    public string LiteDoc => Path.GetFullPath(Path.Combine(BenchDir, "..", "docs", "codegraph-lite.md"));

    public string Cli => Path.Combine(CliDir, OperatingSystem.IsWindows() ? "codegraph.exe" : "codegraph");

    public static string DefaultPath => Path.Combine(FindBenchDir(), "bench.config.json");

    public static BenchConfig Load(string? path = null)
    {
        var file = Path.GetFullPath(path ?? DefaultPath);
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var dir = Path.GetDirectoryName(file)!;
        string Text(string key) => json[key]?.GetValue<string>() ?? throw new InvalidDataException($"{file}: '{key}' is missing");
        string PathOf(string key) => Resolve(dir, Text(key));
        int Number(string key) => json[key]?.GetValue<int>() ?? throw new InvalidDataException($"{file}: '{key}' is missing");
        var workRoot = json["workRoot"]?.GetValue<string>() is { Length: > 0 } root
            ? Resolve(dir, root)
            : Path.Combine(Path.GetTempPath(), "codegraph-modebench");
        return new BenchConfig(
            Text("model"), PathOf("repoPath"), Text("commit"), PathOf("db"), Number("repetitions"), Number("maxTurns"),
            Number("timeoutSeconds"), Text("claude"), Text("dotnet"), PathOf("mcpDotnet"), PathOf("mcpServer"),
            PathOf("cliDir"), PathOf("clientProject"), workRoot, dir, file);
    }

    /// <summary>A command name (claude, dotnet) stays as it is, for PATH to find; anything with a separator is a path.</summary>
    private static string Resolve(string dir, string value)
    {
        if (value.StartsWith('~'))
        {
            value = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + value[1..];
        }

        return value.Contains('/') || value.Contains('\\') ? Path.GetFullPath(Path.Combine(dir, value)) : value;
    }

    private static string FindBenchDir()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "bench", "bench.config.json");
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(candidate)!;
            }

            if (File.Exists(Path.Combine(directory.FullName, "bench.config.json")))
            {
                return directory.FullName;
            }
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "bench");
    }

    public static string Json(object value) => JsonSerializer.Serialize(value);

    /// <summary>A path as written to results: the home folder shown as ~, so committed results name no user.</summary>
    public static string Shown(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0 && (path == home || path.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            ? "~" + path[home.Length..]
            : path;
    }
}
