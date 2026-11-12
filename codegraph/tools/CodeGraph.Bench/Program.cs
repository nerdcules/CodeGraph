using System.Diagnostics;
using System.Globalization;
using CodeGraph.Core;
using CodeGraph.Core.Live;

// Times what a tool call costs against a graph, as a markdown table for BENCHMARKS.md. Each figure is the median
// of several runs after one warm-up, so a cold DuckDB page cache does not skew it. With --index <solution> and
// --indexer <path-to-codegraph-index> it also times a full and a no-op incremental index and watches the file size.
//
//   codegraph-bench <graph.db> [--index <solution> --indexer <codegraph-index>] [--runs N] [--live]

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: codegraph-bench <graph.db> [--index <solution> --indexer <codegraph-index>] [--runs N] [--live]");
    return 1;
}

var dbPath = Path.GetFullPath(args[0]);
string? solution = null;
string? indexer = null;
var runs = 7;
var live = false;
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--index" when i + 1 < args.Length: solution = Path.GetFullPath(args[++i]); break;
        case "--indexer" when i + 1 < args.Length: indexer = Path.GetFullPath(args[++i]); break;
        case "--runs" when i + 1 < args.Length: runs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--live": live = true; break;
    }
}

var rows = new List<(string Label, string Value)>();

if (solution is not null && indexer is not null)
{
    rows.Add(("full index", Seconds(Index(indexer, solution, dbPath, full: true))));
    rows.Add(("graph file after full index", Kilobytes(dbPath)));
    rows.Add(("no-op incremental index", Seconds(Index(indexer, solution, dbPath, full: false))));
    for (var i = 0; i < 3; i++)
    {
        Index(indexer, solution, dbPath, full: true);
    }

    rows.Add(("graph file after 3 more full rebuilds", Kilobytes(dbPath)));
}

rows.Add(("open read-only (per tool call)", Median(() => { using var s = GraphStore.Open(dbPath, readOnly: true, TimeSpan.FromSeconds(5)); })));
using (var anchor = new GraphAnchor(dbPath, idleRelease: TimeSpan.FromMinutes(1)))
using (anchor.Acquire())
{
    rows.Add(("open read-only with the anchor held", Median(() => { using var s = GraphStore.Open(dbPath, readOnly: true, TimeSpan.FromSeconds(5)); })));
}

var probe = new LiveClient(dbPath);
rows.Add(("daemon probe when none runs", Median(() => probe.PingAsync().GetAwaiter().GetResult())));

using var store = GraphStore.Open(dbPath, readOnly: true, TimeSpan.FromSeconds(5));
var (nodes, edges) = store.Count();
rows.Insert(0, ("graph", $"{nodes} nodes, {edges} edges"));
var hot = store.MostCalled(1, 1, 0).FirstOrDefault().Method?.Id;
var anyTest = store.AllNodes(NodeKind.Method).FirstOrDefault(n => n.IsTest)?.Id;
if (hot is not null)
{
    rows.Add(("get_node", Median(() => store.GetNode(hot))));
    rows.Add(("callers_of depth 1 (most-called method)", Median(() => store.Callers(hot, 1))));
    rows.Add(("impact_of depth 5", Median(() => store.Impact(hot, 5))));
    rows.Add(("impact_of depth 20", Median(() => store.Impact(hot, 20))));
    rows.Add(("covered_by depth 30", Median(() => store.TransitiveTests(hot, 30))));
    rows.Add(("explain_reachability", Median(() => store.ExplainReachability(hot))));
    rows.Add(("neighbors", Median(() => { store.EdgesFrom(hot); store.EdgesTo(hot); })));
    rows.Add(("subgraph depth 2", Median(() => store.SubGraph(hot, GraphStore.AllEdgeKinds, 2))));
}

if (hot is not null && anyTest is not null)
{
    rows.Add(("path_between depth 10 (test → most-called)", Median(() => store.PathBetween(anyTest, hot, [nameof(EdgeKind.Calls)], 10))));
}

rows.Add(("find_symbol 'Dispose'", Median(() => store.FindSymbol("Dispose"))));
rows.Add(("search_code 'open store read only'", Median(() => store.Search("open store read only"))));
rows.Add(("most_called", Median(() => store.MostCalled(20, 1, 0))));
rows.Add(("coverage_stats", Median(() => store.CoverageStats())));
rows.Add(("uncovered", Median(() => store.UncoveredMethods())));
rows.Add(("dead_code_report", Median(() => store.DeadCodeReport())));
var freshness = new IndexFreshness(dbPath, TimeSpan.Zero);
rows.Add(("freshness check, cold", Median(() => new IndexFreshness(dbPath, TimeSpan.Zero).Check(), warmups: 0, count: 3)));
rows.Add(("freshness check, warm", Median(() => freshness.Check())));

if (live)
{
    await LiveRows(rows, dbPath, store, hot, runs);
}

Console.WriteLine($"| measurement | value |");
Console.WriteLine($"|---|---|");
foreach (var (label, value) in rows)
{
    Console.WriteLine($"| {label} | {value} |");
}

return 0;

string Median(Action action, int? warmups = null, int? count = null)
{
    for (var i = 0; i < (warmups ?? 1); i++)
    {
        action();
    }

    var samples = new List<double>();
    for (var i = 0; i < (count ?? runs); i++)
    {
        var sw = Stopwatch.StartNew();
        action();
        samples.Add(sw.Elapsed.TotalMilliseconds);
    }

    samples.Sort();
    return Ms(samples[samples.Count / 2]);
}

static string Ms(double ms) => ms < 1 ? $"{ms:F3} ms" : ms < 10 ? $"{ms:F1} ms" : $"{ms:F0} ms";

static string Seconds(TimeSpan t) => $"{t.TotalSeconds:F1} s";

static string Kilobytes(string path) => $"{new FileInfo(path).Length / 1024:N0} KB";

static TimeSpan Index(string indexer, string solution, string dbPath, bool full)
{
    var sw = Stopwatch.StartNew();
    var psi = new ProcessStartInfo(indexer) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
    psi.ArgumentList.Add(solution);
    psi.ArgumentList.Add("--db");
    psi.ArgumentList.Add(dbPath);
    if (full)
    {
        psi.ArgumentList.Add("--full");
    }

    using var process = Process.Start(psi)!;
    process.StandardError.ReadToEnd();
    process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"{indexer} exited with {process.ExitCode}");
    }

    return sw.Elapsed;
}

// Times the daemon's answers when one is running for this graph: the pipe round trip itself, a position lookup,
// find-references on a source and a metadata symbol, file diagnostics, and a what-if compile of a file.
static async Task LiveRows(List<(string, string)> rows, string dbPath, GraphStore store, string? hot, int runs)
{
    var live = new LiveClient(dbPath);
    var status = await live.PingAsync();
    if (status is null)
    {
        rows.Add(("live daemon", "not running (start codegraph-index --watch to measure)"));
        return;
    }

    if (status.Loading)
    {
        rows.Add(("live daemon", "running, still loading the solution; rerun when live_status says idle"));
        return;
    }

    rows.Add(("live daemon", $"{status.Projects} project(s), {status.Documents} document(s)"));
    rows.Add(("live ping (pipe round trip)", await MedianAsync(() => live.CallAsync<LiveStatus>("ping", null), runs)));
    rows.Add(("live flush when idle", await MedianAsync(() => live.CallAsync<LiveStatus>("flush", null), runs)));

    var node = hot is null ? null : store.GetNode(hot);
    if (node?.FilePath is not null)
    {
        var at = new LivePosition(node.FilePath, node.StartLine, 1);
        rows.Add(("live symbol_at (declaration line)", await MedianAsync(() => live.CallAsync<LiveSymbol>("symbol_at", at), runs)));
        rows.Add(("live references, source symbol (most-called)", await MedianAsync(() => live.CallAsync<LiveReferences>("references", new { symbol = hot, limit = 50 }), runs)));
        rows.Add(("live diagnostics, one file", await MedianAsync(() => live.CallAsync<LiveDiagnostics>("diagnostics", new { file = node.FilePath, includeWarnings = false, limit = 30 }), runs)));
        var text = await File.ReadAllTextAsync(node.FilePath);
        rows.Add(("live check_change, unchanged file", await MedianAsync(() => live.CallAsync<LiveCheck>("check_change", new { file = node.FilePath, text }), 3, warmups: 1)));
        rows.Add(("live check_change, edited file", await MedianAsync(() => live.CallAsync<LiveCheck>("check_change", new { file = node.FilePath, text = text + $"\n// bench {Guid.NewGuid():n}\n" }), 3, warmups: 1)));
    }

    if (store.MostCalled(100, 1, 0).Select(m => m.Method).FirstOrDefault(m => m.Id.Contains('(') && m.FilePath is not null && !m.IsTest) is { } withParameters)
    {
        rows.Add(("live data_flow (most-called method with parameters)", await MedianAsync(() => live.CallAsync<LiveDataFlow>("data_flow", new { symbol = withParameters.Id }), runs)));
    }

    rows.Add(("live references, metadata symbol (System.IO.File)", await MedianAsync(() => live.CallAsync<LiveReferences>("references", new { symbol = "System.IO.File", limit = 50 }), runs)));
    rows.Add(("live diagnostics, whole solution", await MedianAsync(() => live.CallAsync<LiveDiagnostics>("diagnostics", new { includeWarnings = false, limit = 30 }), 3, warmups: 1)));
}

static async Task<string> MedianAsync(Func<Task> action, int count, int warmups = 1)
{
    try
    {
        for (var i = 0; i < warmups; i++)
        {
            await action();
        }
    }
    catch (LiveException e)
    {
        // A daemon for another language answers some operations differently (data_flow is C#-only).
        return $"n/a ({e.Message})";
    }

    var samples = new List<double>();
    for (var i = 0; i < count; i++)
    {
        var sw = Stopwatch.StartNew();
        await action();
        samples.Add(sw.Elapsed.TotalMilliseconds);
    }

    samples.Sort();
    return Ms(samples[samples.Count / 2]);
}
