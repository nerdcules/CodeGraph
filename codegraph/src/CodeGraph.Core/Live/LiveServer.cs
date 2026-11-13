using System.IO.Pipes;
using System.Text.Json;

namespace CodeGraph.Core.Live;

/// <summary>
/// Listens on a graph's pipe and hands each request to <paramref name="handler"/>. Transport only: the handler
/// owns the Roslyn solution and lives in the indexer. Requests run concurrently; a Roslyn <c>Solution</c> is
/// immutable, so that is safe for the handler as long as it reads one snapshot per request.
/// </summary>
public sealed class LiveServer(string pipeName, Func<LiveRequest, CancellationToken, Task<LiveResponse>> handler, Action<string>? log = null)
{
    public string PipeName => pipeName;

    /// <summary>Accepts connections until <paramref name="cancellationToken"/> fires.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // On Unix every server instance shares one listening socket, which .NET closes and unlinks whenever no
        // unconnected instance exists: briefly after each accept. Clients check the socket file exists before
        // connecting, so that gap read as "no daemon". This instance never accepts and holds the listener open.
        // Windows needs no keeper: the pipe stays listed while any instance exists. A keeper there also takes
        // connections, because a Windows client can connect to an instance that is not waiting, and nobody reads them.
        await using var keeper = OperatingSystem.IsWindows() ? null : Create();
        var pipe = Create();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException e)
                {
                    await pipe.DisposeAsync();
                    log?.Invoke($"live: accept failed: {e.Message}");
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        pipe = Create();
                    }

                    continue;
                }

                // Keep a listener alive before a fast handler can dispose the accepted instance.
                var connected = pipe;
                pipe = Create();
                _ = ServeAsync(connected, cancellationToken);
            }
        }
        finally
        {
            await pipe.DisposeAsync();
        }
    }

    private NamedPipeServerStream Create() =>
        new(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, leaveOpen: true);
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                LiveResponse response;
                try
                {
                    var request = JsonSerializer.Deserialize<LiveRequest>(line, LivePipe.Json)
                                  ?? throw new JsonException("empty request");
                    response = await handler(request, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    response = LiveResponse.Fail(e.Message);
                }

                var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                await writer.WriteAsync(JsonSerializer.Serialize(response, LivePipe.Json) + "\n");
                if (OperatingSystem.IsWindows())
                {
                    pipe.WaitForPipeDrain();
                }
            }
            catch (IOException)
            {
                // The client went away mid-call; nothing to answer.
            }
        }
    }
}
