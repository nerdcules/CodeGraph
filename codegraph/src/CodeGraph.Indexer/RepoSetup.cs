using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Core;

namespace CodeGraph.Indexer;

/// <summary>How a client tells the server which project it is in, when it does not start the server there.</summary>
internal enum RootHint
{
    /// <summary>The client starts the server in the project; the server walks up from there.</summary>
    None,

    /// <summary><c>--root ${workspaceFolder}</c>, which the client expands (VS Code, the Cursor IDE).</summary>
    WorkspaceVariable,

    /// <summary><c>--root &lt;worktree root&gt;</c>, written out, for a client that neither starts the server in the project nor expands a variable.</summary>
    Absolute,
}

/// <summary>The shape of a client's server entry.</summary>
internal enum EntryShape
{
    /// <summary><c>command</c>, <c>args</c>, <c>env</c>: Claude Code, Cursor, Gemini, Roo Code, Continue.</summary>
    Plain,

    /// <summary>The same with <c>"type": "stdio"</c>: VS Code, Visual Studio, Copilot for JetBrains.</summary>
    Typed,

    /// <summary><c>"type": "local"</c>, <c>command</c> as one array, <c>environment</c>: Kilo Code (opencode's 1.x format).</summary>
    Local,
}

/// <summary>
/// An assistant <c>init</c> can configure: the file it reads its project's MCP servers from (relative to the worktree
/// root, or to the solution's folder for Visual Studio), the key the servers sit under, the entry's shape, and how the
/// server learns the project folder. A null <see cref="ConfigFile"/> is Codex, whose servers live in its global config.
/// </summary>
internal sealed record McpClient(string Name, string Display, string? ConfigFile, string ServersKey, EntryShape Shape, RootHint Root, bool BesideSolution = false)
{
    /// <summary>Claude Code (CLI and IDE extensions); the GitHub Copilot CLI reads the same file.</summary>
    public static readonly McpClient Claude = new("claude", "Claude Code, Copilot CLI", ".mcp.json", "mcpServers", EntryShape.Plain, RootHint.None);

    /// <summary>GitHub Copilot in VS Code (agent mode), and any VS Code extension that reads VS Code's own MCP list.</summary>
    public static readonly McpClient VsCode = new("vscode", "VS Code", Path.Combine(".vscode", "mcp.json"), "servers", EntryShape.Typed, RootHint.WorkspaceVariable);

    /// <summary>
    /// GitHub Copilot in Visual Studio 2022 17.14+ and 2026: <c>.vs/mcp.json</c> beside the solution, which is this
    /// user's and not committed, so it can carry the absolute worktree root (Visual Studio's cwd and variables are undocumented).
    /// </summary>
    public static readonly McpClient VisualStudio = new("visualstudio", "Visual Studio", Path.Combine(".vs", "mcp.json"), "servers", EntryShape.Typed, RootHint.Absolute, BesideSolution: true);

    /// <summary>
    /// GitHub Copilot for JetBrains IDEs (plugin 1.19.2+) reads <c>.github/mcp.json</c>; it starts servers in the IDE's
    /// own folder and expands no variables, so the entry carries the absolute worktree root.
    /// </summary>
    public static readonly McpClient JetBrains = new("jetbrains", "Copilot for JetBrains", Path.Combine(".github", "mcp.json"), "servers", EntryShape.Typed, RootHint.Absolute);

    /// <summary>The Cursor IDE and CLI. The IDE expands <c>${workspaceFolder}</c>; the CLI starts servers in the project.</summary>
    public static readonly McpClient Cursor = new("cursor", "Cursor", Path.Combine(".cursor", "mcp.json"), "mcpServers", EntryShape.Plain, RootHint.WorkspaceVariable);

    /// <summary>The Gemini CLI and Gemini Code Assist's agent mode. Its settings file holds other settings too; they are kept.</summary>
    public static readonly McpClient Gemini = new("gemini", "Gemini CLI", Path.Combine(".gemini", "settings.json"), "mcpServers", EntryShape.Plain, RootHint.None);

    /// <summary>The Codex CLI and IDE extension: one global entry, set through <c>codex mcp add</c>.</summary>
    public static readonly McpClient Codex = new("codex", "Codex", ConfigFile: null, "mcp_servers", EntryShape.Plain, RootHint.None);

    /// <summary>Roo Code: <c>.roo/mcp.json</c>; it starts servers in the first workspace folder.</summary>
    public static readonly McpClient Roo = new("roo", "Roo Code", Path.Combine(".roo", "mcp.json"), "mcpServers", EntryShape.Plain, RootHint.None);

    /// <summary>Kilo Code: <c>.kilo/kilo.json</c>, opencode's format; it starts servers in the workspace folder.</summary>
    public static readonly McpClient Kilo = new("kilo", "Kilo Code", Path.Combine(".kilo", "kilo.json"), "mcp", EntryShape.Local, RootHint.None);

    /// <summary>Continue: a file of its own in <c>.continue/mcpServers/</c>; it starts servers in the first workspace folder.</summary>
    public static readonly McpClient Continue = new("continue", "Continue", Path.Combine(".continue", "mcpServers", "codegraph.json"), "mcpServers", EntryShape.Plain, RootHint.None);

    public static readonly IReadOnlyList<McpClient> All = [Claude, VsCode, VisualStudio, JetBrains, Cursor, Gemini, Codex, Roo, Kilo, Continue];

    /// <summary>Every place GitHub Copilot reads a project's servers from: the CLI, VS Code, Visual Studio, JetBrains.</summary>
    public static readonly IReadOnlyList<McpClient> Copilot = [Claude, VsCode, VisualStudio, JetBrains];

    public const string Names = "claude, copilot (copilot-cli, vscode, visualstudio, jetbrains), cursor, gemini, codex, roo, kilo, continue or all";

    /// <summary>
    /// The clients named in <c>--client</c> values: comma-separated, repeatable, <c>all</c> for every one, and
    /// <c>copilot</c> for every Copilot surface. Null with <paramref name="problem"/> set for an unknown name.
    /// </summary>
    public static IReadOnlyList<McpClient>? Parse(IEnumerable<string> values, out string? problem)
    {
        problem = null;
        var chosen = new List<McpClient>();
        foreach (var name in values.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            var found = name.ToLowerInvariant() switch
            {
                "all" => All,
                "copilot" or "github-copilot" => Copilot,
                "claude" or "claude-code" or "copilot-cli" => [Claude],
                "vscode" or "vs-code" or "code" => [VsCode],
                "visualstudio" or "visual-studio" or "vs" => [VisualStudio],
                "jetbrains" or "intellij" or "rider" => [JetBrains],
                "roo" or "roo-code" or "roocode" => [Roo],
                "kilo" or "kilo-code" or "kilocode" => [Kilo],
                var other => All.Where(c => c.Name == other).ToList(),
            };
            if (found.Count == 0)
            {
                problem = $"unknown client '{name}'; use {Names}";
                return null;
            }

            chosen.AddRange(found.Where(c => !chosen.Contains(c)));
        }

        return chosen.Count == 0 ? [Claude] : chosen;
    }
}

/// <summary>
/// <c>codegraph-index init</c>: wires a repo to codegraph in one step. It finds the solution, writes the
/// <c>codegraph</c> server into each chosen assistant's config (<c>.mcp.json</c> by default; other servers are
/// kept), adds <c>.codegraph/</c> to <c>.gitignore</c>, and works out whether the server needs DOTNET_ROOT to start.
/// The caller then indexes. Nothing here touches MSBuild, so it runs before MSBuildLocator registers.
/// </summary>
internal static class RepoSetup
{
    /// <summary>What an IDE passes so the server looks for the graph in the open folder.</summary>
    public static readonly string[] WorkspaceRootArgs = ["--root", "${workspaceFolder}"];

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
        // Then a project a SCIP indexer handles: Maven or Gradle, Python, Go, Rust.
        candidates.Add(() => ScipIndexers.For(full) is { } scip ? [scip.Marker] : []);
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

        problem = $"no .slnx, .sln, .csproj, angular.json, tsconfig.json, pom.xml, build.gradle, pyproject.toml, go.mod or Cargo.toml in {full}; pass what to index";
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
    /// CODEGRAPH_DAEMON=auto and never removes it. An explicit <paramref name="dbPath"/> is the argument; otherwise
    /// <paramref name="defaultArgs"/> are, when given (an IDE's <c>--root ${workspaceFolder}</c>).
    /// <paramref name="typed"/> adds <c>"type": "stdio"</c>, which VS Code's format has.
    /// </summary>
    public static JsonObject Entry(JsonNode? existing, string command, string? dbPath, string? dotnetRoot, bool live,
        IReadOnlyList<string>? defaultArgs = null, bool typed = false)
    {
        var entry = existing is JsonObject current ? (JsonObject)current.DeepClone() : [];
        if (entry["command"]?.GetValueKind() != JsonValueKind.String || entry["command"]!.GetValue<string>() != command)
        {
            entry.Remove("args");
        }

        if (typed)
        {
            entry["type"] = "stdio";
        }

        entry["command"] = command;
        if (dbPath is not null)
        {
            entry["args"] = new JsonArray(dbPath);
        }
        else if (defaultArgs is not null)
        {
            entry["args"] = new JsonArray(defaultArgs.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
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
    /// Puts <paramref name="entryFor"/>'s result in as <c>&lt;serversKey&gt;.codegraph</c> in <paramref name="existingJson"/>
    /// (a missing or empty file is an empty config) and returns the new text, or null when nothing changed.
    /// </summary>
    public static string? MergeConfig(string? existingJson, Func<JsonNode?, JsonObject> entryFor, string serversKey = "mcpServers")
    {
        var root = string.IsNullOrWhiteSpace(existingJson)
            ? []
            : JsonNode.Parse(existingJson, documentOptions: ReadOptions) as JsonObject
              ?? throw new InvalidDataException("the file is not a JSON object");
        if (root[serversKey] is not JsonObject servers)
        {
            servers = [];
            root[serversKey] = servers;
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
    /// Writes the repo's config for <paramref name="target"/>, for each of <paramref name="clients"/>, and prints what
    /// it did. <paramref name="dbPath"/> is non-null only for an explicit --db, which the server then needs as an
    /// argument. False when it could not.
    /// </summary>
    public static bool Configure(string target, string? dbPath, bool live, IReadOnlyList<McpClient> clients, TextWriter output)
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

        if (!WriteClients(target, server, dbPath, dotnetRoot, live, clients, output))
        {
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

        if (clients is [var only] && only == McpClient.Claude)
        {
            output.WriteLine("  other clients  add --client copilot, cursor, gemini, codex, roo, kilo or continue (or all)");
        }

        return true;
    }

    /// <summary>Writes the server entry into each client's config for <paramref name="target"/>; false at the first that fails.</summary>
    internal static bool WriteClients(string target, string server, string? dbPath, string? dotnetRoot, bool live, IReadOnlyList<McpClient> clients, TextWriter output)
    {
        var root = RepoRoot(target);
        var db = dbPath?.Replace('\\', '/');
        foreach (var client in clients)
        {
            IReadOnlyList<string>? rootArgs = client.Root switch
            {
                RootHint.WorkspaceVariable => WorkspaceRootArgs,
                RootHint.Absolute => ["--root", root.Replace('\\', '/')],
                _ => null,
            };
            var folder = client.BesideSolution ? Path.GetDirectoryName(target)! : root;
            var ok = client.ConfigFile is null
                ? ConfigureCodex(server, db, dotnetRoot, live, output)
                : WriteConfig(Path.Combine(folder, client.ConfigFile), client, current =>
                    Shaped(Entry(Plain(current, client.Shape), server, db, dotnetRoot, live, rootArgs, client.Shape == EntryShape.Typed), current, client.Shape), output);
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A client's existing entry as <c>command</c>/<c>args</c>/<c>env</c>, so <see cref="Entry"/> can merge it. A
    /// <see cref="EntryShape.Local"/> entry's command array splits into command and args, and its
    /// <c>environment</c> (or <c>env</c>) becomes <c>env</c>; its other keys stay for <see cref="Shaped"/>.
    /// </summary>
    internal static JsonNode? Plain(JsonNode? existing, EntryShape shape)
    {
        if (shape != EntryShape.Local || existing is not JsonObject local)
        {
            return existing;
        }

        var plain = new JsonObject();
        if (local["command"] is JsonArray { Count: > 0 } command)
        {
            plain["command"] = command[0]!.DeepClone();
            if (command.Count > 1)
            {
                plain["args"] = new JsonArray(command.Skip(1).Select(a => a!.DeepClone()).ToArray());
            }
        }

        if ((local["environment"] ?? local["env"]) is JsonObject env)
        {
            plain["env"] = env.DeepClone();
        }

        return plain;
    }

    /// <summary>
    /// <paramref name="entry"/> in the client's shape. For <see cref="EntryShape.Local"/>: <c>type</c> local, one
    /// <c>command</c> array, <c>environment</c>; keys of the existing entry this does not set (<c>enabled</c>,
    /// <c>timeout</c>) are kept.
    /// </summary>
    internal static JsonObject Shaped(JsonObject entry, JsonNode? existing, EntryShape shape)
    {
        if (shape != EntryShape.Local)
        {
            return entry;
        }

        var local = existing is JsonObject current ? (JsonObject)current.DeepClone() : [];
        local.Remove("env");
        local.Remove("environment");
        local["type"] = "local";
        var command = new JsonArray(JsonValue.Create(entry["command"]!.GetValue<string>()));
        foreach (var arg in entry["args"] as JsonArray ?? [])
        {
            command.Add(arg!.DeepClone());
        }

        local["command"] = command;
        if (entry["env"] is JsonObject env)
        {
            local["environment"] = env.DeepClone();
        }

        return local;
    }

    /// <summary>Merges the codegraph entry into one client's JSON config file, creating its folder if needed.</summary>
    private static bool WriteConfig(string configPath, McpClient client, Func<JsonNode?, JsonObject> entryFor, TextWriter output)
    {
        var shown = client.ConfigFile!.Replace('\\', '/');
        try
        {
            var existing = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
            var merged = MergeConfig(existing, entryFor, client.ServersKey);
            if (merged is null)
            {
                output.WriteLine($"  {shown,-14} codegraph entry already up to date for {client.Display} ({configPath})");
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                File.WriteAllText(configPath, merged);
                output.WriteLine($"  {shown,-14} {(existing is null ? "created" : "updated")} with the codegraph server for {client.Display} ({configPath})");
            }

            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"  cannot update {configPath}: {e.Message}. Fix or move it, then re-run.");
            return false;
        }
    }

    /// <summary>
    /// Codex keeps its servers in its global config, and starts them in the folder it runs in, so one entry with no
    /// arguments serves every repo. Set through <c>codex mcp add</c>, keeping hand-added env from the current entry
    /// (<c>codex mcp get --json</c>); without the codex CLI, prints the entry to paste. An explicit --db would pin
    /// every repo to one graph, so it is not written there.
    /// </summary>
    private static bool ConfigureCodex(string server, string? dbPath, string? dotnetRoot, bool live, TextWriter output)
    {
        if (dbPath is not null)
        {
            output.WriteLine("  codex          skipped: Codex's config is global, and --db would point every repo at this one graph");
            return true;
        }

        // npm installs Codex on Windows as codex.cmd, which a process start does not find by its bare name.
        var codex = "codex";
        var current = Run(codex, ["mcp", "get", "codegraph", "--json"]);
        if (current is null && OperatingSystem.IsWindows())
        {
            codex = "codex.cmd";
            current = Run(codex, ["mcp", "get", "codegraph", "--json"]);
        }

        if (current is null)
        {
            var wanted = Entry(null, server, null, dotnetRoot, live);
            output.WriteLine("  codex          the codex CLI is not on PATH; add this to ~/.codex/config.toml:");
            foreach (var line in CodexToml(wanted))
            {
                output.WriteLine($"                   {line}");
            }

            return true;
        }

        var existing = current.Value.Exit == 0 ? CodexEntry(current.Value.Output) : null;
        var entry = Entry(existing, server, null, dotnetRoot, live);
        if (existing is not null && JsonNode.DeepEquals(existing, entry))
        {
            output.WriteLine("  codex          codegraph entry already up to date in Codex's global config");
            return true;
        }

        List<string> add = ["mcp", "add", "codegraph"];
        foreach (var (key, value) in entry["env"] as JsonObject ?? [])
        {
            add.AddRange(["--env", $"{key}={value!.GetValue<string>()}"]);
        }

        add.AddRange(["--", server]);
        add.AddRange((entry["args"] as JsonArray ?? []).Select(a => a!.GetValue<string>()));
        var result = Run(codex, add);
        if (result is not { Exit: 0 })
        {
            output.WriteLine($"  codex          codex mcp add failed: {result?.Output.Trim()}");
            return false;
        }

        output.WriteLine($"  codex          {(existing is null ? "added" : "updated")} codegraph in Codex's global config; it serves every repo, finding each one's graph from the folder Codex runs in");
        return true;
    }

    /// <summary>The <c>command</c>, <c>args</c> and <c>env</c> of the entry <c>codex mcp get --json</c> prints, as an .mcp.json-style object.</summary>
    internal static JsonObject? CodexEntry(string json)
    {
        try
        {
            if (JsonNode.Parse(json)?["transport"] is not JsonObject { } transport || transport["command"] is not JsonValue command)
            {
                return null;
            }

            var entry = new JsonObject { ["command"] = command.GetValue<string>() };
            if (transport["args"] is JsonArray { Count: > 0 } args)
            {
                entry["args"] = args.DeepClone();
            }

            if (transport["env"] is JsonObject { Count: > 0 } env)
            {
                entry["env"] = env.DeepClone();
            }

            return entry;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>An entry as the TOML table Codex reads.</summary>
    internal static IEnumerable<string> CodexToml(JsonObject entry)
    {
        static string Quote(string value) => JsonSerializer.Serialize(value);
        yield return "[mcp_servers.codegraph]";
        yield return $"command = {Quote(entry["command"]!.GetValue<string>())}";
        if (entry["args"] is JsonArray args)
        {
            yield return $"args = [{string.Join(", ", args.Select(a => Quote(a!.GetValue<string>())))}]";
        }

        if (entry["env"] is JsonObject env)
        {
            yield return $"env = {{ {string.Join(", ", env.Select(kv => $"{kv.Key} = {Quote(kv.Value!.GetValue<string>())}"))} }}";
        }
    }

    /// <summary>Runs a command and returns its exit code and output, or null when it cannot start (not installed).</summary>
    private static (int Exit, string Output)? Run(string file, IEnumerable<string> arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return null;
            }

            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
            {
                process.Kill(entireProcessTree: true);
                return (-1, "timed out");
            }

            Task.WaitAll(output, error);
            return (process.ExitCode, process.ExitCode == 0 ? output.Result : error.Result + output.Result);
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
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
