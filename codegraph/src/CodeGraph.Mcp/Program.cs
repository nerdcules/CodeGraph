using CodeGraph.Core;
using CodeGraph.Core.Live;
using CodeGraph.Mcp;
using CodeGraph.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

// --version answers without touching a graph: install scripts use it to check the executable starts at all.
if (args is ["--version"])
{
    Console.WriteLine(ServerVersion());
    return 0;
}

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

// No explicit path: use this worktree's graph, found from the current directory (Claude Code starts MCP
// servers in the project directory), so one .mcp.json serves every worktree of a repo.
var discovered = false;
if (string.IsNullOrWhiteSpace(dbPath))
{
    var cwd = Directory.GetCurrentDirectory();
    dbPath = Worktree.FindDb(cwd) ?? Worktree.DefaultDbPath(cwd);
    discovered = true;
}

dbPath = Path.GetFullPath(dbPath);
if (!discovered && !File.Exists(dbPath))
{
    Console.Error.WriteLine("usage: codegraph-mcp [path-to-graph.db] [--web PORT]");
    Console.Error.WriteLine("       no path: finds .codegraph/graph.db from the current directory up to the git worktree root");
    Console.Error.WriteLine("       --web PORT  also serve the graph visualiser on http://localhost:PORT");
    Console.Error.WriteLine("       set CODEGRAPH_DB as an alternative to the positional argument");
    return 1;
}

Console.Error.WriteLine(File.Exists(dbPath)
    ? $"codegraph: serving {dbPath}"
    : $"codegraph: no graph yet at {dbPath}; tools will say so until codegraph-index builds it");

var freshness = new IndexFreshness(dbPath, TimeSpan.FromSeconds(3));

// Telemetry stays beside the graph, one JSON line per tool call; CODEGRAPH_TELEMETRY=off keeps it in memory only.
var telemetrySetting = Environment.GetEnvironmentVariable("CODEGRAPH_TELEMETRY");
var telemetryOff = telemetrySetting is not null && telemetrySetting.Trim().ToLowerInvariant() is "0" or "off" or "false" or "no";
var telemetry = new Telemetry(telemetryOff ? null : Path.Combine(Path.GetDirectoryName(dbPath)!, "telemetry.jsonl"));

// The live daemon (codegraph-index --watch) answers on a pipe named after the graph; when it is up, a stale
// graph is flushed before answering, so STALE rows only appear for saves younger than the daemon's debounce.
var live = new LiveClient(dbPath);
var flushWait = TimeSpan.FromSeconds(8);

// Keeps the DuckDB instance loaded between calls (a 1 ms open instead of 11 ms), letting go when idle or
// when the daemon is about to write.
var anchor = new GraphAnchor(dbPath);

// CODEGRAPH_DAEMON=auto: the first tool call starts a daemon when none runs; once per server process.
var autoStart = LiveLauncher.AutoStart ? 1 : 0;

void AddGraph(IServiceCollection services)
{
    // Read-only and per request: DuckDB lets one process write or many read, so a store held open for the
    // server's lifetime would lock out codegraph-index. Each tool call opens, queries and closes.
    services.AddSingleton(freshness);
    services.AddSingleton(telemetry);
    services.AddSingleton(live);
    services.AddSingleton(new SemanticSearch(EmbeddingSettings.FromEnvironment()));
    services.AddSingleton(anchor);
    services.AddScoped(_ => anchor.Acquire());
    services.AddScoped(sp =>
    {
        sp.GetRequiredService<GraphAnchor.Lease>(); // held until the call's scope ends, after the store is disposed
        return GraphStore.Open(dbPath, readOnly: true, TimeSpan.FromSeconds(15));
    });
    services
        .AddMcpServer(options =>
        {
            // What a client shows on its page for this server (VS Code's "MCP Server: codegraph", Claude's /mcp).
            options.ServerInfo = new Implementation
            {
                Name = "codegraph",
                Title = "CodeGraph",
                Version = ServerVersion(),
                Description = "Compiler-built code graph for C#, TypeScript, Angular and other codebases: who calls this, what breaks if it changes, "
                            + "which tests guard it, where is the code that does X. Read-only, local, no network.",
                WebsiteUrl = "https://github.com/nerdcules/CodeGraph",
                Icons = [new Icon { Source = LogoDataUri(), MimeType = "image/svg+xml", Sizes = ["any"] }],
            };
            options.ServerInstructions =
                """
                codegraph answers structural questions about the indexed codebase from a compiler-built graph, so prefer
                it to grepping or opening files. Typical turn: find_symbol (or search_code when you only know what the
                code does) → impact_of / callers_of / references_to → snippet on the two or three declarations that matter.
                Ids in [brackets] are opaque (Roslyn doc-comment ids for C#, ts:/java:/scip: prefixes otherwise); pass them back verbatim. An answer that starts with
                "⚠ STALE INDEX" is from a graph older than the working tree; rows ending in STALE come from edited files.
                Call impact_of before changing anything shared, and tests_for / covered_by to find the tests that guard it.
                When codegraph-index --watch is running (live_status), four tools answer from the live compiler (Roslyn, the TypeScript language service, or a language server):
                symbol_at turns a file position into an id, live_references finds every use of a symbol including
                framework and NuGet members, diagnostics reports compiler errors, and check_change compiles a proposed
                file in memory and reports what it would break before you write it.
                """;
        })
        .WithStdioServerTransport()
        .WithToolsFromAssembly()
        .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
        {
            if (!File.Exists(dbPath))
            {
                return new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = $"no code graph at {dbPath}. Build it from this worktree with: codegraph-index <solution.sln>" }],
                };
            }

            var scope = telemetry.Begin(context.Params?.Name ?? "?");
            anchor.ReleaseIfSwapped();
            // The daemon hears every save the moment it lands, before the (cached) freshness check would notice
            // it, so ask it first; its idle answer costs a fifth of a millisecond.
            var daemonStatus = await live.PingAsync(cancellationToken);
            if (daemonStatus is null && Interlocked.Exchange(ref autoStart, 0) == 1)
            {
                Console.Error.WriteLine("codegraph: " + LiveLauncher.Start(dbPath, freshness.Check().Meta.GetValueOrDefault(CodeGraph.Core.MetaKeys.Solution)));
            }

            if (daemonStatus is { Loading: false } daemon
                && (daemon.Pending > 0 || daemon.Indexing || freshness.Check().IsStale))
            {
                // The daemon's write needs the file to itself; holding the anchor through the flush would deadlock it.
                anchor.Release();
                if (await live.FlushAsync(flushWait, cancellationToken) is not null)
                {
                    freshness.Invalidate();
                }
            }

            CallToolResult result;
            try
            {
                result = await next(context, cancellationToken);
            }
            catch
            {
                scope.Complete(0, isError: true);
                throw;
            }

            if (freshness.Check().Warning() is { } warning)
            {
                result.Content.Insert(0, new TextContentBlock { Text = warning });
            }

            scope.Complete(result.Content.OfType<TextContentBlock>().Sum(block => (long)block.Text.Length), result.IsError == true);
            return result;
        }));
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
    AddGraph(webBuilder.Services);

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
    AddGraph(builder.Services);

    await builder.Build().RunAsync();
}

return 0;

// ── Web API routes ────────────────────────────────────────────
static void MapWebApi(WebApplication app)
{
    var html = ReadEmbeddedHtml();
    app.MapGet("/", () => Results.Content(html, "text/html"));
    app.MapGraphApi(); // shared with codegraph-web; see CodeGraph.Web/WebApi.cs
}

static string ServerVersion()
{
    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
    var informational = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion;
    // Strip the source-link hash that the SDK appends ("1.2.0+abc123").
    var version = informational?.Split('+')[0];
    return string.IsNullOrEmpty(version) ? assembly.GetName().Version?.ToString(3) ?? "0.0.0" : version;
}

static string LogoDataUri()
{
    using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("CodeGraph.Mcp.logo.svg")
        ?? throw new InvalidOperationException("Embedded logo not found. Rebuild the project.");
    var svg = new StreamReader(stream).ReadToEnd();
    return "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg));
}

static string ReadEmbeddedHtml()
{
    var asm = System.Reflection.Assembly.GetExecutingAssembly();
    using var stream = asm.GetManifestResourceStream("CodeGraph.Mcp.wwwroot.index.html")
        ?? asm.GetManifestResourceStream("CodeGraph.Mcp.index.html")
        ?? throw new InvalidOperationException("Embedded web UI not found. Rebuild the project.");
    return new StreamReader(stream).ReadToEnd();
}
