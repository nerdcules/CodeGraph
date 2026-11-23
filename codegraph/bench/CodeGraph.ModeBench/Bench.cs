using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeGraph.Client;
using CodeGraph.Contracts;

namespace CodeGraph.ModeBench;

/// <summary>One run's record: the session's figures from stream-json, what it did, and how its answer scored.</summary>
internal sealed record RunRecord(
    string RunId,
    string StartedAt,
    string Task,
    string Category,
    string Mode,
    int Repetition,
    string Model,
    string? Subtype,
    bool IsError,
    bool TimedOut,
    string? Failure,
    long? InputTokens,
    long? OutputTokens,
    long? CacheReadTokens,
    long? CacheCreationTokens,
    long? TotalTokens,
    double? CostUsd,
    int? Turns,
    int ApiCalls,
    long? FirstContextTokens,
    double WallSeconds,
    IReadOnlyDictionary<string, int> ToolCalls,
    int Scripts,
    int CliInvocations,
    int CompileFailures,
    double SecondsInScriptsOrCli,
    int PermissionDenials,
    bool RepoChanged,
    double? Score,
    double? Precision,
    double? Recall,
    double? CountAccuracy,
    int AnswerItems,
    int TruthItems,
    string? Answer,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> MissingFields,
    int GraphCalls = 0,
    long? LargestResultChars = null,
    long? TotalResultChars = null,
    bool? Chained = null);

internal static partial class Bench
{
    public sealed record Plan(IReadOnlyList<BenchTask> Tasks, IReadOnlyList<string> Modes, int Repetitions, bool Baseline);

    public static async Task<int> RunAsync(BenchConfig config, Plan plan, string outDir, CancellationToken cancellationToken)
    {
        var (answerFormat, _) = Tasks.Load(config);
        Directory.CreateDirectory(Path.Combine(outDir, "runs"));
        Preflight(config);

        // Ground truth first, from the index the agents read; written down before any agent runs.
        var truths = new Dictionary<string, Truth>();
        using (var graph = CodeGraphClient.Open(config.Db))
        {
            foreach (var task in plan.Tasks)
            {
                truths[task.Id] = await Tasks.TruthAsync(graph, task);
                Console.WriteLine($"truth {task.Id}: {truths[task.Id].Items.Count} item(s) ({truths[task.Id].How})");
            }
        }

        await File.WriteAllTextAsync(Path.Combine(outDir, "truth.json"), JsonSerializer.Serialize(truths, Indented), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(outDir, "plan.json"), JsonSerializer.Serialize(new
        {
            config.Model, RepoPath = BenchConfig.Shown(config.RepoPath), config.Commit, Db = BenchConfig.Shown(config.Db), config.MaxTurns, config.TimeoutSeconds,
            plan.Modes, plan.Repetitions, plan.Baseline, Tasks = plan.Tasks.Select(t => t.Id),
            ClaudeVersion = Version(config.Claude, "--version"), DotnetVersion = Version(config.Dotnet, "--version"),
            // The profiles, tasks and harness this run used: the CodeGraph commit, and whether bench/ had uncommitted changes.
            BenchCommit = Git(config.BenchDir, "rev-parse", "HEAD").Trim(),
            BenchDirty = Git(config.BenchDir, "status", "--porcelain", "--", ".").Length > 0,
        }, Indented), cancellationToken);

        var work = Path.Combine(config.WorkRoot, Path.GetFileName(outDir));
        var records = new List<RunRecord>();
        var jobs = new List<(BenchTask Task, string Mode, int Rep)>();
        for (var rep = 1; rep <= plan.Repetitions; rep++)
        {
            // Interleaved: every mode runs each task before the next repetition starts, so drift hits all modes alike.
            foreach (var task in (plan.Baseline ? plan.Tasks.Prepend(BenchTask.NoOp) : plan.Tasks))
            {
                foreach (var mode in plan.Modes)
                {
                    jobs.Add((task, mode, rep));
                }
            }
        }

        var raw = Path.Combine(outDir, "raw.csv");
        await File.WriteAllTextAsync(raw, Csv.Header + "\n", cancellationToken);
        var n = 0;
        foreach (var (task, mode, rep) in jobs)
        {
            n++;
            var runId = $"{task.Id}-{mode}-r{rep}";
            Console.Write($"[{n}/{jobs.Count}] {runId} … ");
            var record = await RunOneAsync(config, task, mode, rep, runId, answerFormat, truths.GetValueOrDefault(task.Id), work, outDir, cancellationToken);
            records.Add(record);
            await File.AppendAllTextAsync(raw, Csv.Row(record) + "\n", cancellationToken);
            await File.AppendAllTextAsync(Path.Combine(outDir, "runs.jsonl"), JsonSerializer.Serialize(record) + "\n", cancellationToken);
            Console.WriteLine($"{record.Failure ?? "ok"}  tokens {record.TotalTokens?.ToString("N0", CultureInfo.InvariantCulture) ?? "null"}  "
                              + $"${record.CostUsd?.ToString("F4", CultureInfo.InvariantCulture) ?? "null"}  turns {record.Turns?.ToString(CultureInfo.InvariantCulture) ?? "null"}  "
                              + $"{record.WallSeconds:F1}s  score {record.Score?.ToString("F2", CultureInfo.InvariantCulture) ?? "-"}");
        }

        Report.Write(config, outDir, records, truths, plan);
        Console.WriteLine($"results: {outDir}");
        return 0;
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The same repo commit and the same prebuilt index for every run, checked before the first one.</summary>
    private static void Preflight(BenchConfig config)
    {
        var head = Git(config.RepoPath, "rev-parse", "HEAD").Trim();
        if (head != config.Commit)
        {
            throw new InvalidOperationException($"{config.RepoPath} is at {head}, bench.config.json pins {config.Commit}");
        }

        using var graph = CodeGraphClient.Open(config.Db);
        var status = graph.StatusAsync().GetAwaiter().GetResult();
        if (status.Commit != config.Commit)
        {
            throw new InvalidOperationException($"{config.Db} was built at {status.Commit}, bench.config.json pins {config.Commit}");
        }

        foreach (var file in new[] { config.McpServer, config.Cli, config.McpDotnet })
        {
            if (!File.Exists(file))
            {
                throw new FileNotFoundException($"missing {file}; see bench/README.md for the build commands", file);
            }
        }
    }

    private static async Task<RunRecord> RunOneAsync(BenchConfig config, BenchTask task, string mode, int rep, string runId,
        string answerFormat, Truth? truth, string work, string outDir, CancellationToken cancellationToken)
    {
        var copy = Path.Combine(work, runId);
        if (Directory.Exists(copy))
        {
            Directory.Delete(copy, recursive: true);
        }

        Profiles.Render(config, mode, copy);
        var before = RepoState(config);
        var started = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var result = await ClaudeRunner.RunAsync(config,
            new ClaudeLaunch(task.FullPrompt(answerFormat), mode, copy, config.MaxTurns, TimeSpan.FromSeconds(config.TimeoutSeconds)),
            Path.Combine(outDir, "runs", runId + ".stream.jsonl"), cancellationToken);
        var changed = RepoState(config) != before;

        // Keep what a lite agent wrote: the scripts are part of the evidence.
        var scratch = Path.Combine(copy, "scratch");
        if (Directory.Exists(scratch))
        {
            foreach (var script in Directory.GetFiles(scratch, "*.cs"))
            {
                File.Copy(script, Path.Combine(outDir, "runs", $"{runId}.{Path.GetFileName(script)}"), overwrite: true);
            }
        }

        Directory.Delete(copy, recursive: true);

        var score = truth is null || task.Id == BenchTask.Baseline ? null : Scorer.Rate(result.Answer, truth, task.Match, config.RepoPath);
        var bash = result.Tools.Where(t => t.Name == "Bash").ToList();
        var scripts = bash.Where(t => IsScript(t.Input)).ToList();
        var cli = bash.Where(t => CliCount(t.Input) > 0).ToList();
        var failure = result.TimedOut ? "timeout"
            : result.Subtype == "error_max_turns" ? "max_turns"
            : changed ? "repo_changed"
            : result.IsError || result.ExitCode != 0 ? $"error:{result.Subtype ?? "exit " + result.ExitCode}"
            : null;
        var missing = new List<string>();
        void Need(object? value, string name)
        {
            if (value is null)
            {
                missing.Add(name);
            }
        }

        Need(result.InputTokens, "input_tokens");
        Need(result.OutputTokens, "output_tokens");
        Need(result.CacheReadTokens, "cache_read_input_tokens");
        Need(result.CacheCreationTokens, "cache_creation_input_tokens");
        Need(result.CostUsd, "total_cost_usd");
        Need(result.Turns, "num_turns");
        var shape = Shape(result.Tools, task.FullPrompt(answerFormat));
        return new RunRecord(
            runId, started, task.Id, task.Category, mode, rep, config.Model, result.Subtype, result.IsError, result.TimedOut, failure,
            result.InputTokens, result.OutputTokens, result.CacheReadTokens, result.CacheCreationTokens, result.TotalTokens,
            result.CostUsd, result.Turns, result.ApiCalls, result.FirstContextTokens, Math.Round(result.WallSeconds, 2),
            result.Tools.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.Count()),
            scripts.Count,
            cli.Sum(t => CliCount(t.Input)),
            scripts.Count(t => IsCompileFailure(t.Result)),
            Math.Round(scripts.Concat(cli).Distinct().Sum(t => t.Seconds), 2),
            result.PermissionDenials.Count,
            changed,
            score?.F1, score?.Precision, score?.Recall, score?.CountAccuracy, score?.AnswerItems ?? 0, score?.TruthItems ?? 0,
            result.Answer, result.Models, missing,
            shape.GraphCalls, shape.LargestResultChars, shape.TotalResultChars, shape.Chained);
    }

    internal static bool IsScript(string? command) => QuestionShape.IsScript(command);

    /// <summary>How many pipeline segments of a command start with the codegraph CLI.</summary>
    internal static int CliCount(string? command) => QuestionShape.CliCount(command);

    internal static bool IsCompileFailure(string result) =>
        result.Contains(": error CS", StringComparison.Ordinal) || result.Contains("The build failed", StringComparison.Ordinal);

    internal static QuestionShape Shape(IReadOnlyList<ToolCall> tools, string prompt) =>
        QuestionShape.Of(tools.Select(t => new ShapeCall(t.Name, t.Name == "Bash" ? t.Input : null, t.Arguments, t.Result)).ToList(), prompt);

    /// <summary>The repo as git sees it (HEAD, status) plus the graph file's size and time: any write shows here.</summary>
    private static string RepoState(BenchConfig config)
    {
        var db = new FileInfo(config.Db);
        return Git(config.RepoPath, "rev-parse", "HEAD") + Git(config.RepoPath, "status", "--porcelain=v1", "--untracked-files=all")
            + $"{db.Length}:{db.LastWriteTimeUtc.Ticks}";
    }

    private static string Git(string directory, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private static string Version(string command, string arg)
    {
        try
        {
            var info = new ProcessStartInfo(command, arg) { RedirectStandardOutput = true };
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return output;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "unknown";
        }
    }
}

internal static class Csv
{
    public const string Header =
        "run_id,started_at,task,category,mode,rep,model,subtype,failure,input_tokens,output_tokens,cache_read_tokens,cache_creation_tokens,total_tokens,"
        + "cost_usd,turns,api_calls,first_context_tokens,wall_s,tool_calls,scripts,cli_invocations,compile_failures,s_in_scripts_or_cli,"
        + "permission_denials,repo_changed,score,precision,recall,count_accuracy,answer_items,truth_items,missing_fields,"
        + "graph_calls,largest_result_chars,total_result_chars,chained";

    public static string Row(RunRecord r) => string.Join(',',
        r.RunId, r.StartedAt, r.Task, r.Category, r.Mode, N(r.Repetition), r.Model, Q(r.Subtype), Q(r.Failure),
        N(r.InputTokens), N(r.OutputTokens), N(r.CacheReadTokens), N(r.CacheCreationTokens), N(r.TotalTokens),
        N(r.CostUsd), N(r.Turns), N(r.ApiCalls), N(r.FirstContextTokens), N(r.WallSeconds),
        Q(string.Join(' ', r.ToolCalls.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"))),
        N(r.Scripts), N(r.CliInvocations), N(r.CompileFailures), N(r.SecondsInScriptsOrCli), N(r.PermissionDenials),
        r.RepoChanged ? "true" : "false", N(r.Score), N(r.Precision), N(r.Recall), N(r.CountAccuracy), N(r.AnswerItems), N(r.TruthItems),
        Q(string.Join(' ', r.MissingFields)),
        N(r.GraphCalls), N(r.LargestResultChars), N(r.TotalResultChars), r.Chained is { } c ? (c ? "true" : "false") : "null");

    /// <summary>A number, or "null" when the CLI did not print the field.</summary>
    public static string N<T>(T? value) where T : struct, IFormattable =>
        value is { } v ? v.ToString(null, CultureInfo.InvariantCulture) : "null";

    public static string N<T>(T value) where T : struct, IFormattable => value.ToString(null, CultureInfo.InvariantCulture);

    public static string Q(string? text) =>
        text is null ? "" : text.Contains(',') || text.Contains('"') || text.Contains('\n') ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
}

internal static class Stats
{
    public static double? Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return null;
        }

        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    public static string Range(IEnumerable<double> values, string format)
    {
        var list = values.ToList();
        return list.Count == 0 ? "–" : string.Create(CultureInfo.InvariantCulture,
            $"{Median(list)!.Value.ToString(format, CultureInfo.InvariantCulture)} ({list.Min().ToString(format, CultureInfo.InvariantCulture)}–{list.Max().ToString(format, CultureInfo.InvariantCulture)})");
    }

    public static string Text(StringBuilder sb) => sb.ToString();
}
