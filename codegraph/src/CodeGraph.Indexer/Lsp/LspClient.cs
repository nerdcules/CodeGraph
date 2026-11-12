using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeGraph.Indexer.Lsp;

/// <summary>
/// A minimal Language Server Protocol client over a server's stdio: requests with responses, notifications,
/// and the diagnostics the server publishes. Enough for references, rename, hover, definition and diagnostics;
/// documents are opened on first use with their text from disk and kept in sync by the caller.
/// </summary>
internal sealed class LspClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Stream _input;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly ConcurrentDictionary<string, (int Version, JsonArray Items)> _diagnostics = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _open = new(StringComparer.Ordinal);
    private readonly Action<string> _log;
    private readonly Task _reader;
    private int _nextId;

    /// <summary>Raised with the document URI each time the server publishes diagnostics for it.</summary>
    public event Action<string>? DiagnosticsPublished;

    private LspClient(Process process, Action<string> log)
    {
        _process = process;
        _input = process.StandardInput.BaseStream;
        _log = log;
        _reader = Task.Run(ReadLoopAsync);
    }

    public string Name { get; private set; } = "";

    public static async Task<LspClient> StartAsync(IReadOnlyList<string> command, string display, string root, JsonObject? initializationOptions, Action<string> log, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(command[0], command.Skip(1))
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        var process = Process.Start(info) ?? throw new InvalidOperationException($"could not start {command[0]}");
        process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) log($"lsp: {line}"); };
        process.BeginErrorReadLine();
        var client = new LspClient(process, log);
        var capabilities = new JsonObject
        {
            ["textDocument"] = new JsonObject
            {
                ["synchronization"] = new JsonObject { ["didSave"] = true },
                ["references"] = new JsonObject(),
                ["rename"] = new JsonObject { ["prepareSupport"] = false },
                ["hover"] = new JsonObject { ["contentFormat"] = new JsonArray("plaintext", "markdown") },
                ["definition"] = new JsonObject(),
                ["publishDiagnostics"] = new JsonObject { ["versionSupport"] = true },
                ["callHierarchy"] = new JsonObject(),
            },
            ["workspace"] = new JsonObject { ["workspaceEdit"] = new JsonObject { ["documentChanges"] = true } },
        };
        var result = await client.RequestAsync("initialize", new JsonObject
        {
            ["processId"] = Environment.ProcessId,
            ["rootUri"] = UriOf(root),
            ["workspaceFolders"] = new JsonArray(new JsonObject { ["uri"] = UriOf(root), ["name"] = Path.GetFileName(root) }),
            ["capabilities"] = capabilities,
            ["initializationOptions"] = initializationOptions,
        }, cancellationToken, TimeSpan.FromSeconds(60));
        client.Name = result?["serverInfo"]?["name"]?.GetValue<string>() ?? display;
        await client.NotifyAsync("initialized", new JsonObject(), cancellationToken);
        return client;
    }

    public static string UriOf(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    public static string PathOf(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? Path.GetFullPath(parsed.LocalPath) : uri;

    public bool HasExited => _process.HasExited;

    public async Task<JsonNode?> RequestAsync(string method, JsonNode? parameters, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }, cancellationToken);
        using var timer = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        await using (linked.Token.Register(() => completion.TrySetCanceled()))
        {
            try
            {
                return await completion.Task;
            }
            catch (TaskCanceledException) when (timer.IsCancellationRequested)
            {
                throw new TimeoutException($"{Name}: {method} did not answer in time");
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }
    }

    public Task NotifyAsync(string method, JsonNode? parameters, CancellationToken cancellationToken) =>
        SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters }, cancellationToken);

    /// <summary>Opens a document with its text from disk the first time; later calls do nothing.</summary>
    public async Task EnsureOpenAsync(string path, string languageId, CancellationToken cancellationToken)
    {
        var uri = UriOf(path);
        if (_open.ContainsKey(uri))
        {
            return;
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken);
        if (!_open.TryAdd(uri, 1))
        {
            return;
        }

        await NotifyAsync("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = languageId, ["version"] = 1, ["text"] = text },
        }, cancellationToken);
    }

    /// <summary>Replaces a document's whole text in the server (not on disk); returns the new version.</summary>
    public async Task<int> ChangeAsync(string path, string languageId, string text, CancellationToken cancellationToken)
    {
        await EnsureOpenAsync(path, languageId, cancellationToken);
        var uri = UriOf(path);
        var version = _open.AddOrUpdate(uri, 2, (_, v) => v + 1);
        await NotifyAsync("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = version },
            ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = text }),
        }, cancellationToken);
        return version;
    }

    /// <summary>Re-reads an open document from disk after a save, so the server's view matches the graph's.</summary>
    public async Task RefreshAsync(string path, string languageId, CancellationToken cancellationToken)
    {
        if (_open.ContainsKey(UriOf(path)) && File.Exists(path))
        {
            await ChangeAsync(path, languageId, await File.ReadAllTextAsync(path, cancellationToken), cancellationToken);
        }
    }

    public IReadOnlyCollection<string> OpenDocuments => _open.Keys.ToList();

    public JsonArray? DiagnosticsFor(string uri) => _diagnostics.TryGetValue(uri, out var entry) ? entry.Items : null;

    public IEnumerable<(string Uri, JsonArray Items)> AllDiagnostics() => _diagnostics.Select(d => (d.Key, d.Value.Items));

    /// <summary>
    /// Waits until the server has published diagnostics for <paramref name="uri"/> at <paramref name="version"/> or
    /// later (or, for servers that send no version, publishes anything after the call), up to <paramref name="timeout"/>.
    /// </summary>
    public async Task<JsonArray> WaitForDiagnosticsAsync(string uri, int? version, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPublished(string published_uri)
        {
            if (published_uri == uri && (version is null || _diagnostics.TryGetValue(uri, out var entry) && (entry.Version == 0 || entry.Version >= version)))
            {
                published.TrySetResult();
            }
        }

        DiagnosticsPublished += OnPublished;
        try
        {
            // Already published: at this version or later, or (with no version asked for) at all.
            if (_diagnostics.TryGetValue(uri, out var current) && (version is null || current.Version >= version))
            {
                return current.Items;
            }

            await Task.WhenAny(published.Task, Task.Delay(timeout, cancellationToken));
            return DiagnosticsFor(uri) ?? [];
        }
        finally
        {
            DiagnosticsPublished -= OnPublished;
        }
    }

    /// <summary>
    /// Arms a wait for the next diagnostics the server publishes for <paramref name="uri"/>, from now on: for a
    /// file that is not being edited but whose errors an edit elsewhere can change. Await it after the edit.
    /// </summary>
    public Task<JsonArray> NextDiagnosticsAsync(string uri, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPublished(string publishedUri)
        {
            if (publishedUri == uri)
            {
                published.TrySetResult();
            }
        }

        DiagnosticsPublished += OnPublished;
        return Wait();

        async Task<JsonArray> Wait()
        {
            try
            {
                await Task.WhenAny(published.Task, Task.Delay(timeout, cancellationToken));
                return DiagnosticsFor(uri) ?? [];
            }
            finally
            {
                DiagnosticsPublished -= OnPublished;
            }
        }
    }

    private async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _input.WriteAsync(header, cancellationToken);
            await _input.WriteAsync(body, cancellationToken);
            await _input.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        var stream = _process.StandardOutput.BaseStream;
        try
        {
            while (true)
            {
                var length = -1;
                while (true)
                {
                    var line = await ReadHeaderLineAsync(stream);
                    if (line is null)
                    {
                        return;
                    }

                    if (line.Length == 0)
                    {
                        break;
                    }

                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        length = int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    }
                }

                if (length < 0)
                {
                    continue;
                }

                var body = new byte[length];
                await stream.ReadExactlyAsync(body);
                Dispatch(JsonNode.Parse(body));
            }
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or ObjectDisposedException or JsonException)
        {
            _log($"lsp: connection closed ({e.Message})");
        }
        finally
        {
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(new IOException($"{Name} exited"));
            }
        }
    }

    private static async Task<string?> ReadHeaderLineAsync(Stream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one) == 0)
            {
                return null;
            }

            if (one[0] == '\n')
            {
                return Encoding.ASCII.GetString([.. bytes]).TrimEnd('\r');
            }

            bytes.Add(one[0]);
        }
    }

    private void Dispatch(JsonNode? message)
    {
        if (message is not JsonObject json)
        {
            return;
        }

        if (json["id"] is { } idNode && json["method"] is null)
        {
            if (idNode.GetValueKind() == JsonValueKind.Number && _pending.TryGetValue(idNode.GetValue<int>(), out var completion))
            {
                if (json["error"] is { } error)
                {
                    completion.TrySetException(new InvalidOperationException($"{Name}: {error["message"]?.GetValue<string>() ?? error.ToJsonString()}"));
                }
                else
                {
                    completion.TrySetResult(json["result"]?.DeepClone());
                }
            }

            return;
        }

        var method = json["method"]?.GetValue<string>();
        if (method == "textDocument/publishDiagnostics" && json["params"] is JsonObject parameters && parameters["uri"]?.GetValue<string>() is { } uri)
        {
            var version = parameters["version"]?.GetValueKind() == JsonValueKind.Number ? parameters["version"]!.GetValue<int>() : 0;
            _diagnostics[uri] = (version, parameters["diagnostics"]?.DeepClone() as JsonArray ?? []);
            DiagnosticsPublished?.Invoke(uri);
        }
        else if (json["id"] is { } requestId && method is not null)
        {
            // A request from the server (workspace/configuration, window/workDoneProgress/create, ...): answer null
            // so it never waits on us.
            // workspace/configuration wants one value per item asked for; null means "no setting".
            var items = json["params"]?["items"] as JsonArray;
            JsonNode? result = method == "workspace/configuration" ? new JsonArray(Enumerable.Range(0, items?.Count ?? 0).Select(_ => (JsonNode?)null).ToArray()) : null;
            _ = SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = requestId.DeepClone(), ["result"] = result }, CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await RequestAsync("shutdown", null, timeout.Token, TimeSpan.FromSeconds(3));
                await NotifyAsync("exit", null, CancellationToken.None);
                await _process.WaitForExitAsync(timeout.Token);
            }
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException or InvalidOperationException)
        {
        }
        finally
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }

            _process.Dispose();
        }

        await _reader.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { }, TaskScheduler.Default);
    }
}
