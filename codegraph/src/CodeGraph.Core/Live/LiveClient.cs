using System.IO.Pipes;
using System.Text.Json;

namespace CodeGraph.Core.Live;

/// <summary>
/// Calls the daemon for one graph. Every call opens its own connection, which on a local pipe costs well under
/// a millisecond. Absence is checked without connecting (see <see cref="LivePipe.Exists"/>), and an endpoint that
/// exists but refuses (a socket left by a killed daemon) is remembered as dead for <see cref="DeadFor"/>, so a
/// burst of tool calls pays the short connect timeout at most once.
/// </summary>
public sealed class LiveClient(string dbPath)
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(150);

    public static readonly TimeSpan DeadFor = TimeSpan.FromSeconds(3);

    private long _deadUntilTicks;

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
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _deadUntilTicks) || !LivePipe.Exists(PipeName))
        {
            throw new IOException($"no live daemon on pipe {PipeName}");
        }

        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cancellationToken);
        }
        catch (TimeoutException)
        {
            Interlocked.Exchange(ref _deadUntilTicks, (DateTime.UtcNow + DeadFor).Ticks);
            throw;
        }

        Interlocked.Exchange(ref _deadUntilTicks, 0);

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
