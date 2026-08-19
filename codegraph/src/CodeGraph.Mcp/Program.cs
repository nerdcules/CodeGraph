using CodeGraph.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Parse positional db path and optional --web PORT flag.
var dbPath = (string?)null;
int? webPort = null;
var hostArgs = new List<string>();

for (var i = 0; i < args.Length; i++)
{
    if ((args[i] is "--web" or "--port") && i + 1 < args.Length && int.TryParse(args[i + 1], out var p))
    {
        webPort = p;
        i++;                // skip the number
    }
    else if (!args[i].StartsWith("--") && dbPath is null)
    {
        dbPath ??= args[i]; // first non-flag arg is the db path
    }
    else
    {
        hostArgs.Add(args[i]);
    }
}

dbPath ??= Environment.GetEnvironmentVariable("CODEGRAPH_DB");

if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
{
    Console.Error.WriteLine("usage: codegraph-mcp <path-to-graph.db> [--web PORT]");
    Console.Error.WriteLine("       --web PORT  also serve the graph visualiser on http://localhost:PORT");
    Console.Error.WriteLine("       set CODEGRAPH_DB as an alternative to the positional argument");
    return 1;
}

if (webPort.HasValue)
{
    // ── Web + MCP mode ─────────────────────────────────────────
    // Single process: stdio carries the MCP protocol, HTTP serves the browser UI.
    var webBuilder = WebApplication.CreateBuilder(hostArgs.ToArray());
    webBuilder.Configuration["urls"] = $"http://localhost:{webPort}";
    webBuilder.Logging.AddConsole(opts => opts.LogToStandardErrorThreshold = LogLevel.Trace);
    webBuilder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
    webBuilder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
    webBuilder.Services.AddSingleton(new GraphStore(dbPath));
    // Web mode: no stdio MCP transport — use codegraph-mcp without --web for VS Code.

    var webApp = webBuilder.Build();
    MapWebApi(webApp);

    Console.Error.WriteLine($"Graph visualiser → http://localhost:{webPort}");
    await webApp.RunAsync();
}
else
{
    // ── Pure MCP mode ──────────────────────────────────────────
    var builder = Host.CreateApplicationBuilder(hostArgs.ToArray());
    builder.Logging.AddConsole(opts => opts.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.AddSingleton(new GraphStore(dbPath));
    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    await builder.Build().RunAsync();
}

return 0;

// ── Web API routes ────────────────────────────────────────────
static void MapWebApi(WebApplication app)
{
    var html = ReadEmbeddedHtml();

    app.MapGet("/", () => Results.Content(html, "text/html"));

    app.MapGet("/api/stats", (GraphStore store) =>
    {
        var (nodes, edges) = store.Count();
        return new { nodes, edges };
    });

    app.MapGet("/api/search", (string q, string? kind, int limit, GraphStore store) =>
    {
        NodeKind? kindFilter = kind is not null && Enum.TryParse<NodeKind>(kind, ignoreCase: true, out var k) ? k : null;
        return store.FindSymbol(q, kindFilter, Math.Clamp(limit, 1, 50))
            .Select(n => new
            {
                id = n.Id, label = n.Name, fullName = n.FullName,
                kind = n.Kind.ToString(), typeKind = n.TypeKind.ToString(),
                accessibility = n.Accessibility.ToString(), signature = n.Signature,
                file = n.FilePath, line = n.StartLine,
            });
    });

    app.MapGet("/api/node/{id}", (string id, GraphStore store) =>
        store.GetNode(id) is { } n
            ? Results.Ok(new
            {
                id = n.Id, fullName = n.FullName, kind = n.Kind.ToString(),
                typeKind = n.TypeKind.ToString(), accessibility = n.Accessibility.ToString(),
                signature = n.Signature, file = n.FilePath, startLine = n.StartLine, endLine = n.EndLine,
                isStatic = n.IsStatic, isAbstract = n.IsAbstract, isVirtual = n.IsVirtual,
                isSealed = n.IsSealed, isAsync = n.IsAsync, isOverride = n.IsOverride,
                memberKind = n.MemberKind.ToString(),
            })
            : Results.NotFound());

    app.MapGet("/api/subgraph", (string id, string? edges, int depth, GraphStore store) =>
    {
        var edgeKinds = (edges ?? "Calls,Inherits,Implements,Overrides")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => Enum.TryParse<EdgeKind>(e, ignoreCase: true, out _))
            .ToArray();
        if (edgeKinds.Length == 0) edgeKinds = [nameof(EdgeKind.Calls)];

        var result = store.SubGraph(id, edgeKinds, Math.Clamp(depth, 1, 5));
        return new
        {
            nodes = result.Nodes.Select(n => new
            {
                data = new
                {
                    id = n.Id, label = n.Name, kind = n.Kind.ToString(),
                    typeKind = n.TypeKind.ToString(), accessibility = n.Accessibility.ToString(),
                    fullName = n.FullName, isAsync = n.IsAsync, isStatic = n.IsStatic,
                    file = n.FilePath, line = n.StartLine,
                }
            }),
            edges = result.Edges.Select((e, i) => new
            {
                data = new { id = $"e{i}", source = e.FromId, target = e.ToId, kind = e.Kind }
            }),
        };
    });
}

static string ReadEmbeddedHtml()
{
    var asm = System.Reflection.Assembly.GetExecutingAssembly();
    using var stream = asm.GetManifestResourceStream("CodeGraph.Mcp.wwwroot.index.html")
        ?? asm.GetManifestResourceStream("CodeGraph.Mcp.index.html")
        ?? throw new InvalidOperationException("Embedded web UI not found. Rebuild the project.");
    return new StreamReader(stream).ReadToEnd();
}
