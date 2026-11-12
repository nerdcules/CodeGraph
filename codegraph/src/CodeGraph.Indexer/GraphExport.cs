using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Core;

namespace CodeGraph.Indexer;

/// <summary>
/// <c>codegraph-index export</c>: the graph's nodes and edges as the newline-delimited records <c>import</c>
/// reads (EXTRACTORS.md), one language or all. For diffing two graphs, checking an extractor against an answer
/// key, or feeding the graph to another tool.
/// </summary>
internal static class ExportCommand
{
    public static int Run(string[] args, TextWriter stdout, Action<string> log)
    {
        if (args is ["obsidian", .. var obsidianArgs])
        {
            return ObsidianExport.Run(obsidianArgs, stdout, log);
        }

        string? db = null;
        string? language = null;
        string? output = null;
        var deadCode = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db" when i + 1 < args.Length:
                    db = args[++i];
                    break;
                case "--language" when i + 1 < args.Length:
                    language = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case "--dead-code":
                    deadCode = true;
                    break;
                case "-h" or "--help":
                    Console.Error.WriteLine("usage: codegraph-index export [--db <path>] [--language <name>] [--out <file>] [--dead-code]");
                    Console.Error.WriteLine("       writes the graph's nodes and edges as import records (EXTRACTORS.md) to stdout or a file;");
                    Console.Error.WriteLine("       --dead-code adds a {\"type\":\"dead\",\"node\":id} record per dead_code_report candidate, public API included");
                    Console.Error.WriteLine("       codegraph-index export obsidian --out <folder>  writes notes for an Obsidian vault (export obsidian --help)");
                    return 1;
                default:
                    Console.Error.WriteLine($"unexpected argument: {args[i]}");
                    return 1;
            }
        }

        var dbPath = db is not null ? Path.GetFullPath(db) : Worktree.DefaultDbPath(Directory.GetCurrentDirectory());
        if (!File.Exists(dbPath))
        {
            log($"export: no graph at {dbPath}");
            return 1;
        }

        using var store = GraphStore.Open(dbPath, readOnly: true, TimeSpan.FromSeconds(30));
        var nodes = store.AllNodes().Where(n => language is null || n.Language == language).ToList();
        var ids = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var edges = store.AllEdges().Where(e => language is null || ids.Contains(e.FromId)).ToList();

        using var file = output is null ? null : new StreamWriter(output);
        var writer = file ?? stdout;
        foreach (var node in nodes)
        {
            var record = new JsonObject
            {
                ["type"] = "node",
                ["id"] = node.Id,
                ["kind"] = node.Kind.ToString(),
                ["name"] = node.Name,
                ["fullName"] = node.FullName,
                ["signature"] = node.Signature,
                ["file"] = node.FilePath,
                ["start"] = node.StartLine,
                ["end"] = node.EndLine,
                ["isTest"] = node.IsTest,
                ["typeKind"] = node.TypeKind.ToString(),
                ["accessibility"] = node.Accessibility.ToString(),
                ["static"] = node.IsStatic,
                ["abstract"] = node.IsAbstract,
                ["virtual"] = node.IsVirtual,
                ["sealed"] = node.IsSealed,
                ["async"] = node.IsAsync,
                ["override"] = node.IsOverride,
                ["memberKind"] = node.MemberKind.ToString(),
                ["language"] = node.Language,
            };
            writer.WriteLine(record.ToJsonString());
        }

        foreach (var edge in edges)
        {
            writer.WriteLine(JsonSerializer.Serialize(new { type = "edge", from = edge.FromId, to = edge.ToId, kind = edge.Kind.ToString() }));
        }

        var dead = 0;
        if (deadCode)
        {
            for (var offset = 0; ; offset += 500)
            {
                var (_, candidates) = store.DeadCodeReport(includePublicApi: true, limit: 500, offset: offset);
                foreach (var candidate in candidates.Where(c => ids.Contains(c.Method.Id)))
                {
                    writer.WriteLine(JsonSerializer.Serialize(new { type = "dead", node = candidate.Method.Id }));
                    dead++;
                }

                if (candidates.Count < 500)
                {
                    break;
                }
            }
        }

        writer.Flush();
        log($"exported {nodes.Count} nodes, {edges.Count} edges{(deadCode ? $", {dead} dead-code candidates" : "")} from {dbPath}");
        return 0;
    }
}
