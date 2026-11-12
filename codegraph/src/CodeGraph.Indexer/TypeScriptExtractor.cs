using System.ComponentModel;
using System.Diagnostics;
using CodeGraph.Core;

namespace CodeGraph.Indexer;

/// <summary>
/// Runs the TypeScript and Angular extractor (<c>extractors/typescript</c>, a Node program) and imports what it
/// prints. The extractor is found through CODEGRAPH_TS, beside this executable in an install
/// (<c>extractors/typescript/cli.mjs</c>), or in a source checkout above the build output.
/// </summary>
internal static class TypeScriptExtractor
{
    private static readonly string[] WorkspaceFiles = ["angular.json", "nx.json", "tsconfig.json"];

    /// <summary>A target this extractor handles: a tsconfig, angular.json, nx.json or package.json, or a folder with one of the first three.</summary>
    public static bool IsTarget(string path)
    {
        if (Directory.Exists(path))
        {
            return WorkspaceFiles.Any(f => File.Exists(Path.Combine(path, f)));
        }

        var name = Path.GetFileName(path);
        return name is "angular.json" or "nx.json" or "package.json"
            || (name.StartsWith("tsconfig", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A folder's workspace file, angular.json before nx.json before tsconfig.json; the path itself otherwise.</summary>
    public static string WorkspaceFile(string path) =>
        Directory.Exists(path)
            ? WorkspaceFiles.Select(f => Path.Combine(path, f)).FirstOrDefault(File.Exists) ?? path
            : path;

    /// <summary>
    /// TypeScript workspaces in a repo beside a C# solution (an Angular ClientApp, a web/ folder): every angular.json
    /// or nx.json, and every tsconfig.json with a package.json beside it, up to three levels down, outside
    /// node_modules and build output, and not inside another workspace already found.
    /// </summary>
    public static IReadOnlyList<string> WorkspacesUnder(string root)
    {
        var found = new List<string>();
        void Walk(DirectoryInfo dir, int depth)
        {
            if (WorkspaceFiles.Take(2).Select(f => Path.Combine(dir.FullName, f)).FirstOrDefault(File.Exists) is { } workspace
                || (File.Exists(Path.Combine(dir.FullName, "package.json")) && File.Exists(workspace = Path.Combine(dir.FullName, "tsconfig.json"))))
            {
                found.Add(workspace);
                return; // one workspace per subtree
            }

            if (depth == 0)
            {
                return;
            }

            IEnumerable<DirectoryInfo> children;
            try
            {
                children = dir.EnumerateDirectories().ToList();
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                return;
            }

            foreach (var child in children.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                // Relevant() rules out node_modules, dist and the other build folders the watcher ignores.
                if (!child.Name.StartsWith('.') && Relevant(Path.Combine(child.FullName, "x.ts"))
                    && child.Name is not ("bin" or "obj" or "packages" or "TestResults"))
                {
                    Walk(child, depth - 1);
                }
            }
        }

        Walk(new DirectoryInfo(root), 3);
        return found;
    }

    /// <summary>The extractor's cli.mjs, or null when it is not installed.</summary>
    public static string? FindCli()
    {
        if (Environment.GetEnvironmentVariable("CODEGRAPH_TS") is { Length: > 0 } configured)
        {
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var cli = Path.Combine(dir.FullName, "extractors", "typescript", "cli.mjs");
            if (File.Exists(cli))
            {
                return cli;
            }
        }

        return null;
    }

    /// <summary>Runs the extractor on <paramref name="target"/> and imports its records. Returns the exit code.</summary>
    public static async Task<int> IndexAsync(string target, string dbPath, Action<string> log, CancellationToken cancellationToken)
    {
        var cli = FindCli();
        if (cli is null)
        {
            log("typescript: the extractor is not installed (extractors/typescript/cli.mjs); re-run the install script, or set CODEGRAPH_TS");
            return 1;
        }

        if (!Directory.Exists(Path.Combine(Path.GetDirectoryName(cli)!, "node_modules", "typescript")))
        {
            log($"typescript: run `npm ci` in {Path.GetDirectoryName(cli)} first (the extractor's dependencies are missing)");
            return 1;
        }

        var clock = Stopwatch.StartNew();
        var info = new ProcessStartInfo("node", [cli, target])
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            log($"typescript: cannot run node ({e.Message}); install Node.js 20 or later");
            return 1;
        }

        using (process)
        {
            process.StandardInput.Close();
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is { Length: > 0 } line)
                {
                    log(line);
                }
            };
            process.BeginErrorReadLine();
            var batch = await Task.Run(() => GraphBatch.Read(process.StandardOutput), cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                log($"typescript: the extractor exited with {process.ExitCode}; the graph is unchanged");
                return 1;
            }

            foreach (var problem in batch.Problems)
            {
                log($"typescript: {problem}");
            }

            if (batch.Language.Length == 0)
            {
                return 1;
            }

            var (nodes, edges) = await GraphImport.WriteAsync(batch, dbPath, log, cancellationToken);
            log($"indexed {nodes} {batch.Language} nodes, {edges} edges from {batch.Files.Count} files in {clock.Elapsed.TotalSeconds:F1}s → {dbPath}");
            return 0;
        }
    }

    /// <summary>
    /// <c>--watch</c> for a TypeScript workspace: index, then re-index on every save, debounced. There is no live
    /// daemon for TypeScript yet, so the MCP server's live tools say so; graph answers stay fresh.
    /// </summary>
    public static async Task<int> WatchAsync(string target, string dbPath, Action<string> log, CancellationToken cancellationToken)
    {
        var root = Directory.Exists(target) ? Path.GetFullPath(target) : Path.GetDirectoryName(Path.GetFullPath(target))!;
        var exit = await IndexAsync(target, dbPath, log, cancellationToken);
        using var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName };
        var changed = new SemaphoreSlim(0);
        void OnChange(object _, FileSystemEventArgs e)
        {
            if (Relevant(e.FullPath))
            {
                changed.Release();
            }
        }

        watcher.Changed += OnChange;
        watcher.Created += OnChange;
        watcher.Deleted += OnChange;
        watcher.Renamed += (s, e) => OnChange(s, e);
        watcher.EnableRaisingEvents = true;
        log($"watching {root} (Ctrl+C to stop)");
        try
        {
            while (true)
            {
                await changed.WaitAsync(cancellationToken);
                // Debounce: a save often arrives as several events, and editors save several files at once.
                await Task.Delay(500, cancellationToken);
                while (changed.CurrentCount > 0)
                {
                    await changed.WaitAsync(cancellationToken);
                }

                exit = await IndexAsync(target, dbPath, log, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            return exit;
        }
    }

    internal static bool Relevant(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(s => s is "node_modules" or ".git" or ".codegraph" or "dist" or ".angular" or ".nx" or "coverage"))
        {
            return false;
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".ts" or ".tsx" or ".mts" or ".cts" or ".js" or ".jsx" or ".mjs" or ".cjs" or ".html" or ".json";
    }
}
