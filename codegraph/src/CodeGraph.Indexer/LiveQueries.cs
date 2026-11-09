using System.Diagnostics;
using CodeGraph.Core.Live;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
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
    public static Task<LiveCheck> CheckChangeAsync(Solution solution, string file, string text, CancellationToken cancellationToken) =>
        CheckChangeAsync(solution, [new LiveFileText(file, text)], cancellationToken);

    /// <summary>
    /// Several files at once: a change that moves a member from one file to another, or edits a declaration and
    /// its callers together, only compiles as a whole. Every file's project and every dependent is compiled once.
    /// </summary>
    public static async Task<LiveCheck> CheckChangeAsync(Solution solution, IReadOnlyList<LiveFileText> files, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        if (files.Count == 0)
        {
            throw new LiveException("no files to check");
        }

        var texts = files.ToDictionary(f => Path.GetFullPath(f.File), f => f.Text, StringComparer.OrdinalIgnoreCase);
        var forked = WorkspaceSync.Apply(solution, texts.Keys, path => texts[path]);
        var outside = texts.Keys.Where(p => forked.GetDocumentIdsWithFilePath(p).IsEmpty).ToList();
        if (outside.Count > 0)
        {
            throw new LiveException($"{string.Join(", ", outside)} is not inside any project of the loaded solution");
        }

        var changed = texts.Keys.SelectMany(p => forked.GetDocumentIdsWithFilePath(p)).Select(d => d.ProjectId).Distinct().ToList();
        var graph = forked.GetProjectDependencyGraph();
        var affected = changed
            .Concat(changed.SelectMany(graph.GetProjectsThatTransitivelyDependOnThisProject))
            .Distinct()
            .Select(id => forked.GetProject(id)!)
            .Where(p => p.Language == LanguageNames.CSharp)
            .ToList();

        var before = await ErrorsAsync(affected.Select(p => solution.GetProject(p.Id)).OfType<Project>(), cancellationToken);
        var after = await ErrorsAsync(affected, cancellationToken);

        var declarations = new List<LiveDeclarationChange>();
        foreach (var path in texts.Keys)
        {
            var old = await DeclarationsAsync(DocumentsAt(solution, path).FirstOrDefault(), cancellationToken);
            var now = await DeclarationsAsync(DocumentsAt(forked, path).FirstOrDefault(), cancellationToken);
            foreach (var (id, was) in old)
            {
                if (!now.TryGetValue(id, out var isNow))
                {
                    declarations.Add(new LiveDeclarationChange(id, "removed", was.Display));
                }
                else if (was.Display != isNow.Display)
                {
                    declarations.Add(new LiveDeclarationChange(id, "signature", isNow.Display));
                }
                else if (was.Body != isNow.Body)
                {
                    declarations.Add(new LiveDeclarationChange(id, "body", isNow.Display));
                }
            }

            declarations.AddRange(now.Where(d => !old.ContainsKey(d.Key)).Select(d => new LiveDeclarationChange(d.Key, "added", d.Value.Display)));
        }

        return new LiveCheck(
            affected.Select(p => p.Name).Distinct().Order(StringComparer.Ordinal).ToList(),
            after.Except(before).ToList(),
            before.Except(after).ToList(),
            before.Count,
            after.Count,
            Math.Round(clock.Elapsed.TotalSeconds, 2),
            declarations.DistinctBy(d => d.Id).ToList());
    }

    private static readonly SymbolDisplayFormat DeclarationFormat = SymbolDisplayFormat.CSharpErrorMessageFormat
        .AddMemberOptions(SymbolDisplayMemberOptions.IncludeAccessibility | SymbolDisplayMemberOptions.IncludeModifiers | SymbolDisplayMemberOptions.IncludeType);

    /// <summary>
    /// Every member a document declares, by graph id: its signature as displayed, and its body text so an edit
    /// inside an implementation is told apart from one to the signature. Types report signatures only; their
    /// "body" is every member and would always differ.
    /// </summary>
    private static async Task<Dictionary<string, (string Display, string? Body)>> DeclarationsAsync(Document? document, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, (string, string?)>(StringComparer.Ordinal);
        if (document is null
            || await document.GetSyntaxRootAsync(cancellationToken) is not { } root
            || await document.GetSemanticModelAsync(cancellationToken) is not { } model)
        {
            return result;
        }

        foreach (var node in root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax
                     or FieldDeclarationSyntax or EventFieldDeclarationSyntax or VariableDeclarationSyntax))
        {
            if (node is not (MemberDeclarationSyntax or VariableDeclaratorSyntax) || node is BaseNamespaceDeclarationSyntax or BaseFieldDeclarationSyntax or GlobalStatementSyntax)
            {
                continue;
            }

            if (model.GetDeclaredSymbol(node, cancellationToken) is not { } symbol || IdOf(symbol) is not { } id)
            {
                continue;
            }

            var body = symbol is INamedTypeSymbol ? null : (node is VariableDeclaratorSyntax ? node.Parent!.Parent! : node).ToString();
            result.TryAdd(id, (symbol.ToDisplayString(DeclarationFormat), body));
        }

        return result;
    }

    /// <summary>
    /// Renames a source symbol on a fork of the solution and reports every line that would change, the way the
    /// IDE's rename does: overloads optionally, interface members with their implementations, overrides with their
    /// bases. The fork is then compiled, and errors it has that the current solution does not are reported as
    /// conflicts. Nothing is written; the caller applies the edits.
    /// </summary>
    public static async Task<LiveRename> RenamePreviewAsync(Solution solution, string symbolIdOrName, string newName, bool overloads, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        if (!SyntaxFacts.IsValidIdentifier(newName))
        {
            throw new LiveException($"'{newName}' is not a valid C# identifier");
        }

        var symbol = (await ResolveAsync(solution, symbolIdOrName, cancellationToken)).FirstOrDefault()
                     ?? throw new LiveException($"no symbol matches '{symbolIdOrName}'");
        if (!symbol.Locations.Any(l => l.IsInSource))
        {
            throw new LiveException($"{symbol.ToDisplayString()} is declared in metadata and cannot be renamed");
        }

        var options = new SymbolRenameOptions(RenameOverloads: overloads);
        var renamed = await Renamer.RenameSymbolAsync(solution, symbol, options, newName, cancellationToken);

        var files = new List<LiveFileEdit>();
        var changedProjects = new HashSet<ProjectId>();
        foreach (var projectChange in renamed.GetChanges(solution).GetProjectChanges())
        {
            foreach (var id in projectChange.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true))
            {
                changedProjects.Add(id.ProjectId);
                var before = await solution.GetDocument(id)!.GetTextAsync(cancellationToken);
                var after = await renamed.GetDocument(id)!.GetTextAsync(cancellationToken);
                var path = solution.GetDocument(id)!.FilePath ?? solution.GetDocument(id)!.Name;
                if (files.Any(f => f.File == path))
                {
                    continue; // a multi-target project holds the same file once per target
                }

                files.Add(new LiveFileEdit(path, LineEdits(before, after)));
            }
        }

        files.Sort((a, b) => string.CompareOrdinal(a.File, b.File));
        var graph = renamed.GetProjectDependencyGraph();
        var affected = changedProjects
            .Concat(changedProjects.SelectMany(graph.GetProjectsThatTransitivelyDependOnThisProject))
            .Distinct()
            .ToList();
        var errorsBefore = await ErrorsAsync(affected.Select(solution.GetProject).OfType<Project>(), cancellationToken);
        var errorsAfter = await ErrorsAsync(affected.Select(renamed.GetProject).OfType<Project>(), cancellationToken);

        return new LiveRename(
            symbol.ToDisplayString(),
            IdOf(symbol),
            newName,
            files.Sum(f => f.Lines.Count),
            files,
            errorsAfter.Except(errorsBefore).ToList(),
            Math.Round(clock.Elapsed.TotalSeconds, 2));
    }

    /// <summary>Where each parameter of a method goes in its body; see <see cref="LiveDataFlowQuery"/>.</summary>
    public static async Task<LiveDataFlow> DataFlowAsync(Solution solution, string symbolIdOrName, CancellationToken cancellationToken)
    {
        var symbol = (await ResolveAsync(solution, symbolIdOrName, cancellationToken)).FirstOrDefault()
                     ?? throw new LiveException($"no symbol matches '{symbolIdOrName}'");
        return await LiveDataFlowQuery.AnalyzeAsync(solution, symbol, cancellationToken);
    }

    /// <summary>Changed lines between two texts. A rename never adds or removes a line, so lines pair up one to one.</summary>
    private static IReadOnlyList<LiveLineEdit> LineEdits(SourceText before, SourceText after)
    {
        var edits = new List<LiveLineEdit>();
        if (before.Lines.Count == after.Lines.Count)
        {
            for (var i = 0; i < before.Lines.Count; i++)
            {
                var old = before.Lines[i].ToString();
                var now = after.Lines[i].ToString();
                if (!string.Equals(old, now, StringComparison.Ordinal))
                {
                    edits.Add(new LiveLineEdit(i + 1, old, now));
                }
            }

            return edits;
        }

        // Defensive: report each change against the old line it starts on.
        foreach (var change in after.GetTextChanges(before))
        {
            var line = before.Lines.GetLineFromPosition(change.Span.Start);
            edits.Add(new LiveLineEdit(line.LineNumber + 1, line.ToString(), change.NewText ?? ""));
        }

        return edits;
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
