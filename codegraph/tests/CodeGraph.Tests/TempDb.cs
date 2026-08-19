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
    /// Removes a temp directory, tolerating the pooled SQLite handle that can outlive Dispose on Windows.
    /// A leftover temp file is not worth failing a test over.
    /// </summary>
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
