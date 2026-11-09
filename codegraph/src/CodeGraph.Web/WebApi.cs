using CodeGraph.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodeGraph.Web;

/// <summary>
/// The JSON API behind the graph visualiser. One definition serves both hosts: <c>codegraph-web</c> and
/// <c>codegraph-mcp --web</c> link this file, so the routes cannot drift apart. The shapes are what Cytoscape
/// expects: <c>{ data: { id, source, target, … } }</c>.
/// </summary>
public static class WebApi
{
    private const string DefaultSubgraphEdges = "Calls,Inherits,Implements,Overrides";

    public static IEndpointRouteBuilder MapGraphApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/stats", (GraphStore store) =>
        {
            var (nodes, edges) = store.Count();
            return new { nodes, edges };
        });

        app.MapGet("/api/search", (string q, string? kind, GraphStore store, int limit = 20) =>
        {
            NodeKind? kindFilter = kind is not null && Enum.TryParse<NodeKind>(kind, ignoreCase: true, out var k) ? k : null;
            return store.FindSymbol(q, kindFilter, Math.Clamp(limit, 1, 50)).Select(n => new
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

        app.MapGet("/api/subgraph", (string id, string? edges, GraphStore store, int depth = 2) =>
        {
            var result = store.SubGraph(id, EdgeKinds(edges, DefaultSubgraphEdges), Math.Clamp(depth, 1, 5));
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
                    },
                }),
                edges = result.Edges.Select((e, i) => new
                {
                    data = new { id = $"e{i}", source = e.FromId, target = e.ToId, kind = e.Kind },
                }),
            };
        });

        app.MapGet("/api/path", (string from, string to, string? edges, GraphStore store, int maxDepth = 10, int maxPaths = 3) =>
        {
            var edgeKinds = EdgeKinds(edges, nameof(EdgeKind.Calls));
            var paths = store.PathBetween(from, to, edgeKinds, Math.Clamp(maxDepth, 1, 20), Math.Clamp(maxPaths, 1, 5));
            return paths.Select((path, pi) => new
            {
                nodes = path.Select(n => new
                {
                    data = new
                    {
                        id = n.Id, label = n.Name, kind = n.Kind.ToString(), typeKind = n.TypeKind.ToString(),
                        fullName = n.FullName, file = n.FilePath, line = n.StartLine,
                    },
                }),
                edges = path.Zip(path.Skip(1), (a, b) => new
                {
                    data = new { id = $"p{pi}e{a.Id}", source = a.Id, target = b.Id, kind = edgeKinds[0] },
                }),
            });
        });

        return app;
    }

    /// <summary>A comma-separated list of edge kinds, unknown names dropped, falling back to <paramref name="fallback"/>.</summary>
    private static string[] EdgeKinds(string? list, string fallback)
    {
        var kinds = (list ?? fallback)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => Enum.TryParse<EdgeKind>(e, ignoreCase: true, out _))
            .ToArray();
        return kinds.Length == 0 ? [nameof(EdgeKind.Calls)] : kinds;
    }
}
