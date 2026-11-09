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
builder.Services.AddScoped(_ => GraphStore.Open(dbPath, readOnly: true, TimeSpan.FromSeconds(15)));

var app = builder.Build();
app.UseStaticFiles();
app.MapGraphApi();
app.MapFallbackToFile("index.html");
app.Run();
return 0;
