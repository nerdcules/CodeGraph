using CodeGraph.Core;

namespace CodeGraph.Mcp;

/// <summary>
/// The server's side of semantic search: embeds a query with the endpoint the graph was built with, and remembers
/// recent query vectors so a model rephrasing the same question does not pay for it twice. Absent settings mean
/// lexical search only.
/// </summary>
public sealed class SemanticSearch(EmbeddingSettings? settings)
{
    private readonly EmbeddingClient? _client = settings is null ? null : new EmbeddingClient(settings, new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
    private readonly Dictionary<string, float[]> _recent = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public EmbeddingSettings? Settings => settings;

    /// <summary>The query's vector, or null with the reason when it cannot be had.</summary>
    public async Task<(float[]? Vector, string? Problem)> EmbedAsync(string query, string? graphModel, CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            return (null, $"semantic search is off for this server; set {EmbeddingSettings.UrlVariable} (and {EmbeddingSettings.ModelVariable}) in its environment");
        }

        if (graphModel is not null && graphModel != _client.Settings.Model)
        {
            return (null, $"the graph was embedded with {graphModel} but this server is set to {_client.Settings.Model}; vectors from different models do not compare");
        }

        lock (_lock)
        {
            if (_recent.TryGetValue(query, out var cached))
            {
                return (cached, null);
            }
        }

        try
        {
            var vector = (await _client.EmbedAsync([query], cancellationToken))[0];
            lock (_lock)
            {
                if (_recent.Count > 256)
                {
                    _recent.Clear();
                }

                _recent[query] = vector;
            }

            return (vector, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return (null, $"the embeddings endpoint {_client.Settings.Url} failed ({e.Message})");
        }
    }
}
