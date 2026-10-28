using CodeGraph.Core;

namespace CodeGraph.Tests;

public sealed class IndexFreshnessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"));
    private readonly string _db;
    private readonly string _source;

    public IndexFreshnessTests()
    {
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "graph.db");
        _source = Path.Combine(_dir, "ChainRunner.cs");
        File.WriteAllText(_source, "class ChainRunner { }");

        using var store = new GraphStore(_db);
        store.Replace(full: true, [], [], [], [], [FileStamp.Of(_source)],
            new Dictionary<string, string> { [MetaKeys.Solution] = "/repo/App.sln" });
    }

    public void Dispose() => TempDb.TryDelete(_dir);

    [Fact]
    public void Is_fresh_when_nothing_changed()
    {
        var report = new IndexFreshness(_db, TimeSpan.Zero).Check();

        Assert.False(report.IsStale);
        Assert.Null(report.Warning());
    }

    [Fact]
    public void Reports_an_edited_file_by_name_and_marks_it_dirty()
    {
        File.WriteAllText(_source, "class ChainRunner { void Run() { } }");

        var report = new IndexFreshness(_db, TimeSpan.Zero).Check();

        Assert.True(report.IsDirty(_source));
        Assert.Contains("ChainRunner.cs", report.Warning());
        Assert.Contains("codegraph-index \"/repo/App.sln\"", report.Warning());
    }

    [Fact]
    public void A_touch_without_a_content_change_is_not_stale()
    {
        File.SetLastWriteTimeUtc(_source, DateTime.UtcNow.AddMinutes(5));

        Assert.False(new IndexFreshness(_db, TimeSpan.Zero).Check().IsStale);
    }

    [Fact]
    public void Reports_a_deleted_file()
    {
        File.Delete(_source);

        var report = new IndexFreshness(_db, TimeSpan.Zero).Check();

        Assert.Contains(_source, report.MissingFiles);
        Assert.Contains("deleted", report.Warning());
    }

    [Fact]
    public void Flags_a_moved_HEAD()
    {
        var report = new FreshnessReport(
            new Dictionary<string, string> { [MetaKeys.Commit] = "aaaaaaaaaa" }, Head: "bbbbbbbbbb",
            new HashSet<string>(), new HashSet<string>());

        Assert.True(report.HeadMoved);
        Assert.Contains("HEAD is bbbbbbb but the graph was built at aaaaaaa", report.Warning());
    }
}
