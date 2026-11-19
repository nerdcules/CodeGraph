using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Core;
using CodeGraph.Indexer;

namespace CodeGraph.Tests;

/// <summary><c>codegraph-index compare</c>: the log it reads, the sessions it starts and the report it writes.</summary>
public sealed class CompareCommandTests
{
    /// <summary>Lines in the shape Claude Code 2.1.328 writes with <c>-p --output-format stream-json --verbose</c>.</summary>
    private static readonly string[] Log =
    [
        """{"type":"system","subtype":"init","model":"claude-opus-5-5","claude_code_version":"2.1.328","mcp_servers":[{"name":"codegraph","status":"connected"}],"tools":["Read","mcp__codegraph__find_symbol"]}""",
        """{"type":"rate_limit_event"}""",
        """{"type":"assistant","parent_tool_use_id":null,"message":{"id":"msg_1","usage":{"input_tokens":2,"cache_creation_input_tokens":4000,"cache_read_input_tokens":6000,"output_tokens":5},"content":[{"type":"thinking"}]}}""",
        """{"type":"assistant","parent_tool_use_id":null,"message":{"id":"msg_1","usage":{"input_tokens":2,"cache_creation_input_tokens":4000,"cache_read_input_tokens":6000,"output_tokens":5},"content":[{"type":"tool_use","id":"t1","name":"mcp__codegraph__find_symbol"}]}}""",
        """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"..."}]}}""",
        """{"type":"assistant","parent_tool_use_id":"t9","message":{"id":"msg_sub","usage":{"input_tokens":99999},"content":[{"type":"tool_use","id":"t9a","name":"Read"}]}}""",
        """{"type":"assistant","parent_tool_use_id":null,"message":{"id":"msg_2","usage":{"input_tokens":3,"cache_creation_input_tokens":500,"cache_read_input_tokens":10002,"output_tokens":2},"content":[{"type":"tool_use","id":"t2","name":"Read"},{"type":"tool_use","id":"t3","name":"mcp__codegraph__find_symbol"}]}}""",
        "not json",
        """{"type":"assistant","parent_tool_use_id":null,"message":{"id":"msg_3","usage":{"input_tokens":1,"cache_creation_input_tokens":200,"cache_read_input_tokens":10505,"output_tokens":1},"content":[{"type":"text","text":"done"}]}}""",
        """{"type":"result","subtype":"success","is_error":false,"result":"It has 3 callers.","total_cost_usd":0.125,"usage":{"output_tokens":987},"permission_denials":[{"tool_name":"Bash"}]}""",
    ];

    [Fact]
    public void Reads_a_session_log_counting_each_model_call_once_and_leaving_out_subagents()
    {
        var m = ClaudeLog.Parse(Log);

        Assert.Equal("claude-opus-5-5", m.Model);
        Assert.Equal("2.1.328", m.ClaudeVersion);
        Assert.Equal([new McpServerState("codegraph", "connected")], m.McpServers);
        Assert.Equal(3, m.ModelCalls);
        Assert.Equal(10_002 + 10_505 + 10_706, m.InputTokens);
        Assert.Equal(6_000 + 10_002 + 10_505, m.CacheReadTokens);
        Assert.Equal(987, m.OutputTokens);
        Assert.Equal(0.125, m.CostUsd);
        Assert.Equal(10_002, m.FirstContext);
        Assert.Equal(10_706, m.LastContext);
        Assert.Equal(704, m.ContextGrowth);
        Assert.Equal(2, m.CodeGraphCalls);
        Assert.Equal(1, m.OtherToolCalls);
        Assert.Equal(1, m.Denied);
        Assert.True(m.HasResult);
        Assert.False(m.IsError);
        Assert.Equal("It has 3 callers.", m.Answer);
    }

    [Fact]
    public void A_log_without_a_result_says_so()
    {
        var m = ClaudeLog.Parse(Log.Take(4));
        Assert.False(m.HasResult);
        Assert.Equal(1, m.ModelCalls);
        Assert.Contains("no result in the log", CompareCommand.Problems(CompareCommand.With, "0", m, "", "", null));
    }

    [Fact]
    public void The_two_conditions_differ_only_in_the_codegraph_server()
    {
        var options = CompareCommand.Parse(["why?", "--model", "opus", "--effort", "high"], TextWriter.Null)!;
        var server = new JsonObject { ["command"] = "/bin/codegraph-mcp" };
        var with = CompareCommand.Arguments(options, CompareCommand.ReadOnlyRules, server, CompareCommand.With);
        var without = CompareCommand.Arguments(options, CompareCommand.ReadOnlyRules, server, CompareCommand.Without);

        var withConfig = with[with.IndexOf("--mcp-config") + 1];
        var withoutConfig = without[without.IndexOf("--mcp-config") + 1];
        Assert.Equal("/bin/codegraph-mcp", JsonNode.Parse(withConfig)!["mcpServers"]!["codegraph"]!["command"]!.GetValue<string>());
        Assert.Equal("""{"mcpServers":{}}""", withoutConfig);
        Assert.Contains("mcp__codegraph", with);
        Assert.DoesNotContain("mcp__codegraph", without);
        Assert.Equal(without, with.Where(a => a != "mcp__codegraph").Select(a => a == withConfig ? withoutConfig : a));

        Assert.Equal(["-p", "why?"], with.Take(2));
        Assert.Equal("--verbose", with[^1]);
        foreach (var flag in new[] { "--strict-mcp-config", "--no-session-persistence", "--permission-mode", "dontAsk", "--tools", CompareCommand.BuiltInTools, "--model", "opus", "--effort", "high" })
        {
            Assert.Contains(flag, without);
        }
    }

    [Fact]
    public void No_rule_lets_a_session_write_a_file()
    {
        string[] writers = ["Bash(find", "Bash(sed", "Bash(awk", "Bash(sort", "Bash(uniq", "Bash(rg", "Bash(git log", "Bash(git show", "Bash(git diff", "Bash(git grep"];
        Assert.DoesNotContain(CompareCommand.ReadOnlyRules, rule => rule is "Edit" or "Write" or "NotebookEdit" or "Bash" || writers.Any(w => rule.StartsWith(w, StringComparison.Ordinal)));
        Assert.Equal("Read,Grep,Glob,Bash", CompareCommand.BuiltInTools);
    }

    [Fact]
    public void Drops_the_variables_a_Claude_Code_session_sets_and_keeps_the_users_own()
    {
        Assert.True(CompareCommand.Inherited("CLAUDECODE"));
        Assert.True(CompareCommand.Inherited("CLAUDE_EFFORT"));
        Assert.True(CompareCommand.Inherited("CLAUDE_CODE_ENTRYPOINT"));
        Assert.True(CompareCommand.Inherited("CLAUDE_CODE_SESSION_ID"));
        Assert.False(CompareCommand.Inherited("CLAUDE_CODE_USE_BEDROCK"));
        Assert.False(CompareCommand.Inherited("CLAUDE_CODE_OAUTH_TOKEN"));
        Assert.False(CompareCommand.Inherited("CLAUDE_CONFIG_DIR"));
        Assert.False(CompareCommand.Inherited("ANTHROPIC_API_KEY"));
        Assert.False(CompareCommand.Inherited("PATH"));
    }

    [Fact]
    public void A_run_does_not_count_without_its_server_with_a_server_it_should_not_have_or_with_an_edited_tree()
    {
        var connected = ClaudeLog.Parse(Log);
        var none = connected with { McpServers = [], Tools = ["Read"] };

        Assert.Empty(CompareCommand.Problems(CompareCommand.With, "0", connected, "a", "a", null));
        Assert.Empty(CompareCommand.Problems(CompareCommand.Without, "0", none, "a", "a", null));
        Assert.Contains("the codegraph server was missing", CompareCommand.Problems(CompareCommand.With, "0", none, "a", "a", null));
        Assert.Contains("the codegraph server was failed", CompareCommand.Problems(CompareCommand.With, "0", connected with { McpServers = [new("codegraph", "failed")] }, "a", "a", null));
        Assert.Contains("an MCP server was loaded", CompareCommand.Problems(CompareCommand.Without, "0", connected, "a", "a", null));
        Assert.Contains("the working tree changed during the run", CompareCommand.Problems(CompareCommand.Without, "0", none, "a", "a\n?? new.txt", null));
        Assert.Contains("exit timeout", CompareCommand.Problems(CompareCommand.Without, "timeout", none, "a", "a", null));
    }

    [Fact]
    public void Needs_a_prompt_and_takes_the_runs()
    {
        Assert.Null(CompareCommand.Parse([], TextWriter.Null));
        Assert.Null(CompareCommand.Parse(["--runs", "0", "q"], TextWriter.Null));
        Assert.Null(CompareCommand.Parse(["q", "--bogus"], TextWriter.Null));
        var options = CompareCommand.Parse(["q", "--runs", "5", "--allow", "Bash(find *)", "-y"], TextWriter.Null)!;
        Assert.Equal("q", options.Prompt);
        Assert.Equal(5, options.Runs);
        Assert.Equal(["Bash(find *)"], options.Allow);
        Assert.True(options.Yes);
        Assert.Equal(3, CompareCommand.Parse(["q"], TextWriter.Null)!.Runs);
    }

    [Fact]
    public void Takes_the_median()
    {
        Assert.Equal(2, CompareCommand.Median([3, 1, 2]));
        Assert.Equal(2.5, CompareCommand.Median([4, 1, 2, 3]));
    }

    [Fact]
    public async Task Without_a_terminal_it_runs_nothing_unless_told_yes()
    {
        using var repo = FakeRepo.Create();
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await CompareCommand.RunAsync(["q", "--claude", repo.Claude], output, error, "9.9.9", ask: null, repo.Root, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
        Assert.Contains("--yes", error.ToString());
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".codegraph", "compare")));
    }

    [Fact]
    public async Task Runs_each_condition_as_its_own_session_alternating_the_order_and_reports_from_the_logs()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stand-in claude is a shell script");
        using var repo = FakeRepo.Create();
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await CompareCommand.RunAsync(["what calls Replace?", "--runs", "2", "--claude", repo.Claude], output, error, "9.9.9", ask: () => "y", repo.Root, TestContext.Current.CancellationToken);

        Assert.True(exit == 0, output + "\n" + error);
        var text = output.ToString();
        // The confirmation question has no newline of its own: the person's Enter supplies it.
        var lines = text.Split('\n').Where(l => l.Contains("round ", StringComparison.Ordinal) && l.Contains(" s, ", StringComparison.Ordinal))
            .Select(l => l[l.IndexOf("round ", StringComparison.Ordinal)..l.IndexOf(':', StringComparison.Ordinal)]).ToList();
        Assert.True(lines.SequenceEqual(["round 1 without", "round 1 with", "round 2 with", "round 2 without"]), text);
        Assert.Contains("Medians of the counted runs (with: 2, without: 2)", text);
        Assert.Contains("| model calls | 2 | 3 |", text);

        var calls = Directory.GetFiles(repo.Calls).Select(File.ReadAllLines).ToList();
        Assert.Equal(4, calls.Count);
        Assert.All(calls, args => Assert.Contains("--no-session-persistence", args));
        Assert.All(calls, args => Assert.EndsWith(Path.DirectorySeparatorChar + "repo", args[^1], StringComparison.Ordinal));
        Assert.Equal(2, calls.Count(args => args.Any(a => a.Contains("codegraph-mcp", StringComparison.Ordinal) && a.Contains("\"CODEGRAPH_TELEMETRY\":\"off\"", StringComparison.Ordinal))));

        var outDir = Directory.GetDirectories(Path.Combine(repo.Root, ".codegraph", "compare")).Single();
        Assert.Equal(4, Directory.GetFiles(outDir, "*.jsonl").Length);
        var report = File.ReadAllText(Path.Combine(outDir, "report.md"));
        Assert.Contains("### Round 2, without", report);
        Assert.Contains("answer without the graph", report);
        using var results = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "results.json")));
        Assert.Equal(4, results.RootElement.GetProperty("runs").GetArrayLength());
    }

    [Fact]
    public async Task A_session_that_changes_the_working_tree_is_not_counted()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stand-in claude is a shell script");
        using var repo = FakeRepo.Create();
        var output = new StringWriter();

        var exit = await CompareCommand.RunAsync(["please edit a.txt", "--runs", "1", "--claude", repo.Claude, "--yes"], output, TextWriter.Null, "9.9.9", ask: null, repo.Root, TestContext.Current.CancellationToken);

        Assert.Equal(2, exit);
        var text = output.ToString();
        Assert.Contains("round 1 without: ", text);
        Assert.Contains("(not counted: the working tree changed during the run)", text);
        Assert.Contains("No medians: counted runs with 0, without 0.", text);
    }

    /// <summary>A git repo with a graph, an .mcp.json and a stand-in <c>claude</c> that writes a canned log.</summary>
    private sealed class FakeRepo : IDisposable
    {
        private readonly string _directory;

        private FakeRepo(string directory) => _directory = directory;

        public string Root => Path.Combine(_directory, "repo");

        public string Claude => Path.Combine(_directory, "bin", "claude");

        public string Calls => Path.Combine(_directory, "calls");

        public static FakeRepo Create()
        {
            var repo = new FakeRepo(Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n")));
            Directory.CreateDirectory(repo.Root);
            Directory.CreateDirectory(repo.Calls);
            Directory.CreateDirectory(Path.GetDirectoryName(repo.Claude)!);
            File.WriteAllText(Path.Combine(repo.Root, "a.txt"), "hello\n");
            File.WriteAllText(Path.Combine(repo.Root, ".mcp.json"), """{"mcpServers":{"codegraph":{"command":"/opt/codegraph-mcp"}}}""");
            Git(repo.Root, "init", "-q");
            using (GraphStore.Open(Path.Combine(repo.Root, Worktree.DbRelativePath), readOnly: false, TimeSpan.FromSeconds(5)))
            {
            }

            // Two model calls with the server, three without; each call's input is in the usage fields.
            File.WriteAllText(repo.Claude, $$$"""
                #!/usr/bin/env bash
                { printf '%s\n' "$@"; pwd -P; } > "{{{repo.Calls}}}/$$-$RANDOM"
                case "$2" in *edit*) echo changed >> a.txt ;; esac
                case "$*" in
                  *codegraph-mcp*) servers='[{"name":"codegraph","status":"connected"}]'; n=2; answer='answer with the graph' ;;
                  *) servers='[]'; n=3; answer='answer without the graph' ;;
                esac
                echo '{"type":"system","subtype":"init","model":"claude-test","claude_code_version":"9.9.9","mcp_servers":'"$servers"',"tools":["Read"]}'
                for i in $(seq 1 $n); do
                  echo '{"type":"assistant","parent_tool_use_id":null,"message":{"id":"m'"$i"'","usage":{"input_tokens":'"$((i * 1000))"'},"content":[{"type":"tool_use","id":"t'"$i"'","name":"Read"}]}}'
                done
                echo '{"type":"result","is_error":false,"result":"'"$answer"'","total_cost_usd":0.01,"usage":{"output_tokens":50}}'
                """.Replace("\r\n", "\n"));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(repo.Claude, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return repo;
        }

        public void Dispose() => TempDb.TryDelete(_directory);

        private static void Git(string directory, params string[] args)
        {
            using var process = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = directory, UseShellExecute = false })!;
            process.WaitForExit();
        }
    }
}
