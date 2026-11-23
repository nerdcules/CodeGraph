using System.Text;
using System.Text.RegularExpressions;

namespace CodeGraph.Client;

/// <summary>One tool call as the shape sees it: the tool's name, a Bash command (else null), the whole input as JSON, and
/// the text the call returned.</summary>
public sealed record ShapeCall(string Name, string? Command, string? Arguments, string Result);

/// <summary>
/// The shape of the question an agent answered, from its tool calls (issue #18): how many graph steps it took, how large
/// their results were, and whether a later step reused something an earlier one returned.
/// <list type="bullet">
/// <item>A graph step is one codegraph MCP tool call, one <c>codegraph</c> CLI invocation (a pipeline segment that
/// starts with it), or one <c>dotnet run</c> of a script. ToolSearch, Read, Write and Edit are not graph steps.</item>
/// <item>A step's result size is the length in characters of the text its tool call returned (a Bash call's whole
/// output, so a CLI call filtered by jq counts what jq printed).</item>
/// <item>Chained is a heuristic: a later step's input (for a script run, also what Write or Edit put in the script
/// since the last step) contains an id, an 8-character handle or a dotted name of 8 or more characters that appeared
/// in an earlier step's result and does not appear in the prompt.</item>
/// </list>
/// </summary>
public sealed partial record QuestionShape(int GraphCalls, long? LargestResultChars, long? TotalResultChars, bool? Chained)
{
    private sealed record Step(string Input, string Result);

    /// <summary>The shape of one question: the tool calls made while answering it, in order, and the prompt.</summary>
    public static QuestionShape Of(IReadOnlyList<ShapeCall> tools, string prompt)
    {
        var steps = new List<Step>();
        var calls = 0;
        var written = new StringBuilder();
        foreach (var tool in tools)
        {
            var arguments = tool.Arguments ?? tool.Command ?? "";
            if (tool.Name.StartsWith("mcp__codegraph__", StringComparison.Ordinal))
            {
                steps.Add(new Step(arguments, tool.Result));
                calls++;
            }
            else if (tool.Name == "Bash")
            {
                var cli = CliCount(tool.Command);
                var script = IsScript(tool.Command) ? 1 : 0;
                if (cli + script > 0)
                {
                    steps.Add(new Step(written + arguments, tool.Result));
                    calls += cli + script;
                    written.Clear();
                }
            }
            else if (tool.Name is "Write" or "Edit" or "MultiEdit")
            {
                written.Append(arguments).Append('\n');
            }
        }

        if (steps.Count == 0)
        {
            return new QuestionShape(0, null, null, null);
        }

        var chained = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (seen.Any(token => step.Input.Contains(token, StringComparison.Ordinal)))
            {
                chained = true;
                break;
            }

            seen.UnionWith(Tokens(step.Result).Where(token => !prompt.Contains(token, StringComparison.Ordinal)));
        }

        return new QuestionShape(calls, steps.Max(s => (long)s.Result.Length), steps.Sum(s => (long)s.Result.Length), chained);
    }

    /// <summary>Whether a Bash command runs a C# file-based script (<c>dotnet run</c>).</summary>
    public static bool IsScript(string? command) => command is not null && ScriptRun().IsMatch(command);

    /// <summary>How many pipeline segments of a command start with the codegraph CLI.</summary>
    public static int CliCount(string? command) =>
        command is null ? 0 : Segments().Split(command).Count(s => CliStart().IsMatch(s));

    /// <summary>The names and ids in a result that a later call could pass back.</summary>
    internal static IEnumerable<string> Tokens(string result)
    {
        foreach (Match m in DocId().Matches(result))
        {
            yield return m.Value;
        }

        foreach (Match m in Handle().Matches(result))
        {
            yield return m.Groups[1].Value;
        }

        foreach (Match m in Dotted().Matches(result))
        {
            if (m.Value.Length >= 8)
            {
                yield return m.Value;
            }
        }
    }

    [GeneratedRegex(@"(^|[;&|]\s*)dotnet\s+run\b")]
    private static partial Regex ScriptRun();

    [GeneratedRegex(@"\|\||&&|[|;&\n]")]
    private static partial Regex Segments();

    [GeneratedRegex(@"^\s*(\S*/)?codegraph(\s|$)")]
    private static partial Regex CliStart();

    [GeneratedRegex(@"\b[TMPFE]:[A-Za-z_][\w.`]*")]
    private static partial Regex DocId();

    [GeneratedRegex(@"\[([0-9a-f]{8})\]")]
    private static partial Regex Handle();

    [GeneratedRegex(@"\b[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+")]
    private static partial Regex Dotted();
}
