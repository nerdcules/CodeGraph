using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeGraph.Core;
using CodeGraph.Core.Live;

namespace CodeGraph.Indexer;

/// <summary>
/// <c>codegraph-index status [--db PATH] [--json]</c>: the graph, how it compares with the working tree, the daemon
/// serving it and what the graph did for its users, in one call. <c>--json</c> is for tools that drive the indexer
/// (the VS Code extension); without it the same facts are printed for a terminal.
/// </summary>
internal static class StatusCommand
{
    /// <summary>How many edited or deleted paths the report lists; the counts are always complete.</summary>
    public const int ListedPaths = 20;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, string version, string? currentDirectory = null)
    {
        string? dbArg = null;
        var json = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db" when i + 1 < args.Length:
                    dbArg = args[++i];
                    break;
                case "--json":
                    json = true;
                    break;
                default:
                    error.WriteLine("usage: codegraph-index status [--db <path>] [--json]");
                    return 1;
            }
        }

        var start = currentDirectory ?? Directory.GetCurrentDirectory();
        var dbPath = dbArg is not null ? Path.GetFullPath(dbArg) : Worktree.FindDb(start) ?? Worktree.DefaultDbPath(start);
        var report = await ReadAsync(dbPath, version);
        output.WriteLine(json ? JsonSerializer.Serialize(report, Json) : Text(report));
        return 0;
    }

    /// <summary>Everything the status reports. Never throws for a missing, locked or damaged graph: it says so in <see cref="StatusReport.Error"/>.</summary>
    public static async Task<StatusReport> ReadAsync(string dbPath, string version)
    {
        var daemon = await new LiveClient(dbPath).PingAsync();
        var usage = Usage(dbPath);
        if (!File.Exists(dbPath))
        {
            return new StatusReport(version, dbPath, Exists: false, Graph: null, Daemon: daemon, Usage: usage, Error: null);
        }

        try
        {
            IReadOnlyDictionary<string, string> meta;
            (int Nodes, int Edges) counts;
            IReadOnlyList<(string Language, int Nodes, int Files)> byLanguage;
            using (var store = GraphStore.Open(dbPath, readOnly: true, TimeSpan.FromSeconds(5)))
            {
                meta = store.ReadMeta();
                counts = store.Count();
                byLanguage = store.CountByLanguage();
            }

            var freshness = new IndexFreshness(dbPath, TimeSpan.Zero).Check();
            string? Meta(string key) => meta.GetValueOrDefault(key) is { Length: > 0 } value ? value : null;
            var graph = new GraphStatus(
                Solution: Meta(MetaKeys.Solution),
                WorktreeRoot: Meta(MetaKeys.WorktreeRoot),
                IndexedAt: Meta(MetaKeys.IndexedAt),
                Mode: Meta(MetaKeys.Mode),
                SchemaVersion: Meta(MetaKeys.SchemaVersion),
                SchemaCurrent: Meta(MetaKeys.SchemaVersion) == GraphStore.SchemaVersion,
                Commit: freshness.IndexedCommit,
                Head: freshness.Head,
                HeadMoved: freshness.HeadMoved,
                Stale: freshness.IsStale,
                ChangedCount: freshness.ChangedFiles.Count,
                ChangedFiles: [.. freshness.ChangedFiles.Order(StringComparer.OrdinalIgnoreCase).Take(ListedPaths)],
                MissingCount: freshness.MissingFiles.Count,
                MissingFiles: [.. freshness.MissingFiles.Order(StringComparer.OrdinalIgnoreCase).Take(ListedPaths)],
                Nodes: counts.Nodes,
                Edges: counts.Edges,
                Languages: Languages(meta, byLanguage));
            return new StatusReport(version, dbPath, Exists: true, graph, daemon, usage, Error: null);
        }
        catch (Exception e) when (e is DuckDB.NET.Data.DuckDBException or IOException)
        {
            var problem = GraphStore.IsCorrupt(e)
                ? $"the graph file is damaged ({e.Message.Split('\n', 2)[0].Trim()}); rebuild it with: codegraph-index <target> --full"
                : $"the graph could not be read ({e.Message.Split('\n', 2)[0].Trim()})";
            return new StatusReport(version, dbPath, Exists: true, Graph: null, daemon, usage, problem);
        }
    }

    private static List<LanguageReport> Languages(IReadOnlyDictionary<string, string> meta, IReadOnlyList<(string Language, int Nodes, int Files)> counts)
    {
        var names = meta.Keys.Select(MetaKeys.LanguageOf).OfType<string>()
            .Concat(counts.Select(c => c.Language))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        var languages = new List<LanguageReport>();
        foreach (var name in names)
        {
            string? Get(string key) => meta.GetValueOrDefault(MetaKeys.Language(name, key)) is { Length: > 0 } value ? value : null;
            int? Number(string key) => int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
            var count = counts.FirstOrDefault(c => c.Language == name);
            languages.Add(new LanguageReport(
                name,
                count.Nodes,
                count.Files,
                Get(LanguageMetaKeys.Target),
                Get(LanguageMetaKeys.Tool),
                Get(LanguageMetaKeys.Compiler),
                Get(LanguageMetaKeys.NewestLanguageVersion),
                Get(LanguageMetaKeys.LanguageVersions),
                Number(LanguageMetaKeys.CompileErrors),
                Get(LanguageMetaKeys.FirstCompileError),
                Number(LanguageMetaKeys.LanguageVersionErrors),
                Get(LanguageMetaKeys.FirstLanguageVersionError),
                Get(LanguageMetaKeys.Unmapped),
                Get(LanguageMetaKeys.Notes)?.Split("; ", StringSplitOptions.RemoveEmptyEntries),
                Get(LanguageMetaKeys.IndexedAt)));
        }

        return languages;
    }

    private static UsageReport? Usage(string dbPath)
    {
        var calls = new Telemetry(UsageCommand.TelemetryPath(dbPath)).AllTime();
        if (calls.Count == 0)
        {
            return null;
        }

        var week = UsageSummary.LastDays(calls, 7);
        return new UsageReport(
            week.Calls,
            week.Errors,
            week.SourceTokens,
            week.ResultTokens,
            week.TokensSaved,
            [.. week.Tools.Take(5).Select(t => new ToolCount(t.Tool, t.Calls))],
            calls.Count,
            calls[0].At,
            week.Line("last 7 days"));
    }

    /// <summary>The terminal form: one fact per line, the same order as <c>index_status</c>.</summary>
    public static string Text(StatusReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"graph     : {report.Db}{(report.Exists ? "" : "  (not built yet; run codegraph-index init)")}");
        if (report.Error is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"error     : {report.Error}");
        }

        if (report.Graph is { } g)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"solution  : {g.Solution ?? "?"}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"built     : {g.IndexedAt ?? "?"} ({g.Mode ?? "?"})");
            sb.AppendLine(CultureInfo.InvariantCulture, $"commit    : {g.Commit ?? "?"}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"HEAD now  : {g.Head ?? "?"}{(g.HeadMoved ? "  (moved since indexing)" : "")}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"size      : {g.Nodes} nodes, {g.Edges} edges");
            sb.AppendLine(CultureInfo.InvariantCulture, $"status    : {(g.Stale ? $"STALE ({g.ChangedCount} edited, {g.MissingCount} deleted)" : "up to date with the working tree")}");
            foreach (var language in g.Languages)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{language.Name,-10}: {language.Nodes} nodes, {language.Files} files{(language.Compiler is { } c ? $", {c}" : "")}{(language.CompileErrors is > 0 and var e ? $", {e} compile error(s)" : "")}");
            }
        }

        sb.AppendLine(report.Daemon is { } d
            ? string.Create(CultureInfo.InvariantCulture, $"daemon    : pid {d.Pid}, {(d.Loading ? "loading" : d.Indexing ? "indexing" : "watching")}, {d.Projects} project(s), {d.Documents} document(s), since {d.StartedAt:u}")
            : "daemon    : not running");
        sb.Append(report.Usage is { } u ? $"usage     : {u.Line}" : "usage     : no tool calls recorded yet");
        return sb.ToString();
    }

    /// <summary>
    /// <c>codegraph-index stop [--db PATH]</c>: asks the daemon serving the graph to stop, and waits until its pipe is
    /// gone. A daemon from 1.2.0 or earlier does not know <c>stop</c>; the command says so and leaves it running.
    /// </summary>
    public static async Task<int> StopAsync(string[] args, TextWriter output, TextWriter error, string? currentDirectory = null)
    {
        string? dbArg = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db" when i + 1 < args.Length:
                    dbArg = args[++i];
                    break;
                default:
                    error.WriteLine("usage: codegraph-index stop [--db <path>]");
                    return 1;
            }
        }

        var start = currentDirectory ?? Directory.GetCurrentDirectory();
        var dbPath = dbArg is not null ? Path.GetFullPath(dbArg) : Worktree.FindDb(start) ?? Worktree.DefaultDbPath(start);
        var client = new LiveClient(dbPath);
        if (await client.PingAsync() is not { } running)
        {
            output.WriteLine($"no daemon serves {dbPath}");
            return 0;
        }

        try
        {
            await client.CallAsync<LiveStatus>("stop", null);
        }
        catch (LiveException)
        {
            // A daemon from 1.2.0 or earlier answers "unknown method". Ending a process by the pid a pipe reports is
            // not safe (the pid may be stale, or not the daemon's), so say what to do instead.
            error.WriteLine($"the daemon (pid {running.Pid}) is from CodeGraph 1.2.0 or earlier and does not know `stop`; end it with Ctrl+C in its terminal, or end process {running.Pid}");
            return 1;
        }
        catch (IOException)
        {
            // It closed the pipe while answering, which is what stopping looks like from here.
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (await new LiveClient(dbPath).PingAsync() is null)
            {
                output.WriteLine($"stopped the daemon (pid {running.Pid}) serving {dbPath}");
                return 0;
            }

            await Task.Delay(100);
        }

        error.WriteLine($"the daemon (pid {running.Pid}) still answers after 15 s");
        return 1;
    }
}

public sealed record StatusReport(string Version, string Db, bool Exists, GraphStatus? Graph, LiveStatus? Daemon, UsageReport? Usage, string? Error);

public sealed record GraphStatus(
    string? Solution,
    string? WorktreeRoot,
    string? IndexedAt,
    string? Mode,
    string? SchemaVersion,
    bool SchemaCurrent,
    string? Commit,
    string? Head,
    bool HeadMoved,
    bool Stale,
    int ChangedCount,
    IReadOnlyList<string> ChangedFiles,
    int MissingCount,
    IReadOnlyList<string> MissingFiles,
    int Nodes,
    int Edges,
    IReadOnlyList<LanguageReport> Languages);

public sealed record LanguageReport(
    string Name,
    int Nodes,
    int Files,
    string? Target,
    string? Tool,
    string? Compiler,
    string? NewestLanguageVersion,
    string? LanguageVersions,
    int? CompileErrors,
    string? FirstCompileError,
    int? LanguageVersionErrors,
    string? FirstLanguageVersionError,
    string? Unmapped,
    IReadOnlyList<string>? Notes,
    string? IndexedAt);

public sealed record ToolCount(string Tool, int Calls);

public sealed record UsageReport(
    int Calls,
    int Errors,
    long SourceTokens,
    long ResultTokens,
    long TokensSaved,
    IReadOnlyList<ToolCount> TopTools,
    int AllTimeCalls,
    DateTime FirstCallAt,
    string Line);
