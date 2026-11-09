using System.Diagnostics;
using CodeGraph.Core;

namespace CodeGraph.Indexer;

/// <summary>The embedding part of an index pass, apart from the rest so it can be tested against a fake endpoint.</summary>
public static class EmbeddingStep
{
    /// <summary>
    /// Vectors for the walked declarations: reused from the existing graph when the text is unchanged (by hash) and
    /// the model is the same, computed for the rest. Null when the endpoint fails; the graph is then written without
    /// new vectors and search falls back to words for those declarations.
    /// </summary>
    public static async Task<IReadOnlyList<NodeEmbedding>?> RunAsync(
        EmbeddingClient client, IReadOnlyDictionary<string, string> texts, string dbPath, Action<string> log, CancellationToken cancellationToken)
    {
        var settings = client.Settings;
        var clock = Stopwatch.StartNew();
        IReadOnlyDictionary<string, QuantizedVector> cache = new Dictionary<string, QuantizedVector>();
        if (File.Exists(dbPath))
        {
            using var old = GraphStore.Open(dbPath, readOnly: true, TimeSpan.FromSeconds(30));
            if (old.ReadMeta().GetValueOrDefault(MetaKeys.EmbeddingsModel) == settings.Model)
            {
                cache = old.EmbeddingCache();
            }
        }

        var result = new List<NodeEmbedding>(texts.Count);
        var missing = new List<(string NodeId, string Hash, string Text)>();
        foreach (var (nodeId, text) in texts)
        {
            var hash = EmbeddingClient.Hash(text);
            if (cache.TryGetValue(hash, out var vector))
            {
                result.Add(new NodeEmbedding(nodeId, hash, vector));
            }
            else
            {
                missing.Add((nodeId, hash, text));
            }
        }

        try
        {
            var vectors = await client.EmbedAsync(missing.Select(m => m.Text).ToList(), cancellationToken);
            result.AddRange(missing.Select((m, i) => new NodeEmbedding(m.NodeId, m.Hash, VectorQuantizer.Quantize(vectors[i]))));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            log($"embeddings: {settings.Url} failed ({e.Message}); search_code stays lexical for these declarations");
            return null;
        }

        log($"embeddings: {result.Count - missing.Count} reused, {missing.Count} computed with {settings.Model} in {clock.Elapsed.TotalSeconds:F1}s{(settings.IsLocal ? "" : $" (sent to {settings.Url.Host})")}");
        return result;
    }
}
