using CodeGraph.Core;
using CodeGraph.Core.Live;
using CodeGraph.Mcp;

namespace CodeGraph.Tests;

/// <summary>
/// Paths in answers are relative to the repository root, named once at the top of the answer. The graph stores
/// absolute paths; the root was most of every path in an answer with many rows.
/// </summary>
public sealed class AnswerPathsTests
{
    [Fact]
    public void A_path_under_the_root_loses_the_root_and_the_answer_names_it_once()
    {
        var paths = new AnswerPaths("/repo");

        Assert.Null(paths.Header);
        Assert.Equal("src/App/Service.cs", paths.Relative("/repo/src/App/Service.cs"));
        Assert.True(paths.Shortened);
        Assert.Equal("paths are relative to /repo", paths.Header);
    }

    [Fact]
    public void A_path_outside_the_root_stays_absolute_and_does_not_name_the_root()
    {
        var paths = new AnswerPaths("/repo");

        Assert.Equal("/other/Service.cs", paths.Relative("/other/Service.cs"));
        Assert.Equal("/repository/Service.cs", paths.Relative("/repository/Service.cs"));
        Assert.Equal("/repo", paths.Relative("/repo"));
        Assert.Null(paths.Relative(null));
        Assert.False(paths.Shortened);
        Assert.Null(paths.Header);
    }

    [Fact]
    public void Either_separator_matches_and_a_trailing_separator_on_the_root_is_ignored()
    {
        var paths = new AnswerPaths(@"C:\dev\App\");

        Assert.Equal(@"C:\dev\App", paths.Root);
        Assert.Equal(@"src\Service.cs", paths.Relative(@"C:\dev\App\src\Service.cs"));
        Assert.Equal("src/Service.cs", paths.Relative("C:/dev/App/src/Service.cs"));
    }

    [Fact]
    public void Case_is_ignored_on_Windows_only()
    {
        var paths = new AnswerPaths("/Repo");
        var relative = paths.Relative("/repo/Service.cs");

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("Service.cs", relative);
        }
        else
        {
            Assert.Equal("/repo/Service.cs", relative);
        }
    }

    [Fact]
    public void Without_a_root_every_path_stays_as_it_is()
    {
        foreach (var root in new string?[] { null, "", "/" })
        {
            var paths = new AnswerPaths(root);
            Assert.Null(paths.Root);
            Assert.Equal("/repo/Service.cs", paths.Relative("/repo/Service.cs"));
            Assert.Null(paths.Header);
        }
    }

    [Fact]
    public void Tool_rows_show_relative_paths_and_the_answer_knows_it_named_the_root()
    {
        using var db = new TempDb();
        db.Store.Seeded();
        var paths = new AnswerPaths("/repo");
        var tools = new CodeGraphTools(db.Store, new IndexFreshness(db.DbPath, TimeSpan.FromSeconds(3)), new Telemetry(null),
            new LiveClient(db.DbPath), new SemanticSearch(null), new GraphSnapshotCache(db.DbPath), paths);

        var answer = tools.FindSymbol("Save", nameMatch: "Exact");

        Assert.Contains("  Service.cs:1-2  [M:Demo.Service.Save]", answer);
        Assert.DoesNotContain("/repo/", answer);
        Assert.Equal("paths are relative to /repo", paths.Header);
    }

    [Fact]
    public void The_root_is_the_worktree_else_the_folder_above_codegraph_else_the_graphs_folder()
    {
        var temp = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"));
        try
        {
            var worktree = Path.Combine(temp, "repo");
            Directory.CreateDirectory(Path.Combine(worktree, ".git"));
            Directory.CreateDirectory(Path.Combine(worktree, "sub", ".codegraph"));
            Assert.Equal(worktree, AnswerPaths.RootFor(Path.Combine(worktree, "sub", ".codegraph", "graph.db")));

            var plain = Path.Combine(temp, "plain");
            Directory.CreateDirectory(Path.Combine(plain, ".codegraph"));
            Assert.Equal(plain, AnswerPaths.RootFor(Path.Combine(plain, ".codegraph", "graph.db")));

            var elsewhere = Path.Combine(temp, "elsewhere");
            Directory.CreateDirectory(elsewhere);
            Assert.Equal(elsewhere, AnswerPaths.RootFor(Path.Combine(elsewhere, "graph.db")));
        }
        finally
        {
            TempDb.TryDelete(temp);
        }
    }
}
