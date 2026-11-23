namespace CodeGraph.Contracts;

/// <summary>
/// <c>CODEGRAPH_MODE</c>: how an agent reaches the graph. <c>direct</c> (the default): the MCP server's tools.
/// <c>lite</c>: the agent writes C# scripts over <see cref="ICodeGraph"/> and runs them with <c>dotnet run</c>.
/// <c>lite-cli</c>: the agent runs the <c>codegraph</c> CLI from the shell. In both lite modes the MCP server
/// advertises no tools and no instructions.
/// </summary>
public static class CodeGraphMode
{
    public const string Variable = "CODEGRAPH_MODE";
    public const string Direct = "direct";
    public const string Lite = "lite";
    public const string LiteCli = "lite-cli";

    public static readonly IReadOnlyList<string> All = [Direct, Lite, LiteCli];

    /// <summary>The mode a value names; empty or unset is <see cref="Direct"/>. Throws on anything else, so a typo cannot pick a mode silently.</summary>
    public static string Parse(string? value)
    {
        var mode = string.IsNullOrWhiteSpace(value) ? Direct : value.Trim().ToLowerInvariant();
        return All.Contains(mode)
            ? mode
            : throw new ArgumentException($"{Variable}='{value}' is not a mode; use {string.Join(", ", All)}");
    }

    public static string FromEnvironment() => Parse(Environment.GetEnvironmentVariable(Variable));
}
