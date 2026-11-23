using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using CodeGraph.Contracts;

namespace CodeGraph.ModeBench;

/// <summary>One headless Claude Code session: how it is launched for a mode, and what its stream-json said.</summary>
internal sealed record ClaudeLaunch(string Prompt, string Mode, string WorkingDirectory, int MaxTurns, TimeSpan Timeout, bool LoadMcp = true, string? ToolsOverride = null);

/// <summary>A tool call: its name, its input (a Bash command or a file path), how long until its result arrived, the
/// result, and the whole input as JSON (for the question's shape: which later call reused what).</summary>
internal sealed record ToolCall(string Name, string? Input, double Seconds, bool IsError, string Result, string? Arguments = null);

/// <summary>
/// Everything recorded about a session. Token and cost fields come from the final result message of
/// <c>--output-format stream-json</c>; a field the CLI did not print is null. <see cref="FirstContextTokens"/> is the
/// first API call's input (input + cache creation + cache read): the session's fixed start.
/// </summary>
internal sealed record ClaudeResult(
    string? Subtype,
    bool IsError,
    bool TimedOut,
    int ExitCode,
    string? Answer,
    long? InputTokens,
    long? OutputTokens,
    long? CacheReadTokens,
    long? CacheCreationTokens,
    double? CostUsd,
    int? Turns,
    double WallSeconds,
    double? DurationMs,
    long? FirstContextTokens,
    int ApiCalls,
    IReadOnlyList<ToolCall> Tools,
    IReadOnlyList<string> PermissionDenials,
    IReadOnlyList<string> Models,
    string? Error)
{
    public long? TotalTokens => InputTokens is null || OutputTokens is null || CacheReadTokens is null || CacheCreationTokens is null
        ? null
        : InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
}

internal static class ClaudeRunner
{
    /// <summary>Built-in tools per mode. Every mode has ToolSearch, as Claude Code does by default (MCP tools load on demand).</summary>
    public static string ToolsFor(string mode) => mode switch
    {
        CodeGraphMode.Direct => "ToolSearch",
        CodeGraphMode.Lite => "Bash,Read,Write,Edit,ToolSearch",
        CodeGraphMode.LiteCli => "Bash,Read,ToolSearch",
        _ => throw new ArgumentException(mode),
    };

    /// <summary>
    /// Variables a Claude Code session sets for its children: a benchmark started from inside one would otherwise
    /// inherit them (CLAUDE_EFFORT changes the effort). Account and provider settings stay.
    /// </summary>
    private static readonly string[] SessionVariables =
        ["CLAUDECODE", "CLAUDE_PID", "CLAUDE_EFFORT", "CLAUDE_AGENT_SDK_VERSION", "MCP_CONNECTION_NONBLOCKING", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT"];

    private static readonly string[] KeptPrefixes = ["CLAUDE_CODE_USE_", "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_SKIP_", "CLAUDE_CODE_API_KEY"];

    public static IReadOnlyList<string> Arguments(BenchConfig config, ClaudeLaunch launch)
    {
        var settings = Path.Combine(launch.WorkingDirectory, ".claude", "settings.json");
        var args = new List<string>
        {
            "-p", launch.Prompt,
            "--model", config.Model,
            "--output-format", "stream-json", "--verbose",
            // Only the profile: no user settings, plugins, hooks or user CLAUDE.md; the profile's CLAUDE.md is the project's.
            "--setting-sources", "project",
            // Project allow rules apply only in a trusted workspace, and -p never asks; passing the file applies them.
            "--settings", settings,
            "--strict-mcp-config",
            "--tools", launch.ToolsOverride ?? ToolsFor(launch.Mode),
            "--permission-mode", "dontAsk",
            "--no-session-persistence",
            "--max-turns", launch.MaxTurns.ToString(CultureInfo.InvariantCulture),
        };
        args.Add("--mcp-config");
        args.Add(launch.LoadMcp ? Path.Combine(launch.WorkingDirectory, ".mcp.json") : """{"mcpServers":{}}""");
        return args;
    }

    public static async Task<ClaudeResult> RunAsync(BenchConfig config, ClaudeLaunch launch, string? transcriptPath, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(config.Claude)
        {
            WorkingDirectory = launch.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in Arguments(config, launch))
        {
            info.ArgumentList.Add(arg);
        }

        foreach (var name in info.Environment.Keys.Where(Inherited).ToList())
        {
            info.Environment.Remove(name);
        }

        info.Environment["CLAUDE_CODE_DISABLE_AUTO_MEMORY"] = "1";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment.Remove(CodeGraphMode.Variable);
        info.Environment.Remove("CODEGRAPH_DB");
        var path = (info.Environment.TryGetValue("PATH", out var p) ? p : "") ?? "";
        // The CLI is on PATH only in lite-cli, so no other mode can reach the graph through it.
        var parts = path.Split(Path.PathSeparator).Where(d => Path.GetFullPath(d.Length == 0 ? "." : d) != config.CliDir);
        info.Environment["PATH"] = string.Join(Path.PathSeparator, launch.Mode == CodeGraphMode.LiteCli ? parts.Prepend(config.CliDir) : parts);

        var watch = Stopwatch.StartNew();
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"could not start {config.Claude}");
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var transcript = transcriptPath is null ? null : new StreamWriter(transcriptPath);
        var parse = new StreamParse(watch);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(launch.Timeout);
        var timedOut = false;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (transcript is not null)
                {
                    await transcript.WriteLineAsync($"{watch.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)}\t{line}");
                }

                parse.Line(line);
            }

            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }

        watch.Stop();
        var errorText = (await stderr).Trim();
        return parse.Result(timedOut, process.ExitCode, watch.Elapsed.TotalSeconds, errorText.Length == 0 ? null : errorText[..Math.Min(errorText.Length, 2000)]);
    }

    private static bool Inherited(string name) =>
        SessionVariables.Contains(name, StringComparer.OrdinalIgnoreCase)
        || (name.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
            && !KeptPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Parses a saved transcript (each line "seconds\tjson") again: tool calls and figures, not timing.</summary>
    public static ClaudeResult Replay(string transcriptPath)
    {
        var parse = new StreamParse(new Stopwatch());
        foreach (var line in File.ReadLines(transcriptPath))
        {
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            parse.Line(tab >= 0 ? line[(tab + 1)..] : line);
        }

        return parse.Result(false, 0, 0, null);
    }

    /// <summary>Reads stream-json lines as they arrive; a tool's time is from its tool_use line to its tool_result line.</summary>
    private sealed class StreamParse(Stopwatch watch)
    {
        private readonly Dictionary<string, (string Name, string? Input, double Start, string? Arguments)> _pending = [];
        private readonly List<ToolCall> _tools = [];
        private readonly HashSet<string> _calls = [];
        private readonly HashSet<string> _models = [];
        private long? _firstContext;
        private JsonObject? _result;

        public void Line(string line)
        {
            JsonObject? message;
            try
            {
                message = JsonNode.Parse(line) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                return;
            }

            switch (message?["type"]?.GetValue<string>())
            {
                case "assistant" when message["message"] is JsonObject assistant:
                    if (assistant["id"]?.GetValue<string>() is { } id && _calls.Add(id) && _firstContext is null && assistant["usage"] is JsonObject usage)
                    {
                        _firstContext = Number(usage, "input_tokens") + Number(usage, "cache_creation_input_tokens") + Number(usage, "cache_read_input_tokens");
                    }

                    if (assistant["model"]?.GetValue<string>() is { } model)
                    {
                        _models.Add(model);
                    }

                    foreach (var block in assistant["content"]?.AsArray().OfType<JsonObject>() ?? [])
                    {
                        if (block["type"]?.GetValue<string>() == "tool_use" && block["id"]?.GetValue<string>() is { } toolId)
                        {
                            var input = block["input"] as JsonObject;
                            var text = input?["command"]?.GetValue<string>() ?? input?["file_path"]?.GetValue<string>() ?? input?["query"]?.ToString();
                            _pending[toolId] = (block["name"]?.GetValue<string>() ?? "?", text, watch.Elapsed.TotalSeconds, input?.ToJsonString());
                        }
                    }

                    break;
                case "user" when (message["message"]?["content"]) is JsonArray content:
                    foreach (var block in content.OfType<JsonObject>().Where(b => b["type"]?.GetValue<string>() == "tool_result"))
                    {
                        if (block["tool_use_id"]?.GetValue<string>() is { } toolId && _pending.Remove(toolId, out var call))
                        {
                            var result = block["content"] switch
                            {
                                JsonValue value => value.ToString(),
                                JsonArray parts => string.Join("\n", parts.OfType<JsonObject>().Select(part => part["text"]?.GetValue<string>() ?? "")),
                                _ => "",
                            };
                            _tools.Add(new ToolCall(call.Name, call.Input, watch.Elapsed.TotalSeconds - call.Start,
                                block["is_error"]?.GetValue<bool>() ?? false, result, call.Arguments));
                        }
                    }

                    break;
                case "result":
                    _result = message;
                    break;
            }
        }

        public ClaudeResult Result(bool timedOut, int exitCode, double wallSeconds, string? stderr)
        {
            var usage = _result?["usage"] as JsonObject;
            long? Field(string name) => usage?[name] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;
            var denials = _result?["permission_denials"]?.AsArray().OfType<JsonObject>()
                .Select(d => $"{d["tool_name"]}: {(d["tool_input"]?["command"] ?? d["tool_input"]?["file_path"])}").ToList() ?? [];
            // A tool whose result never came (a timeout, the turn cap) still counts as a call.
            var tools = _tools.Concat(_pending.Values.Select(p => new ToolCall(p.Name, p.Input, wallSeconds - p.Start, true, "(no result)", p.Arguments))).ToList();
            return new ClaudeResult(
                _result?["subtype"]?.GetValue<string>(),
                _result?["is_error"]?.GetValue<bool>() ?? true,
                timedOut,
                exitCode,
                _result?["result"]?.GetValue<string>(),
                Field("input_tokens"),
                Field("output_tokens"),
                Field("cache_read_input_tokens"),
                Field("cache_creation_input_tokens"),
                _result?["total_cost_usd"] is JsonValue cost && cost.TryGetValue<double>(out var usd) ? usd : null,
                _result?["num_turns"] is JsonValue turns && turns.TryGetValue<int>(out var t) ? t : null,
                wallSeconds,
                _result?["duration_ms"] is JsonValue ms && ms.TryGetValue<double>(out var d) ? d : null,
                _firstContext,
                _calls.Count,
                tools,
                denials,
                _models.Order().ToList(),
                stderr);
        }

        private static long Number(JsonObject usage, string name) => usage[name] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
    }
}
