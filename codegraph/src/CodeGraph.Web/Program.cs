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
var anchor = new GraphAnchor(dbPath);
builder.Services.AddSingleton(anchor);
builder.Services.AddScoped(_ => anchor.Acquire());
builder.Services.AddScoped(sp =>
{
    sp.GetRequiredService<GraphAnchor.Lease>();
    return GraphStore.Open(dbPath, readOnly: true, TimeSpan.FromSeconds(15));
});

var app = builder.Build();
app.UseStaticFiles();
app.MapGraphApi();
app.MapFallbackToFile("index.html");
app.Run();
return 0;
