using System.Collections.Concurrent;
using CodeGraph.Core.Live;
using CodeGraph.Indexer;

namespace CodeGraph.Tests;

/// <summary>
/// The daemon for a language indexed by an external extractor (TypeScript, Python, Java, Go, Rust), with the
/// extractor replaced by a pass that runs until it is cancelled. These pin down that a stop which lands while the
/// first index is still running ends the daemon cleanly instead of escaping as an unhandled exception (an abort
/// and a core dump).
/// </summary>
public sealed class ExternalDaemonTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_cancel_during_the_first_index_ends_the_watch_without_throwing()
    {
        var root = NewFolder();
        try
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var watch = new ReindexWatch(root, new HashSet<string>([".py"]), async token =>
            {
                started.TrySetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, token);
                return 0;
            }, _ => { });
            using var stopping = new CancellationTokenSource();

            var running = watch.RunAsync(stopping.Token);
            await started.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            await stopping.CancelAsync();

            Assert.Equal(-1, await running.WaitAsync(Timeout, TestContext.Current.CancellationToken));
            Assert.False(watch.Indexing);
            Assert.Null(watch.LastIndexedAt);
        }
        finally
        {
            TempDb.TryDelete(root);
        }
    }

    [Fact]
    public async Task A_stop_over_the_pipe_during_the_first_index_stops_the_daemon_cleanly()
    {
        var root = NewFolder();
        var dbPath = Path.Combine(root, ".codegraph", "graph.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var lines = new ConcurrentQueue<string>();
        try
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var daemon = ExternalDaemon.RunAsync(root, dbPath, "python", new HashSet<string>([".py"]), async token =>
            {
                started.TrySetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, token);
                return 0;
            }, server: null, idleExitMinutes: null, lines.Enqueue);
            await started.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            var status = await new LiveClient(dbPath).CallAsync<LiveStatus>("stop", null, TestContext.Current.CancellationToken);
            Assert.Equal(Environment.ProcessId, status.Pid);

            Assert.Equal(0, await daemon.WaitAsync(Timeout, TestContext.Current.CancellationToken));
            Assert.Contains("stop requested over the pipe", lines);
            Assert.Equal("stopped", lines.Last());
        }
        finally
        {
            TempDb.TryDelete(root);
        }
    }

    [Fact]
    public async Task A_cancelled_index_ends_the_indexer_process()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stand-in indexer is a shell command");
        var root = NewFolder();
        var marker = Path.Combine(root, "pyproject.toml");
        File.WriteAllText(marker, "[project]\nname = \"app\"\n");
        var pidFile = Path.Combine(root, "indexer.pid");
        // Stands in for scip-python: records its pid, then runs until it is killed.
        var indexer = new ScipIndexers.Indexer("python", ["pyproject.toml"], "sh",
            (_, _) => ["-c", $"echo $$ > '{pidFile}'; exec sleep 60"], "", [".py"], "");
        try
        {
            using var stopping = new CancellationTokenSource();
            var indexing = ScipIndexers.IndexAsync(indexer, marker, Path.Combine(root, "graph.db"), _ => { }, stopping.Token);
            var pid = await PidAsync(pidFile);
            await stopping.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => indexing.WaitAsync(Timeout, TestContext.Current.CancellationToken));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (Running(pid) && clock.Elapsed < Timeout)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            Assert.False(Running(pid), $"the indexer (pid {pid}) is still running after the cancel");
        }
        finally
        {
            TempDb.TryDelete(root);
        }
    }

    private static async Task<int> PidAsync(string pidFile)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < Timeout)
        {
            if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
            {
                return pid;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"the stand-in indexer did not write {pidFile}");
    }

    private static bool Running(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string NewFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }
}
