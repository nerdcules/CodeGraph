using System.ComponentModel;
using CodeGraph.Core;
using System.Diagnostics;

namespace CodeGraph.Indexer;

/// <summary>
/// Languages indexed through their SCIP indexer: codegraph-index runs the tool in the project folder, then imports
/// the index it writes (<see cref="ScipImport"/>). The tool must be on PATH or where its installer puts it (<see cref="ToolLocator"/>); each entry says where to get it.
/// </summary>
internal static class ScipIndexers
{
    /// <param name="Tested">The indexer version this release's corpus and fixtures ran against. An older one on PATH is a note in the graph, not a stop: the importer reads what it writes either way.</param>
    internal sealed record Indexer(string Language, string[] Markers, string Tool, Func<string, string, string[]> Arguments, string Install, string[] Extensions, string Tested);

    internal static readonly Indexer[] All =
    [
        new("java", ["pom.xml", "build.gradle", "build.gradle.kts", "build.sbt"], "scip-java",
            (_, output) => ["index", "--output", output],
            "https://sourcegraph.github.io/scip-java/ (coursier: cs install --contrib scip-java)",
            [".java", ".kt", ".kts", ".scala", ".gradle", ".xml", ".sbt"], Tested: "0.12.3"),
        new("python", ["pyproject.toml", "setup.py", "setup.cfg"], "scip-python",
            (dir, output) => ["index", ".", "--project-name", Path.GetFileName(dir), "--output", output, .. PythonEnvironment(output)],
            "npm install -g @sourcegraph/scip-python",
            [".py", ".toml", ".cfg"], Tested: "0.6.8"),
        new("go", ["go.work", "go.mod"], "scip-go",
            (dir, output) => ["index", .. GoWorkspacePatterns(dir), "--output", output],
            "go install github.com/scip-code/scip-go/cmd/scip-go@latest",
            [".go", ".mod", ".work"], Tested: "0.2.9"),
        new("rust", ["Cargo.toml"], "rust-analyzer",
            (_, output) => ["scip", ".", "--output", output],
            "rustup component add rust-analyzer",
            [".rs", ".toml"], Tested: "1.99.1"),
    ];

    /// <summary>
    /// The language version the build file names, as <c>Java 21</c>, <c>Go 1.22</c>, <c>Rust 1.70, edition 2021</c> or
    /// <c>Python >=3.9</c>; null when it names none. SCIP indexes do not carry it, and it is what tells a reader of
    /// index_status which language level the graph was built from.
    /// </summary>
    internal static string? LanguageVersionOf(string language, string marker)
    {
        string text;
        try
        {
            text = File.ReadAllText(marker);
        }
        catch (IOException)
        {
            return null;
        }

        static string? First(string text, params string[] patterns) =>
            patterns.Select(p => System.Text.RegularExpressions.Regex.Match(text, p, System.Text.RegularExpressions.RegexOptions.Multiline))
                .FirstOrDefault(m => m.Success)?.Groups[1].Value.Trim();

        var name = Path.GetFileName(marker).ToLowerInvariant();
        switch (language)
        {
            case "java" when name == "pom.xml":
                return First(text, @"<maven\.compiler\.release>\s*([^<\s]+)", @"<release>\s*([^<\s]+)", @"<maven\.compiler\.source>\s*([^<\s]+)",
                    @"<source>\s*([^<\s]+)", @"<java\.version>\s*([^<\s]+)", @"<kotlin\.version>\s*([^<\s]+)") is { } java ? $"Java {java.Replace("1.", "")}" : null;
            case "java" when name.StartsWith("build.gradle", StringComparison.Ordinal):
                return First(text, @"languageVersion\s*(?:=|\.set\()\s*JavaLanguageVersion\.of\(\s*(\d+)", @"jvmToolchain\(\s*(\d+)", @"(?:options\.)?release\s*(?:=|\.set\()\s*(\d+)",
                    @"sourceCompatibility\s*=?\s*(?:JavaVersion\.VERSION_)?['""]?(\d+(?:\.\d+)?|1_\d+)", @"targetCompatibility\s*=?\s*(?:JavaVersion\.VERSION_)?['""]?(\d+(?:\.\d+)?|1_\d+)")
                    is { } gradle ? $"Java {gradle.Replace("1_", "").Replace("1.", "")}" : null;
            case "java" when name == "build.sbt":
                return First(text, @"scalaVersion\s*:=\s*""([^""]+)""") is { } scala ? $"Scala {scala}" : null;
            case "go":
                return First(text, @"^go\s+(\d+(?:\.\d+)*)") is { } go ? $"Go {go}" : null;
            case "rust":
                var rust = First(text, @"^rust-version\s*=\s*""([^""]+)""");
                var edition = First(text, @"^edition\s*=\s*""([^""]+)""");
                return (rust, edition) switch
                {
                    (null, null) => null,
                    (null, _) => $"Rust edition {edition}",
                    (_, null) => $"Rust {rust}",
                    _ => $"Rust {rust}, edition {edition}",
                };
            case "python":
                return First(text, @"requires-python\s*=\s*""([^""]+)""", @"python_requires\s*=\s*['""]?([^'"",\n]+)") is { } python ? $"Python {python.Trim()}" : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// A note when the indexer on PATH is older than the one this release was tested with: its index still
    /// imports, but constructs it did not know yet are not in it. <paramref name="tool"/> is the SCIP
    /// tool_info, <c>scip-java 0.10.0</c>.
    /// </summary>
    internal static string? VersionNote(Indexer indexer, string? tool)
    {
        var found = System.Text.RegularExpressions.Regex.Match(tool ?? "", @"\b(\d+(?:\.\d+){1,3})\b");
        if (!found.Success || !Version.TryParse(Pad(found.Groups[1].Value), out var version) || !Version.TryParse(Pad(indexer.Tested), out var tested))
        {
            return null;
        }

        return version < tested ? $"{indexer.Tool} {found.Groups[1].Value} is older than the {indexer.Tested} this release was tested with; constructs it did not know yet are not in the graph" : null;

        static string Pad(string v) => v.Count(c => c == '.') == 0 ? v + ".0" : v;
    }

    /// <summary>
    /// scip-python lists the installed packages with pip and stops when there is no pip. Without one, an empty
    /// environment lets it index the project's own code (calls into packages are then unresolved).
    /// </summary>
    internal static string[] PythonEnvironment(string output)
    {
        if (ToolLocator.Find("pip") is not null || ToolLocator.Find("pip3") is not null)
        {
            return [];
        }

        var environment = output + ".environment.json";
        File.WriteAllText(environment, "[]");
        return ["--environment", environment];
    }

    /// <summary>
    /// A go.work root has no module of its own, so scip-go would index nothing: name each used module's packages
    /// (./svc/...), which gives one index for the whole workspace. A plain module needs no patterns.
    /// </summary>
    internal static string[] GoWorkspacePatterns(string dir)
    {
        var work = Path.Combine(dir, "go.work");
        if (!File.Exists(work) || File.Exists(Path.Combine(dir, "go.mod")))
        {
            return [];
        }

        var patterns = new List<string>();
        var inBlock = false;
        foreach (var raw in File.ReadLines(work))
        {
            var line = raw.Split("//")[0].Trim();
            if (inBlock)
            {
                if (line == ")")
                {
                    inBlock = false;
                }
                else if (line.Length > 0)
                {
                    patterns.Add(Pattern(line));
                }
            }
            else if (line == "use (")
            {
                inBlock = true;
            }
            else if (line.StartsWith("use ", StringComparison.Ordinal))
            {
                patterns.Add(Pattern(line[4..].Trim()));
            }
        }

        return [.. patterns];

        static string Pattern(string module)
        {
            var path = module.Trim('"').Replace('\\', '/').TrimEnd('/');
            return (path is "." ? "." : path.StartsWith("./", StringComparison.Ordinal) || path.StartsWith("../", StringComparison.Ordinal) ? path : "./" + path) + "/...";
        }
    }

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
            if (LanguageVersionOf(indexer.Language, batch.Target) is { } languageVersion)
            {
                batch.Meta[LanguageMetaKeys.LanguageVersions] = languageVersion;
            }

            if (VersionNote(indexer, batch.Tool) is { } note)
            {
                batch.Meta[LanguageMetaKeys.Notes] = note;
            }

            foreach (var line in ScipImport.CompilerSummary(batch))
            {
                log(line);
            }
            var (nodes, edges) = await GraphImport.WriteAsync(batch, dbPath, log, cancellationToken);
            log($"indexed {nodes} {batch.Language} nodes, {edges} edges from {batch.Files.Count} files in {clock.Elapsed.TotalSeconds:F1}s → {dbPath}");
            return 0;
        }
        finally
        {
            try
            {
                File.Delete(output);
                File.Delete(output + ".environment.json");
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
