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
