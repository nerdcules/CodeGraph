using System.Collections.Concurrent;
using CodeGraph.Core.Live;
using CodeGraph.Indexer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace CodeGraph.Tests;

/// <summary>
/// The daemon's loop without MSBuild, a file watcher or a database: loads and index passes are recorded, file
/// reads come from a dictionary, and the debounce is short. These pin down when a pass runs, what it sees, and
/// when a flush may return.
/// </summary>
public sealed class WatchLoopTests : IAsyncDisposable
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "codegraph-watch");
    private static readonly string A = Path.Combine(Root, "A.cs");

    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, string?> _disk = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<(string Text, bool Full)> _passes = new();
    private int _loads;
    private int _failNext;
    private TaskCompletionSource? _loadGate;
    private Task? _running;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_running is not null)
        {
            try
            {
                await _running;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task Loads_and_indexes_once_then_reports_ready_and_idle()
    {
        var loop = Start();
        await loop.Ready.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(1, _loads);
        Assert.Equal([("class A { }", false)], _passes);
        var status = loop.Status();
        Assert.False(status.Loading);
        Assert.Equal((0, false), (status.Pending, status.Indexing));
        Assert.NotNull(status.LastIndexedAt);
    }

    [Fact]
    public async Task A_save_is_applied_in_memory_and_a_flush_waits_for_its_pass()
    {
        var loop = Start(debounce: TimeSpan.FromSeconds(30)); // long: only the flush can make this fast
        await loop.Ready.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        _disk[A] = "class A { void M() { } }";
        loop.Enqueue(A);
        Assert.Equal(1, loop.Status().Pending);

        var status = await loop.FlushAsync(TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(0, status.Pending);
        Assert.Equal(1, _loads); // no reload for a source edit
        Assert.Equal("class A { void M() { } }", _passes.Last().Text);
        Assert.Equal("class A { void M() { } }", (await loop.Current!.GetDocument(loop.Current.GetDocumentIdsWithFilePath(A)[0])!.GetTextAsync(TestContext.Current.CancellationToken)).ToString());
    }

    [Fact]
    public async Task A_flush_with_nothing_pending_returns_at_once()
    {
        var loop = Start();
        await loop.Ready.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        var flush = loop.FlushAsync(TestContext.Current.CancellationToken);

        Assert.True(flush.IsCompleted);
    }

    [Fact]
    public async Task A_flush_during_the_first_load_returns_at_once_and_says_loading()
    {
        _loadGate = new TaskCompletionSource();
        var loop = Start();

        var flush = loop.FlushAsync(TestContext.Current.CancellationToken);

        Assert.True(flush.IsCompleted, "a flush must not wait out a solution load");
        Assert.True((await flush).Loading);
        _loadGate.SetResult();
        await loop.Ready.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.False(loop.Status().Loading);
    }

    [Fact]
    public async Task A_project_file_change_reloads_the_solution()
    {
        var loop = Start(debounce: TimeSpan.FromMilliseconds(10));
        await loop.Ready.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        loop.Enqueue(Path.Combine(Root, "Demo.csproj"));
        await loop.FlushAsync(TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(2, _loads);
    }

    [Fact]
    public async Task Lost_watcher_events_reload_the_solution()
    {
        var loop = Start(debounce: TimeSpan.FromMilliseconds(10));
        await loop.Ready.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        loop.LostEvents();
        await loop.FlushAsync(TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(2, _loads);
    }

    [Fact]
    public async Task A_failed_pass_is_retried_and_the_flush_waits_for_the_retry()
    {
        var loop = Start(debounce: TimeSpan.FromMilliseconds(10));
        await loop.Ready.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        _failNext = 1;
        _disk[A] = "class A { int F; }";
        loop.Enqueue(A);
        await loop.FlushAsync(TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal("class A { int F; }", _passes.Last().Text);
        Assert.Equal(0, _failNext);
    }

    [Fact]
    public async Task Files_that_are_neither_source_nor_project_are_ignored()
    {
        var loop = Start();
        await loop.Ready.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        loop.Enqueue(Path.Combine(Root, "README.md"));
        loop.Enqueue(Path.Combine(Root, "obj", "Gen.cs"));

        Assert.Equal(0, loop.Status().Pending);
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private WatchLoop Start(TimeSpan? debounce = null)
    {
        _disk[A] = "class A { }";
        var loop = new WatchLoop(Load, Index, path => _disk.GetValueOrDefault(path), _ => { }, Path.Combine(Root, "Demo.sln"), Path.Combine(Root, "graph.db"), debounce ?? TimeSpan.FromMilliseconds(10));
        _running = loop.RunAsync(forceFull: false, _stop.Token);
        return loop;
    }

    private async Task<Solution> Load(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _loads);
        if (_loadGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        workspace.AddProject(ProjectInfo.Create(
            projectId, VersionStamp.Default, "Demo", "Demo", LanguageNames.CSharp,
            filePath: Path.Combine(Root, "Demo.csproj"),
            documents:
            [
                DocumentInfo.Create(DocumentId.CreateNewId(projectId), "A.cs",
                    loader: TextLoader.From(TextAndVersion.Create(SourceText.From(_disk[A] ?? ""), VersionStamp.Default)), filePath: A),
            ]));
        return workspace.CurrentSolution;
    }

    private async Task Index(Solution solution, bool full, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _failNext, 0) == 1)
        {
            throw new IOException("database is locked");
        }

        var document = solution.GetDocument(solution.GetDocumentIdsWithFilePath(A)[0])!;
        _passes.Enqueue(((await document.GetTextAsync(cancellationToken)).ToString(), full));
    }
}

public sealed class DaemonLifecycleTests
{
    [Fact]
    public async Task Claim_detects_a_running_daemon_and_frees_a_name_nobody_uses()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"), "graph.db");
        var client = new LiveClient(dbPath);
        Assert.Null(await client.ClaimAsync(cancellationToken: TestContext.Current.CancellationToken));

        using var stop = new CancellationTokenSource();
        var serving = new LiveServer(LivePipe.NameFor(dbPath), (_, _) =>
            Task.FromResult(LiveResponse.Ok(new LiveStatus(4242, "s", "g", 0, 0, DateTime.UnixEpoch, null, null, 0, false)))).RunAsync(stop.Token);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        var running = await client.ClaimAsync(cancellationToken: TestContext.Current.CancellationToken);
        stop.Cancel();
        await serving;

        Assert.Equal(4242, running?.Pid);
    }

    [Fact]
    public async Task Claim_removes_a_socket_left_by_a_killed_daemon()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"), "graph.db");
        var socket = LivePipe.SocketPath(LivePipe.NameFor(dbPath));
        // Bound and never listening: a file that refuses connections, which is what a kill -9 leaves. It stays
        // open for the test, because .NET unlinks a bound socket's file when the socket is disposed.
        using var orphan = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
        orphan.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(socket));
        Assert.True(File.Exists(socket));

        var log = new List<string>();
        Assert.Null(await new LiveClient(dbPath).ClaimAsync(log.Add, TestContext.Current.CancellationToken));

        Assert.False(File.Exists(socket));
        Assert.Contains(log, l => l.Contains("stale socket"));
    }

    [Fact]
    public void Idle_means_quiet_for_the_whole_window_and_never_while_loading_or_without_a_limit()
    {
        var loop = new WatchLoop(_ => Task.FromResult<Solution>(null!), (_, _, _) => Task.CompletedTask, _ => null, _ => { }, "s.sln", "g.db")
        {
            IdleExit = TimeSpan.FromMinutes(5),
        };
        var noLimit = new WatchLoop(_ => Task.FromResult<Solution>(null!), (_, _, _) => Task.CompletedTask, _ => null, _ => { }, "s.sln", "g.db");

        Assert.False(loop.IsIdle(DateTime.UtcNow.AddHours(1)), "still loading: nothing indexed yet");
        Assert.False(noLimit.IsIdle(DateTime.UtcNow.AddDays(1)));
    }

    [Fact]
    public async Task A_loaded_loop_goes_idle_after_the_window_and_activity_resets_it()
    {
        using var stop = new CancellationTokenSource();
        var workspace = new AdhocWorkspace();
        var loop = new WatchLoop(_ => Task.FromResult(workspace.CurrentSolution), (_, _, _) => Task.CompletedTask, _ => null, _ => { }, "s.sln", "g.db")
        {
            IdleExit = TimeSpan.FromMinutes(5),
        };
        var running = loop.RunAsync(false, stop.Token);
        await loop.Ready.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.False(loop.IsIdle(DateTime.UtcNow));
        Assert.True(loop.IsIdle(DateTime.UtcNow.AddMinutes(6)));
        var status = loop.Status();
        Assert.Equal(5, status.IdleExitMinutes);
        Assert.True(status.WorkingSetBytes > 0);

        loop.Touch();
        Assert.False(loop.IsIdle(DateTime.UtcNow.AddMinutes(4)));

        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }
}
