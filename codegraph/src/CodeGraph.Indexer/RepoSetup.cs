using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Core;

namespace CodeGraph.Indexer;

/// <summary>
/// <c>codegraph-index init</c>: wires a repo to codegraph in one step. It finds the solution, writes the
/// <c>codegraph</c> server into the repo's <c>.mcp.json</c> (other servers are kept), adds <c>.codegraph/</c> to
/// <c>.gitignore</c>, and works out whether the server needs DOTNET_ROOT to start. The caller then indexes.
/// Nothing here touches MSBuild, so it runs before MSBuildLocator registers.
/// </summary>
internal static class RepoSetup
{
    private static readonly string[] SolutionPatterns = ["*.slnx", "*.sln"];
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", "packages", "TestResults" };

    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// What to index from a file or a directory: the file itself; else the one solution in the directory; else the
    /// one solution below it (three levels, skipping build output and hidden folders); else, from a subfolder of a
    /// repo, the one solution at or below the worktree root; else the one project in the directory; else a TypeScript
    /// workspace (angular.json, nx.json, or tsconfig.json) in it or below it.
    /// When <c>App.sln</c> and <c>App.slnx</c> sit side by side the .slnx wins. Null with <paramref name="problem"/> set otherwise.
    /// </summary>
    public static string? FindTarget(string path, out string? problem)
    {
        problem = null;
        var full = Path.GetFullPath(path);
        if (File.Exists(full))
        {
            return full;
        }

        if (!Directory.Exists(full))
        {
            problem = $"not found: {full}";
            return null;
        }

        var repoRoot = Worktree.RootOf(full);
        var candidates = new List<Func<List<string>>>
        {
            () => Solutions(full, SearchOption.TopDirectoryOnly),
            () => Solutions(full, SearchOption.AllDirectories),
        };
        if (repoRoot is not null && !string.Equals(repoRoot, full, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(() => Solutions(repoRoot, SearchOption.AllDirectories));
        }

        candidates.Add(() => Directory.GetFiles(full, "*.csproj").Order(StringComparer.Ordinal).ToList());
        // No C#: a TypeScript or Angular workspace (angular.json, nx.json, tsconfig.json) here or below.
        candidates.Add(() => TypeScriptExtractor.IsTarget(full) ? [TypeScriptExtractor.WorkspaceFile(full)] : TypeScriptExtractor.WorkspacesUnder(full).ToList());
        foreach (var search in candidates)
        {
            var found = search();
            if (found.Count == 1)
            {
                return found[0];
            }

            if (found.Count > 1)
            {
                problem = $"more than one candidate under {full}; pass the one to index:\n  " + string.Join("\n  ", found.Select(f => Path.GetRelativePath(full, f)));
                return null;
            }
        }

        problem = $"no .slnx, .sln, .csproj, angular.json or tsconfig.json in {full}; pass what to index";
        return null;
    }

    private static List<string> Solutions(string directory, SearchOption depth)
    {
        var files = depth == SearchOption.TopDirectoryOnly
            ? SolutionPatterns.SelectMany(p => Directory.GetFiles(directory, p))
            : Walk(new DirectoryInfo(directory), 3);
        // App.sln beside App.slnx is one solution in two formats.
        return files
            .GroupBy(f => Path.Combine(Path.GetDirectoryName(f)!, Path.GetFileNameWithoutExtension(f)), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(f => f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)).First())
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<string> Walk(DirectoryInfo directory, int depth)
    {
        foreach (var pattern in SolutionPatterns)
        {
            foreach (var file in directory.EnumerateFiles(pattern))
            {
                yield return file.FullName;
            }
        }

        if (depth == 0)
        {
            yield break;
        }

        foreach (var child in directory.EnumerateDirectories())
        {
            if (child.Name.StartsWith('.') || SkippedDirectories.Contains(child.Name) || child.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            foreach (var file in Walk(child, depth - 1))
            {
                yield return file;
            }
        }
    }

    /// <summary>Where <c>.mcp.json</c> goes for a target: its git worktree root, else the target's folder.</summary>
    public static string RepoRoot(string target)
    {
        var directory = Path.GetDirectoryName(target)!;
        return Worktree.RootOf(directory) ?? directory;
    }

    /// <summary>
    /// The codegraph server entry. Starts from the existing one so hand-added settings (an embeddings endpoint in
    /// <c>env</c>, say) survive a re-run; <c>args</c> are dropped when the command changes, since they belonged to
    /// the old launcher. DOTNET_ROOT is set or cleared to match what the probe found; <paramref name="live"/> adds
    /// CODEGRAPH_DAEMON=auto and never removes it.
    /// </summary>
    public static JsonObject Entry(JsonNode? existing, string command, string? dbPath, string? dotnetRoot, bool live)
    {
        var entry = existing is JsonObject current ? (JsonObject)current.DeepClone() : [];
        if (entry["command"]?.GetValueKind() != JsonValueKind.String || entry["command"]!.GetValue<string>() != command)
        {
            entry.Remove("args");
        }

        entry["command"] = command;
        if (dbPath is not null)
        {
            entry["args"] = new JsonArray(dbPath);
        }

        var env = entry["env"] as JsonObject ?? [];
        if (dotnetRoot is null)
        {
            env.Remove("DOTNET_ROOT");
        }
        else
        {
            env["DOTNET_ROOT"] = dotnetRoot;
        }

        if (live)
        {
            env["CODEGRAPH_DAEMON"] = "auto";
        }

        entry.Remove("env");
        if (env.Count > 0)
        {
            entry["env"] = env;
        }

        return entry;
    }

    /// <summary>
    /// Puts <paramref name="entryFor"/>'s result in as <c>mcpServers.codegraph</c> in <paramref name="existingJson"/>
    /// (a missing or empty file is an empty config) and returns the new text, or null when nothing changed.
    /// </summary>
    public static string? MergeConfig(string? existingJson, Func<JsonNode?, JsonObject> entryFor)
    {
        var root = string.IsNullOrWhiteSpace(existingJson)
            ? []
            : JsonNode.Parse(existingJson, documentOptions: ReadOptions) as JsonObject
              ?? throw new InvalidDataException("the file is not a JSON object");
        if (root["mcpServers"] is not JsonObject servers)
        {
            servers = [];
            root["mcpServers"] = servers;
        }

        var current = servers["codegraph"];
        var entry = entryFor(current);
        if (current is not null && JsonNode.DeepEquals(current, entry))
        {
            return null;
        }

        servers["codegraph"] = entry;
        return root.ToJsonString(WriteOptions) + "\n";
    }

    /// <summary>Whether a .gitignore already covers the graph folder at the repo root.</summary>
    public static bool IgnoresGraph(string gitignore) =>
        gitignore.Split('\n').Select(line => line.Trim()).Any(line => line is ".codegraph" or ".codegraph/" or "/.codegraph" or "/.codegraph/" or ".codegraph/*" or "/.codegraph/*");

    /// <summary>
    /// The server executable for this indexer: beside it in an install, or in the sibling project's build output in
    /// a source checkout. Paths use forward slashes, which every MCP host reads on Windows too.
    /// </summary>
    public static string? FindServer()
    {
        var exe = OperatingSystem.IsWindows() ? "codegraph-mcp.exe" : "codegraph-mcp";
        var baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, exe),
            Path.Combine(baseDir.Replace("CodeGraph.Indexer", "CodeGraph.Mcp", StringComparison.Ordinal), exe),
        ];
        return candidates.FirstOrDefault(File.Exists)?.Replace('\\', '/');
    }

    /// <summary>
    /// Whether <paramref name="exe"/> starts (<c>--version</c>) with DOTNET_ROOT unset, or set to
    /// <paramref name="dotnetRoot"/>. An MCP host launches the server without the user's shell profile, so the
    /// probe clears every DOTNET_ROOT variant this shell might have.
    /// </summary>
    public static bool Starts(string exe, string? dotnetRoot)
    {
        var info = new ProcessStartInfo(exe, ["--version"])
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("DOTNET_ROOT", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            info.Environment.Remove(key);
        }

        if (dotnetRoot is not null)
        {
            info.Environment["DOTNET_ROOT"] = dotnetRoot;
        }

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            Task.WaitAll(output, error);
            return process.ExitCode == 0;
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes the repo's config for <paramref name="target"/> and prints what it did. <paramref name="dbPath"/> is
    /// non-null only for an explicit --db, which the server then needs as an argument. False when it could not.
    /// </summary>
    public static bool Configure(string target, string? dbPath, bool live, TextWriter output)
    {
        var root = RepoRoot(target);
        output.WriteLine($"codegraph init: {target}");

        var server = FindServer();
        if (server is null)
        {
            output.WriteLine($"  cannot find codegraph-mcp next to {AppContext.BaseDirectory}; install both executables into one folder");
            return false;
        }

        string? dotnetRoot = null;
        if (!Starts(server, null))
        {
            dotnetRoot = DotnetRuntime.Root()?.Replace('\\', '/');
            if (dotnetRoot is not null && Starts(server, dotnetRoot))
            {
                output.WriteLine($"  DOTNET_ROOT    the default .NET location cannot run the server; the entry sets DOTNET_ROOT={dotnetRoot}");
            }
            else
            {
                output.WriteLine($"  warning        {server} --version fails; run it by hand to see why. Writing the entry anyway.");
                dotnetRoot = null;
            }
        }

        var configPath = Path.Combine(root, ".mcp.json");
        try
        {
            var existing = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
            var merged = MergeConfig(existing, current => Entry(current, server, dbPath?.Replace('\\', '/'), dotnetRoot, live));
            if (merged is null)
            {
                output.WriteLine($"  .mcp.json      codegraph entry already up to date ({configPath})");
            }
            else
            {
                File.WriteAllText(configPath, merged);
                output.WriteLine($"  .mcp.json      {(existing is null ? "created" : "updated")} with the codegraph server ({configPath})");
            }
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"  cannot update {configPath}: {e.Message}. Fix or move it, then re-run.");
            return false;
        }

        if (Worktree.RootOf(root) is not null)
        {
            var gitignore = Path.Combine(root, ".gitignore");
            var text = File.Exists(gitignore) ? File.ReadAllText(gitignore) : "";
            if (IgnoresGraph(text) || GitIgnores(root))
            {
                output.WriteLine("  .gitignore     already ignores .codegraph/");
            }
            else
            {
                var separator = text.Length == 0 || text.EndsWith('\n') ? "" : "\n";
                File.AppendAllText(gitignore, $"{separator}.codegraph/\n");
                output.WriteLine("  .gitignore     added .codegraph/");
            }
        }

        if (live)
        {
            output.WriteLine("  live tools     the server starts codegraph-index --watch on its first call (CODEGRAPH_DAEMON=auto)");
        }

        return true;
    }

    /// <summary>Whether git already ignores the graph through any rule (a global excludes file, a pattern elsewhere).</summary>
    private static bool GitIgnores(string root)
    {
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", ["-C", root, "check-ignore", "-q", Path.Combine(".codegraph", "graph.db")])
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (git is null)
            {
                return false;
            }

            git.StandardInput.Close();
            git.WaitForExit();
            return git.ExitCode == 0;
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            return false;
        }
    }
}
