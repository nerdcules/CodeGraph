namespace CodeGraph.Indexer;

/// <summary>Writing a whole graph into a fresh file beside the live one, then putting it in place.</summary>
internal static class GraphFile
{
    /// <summary>The fresh file a rebuild writes: <c>graph.db.tmp</c>, emptied first (its folder made, for a first index).</summary>
    public static string Fresh(string dbPath)
    {
        var path = dbPath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);
        File.Delete(path + ".wal");
        return path;
    }

    /// <summary>How long a writer waits for another to finish before giving up.</summary>
    public static readonly TimeSpan WriteLockWait = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Holds <c>graph.db.lock</c> for one whole write: reading the old graph, writing the new rows (or a fresh
    /// <c>graph.db.tmp</c> with the other languages copied across) and swapping it in. Two writers on one graph (the
    /// daemon re-indexing C# while an editor imports the TypeScript front end, or two imports) would otherwise share
    /// <c>graph.db.tmp</c> or copy rows the other is about to replace. The lock is an open file with no sharing,
    /// which the OS releases when the process ends, however it ends; the file itself stays.
    /// </summary>
    public static async Task<IDisposable> LockAsync(string dbPath, Action<string> log, CancellationToken cancellationToken)
    {
        var path = dbPath + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var deadline = DateTime.UtcNow + WriteLockWait;
        var said = false;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                if (!said)
                {
                    log($"another index run is writing {dbPath}; waiting for it to finish");
                    said = true;
                }

                await Task.Delay(250, cancellationToken);
            }
            catch (IOException e)
            {
                throw new IOException($"another index run has been writing {dbPath} for {WriteLockWait.TotalMinutes:0} min; not waiting longer ({e.Message})", e);
            }
        }
    }

    /// <summary>
    /// Moves the rebuilt file over the graph. Readers hold the old one open briefly (and on Windows a held file
    /// cannot be replaced), so it retries for up to 30 seconds.
    /// </summary>
    public static void SwapIn(string source, string destination)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try
            {
                File.Delete(destination + ".wal");
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
        }
    }
}
