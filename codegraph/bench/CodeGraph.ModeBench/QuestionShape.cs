using System.Text;
using System.Text.RegularExpressions;

namespace CodeGraph.ModeBench;

/// <summary>
/// The shape of the question a run answered, from its tool calls (issue #18): how many graph steps it took, how large
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
internal sealed partial record QuestionShape(int GraphCalls, long? LargestResultChars, long? TotalResultChars, bool? Chained)
{
    private sealed record Step(string Input, string Result);

    public static QuestionShape Of(IReadOnlyList<ToolCall> tools, string prompt)
    {
        var steps = new List<Step>();
        var calls = 0;
        var written = new StringBuilder();
        foreach (var tool in tools)
        {
            var arguments = tool.Arguments ?? tool.Input ?? "";
            if (tool.Name.StartsWith("mcp__codegraph__", StringComparison.Ordinal))
            {
                steps.Add(new Step(arguments, tool.Result));
                calls++;
            }
            else if (tool.Name == "Bash")
            {
                var cli = Bench.CliCount(tool.Input);
                var script = Bench.IsScript(tool.Input) ? 1 : 0;
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

    [GeneratedRegex(@"\b[TMPFE]:[A-Za-z_][\w.`]*")]
    private static partial Regex DocId();

    [GeneratedRegex(@"\[([0-9a-f]{8})\]")]
    private static partial Regex Handle();

    [GeneratedRegex(@"\b[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+")]
    private static partial Regex Dotted();
}
