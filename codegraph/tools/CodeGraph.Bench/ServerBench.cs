using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Core;

/// <summary>
/// Times tool calls the way a model makes them: a codegraph-mcp server over stdio, one JSON-RPC request at a time,
/// measured from request written to answer read. Rows: the first call after start, calls back to back, and calls two
/// seconds apart (a model's pace, which is longer than the anchor's idle release). Each server argument is a
/// <c>codegraph-mcp.dll</c>, optionally labelled (<c>before=path</c>) and followed by <c>;NAME=value</c> environment
/// settings, for example <c>off=codegraph-mcp.dll;CODEGRAPH_SNAPSHOT=off</c> to time the same build without the snapshot.
/// </summary>
internal static class ServerBench
{
    public static async Task RunAsync(string dbPath, IReadOnlyList<string> servers, int runs)
    {
        string hot;
        using (var store = GraphStore.Open(dbPath, readOnly: true, TimeSpan.FromSeconds(5)))
        {
            hot = store.MostCalled(1, 1, 0).First().Method.Id;
        }

        var calls = new (string Label, string Tool, JsonObject Arguments)[]
        {
            ("graph_stats", "graph_stats", []),
            ("find_symbol 'Dispose'", "find_symbol", new JsonObject { ["query"] = "Dispose" }),
            ("callers_of depth 1 (most-called)", "callers_of", new JsonObject { ["id"] = hot, ["depth"] = 1 }),
            ("impact_of depth 5", "impact_of", new JsonObject { ["id"] = hot, ["depth"] = 5 }),
            ("impact_of depth 20", "impact_of", new JsonObject { ["id"] = hot, ["depth"] = 20 }),
            ("covered_by depth 30", "covered_by", new JsonObject { ["id"] = hot, ["depth"] = 30 }),
        };

        var table = new List<(string Label, List<string> Values)>();
        foreach (var (label, _, _) in calls)
        {
            table.Add(($"{label}, back to back", []));
            table.Add(($"{label}, 2 s apart", []));
        }

        var first = new List<string>();
        foreach (var server in servers)
        {
            await using var client = await Client.StartAsync(server, dbPath);
            var watch = Stopwatch.StartNew();
            await client.CallAsync("find_symbol", new JsonObject { ["query"] = "Dispose" });
            first.Add(Ms(watch.Elapsed.TotalMilliseconds));

            // Let a background snapshot load land, as it would between a session's start and its first question.
            await Task.Delay(TimeSpan.FromSeconds(5));
            for (var c = 0; c < calls.Length; c++)
            {
                var (_, tool, arguments) = calls[c];
                table[2 * c].Values.Add(Ms(await MedianAsync(() => client.CallAsync(tool, arguments), runs, TimeSpan.Zero)));
                table[2 * c + 1].Values.Add(Ms(await MedianAsync(() => client.CallAsync(tool, arguments), Math.Min(runs, 5), TimeSpan.FromSeconds(2))));
            }
        }

        Console.WriteLine($"| tool call | {string.Join(" | ", servers.Select(Name))} |");
        Console.WriteLine($"|---|{string.Concat(servers.Select(_ => "---|"))}");
        Console.WriteLine($"| first call after start (find_symbol) | {string.Join(" | ", first)} |");
        foreach (var (label, values) in table)
        {
            Console.WriteLine($"| {label} | {string.Join(" | ", values)} |");
        }
    }

    private static (string Label, string Path, string[] Settings) Parse(string server)
    {
        var parts = server.Split(';');
        var eq = parts[0].IndexOf('=');
        var labelled = eq > 0 && parts[0][..eq].IndexOfAny(['/', '\\']) < 0;
        var path = labelled ? parts[0][(eq + 1)..] : parts[0];
        return (labelled ? parts[0][..eq] : System.IO.Path.GetFileName(path), System.IO.Path.GetFullPath(path), parts[1..]);
    }

    private static string Name(string server)
    {
        var (label, _, settings) = Parse(server);
        return settings.Length > 0 ? $"{label} ({string.Join(", ", settings)})" : label;
    }

    private static async Task<double> MedianAsync(Func<Task> call, int count, TimeSpan pause)
    {
        await call(); // warm-up
        var samples = new List<double>();
        for (var i = 0; i < count; i++)
        {
            if (pause > TimeSpan.Zero)
            {
                await Task.Delay(pause);
            }

            var watch = Stopwatch.StartNew();
            await call();
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        return samples[samples.Count / 2];
    }

    private static string Ms(double ms) => ms < 1 ? $"{ms:F2} ms" : ms < 10 ? $"{ms:F1} ms" : $"{ms:F0} ms";

    private sealed class Client : IAsyncDisposable
    {
        private readonly Process _process;
        private int _id;

        private Client(Process process) => _process = process;

        public static async Task<Client> StartAsync(string server, string dbPath)
        {
            var (_, path, settings) = Parse(server);
            var info = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.ArgumentList.Add(path);
            info.ArgumentList.Add(dbPath);
            info.Environment["CODEGRAPH_TELEMETRY"] = "off"; // a benchmark is not usage
            foreach (var setting in settings)
            {
                var eq = setting.IndexOf('=');
                info.Environment[setting[..eq]] = setting[(eq + 1)..];
            }

            var process = Process.Start(info)!;
            _ = process.StandardError.ReadToEndAsync(); // drain the log
            var client = new Client(process);
            await client.RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "codegraph-bench", ["version"] = "1" },
            });
            await client.SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
            return client;
        }

        public async Task CallAsync(string tool, JsonObject arguments)
        {
            var result = await RequestAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments.DeepClone() });
            if (result?["isError"]?.GetValue<bool>() == true)
            {
                throw new InvalidOperationException($"{tool}: {result.ToJsonString()}");
            }
        }

        private async Task<JsonNode?> RequestAsync(string method, JsonObject parameters)
        {
            var id = ++_id;
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                var message = JsonNode.Parse(line);
                if (message?["id"]?.GetValue<int>() == id)
                {
                    return message["error"] is { } error ? throw new InvalidOperationException(error.ToJsonString()) : message["result"];
                }
            }

            throw new InvalidOperationException("the server closed its output");
        }

        private async Task SendAsync(JsonObject message)
        {
            await _process.StandardInput.WriteLineAsync(message.ToJsonString(JsonSerializerOptions.Default));
            await _process.StandardInput.FlushAsync();
        }

        public async ValueTask DisposeAsync()
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(5000))
            {
                _process.Kill(entireProcessTree: true);
            }

            await _process.WaitForExitAsync();
            _process.Dispose();
        }
    }
}
