using System.Text.Json;

namespace CodeGraph.Indexer;

/// <summary>An MCP server as Claude Code's init message lists it.</summary>
internal sealed record McpServerState(string Name, string Status);

/// <summary>
/// What one <c>claude -p --output-format stream-json --verbose</c> session reported about itself. Every figure is read
/// from the log; nothing is estimated. Token counts are the API's own usage fields.
/// </summary>
internal sealed record RunMetrics
{
    public string? Model { get; init; }

    public string? ClaudeVersion { get; init; }

    public IReadOnlyList<McpServerState> McpServers { get; init; } = [];

    /// <summary>The tools the session had, from the init message.</summary>
    public IReadOnlyList<string> Tools { get; init; } = [];

    /// <summary>Distinct API responses of the main conversation (subagents excluded).</summary>
    public int ModelCalls { get; init; }

    /// <summary>Input tokens over all model calls: uncached input, cache writes and cache reads.</summary>
    public long InputTokens { get; init; }

    public long CacheReadTokens { get; init; }

    /// <summary>The result message's output tokens, thinking included.</summary>
    public long OutputTokens { get; init; }

    /// <summary>The cost Claude Code reports (<c>total_cost_usd</c>).</summary>
    public double? CostUsd { get; init; }

    /// <summary>The input of the first model call: system prompt, tools, CLAUDE.md, memory and the prompt.</summary>
    public long FirstContext { get; init; }

    /// <summary>The input of the last model call.</summary>
    public long LastContext { get; init; }

    public long ContextGrowth => LastContext - FirstContext;

    /// <summary>Tool calls by tool name, in the order first seen.</summary>
    public IReadOnlyList<KeyValuePair<string, int>> ToolCalls { get; init; } = [];

    public int Denied { get; init; }

    public bool HasResult { get; init; }

    public bool IsError { get; init; }

    public string? Answer { get; init; }

    public int CodeGraphCalls => ToolCalls.Where(t => IsCodeGraphTool(t.Key)).Sum(t => t.Value);

    public int OtherToolCalls => ToolCalls.Where(t => !IsCodeGraphTool(t.Key)).Sum(t => t.Value);

    public static bool IsCodeGraphTool(string name) => name.StartsWith("mcp__codegraph__", StringComparison.Ordinal);
}

/// <summary>Reads a Claude Code stream-json log into <see cref="RunMetrics"/>.</summary>
internal static class ClaudeLog
{
    public static RunMetrics Parse(IEnumerable<string> lines)
    {
        string? model = null, version = null, answer = null;
        var servers = new List<McpServerState>();
        var tools = new List<string>();
        // One API response arrives as several assistant lines (one per content block) sharing an id and usage.
        var calls = new List<string>();
        var inputByCall = new Dictionary<string, (long Input, long CacheRead)>(StringComparer.Ordinal);
        var toolUses = new HashSet<string>(StringComparer.Ordinal);
        var toolCounts = new List<KeyValuePair<string, int>>();
        long outputTokens = 0;
        double? cost = null;
        int denied = 0;
        bool hasResult = false, isError = false;

        foreach (var line in lines)
        {
            if (line.Length == 0 || line[0] != '{')
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                switch (String(root, "type"))
                {
                    case "system" when String(root, "subtype") == "init":
                        model = String(root, "model");
                        version = String(root, "claude_code_version");
                        if (root.TryGetProperty("mcp_servers", out var list) && list.ValueKind == JsonValueKind.Array)
                        {
                            servers.AddRange(list.EnumerateArray().Select(s => new McpServerState(String(s, "name") ?? "?", String(s, "status") ?? "?")));
                        }

                        if (root.TryGetProperty("tools", out var toolList) && toolList.ValueKind == JsonValueKind.Array)
                        {
                            tools.AddRange(toolList.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!));
                        }

                        break;
                    case "assistant" when !HasParent(root) && root.TryGetProperty("message", out var message):
                        var id = String(message, "id");
                        if (id is not null && !inputByCall.ContainsKey(id))
                        {
                            calls.Add(id);
                            var usage = message.TryGetProperty("usage", out var u) ? u : default;
                            var cacheRead = Number(usage, "cache_read_input_tokens");
                            inputByCall[id] = (Number(usage, "input_tokens") + Number(usage, "cache_creation_input_tokens") + cacheRead, cacheRead);
                        }

                        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var block in content.EnumerateArray())
                            {
                                if (String(block, "type") == "tool_use" && String(block, "name") is { } name && toolUses.Add(String(block, "id") ?? Guid.NewGuid().ToString()))
                                {
                                    var at = toolCounts.FindIndex(t => t.Key == name);
                                    if (at < 0)
                                    {
                                        toolCounts.Add(new(name, 1));
                                    }
                                    else
                                    {
                                        toolCounts[at] = new(name, toolCounts[at].Value + 1);
                                    }
                                }
                            }
                        }

                        break;
                    case "result":
                        hasResult = true;
                        isError = root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True;
                        answer = String(root, "result");
                        cost = root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null;
                        outputTokens = root.TryGetProperty("usage", out var total) ? Number(total, "output_tokens") : 0;
                        denied = root.TryGetProperty("permission_denials", out var denials) && denials.ValueKind == JsonValueKind.Array ? denials.GetArrayLength() : 0;
                        break;
                }
            }
        }

        return new RunMetrics
        {
            Model = model,
            ClaudeVersion = version,
            McpServers = servers,
            Tools = tools,
            ModelCalls = calls.Count,
            InputTokens = inputByCall.Values.Sum(v => v.Input),
            CacheReadTokens = inputByCall.Values.Sum(v => v.CacheRead),
            OutputTokens = outputTokens,
            CostUsd = cost,
            FirstContext = calls.Count == 0 ? 0 : inputByCall[calls[0]].Input,
            LastContext = calls.Count == 0 ? 0 : inputByCall[calls[^1]].Input,
            ToolCalls = toolCounts,
            Denied = denied,
            HasResult = hasResult,
            IsError = isError,
            Answer = answer,
        };
    }

    private static bool HasParent(JsonElement root) =>
        root.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String;

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;
}
