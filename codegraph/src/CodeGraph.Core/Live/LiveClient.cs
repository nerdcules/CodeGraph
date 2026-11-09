using System.IO.Pipes;
using System.Text.Json;

namespace CodeGraph.Core.Live;

/// <summary>
/// Calls the daemon for one graph. Every call opens its own connection, which on a local pipe costs well under
/// a millisecond, and a daemon that is not running fails in microseconds, so callers can probe freely.
/// </summary>
public sealed class LiveClient(string dbPath)
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);

    public string PipeName { get; } = LivePipe.NameFor(dbPath);

    /// <summary>True when a daemon answers on this graph's pipe.</summary>
    public async Task<LiveStatus?> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await CallAsync<LiveStatus>("ping", null, cancellationToken);
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or LiveException)
        {
            return null;
        }
    }

    /// <summary>
    /// Asks the daemon to index everything pending right now and waits for it, so the next graph query sees the
    /// working tree. Null when no daemon is running or it did not finish within <paramref name="wait"/>.
    /// </summary>
    public async Task<LiveStatus?> FlushAsync(TimeSpan wait, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(wait);
        try
        {
            return await CallAsync<LiveStatus>("flush", null, timeout.Token);
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or LiveException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>One request, one response. Throws <see cref="LiveException"/> for a daemon-side error and IO exceptions when it is not running.</summary>
    public async Task<T> CallAsync<T>(string method, object? parameters, CancellationToken cancellationToken = default)
    {
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cancellationToken);

        var request = new LiveRequest(method, parameters is null ? null : JsonSerializer.SerializeToElement(parameters, LivePipe.Json));
        var writer = new StreamWriter(pipe) { AutoFlush = true };
        await writer.WriteAsync(JsonSerializer.Serialize(request, LivePipe.Json) + "\n");

        using var reader = new StreamReader(pipe);
        var line = await reader.ReadLineAsync(cancellationToken) ?? throw new IOException("the daemon closed the pipe without answering");
        var response = JsonSerializer.Deserialize<LiveResponse>(line, LivePipe.Json) ?? throw new IOException("empty response from the daemon");
        if (response.Error is not null)
        {
            throw new LiveException(response.Error);
        }

        return response.Result is { } result
            ? result.Deserialize<T>(LivePipe.Json) ?? throw new IOException("the daemon's response did not deserialize")
            : throw new IOException("the daemon answered without a result");
    }
}
