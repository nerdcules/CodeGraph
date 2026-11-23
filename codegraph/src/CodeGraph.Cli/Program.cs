using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CodeGraph.Client;
using CodeGraph.Contracts;

// codegraph <command> [arguments] [options]: one ICodeGraph call, its result as JSON on stdout. Errors go to stderr
// with exit code 1 (2 for a usage error). Hand-rolled parsing: ten commands and a few options need no library, and
// nothing here uses reflection, so the Native AOT publish stays warning-free.
return await CodeGraphCli.RunAsync(args, Console.Out, Console.Error);

internal static class CodeGraphCli
{
    public const string Help =
        """
        codegraph: the code graph from the shell. JSON on stdout; errors on stderr, exit 1 (2 for usage).
        Lists are {"items": [...], "total": N, "truncated": bool}: the first --limit items (default 25, 0 = all).
        A <symbol> is an id (M:Ns.Type.Method(System.Guid)) or an exact name (Method, Type.Method); a name that
        several symbols have fails with their ids on stderr. Symbols: {id, kind, name, fullName, signature, file,
        startLine, endLine, isTest, typeKind, accessibility, isStatic, isAbstract, isVirtual, isOverride, isAsync,
        language, project}; file is relative to the repo root.

          find <text> [--kind K] [--match Contains|StartsWith|EndsWith|Exact] [--type-kind T] [--accessibility A] [--offset N]
                                          list of symbols, exact names first. K: Type, Method, Property, Field, Event, File, Project
          outline <symbol>                {target, children: list}: members of a type, types in a file, files in a project
          snippet <symbol> [--context N]  {target, file, startLine, endLine, lines}: the declaration's source lines
          callers <symbol> [--depth N]    {target, depth, hits: list of {symbol, depth}}: who calls or reads it, nearest first (depth 1-20, default 1)
          callees <symbol> [--depth N] [--external]  {target, depth, hits}: what it calls
          impact <symbol> [--depth N]     {target, depth, dependents: list of {symbol, depth}, tests: list of {symbol, depth}} (default depth 5)
          tests <symbol>                  {target, tests: list}: test methods that call it directly
          query <sql> [--timeout S]       {columns, items: rows as arrays, total, truncated}: one read-only SELECT
          schema                          {tables: [{name, columns}], nodeKinds, edgeKinds, notes}
          status                          {db, root, nodes, edges, schemaVersion, indexedAt, commit, languages}

        Options: --limit N, --db PATH (else CODEGRAPH_DB, codegraph.client.json, or the worktree's .codegraph/graph.db).
        Example: codegraph callers IUserManager.GetUserById --depth 2 --limit 0 | jq -c '[.hits.items[].symbol.project] | group_by(.) | map({(.[0] // "?"): length}) | add'
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            await stdout.WriteLineAsync(Help);
            return args.Length == 0 ? 2 : 0;
        }

        if (args[0] is "--version")
        {
            await stdout.WriteLineAsync(typeof(CodeGraphClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
            return 0;
        }

        Arguments parsed;
        try
        {
            parsed = Arguments.Parse(args);
        }
        catch (UsageException e)
        {
            await stderr.WriteLineAsync($"codegraph: {e.Message}. Run codegraph --help.");
            return 2;
        }

        try
        {
            using var graph = CodeGraphClient.Open(parsed.Option("db"));
            var json = await RunAsync(graph, parsed);
            await stdout.WriteLineAsync(json);
            return 0;
        }
        catch (UsageException e)
        {
            await stderr.WriteLineAsync($"codegraph: {e.Message}. Run codegraph --help.");
            return 2;
        }
        catch (AmbiguousSymbolException e)
        {
            await stderr.WriteLineAsync($"codegraph: {(e.Candidates.Count > 20 ? "more than 20" : e.Candidates.Count.ToString(CultureInfo.InvariantCulture))} symbols are named {e.Query}; pass one id:");
            foreach (var candidate in e.Candidates.Take(20))
            {
                await stderr.WriteLineAsync($"  {candidate.Id}  {candidate.File}:{candidate.StartLine}");
            }

            return 1;
        }
        catch (Exception e) when (e is SymbolNotFoundException or ArgumentException or TimeoutException or FileNotFoundException
                                      or System.Data.Common.DbException or InvalidOperationException)
        {
            await stderr.WriteLineAsync($"codegraph: {e.Message}");
            return 1;
        }
    }

    private static async Task<string> RunAsync(ICodeGraph graph, Arguments a)
    {
        var limit = a.Limit;
        return a.Command switch
        {
            "find" => Json(await graph.FindAsync(new SymbolQuery(a.Positional("text"))
            {
                Kind = a.Option("kind"),
                Match = a.Option("match") ?? "Contains",
                TypeKind = a.Option("type-kind"),
                Accessibility = a.Option("accessibility"),
                Offset = a.Number("offset", 0),
                Limit = limit,
            }), ContractsJson.Default.PageSymbol),
            "outline" => Json(await graph.OutlineAsync(a.Positional("symbol"), limit), ContractsJson.Default.Outline),
            "snippet" => Json(await graph.SnippetAsync(a.Positional("symbol"), a.Number("context", 0)), ContractsJson.Default.Snippet),
            "callers" => Json(await graph.CallersAsync(a.Positional("symbol"), a.Number("depth", 1), limit), ContractsJson.Default.Traversal),
            "callees" => Json(await graph.CalleesAsync(a.Positional("symbol"), a.Number("depth", 1), a.Flag("external"), limit), ContractsJson.Default.Traversal),
            "impact" => Json(await graph.ImpactAsync(a.Positional("symbol"), a.Number("depth", 5), limit), ContractsJson.Default.Impact),
            "tests" => Json(await graph.TestsAsync(a.Positional("symbol"), limit), ContractsJson.Default.TestList),
            "query" => Json(await graph.QueryAsync(a.Positional("sql"), limit, a.Number("timeout", 10)), ContractsJson.Default.QueryResult),
            "schema" => Json(await graph.SchemaAsync(), ContractsJson.Default.Schema),
            "status" => Json(await graph.StatusAsync(), ContractsJson.Default.Status),
            var other => throw new UsageException($"unknown command '{other}'"),
        };
    }

    /// <summary>
    /// The contract types, unescaped: the default encoder writes &lt; as \u003C, which costs an agent tokens in every
    /// generic signature. Output goes to a terminal or a pipe, never into HTML.
    /// </summary>
    private static readonly ContractsJson Contracts = new(new JsonSerializerOptions(ContractsJson.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    private static string Json<T>(T value, JsonTypeInfo<T> type) =>
        JsonSerializer.Serialize(value, (JsonTypeInfo<T>)Contracts.GetTypeInfo(typeof(T))!);
}

internal sealed class UsageException(string message) : Exception(message);

/// <summary>A command, its one positional argument, and --name value options (--external is a flag).</summary>
internal sealed class Arguments
{
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["find"] = ["kind", "match", "type-kind", "accessibility", "offset"],
        ["outline"] = [],
        ["snippet"] = ["context"],
        ["callers"] = ["depth"],
        ["callees"] = ["depth", "external"],
        ["impact"] = ["depth"],
        ["tests"] = [],
        ["query"] = ["timeout"],
        ["schema"] = [],
        ["status"] = [],
    };

    private static readonly string[] Common = ["limit", "db"];

    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);
    private readonly List<string> _positional = [];

    public required string Command { get; init; }

    public static Arguments Parse(string[] args)
    {
        var command = args[0];
        if (!Allowed.TryGetValue(command, out var names))
        {
            throw new UsageException($"unknown command '{command}'");
        }

        var parsed = new Arguments { Command = command };
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
            {
                var name = arg[2..];
                string? value = null;
                if (name.IndexOf('=') is > 0 and var eq)
                {
                    value = name[(eq + 1)..];
                    name = name[..eq];
                }

                if (!names.Contains(name) && !Common.Contains(name))
                {
                    throw new UsageException($"{command} has no option --{name}");
                }

                if (name != "external" && value is null)
                {
                    value = i + 1 < args.Length ? args[++i] : throw new UsageException($"--{name} needs a value");
                }

                parsed._options[name] = value;
            }
            else
            {
                parsed._positional.Add(arg);
            }
        }

        var wanted = command is "schema" or "status" ? 0 : 1;
        if (parsed._positional.Count != wanted)
        {
            throw new UsageException(wanted == 0
                ? $"{command} takes no argument"
                : $"{command} takes one argument (quote it if it has spaces), got {parsed._positional.Count}");
        }

        return parsed;
    }

    public string Positional(string name) => _positional.Count > 0 ? _positional[0] : throw new UsageException($"missing <{name}>");

    public string? Option(string name) => _options.GetValueOrDefault(name);

    public bool Flag(string name) => _options.ContainsKey(name);

    public int Number(string name, int fallback) =>
        Option(name) is not { } text ? fallback
        : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value
        : throw new UsageException($"--{name} takes a whole number, not '{text}'");

    /// <summary>--limit N: N items (default 25); 0 means all.</summary>
    public int Limit => Number("limit", Page.DefaultLimit) is var n && n <= 0 ? int.MaxValue : n;
}
