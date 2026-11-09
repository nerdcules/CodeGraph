using System.Diagnostics;
using CodeGraph.Core;
using CodeGraph.Core.Live;

namespace CodeGraph.Mcp;

/// <summary>
/// Starts <c>codegraph-index --watch</c> for this graph when asked to. The indexer is looked for next to this
/// executable (an install), in the sibling project's build output (a source checkout run with <c>dotnet run</c>),
/// or at <c>CODEGRAPH_INDEXER</c>. The child's log goes to a file beside the graph, and none of its standard
/// streams touch this process's stdio, which carries the MCP protocol.
/// </summary>
public static class LiveLauncher
{
    public static string? FindIndexer()
    {
        var exe = OperatingSystem.IsWindows() ? "codegraph-index.exe" : "codegraph-index";
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("CODEGRAPH_INDEXER"),
            Path.Combine(AppContext.BaseDirectory, exe),
            Path.Combine(AppContext.BaseDirectory.Replace("CodeGraph.Mcp", "CodeGraph.Indexer", StringComparison.Ordinal), exe),
        };
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(c));
    }

    /// <summary>The command a person would run by hand, for the tools' "no daemon" answers.</summary>
    public static string Command(string? solution) =>
        $"codegraph-index \"{solution ?? "<solution>"}\" --watch";

    public static string LogPath(string dbPath) => Path.Combine(Path.GetDirectoryName(dbPath)!, "daemon.log");

    /// <summary>Spawns the daemon. Returns a message for the model: started, or why not.</summary>
    public static string Start(string dbPath, string? solution)
    {
        if (solution is null || !File.Exists(solution))
        {
            return $"cannot start a daemon: the graph does not record a solution path. Run by hand: {Command(solution)}";
        }

        var indexer = FindIndexer();
        if (indexer is null)
        {
            return $"cannot find codegraph-index next to this server (set CODEGRAPH_INDEXER). Run by hand: {Command(solution)}";
        }

        var log = LogPath(dbPath);
        var info = new ProcessStartInfo(indexer)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(solution)!,
        };
        info.ArgumentList.Add(solution);
        info.ArgumentList.Add("--db");
        info.ArgumentList.Add(dbPath);
        info.ArgumentList.Add("--watch");
        info.ArgumentList.Add("--log");
        info.ArgumentList.Add(log);

        try
        {
            var process = Process.Start(info) ?? throw new InvalidOperationException("Process.Start returned null");
            process.StandardInput.Close();
            // With --log the child writes nothing to these, but drain them anyway so it can never block on a full pipe.
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return $"started codegraph-index --watch (pid {process.Id}); it loads the solution first, which takes seconds to a minute. Log: {log}. Call live_status again to see when it is ready.";
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"could not start {indexer}: {e.Message}. Run by hand: {Command(solution)}";
        }
    }
}
