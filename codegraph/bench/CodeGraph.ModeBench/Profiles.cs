using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Contracts;

namespace CodeGraph.ModeBench;

/// <summary>
/// Renders bench/profiles/&lt;mode&gt; into a working copy: CLAUDE.md (the mode's snippet), .claude/settings.json
/// (permissions, auto memory off), .mcp.json (the codegraph server with CODEGRAPH_MODE set), and for the lite modes
/// codegraph.client.json, plus for lite codegraph-lite.md and an empty scratch/ folder. Switching a copy to another
/// mode first removes what the previous switch wrote, listed in .codegraph-mode; a file it did not write is never
/// overwritten.
/// </summary>
internal static class Profiles
{
    public const string Manifest = ".codegraph-mode";

    public static IReadOnlyList<string> Render(BenchConfig config, string mode, string directory)
    {
        mode = CodeGraphMode.Parse(mode);
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        Clear(directory);

        var scratch = Path.Combine(directory, "scratch");
        var values = Values(config, mode, directory, scratch);
        var source = Path.Combine(config.ProfilesDir, mode);
        var written = new List<string>();

        void Write(string relative, string text)
        {
            var target = Path.Combine(directory, relative);
            if (File.Exists(target))
            {
                throw new IOException($"{target} exists and was not written by a switch; move it away first");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, Fill(text, values, json: relative.EndsWith(".json", StringComparison.Ordinal)));
            written.Add(relative);
        }

        Write("CLAUDE.md", File.ReadAllText(Path.Combine(source, "CLAUDE.md")));
        Write(Path.Combine(".claude", "settings.json"), File.ReadAllText(Path.Combine(source, ".claude", "settings.json")));
        Write(".mcp.json", File.ReadAllText(Path.Combine(source, "mcp.json")));
        if (mode != CodeGraphMode.Direct)
        {
            Write(CodeGraph.Client.CodeGraphClient.ConfigFileName, JsonSerializer.Serialize(new ClientConfig(config.Db), ContractsJson.Default.ClientConfig));
        }

        if (mode == CodeGraphMode.Lite)
        {
            Write("codegraph-lite.md", File.ReadAllText(config.LiteDoc));
            Directory.CreateDirectory(scratch);
        }

        // Every rendered file must parse where it is JSON; a broken settings file is ignored silently by claude -p.
        foreach (var json in written.Where(f => f.EndsWith(".json", StringComparison.Ordinal)))
        {
            JsonNode.Parse(File.ReadAllText(Path.Combine(directory, json)));
        }

        File.WriteAllLines(Path.Combine(directory, Manifest), [mode, .. written]);
        return written;
    }

    /// <summary>The mode a working copy is in, or null.</summary>
    public static string? ModeOf(string directory)
    {
        var manifest = Path.Combine(directory, Manifest);
        return File.Exists(manifest) ? File.ReadLines(manifest).FirstOrDefault() : null;
    }

    private static void Clear(string directory)
    {
        var manifest = Path.Combine(directory, Manifest);
        if (!File.Exists(manifest))
        {
            return;
        }

        foreach (var relative in File.ReadAllLines(manifest).Skip(1))
        {
            File.Delete(Path.Combine(directory, relative));
        }

        File.Delete(manifest);
    }

    public static Dictionary<string, string> Values(BenchConfig config, string mode, string directory, string scratch)
    {
        var dotnetRoot = Path.GetDirectoryName(Path.GetFullPath(config.McpDotnet))!;
        string ServerEnv(string serverMode) => JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [CodeGraphMode.Variable] = serverMode,
            // The server writes telemetry beside the graph, inside the repo being analysed; the bench writes nothing there.
            ["CODEGRAPH_TELEMETRY"] = "off",
            ["DOTNET_ROOT"] = dotnetRoot,
        });

        return new Dictionary<string, string>
        {
            ["TARGET_REPO"] = config.RepoPath,
            ["RUN_DIR"] = directory,
            ["SCRATCH"] = scratch,
            ["DB"] = config.Db,
            ["CLIENT_PROJECT"] = config.ClientProject,
            ["CLI_DIR"] = config.CliDir,
            ["MCP_COMMAND"] = Path.GetFullPath(config.McpDotnet),
            ["MCP_ARGS"] = JsonSerializer.Serialize(new[] { config.McpServer, config.Db }),
            ["MCP_ENV_DIRECT"] = ServerEnv(CodeGraphMode.Direct),
            ["MCP_ENV_LITE"] = ServerEnv(CodeGraphMode.Lite),
            ["MCP_ENV_LITE_CLI"] = ServerEnv(CodeGraphMode.LiteCli),
            ["MODE"] = mode,
        };
    }

    /// <summary>The values that are JSON already (an array or object), filled into a JSON file as they are.</summary>
    private static readonly HashSet<string> JsonFragments = ["MCP_ARGS", "MCP_ENV_DIRECT", "MCP_ENV_LITE", "MCP_ENV_LITE_CLI"];

    /// <summary>Replaces each {{KEY}}. In a JSON file every other value lands inside a string and is JSON-escaped: a
    /// Windows path's backslashes would otherwise break it.</summary>
    public static string Fill(string text, IReadOnlyDictionary<string, string> values, bool json = false)
    {
        foreach (var (key, value) in values)
        {
            var filled = json && !JsonFragments.Contains(key)
                ? JsonEncodedText.Encode(value, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping).Value
                : value;
            text = text.Replace("{{" + key + "}}", filled, StringComparison.Ordinal);
        }

        var left = text.IndexOf("{{", StringComparison.Ordinal);
        return left < 0 ? text : throw new InvalidDataException($"unfilled placeholder at: {text[left..Math.Min(text.Length, left + 40)]}");
    }
}
