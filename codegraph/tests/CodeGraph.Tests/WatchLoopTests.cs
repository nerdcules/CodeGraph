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
