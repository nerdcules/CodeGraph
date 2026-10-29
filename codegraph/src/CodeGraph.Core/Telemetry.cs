using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeGraph.Core;

/// <summary>One tool call as the server saw it. Token figures are estimates at four characters per token.</summary>
public sealed record ToolCallRecord(
    [property: JsonPropertyName("at")] DateTime At,
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("ms")] long Milliseconds,
    [property: JsonPropertyName("result_chars")] long ResultChars,
    [property: JsonPropertyName("source_chars")] long SourceChars,
    [property: JsonPropertyName("error")] bool IsError)
{
    [JsonIgnore]
    public long ResultTokens => Tokens(ResultChars);

    /// <summary>What reading the files the answer points at would have cost instead.</summary>
    [JsonIgnore]
    public long SourceTokens => Tokens(SourceChars);

    [JsonIgnore]
    public long TokensSaved => Math.Max(0, SourceTokens - ResultTokens);

    public static long Tokens(long chars) => (chars + 3) / 4;
}

/// <summary>Per-tool totals over a set of calls.</summary>
public sealed record ToolUsage(string Tool, int Calls, int Errors, double AverageMilliseconds, long ResultTokens, long SourceTokens, long TokensSaved)
{
    public static ToolUsage Of(string tool, IReadOnlyCollection<ToolCallRecord> calls) => new(
        tool,
        calls.Count,
        calls.Count(c => c.IsError),
        calls.Count == 0 ? 0 : calls.Average(c => c.Milliseconds),
        calls.Sum(c => c.ResultTokens),
        calls.Sum(c => c.SourceTokens),
        calls.Sum(c => c.TokensSaved));

    public static IReadOnlyList<ToolUsage> Group(IEnumerable<ToolCallRecord> calls) => calls
        .GroupBy(c => c.Tool, StringComparer.Ordinal)
        .Select(g => Of(g.Key, g.ToList()))
        .OrderByDescending(u => u.Calls)
        .ThenBy(u => u.Tool, StringComparer.Ordinal)
        .ToList();
}

/// <summary>
/// The feedback loop: how often each tool is called, how long it takes, how much it returns, and how much source
/// it saved the model from reading. Everything stays on this machine: calls are kept in memory for the session and
/// appended as JSON lines to <see cref="LogPath"/> (null disables the file) so <c>usage_stats</c> can report all time.
/// </summary>
public sealed class Telemetry(string? logPath)
{
    private static readonly AsyncLocal<ToolCallScope?> CurrentScope = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private readonly Lock _lock = new();
    private readonly List<ToolCallRecord> _session = [];

    /// <summary>The scope of the tool call running on this async flow, so a tool can report the source it stood in for.</summary>
    public static ToolCallScope? Current => CurrentScope.Value;

    public string? LogPath => logPath;

    /// <summary>Calls recorded since this server started.</summary>
    public IReadOnlyList<ToolCallRecord> Session
    {
        get
        {
            lock (_lock)
            {
                return [.. _session];
            }
        }
    }

    /// <summary>Starts timing a call. Dispose the scope, or call <see cref="ToolCallScope.Complete"/>, to record it.</summary>
    public ToolCallScope Begin(string tool)
    {
        var scope = new ToolCallScope(this, tool);
        CurrentScope.Value = scope;
        return scope;
    }

    /// <summary>Every call in the log file, oldest first; empty when there is no file or it is unreadable.</summary>
    public IReadOnlyList<ToolCallRecord> AllTime()
    {
        if (logPath is null || !File.Exists(logPath))
        {
            return [];
        }

        var records = new List<ToolCallRecord>();
        try
        {
            foreach (var line in File.ReadLines(logPath))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                try
                {
                    if (JsonSerializer.Deserialize<ToolCallRecord>(line, JsonOptions) is { } record)
                    {
                        records.Add(record);
                    }
                }
                catch (JsonException)
                {
                    // A torn line from a crash mid-append; skip it.
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return records;
    }

    internal void Record(ToolCallRecord record)
    {
        lock (_lock)
        {
            _session.Add(record);
            if (CurrentScope.Value is { } scope && scope.Tool == record.Tool)
            {
                CurrentScope.Value = null;
            }

            if (logPath is null)
            {
                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(logPath, JsonSerializer.Serialize(record, JsonOptions) + "\n");
            }
            catch (IOException)
            {
                // Telemetry never fails a tool call.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>A human summary of per-tool totals, one line per tool.</summary>
    public static string Table(IReadOnlyList<ToolUsage> usage)
    {
        if (usage.Count == 0)
        {
            return "(no calls)";
        }

        var width = Math.Max(4, usage.Max(u => u.Tool.Length));
        var lines = new List<string>
        {
            $"{"tool".PadRight(width)}  calls  errors   avg ms  result tok  source tok   saved tok",
        };
        foreach (var u in usage)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{u.Tool.PadRight(width)}  {u.Calls,5}  {u.Errors,6}  {u.AverageMilliseconds,7:F0}  {u.ResultTokens,10}  {u.SourceTokens,10}  {u.TokensSaved,10}"));
        }

        return string.Join('\n', lines);
    }
}

/// <summary>A tool call in progress. Tools add the size of the source they answered instead of.</summary>
public sealed class ToolCallScope : IDisposable
{
    private readonly Telemetry _telemetry;
    private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private long _sourceChars;
    private bool _completed;

    internal ToolCallScope(Telemetry telemetry, string tool)
    {
        _telemetry = telemetry;
        Tool = tool;
    }

    public string Tool { get; }

    public long SourceChars => _sourceChars;

    /// <summary>Counts a file the model would otherwise have opened, once per call however many rows point at it.</summary>
    public void AddSourceFile(string? path)
    {
        if (path is null || !_files.Add(path))
        {
            return;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                Interlocked.Add(ref _sourceChars, info.Length);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Complete(long resultChars, bool isError)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _telemetry.Record(new ToolCallRecord(DateTime.UtcNow, Tool, _stopwatch.ElapsedMilliseconds, resultChars, _sourceChars, isError));
    }

    public void Dispose() => Complete(0, isError: false);
}
