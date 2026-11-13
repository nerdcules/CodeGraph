using CodeGraph.Core;
using CodeGraph.Indexer;

namespace CodeGraph.Tests;

public sealed class UsageCommandTests
{
    [Fact]
    public void Reports_the_telemetry_beside_the_graph()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"));
        try
        {
            var dbPath = Path.Combine(directory, ".codegraph", "graph.db");
            var telemetry = new Telemetry(UsageCommand.TelemetryPath(dbPath));
            telemetry.Begin("find_symbol").Complete(resultChars: 400, isError: false);
            telemetry.Begin("impact_of").Complete(resultChars: 4_000, isError: false);

            var output = new StringWriter();
            var exit = UsageCommand.Run(["--db", dbPath], output, TextWriter.Null);

            Assert.Equal(0, exit);
            var text = output.ToString();
            Assert.Contains("last 7 days: 2 tool calls answered", text);
            Assert.Contains("since " + DateTime.UtcNow.ToString("yyyy-MM-dd"), text);
            Assert.Contains("impact_of", text);
            Assert.Equal("last 7 days: 2 tool calls answered; the files they pointed at come to about 0 tokens, the answers to 1,100, so about 0 tokens stayed out of the context; most used: find_symbol ×1, impact_of ×1 (codegraph-index usage for details)", UsageCommand.AfterIndexLine(dbPath));
        }
        finally
        {
            TempDb.TryDelete(directory);
        }
    }

    [Fact]
    public void Says_when_nothing_was_recorded()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"));
        var dbPath = Path.Combine(directory, ".codegraph", "graph.db");

        var output = new StringWriter();
        Assert.Equal(0, UsageCommand.Run(["--db", dbPath], output, TextWriter.Null));
        Assert.StartsWith("no tool calls recorded yet", output.ToString());
        Assert.Null(UsageCommand.AfterIndexLine(dbPath));
        Assert.Equal(1, UsageCommand.Run(["--bogus"], TextWriter.Null, TextWriter.Null));
    }
}
