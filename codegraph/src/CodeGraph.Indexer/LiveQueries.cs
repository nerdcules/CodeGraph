using System.Diagnostics;
using CodeGraph.Core.Live;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace CodeGraph.Indexer;

/// <summary>
/// Questions the graph cannot answer, asked of the loaded Roslyn <see cref="Solution"/> the way an IDE asks
/// them: the symbol under a position, every use of a symbol including ones in metadata, the diagnostics of the
/// working tree, and what a hypothetical edit would break. Each call reads one immutable snapshot, so calls
/// run concurrently with each other and with the watch loop's re-indexing.
/// </summary>
public static class LiveQueries
{
    private const int MaxReferences = 200;
    private const int MaxDiagnostics = 100;

    public static async Task<LiveSymbol> SymbolAtAsync(Solution solution, LivePosition at, CancellationToken cancellationToken)
    {
        var document = DocumentsAt(solution, at.File).FirstOrDefault()
                       ?? throw new LiveException($"{at.File} is not part of the loaded solution");
        var text = await document.GetTextAsync(cancellationToken);
        if (at.Line < 1 || at.Line > text.Lines.Count)
        {
            throw new LiveException($"{Path.GetFileName(at.File)} has {text.Lines.Count} lines; line {at.Line} is out of range");
        }

        var line = text.Lines[at.Line - 1];
        var position = Math.Min(line.Start + Math.Max(0, at.Column - 1), line.End);
        var model = await document.GetSemanticModelAsync(cancellationToken) ?? throw new LiveException("no semantic model");
        var root = await document.GetSyntaxRootAsync(cancellationToken) ?? throw new LiveException("no syntax tree");

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancellationToken);
        var enclosing = Member(model.GetEnclosingSymbol(position, cancellationToken));
        symbol ??= enclosing;
        if (symbol is null)
        {
            throw new LiveException($"nothing declared or referenced at {Path.GetFileName(at.File)}:{at.Line}:{at.Column}");
        }

        // The innermost expression with a type: the name in `Files.Clean(...)` is a method group with none, so the
        // walk continues out to the invocation and reports what the call produces.
        var token = root.FindToken(position);
        var expressionType = token.Parent?.AncestorsAndSelf().OfType<ExpressionSyntax>()
            .Select(e => model.GetTypeInfo(e, cancellationToken).Type)
            .FirstOrDefault(t => t is not null && t.TypeKind != TypeKind.Error)?.ToDisplayString();
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan();

        return new LiveSymbol(
            IdOf(symbol),
            symbol.Kind.ToString(),
            symbol.ToDisplayString(),
            symbol.Locations.Any(l => l.IsInSource),
            location?.Path,
            location is { } span ? span.StartLinePosition.Line + 1 : null,
            enclosing is null || SymbolEqualityComparer.Default.Equals(enclosing, symbol) ? null : IdOf(enclosing),
            expressionType);
    }

    /// <summary>
    /// Every use of a symbol in the solution. <paramref name="symbolIdOrName"/> is a documentation-comment id
    /// (<c>M:System.IO.File.Delete(System.String)</c>) or a dotted name (<c>System.IO.File.Delete</c>, all overloads).
    /// Metadata symbols work too, which is the point: the graph only has edges between source members.
    /// </summary>
    public static async Task<LiveReferences> ReferencesAsync(Solution solution, string symbolIdOrName, int limit, CancellationToken cancellationToken)
    {
        var symbols = await ResolveAsync(solution, symbolIdOrName, cancellationToken);
        if (symbols.Count == 0)
        {
            throw new LiveException($"no symbol matches '{symbolIdOrName}' in the solution or its references");
        }

        var references = new List<LiveReference>();
        var seen = new HashSet<(string, int)>();
        foreach (var symbol in symbols)
        {
            foreach (var referenced in await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken))
            {
                foreach (var location in referenced.Locations)
                {
                    if (location.IsImplicit || !location.Location.IsInSource)
                    {
                        continue;
                    }

                    var span = location.Location.GetLineSpan();
                    if (!seen.Add((span.Path, span.StartLinePosition.Line)))
                    {
                        continue;
                    }

                    var model = await location.Document.GetSemanticModelAsync(cancellationToken);
                    var text = await location.Document.GetTextAsync(cancellationToken);
                    var enclosing = model is null ? null : Member(model.GetEnclosingSymbol(location.Location.SourceSpan.Start, cancellationToken));
                    references.Add(new LiveReference(
                        span.Path,
                        span.StartLinePosition.Line + 1,
                        enclosing is null ? null : IdOf(enclosing),
                        enclosing?.ToDisplayString(),
                        text.Lines[span.StartLinePosition.Line].ToString().Trim()));
                }
            }
        }

        references.Sort((a, b) => string.CompareOrdinal(a.File, b.File) is var c && c != 0 ? c : a.Line.CompareTo(b.Line));
        var first = symbols[0];
        return new LiveReferences(
            first.ToDisplayString(),
            IdOf(first),
            first.Locations.Any(l => l.IsInSource),
            references.Count,
            references.Take(Math.Clamp(limit, 1, MaxReferences)).ToList());
    }

    /// <summary>Compiler diagnostics for one file, or for the whole solution when <paramref name="file"/> is null.</summary>
    public static async Task<LiveDiagnostics> DiagnosticsAsync(Solution solution, string? file, bool includeWarnings, int limit, CancellationToken cancellationToken)
    {
        var items = new List<Diagnostic>();
        if (file is not null)
        {
            var documents = DocumentsAt(solution, file).ToList();
            if (documents.Count == 0)
            {
                throw new LiveException($"{file} is not part of the loaded solution");
            }

            foreach (var document in documents)
            {
                var model = await document.GetSemanticModelAsync(cancellationToken);
                if (model is not null)
                {
                    items.AddRange(model.GetDiagnostics(cancellationToken: cancellationToken));
                }
            }
        }
        else
        {
            foreach (var project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
            {
                var compilation = await project.GetCompilationAsync(cancellationToken);
                if (compilation is not null)
                {
                    items.AddRange(compilation.GetDiagnostics(cancellationToken));
                }
            }
        }

        var relevant = items
            .Where(d => d.Severity == DiagnosticSeverity.Error || (includeWarnings && d.Severity == DiagnosticSeverity.Warning))
            .Select(Convert)
            .Distinct()
            .OrderBy(d => d.Severity == "Error" ? 0 : 1).ThenBy(d => d.File, StringComparer.Ordinal).ThenBy(d => d.Line)
            .ToList();
        return new LiveDiagnostics(
            relevant.Count(d => d.Severity == "Error"),
            relevant.Count(d => d.Severity == "Warning"),
            relevant.Take(Math.Clamp(limit, 1, MaxDiagnostics)).ToList());
    }

    /// <summary>
    /// Compiles the solution as it would be if <paramref name="file"/> held <paramref name="text"/>: the file's
    /// own project(s) and everything that depends on them. Roslyn forks the snapshot for free and re-binds only
    /// what changed, so this is the cost of an incremental build, with nothing written to disk.
    /// </summary>
    public static async Task<LiveCheck> CheckChangeAsync(Solution solution, string file, string text, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var path = Path.GetFullPath(file);
        var forked = WorkspaceSync.Apply(solution, [path], _ => text);
        if (forked == solution)
        {
            throw new LiveException($"{file} is not inside any project of the loaded solution");
        }

        var changed = forked.GetDocumentIdsWithFilePath(path).Select(d => d.ProjectId).Distinct().ToList();
        var graph = forked.GetProjectDependencyGraph();
        var affected = changed
            .Concat(changed.SelectMany(graph.GetProjectsThatTransitivelyDependOnThisProject))
            .Distinct()
            .Select(id => forked.GetProject(id)!)
            .Where(p => p.Language == LanguageNames.CSharp)
            .ToList();

        var before = await ErrorsAsync(affected.Select(p => solution.GetProject(p.Id)).OfType<Project>(), cancellationToken);
        var after = await ErrorsAsync(affected, cancellationToken);

        return new LiveCheck(
            affected.Select(p => p.Name).Distinct().Order(StringComparer.Ordinal).ToList(),
            after.Except(before).ToList(),
            before.Except(after).ToList(),
            before.Count,
            after.Count,
            Math.Round(clock.Elapsed.TotalSeconds, 2));
    }

    private static async Task<HashSet<LiveDiagnostic>> ErrorsAsync(IEnumerable<Project> projects, CancellationToken cancellationToken)
    {
        var errors = new HashSet<LiveDiagnostic>();
        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
            {
                continue;
            }

            errors.UnionWith(compilation.GetDiagnostics(cancellationToken)
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(Convert));
        }

        return errors;
    }

    private static LiveDiagnostic Convert(Diagnostic d)
    {
        var span = d.Location.IsInSource ? d.Location.GetLineSpan() : default(FileLinePositionSpan?);
        return new LiveDiagnostic(d.Severity.ToString(), d.Id, d.GetMessage(), span?.Path, span is { } s ? s.StartLinePosition.Line + 1 : null);
    }

    private static IEnumerable<Document> DocumentsAt(Solution solution, string file)
    {
        var path = Path.GetFullPath(file);
        return solution.GetDocumentIdsWithFilePath(path).Select(solution.GetDocument).OfType<Document>();
    }

    private static async Task<IReadOnlyList<ISymbol>> ResolveAsync(Solution solution, string idOrName, CancellationToken cancellationToken)
    {
        var found = new List<ISymbol>();
        foreach (var project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
            {
                continue;
            }

            if (idOrName.Length > 2 && idOrName[1] == ':')
            {
                if (DocumentationCommentId.GetFirstSymbolForDeclarationId(idOrName, compilation) is { } symbol)
                {
                    found.Add(symbol);
                    break;
                }

                continue;
            }

            // A dotted name: the type itself, or a member of the type named by everything before the last dot.
            if (compilation.GetTypeByMetadataName(idOrName) is { } type)
            {
                found.Add(type);
                break;
            }

            var dot = idOrName.LastIndexOf('.');
            if (dot > 0 && compilation.GetTypeByMetadataName(idOrName[..dot]) is { } owner)
            {
                var members = owner.GetMembers(idOrName[(dot + 1)..]);
                if (members.Length > 0)
                {
                    found.AddRange(members);
                    break;
                }
            }
        }

        return found;
    }

    /// <summary>The member or type that owns a position, climbing out of lambdas and local functions.</summary>
    private static ISymbol? Member(ISymbol? symbol)
    {
        while (symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
        {
            symbol = symbol.ContainingSymbol;
        }

        return symbol is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol or INamedTypeSymbol ? symbol : symbol?.ContainingType;
    }

    private static string? IdOf(ISymbol symbol) => symbol.OriginalDefinition.GetDocumentationCommentId();
}
