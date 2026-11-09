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
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }
            catch (IOException e)
            {
                await pipe.DisposeAsync();
                log?.Invoke($"live: accept failed: {e.Message}");
                continue;
            }

            _ = ServeAsync(pipe, cancellationToken);
        }
    }

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
