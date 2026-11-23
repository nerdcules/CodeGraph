using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Client;

namespace CodeGraph.Cli;

/// <summary>
/// The question log (issue #18, Part 2): <c>codegraph hook</c> is a Claude Code hook for UserPromptSubmit, PostToolUse
/// and Stop. It writes one line per question to <c>questions.jsonl</c> beside the graph: the prompt, the mode
/// (CODEGRAPH_MODE), and the question's shape (<see cref="QuestionShape"/>). <c>codegraph questions</c> reads the log back
/// and adds each question's tokens from the session transcript.
/// <para>
/// It runs only where hooks register it, writes nothing when CODEGRAPH_TELEMETRY=off or no graph is found, prints
/// nothing and always exits 0, so it never changes a session (stdout of a UserPromptSubmit hook would reach the
/// agent). Tool results are kept in a pending file only until the question's Stop event, then deleted.
/// </para>
/// Input fields used, as Claude Code 2.1.330 sent them in a probe: every event has session_id, prompt_id, cwd,
/// transcript_path and hook_event_name; UserPromptSubmit has prompt; PostToolUse has tool_name, tool_input and
/// tool_response (a list of {type, text} blocks for an MCP tool, {stdout, stderr, ...} for Bash).
/// </summary>
internal static class QuestionLog
{
    public const string FileName = "questions.jsonl";
    private const string PendingFolder = "questions-pending";

    public static Task<int> HookAsync(TextReader stdin, TextWriter stderr) => HookAsync(stdin, stderr, Directory);

    /// <summary>The hook, with the lookup of the graph's folder from the session's cwd given (for tests).</summary>
    internal static async Task<int> HookAsync(TextReader stdin, TextWriter stderr, Func<string?, string?> folderOf)
    {
        try
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CODEGRAPH_TELEMETRY"), "off", StringComparison.OrdinalIgnoreCase)
                || JsonNode.Parse(await stdin.ReadToEndAsync()) is not JsonObject e
                || Text(e, "session_id") is not { Length: > 0 } session
                || folderOf(Text(e, "cwd")) is not { } folder)
            {
                return 0;
            }

            var pending = Path.Combine(folder, PendingFolder, Safe(session) + ".jsonl");
            switch (Text(e, "hook_event_name"))
            {
                case "UserPromptSubmit":
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(pending)!);
                    await File.WriteAllTextAsync(pending, new JsonObject
                    {
                        ["at"] = Now(),
                        ["session"] = session,
                        ["promptId"] = Text(e, "prompt_id"),
                        ["prompt"] = Text(e, "prompt"),
                        ["mode"] = Environment.GetEnvironmentVariable("CODEGRAPH_MODE"),
                        ["transcript"] = Text(e, "transcript_path"),
                    }.ToJsonString() + "\n");
                    break;
                case "PostToolUse" when File.Exists(pending):
                    var input = e["tool_input"] as JsonObject;
                    await File.AppendAllTextAsync(pending, new JsonObject
                    {
                        ["name"] = Text(e, "tool_name") ?? "?",
                        ["command"] = Text(e, "tool_name") == "Bash" ? input?["command"]?.GetValue<string>() : null,
                        ["arguments"] = input?.ToJsonString(),
                        ["result"] = ResultText(e["tool_response"]),
                    }.ToJsonString() + "\n");
                    break;
                case "Stop" when File.Exists(pending):
                    var lines = (await File.ReadAllLinesAsync(pending)).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
                    File.Delete(pending);
                    var question = lines[0];
                    var calls = lines.Skip(1).Select(c => new ShapeCall(Text(c, "name") ?? "?", Text(c, "command"), Text(c, "arguments"), Text(c, "result") ?? "")).ToList();
                    var shape = QuestionShape.Of(calls, Text(question, "prompt") ?? "");
                    var tools = new JsonObject();
                    foreach (var group in calls.GroupBy(c => c.Name).OrderBy(g => g.Key, StringComparer.Ordinal))
                    {
                        tools[group.Key] = group.Count();
                    }

                    question["startedAt"] = question["at"]!.DeepClone();
                    question["at"] = Now();
                    question["graphSteps"] = shape.GraphCalls;
                    question["largestResultChars"] = shape.LargestResultChars;
                    question["totalResultChars"] = shape.TotalResultChars;
                    question["chained"] = shape.Chained;
                    question["tools"] = tools;
                    await File.AppendAllTextAsync(Path.Combine(folder, FileName), question.ToJsonString() + "\n");
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            // A logger must not disturb the session: Claude never sees the stderr of a hook that exits 0.
            await stderr.WriteLineAsync($"codegraph hook: {ex.Message}");
        }

        return 0;
    }

    /// <summary>
    /// The log with each question's tokens: the usage of the session's main-thread assistant messages (each message id
    /// once) timestamped from the question's start to its Stop. Null when the transcript is gone.
    /// </summary>
    public static JsonObject Read(string folder, int limit)
    {
        var path = Path.Combine(folder, FileName);
        var questions = File.Exists(path) ? File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()).ToList() : [];
        var transcripts = new Dictionary<string, List<(DateTimeOffset At, JsonObject Usage)>>(StringComparer.Ordinal);
        foreach (var q in questions)
        {
            var tokens = Text(q, "transcript") is { } t && Usage(t, transcripts) is { } usage
                && DateTimeOffset.TryParse(Text(q, "startedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var from)
                && DateTimeOffset.TryParse(Text(q, "at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var to)
                ? Sum(usage.Where(u => u.At >= from && u.At <= to).Select(u => u.Usage))
                : null;
            q["tokens"] = tokens;
        }

        var summary = new JsonArray();
        foreach (var group in questions.GroupBy(q => (Mode: Text(q, "mode") ?? "unset", Steps: Bucket(q["graphSteps"]?.GetValue<int>() ?? 0)))
                     .OrderBy(g => g.Key.Mode, StringComparer.Ordinal).ThenBy(g => g.Key.Steps, StringComparer.Ordinal))
        {
            var totals = group.Select(q => q["tokens"]?["total"]?.GetValue<long>()).OfType<long>().Order().ToList();
            summary.Add((JsonNode)new JsonObject
            {
                ["mode"] = group.Key.Mode,
                ["graphSteps"] = group.Key.Steps,
                ["questions"] = group.Count(),
                ["withTokens"] = totals.Count,
                ["tokensMedian"] = totals.Count == 0 ? null : (totals.Count % 2 == 1 ? totals[totals.Count / 2] : (totals[totals.Count / 2 - 1] + totals[totals.Count / 2]) / 2.0),
            });
        }

        var shown = limit == 0 ? questions : questions.TakeLast(limit).ToList();
        return new JsonObject
        {
            ["log"] = path,
            ["summary"] = summary,
            ["items"] = new JsonArray(shown.Select(q => (JsonNode)q.DeepClone()).ToArray()),
            ["total"] = questions.Count,
            ["truncated"] = shown.Count < questions.Count,
        };
    }

    /// <summary>The folder of the graph this session's cwd uses, or null when there is none.</summary>
    internal static string? Directory(string? cwd)
    {
        var db = CodeGraphClient.LocateFrom(string.IsNullOrEmpty(cwd) ? System.IO.Directory.GetCurrentDirectory() : cwd);
        return File.Exists(db) ? Path.GetDirectoryName(db) : null;
    }

    /// <summary>The text a tool call returned: an MCP tool's text blocks, Bash's stdout and stderr, a string as it is.</summary>
    internal static string ResultText(JsonNode? response) => response switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray blocks => string.Join("\n", blocks.OfType<JsonObject>().Select(b => b["text"] is JsonValue t && t.TryGetValue<string>(out var s) ? s : "")),
        JsonObject o when o.ContainsKey("stdout") => string.Join("\n", new[] { Text(o, "stdout"), Text(o, "stderr") }.Where(s => !string.IsNullOrEmpty(s))),
        _ => response.ToJsonString(),
    };

    private static string Bucket(int steps) => steps switch { 0 => "0", 1 => "1", <= 3 => "2-3", _ => "4+" };

    private static List<(DateTimeOffset At, JsonObject Usage)>? Usage(string transcript, Dictionary<string, List<(DateTimeOffset, JsonObject)>> cache)
    {
        if (cache.TryGetValue(transcript, out var known))
        {
            return known;
        }

        if (!File.Exists(transcript))
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<(DateTimeOffset, JsonObject)>();
        foreach (var line in File.ReadLines(transcript))
        {
            if (!line.Contains("\"assistant\"", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(line) is JsonObject entry && Text(entry, "type") == "assistant"
                    && entry["isSidechain"]?.GetValue<bool>() != true
                    && entry["message"] is JsonObject message && Text(message, "id") is { } id && seen.Add(id)
                    && message["usage"] is JsonObject usage
                    && DateTimeOffset.TryParse(Text(entry, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                {
                    list.Add((at, usage));
                }
            }
            catch (JsonException)
            {
                // A line being written as we read: skip it.
            }
        }

        cache[transcript] = list;
        return list;
    }

    private static JsonObject? Sum(IEnumerable<JsonObject> usages)
    {
        long input = 0, output = 0, read = 0, creation = 0, calls = 0;
        foreach (var u in usages)
        {
            calls++;
            input += Long(u, "input_tokens");
            output += Long(u, "output_tokens");
            read += Long(u, "cache_read_input_tokens");
            creation += Long(u, "cache_creation_input_tokens");
        }

        return calls == 0 ? null : new JsonObject
        {
            ["apiCalls"] = calls,
            ["input"] = input,
            ["output"] = output,
            ["cacheRead"] = read,
            ["cacheCreation"] = creation,
            ["total"] = input + output + read + creation,
        };
    }

    private static long Long(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;

    private static string? Text(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static string Safe(string session)
    {
        var sb = new StringBuilder(session.Length);
        foreach (var c in session)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }

        return sb.ToString();
    }
}
