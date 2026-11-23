using System.Text.Json.Nodes;
using CodeGraph.Cli;

namespace CodeGraph.Tests;

/// <summary>The question-log hook (issue #18, Part 2): events in, one line per question out, tokens from the transcript.</summary>
public sealed class QuestionLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"));

    public QuestionLogTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => TempDb.TryDelete(_folder);

    private async Task Send(JsonObject e)
    {
        var stderr = new StringWriter();
        Assert.Equal(0, await QuestionLog.HookAsync(new StringReader(e.ToJsonString()), stderr, _ => _folder));
        Assert.Equal("", stderr.ToString());
    }

    private static JsonObject Event(string name, JsonObject? extra = null)
    {
        var e = new JsonObject { ["hook_event_name"] = name, ["session_id"] = "s1", ["prompt_id"] = "p1", ["cwd"] = "/repo", ["transcript_path"] = "/nowhere.jsonl" };
        foreach (var (key, value) in extra ?? [])
        {
            e[key] = value?.DeepClone();
        }

        return e;
    }

    [Fact]
    public async Task A_question_becomes_one_line_with_its_shape_and_the_pending_file_goes()
    {
        await Send(Event("UserPromptSubmit", new() { ["prompt"] = "Who calls Place?" }));
        await Send(Event("PostToolUse", new()
        {
            ["tool_name"] = "mcp__codegraph__find_symbol",
            ["tool_input"] = new JsonObject { ["query"] = "Place" },
            ["tool_response"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "method  Shop.Orders.OrderService.Place  [a1b2c3d4]" }),
        }));
        await Send(Event("PostToolUse", new()
        {
            ["tool_name"] = "Bash",
            ["tool_input"] = new JsonObject { ["command"] = "codegraph callers a1b2c3d4 | jq .hits.total" },
            ["tool_response"] = new JsonObject { ["stdout"] = "3", ["stderr"] = "", ["interrupted"] = false },
        }));
        await Send(Event("Stop", new() { ["last_assistant_message"] = "3" }));

        var lines = File.ReadAllLines(Path.Combine(_folder, QuestionLog.FileName));
        var q = JsonNode.Parse(Assert.Single(lines))!;
        Assert.Equal("Who calls Place?", q["prompt"]!.GetValue<string>());
        Assert.Equal(2, q["graphSteps"]!.GetValue<int>());
        Assert.Equal(50, q["largestResultChars"]!.GetValue<long>());
        Assert.True(q["chained"]!.GetValue<bool>());
        Assert.Equal(1, q["tools"]!["Bash"]!.GetValue<int>());
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "questions-pending")));
    }

    [Fact]
    public async Task Tool_calls_before_any_prompt_and_a_stop_without_one_write_nothing()
    {
        await Send(Event("PostToolUse", new() { ["tool_name"] = "Bash", ["tool_input"] = new JsonObject { ["command"] = "codegraph status" } }));
        await Send(Event("Stop"));
        Assert.False(File.Exists(Path.Combine(_folder, QuestionLog.FileName)));
    }

    [Fact]
    public async Task Bad_input_still_exits_zero_and_prints_nothing_to_stdout()
    {
        var stderr = new StringWriter();
        Assert.Equal(0, await QuestionLog.HookAsync(new StringReader("not json"), stderr, _ => _folder));
        Assert.Equal(0, await QuestionLog.HookAsync(new StringReader("{}"), stderr, _ => _folder));
        Assert.Equal(0, await QuestionLog.HookAsync(new StringReader(Event("Stop").ToJsonString()), stderr, _ => null));
    }

    [Fact]
    public void Result_text_is_the_mcp_blocks_or_bash_output()
    {
        Assert.Equal("a\nb", QuestionLog.ResultText(new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "a" }, new JsonObject { ["type"] = "text", ["text"] = "b" })));
        Assert.Equal("out\nerr", QuestionLog.ResultText(new JsonObject { ["stdout"] = "out", ["stderr"] = "err" }));
        Assert.Equal("failed", QuestionLog.ResultText(JsonValue.Create("failed")));
    }

    [Fact]
    public void Tokens_come_from_the_transcript_once_per_message_inside_the_question_and_not_from_subagents()
    {
        var transcript = Path.Combine(_folder, "t.jsonl");
        static string Line(string id, string at, int output, bool sidechain = false) =>
            new JsonObject
            {
                ["type"] = "assistant", ["isSidechain"] = sidechain, ["timestamp"] = at,
                ["message"] = new JsonObject { ["id"] = id, ["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = output, ["cache_read_input_tokens"] = 100, ["cache_creation_input_tokens"] = 10 } },
            }.ToJsonString();
        File.WriteAllLines(transcript,
        [
            Line("m0", "2026-11-23T09:59:00Z", 5),          // before the question
            Line("m1", "2026-11-23T10:00:01Z", 20),
            Line("m1", "2026-11-23T10:00:01Z", 20),         // the same message, another content block
            Line("m2", "2026-11-23T10:00:05Z", 30),
            Line("m3", "2026-11-23T10:00:06Z", 99, sidechain: true),
            Line("m4", "2026-11-23T10:01:00Z", 7),          // after the Stop
        ]);
        File.WriteAllText(Path.Combine(_folder, QuestionLog.FileName), new JsonObject
        {
            ["startedAt"] = "2026-11-23T10:00:00Z", ["at"] = "2026-11-23T10:00:10Z", ["mode"] = "lite-cli", ["graphSteps"] = 1, ["transcript"] = transcript,
        }.ToJsonString() + "\n");

        var read = QuestionLog.Read(_folder, 25);
        var tokens = read["items"]![0]!["tokens"]!;
        Assert.Equal(2, tokens["apiCalls"]!.GetValue<long>());
        Assert.Equal(50, tokens["output"]!.GetValue<long>());
        Assert.Equal(2 + 50 + 200 + 20, tokens["total"]!.GetValue<long>());
        Assert.Equal("1", read["summary"]![0]!["graphSteps"]!.GetValue<string>());
    }
}
