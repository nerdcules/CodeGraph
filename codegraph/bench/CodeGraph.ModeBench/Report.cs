using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Contracts;

namespace CodeGraph.ModeBench;

/// <summary>
/// summary.csv and report.md from the run records. A run counts toward the token comparison only if it finished
/// and its score is at least the median score of the direct runs of the same task; a finished run below that is
/// "cheaper but worse" and listed apart. A lite mode wins or loses a category only when the min–max ranges of the
/// counted runs do not overlap; otherwise the data is too noisy to say.
/// </summary>
internal static class Report
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void Write(BenchConfig config, string outDir, IReadOnlyList<RunRecord> records, IReadOnlyDictionary<string, Truth> truths, Bench.Plan plan)
    {
        var bar = DirectBar(records);
        bool Counted(RunRecord r) => r.Failure is null && (r.Task == BenchTask.Baseline || (r.Score is { } s && s >= bar.GetValueOrDefault(r.Task, 0)));
        var categories = records.Select(r => r.Category).Distinct().OrderBy(c => c == BenchTask.Baseline ? "~" : c, StringComparer.Ordinal).ToList();
        var modes = CodeGraphMode.All.Where(m => records.Any(r => r.Mode == m)).ToList();

        // summary.csv: one row per category x mode.
        var csv = new StringBuilder("category,mode,runs,finished,counted,cheaper_but_worse,tokens_median,tokens_min,tokens_max,cost_median,cost_min,cost_max,"
                                    + "turns_median,turns_min,turns_max,wall_median,wall_min,wall_max,score_median,score_min,score_max,scripts,cli_invocations,compile_failures,s_in_scripts_or_cli\n");
        foreach (var category in categories)
        {
            foreach (var mode in modes)
            {
                var all = records.Where(r => r.Category == category && r.Mode == mode).ToList();
                var counted = all.Where(Counted).ToList();
                var scores = all.Where(r => r.Score is not null).Select(r => r.Score!.Value).ToList();
                csv.AppendLine(string.Join(',', category, mode, all.Count, all.Count(r => r.Failure is null), counted.Count,
                    all.Count(r => r.Failure is null && !Counted(r)),
                    Triple(counted.Where(r => r.TotalTokens is not null).Select(r => (double)r.TotalTokens!.Value)),
                    Triple(counted.Where(r => r.CostUsd is not null).Select(r => r.CostUsd!.Value)),
                    Triple(counted.Where(r => r.Turns is not null).Select(r => (double)r.Turns!.Value)),
                    Triple(counted.Select(r => r.WallSeconds)),
                    Triple(scores),
                    all.Sum(r => r.Scripts), all.Sum(r => r.CliInvocations), all.Sum(r => r.CompileFailures),
                    all.Sum(r => r.SecondsInScriptsOrCli).ToString("F1", Inv)));
            }
        }

        File.WriteAllText(Path.Combine(outDir, "summary.csv"), csv.ToString());

        var md = new StringBuilder();
        md.AppendLine("# CodeGraph lite benchmark");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"Model `{config.Model}`; repo `{BenchConfig.Shown(config.RepoPath)}` at `{config.Commit}`; index `{BenchConfig.Shown(config.Db)}` (prebuilt, index time excluded); "
                       + $"{plan.Repetitions} repetition(s), interleaved across modes; turn cap {config.MaxTurns}; timeout {config.TimeoutSeconds} s. "
                       + $"Tasks: {string.Join(", ", plan.Tasks.Select(t => $"{t.Id} ({t.Category})"))}{(plan.Baseline ? "; baseline no-op prompt" : "")}.");
        md.AppendLine();
        md.AppendLine("Total tokens = input + output + cache read + cache creation, as the claude CLI's result message reports them. "
                      + "Figures are median (min–max) over the counted runs: finished, and scoring at least the median direct score of the same task.");
        md.AppendLine();

        md.AppendLine("## Tokens, cost, turns and wall time per category and mode");
        md.AppendLine();
        md.AppendLine("| category | mode | runs | counted | total tokens | cost USD | turns | wall s |");
        md.AppendLine("|---|---|---:|---:|---|---|---|---|");
        foreach (var category in categories)
        {
            foreach (var mode in modes)
            {
                var all = records.Where(r => r.Category == category && r.Mode == mode).ToList();
                var counted = all.Where(Counted).ToList();
                md.AppendLine(CultureInfo.InvariantCulture, $"| {category} | {mode} | {all.Count} | {counted.Count} | "
                    + $"{Stats.Range(counted.Where(r => r.TotalTokens is not null).Select(r => (double)r.TotalTokens!.Value), "N0")} | "
                    + $"{Stats.Range(counted.Where(r => r.CostUsd is not null).Select(r => r.CostUsd!.Value), "F4")} | "
                    + $"{Stats.Range(counted.Where(r => r.Turns is not null).Select(r => (double)r.Turns!.Value), "F0")} | "
                    + $"{Stats.Range(counted.Select(r => r.WallSeconds), "F1")} |");
            }
        }

        md.AppendLine();
        md.AppendLine("## Change from direct (median total tokens and cost of counted runs)");
        md.AppendLine();
        md.AppendLine("| category | mode | tokens change | cost change | runs (direct / mode) |");
        md.AppendLine("|---|---|---:|---:|---|");
        foreach (var category in categories)
        {
            var direct = records.Where(r => r.Category == category && r.Mode == CodeGraphMode.Direct && Counted(r)).ToList();
            foreach (var mode in modes.Where(m => m != CodeGraphMode.Direct))
            {
                var lite = records.Where(r => r.Category == category && r.Mode == mode && Counted(r)).ToList();
                md.AppendLine(CultureInfo.InvariantCulture, $"| {category} | {mode} | {Change(direct, lite, r => r.TotalTokens)} | {Change(direct, lite, r => r.CostUsd)} | {direct.Count} / {lite.Count} |");
            }
        }

        md.AppendLine();
        md.AppendLine("## Correctness (F1 on the listed items)");
        md.AppendLine();
        md.AppendLine("| category | mode | scored runs | F1 median (min–max) | finished but failed the bar |");
        md.AppendLine("|---|---|---:|---|---:|");
        foreach (var category in categories.Where(c => c != BenchTask.Baseline))
        {
            foreach (var mode in modes)
            {
                var scored = records.Where(r => r.Category == category && r.Mode == mode && r.Score is not null).ToList();
                md.AppendLine(CultureInfo.InvariantCulture, $"| {category} | {mode} | {scored.Count} | {Stats.Range(scored.Select(r => r.Score!.Value), "F2")} | "
                    + $"{scored.Count(r => r.Failure is null && !Counted(r))} |");
            }
        }

        md.AppendLine();
        md.AppendLine("Median direct score per task (the bar): " + string.Join(", ", bar.Select(kv => $"{kv.Key} {kv.Value.ToString("F2", Inv)}")) + ".");
        md.AppendLine();
        md.AppendLine("Ground truth items per task: " + string.Join(", ", truths.Where(t => t.Key != BenchTask.Baseline).Select(t => $"{t.Key} {t.Value.Items.Count}")) + ".");
        md.AppendLine();

        var worse = records.Where(r => r.Failure is null && !Counted(r)).ToList();
        md.AppendLine("## Cheaper but worse");
        md.AppendLine();
        if (worse.Count == 0)
        {
            md.AppendLine("No finished run scored below the direct median of its task.");
        }
        else
        {
            md.AppendLine("| run | score | bar | total tokens | cost USD |");
            md.AppendLine("|---|---:|---:|---:|---:|");
            foreach (var r in worse)
            {
                md.AppendLine(CultureInfo.InvariantCulture, $"| {r.RunId} | {r.Score?.ToString("F2", Inv) ?? "-"} | {bar.GetValueOrDefault(r.Task).ToString("F2", Inv)} | {r.TotalTokens?.ToString("N0", Inv) ?? "null"} | {r.CostUsd?.ToString("F4", Inv) ?? "null"} |");
            }
        }

        md.AppendLine();
        var failed = records.Where(r => r.Failure is not null).ToList();
        md.AppendLine("## Failures (recorded, not retried)");
        md.AppendLine();
        md.AppendLine(failed.Count == 0 ? "None." : string.Join("\n", failed.Select(r => $"- {r.RunId}: {r.Failure}")));
        md.AppendLine();
        var missing = records.Where(r => r.MissingFields.Count > 0).ToList();
        md.AppendLine("Fields missing from the CLI's JSON (logged as null): " + (missing.Count == 0 ? "none." : string.Join("; ", missing.Select(r => $"{r.RunId}: {string.Join(' ', r.MissingFields)}"))));
        md.AppendLine();

        md.AppendLine("## Scripts and CLI calls in the lite modes");
        md.AppendLine();
        md.AppendLine("| mode | runs | scripts run | compile failures | CLI invocations | time inside scripts or CLI, s (sum) | median per run, s |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var mode in modes.Where(m => m != CodeGraphMode.Direct))
        {
            var runs = records.Where(r => r.Mode == mode).ToList();
            var scripts = runs.Sum(r => r.Scripts);
            md.AppendLine(CultureInfo.InvariantCulture, $"| {mode} | {runs.Count} | {scripts} | {runs.Sum(r => r.CompileFailures)}{(scripts > 0 ? $" ({100.0 * runs.Sum(r => r.CompileFailures) / scripts:F0}%)" : "")} | "
                + $"{runs.Sum(r => r.CliInvocations)} | {runs.Sum(r => r.SecondsInScriptsOrCli):F1} | {Stats.Median(runs.Select(r => r.SecondsInScriptsOrCli))?.ToString("F1", Inv) ?? "–"} |");
        }

        md.AppendLine();
        md.AppendLine("Tool calls by type, summed per mode: " + string.Join("; ", modes.Select(m =>
            $"{m}: " + string.Join(", ", records.Where(r => r.Mode == m).SelectMany(r => r.ToolCalls).GroupBy(kv => kv.Key).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Key} {g.Sum(kv => kv.Value)}")))) + ".");
        md.AppendLine();
        Shape(md, records, plan, modes, Counted);
        AppendFile(md, "## Static overhead (Stage 4)", Latest(config, "overhead-*", "overhead.md"));
        AppendFile(md, "## Start-up (Stages 2 and 3)", Path.Combine(config.BenchDir, "results", "startup.md"));

        md.AppendLine("## Conclusion (computed from the ranges above)");
        md.AppendLine();
        foreach (var category in categories.Where(c => c != BenchTask.Baseline))
        {
            var direct = Tokens(records, category, CodeGraphMode.Direct, Counted);
            foreach (var mode in modes.Where(m => m != CodeGraphMode.Direct))
            {
                md.AppendLine(CultureInfo.InvariantCulture, $"- {category}, {mode} vs direct: {Verdict(direct, Tokens(records, category, mode, Counted))}");
            }

            if (modes.Contains(CodeGraphMode.Lite) && modes.Contains(CodeGraphMode.LiteCli))
            {
                md.AppendLine(CultureInfo.InvariantCulture, $"- {category}, lite-cli vs lite: {Verdict(Tokens(records, category, CodeGraphMode.Lite, Counted), Tokens(records, category, CodeGraphMode.LiteCli, Counted))}");
            }
        }

        var written = Path.Combine(outDir, "conclusion.md");
        if (File.Exists(written))
        {
            md.AppendLine();
            md.AppendLine(File.ReadAllText(written).Trim());
            md.AppendLine();
        }

        var estimate = Path.Combine(outDir, "estimate.md");
        if (File.Exists(estimate))
        {
            md.AppendLine();
            md.AppendLine("## Full-matrix estimate");
            md.AppendLine();
            md.AppendLine(File.ReadAllText(estimate).Trim());
        }

        File.WriteAllText(Path.Combine(outDir, "report.md"), md.ToString());
    }

    /// <summary>
    /// Question shape (issue #18). Each task is placed by how direct mode answered it (graph calls, largest result,
    /// chained), since that is what a router would have to predict; then each mode's tokens for that task.
    /// </summary>
    private static void Shape(StringBuilder md, IReadOnlyList<RunRecord> records, Bench.Plan plan, IReadOnlyList<string> modes, Func<RunRecord, bool> counted)
    {
        md.AppendLine("## Question shape (issue #18)");
        md.AppendLine();
        md.AppendLine("A graph step is one codegraph MCP tool call, one `codegraph` CLI invocation or one `dotnet run` of a script. Result size is the "
                      + "characters the step's tool call returned. Chained is a heuristic: a later step's input reuses an id, a handle or a dotted name "
                      + "from an earlier step's result that is not in the prompt. Figures are median (min–max) over all finished runs; tokens over counted runs.");
        md.AppendLine();
        md.AppendLine("| task | mode | runs | graph steps | largest result, chars | total result, chars | chained |");
        md.AppendLine("|---|---|---:|---|---|---|---:|");
        foreach (var task in plan.Tasks)
        {
            foreach (var mode in modes)
            {
                var runs = records.Where(r => r.Task == task.Id && r.Mode == mode && r.Failure is null).ToList();
                md.AppendLine(CultureInfo.InvariantCulture, $"| {task.Id} | {mode} | {runs.Count} | {Stats.Range(runs.Select(r => (double)r.GraphCalls), "F0")} | "
                    + $"{Stats.Range(runs.Where(r => r.LargestResultChars is not null).Select(r => (double)r.LargestResultChars!.Value), "N0")} | "
                    + $"{Stats.Range(runs.Where(r => r.TotalResultChars is not null).Select(r => (double)r.TotalResultChars!.Value), "N0")} | "
                    + $"{runs.Count(r => r.Chained == true)} of {runs.Count} |");
            }
        }

        if (!modes.Contains(CodeGraphMode.Direct))
        {
            md.AppendLine();
            return;
        }

        md.AppendLine();
        md.AppendLine("### Tokens by the shape direct mode needed");
        md.AppendLine();
        md.AppendLine("Tasks ordered by direct mode's median graph steps. A mode is named cheaper only when its token range does not overlap direct's.");
        md.AppendLine();
        md.AppendLine("| task | direct steps | direct largest result | " + string.Join(" | ", modes.Select(m => $"{m} tokens")) + " | " + string.Join(" | ", modes.Where(m => m != CodeGraphMode.Direct).Select(m => $"{m} vs direct")) + " |");
        md.AppendLine("|---|---:|---:|" + string.Concat(modes.Select(_ => "---|")) + string.Concat(modes.Where(m => m != CodeGraphMode.Direct).Select(_ => "---|")));
        var ordered = plan.Tasks
            .Select(t => (Task: t, Direct: records.Where(r => r.Task == t.Id && r.Mode == CodeGraphMode.Direct && r.Failure is null).ToList()))
            .OrderBy(x => Stats.Median(x.Direct.Select(r => (double)r.GraphCalls)) ?? double.MaxValue)
            .ThenBy(x => Stats.Median(x.Direct.Where(r => r.LargestResultChars is not null).Select(r => (double)r.LargestResultChars!.Value)) ?? double.MaxValue);
        foreach (var (task, direct) in ordered)
        {
            List<double> TokensOf(string mode) => records.Where(r => r.Task == task.Id && r.Mode == mode && counted(r) && r.TotalTokens is not null)
                .Select(r => (double)r.TotalTokens!.Value).ToList();
            var steps = Stats.Median(direct.Select(r => (double)r.GraphCalls));
            var largest = Stats.Median(direct.Where(r => r.LargestResultChars is not null).Select(r => (double)r.LargestResultChars!.Value));
            md.AppendLine($"| {task.Id} | {steps?.ToString("F1", Inv) ?? "–"} | {largest?.ToString("N0", Inv) ?? "–"} | "
                + string.Join(" | ", modes.Select(m => Stats.Range(TokensOf(m), "N0"))) + " | "
                + string.Join(" | ", modes.Where(m => m != CodeGraphMode.Direct).Select(m => Short(TokensOf(CodeGraphMode.Direct), TokensOf(m)))) + " |");
        }

        md.AppendLine();
    }

    /// <summary>The verdict in a table cell: fewer, more, or overlap, with the median change.</summary>
    private static string Short(IReadOnlyList<double> direct, IReadOnlyList<double> mode)
    {
        if (direct.Count == 0 || mode.Count == 0)
        {
            return "–";
        }

        var change = ((Stats.Median(mode)!.Value / Stats.Median(direct)!.Value - 1) * 100).ToString("+0;-0", Inv) + "%";
        return mode.Max() < direct.Min() ? $"fewer ({change})" : mode.Min() > direct.Max() ? $"more ({change})" : $"overlap ({change})";
    }

    /// <summary>The median score of each task's finished direct runs.</summary>
    private static Dictionary<string, double> DirectBar(IReadOnlyList<RunRecord> records) =>
        records.Where(r => r.Mode == CodeGraphMode.Direct && r.Failure is null && r.Score is not null)
            .GroupBy(r => r.Task)
            .ToDictionary(g => g.Key, g => Stats.Median(g.Select(r => r.Score!.Value))!.Value);

    private static List<double> Tokens(IReadOnlyList<RunRecord> records, string category, string mode, Func<RunRecord, bool> counted) =>
        records.Where(r => r.Category == category && r.Mode == mode && counted(r) && r.TotalTokens is not null).Select(r => (double)r.TotalTokens!.Value).ToList();

    /// <summary>"b" against "a" on total tokens: a win only when b's max is below a's min, a loss when b's min is above a's max.</summary>
    internal static string Verdict(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return $"no counted runs to compare ({a.Count} / {b.Count})";
        }

        var change = (Stats.Median(b)!.Value / Stats.Median(a)!.Value - 1) * 100;
        var figures = string.Create(Inv, $"median {Stats.Median(a):N0} → {Stats.Median(b):N0} tokens ({change:+0;-0}%), ranges {a.Min():N0}–{a.Max():N0} and {b.Min():N0}–{b.Max():N0}, {a.Count} / {b.Count} runs");
        return b.Max() < a.Min() ? $"fewer tokens, ranges do not overlap: {figures}"
            : b.Min() > a.Max() ? $"more tokens, ranges do not overlap: {figures}"
            : $"ranges overlap, too noisy to say: {figures}";
    }

    private static string Change(IReadOnlyList<RunRecord> direct, IReadOnlyList<RunRecord> lite, Func<RunRecord, double?> value)
    {
        var a = Stats.Median(direct.Select(value).OfType<double>());
        var b = Stats.Median(lite.Select(value).OfType<double>());
        return a is null || b is null || a == 0 ? "–" : ((b.Value / a.Value - 1) * 100).ToString("+0;-0", Inv) + "%";
    }

    private static string Triple(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? ",," : string.Join(',', Stats.Median(list)!.Value.ToString(Inv), list.Min().ToString(Inv), list.Max().ToString(Inv));
    }

    private static string? Latest(BenchConfig config, string pattern, string file)
    {
        var results = Path.Combine(config.BenchDir, "results");
        return Directory.Exists(results)
            ? Directory.GetDirectories(results, pattern).Order(StringComparer.Ordinal).Select(d => Path.Combine(d, file)).LastOrDefault(File.Exists)
            : null;
    }

    private static void AppendFile(StringBuilder md, string heading, string? path)
    {
        md.AppendLine(heading);
        md.AppendLine();
        md.AppendLine(path is not null && File.Exists(path) ? $"From `{Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileName(path)}`:\n\n{File.ReadAllText(path).Trim()}" : "Not measured.");
        md.AppendLine();
    }

    /// <summary>The full-matrix estimate after a pilot: runs, and tokens and cost from the pilot's medians per category and mode.</summary>
    public static string Estimate(BenchConfig config, IReadOnlyList<RunRecord> pilot, IReadOnlyList<BenchTask> allTasks)
    {
        var modes = CodeGraphMode.All;
        var categories = allTasks.GroupBy(t => t.Category).ToDictionary(g => g.Key, g => g.Count());
        categories[BenchTask.Baseline] = 1;
        var runs = categories.Values.Sum() * modes.Count * config.Repetitions;
        double tokens = 0, cost = 0, wall = 0;
        var gaps = new List<string>();
        foreach (var (category, count) in categories)
        {
            foreach (var mode in modes)
            {
                var cell = pilot.Where(r => r.Category == category && r.Mode == mode).ToList();
                if (cell.Count == 0)
                {
                    gaps.Add($"{category}/{mode}");
                    continue;
                }

                var n = count * config.Repetitions;
                tokens += n * (Stats.Median(cell.Where(r => r.TotalTokens is not null).Select(r => (double)r.TotalTokens!.Value)) ?? 0);
                cost += n * (Stats.Median(cell.Where(r => r.CostUsd is not null).Select(r => r.CostUsd!.Value)) ?? 0);
                wall += n * (Stats.Median(cell.Select(r => r.WallSeconds)) ?? 0);
            }
        }

        return FormattableString.Invariant($"Full matrix: {allTasks.Count} tasks + baseline, {modes.Count} modes, {config.Repetitions} repetitions = {runs} runs. ")
            + "Estimated from the pilot's medians per category and mode (each pilot cell is one task, so other tasks in a category may cost more or less): "
            + FormattableString.Invariant($"about {tokens:N0} total tokens, ${cost:F2}, {wall / 3600:F1} h of sequential wall time.")
            + (gaps.Count > 0 ? $" No pilot data for {string.Join(", ", gaps)}." : "");
    }

    public static IReadOnlyList<RunRecord> Read(string outDir) =>
        File.ReadLines(Path.Combine(outDir, "runs.jsonl")).Select(l => JsonSerializer.Deserialize<RunRecord>(l)!).ToList();

    public static Dictionary<string, Truth> ReadTruth(string outDir) =>
        JsonSerializer.Deserialize<Dictionary<string, Truth>>(File.ReadAllText(Path.Combine(outDir, "truth.json")))!;

    public static Bench.Plan ReadPlan(BenchConfig config, string outDir)
    {
        var plan = JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, "plan.json")))!;
        var ids = plan["Tasks"]!.AsArray().Select(t => t!.GetValue<string>()).ToHashSet();
        return new Bench.Plan(Tasks.Load(config).Tasks.Where(t => ids.Contains(t.Id)).ToList(),
            plan["Modes"]!.AsArray().Select(m => m!.GetValue<string>()).ToList(), plan["Repetitions"]!.GetValue<int>(), plan["Baseline"]!.GetValue<bool>());
    }
}
