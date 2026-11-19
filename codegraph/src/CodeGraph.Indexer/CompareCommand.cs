using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Core;

namespace CodeGraph.Indexer;

/// <summary>
/// <c>codegraph-index compare "&lt;prompt&gt;"</c>: asks Claude Code the same question in separate new sessions, with
/// the codegraph server and without it, and reports what each session's own log says it cost. Each run is its own
/// <c>claude -p</c> process with no saved session, so no run sees another's context; the rounds alternate which
/// condition goes first. Both conditions get the same model, effort, settings, CLAUDE.md and memory, and the same
/// read-only tools; the only difference is the codegraph server from the project's <c>.mcp.json</c>.
/// </summary>
internal static class CompareCommand
{
    public const string Usage =
        "usage: codegraph-index compare <prompt> [--prompt-file <file>] [--runs <n>] [--model <name>] [--effort <level>]\n" +
        "                               [--allow <rule>]... [--out <folder>] [--timeout <seconds>] [--claude <path>] [--yes]";

    /// <summary>The built-in tools both conditions get. None of them writes a file.</summary>
    public const string BuiltInTools = "Read,Grep,Glob,Bash";

    /// <summary>
    /// What both conditions may run without asking (Claude Code's <c>dontAsk</c> mode denies everything else). Only
    /// commands with no option that writes a file or runs another program: not <c>find</c> (<c>-delete</c>, <c>-exec</c>),
    /// <c>sed</c> (<c>-i</c>), <c>awk</c>, <c>sort</c> (<c>-o</c>), <c>uniq</c> (an output file), <c>rg</c> (<c>--pre</c>),
    /// <c>git log/show/diff</c> (<c>--output</c>) or <c>git grep</c> (<c>-O</c>). Claude Code checks each part of a
    /// compound command and refuses output redirection.
    /// </summary>
    public static readonly string[] ReadOnlyRules =
    [
        "Read", "Grep", "Glob",
        "Bash(grep *)", "Bash(ls)", "Bash(ls *)", "Bash(cat *)", "Bash(head *)", "Bash(tail *)", "Bash(wc *)", "Bash(pwd)",
        "Bash(git status)", "Bash(git status *)", "Bash(git ls-files)", "Bash(git ls-files *)",
    ];

    /// <summary>
    /// Variables a Claude Code session sets for the processes it starts. A run launched from inside a session would
    /// otherwise inherit them (CLAUDE_EFFORT changes the effort, for one).
    /// </summary>
    private static readonly string[] SessionVariables = ["CLAUDECODE", "CLAUDE_PID", "CLAUDE_EFFORT", "CLAUDE_AGENT_SDK_VERSION", "MCP_CONNECTION_NONBLOCKING"];

    /// <summary>CLAUDE_CODE_* variables that configure the account or provider, which both conditions keep.</summary>
    private static readonly string[] KeptPrefixes = ["CLAUDE_CODE_USE_", "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_SKIP_", "CLAUDE_CODE_API_KEY"];

    public const string With = "with";
    public const string Without = "without";

    internal sealed record Options(
        string Prompt,
        int Runs,
        string? Model,
        string? Effort,
        IReadOnlyList<string> Allow,
        string? OutDir,
        int TimeoutSeconds,
        string? Claude,
        bool Yes);

    internal sealed record CompareRun(
        int Round,
        string Condition,
        double WallSeconds,
        string Exit,
        string Log,
        RunMetrics Metrics,
        IReadOnlyList<string> Problems)
    {
        public bool Valid => Problems.Count == 0;
    }

    internal sealed record CompareSetup(
        DateTime StartedAt,
        string Prompt,
        string Root,
        string? Head,
        string DbPath,
        string? GraphCommit,
        bool GraphStale,
        int GraphChangedFiles,
        string? Model,
        string? Effort,
        IReadOnlyList<string> AllowedTools,
        string Claude);

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, string version,
        Func<string?>? ask, string? currentDirectory = null, CancellationToken cancellationToken = default)
    {
        var options = Parse(args, error);
        if (options is null)
        {
            return 1;
        }

        var start = currentDirectory ?? Directory.GetCurrentDirectory();
        var root = Worktree.RootOf(start) ?? Path.GetFullPath(start);
        var dbPath = Worktree.FindDb(start);
        if (dbPath is null || !File.Exists(dbPath))
        {
            error.WriteLine($"codegraph compare: no graph for {root}; run codegraph-index init first");
            return 1;
        }

        var server = ServerEntry(root);
        if (server is null)
        {
            error.WriteLine($"codegraph compare: {Path.Combine(root, ".mcp.json")} has no codegraph server; run codegraph-index init first");
            return 1;
        }

        var claude = options.Claude ?? ToolLocator.Find("claude");
        if (claude is null || (options.Claude is not null && !File.Exists(claude)))
        {
            error.WriteLine("codegraph compare: cannot find Claude Code (claude); install it or pass --claude <path>");
            return 1;
        }

        var status = await StatusCommand.ReadAsync(dbPath, version);
        var setup = new CompareSetup(
            DateTime.UtcNow, options.Prompt, root, Worktree.Head(root), dbPath, status.Graph?.Commit,
            status.Graph?.Stale ?? false, status.Graph?.ChangedCount ?? 0, options.Model, options.Effort,
            [.. ReadOnlyRules, .. options.Allow], claude);

        var sessions = options.Runs * 2;
        output.WriteLine($"compare runs {sessions} Claude Code sessions ({options.Runs} with the codegraph server, {options.Runs} without), one after another.");
        output.WriteLine("Each is a new `claude -p` session with no saved session, so no run sees another's context.");
        output.WriteLine("They use your Claude account's usage. The sessions get read-only tools: none can edit a file.");
        if (setup.GraphStale)
        {
            output.WriteLine($"The graph is STALE ({setup.GraphChangedFiles} indexed file(s) edited since it was built); the with runs will see that warning.");
        }

        if (!options.Yes)
        {
            if (ask is null)
            {
                error.WriteLine("codegraph compare: add --yes to start the sessions without a terminal to confirm in");
                return 1;
            }

            output.Write("Start them? [y/N] ");
            output.Flush();
            if (ask()?.Trim().ToLowerInvariant() is not ("y" or "yes"))
            {
                output.WriteLine("Nothing was run.");
                return 1;
            }
        }

        var outDir = Path.GetFullPath(options.OutDir ?? Path.Combine(Path.GetDirectoryName(dbPath)!, "compare",
            setup.StartedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)));
        Directory.CreateDirectory(outDir);

        var runs = new List<CompareRun>();
        for (var round = 1; round <= options.Runs; round++)
        {
            // Odd rounds start without the server, even rounds with it, so a warm prompt cache helps both alike.
            string[] order = round % 2 == 1 ? [Without, With] : [With, Without];
            foreach (var condition in order)
            {
                var run = await RunOneAsync(options, setup, server, condition, round, outDir, cancellationToken);
                runs.Add(run);
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"round {round} {condition}: {run.WallSeconds:F1} s, {run.Metrics.ModelCalls} model calls, {run.Metrics.InputTokens:N0} input tokens{(run.Valid ? "" : $" (not counted: {string.Join("; ", run.Problems)})")}"));
            }
        }

        var report = Report(setup, runs, outDir);
        await File.WriteAllTextAsync(Path.Combine(outDir, "report.md"), report + "\n" + Answers(runs), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(outDir, "results.json"),
            JsonSerializer.Serialize(new { setup, runs }, StatusCommand.Json), cancellationToken);
        output.WriteLine();
        output.WriteLine(report);
        return runs.All(r => r.Valid) ? 0 : 2;
    }

    internal static Options? Parse(string[] args, TextWriter error)
    {
        string? prompt = null, model = null, effort = null, outDir = null, claude = null;
        int runs = 3, timeout = 900;
        var allow = new List<string>();
        var yes = false;
        for (var i = 0; i < args.Length; i++)
        {
            var hasValue = i + 1 < args.Length;
            switch (args[i])
            {
                case "--prompt-file" when hasValue:
                    var file = args[++i];
                    if (!File.Exists(file))
                    {
                        error.WriteLine($"codegraph compare: no such file {file}");
                        return null;
                    }

                    prompt = File.ReadAllText(file).Trim();
                    break;
                case "--runs" when hasValue && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 20:
                    runs = n;
                    i++;
                    break;
                case "--timeout" when hasValue && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) && t > 0:
                    timeout = t;
                    i++;
                    break;
                case "--model" when hasValue:
                    model = args[++i];
                    break;
                case "--effort" when hasValue:
                    effort = args[++i];
                    break;
                case "--allow" when hasValue:
                    allow.Add(args[++i]);
                    break;
                case "--out" when hasValue:
                    outDir = args[++i];
                    break;
                case "--claude" when hasValue:
                    claude = args[++i];
                    break;
                case "--yes" or "-y":
                    yes = true;
                    break;
                case var positional when !positional.StartsWith('-') && prompt is null:
                    prompt = positional;
                    break;
                default:
                    error.WriteLine(Usage);
                    return null;
            }
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            error.WriteLine(Usage);
            return null;
        }

        return new Options(prompt, runs, model, effort, allow, outDir, timeout, claude, yes);
    }

    /// <summary>The project's codegraph server entry from <c>.mcp.json</c>, with telemetry off so the runs stay out of the usage log.</summary>
    internal static JsonObject? ServerEntry(string root)
    {
        var path = Path.Combine(root, ".mcp.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path))?["mcpServers"]?["codegraph"] is not JsonObject entry || entry["command"] is null)
            {
                return null;
            }

            var copy = (JsonObject)entry.DeepClone();
            if (copy["env"] is not JsonObject env)
            {
                env = [];
                copy["env"] = env;
            }

            env["CODEGRAPH_TELEMETRY"] = "off";
            return copy;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The <c>claude</c> arguments for one run. The only difference between the conditions is the MCP config.</summary>
    internal static List<string> Arguments(Options options, IReadOnlyList<string> allowedTools, JsonObject server, string condition)
    {
        var servers = new JsonObject();
        if (condition == With)
        {
            servers["codegraph"] = server.DeepClone();
        }

        var rules = condition == With ? [.. allowedTools, "mcp__codegraph"] : allowedTools.ToList();
        // --allowedTools, --tools and --mcp-config take several values; a flag after each ends the list.
        var arguments = new List<string> { "-p", options.Prompt, "--allowedTools" };
        arguments.AddRange(rules);
        arguments.AddRange(["--tools", BuiltInTools, "--mcp-config", new JsonObject { ["mcpServers"] = servers }.ToJsonString(),
            "--strict-mcp-config", "--permission-mode", "dontAsk", "--no-session-persistence", "--output-format", "stream-json"]);
        if (options.Model is not null)
        {
            arguments.AddRange(["--model", options.Model]);
        }

        if (options.Effort is not null)
        {
            arguments.AddRange(["--effort", options.Effort]);
        }

        arguments.Add("--verbose");
        return arguments;
    }

    private static async Task<CompareRun> RunOneAsync(Options options, CompareSetup setup, JsonObject server, string condition,
        int round, string outDir, CancellationToken cancellationToken)
    {
        var log = Path.Combine(outDir, $"{round}-{condition}.jsonl");
        var info = new ProcessStartInfo(setup.Claude, Arguments(options, setup.AllowedTools, server, condition))
        {
            WorkingDirectory = setup.Root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var name in info.Environment.Keys.Where(Inherited).ToList())
        {
            info.Environment.Remove(name);
        }

        var before = TreeState(setup.Root);
        var stopwatch = Stopwatch.StartNew();
        string exit;
        using (var process = Process.Start(info) ?? throw new InvalidOperationException("Process.Start returned null"))
        {
            process.StandardInput.Close();
            await using var stdout = File.Create(log);
            await using var stderr = File.Create(Path.ChangeExtension(log, ".err"));
            var copyOut = process.StandardOutput.BaseStream.CopyToAsync(stdout, cancellationToken);
            var copyErr = process.StandardError.BaseStream.CopyToAsync(stderr, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                exit = process.ExitCode.ToString(CultureInfo.InvariantCulture);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                exit = cancellationToken.IsCancellationRequested ? "cancelled" : "timeout";
            }

            await Task.WhenAll(copyOut, copyErr);
        }

        stopwatch.Stop();
        var after = TreeState(setup.Root);
        var metrics = ClaudeLog.Parse(File.ReadLines(log));
        return new CompareRun(round, condition, Math.Round(stopwatch.Elapsed.TotalSeconds, 1), exit, log, metrics,
            Problems(condition, exit, metrics, before, after, setup.Model));
    }

    /// <summary>Whether a variable of this process is one a Claude Code session set for it, not the user's own.</summary>
    internal static bool Inherited(string name) =>
        SessionVariables.Contains(name, StringComparer.OrdinalIgnoreCase)
        || (name.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
            && !KeptPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Why a run does not count, if it does not: each check is a fact read from the log or the working tree.</summary>
    internal static List<string> Problems(string condition, string exit, RunMetrics metrics, string? before, string? after, string? model)
    {
        var problems = new List<string>();
        if (exit != "0")
        {
            problems.Add($"exit {exit}");
        }

        if (!metrics.HasResult)
        {
            problems.Add("no result in the log");
        }
        else if (metrics.IsError)
        {
            problems.Add("the session ended in an error");
        }

        var codegraph = metrics.McpServers.FirstOrDefault(s => s.Name == "codegraph");
        if (condition == With && codegraph?.Status != "connected")
        {
            problems.Add($"the codegraph server was {codegraph?.Status ?? "missing"}");
        }

        if (condition == Without && (metrics.McpServers.Count > 0 || metrics.Tools.Any(RunMetrics.IsCodeGraphTool)))
        {
            problems.Add("an MCP server was loaded");
        }

        if (before != after)
        {
            problems.Add("the working tree changed during the run");
        }

        return problems;
    }

    /// <summary>
    /// The working tree's changes as git lists them, with each listed file's hash, without the graph folder; null
    /// outside git. Two equal states mean no tracked file changed and no listed file was rewritten.
    /// </summary>
    internal static string? TreeState(string root)
    {
        try
        {
            var info = new ProcessStartInfo("git", ["status", "--porcelain=v1", "--untracked-files=all"])
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return null;
            }

            // A file that was already edited or untracked stays on the list when a run edits it again, so each
            // listed file's content goes into the state as well.
            var lines = text.Split('\n')
                .Where(line => line.Length > 3 && !line[3..].Trim('"').StartsWith(".codegraph/", StringComparison.Ordinal))
                .Select(line =>
                {
                    var path = line[3..];
                    var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
                    var file = Path.Combine(root, (arrow >= 0 ? path[(arrow + 4)..] : path).Trim('"'));
                    return File.Exists(file) ? line + " " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))) : line;
                });
            return string.Join('\n', lines);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    internal static string Report(CompareSetup setup, IReadOnlyList<CompareRun> runs, string outDir)
    {
        var first = runs.Select(r => r.Metrics).FirstOrDefault(m => m.ClaudeVersion is not null);
        var models = runs.Select(r => r.Metrics.Model).Where(m => m is not null).Distinct().ToList();
        var sb = new StringBuilder();
        sb.AppendLine(Invariant($"# CodeGraph with and without, {setup.StartedAt:yyyy-MM-dd HH:mm} UTC"));
        sb.AppendLine();
        sb.AppendLine($"- Prompt: {setup.Prompt}");
        sb.AppendLine($"- Repository: {setup.Root}, commit {Short(setup.Head)}");
        sb.AppendLine($"- Graph: {setup.DbPath}, built at commit {Short(setup.GraphCommit)}; " +
                      (setup.GraphStale ? $"STALE, {setup.GraphChangedFiles} indexed file(s) edited since" : "up to date with the working tree"));
        sb.AppendLine($"- Claude Code {first?.ClaudeVersion ?? "?"}, model {(models.Count == 0 ? "?" : string.Join(", ", models))}, " +
                      $"effort {setup.Effort ?? "not set (Claude Code's default)"}");
        sb.AppendLine("- Each run is a new `claude -p` session with no saved session: no run sees another's context. " +
                      "Odd rounds ran without the server first, even rounds with it first.");
        sb.AppendLine("- Both conditions: the same settings, CLAUDE.md and memory; tools Read, Grep, Glob and read-only shell commands. " +
                      "With: the codegraph server from .mcp.json as well, telemetry off.");
        sb.AppendLine();
        sb.AppendLine("Every figure below is from the session's own log except wall time, which compare measured. " +
                      "Input tokens count every model call's whole input, cache reads included. " +
                      "First and last context are the input of the first and the last model call; the first is what the session starts with " +
                      "(system prompt, tools, CLAUDE.md, memory, the prompt).");
        sb.AppendLine();
        sb.AppendLine("| run | condition | wall s | model calls | input tokens | cache reads | output tokens | cost USD | first context | last context | growth | codegraph calls | other tool calls | denied | counted |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var run in runs)
        {
            var m = run.Metrics;
            sb.AppendLine(Invariant(
                $"| {run.Round} | {run.Condition} | {run.WallSeconds:F1} | {m.ModelCalls} | {m.InputTokens:N0} | {m.CacheReadTokens:N0} | {m.OutputTokens:N0} | {Cost(m.CostUsd)} | {m.FirstContext:N0} | {m.LastContext:N0} | {m.ContextGrowth:N0} | {m.CodeGraphCalls} | {m.OtherToolCalls} | {m.Denied} | {(run.Valid ? "yes" : "no: " + string.Join("; ", run.Problems))} |"));
        }

        var with = runs.Where(r => r.Valid && r.Condition == With).Select(r => r.Metrics).ToList();
        var without = runs.Where(r => r.Valid && r.Condition == Without).Select(r => r.Metrics).ToList();
        var withWall = runs.Where(r => r.Valid && r.Condition == With).Select(r => r.WallSeconds).ToList();
        var withoutWall = runs.Where(r => r.Valid && r.Condition == Without).Select(r => r.WallSeconds).ToList();
        sb.AppendLine();
        if (with.Count == 0 || without.Count == 0)
        {
            sb.AppendLine($"No medians: counted runs with {with.Count}, without {without.Count}.");
        }
        else
        {
            sb.AppendLine($"Medians of the counted runs (with: {with.Count}, without: {without.Count}):");
            sb.AppendLine();
            sb.AppendLine("| | with | without |");
            sb.AppendLine("|---|---|---|");
            sb.AppendLine(Invariant($"| wall s | {Median(withWall):F1} | {Median(withoutWall):F1} |"));
            Row(sb, "model calls", with, without, m => m.ModelCalls);
            Row(sb, "input tokens", with, without, m => m.InputTokens);
            Row(sb, "cache reads", with, without, m => m.CacheReadTokens);
            Row(sb, "output tokens", with, without, m => m.OutputTokens);
            var withCost = with.Where(m => m.CostUsd is not null).Select(m => m.CostUsd!.Value).ToList();
            var withoutCost = without.Where(m => m.CostUsd is not null).Select(m => m.CostUsd!.Value).ToList();
            if (withCost.Count > 0 && withoutCost.Count > 0)
            {
                sb.AppendLine(Invariant($"| cost USD | {Median(withCost):F4} | {Median(withoutCost):F4} |"));
            }

            Row(sb, "first context", with, without, m => m.FirstContext);
            Row(sb, "last context", with, without, m => m.LastContext);
            Row(sb, "context growth", with, without, m => m.ContextGrowth);
            Row(sb, "tool calls", with, without, m => m.CodeGraphCalls + m.OtherToolCalls);
        }

        sb.AppendLine();
        sb.AppendLine($"{runs.Count(r => r.Condition == With)} run(s) with and {runs.Count(r => r.Condition == Without)} without, one prompt, one repository: " +
                      "a direction, not a general result. The answers are not scored; read them in report.md before trusting a faster or cheaper run.");
        sb.Append($"Logs: {outDir}");
        return sb.ToString();
    }

    private static string Answers(IReadOnlyList<CompareRun> runs)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## Answers");
        foreach (var run in runs)
        {
            sb.AppendLine();
            sb.AppendLine($"### Round {run.Round}, {run.Condition}");
            sb.AppendLine();
            sb.AppendLine("Tools: " + (run.Metrics.ToolCalls.Count == 0 ? "none" : string.Join(", ", run.Metrics.ToolCalls.Select(t => $"{t.Key} ×{t.Value}"))));
            sb.AppendLine();
            sb.AppendLine(run.Metrics.Answer ?? "(no answer in the log)");
        }

        return sb.ToString();
    }

    private static void Row(StringBuilder sb, string label, List<RunMetrics> with, List<RunMetrics> without, Func<RunMetrics, long> value) =>
        sb.AppendLine(Invariant($"| {label} | {Median(with.Select(m => (double)value(m)).ToList()):N0} | {Median(without.Select(m => (double)value(m)).ToList()):N0} |"));

    internal static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static string Cost(double? cost) => cost is null ? "?" : cost.Value.ToString("F4", CultureInfo.InvariantCulture);

    private static string Short(string? commit) => commit is null ? "?" : commit[..Math.Min(7, commit.Length)];

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
