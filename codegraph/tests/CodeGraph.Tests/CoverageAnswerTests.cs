using CodeGraph.Core;
using CodeGraph.Core.Live;
using CodeGraph.Mcp;

namespace CodeGraph.Tests;

/// <summary>
/// The test lists an answer gives: the nearest 50 tests by depth, then the count of the rest at each depth, so an
/// answer stays under what a client shows a model in one piece and still carries the total and its shape.
/// </summary>
public sealed class CoverageAnswerTests
{
    private const string Target = "M:Demo.Service.Save";
    private const string Via = "M:Demo.Caller.Handle";

    /// <summary>55 tests call Save; 10 call Handle, which calls Save. So 65 reach it: 55 at depth 1, 10 at depth 2.</summary>
    private static (GraphNode[] Nodes, GraphEdge[] Edges) ManyTests()
    {
        var nodes = new List<GraphNode>
        {
            Graph.Node(Target, NodeKind.Method, "Save", "Demo.Service.Save"),
            Graph.Node(Via, NodeKind.Method, "Handle", "Demo.Caller.Handle"),
        };
        var edges = new List<GraphEdge> { new(Via, Target, EdgeKind.Calls) };
        for (var i = 0; i < 55; i++)
        {
            var id = $"M:Demo.Tests.Direct{i:D2}";
            nodes.Add(Graph.Node(id, NodeKind.Method, $"Direct{i:D2}", $"Demo.Tests.Direct{i:D2}", isTest: true));
            edges.Add(new GraphEdge(id, Target, EdgeKind.Calls));
        }

        for (var i = 0; i < 10; i++)
        {
            var id = $"M:Demo.Tests.Indirect{i:D2}";
            nodes.Add(Graph.Node(id, NodeKind.Method, $"Indirect{i:D2}", $"Demo.Tests.Indirect{i:D2}", isTest: true));
            edges.Add(new GraphEdge(id, Via, EdgeKind.Calls));
        }

        return (nodes.ToArray(), edges.ToArray());
    }

    private static CodeGraphTools Tools(TempDb db) =>
        new(db.Store, new IndexFreshness(db.DbPath, TimeSpan.FromSeconds(3)), new Telemetry(null),
            new LiveClient(db.DbPath), new SemanticSearch(null), new GraphSnapshotCache(db.DbPath), new AnswerPaths(null));

    [Fact]
    public void Covered_by_lists_the_nearest_50_tests_and_counts_the_rest_at_each_depth()
    {
        using var db = new TempDb();
        var (nodes, edges) = ManyTests();
        db.Store.Write(nodes, edges);

        var answer = Tools(db).CoveredBy(Target);
        var lines = answer.TrimEnd().Split('\n');

        Assert.StartsWith("65 test(s) reach Demo.Service.Save through calls", lines[0]);
        Assert.Equal("nearest 50 by depth; the rest by depth: 1: 5, 2: 10", lines[1].TrimEnd());
        Assert.Equal(50, lines.Skip(2).Count(l => l.StartsWith("method  ", StringComparison.Ordinal)));
        Assert.All(lines.Skip(2), l => Assert.Contains("depth 1", l));
        Assert.DoesNotContain("Indirect", answer);
    }

    [Fact]
    public void Covered_by_with_50_tests_or_fewer_lists_them_all_without_a_count()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        var answer = Tools(db).CoveredBy(Graph.Save);

        Assert.StartsWith("1 test(s) reach Demo.Service.Save through calls", answer);
        Assert.DoesNotContain("the rest by depth", answer);
        Assert.Contains("Demo.Tests.SaveTest  depth 1", answer);
    }

    [Fact]
    public void Impact_of_counts_the_tests_it_does_not_list_at_each_depth()
    {
        using var db = new TempDb();
        var (nodes, edges) = ManyTests();
        db.Store.Write(nodes, edges);

        var answer = Tools(db).ImpactOf(Target);

        Assert.Contains("impact of Demo.Service.Save: 1 dependents, 65 guarding tests", answer);
        Assert.Contains("tests (nearest 50 of 65; the rest by depth: 1: 5, 2: 10):", answer);
        Assert.Equal(51, answer.Split('\n').Count(l => l.StartsWith("method  ", StringComparison.Ordinal)));
    }
}
