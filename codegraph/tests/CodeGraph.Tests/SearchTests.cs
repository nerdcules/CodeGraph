using CodeGraph.Core;

namespace CodeGraph.Tests;

public sealed class SearchTests
{
    [Theory]
    [InlineData("SaveWorkoutAsync", new[] { "sav", "workout", "saveworkoutasync" })] // async is C# noise; save/saved/saves all stem to sav
    [InlineData("HTTPClient", new[] { "http", "client", "httpclient" })]
    [InlineData("parse_json2", new[] { "pars", "json", "parse_json2" })]
    [InlineData("where's the code that retries failed uploads", new[] { "retry", "fail", "upload" })]
    [InlineData("Sessions", new[] { "session" })]
    [InlineData("compared compare comparing", new[] { "compar", "compar", "compar" })]
    [InlineData("speed needed", new[] { "speed", "need" })]
    [InlineData("the a of", new string[0])]
    public void Tokenizes_identifiers_and_prose_the_same_way(string text, string[] expected)
    {
        Assert.Equal(expected, SearchText.Tokenize(text).ToArray());
    }

    [Fact]
    public void Weights_a_documents_terms()
    {
        var document = SearchText.Document("M:X", [("SaveWorkout", 3), ("writes the workout to disk", 1)]);

        Assert.NotNull(document);
        Assert.Equal(3 + 1, document.Terms["workout"]);
        Assert.Equal(3, document.Terms["sav"]);
        Assert.Equal(1, document.Terms["disk"]);
    }

    [Fact]
    public void Ranks_the_node_matching_most_query_terms_first()
    {
        using var db = new TempDb();
        db.Store.Replace(full: true, [],
            [
                Graph.Node("M:A.SendEmail", NodeKind.Method, "SendEmail", "A.SendEmail"),
                Graph.Node("M:A.SendWelcomeEmail", NodeKind.Method, "SendWelcomeEmail", "A.SendWelcomeEmail"),
                Graph.Node("M:A.Welcome", NodeKind.Method, "Welcome", "A.Welcome"),
            ],
            [], [], [], new Dictionary<string, string>(),
            [
                SearchText.Document("M:A.SendEmail", [("SendEmail", 3), ("smtp client message", 1)])!,
                SearchText.Document("M:A.SendWelcomeEmail", [("SendWelcomeEmail", 3), ("new user greeting", 1)])!,
                SearchText.Document("M:A.Welcome", [("Welcome", 3), ("greeting page", 1)])!,
            ]);

        var hits = db.Store.Search("send welcome email");

        Assert.Equal("M:A.SendWelcomeEmail", hits[0].Node.Id);
        Assert.Equal(3, hits[0].MatchedTerms);
        Assert.Equal(3, hits[0].QueryTerms);
        Assert.Equal(3, hits.Count);
    }

    [Fact]
    public void Filters_by_kind_and_file()
    {
        using var db = new TempDb();
        db.Store.Replace(full: true, [],
            [
                Graph.Node("T:A.Mailer", NodeKind.Type, "Mailer", "A.Mailer", filePath: "/repo/Mailer.cs"),
                Graph.Node("M:A.Mailer.Send", NodeKind.Method, "Send", "A.Mailer.Send", filePath: "/repo/Mailer.cs"),
                Graph.Node("M:B.Queue.Send", NodeKind.Method, "Send", "B.Queue.Send", filePath: "/repo/Queue.cs"),
            ],
            [], [], [], new Dictionary<string, string>(),
            [
                SearchText.Document("T:A.Mailer", [("Mailer send", 1)])!,
                SearchText.Document("M:A.Mailer.Send", [("Send", 3)])!,
                SearchText.Document("M:B.Queue.Send", [("Send", 3)])!,
            ]);

        Assert.Equal(["M:A.Mailer.Send", "M:B.Queue.Send"], db.Store.Search("send", kind: NodeKind.Method).Select(h => h.Node.Id));
        Assert.Equal(["M:B.Queue.Send"], db.Store.Search("send", filePattern: "Queue").Select(h => h.Node.Id));
    }

    [Fact]
    public void Removing_a_file_drops_its_search_documents()
    {
        using var db = new TempDb();
        db.Store.Replace(full: true, [],
            [Graph.Node("M:A.Send", NodeKind.Method, "Send", "A.Send", filePath: "/repo/A.cs")],
            [], [], [], new Dictionary<string, string>(),
            [SearchText.Document("M:A.Send", [("Send", 3)])!]);
        Assert.Single(db.Store.Search("send"));

        db.Store.Replace(full: false, ["/repo/A.cs"], [], [], [], [], new Dictionary<string, string>(), []);

        Assert.Empty(db.Store.Search("send"));
        Assert.Equal(0, db.Store.SearchDocumentCount());
    }

    [Fact]
    public void Rewriting_a_node_replaces_its_terms()
    {
        using var db = new TempDb();
        var node = Graph.Node("M:A.Send", NodeKind.Method, "Send", "A.Send", filePath: "/repo/A.cs");
        db.Store.Replace(full: true, [], [node], [], [], [], new Dictionary<string, string>(),
            [SearchText.Document("M:A.Send", [("Send", 3), ("smtp", 1)])!]);

        db.Store.Replace(full: false, ["/repo/A.cs"], [node], [], [], [], new Dictionary<string, string>(),
            [SearchText.Document("M:A.Send", [("Send", 3), ("queue", 1)])!]);

        Assert.Empty(db.Store.Search("smtp"));
        Assert.Single(db.Store.Search("queue"));
    }

    [Fact]
    public async Task Indexer_builds_a_document_from_names_comments_literals_and_body_identifiers()
    {
        var indexed = await Indexed.OfAsync(
            """
            namespace N;
            /// <summary>Talks to the mail server.</summary>
            class Mailer
            {
                // Retries once on failure.
                public void SendWelcome(string address)
                {
                    var subject = "Welcome aboard";
                    Transport.Deliver(address, subject);
                }
            }
            static class Transport { public static void Deliver(string a, string s) { } }
            """);

        var type = indexed.SearchDocuments.Single(d => d.NodeId == "T:N.Mailer");
        var method = indexed.SearchDocuments.Single(d => d.NodeId == "M:N.Mailer.SendWelcome(System.String)");

        Assert.Equal(3, type.Terms["mailer"]);
        Assert.Equal(2, type.Terms["server"]);
        Assert.True(method.Terms["welcom"] >= 4, "name ×3 plus the string literal"); // "Welcome aboard"
        Assert.Equal(1, method.Terms["aboard"]);
        Assert.Equal(2, method.Terms["retry"]);
        Assert.True(method.Terms.ContainsKey("deliver"));
        Assert.True(method.Terms.ContainsKey("address"));
    }
}
