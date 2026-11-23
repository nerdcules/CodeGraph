using System.Text.Json.Nodes;
using CodeGraph.Client;
using CodeGraph.Contracts;

namespace CodeGraph.ModeBench;

/// <summary>A benchmark question: its text, the shape of the answer items, how its ground truth is computed and how answers match.</summary>
internal sealed record BenchTask(string Id, string Category, string Prompt, string Items, JsonObject Truth, string Match)
{
    public const string Baseline = "baseline";

    /// <summary>The no-op prompt: what a mode costs before it does anything.</summary>
    public static readonly BenchTask NoOp = new(Baseline, Baseline, "Reply with the single word OK.", "", new JsonObject { ["kind"] = "none" }, "none");

    /// <summary>The text every mode receives: the question, the item format, and the shared answer format.</summary>
    public string FullPrompt(string answerFormat) =>
        Id == Baseline ? Prompt : $"{Prompt}\n\n{answerFormat} Items: {Items}.";
}

/// <summary>The expected answer: the items, and for project tasks the count each project should have.</summary>
internal sealed record Truth(IReadOnlyList<string> Items, IReadOnlyDictionary<string, int>? Counts, string How);

internal static class Tasks
{
    public static (string AnswerFormat, IReadOnlyList<BenchTask> Tasks) Load(BenchConfig config)
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(config.BenchDir, "tasks.json")))!.AsObject();
        var tasks = json["tasks"]!.AsArray().Select(t => new BenchTask(
            t!["id"]!.GetValue<string>(),
            t["category"]!.GetValue<string>(),
            t["prompt"]!.GetValue<string>(),
            t["items"]!.GetValue<string>(),
            t["truth"]!.AsObject(),
            t["match"]!.GetValue<string>())).ToList();
        return (json["answerFormat"]!.GetValue<string>(), tasks);
    }

    /// <summary>
    /// The ground truth, computed from the index: a reference SQL query, or a check over <see cref="ICodeGraph"/>
    /// (the same API the scripts call, over the same read-only graph). Nothing here asks a model.
    /// </summary>
    public static async Task<Truth> TruthAsync(ICodeGraph graph, BenchTask task)
    {
        var spec = task.Truth;
        string Text(string key) => spec[key]!.GetValue<string>();
        int Number(string key) => spec[key]!.GetValue<int>();
        switch (Text("kind"))
        {
            case "none":
                return new Truth([], null, "no answer");
            case "file":
            {
                var symbol = await ResolveAsync(graph, Text("symbol"));
                return new Truth([symbol.File!], null, $"file of {symbol.Id}");
            }

            case "signature":
            {
                var symbol = await ResolveAsync(graph, Text("symbol"));
                return new Truth([symbol.Signature!], null, $"signature of {symbol.Id}");
            }

            case "impactTests":
            {
                var impact = await graph.ImpactAsync(Text("symbol"), Number("depth"), int.MaxValue);
                return new Truth(impact.Tests.Items.Select(h => h.Symbol.FullName).ToList(), null,
                    $"ImpactAsync({Text("symbol")}, depth {Number("depth")}).Tests; {impact.Dependents.Total} dependents");
            }

            case "callers":
            {
                var walk = await graph.CallersAsync(Text("symbol"), Number("depth"), int.MaxValue);
                return new Truth(walk.Hits.Items.Select(h => h.Symbol.FullName).ToList(), null, $"CallersAsync({Text("symbol")}, depth {Number("depth")})");
            }

            case "callerProjects":
            {
                var walk = await graph.CallersAsync(Text("symbol"), Number("depth"), int.MaxValue);
                var counts = walk.Hits.Items.GroupBy(h => h.Symbol.Project ?? "(none)").ToDictionary(g => g.Key, g => g.Count());
                return new Truth(counts.Keys.Order(StringComparer.Ordinal).ToList(), counts,
                    $"CallersAsync({Text("symbol")}, depth {Number("depth")}) grouped by Symbol.Project; {walk.Hits.Total} callers");
            }

            case "onlyTestCallers":
            {
                var project = Text("project");
                var candidates = await graph.QueryAsync(
                    $"""
                     WITH po AS (SELECT f.file_path, p.name AS project FROM edges e
                         JOIN nodes p ON p.id = e.from_id AND p.kind = 'Project'
                         JOIN nodes f ON f.id = e.to_id AND f.kind = 'File' WHERE e.kind = 'Contains')
                     SELECT m.id FROM nodes m JOIN po ON po.file_path = m.file_path
                     WHERE po.project = '{project.Replace("'", "''", StringComparison.Ordinal)}' AND m.kind = 'Method'
                       AND m.accessibility = 'Public' AND m.member_kind = 'Ordinary' AND NOT m.is_test
                     ORDER BY m.id
                     """, CodeGraphClient.MaxQueryRows, timeoutSeconds: 60);
                var items = new List<string>();
                foreach (var row in candidates.Items)
                {
                    var walk = await graph.CallersAsync((string)row[0]!, 1, int.MaxValue);
                    if (walk.Hits.Total > 0 && walk.Hits.Items.All(h => h.Symbol.IsTest))
                    {
                        items.Add(walk.Target.FullName);
                    }
                }

                return new Truth(items, null, $"public ordinary methods of {project} whose CallersAsync(depth 1) hits are all tests ({candidates.Total} checked)");
            }

            case "sql":
            {
                var result = await graph.QueryAsync(Text("sql"), CodeGraphClient.MaxQueryRows, timeoutSeconds: 60);
                return new Truth(result.Items.Select(r => Convert.ToString(r[0], System.Globalization.CultureInfo.InvariantCulture)!).ToList(), null, "reference SQL in tasks.json");
            }

            case "projectCycles":
            {
                var edges = await graph.QueryAsync(
                    """
                    SELECT a.name, b.name FROM edges e
                    JOIN nodes a ON a.id = e.from_id AND a.kind = 'Project'
                    JOIN nodes b ON b.id = e.to_id AND b.kind = 'Project'
                    WHERE e.kind = 'DependsOn'
                    """, CodeGraphClient.MaxQueryRows);
                var graphOf = edges.Items.GroupBy(r => (string)r[0]!).ToDictionary(g => g.Key, g => g.Select(r => (string)r[1]!).ToList());
                var inCycles = StronglyConnected(graphOf).Where(c => c.Count > 1).SelectMany(c => c).Order(StringComparer.Ordinal).ToList();
                return new Truth(inCycles, null, $"projects in strongly connected components of size > 1 over {edges.Total} DependsOn edges");
            }

            default:
                throw new InvalidDataException($"{task.Id}: unknown truth kind {Text("kind")}");
        }
    }

    private static async Task<Symbol> ResolveAsync(ICodeGraph graph, string symbol) =>
        (await graph.OutlineAsync(symbol, 1)).Target;

    /// <summary>Tarjan's algorithm, iteration-free recursion depth bounded by the project count.</summary>
    internal static List<List<string>> StronglyConnected(IReadOnlyDictionary<string, List<string>> edges)
    {
        var index = new Dictionary<string, int>();
        var low = new Dictionary<string, int>();
        var stack = new Stack<string>();
        var onStack = new HashSet<string>();
        var components = new List<List<string>>();
        var next = 0;
        void Visit(string v)
        {
            index[v] = low[v] = next++;
            stack.Push(v);
            onStack.Add(v);
            foreach (var w in edges.GetValueOrDefault(v) ?? [])
            {
                if (!index.ContainsKey(w))
                {
                    Visit(w);
                    low[v] = Math.Min(low[v], low[w]);
                }
                else if (onStack.Contains(w))
                {
                    low[v] = Math.Min(low[v], index[w]);
                }
            }

            if (low[v] == index[v])
            {
                var component = new List<string>();
                string w2;
                do
                {
                    w2 = stack.Pop();
                    onStack.Remove(w2);
                    component.Add(w2);
                }
                while (w2 != v);
                components.Add(component);
            }
        }

        foreach (var v in edges.Keys.Concat(edges.Values.SelectMany(t => t)).Distinct().ToList())
        {
            if (!index.ContainsKey(v))
            {
                Visit(v);
            }
        }

        return components;
    }
}
