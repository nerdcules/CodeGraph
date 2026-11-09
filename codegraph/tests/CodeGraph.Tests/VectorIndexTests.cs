using CodeGraph.Core;

namespace CodeGraph.Tests;

/// <summary>The quantised in-memory vector search must rank like exact float cosine, and stay in step with the graph file.</summary>
public sealed class VectorIndexTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(384)]
    [InlineData(769)]
    public void The_simd_dot_product_equals_the_scalar_one_at_any_length(int length)
    {
        var random = new Random(length);
        var a = Enumerable.Range(0, length).Select(_ => (sbyte)random.Next(-127, 128)).ToArray();
        var b = Enumerable.Range(0, length).Select(_ => (sbyte)random.Next(-127, 128)).ToArray();

        Assert.Equal(a.Zip(b, (x, y) => x * y).Sum(), VectorQuantizer.Dot(a, b));
    }

    [Fact]
    public void Quantised_ranking_matches_exact_float_cosine()
    {
        var random = new Random(7);
        float[] Vector() => Enumerable.Range(0, 384).Select(_ => (float)random.NextDouble() - 0.5f).ToArray();
        var vectors = Enumerable.Range(0, 2000).Select(i => ($"M:{i}", Vector())).ToList();
        var index = new VectorIndex(vectors.Select(v => (v.Item1, VectorQuantizer.Quantize(v.Item2))).ToList());

        var overlap = 0;
        for (var q = 0; q < 20; q++)
        {
            var query = Vector();
            var exact = vectors.OrderByDescending(v => Cosine(v.Item2, query)).Take(10).Select(v => v.Item1).ToHashSet();
            overlap += index.Top(VectorQuantizer.Quantize(query), 10).Count(t => exact.Contains(t.Id));
        }

        // 200 slots over 20 queries; quantisation may swap a near-tie at the edge of the top 10, rarely more.
        Assert.True(overlap >= 190, $"top-10 overlap {overlap}/200");
    }

    [Fact]
    public void Similarity_estimates_cosine()
    {
        var a = new[] { 1f, 2f, 3f, 0f };
        var b = new[] { 2f, 1f, 3f, 1f };
        var index = new VectorIndex([("x", VectorQuantizer.Quantize(a))]);

        Assert.Equal(Cosine(a, b), index.Top(VectorQuantizer.Quantize(b), 1)[0].Similarity, precision: 2);
    }

    [Fact]
    public void A_filtered_search_widens_until_enough_matches_survive()
    {
        using var db = new TempDb();
        var random = new Random(3);
        float[] Vector() => Enumerable.Range(0, 32).Select(_ => (float)random.NextDouble() - 0.5f).ToArray();
        var nodes = Enumerable.Range(0, 500).Select(i => Graph.Node($"M:N.M{i}", i % 100 == 0 ? NodeKind.Property : NodeKind.Method, $"M{i}", $"N.M{i}")).ToList();
        var embeddings = nodes.Select(n => new NodeEmbedding(n.Id, n.Id, VectorQuantizer.Quantize(Vector()))).ToList();
        db.Store.Replace(full: true, [], nodes, [], [], [], new Dictionary<string, string>(), null, embeddings);

        var properties = db.Store.VectorSearch(Vector(), kind: NodeKind.Property, limit: 5);

        Assert.Equal(5, properties.Count);
        Assert.All(properties, p => Assert.Equal(NodeKind.Property, p.Node.Kind));
        Assert.True(properties.Zip(properties.Skip(1)).All(p => p.First.Similarity >= p.Second.Similarity));
    }

    [Fact]
    public void A_rewritten_graph_file_reloads_the_index()
    {
        using var db = new TempDb();
        var node = Graph.Node("M:N.A", NodeKind.Method, "A", "N.A");
        db.Store.Replace(full: true, [], [node], [], [], [], new Dictionary<string, string>(), null,
            [new NodeEmbedding(node.Id, "h1", VectorQuantizer.Quantize([1f, 0f]))]);
        Assert.Equal(1.0, db.Store.VectorSearch([1f, 0f], limit: 1)[0].Similarity, precision: 2);

        db.Store.Replace(full: false, [], [node], [], [], [], new Dictionary<string, string>(), null,
            [new NodeEmbedding(node.Id, "h2", VectorQuantizer.Quantize([0f, 1f]))]);
        File.SetLastWriteTimeUtc(db.DbPath, DateTime.UtcNow.AddSeconds(5));

        Assert.Equal(0.0, db.Store.VectorSearch([1f, 0f], limit: 1)[0].Similarity, precision: 2);
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return dot / Math.Sqrt(na * nb);
    }
}
