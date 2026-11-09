using System.Text.Json;
using CodeGraph.Core.Live;
using Microsoft.CodeAnalysis;

namespace CodeGraph.Indexer;

/// <summary>
/// Maps pipe requests onto <see cref="LiveQueries"/> and the watch loop. The watch loop owns the current
/// <see cref="Solution"/> and swaps it after every re-index; each request takes the snapshot once.
/// </summary>
public sealed class LiveService(Func<Solution?> snapshot, Func<LiveStatus> status, Func<CancellationToken, Task<LiveStatus>> flush)
{
    public async Task<LiveResponse> HandleAsync(LiveRequest request, CancellationToken cancellationToken)
    {
        switch (request.Method)
        {
            case "ping":
                return LiveResponse.Ok(status());
            case "flush":
                return LiveResponse.Ok(await flush(cancellationToken));
        }

        var solution = snapshot() ?? throw new LiveException("the solution is still loading");
        switch (request.Method)
        {
            case "symbol_at":
                return LiveResponse.Ok(await LiveQueries.SymbolAtAsync(solution, Params<LivePosition>(request), cancellationToken));
            case "references":
                var r = Params<ReferencesParams>(request);
                return LiveResponse.Ok(await LiveQueries.ReferencesAsync(solution, r.Symbol, r.Limit, cancellationToken));
            case "diagnostics":
                var d = Params<DiagnosticsParams>(request);
                return LiveResponse.Ok(await LiveQueries.DiagnosticsAsync(solution, d.File, d.IncludeWarnings, d.Limit, cancellationToken));
            case "check_change":
                var c = Params<CheckChangeParams>(request);
                IReadOnlyList<LiveFileText> files = c.Files is { Count: > 0 } many ? many
                    : c.File is not null && c.Text is not null ? [new LiveFileText(c.File, c.Text)]
                    : throw new LiveException("check_change: pass file and text, or files");
                return LiveResponse.Ok(await LiveQueries.CheckChangeAsync(solution, files, cancellationToken));
            case "data_flow":
                var f = Params<SymbolParams>(request);
                return LiveResponse.Ok(await LiveQueries.DataFlowAsync(solution, f.Symbol, cancellationToken));
            case "rename_preview":
                var n = Params<RenameParams>(request);
                return LiveResponse.Ok(await LiveQueries.RenamePreviewAsync(solution, n.Symbol, n.NewName, n.Overloads, cancellationToken));
            default:
                throw new LiveException($"unknown method '{request.Method}'");
        }
    }

    private static T Params<T>(LiveRequest request) =>
        request.Params is { } p
            ? p.Deserialize<T>(LivePipe.Json) ?? throw new LiveException($"{request.Method}: invalid parameters")
            : throw new LiveException($"{request.Method}: parameters are required");

    public sealed record ReferencesParams(string Symbol, int Limit = 50);

    public sealed record DiagnosticsParams(string? File = null, bool IncludeWarnings = false, int Limit = 30);

    public sealed record CheckChangeParams(string? File = null, string? Text = null, IReadOnlyList<LiveFileText>? Files = null);

    public sealed record SymbolParams(string Symbol);

    public sealed record RenameParams(string Symbol, string NewName, bool Overloads = false);
}
