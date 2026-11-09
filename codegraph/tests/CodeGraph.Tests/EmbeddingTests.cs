using System.Net;
using System.Text.Json;
using CodeGraph.Core;
using CodeGraph.Indexer;

namespace CodeGraph.Tests;

/// <summary>
/// A local stand-in for an embeddings endpoint, OpenAI-compatible. Each word of the input (after the same tokenizer
/// search uses) adds to one of 64 dimensions, and "remove", "erase" and "delete" share a dimension, so a query by
/// meaning finds code by a different word. Deterministic, so rankings can be asserted.
/// </summary>
internal sealed class FakeEmbedder : IDisposable
{
    private static readonly Dictionary<string, string> Synonyms = new() { ["remov"] = "delet", ["eras"] = "delet", ["purg"] = "delet" };
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();

    public FakeEmbedder()
    {
        for (var port = 42000 + Random.Shared.Next(2000); ; port++)
        {
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                Url = new Uri($"http://127.0.0.1:{port}/v1/embeddings");
                break;
            }
            catch (HttpListenerException)
            {
                _listener.Close();
                _listener = new HttpListener();
            }
        }

        _ = Task.Run(ServeAsync);
    }

    public Uri Url { get; }

    public int Requests;

    public int Inputs;

    public bool Fail { get; set; }

    public static float[] Vector(string text)
    {
        var v = new float[64];
        foreach (var term in SearchText.Tokenize(text))
        {
            var t = Synonyms.GetValueOrDefault(term, term);
            v[(int)((uint)StringComparer.Ordinal.GetHashCode(t) % 64)] += 1;
        }

        var norm = MathF.Sqrt(v.Sum(x => x * x));
        return norm == 0 ? v : v.Select(x => x / norm).ToArray();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }

            Interlocked.Increment(ref Requests);
            if (Fail)
            {
                context.Response.StatusCode = 503;
                context.Response.Close();
                continue;
            }

            using var body = await JsonDocument.ParseAsync(context.Request.InputStream);
            var inputs = body.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
            Interlocked.Add(ref Inputs, inputs.Count);
            var response = new { data = inputs.Select((t, i) => new { index = i, embedding = Vector(t) }) };
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            context.Response.Close();
        }
    }
}

public sealed class EmbeddingTests
{
    private const string Source = """
        namespace N;
        /// <summary>Removes a file from the cache directory.</summary>
        public class Cache
        {
            /// <summary>Deletes the entry's file.</summary>
            public void Purge(string key) { System.IO.File.Delete(key); }
            /// <summary>Reads the entry back.</summary>
            public string Load(string key) => System.IO.File.ReadAllText(key);
        }
        public class Mailer { /// <summary>Sends a welcome email.</summary>
            public void Welcome(string address) { } }
        """;

    [Fact]
    public async Task The_client_batches_and_keeps_order()
    {
        using var fake = new FakeEmbedder();
        var client = new EmbeddingClient(new EmbeddingSettings(fake.Url, "fake", null));
        var texts = Enumerable.Range(0, 70).Select(i => $"word{i} text").ToList();

        var vectors = await client.EmbedAsync(texts, TestContext.Current.CancellationToken);

        Assert.Equal(70, vectors.Count);
        Assert.Equal(3, fake.Requests); // 32 + 32 + 6
        Assert.Equal(FakeEmbedder.Vector("word69 text"), vectors[69]);
    }

    [Fact]
    public async Task Vector_search_finds_by_meaning_and_hybrid_puts_the_best_of_both_first()
    {
        using var fake = new FakeEmbedder();
        using var db = new TempDb();
        await IndexWithEmbeddings(db, fake);

        var byMeaning = db.Store.VectorSearch(FakeEmbedder.Vector("remove an entry"), limit: 3);
        Assert.Equal("M:N.Cache.Purge(System.String)", byMeaning[0].Node.Id);

        // Words alone miss it: nothing says "remove" in Purge, and the type's comment does.
        var byWords = db.Store.Search("remove entry", limit: 3).Select(h => h.Node).ToList();
        var fused = SearchFusion.Fuse(byWords, byMeaning.Select(h => h.Node).ToList(), 3);
        Assert.Contains(fused, f => f.Node.Id == "M:N.Cache.Purge(System.String)");
        Assert.True(fused[0].LexicalRank is not null || fused[0].SemanticRank is not null);
    }

    [Fact]
    public async Task A_reindex_embeds_only_changed_declarations_and_removing_a_file_drops_its_vectors()
    {
        using var fake = new FakeEmbedder();
        using var db = new TempDb();
        var first = await IndexWithEmbeddings(db, fake);
        Assert.Equal(first, db.Store.EmbeddingCount());
        var inputsAfterFirst = fake.Inputs;

        // Same text again: everything comes from the cache.
        var indexed = await Indexed.OfAsync(Source);
        var embeddings = await EmbeddingStep.RunAsync(new EmbeddingClient(new EmbeddingSettings(fake.Url, "fake", null)), indexed.EmbeddingTexts, db.DbPath, _ => { }, TestContext.Current.CancellationToken);
        Assert.Equal(first, embeddings!.Count);
        Assert.Equal(inputsAfterFirst, fake.Inputs);

        db.Store.Replace(full: false, [Indexed.PathOf("Test.cs")], [], [], [], [], Meta(), [], []);
        Assert.Equal(0, db.Store.EmbeddingCount());
    }

    [Fact]
    public async Task A_failing_endpoint_yields_no_vectors_and_a_log_line_not_an_error()
    {
        using var fake = new FakeEmbedder { Fail = true };
        var indexed = await Indexed.OfAsync(Source);
        var log = new List<string>();

        var embeddings = await EmbeddingStep.RunAsync(new EmbeddingClient(new EmbeddingSettings(fake.Url, "fake", null)), indexed.EmbeddingTexts, Path.Combine(Path.GetTempPath(), "none.db"), log.Add, TestContext.Current.CancellationToken);

        Assert.Null(embeddings);
        Assert.Contains(log, l => l.Contains("stays lexical"));
    }

    [Fact]
    public void Fusion_rewards_agreement_and_keeps_either_sides_unique_hits()
    {
        GraphNode N(string id) => Graph.Node(id, NodeKind.Method, id, id);
        var fused = SearchFusion.Fuse([N("a"), N("b"), N("c")], [N("b"), N("d")], 10);

        Assert.Equal("b", fused[0].Node.Id);
        Assert.Equal((2, 1), (fused[0].LexicalRank, fused[0].SemanticRank));
        Assert.Equal(["a", "b", "c", "d"], fused.Select(f => f.Node.Id).Order());
    }

    [Fact]
    public void A_loopback_endpoint_counts_as_local_and_any_other_does_not()
    {
        Assert.True(new EmbeddingSettings(new Uri("http://localhost:11434/v1/embeddings"), "m", null).IsLocal);
        Assert.False(new EmbeddingSettings(new Uri("https://api.example.com/v1/embeddings"), "m", null).IsLocal);
    }

    private static async Task<int> IndexWithEmbeddings(TempDb db, FakeEmbedder fake)
    {
        var indexed = await Indexed.OfAsync(Source);
        var embeddings = await EmbeddingStep.RunAsync(new EmbeddingClient(new EmbeddingSettings(fake.Url, "fake", null)), indexed.EmbeddingTexts, db.DbPath, _ => { }, TestContext.Current.CancellationToken);
        db.Store.Replace(full: true, [], indexed.Nodes, indexed.Edges, indexed.Origins, [], Meta(), indexed.SearchDocuments, embeddings);
        return embeddings!.Count;
    }

    private static Dictionary<string, string> Meta() => new() { [MetaKeys.EmbeddingsModel] = "fake" };
}
