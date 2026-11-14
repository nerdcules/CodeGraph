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

    [Theory]
    [InlineData("[M:App.Program.Run(System.String)]", "M:App.Program.Run(System.String)")]
    [InlineData(" [ts:src/app.ts#App] ", "ts:src/app.ts#App")]
    [InlineData("M:App.Program.Run(System.String[])", "M:App.Program.Run(System.String[])")]
    [InlineData("[", "[")]
    public void A_bracketed_id_copied_from_an_answer_is_unwrapped(string given, string expected) =>
        Assert.Equal(expected, NodeIds.Unwrap(given));

    [Fact]
    public void An_id_passed_under_another_name_reaches_the_argument_the_tool_takes()
    {
        var call = new Dictionary<string, string> { ["symbolId"] = "M:A.B", ["depth"] = "2" };
        Assert.True(NodeIds.NameTheId("impact_of", call));
        Assert.Equal("M:A.B", call["id"]);
        Assert.False(call.ContainsKey("symbolId"));

        var live = new Dictionary<string, string> { ["id"] = "M:A.B" };
        Assert.True(NodeIds.NameTheId("live_references", live));
        Assert.Equal("M:A.B", live["symbol"]);

        var right = new Dictionary<string, string> { ["id"] = "M:A.B", ["nodeId"] = "M:X" };
        Assert.False(NodeIds.NameTheId("tests_for", right));
        Assert.Equal("M:A.B", right["id"]);
    }

    [Fact]
    public void A_root_from_the_client_counts_only_when_it_is_a_real_folder()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "open-folder")).FullName;

        Assert.Equal(folder, Worktree.UsableRoot(folder));
        // The Cursor CLI passes ${workspaceFolder} through as written; the server then looks from where it started.
        Assert.Null(Worktree.UsableRoot("${workspaceFolder}"));
        Assert.Null(Worktree.UsableRoot(""));
        Assert.Null(Worktree.UsableRoot(null));
        Assert.Null(Worktree.UsableRoot(Path.Combine(_root, "missing")));
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

    [Fact]
    public void Head_is_read_from_the_files_as_git_resolves_it()
    {
        Directory.CreateDirectory(_root);
        if (Git(_root, "--version") is null)
        {
            return; // no git on this machine
        }

        var repo = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        Git(repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "a");
        Git(repo, "add", ".");
        Git(repo, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "one");
        var deep = Directory.CreateDirectory(Path.Combine(repo, "src", "x")).FullName;

        // A loose ref.
        Assert.Equal(Git(repo, "rev-parse", "HEAD"), Worktree.HeadFromFiles(deep));

        // A packed ref, after a second commit moved the branch and gc packed it.
        File.WriteAllText(Path.Combine(repo, "b.txt"), "b");
        Git(repo, "add", ".");
        Git(repo, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "two");
        Git(repo, "pack-refs", "--all", "--prune");
        Assert.False(File.Exists(Path.Combine(repo, ".git", "refs", "heads", "main")));
        Assert.Equal(Git(repo, "rev-parse", "HEAD"), Worktree.HeadFromFiles(repo));

        // A linked worktree on its own branch: HEAD in the worktree's gitdir, the ref in the common directory.
        var linked = Path.Combine(_root, "linked");
        Git(repo, "worktree", "add", "-q", "-b", "side", linked);
        Assert.Equal(Git(linked, "rev-parse", "HEAD"), Worktree.HeadFromFiles(linked));

        // A detached HEAD.
        Git(repo, "checkout", "-q", "--detach", "HEAD~1");
        Assert.Equal(Git(repo, "rev-parse", "HEAD"), Worktree.HeadFromFiles(repo));
        Assert.Equal(Worktree.HeadFromFiles(repo), Worktree.Head(repo));
    }

    private static string? Git(string directory, params string[] args)
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("git", ["-C", directory, .. args])
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = System.Diagnostics.Process.Start(info)!;
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : throw new InvalidOperationException($"git {string.Join(' ', args)}: {process.StandardError.ReadToEnd()}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
