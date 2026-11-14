using CodeGraph.Core;
using CodeGraph.Web;

var dbPath = args.Length > 0
    ? args[0]
    : Environment.GetEnvironmentVariable("CODEGRAPH_DB");

if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
{
    Console.Error.WriteLine("usage: codegraph-web <path-to-graph.db>   (or set CODEGRAPH_DB)");
    Console.Error.WriteLine("       run codegraph-index first to create the database");
    return 1;
}

var builder = WebApplication.CreateBuilder(args);
// Per request, not singleton: DuckDB allows one writer or many readers, so a connection held open for the
// server's lifetime would lock codegraph-index out; and a DuckDB connection is not safe to share across requests.
// The anchor keeps the instance loaded between requests so each per-request open costs about a millisecond.
// Graph reads answer from an in-memory snapshot once it has loaded (see GraphSnapshotCache); a request opens the
// database only for what the snapshot does not hold, such as full-text search.
var anchor = new GraphAnchor(dbPath);
var snapshots = new GraphSnapshotCache(dbPath, anchor, poll: GraphSnapshotCache.Enabled ? TimeSpan.FromSeconds(1) : null);
builder.Services.AddSingleton(anchor);
builder.Services.AddSingleton(snapshots);
builder.Services.AddScoped(_ => GraphStore.OpenReader(
    dbPath, GraphSnapshotCache.Enabled ? snapshots.Current : null, TimeSpan.FromSeconds(15), anchor.Acquire));

var app = builder.Build();
app.UseStaticFiles();
app.MapGraphApi();
app.MapFallbackToFile("index.html");
app.Run();
return 0;
