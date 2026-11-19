using System.Diagnostics;
using CodeGraph.Core;

namespace CodeGraph.Tests;

/// <summary>
/// The Windows install layout (issue #9): the install folder is a junction to one release folder in
/// <c>.&lt;name&gt;.versions</c> beside it. Only that layout makes an executable start itself again from the release
/// folder; a link that points anywhere else keeps the old behaviour.
/// </summary>
public sealed class ReleaseFolderTests
{
    [Fact]
    public void Only_the_junction_of_the_install_layout_names_a_release_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"));
        try
        {
            var release = Directory.CreateDirectory(Path.Combine(root, "tools", ".codegraph.versions", "1.5.0-abcd")).FullName;
            var elsewhere = Directory.CreateDirectory(Path.Combine(root, "elsewhere")).FullName;
            var install = Path.Combine(root, "tools", "codegraph");
            var other = Path.Combine(root, "tools", "other");
            Link(install, release);
            Link(other, elsewhere);

            Assert.Equal(release, ReleaseFolder.RealFolder(install), ignoreCase: OperatingSystem.IsWindows());
            Assert.Equal(release, ReleaseFolder.RealFolder(install + Path.DirectorySeparatorChar), ignoreCase: OperatingSystem.IsWindows());
            Assert.Null(ReleaseFolder.RealFolder(release));    // the release folder itself is no link
            Assert.Null(ReleaseFolder.RealFolder(other));      // a link outside the layout
            Assert.Null(ReleaseFolder.RealFolder(elsewhere));
        }
        finally
        {
            TempDb.TryDelete(root);
        }
    }

    /// <summary>A junction on Windows (no rights needed, as the installers make it), a symbolic link elsewhere.</summary>
    private static void Link(string path, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(path, target);
            return;
        }

        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", path, target])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }
}
