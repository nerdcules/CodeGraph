using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeGraph.Core.Live;

/// <summary>
/// The wire between the MCP server and the daemon that holds the Roslyn solution (<c>codegraph-index --watch</c>):
/// a named pipe per graph file, one JSON request line in, one JSON response line out, connection per call.
/// Named pipes are Unix domain sockets on Linux and macOS and real pipes on Windows, so no port or discovery file
/// is needed: both sides derive the pipe name from the graph's path.
/// </summary>
public static class LivePipe
{
    /// <summary>A pipe name unique to this graph, short enough for every platform's limits.</summary>
    public static string NameFor(string dbPath)
    {
        var normalized = Path.GetFullPath(dbPath).Replace('\\', '/').ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "codegraph-" + Convert.ToHexStringLower(hash.AsSpan(0, 10));
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>One call. <see cref="Params"/> is whatever the method expects, as JSON.</summary>
public sealed record LiveRequest(string Method, JsonElement? Params = null);

/// <summary>One answer: a result or an error message, never both.</summary>
public sealed record LiveResponse(JsonElement? Result, string? Error)
{
    public static LiveResponse Ok<T>(T value) => new(JsonSerializer.SerializeToElement(value, LivePipe.Json), null);

    public static LiveResponse Fail(string error) => new(null, error);
}

/// <summary>Thrown by the client when the daemon answered with an error.</summary>
public sealed class LiveException(string message) : Exception(message);

/// <summary>What <c>ping</c> returns: enough for a status line and for the MCP server to describe the daemon.</summary>
public sealed record LiveStatus(
    int Pid,
    string Solution,
    string DbPath,
    int Projects,
    int Documents,
    DateTime StartedAt,
    DateTime? LastIndexedAt,
    double? LastIndexSeconds,
    int Pending,
    bool Indexing);

/// <summary>A source position, one-based like editors show it.</summary>
public sealed record LivePosition(string File, int Line, int Column);

/// <summary>What the daemon knows about the symbol under a position.</summary>
public sealed record LiveSymbol(
    string? Id,
    string Kind,
    string Display,
    bool InSource,
    string? File,
    int? Line,
    string? EnclosingMemberId,
    string? ExpressionType);

/// <summary>One use of a symbol: where, inside which member, and the line of code.</summary>
public sealed record LiveReference(string File, int Line, string? EnclosingMemberId, string? EnclosingMember, string Text);

public sealed record LiveReferences(string Symbol, string? SymbolId, bool InSource, int Total, IReadOnlyList<LiveReference> References);

public sealed record LiveDiagnostic(string Severity, string Id, string Message, string? File, int? Line);

public sealed record LiveDiagnostics(int Errors, int Warnings, IReadOnlyList<LiveDiagnostic> Items);

/// <summary>The outcome of compiling a hypothetical edit: what it breaks and what it fixes, nothing written to disk.</summary>
public sealed record LiveCheck(
    IReadOnlyList<string> ProjectsCompiled,
    IReadOnlyList<LiveDiagnostic> NewErrors,
    IReadOnlyList<LiveDiagnostic> FixedErrors,
    int ErrorsBefore,
    int ErrorsAfter,
    double Seconds);
