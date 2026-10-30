using System.Diagnostics;

namespace CodeGraph.Indexer;

/// <summary>
/// One status line on stderr that updates in place: a spinner, the current phase, a detail and the phase's
/// elapsed time, e.g. <c>/ indexing: [12/33] App.Core: 120/340 files (3s)</c>. It redraws on a timer so a slow
/// phase (an MSBuild load, a big compilation) still visibly ticks. Log lines are written above it and it is
/// redrawn below them. When stderr is redirected (a pipe, a test runner, an MCP client's log capture) nothing is
/// drawn and only the log lines go out.
/// </summary>
internal static class ConsoleStatus
{
    private const string Frames = @"-\|/";
    private static readonly Lock Sync = new();
    private static readonly Stopwatch PhaseClock = new();
    private static readonly bool Enabled = !Console.IsErrorRedirected;
    private static Timer? _timer;
    private static string _phase = "";
    private static string _detail = "";
    private static int _frame;
    private static int _drawnWidth;

    /// <summary>Starts a new phase; its clock restarts. <paramref name="detail"/> replaces the previous one.</summary>
    public static void Phase(string phase, string detail = "")
    {
        lock (Sync)
        {
            if (!string.Equals(phase, _phase, StringComparison.Ordinal))
            {
                PhaseClock.Restart();
            }

            _phase = phase;
            _detail = detail;
            Draw();
            Start();
        }
    }

    /// <summary>Updates the detail within the current phase; the clock keeps running.</summary>
    public static void Detail(string detail)
    {
        lock (Sync)
        {
            _detail = detail;
            Draw();
        }
    }

    /// <summary>Writes a log line above the status line and redraws the status line below it.</summary>
    public static void Log(string message)
    {
        lock (Sync)
        {
            Erase();
            Console.Error.WriteLine(message);
            Draw();
        }
    }

    /// <summary>Removes the status line, between runs and before exit.</summary>
    public static void Clear()
    {
        lock (Sync)
        {
            _timer?.Dispose();
            _timer = null;
            _phase = "";
            _detail = "";
            Erase();
        }
    }

    private static void Start()
    {
        if (Enabled && _timer is null)
        {
            _timer = new Timer(_ => Tick(), null, 100, 100);
        }
    }

    private static void Tick()
    {
        lock (Sync)
        {
            if (_timer is null)
            {
                return;
            }

            _frame = (_frame + 1) % Frames.Length;
            Draw();
        }
    }

    private static void Draw()
    {
        if (!Enabled || _phase.Length == 0)
        {
            return;
        }

        var line = Line(Frames[_frame], _phase, _detail, (int)PhaseClock.Elapsed.TotalSeconds, Width());

        // Pad over whatever the previous draw left behind so a shorter line does not leave tail characters.
        var pad = Math.Max(0, _drawnWidth - line.Length);
        Console.Error.Write($"\r{line}{new string(' ', pad)}");
        if (pad > 0)
        {
            Console.Error.Write($"\r{line}");
        }

        _drawnWidth = line.Length;
    }

    /// <summary>
    /// The text of the status line: <c>/ phase: detail (12s)</c>, cut to fit a console <paramref name="width"/>
    /// columns wide (0 for unknown) so it never wraps, which would leave a trail of stale lines.
    /// </summary>
    internal static string Line(char frame, string phase, string detail, int seconds, int width)
    {
        var line = detail.Length == 0 ? $"{frame} {phase} ({seconds}s)" : $"{frame} {phase}: {detail} ({seconds}s)";
        if (width > 0 && line.Length >= width)
        {
            line = width <= 1 ? "" : string.Concat(line.AsSpan(0, width - 2), "…");
        }

        return line;
    }

    private static void Erase()
    {
        if (!Enabled || _drawnWidth == 0)
        {
            return;
        }

        Console.Error.Write($"\r{new string(' ', _drawnWidth)}\r");
        _drawnWidth = 0;
    }

    private static int Width()
    {
        try
        {
            return Console.WindowWidth;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
