using System.Globalization;
using CodeGraph.Core;

namespace CodeGraph.Indexer;

/// <summary>
/// <c>codegraph-index usage [--db PATH] [--days N]</c>: what the graph did for its users, from the telemetry file
/// beside it. The same figures as the server's <c>usage_stats</c> tool, for a terminal.
/// </summary>
internal static class UsageCommand
{
    public static int Run(string[] args, TextWriter output, TextWriter error, string? currentDirectory = null)
    {
        string? dbArg = null;
        var days = 7;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db" when i + 1 < args.Length:
                    dbArg = args[++i];
                    break;
                case "--days" when i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0:
                    days = n;
                    i++;
                    break;
                default:
                    error.WriteLine("usage: codegraph-index usage [--db <path>] [--days <n>]");
                    return 1;
            }
        }

        var start = currentDirectory ?? Directory.GetCurrentDirectory();
        var dbPath = dbArg is not null ? Path.GetFullPath(dbArg) : Worktree.FindDb(start) ?? Worktree.DefaultDbPath(start);
        var logPath = TelemetryPath(dbPath);
        var calls = new Telemetry(logPath).AllTime();
        if (calls.Count == 0)
        {
            output.WriteLine($"no tool calls recorded yet ({logPath}); the server writes one line there per call");
            return 0;
        }

        var period = UsageSummary.LastDays(calls, days);
        output.WriteLine(period.Line(string.Create(CultureInfo.InvariantCulture, $"last {days} days")));
        output.WriteLine(UsageSummary.Of(calls, DateTime.MinValue).Line(string.Create(CultureInfo.InvariantCulture, $"since {calls[0].At:yyyy-MM-dd}")));
        if (period.Tools.Count > 0)
        {
            output.WriteLine();
            output.WriteLine(Telemetry.Table(period.Tools));
        }

        return 0;
    }

    /// <summary>The server's telemetry file: beside the graph.</summary>
    public static string TelemetryPath(string dbPath) => Path.Combine(Path.GetDirectoryName(dbPath)!, "telemetry.jsonl");

    /// <summary>The one line the indexer prints after a run, or null when the graph has not been asked anything yet.</summary>
    public static string? AfterIndexLine(string dbPath)
    {
        var calls = new Telemetry(TelemetryPath(dbPath)).AllTime();
        if (calls.Count == 0)
        {
            return null;
        }

        return UsageSummary.LastDays(calls, 7).Line("last 7 days") + " (codegraph-index usage for details)";
    }
}
