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

    /// <summary>
    /// Whether a daemon's endpoint exists, without connecting. A connect to a missing pipe does not fail fast:
    /// .NET retries until its timeout on every platform, which would add that timeout to every tool call when no
    /// daemon runs. On Unix the endpoint is a socket file in the temp directory; on Windows it is listed under
    /// <c>\\.\pipe\</c>. A socket left by a killed daemon still exists; <see cref="LiveClient"/> handles that.
    /// </summary>
    public static bool Exists(string pipeName)
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? Directory.EnumerateFiles(@"\\.\pipe\", pipeName).Any()
                : File.Exists(SocketPath(pipeName));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Where .NET puts a named pipe's Unix domain socket.</summary>
    public static string SocketPath(string pipeName) => Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + pipeName);

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
    bool Indexing,
    bool Loading = false);

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

/// <summary>One file's proposed full contents, for a multi-file what-if compile.</summary>
public sealed record LiveFileText(string File, string Text);

/// <summary>
/// How one declaration differs between the current file and the proposed one. <see cref="Change"/> is
/// <c>removed</c>, <c>added</c>, <c>signature</c> (same id, different modifiers, return type or accessibility)
/// or <c>body</c> (same signature, different implementation).
/// </summary>
public sealed record LiveDeclarationChange(string Id, string Change, string Display);

/// <summary>
/// The outcome of compiling a hypothetical edit: what it breaks and what it fixes, and which declarations it
/// touches (so the graph can say who depends on them), nothing written to disk.
/// </summary>
public sealed record LiveCheck(
    IReadOnlyList<string> ProjectsCompiled,
    IReadOnlyList<LiveDiagnostic> NewErrors,
    IReadOnlyList<LiveDiagnostic> FixedErrors,
    int ErrorsBefore,
    int ErrorsAfter,
    double Seconds,
    IReadOnlyList<LiveDeclarationChange>? Declarations = null);

/// <summary>One changed line of a previewed edit, one-based.</summary>
public sealed record LiveLineEdit(int Line, string Before, string After);

public sealed record LiveFileEdit(string File, IReadOnlyList<LiveLineEdit> Lines);

/// <summary>
/// What renaming a symbol would change, computed by Roslyn's renamer on a fork of the solution: every edited line,
/// plus the compile errors the renamed solution would have that the current one does not (name clashes, hidden members).
/// </summary>
public sealed record LiveRename(
    string Symbol,
    string? SymbolId,
    string NewName,
    int Edits,
    IReadOnlyList<LiveFileEdit> Files,
    IReadOnlyList<LiveDiagnostic> Conflicts,
    double Seconds);
