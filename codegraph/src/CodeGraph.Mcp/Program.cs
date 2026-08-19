using CodeGraph.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var dbPath = args.Length > 0
    ? args[0]
    : Environment.GetEnvironmentVariable("CODEGRAPH_DB");

if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
{
    Console.Error.WriteLine("usage: codegraph-mcp <path-to-graph.db>   (or set CODEGRAPH_DB)");
    Console.Error.WriteLine("       run codegraph-index first to create the database");
    return 1;
}

var builder = Host.CreateApplicationBuilder(args);

// stdout is the MCP transport; every log line must go to stderr or the protocol breaks.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(new GraphStore(dbPath));
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
return 0;
