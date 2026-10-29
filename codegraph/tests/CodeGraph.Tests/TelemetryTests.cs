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
