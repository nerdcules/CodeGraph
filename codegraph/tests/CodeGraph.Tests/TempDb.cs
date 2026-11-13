using CodeGraph.Core;

namespace CodeGraph.Tests;

/// <summary>A GraphStore on a throwaway file, deleted when the test finishes.</summary>
internal sealed class TempDb : IDisposable
{
    private readonly string _directory;

    public TempDb()
    {
        _directory = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"));
        Store = new GraphStore(Path.Combine(_directory, "graph.db"));
    }

    public GraphStore Store { get; }

    public string DbPath => Path.Combine(_directory, "graph.db");

    public void Dispose()
    {
        Store.Dispose();
        TryDelete(_directory);
    }

    /// <summary>
    /// Removes a temp directory, tolerating a DuckDB file handle that can outlive Dispose on Windows.
    /// A leftover temp file is not worth failing a test over.
    /// </summary>
    /// <summary>The path with every symbolic link in it resolved, as a shell's <c>pwd</c> prints it.</summary>
    public static string RealPath(string path)
    {
        var current = Path.GetPathRoot(path)!;
        foreach (var part in path[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                current = target.FullName;
            }
        }

        return current;
    }

    public static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
