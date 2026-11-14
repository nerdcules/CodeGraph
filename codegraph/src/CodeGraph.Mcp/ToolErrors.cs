using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using CodeGraph.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CodeGraph.Mcp;

/// <summary>
/// Turns a tool call that throws into an answer a model can act on, instead of the SDK's bare "An error occurred
/// invoking 'x'.": arguments that do not fit the tool get the tool's parameter list, a damaged graph file gets the
/// command that rebuilds it. Cancellation and protocol errors (an unknown tool) are left to the SDK.
/// </summary>
internal static class ToolErrors
{
    /// <summary>"id (string, required), depth (integer, default 5)" for each tool, from the tool methods themselves.</summary>
    private static readonly Dictionary<string, string> Parameters = typeof(CodeGraphTools)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
        .Select(m => (Tool: m.GetCustomAttribute<McpServerToolAttribute>()?.Name, Method: m))
        .Where(t => t.Tool is not null)
        .ToDictionary(t => t.Tool!, t => string.Join(", ", t.Method.GetParameters()
            .Where(p => p.GetCustomAttribute<DescriptionAttribute>() is not null)
            .Select(Describe)));

    /// <summary>The message for a call to <paramref name="tool"/> that threw <paramref name="error"/>, or null to let it propagate.</summary>
    public static string? Explain(string tool, Exception error, IDictionary<string, JsonElement>? arguments, string dbPath)
    {
        if (error is OperationCanceledException or McpException || !Parameters.ContainsKey(tool))
        {
            return null;
        }

        var reason = error.Message.Split('\n', 2)[0].Trim().TrimEnd('.');
        if (GraphStore.IsCorrupt(error))
        {
            return $"{tool} failed: the graph file {dbPath} is damaged ({reason}). Rebuild it with: codegraph-index <solution or folder> --full";
        }

        var given = arguments is null || arguments.Count == 0
            ? "none"
            : string.Join(", ", arguments.Select(a => $"{a.Key} ({a.Value.ValueKind.ToString().ToLowerInvariant()})"));
        var takes = Parameters[tool] is { Length: > 0 } list ? list : "no arguments";
        var what = error is JsonException or ArgumentException or FormatException or InvalidCastException
            ? "could not use these arguments"
            : $"failed ({error.GetType().Name})";
        return $"{tool} {what}: {reason}. Arguments given: {given}. {tool} takes: {takes}.";
    }

    private static string Describe(ParameterInfo parameter)
    {
        var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
        var kind = type == typeof(string) ? "string"
            : type == typeof(int) || type == typeof(long) ? "integer"
            : type == typeof(bool) ? "boolean"
            : type == typeof(double) ? "number"
            : type.IsArray || type.IsGenericType ? "array"
            : "object";
        var need = parameter.HasDefaultValue
            ? parameter.DefaultValue is null ? "optional" : $"default {Convert.ToString(parameter.DefaultValue, System.Globalization.CultureInfo.InvariantCulture)?.ToLowerInvariant()}"
            : "required";
        return $"{parameter.Name} ({kind}, {need})";
    }
}
