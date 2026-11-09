using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace CodeGraph.Core;

/// <summary>
/// Where vectors for semantic search come from: any OpenAI-compatible <c>/v1/embeddings</c> endpoint, typically a
/// local one (Ollama: <c>http://localhost:11434/v1/embeddings</c> with <c>nomic-embed-text</c>). Off unless
/// <c>CODEGRAPH_EMBEDDINGS_URL</c> is set. A remote endpoint receives declaration text (names, signatures, doc
/// comments and the start of each body), so the default stays local-only by being off.
/// </summary>
public sealed record EmbeddingSettings(Uri Url, string Model, string? ApiKey)
{
    public const string UrlVariable = "CODEGRAPH_EMBEDDINGS_URL";
    public const string ModelVariable = "CODEGRAPH_EMBEDDINGS_MODEL";
    public const string KeyVariable = "CODEGRAPH_EMBEDDINGS_KEY";

    /// <summary>Null when embeddings are not configured.</summary>
    public static EmbeddingSettings? FromEnvironment()
    {
        var url = Environment.GetEnvironmentVariable(UrlVariable);
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        var model = Environment.GetEnvironmentVariable(ModelVariable);
        return new EmbeddingSettings(uri, string.IsNullOrWhiteSpace(model) ? "nomic-embed-text" : model.Trim(), Environment.GetEnvironmentVariable(KeyVariable));
    }

    public bool IsLocal => Url.IsLoopback;
}

/// <summary>
/// A node's vector, quantised (see <see cref="VectorQuantizer"/>), and the hash of the text it was computed from,
/// so unchanged declarations are not re-embedded.
/// </summary>
public sealed record NodeEmbedding(string NodeId, string TextHash, QuantizedVector Vector);

/// <summary>Calls an embeddings endpoint in batches.</summary>
public sealed class EmbeddingClient(EmbeddingSettings settings, HttpClient? http = null)
{
    private const int BatchSize = 32;
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

    public EmbeddingSettings Settings => settings;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        var vectors = new List<float[]>(texts.Count);
        for (var start = 0; start < texts.Count; start += BatchSize)
        {
            var batch = texts.Skip(start).Take(BatchSize).ToList();
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.Url)
            {
                // Buffered with a Content-Length: some local servers reject a chunked body.
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new EmbeddingRequest(settings.Model, batch)), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(settings.ApiKey))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.ApiKey);
            }

            using var response = await _http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken)
                       ?? throw new HttpRequestException("empty embeddings response");
            if (body.Data.Count != batch.Count)
            {
                throw new HttpRequestException($"asked for {batch.Count} embeddings, got {body.Data.Count}");
            }

            vectors.AddRange(body.Data.OrderBy(d => d.Index).Select(d => d.Embedding));
        }

        return vectors;
    }

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    private sealed record EmbeddingRequest([property: JsonPropertyName("model")] string Model, [property: JsonPropertyName("input")] IReadOnlyList<string> Input);

    private sealed record EmbeddingResponse([property: JsonPropertyName("data")] IReadOnlyList<EmbeddingItem> Data);

    private sealed record EmbeddingItem([property: JsonPropertyName("index")] int Index, [property: JsonPropertyName("embedding")] float[] Embedding);
}

/// <summary>
/// Reciprocal rank fusion of the lexical and the semantic ranking: each list contributes 1 / (k + rank) per node.
/// Robust to the two scores being on different scales, and a node both rankings like rises to the top.
/// </summary>
public static class SearchFusion
{
    public static IReadOnlyList<(GraphNode Node, double Score, int? LexicalRank, int? SemanticRank)> Fuse(
        IReadOnlyList<GraphNode> lexical, IReadOnlyList<GraphNode> semantic, int limit, int k = 60)
    {
        var fused = new Dictionary<string, (GraphNode Node, double Score, int? Lexical, int? Semantic)>(StringComparer.Ordinal);
        for (var i = 0; i < lexical.Count; i++)
        {
            fused[lexical[i].Id] = (lexical[i], 1.0 / (k + i + 1), i + 1, null);
        }

        for (var i = 0; i < semantic.Count; i++)
        {
            var node = semantic[i];
            fused[node.Id] = fused.TryGetValue(node.Id, out var existing)
                ? (existing.Node, existing.Score + 1.0 / (k + i + 1), existing.Lexical, i + 1)
                : (node, 1.0 / (k + i + 1), null, i + 1);
        }

        return fused.Values
            .OrderByDescending(f => f.Score)
            .ThenBy(f => f.Node.FullName, StringComparer.Ordinal)
            .Take(limit)
            .Select(f => (f.Node, f.Score, f.Lexical, f.Semantic))
            .ToList();
    }
}
