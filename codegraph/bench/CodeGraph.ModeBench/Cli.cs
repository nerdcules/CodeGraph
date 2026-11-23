using CodeGraph.Contracts;

namespace CodeGraph.ModeBench;

internal static class Cli
{
    public const string Usage =
        """
        codegraph-modebench <command> [--config bench/bench.config.json]
          switch <direct|lite|lite-cli> <dir>   render that mode's profile into a working copy (one line to switch)
          overhead [--compile-error FILE] [--out DIR]   static tokens per mode (writes overhead.json and overhead.md)
          tokens <file>                         how many tokens a file's text is, with the pinned model
          truth [--tasks id,...]                compute and print each task's ground truth from the index
          run [--mode direct,lite,lite-cli] [--tasks id,...] [--category c,...] [--reps N] [--no-baseline] [--out DIR]
                                                headless Claude Code once per task x mode x repetition, interleaved
          run --pilot                           one task per category, every mode, 2 repetitions, then the full-matrix estimate
          report <results dir>                  rewrite summary.csv and report.md from runs.jsonl
          reshape <results dir>                 recompute the question-shape fields from the saved transcripts, then report
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        var rest = args.ToList();
        var configPath = Take(rest, "--config");
        if (rest.Count == 0 || rest[0] is "-h" or "--help")
        {
            Console.WriteLine(Usage);
            return rest.Count == 0 ? 2 : 0;
        }

        var config = BenchConfig.Load(configPath);
        switch (rest[0])
        {
            case "switch" when rest.Count == 3:
                var files = Profiles.Render(config, rest[1], rest[2]);
                Console.WriteLine($"{Path.GetFullPath(rest[2])} is now in {CodeGraphMode.Parse(rest[1])} mode: {string.Join(", ", files)}");
                return 0;
            case "overhead":
                return await Overhead.MeasureAsync(config, Take(rest, "--out") ?? Path.Combine(config.BenchDir, "results", "overhead-" + Stamp()),
                    Take(rest, "--compile-error"), CancellationToken.None);
            case "tokens" when rest.Count == 2:
                Console.WriteLine(await Overhead.TextTokensAsync(config, await File.ReadAllTextAsync(rest[1]), CancellationToken.None));
                return 0;
            case "truth":
            {
                var (_, all) = Tasks.Load(config);
                var ids = Take(rest, "--tasks")?.Split(',');
                using var graph = CodeGraph.Client.CodeGraphClient.Open(config.Db);
                foreach (var task in all.Where(t => ids is null || ids.Contains(t.Id)))
                {
                    var truth = await Tasks.TruthAsync(graph, task);
                    Console.WriteLine($"## {task.Id} ({task.Category}): {truth.Items.Count} item(s); {truth.How}");
                    foreach (var item in truth.Items)
                    {
                        Console.WriteLine($"  {item}{(truth.Counts?.TryGetValue(item, out var c) == true ? $": {c}" : "")}");
                    }
                }

                return 0;
            }

            case "run":
            {
                var (_, all) = Tasks.Load(config);
                var pilot = rest.Remove("--pilot");
                var baseline = !rest.Remove("--no-baseline");
                var modes = (Take(rest, "--mode") ?? string.Join(',', CodeGraphMode.All)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(CodeGraphMode.Parse).Distinct().ToList();
                var ids = Take(rest, "--tasks")?.Split(',');
                var categories = Take(rest, "--category")?.Split(',');
                var reps = Take(rest, "--reps") is { } r ? int.Parse(r, System.Globalization.CultureInfo.InvariantCulture) : pilot ? 2 : config.Repetitions;
                var tasks = pilot
                    ? all.GroupBy(t => t.Category).Select(g => g.First()).ToList()
                    : all.Where(t => (ids is null || ids.Contains(t.Id)) && (categories is null || categories.Contains(t.Category))).ToList();
                var outDir = Take(rest, "--out") ?? Path.Combine(config.BenchDir, "results", (pilot ? "pilot-" : "") + Stamp());
                if (rest.Count > 1)
                {
                    Console.Error.WriteLine($"unknown arguments: {string.Join(' ', rest.Skip(1))}\n{Usage}");
                    return 2;
                }

                var code = await Bench.RunAsync(config, new Bench.Plan(tasks, modes, reps, baseline), outDir, CancellationToken.None);
                if (pilot)
                {
                    var estimate = Report.Estimate(config, Report.Read(outDir), all);
                    await File.WriteAllTextAsync(Path.Combine(outDir, "estimate.md"), estimate + "\n");
                    Report.Write(config, outDir, Report.Read(outDir), Report.ReadTruth(outDir), Report.ReadPlan(config, outDir));
                    Console.WriteLine(estimate);
                }

                return code;
            }

            case "report" when rest.Count == 2:
                Report.Write(config, rest[1], Report.Read(rest[1]), Report.ReadTruth(rest[1]), Report.ReadPlan(config, rest[1]));
                Console.WriteLine(Path.Combine(rest[1], "report.md"));
                return 0;
            case "reshape" when rest.Count == 2:
            {
                var (answerFormat, all) = Tasks.Load(config);
                var records = Report.Read(rest[1]).Select(r =>
                {
                    var transcript = Path.Combine(rest[1], "runs", r.RunId + ".stream.jsonl");
                    if (!File.Exists(transcript))
                    {
                        Console.Error.WriteLine($"{r.RunId}: no transcript; shape left as recorded");
                        return r;
                    }

                    var task = r.Task == BenchTask.Baseline ? BenchTask.NoOp : all.Single(t => t.Id == r.Task);
                    var shape = Bench.Shape(ClaudeRunner.Replay(transcript).Tools, task.FullPrompt(answerFormat));
                    return r with { GraphCalls = shape.GraphCalls, LargestResultChars = shape.LargestResultChars, TotalResultChars = shape.TotalResultChars, Chained = shape.Chained };
                }).ToList();
                await File.WriteAllLinesAsync(Path.Combine(rest[1], "runs.jsonl"), records.Select(r => System.Text.Json.JsonSerializer.Serialize(r)));
                await File.WriteAllLinesAsync(Path.Combine(rest[1], "raw.csv"), records.Select(Csv.Row).Prepend(Csv.Header));
                Report.Write(config, rest[1], records, Report.ReadTruth(rest[1]), Report.ReadPlan(config, rest[1]));
                Console.WriteLine(Path.Combine(rest[1], "report.md"));
                return 0;
            }

            default:
                Console.Error.WriteLine(Usage);
                return 2;
        }
    }

    public static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

    public static string? Take(List<string> args, string name)
    {
        var i = args.IndexOf(name);
        if (i < 0 || i + 1 >= args.Count)
        {
            return null;
        }

        var value = args[i + 1];
        args.RemoveRange(i, 2);
        return value;
    }
}
