using CodeGraph.Core;

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
builder.Services.AddSingleton(new GraphStore(dbPath, readOnly: true));

var app = builder.Build();
app.UseStaticFiles();

app.MapGet("/api/search", (string q, string? kind, int limit = 20, GraphStore store = null!) =>
{
    NodeKind? kindFilter = kind is not null && Enum.TryParse<NodeKind>(kind, ignoreCase: true, out var k) ? k : null;
    return store.FindSymbol(q, kindFilter, Math.Clamp(limit, 1, 50))
        .Select(n => new
        {
            id = n.Id,
            label = n.Name,
            fullName = n.FullName,
            kind = n.Kind.ToString(),
            typeKind = n.TypeKind.ToString(),
            accessibility = n.Accessibility.ToString(),
            signature = n.Signature,
            file = n.FilePath,
            line = n.StartLine,
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

app.MapGet("/api/subgraph", (string id, string? edges, int depth = 2, GraphStore store = null!) =>
{
    var edgeKinds = (edges ?? "Calls,Inherits,Implements,Overrides")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(e => Enum.TryParse<EdgeKind>(e, ignoreCase: true, out _))
        .ToArray();
    if (edgeKinds.Length == 0) edgeKinds = [nameof(EdgeKind.Calls)];

    var result = store.SubGraph(id, edgeKinds, depth);
    return new
    {
        nodes = result.Nodes.Select(n => new
        {
            data = new
            {
                id = n.Id,
                label = n.Name,
                kind = n.Kind.ToString(),
                typeKind = n.TypeKind.ToString(),
                accessibility = n.Accessibility.ToString(),
                fullName = n.FullName,
                isAsync = n.IsAsync,
                isStatic = n.IsStatic,
                file = n.FilePath,
                line = n.StartLine,
            }
        }),
        edges = result.Edges.Select((e, i) => new
        {
            data = new
            {
                id = $"e{i}",
                source = e.FromId,
                target = e.ToId,
                kind = e.Kind,
            }
        }),
    };
});

app.MapGet("/api/path", (string from, string to, string? edges, int maxDepth = 10, int maxPaths = 3, GraphStore store = null!) =>
{
    var edgeKinds = (edges ?? "Calls")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(e => Enum.TryParse<EdgeKind>(e, ignoreCase: true, out _))
        .ToArray();
    if (edgeKinds.Length == 0) edgeKinds = [nameof(EdgeKind.Calls)];

    var paths = store.PathBetween(from, to, edgeKinds, Math.Clamp(maxDepth, 1, 20), Math.Clamp(maxPaths, 1, 5));
    return paths.Select((path, pi) => new
    {
        nodes = path.Select(n => new
        {
            data = new { id = n.Id, label = n.Name, kind = n.Kind.ToString(), typeKind = n.TypeKind.ToString(),
                         fullName = n.FullName, file = n.FilePath, line = n.StartLine }
        }),
        edges = path.Zip(path.Skip(1), (a, b) => new
        {
            data = new { id = $"p{pi}e{a.Id}", source = a.Id, target = b.Id, kind = edgeKinds[0] }
        }),
    });
});

app.MapGet("/api/stats", (GraphStore store) =>
{
    var (nodes, edges) = store.Count();
    return new { nodes, edges };
});

app.MapFallbackToFile("index.html");
app.Run();
return 0;
