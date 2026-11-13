using CodeGraph.Core;

namespace CodeGraph.Tests;

public sealed class TelemetryTests
{
    [Fact]
    public void Records_a_call_in_the_session_and_in_the_log()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"));
        try
        {
            var logPath = Path.Combine(directory, "telemetry.jsonl");
            var telemetry = new Telemetry(logPath);

            var scope = telemetry.Begin("find_symbol");
            Assert.Same(scope, Telemetry.Current);
            scope.Complete(resultChars: 400, isError: false);

            var call = Assert.Single(telemetry.Session);
            Assert.Equal("find_symbol", call.Tool);
            Assert.Equal(100, call.ResultTokens);

            var reloaded = new Telemetry(logPath).AllTime();
            Assert.Equal(call, Assert.Single(reloaded));
        }
        finally
        {
            TempDb.TryDelete(directory);
        }
    }

    [Fact]
    public void Counts_each_source_file_once_and_estimates_tokens_saved()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "Big.cs");
            File.WriteAllText(file, new string('x', 4000));
            var telemetry = new Telemetry(logPath: null);

            var scope = telemetry.Begin("impact_of");
            scope.AddSourceFile(file);
            scope.AddSourceFile(file);
            scope.AddSourceFile(Path.Combine(directory, "Missing.cs"));
            scope.Complete(resultChars: 400, isError: false);

            var call = Assert.Single(telemetry.Session);
            Assert.Equal(4000, call.SourceChars);
            Assert.Equal(1000 - 100, call.TokensSaved);
            Assert.Empty(telemetry.AllTime());
        }
        finally
        {
            TempDb.TryDelete(directory);
        }
    }

    [Fact]
    public void Groups_usage_per_tool()
    {
        var now = DateTime.UtcNow;
        var usage = ToolUsage.Group(
        [
            new ToolCallRecord(now, "find_symbol", 10, 400, 0, false),
            new ToolCallRecord(now, "find_symbol", 30, 400, 0, true),
            new ToolCallRecord(now, "snippet", 5, 800, 8000, false),
        ]);

        Assert.Equal(["find_symbol", "snippet"], usage.Select(u => u.Tool));
        Assert.Equal(2, usage[0].Calls);
        Assert.Equal(1, usage[0].Errors);
        Assert.Equal(20, usage[0].AverageMilliseconds);
        Assert.Equal(1800, usage[1].TokensSaved);
        Assert.Contains("snippet", Telemetry.Table(usage));
    }
}

public sealed class UsageSummaryTests
{
    private static ToolCallRecord Call(DateTime at, string tool, long result, long source, bool error = false) =>
        new(at, tool, 5, result, source, error);

    [Fact]
    public void Sums_the_calls_in_the_period_only()
    {
        var now = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
        var calls = new[]
        {
            Call(now.AddDays(-10), "impact_of", 4_000, 400_000),
            Call(now.AddDays(-2), "find_symbol", 400, 40_000),
            Call(now.AddDays(-1), "find_symbol", 400, 40_000, error: true),
            Call(now.AddHours(-1), "snippet", 2_000, 2_000),
        };

        var week = UsageSummary.LastDays(calls, 7, now);

        Assert.Equal(3, week.Calls);
        Assert.Equal(1, week.Errors);
        Assert.Equal(700, week.ResultTokens);
        Assert.Equal(20_500, week.SourceTokens);
        Assert.Equal(19_800, week.TokensSaved);
        Assert.Equal("find_symbol", week.Tools[0].Tool);
        Assert.Equal(
            "last 7 days: 3 tool calls answered (1 failed); the files they pointed at come to about 20,500 tokens, the answers to 700, so about 19,800 tokens stayed out of the context; most used: find_symbol ×2, snippet ×1",
            week.Line("last 7 days"));
    }

    [Fact]
    public void Says_so_when_there_were_no_calls()
    {
        var summary = UsageSummary.LastDays([], 7);
        Assert.Equal("last 7 days: no tool calls", summary.Line("last 7 days"));
    }
}
