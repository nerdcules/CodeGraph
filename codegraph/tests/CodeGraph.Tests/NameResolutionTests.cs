using CodeGraph.Core;
using CodeGraph.Core.Live;
using CodeGraph.Mcp;

namespace CodeGraph.Tests;

/// <summary>
/// impact_of, covered_by, callers_of, tests_for and snippet take a name where they take an id, when exactly one
/// symbol has that exact name; otherwise they list the candidates. And the server instructions stay under the
/// 4,096 characters Claude Code shows a model.
/// </summary>
public sealed class NameResolutionTests
{
    private static CodeGraphTools Tools(TempDb db) =>
        new(db.Store, new IndexFreshness(db.DbPath, TimeSpan.FromSeconds(3)), new Telemetry(null),
            new LiveClient(db.DbPath), new SemanticSearch(null), new GraphSnapshotCache(db.DbPath), new AnswerPaths(null));

    [Fact]
    public void A_unique_name_answers_like_the_id()
    {
        using var db = new TempDb();
        db.Store.Seeded();
        var tools = Tools(db);

        Assert.Equal(tools.ImpactOf(Graph.Save), tools.ImpactOf("Save"));
        Assert.Equal(tools.CoveredBy(Graph.Save), tools.CoveredBy("Service.Save"));
        Assert.Equal(tools.CallersOf(Graph.Save), tools.CallersOf("Demo.Service.Save"));
        Assert.Equal(tools.TestsFor(Graph.Save), tools.TestsFor("Save"));
        Assert.StartsWith("impact of Demo.Service.Save:", tools.ImpactOf("Save"));
    }

    [Fact]
    public async Task Snippet_takes_a_name_too()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        var answer = await Tools(db).Snippet("Save", cancellationToken: TestContext.Current.CancellationToken);

        // The seeded graph points at a file that does not exist; the message names the node, so the name resolved.
        Assert.StartsWith("Demo.Service.Save has no readable source location", answer);
    }

    [Fact]
    public void A_name_that_several_symbols_have_lists_them_with_their_ids()
    {
        using var db = new TempDb();
        db.Store.Write(
            [
                Graph.Node("M:Demo.Alpha.Run", NodeKind.Method, "Run", "Demo.Alpha.Run"),
                Graph.Node("M:Demo.Beta.Run", NodeKind.Method, "Run", "Demo.Beta.Run"),
            ],
            []);

        var answer = Tools(db).CallersOf("Run");

        Assert.StartsWith("2 symbols are named Run; pass the id of one:", answer);
        Assert.Contains($"[{NodeHandles.Encode(NodeHandles.Hash("M:Demo.Alpha.Run"))}]", answer);
        Assert.Contains($"[{NodeHandles.Encode(NodeHandles.Hash("M:Demo.Beta.Run"))}]", answer);
        // A qualified tail picks one of them.
        Assert.StartsWith("Demo.Alpha.Run: no callers", Tools(db).CallersOf("Alpha.Run"));
    }

    [Fact]
    public void A_name_nothing_has_says_so()
    {
        using var db = new TempDb();
        db.Store.Seeded();

        var answer = Tools(db).TestsFor("Nope");

        Assert.StartsWith("no node has the id 'Nope' and no symbol has that exact name.", answer);
        Assert.StartsWith("'zzzzzzzz' is not a handle that this server knows", Tools(db).TestsFor("zzzzzzzz"));
    }

    [Fact]
    public void The_server_instructions_fit_what_Claude_Code_shows_a_model()
    {
        Assert.InRange(ServerInstructions.Text.Length, 1, ServerInstructions.Limit - 1);
        Assert.Equal(4096, ServerInstructions.Limit);
        Assert.Contains("impact_of", ServerInstructions.Text);
        Assert.Contains("handle", ServerInstructions.Text);
        Assert.Contains("relative to the root", ServerInstructions.Text);
        Assert.Contains("index_status", ServerInstructions.Text);
    }
}
