using CodeGraph.Core;

namespace CodeGraph.Tests;

public sealed class WorktreeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"));

    public void Dispose() => TempDb.TryDelete(_root);

    [Fact]
    public void A_linked_worktree_with_a_git_file_counts_as_a_root()
    {
        var worktree = Directory.CreateDirectory(Path.Combine(_root, "sa-1")).FullName;
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: ../main/.git/worktrees/sa-1");
        var src = Directory.CreateDirectory(Path.Combine(worktree, "src", "App")).FullName;

        Assert.Equal(worktree, Worktree.RootOf(src));
        Assert.Equal(Path.Combine(worktree, ".codegraph", "graph.db"), Worktree.DefaultDbPath(src));
    }

    [Fact]
    public void FindDb_walks_up_to_the_worktree_root()
    {
        var worktree = Directory.CreateDirectory(Path.Combine(_root, "sa-2")).FullName;
        Directory.CreateDirectory(Path.Combine(worktree, ".git"));
        var db = Path.Combine(worktree, ".codegraph", "graph.db");
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        File.WriteAllText(db, "");
        var deep = Directory.CreateDirectory(Path.Combine(worktree, "a", "b")).FullName;

        Assert.Equal(db, Worktree.FindDb(deep));
    }

    [Fact]
    public void FindDb_does_not_escape_into_a_parent_repo()
    {
        // A worktree nested under a directory that has its own graph must not borrow it.
        var outer = Path.Combine(_root, ".codegraph", "graph.db");
        Directory.CreateDirectory(Path.GetDirectoryName(outer)!);
        File.WriteAllText(outer, "");
        var worktree = Directory.CreateDirectory(Path.Combine(_root, "sa-3")).FullName;
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: elsewhere");

        Assert.Null(Worktree.FindDb(worktree));
    }
}
