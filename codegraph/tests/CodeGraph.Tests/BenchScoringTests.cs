using CodeGraph.ModeBench;

namespace CodeGraph.Tests;

/// <summary>The benchmark's scorer, cycle check, command classification and verdict rule.</summary>
public sealed class BenchScoringTests
{
    private static Truth Names(params string[] items) => new(items, null, "test");

    [Fact]
    public void Items_are_read_after_the_last_answer_line()
    {
        var items = Scorer.AnswerItems("Thinking about ANSWER\nANSWER\n- `Ns.A.B()`\n2. Ns.C.D\n\n```\n", out var block);

        Assert.True(block);
        Assert.Equal(["Ns.A.B()", "Ns.C.D"], items);
        Assert.Empty(Scorer.AnswerItems("no block here", out var none));
        Assert.False(none);
    }

    [Fact]
    public void Names_match_without_parameters_and_by_qualified_tail()
    {
        var truth = Names("Ns.Type.Run(System.String)", "Ns.Other.Stop()", "Ns.Gen<T>.Make(int)");
        var score = Scorer.Rate("ANSWER\nType.Run\nNs.Other.Stop(string)\nGen.Make\nNs.Wrong.Thing\nRun", truth, "name", "/repo");

        // Type.Run, Ns.Other.Stop and Gen.Make match; Ns.Wrong.Thing does not; a bare "Run" is not a qualified tail.
        Assert.Equal(3, score.Matched);
        Assert.Equal(5, score.AnswerItems);
        Assert.Equal(1.0, score.Recall);
        Assert.Equal(0.6, score.Precision, 3);
        Assert.Equal(0.75, score.F1, 3);
    }

    [Fact]
    public void Files_signatures_and_projects_match_their_own_way()
    {
        Assert.Equal(1.0, Scorer.Rate("ANSWER\n/repo/src/A.cs:12-14", Names("src/A.cs"), "file", "/repo").F1);
        Assert.Equal(1.0, Scorer.Rate("ANSWER\npublic  int M(string s);", Names("public int M(string s)"), "signature", "/repo").F1);

        var projects = new Truth(["Api", "Core"], new Dictionary<string, int> { ["Api"] = 3, ["Core"] = 2 }, "test");
        var score = Scorer.Rate("ANSWER\nApi: 3\nCore: 5\nExtra: 1", projects, "project", "/repo");
        Assert.Equal(1.0, score.Recall);
        Assert.Equal(2.0 / 3, score.Precision, 3);
        Assert.Equal(0.5, score.CountAccuracy);
    }

    [Fact]
    public void An_empty_truth_wants_an_empty_answer()
    {
        Assert.Equal(1.0, Scorer.Rate("There are none.\nANSWER\nNONE", Names(), "project", "/repo").F1);
        Assert.Equal(0.0, Scorer.Rate("ANSWER\nApi", Names(), "project", "/repo").F1);
        Assert.Equal(0.0, Scorer.Rate("There are none.", Names(), "project", "/repo").F1);
    }

    [Fact]
    public void Cycles_are_strongly_connected_components_larger_than_one()
    {
        var edges = new Dictionary<string, List<string>> { ["a"] = ["b"], ["b"] = ["c"], ["c"] = ["a", "d"], ["d"] = [] };

        var cycles = Tasks.StronglyConnected(edges).Where(c => c.Count > 1).ToList();

        Assert.Single(cycles);
        Assert.Equal(["a", "b", "c"], cycles[0].Order());
        Assert.DoesNotContain(Tasks.StronglyConnected(new Dictionary<string, List<string>> { ["a"] = ["b"], ["b"] = [] }), c => c.Count > 1);
    }

    [Theory]
    [InlineData("dotnet run /x/scratch/a.cs -- Foo", true, 0)]
    [InlineData("cd /x && dotnet run scratch/a.cs", true, 0)]
    [InlineData("codegraph callers X --limit 0 | jq '.hits.total'", false, 1)]
    [InlineData("codegraph find A | jq . && /opt/cli/codegraph status", false, 2)]
    [InlineData("jq -n 1", false, 0)]
    public void Bash_commands_are_classified(string command, bool script, int cli)
    {
        Assert.Equal(script, Bench.IsScript(command));
        Assert.Equal(cli, Bench.CliCount(command));
    }

    [Fact]
    public void A_win_needs_ranges_that_do_not_overlap()
    {
        Assert.StartsWith("fewer tokens, ranges do not overlap", Report.Verdict([100, 120], [50, 90]));
        Assert.StartsWith("more tokens, ranges do not overlap", Report.Verdict([100, 120], [130, 200]));
        Assert.StartsWith("ranges overlap, too noisy to say", Report.Verdict([100, 120], [90, 110]));
        Assert.StartsWith("no counted runs", Report.Verdict([], [1]));
    }

    [Fact]
    public void One_mcp_call_is_a_single_unchained_step()
    {
        var shape = QuestionShape.Of(
        [
            new ToolCall("ToolSearch", "select:mcp__codegraph__find_symbol", 0.1, false, "loaded", """{"query":"select:mcp__codegraph__find_symbol"}"""),
            new ToolCall("mcp__codegraph__find_symbol", null, 0.2, false, "type  Shop.Orders.OrderService  src/Orders.cs:12  [a1b2c3d4]", """{"query":"OrderService"}"""),
        ], "Where is OrderService defined?");
        Assert.Equal(new QuestionShape(1, 60, 60, false), shape);
    }

    [Fact]
    public void A_later_call_that_passes_back_a_handle_or_name_from_a_result_is_chained()
    {
        var find = new ToolCall("mcp__codegraph__find_symbol", null, 0.2, false, "method  Shop.Orders.OrderService.Place  [a1b2c3d4]", """{"query":"Place"}""");
        var handle = new ToolCall("mcp__codegraph__callers_of", null, 0.2, false, "3 callers", """{"symbol":"a1b2c3d4"}""");
        Assert.True(QuestionShape.Of([find, handle], "Who calls Place?").Chained);
        var name = handle with { Arguments = """{"symbol":"Shop.Orders.OrderService.Place"}""" };
        Assert.True(QuestionShape.Of([find, name], "Who calls Place?").Chained);
        // A name the prompt already gave is not something the first result supplied.
        Assert.False(QuestionShape.Of([find, name], "Who calls Shop.Orders.OrderService.Place?").Chained);
    }

    [Fact]
    public void Cli_pipeline_segments_and_script_runs_are_graph_steps_and_reads_are_not()
    {
        var shape = QuestionShape.Of(
        [
            new ToolCall("Bash", "codegraph --help", 0.1, false, new string('h', 100)),
            new ToolCall("Bash", "codegraph find X | jq -c .items; codegraph outline Y", 0.1, false, "{}"),
            new ToolCall("Read", "/repo/a.cs", 0.1, false, new string('r', 5000)),
        ], "q");
        Assert.Equal(3, shape.GraphCalls);
        Assert.Equal(100, shape.LargestResultChars);
        Assert.Equal(102, shape.TotalResultChars);

        var script = QuestionShape.Of(
        [
            new ToolCall("Bash", "dotnet run scratch/a.cs", 1, false, "Found Shop.Orders.OrderService.Place"),
            new ToolCall("Write", "scratch/b.cs", 0.1, false, "ok", """{"file_path":"scratch/b.cs","content":"await g.CallersAsync(\"Shop.Orders.OrderService.Place\")"}"""),
            new ToolCall("Bash", "dotnet run scratch/b.cs", 1, false, "2 callers"),
        ], "q");
        Assert.Equal(2, script.GraphCalls);
        Assert.True(script.Chained);
    }
}
