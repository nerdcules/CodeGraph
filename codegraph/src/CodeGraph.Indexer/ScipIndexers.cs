using System.ComponentModel;
using System.Diagnostics;

namespace CodeGraph.Indexer;

/// <summary>
/// Languages indexed through their SCIP indexer: codegraph-index runs the tool in the project folder, then imports
/// the index it writes (<see cref="ScipImport"/>). The tool must be on PATH or where its installer puts it (<see cref="ToolLocator"/>); each entry says where to get it.
/// </summary>
internal static class ScipIndexers
{
    internal sealed record Indexer(string Language, string[] Markers, string Tool, Func<string, string, string[]> Arguments, string Install, string[] Extensions);

    internal static readonly Indexer[] All =
    [
        new("java", ["pom.xml", "build.gradle", "build.gradle.kts", "build.sbt"], "scip-java",
            (_, output) => ["index", "--output", output],
            "https://sourcegraph.github.io/scip-java/ (coursier: cs install --contrib scip-java)",
            [".java", ".kt", ".kts", ".scala", ".gradle", ".xml", ".sbt"]),
        new("python", ["pyproject.toml", "setup.py", "setup.cfg"], "scip-python",
            (dir, output) => ["index", ".", "--project-name", Path.GetFileName(dir), "--output", output],
            "npm install -g @sourcegraph/scip-python",
            [".py", ".toml", ".cfg"]),
        new("go", ["go.mod"], "scip-go",
            (_, output) => ["--output", output],
            "go install github.com/sourcegraph/scip-go/cmd/scip-go@latest",
            [".go", ".mod"]),
        new("rust", ["Cargo.toml"], "rust-analyzer",
            (_, output) => ["scip", ".", "--output", output],
            "rustup component add rust-analyzer",
            [".rs", ".toml"]),
    ];

    /// <summary>The indexer for a build file, or for a folder holding one.</summary>
    public static (Indexer Indexer, string Marker)? For(string path)
    {
        foreach (var indexer in All)
        {
            if (Directory.Exists(path))
            {
                if (indexer.Markers.Select(m => Path.Combine(path, m)).FirstOrDefault(File.Exists) is { } marker)
                {
                    return (indexer, marker);
                }
            }
            else if (indexer.Markers.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            {
                return (indexer, path);
            }
        }

        return null;
    }

    public static async Task<int> IndexAsync(Indexer indexer, string marker, string dbPath, Action<string> log, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(marker))!;
        var output = Path.Combine(Path.GetTempPath(), $"codegraph-{Guid.NewGuid():n}.scip");
        var clock = Stopwatch.StartNew();
        try
        {
            var info = new ProcessStartInfo(ToolLocator.Find(indexer.Tool) ?? indexer.Tool, indexer.Arguments(directory, output))
            {
                WorkingDirectory = directory,
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
                log($"{indexer.Language}: {indexer.Tool} is not on PATH ({e.Message}). Install it: {indexer.Install}");
                return 1;
            }

            using (process)
            {
                process.StandardInput.Close();
                process.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) log($"{indexer.Tool}: {line}"); };
                process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) log($"{indexer.Tool}: {line}"); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await process.WaitForExitAsync(cancellationToken);
                if (process.ExitCode != 0 || !File.Exists(output))
                {
                    log($"{indexer.Language}: {indexer.Tool} exited with {process.ExitCode}; the graph is unchanged");
                    return 1;
                }
            }

            var batch = ScipImport.Read(output, log);
            batch.Target = Path.GetFullPath(marker); // freshness hints and the daemon launcher name the build file, not a temp index
            var (nodes, edges) = await GraphImport.WriteAsync(batch, dbPath, log, cancellationToken);
            log($"indexed {nodes} {batch.Language} nodes, {edges} edges from {batch.Files.Count} files in {clock.Elapsed.TotalSeconds:F1}s → {dbPath}");
            return 0;
        }
        finally
        {
            try
            {
                File.Delete(output);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary><c>--watch</c>: re-index on every save; the language's server (jdtls, pyright, gopls, rust-analyzer), when on PATH, answers the live tools.</summary>
    public static Task<int> WatchAsync(Indexer indexer, string marker, string dbPath, double? idleExitMinutes, Action<string> log)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(marker))!;
        return ExternalDaemon.RunAsync(marker, dbPath, indexer.Language, new HashSet<string>(indexer.Extensions, StringComparer.OrdinalIgnoreCase),
            token => IndexAsync(indexer, marker, dbPath, log, token), Lsp.LspServer.For(indexer.Language, root, log), idleExitMinutes, log);
    }
}
