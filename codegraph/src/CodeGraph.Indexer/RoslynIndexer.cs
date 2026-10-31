using CodeGraph.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynAccessibility = Microsoft.CodeAnalysis.Accessibility;
using RoslynTypeKind = Microsoft.CodeAnalysis.TypeKind;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace CodeGraph.Indexer;

/// <summary>
/// Walks every C# project in a solution and emits <see cref="GraphNode"/>s and <see cref="GraphEdge"/>s.
/// Uses the semantic model, so calls are resolved symbols rather than text matches.
/// Every edge also records the file whose walk produced it (<see cref="Origins"/>), so an incremental run
/// can drop and redo one file's contribution.
/// </summary>
public sealed partial class RoslynIndexer
{
    private static readonly HashSet<string> TestAttributeNames = new(StringComparer.Ordinal)
    {
        "Fact", "FactAttribute",
        "Theory", "TheoryAttribute",
        "Test", "TestAttribute",
        "TestCase", "TestCaseAttribute",
        "TestMethod", "TestMethodAttribute",
    };

    private static readonly HashSet<string> FrameworkEntrypointAttributeNames = new(StringComparer.Ordinal)
    {
        "Function", "FunctionAttribute",
        "FunctionName", "FunctionNameAttribute",
    };

    private static readonly SymbolDisplayFormat SignatureFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
                     | SymbolDisplayMemberOptions.IncludeType
                     | SymbolDisplayMemberOptions.IncludeAccessibility
                     | SymbolDisplayMemberOptions.IncludeModifiers,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
                        | SymbolDisplayParameterOptions.IncludeName
                        | SymbolDisplayParameterOptions.IncludeDefaultValue,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private readonly Dictionary<string, GraphNode> _nodes = new(StringComparer.Ordinal);
    private readonly HashSet<GraphEdge> _edges = [];
    private readonly HashSet<EdgeOrigin> _origins = [];
    private readonly Dictionary<string, SearchDocument> _search = new(StringComparer.Ordinal);
    private readonly HashSet<string> _documentPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _log;
    private readonly Action<IndexProgress>? _progress;
    private int _projectOrdinal;
    private int _projectCount;

    // Names of every type declared in the solution (plus "Foo" for "FooAttribute", plus every using alias).
    // Binding every identifier in every body is the expensive part of finding type references; most identifiers
    // are locals and members, so only names that could be a source type are handed to the semantic model.
    private HashSet<string>? _sourceTypeNames;

    // Names of every property, field and event declared in the solution, the same prefilter for member accesses.
    private HashSet<string>? _sourceMemberNames;

    /// <param name="log">Receives one line per project walked and any workspace failures.</param>
    /// <param name="progress">Receives finer-grained progress than the log: which project, which file, compiling.</param>
    public RoslynIndexer(Action<string> log, Action<IndexProgress>? progress = null)
    {
        _log = log;
        _progress = progress;
    }

    public IReadOnlyCollection<GraphNode> Nodes => _nodes.Values;

    public IReadOnlyCollection<GraphEdge> Edges => _edges;

    public IReadOnlyCollection<EdgeOrigin> Origins => _origins;

    /// <summary>Full-text documents for every type and member node that was walked.</summary>
    public IReadOnlyCollection<SearchDocument> SearchDocuments => _search.Values;

    /// <summary>Every C# source file in the solution, walked or not.</summary>
    public IReadOnlyCollection<string> DocumentPaths => _documentPaths;

    public Task IndexAsync(string solutionOrProjectPath, CancellationToken cancellationToken) =>
        IndexAsync(solutionOrProjectPath, selectFiles: null, cancellationToken);

    /// <summary>
    /// Opens the solution and walks it. Once it is loaded, <paramref name="selectFiles"/> gets every C# source path
    /// and returns the subset to walk, or null for all of them. Project-level nodes and edges are always emitted.
    /// </summary>
    public async Task IndexAsync(
        string solutionOrProjectPath,
        Func<IReadOnlyCollection<string>, IReadOnlySet<string>?>? selectFiles,
        CancellationToken cancellationToken)
    {
        using var workspace = await OpenAsync(solutionOrProjectPath, _log, cancellationToken);
        await IndexAsync(workspace.CurrentSolution, selectFiles, cancellationToken);
    }

    /// <summary>
    /// Loads a solution or project through MSBuild. This is the fixed cost of every run (seconds to tens of
    /// seconds); watch mode pays it once and keeps the workspace, applying later edits in memory.
    /// </summary>
    public static async Task<MSBuildWorkspace> OpenAsync(string solutionOrProjectPath, Action<string> log, CancellationToken cancellationToken)
    {
        var workspace = MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(e =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
            {
                log($"workspace: {e.Diagnostic.Message}");
            }
        });

        var extension = Path.GetExtension(solutionOrProjectPath);
        if (extension is ".sln" or ".slnx")
        {
            await workspace.OpenSolutionAsync(solutionOrProjectPath, cancellationToken: cancellationToken);
        }
        else
        {
            await workspace.OpenProjectAsync(solutionOrProjectPath, cancellationToken: cancellationToken);
        }

        return workspace;
    }

    /// <summary>Walks an already-loaded solution; see <see cref="IndexAsync(string, Func{IReadOnlyCollection{string}, IReadOnlySet{string}?}?, CancellationToken)"/>.</summary>
    public async Task IndexAsync(
        Solution solution,
        Func<IReadOnlyCollection<string>, IReadOnlySet<string>?>? selectFiles,
        CancellationToken cancellationToken)
    {
        var projects = solution.Projects.Where(p => p.Language == LanguageNames.CSharp).ToList();
        foreach (var path in projects.SelectMany(p => p.Documents).Select(d => d.FilePath).OfType<string>())
        {
            _documentPaths.Add(path);
        }

        var files = selectFiles?.Invoke(_documentPaths);
        _projectCount = projects.Count;
        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _projectOrdinal++;
            await IndexProjectAsync(project, solution, cancellationToken, files);
        }
    }

    /// <summary>
    /// Indexes one already-open Roslyn project, optionally only the documents in <paramref name="files"/>.
    /// Internal so tests can drive it without MSBuild.
    /// </summary>
    internal async Task IndexProjectAsync(Project project, Solution solution, CancellationToken cancellationToken, IReadOnlySet<string>? files = null)
    {
        var counter = _projectCount > 0 ? $"[{_projectOrdinal}/{_projectCount}] " : "";
        _log($"{counter}project: {project.Name}");

        var projectId = $"P:{project.Name}";
        var projectOrigin = project.FilePath ?? projectId;
        AddNode(new GraphNode(projectId, NodeKind.Project, project.Name, project.Name,
            Signature: null, FilePath: project.FilePath, StartLine: 0, EndLine: 0, IsTest: false));

        foreach (var reference in project.ProjectReferences)
        {
            var target = solution.GetProject(reference.ProjectId);
            if (target is not null)
            {
                AddEdge(new GraphEdge(projectId, $"P:{target.Name}", EdgeKind.DependsOn), projectOrigin);
            }
        }

        // A synthetic root node for framework-triggered entrypoints in this project.
        var frameworkRootId = $"R:{project.Name}:framework";
        AddNode(new GraphNode(frameworkRootId, NodeKind.Project, "framework", $"{project.Name}.framework",
            Signature: null, FilePath: project.FilePath, StartLine: 0, EndLine: 0, IsTest: false));
        AddEdge(new GraphEdge(projectId, frameworkRootId, EdgeKind.Contains), projectOrigin);

        var documents = project.Documents
            .Where(d => d.FilePath is not null && (files is null || files.Contains(d.FilePath)))
            .ToList();
        if (documents.Count == 0)
        {
            return; // nothing changed here; skip the compilation, which is most of the cost
        }

        _progress?.Invoke(new IndexProgress(_projectOrdinal, _projectCount, project.Name, 0, documents.Count));
        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is null)
        {
            _log($"  no compilation for {project.Name}, skipping");
            return;
        }

        if (_sourceTypeNames is null)
        {
            (_sourceTypeNames, _sourceMemberNames) = await SourceNamesAsync(solution, cancellationToken);
        }

        var walked = 0;
        foreach (var document in documents)
        {
            _progress?.Invoke(new IndexProgress(_projectOrdinal, _projectCount, project.Name, ++walked, documents.Count));
            var tree = await document.GetSyntaxTreeAsync(cancellationToken);
            if (tree is null)
            {
                continue;
            }

            var path = document.FilePath!;
            var fileId = $"F:{path}";
            AddNode(new GraphNode(fileId, NodeKind.File, Path.GetFileName(path), path,
                Signature: null, FilePath: path, StartLine: 1,
                EndLine: tree.GetText(cancellationToken).Lines.Count, IsTest: false));
            AddEdge(new GraphEdge(projectId, fileId, EdgeKind.Contains), path);

            var model = compilation.GetSemanticModel(tree);
            var root = await tree.GetRootAsync(cancellationToken);
            IndexDocument(root, model, fileId, path, frameworkRootId, cancellationToken);
        }
    }

    private void IndexDocument(SyntaxNode root, SemanticModel model, string fileId, string origin, string frameworkRootId, CancellationToken cancellationToken)
    {
        IndexTopLevelStatements(root, model, fileId, origin, frameworkRootId, cancellationToken);

        foreach (var typeDeclaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (model.GetDeclaredSymbol(typeDeclaration, cancellationToken) is not INamedTypeSymbol type)
            {
                continue;
            }

            var typeId = IdOf(type);
            if (typeId is null)
            {
                continue;
            }

            // Partial types: always locate the node at the primary declaration, whichever part is being walked,
            // so re-walking one part in an incremental run cannot move it.
            var primary = type.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) ?? typeDeclaration;
            AddNode(NodeFor(type, typeId, NodeKind.Type, primary, isTest: false));
            AddEdge(new GraphEdge(fileId, typeId, EdgeKind.Contains), origin);
            AddSearchDocument(typeId,
            [
                (type.Name, 3),
                (type.ContainingNamespace?.ToDisplayString(), 1),
                (CommentText(typeDeclaration.GetLeadingTrivia()), 2),
            ]);

            // also wire parent type → nested type so outline(outer) shows nested types
            if (type.ContainingType is { } parentType && IsInSource(parentType)
                && IdOf(parentType) is { } parentTypeId)
                AddEdge(new GraphEdge(parentTypeId, typeId, EdgeKind.Contains), origin);

            var supertypes = new HashSet<string>(StringComparer.Ordinal) { typeId };
            if (type.BaseType is { SpecialType: SpecialType.None } baseType && IsInSource(baseType))
            {
                AddEdgeTo(typeId, baseType, EdgeKind.Inherits, origin, supertypes);
            }

            foreach (var iface in type.Interfaces.Where(IsInSource))
            {
                AddEdgeTo(typeId, iface, EdgeKind.Implements, origin, supertypes);
            }

            // Member-level dispatch: Run() implements IRunner.Run(), so a call to the interface member reaches
            // this one. Without these edges a test that only sees the interface never covers the class.
            if (type.TypeKind is RoslynTypeKind.Class or RoslynTypeKind.Struct)
            {
                foreach (var ifaceMember in type.AllInterfaces.Where(IsInSource).SelectMany(i => i.GetMembers()))
                {
                    if (ifaceMember is not (IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol or IEventSymbol)
                        || type.FindImplementationForInterfaceMember(ifaceMember) is not { } implementation
                        || !IsInSource(implementation)
                        || IdOf(implementation.OriginalDefinition) is not { } implementationId
                        || IdOf(ifaceMember) is not { } ifaceMemberId)
                    {
                        continue;
                    }

                    AddEdge(new GraphEdge(implementationId, ifaceMemberId, EdgeKind.Implements), origin);
                }
            }

            // Type-level mentions outside any member: attributes, primary-constructor / record parameters,
            // generic arguments in the base list (IHandler<Foo>) and constraints. The supertypes themselves
            // are already Inherits/Implements edges.
            if (typeDeclaration is TypeDeclarationSyntax declaration)
            {
                SyntaxNode?[] headers = [.. declaration.AttributeLists, declaration.ParameterList, declaration.BaseList, .. declaration.ConstraintClauses];
                foreach (var header in headers.OfType<SyntaxNode>())
                {
                    IndexTypeReferences(header, model, typeId, origin, supertypes, cancellationToken);
                }

                foreach (var member in declaration.Members)
                {
                    IndexMember(member, model, typeId, origin, frameworkRootId, cancellationToken);
                }
            }
        }
    }

    /// <summary>
    /// Top-level statements are the compiler-generated entry point, and in an ASP.NET app they hold the DI
    /// registrations and the lambdas that construct services. They get a node for the generated method,
    /// reachable from the framework root, and their calls and accesses are attributed to it.
    /// </summary>
    private void IndexTopLevelStatements(SyntaxNode root, SemanticModel model, string fileId, string origin, string frameworkRootId, CancellationToken cancellationToken)
    {
        if (root is not CompilationUnitSyntax unit)
        {
            return;
        }

        var statements = unit.Members.OfType<GlobalStatementSyntax>().ToList();
        if (statements.Count == 0
            || model.Compilation.GetEntryPoint(cancellationToken) is not { } entry
            || IdOf(entry) is not { } entryId)
        {
            return;
        }

        var location = unit.SyntaxTree.GetLocation(TextSpan.FromBounds(statements[0].SpanStart, statements[^1].Span.End));
        // Roslyn displays the synthesized method as <top-level-statements-entry-point>; "Main" is what people look for.
        AddNode(NodeFor(entry, entryId, NodeKind.Method, location, isTest: false) with
        {
            Name = "Main",
            FullName = $"{entry.ContainingType.ToDisplayString()}.Main",
            Signature = "top-level statements",
        });
        AddEdge(new GraphEdge(fileId, entryId, EdgeKind.Contains), origin);
        AddEdge(new GraphEdge(frameworkRootId, entryId, EdgeKind.InvokedByFramework), origin);
        AddSearchDocument(entryId,
        [
            ("Main", 3),
            (Path.GetFileName(origin), 1),
            (string.Join(" ", statements.Select(BodyText)), 1),
        ]);

        foreach (var statement in statements)
        {
            IndexBody(statement, model, entryId, callerIsTest: false, origin, cancellationToken);
        }
    }

    private void IndexMember(MemberDeclarationSyntax member, SemanticModel model, string typeId, string origin, string frameworkRootId, CancellationToken cancellationToken)
    {
        // Fields and events declare several symbols per syntax node; everything else declares one.
        IEnumerable<ISymbol?> symbols = member switch
        {
            FieldDeclarationSyntax field => field.Declaration.Variables
                .Select(v => model.GetDeclaredSymbol(v, cancellationToken)),
            EventFieldDeclarationSyntax @event => @event.Declaration.Variables
                .Select(v => model.GetDeclaredSymbol(v, cancellationToken)),
            BaseTypeDeclarationSyntax => [], // nested types are picked up by the DescendantNodes walk
            _ => [model.GetDeclaredSymbol(member, cancellationToken)],
        };

        foreach (var symbol in symbols)
        {
            if (symbol is null || KindOf(symbol) is not { } kind || IdOf(symbol) is not { } memberId)
            {
                continue;
            }

            var isTest = symbol is IMethodSymbol && HasTestAttribute(symbol);
            AddNode(NodeFor(symbol, memberId, kind, member, isTest));
            AddEdge(new GraphEdge(typeId, memberId, EdgeKind.Contains), origin);
            AddSearchDocument(memberId,
            [
                (symbol.Name, 3),
                (symbol.ContainingType?.Name, 1),
                (CommentText(member.GetLeadingTrivia()), 2),
                (symbol.ToDisplayString(SignatureFormat), 1),
                (BodyText(member), 1),
            ]);

            if (symbol is IMethodSymbol method && HasFrameworkEntrypointAttribute(method))
            {
                AddEdge(new GraphEdge(frameworkRootId, memberId, EdgeKind.InvokedByFramework), origin);
            }

            var overridden = symbol switch
            {
                IMethodSymbol { OverriddenMethod: { } m } => (ISymbol)m,
                IPropertySymbol { OverriddenProperty: { } p } => p,
                _ => null,
            };
            if (overridden is not null && IsInSource(overridden))
            {
                AddEdgeTo(memberId, overridden, EdgeKind.Overrides, origin);
            }

            IndexBody(member, model, memberId, isTest, origin, cancellationToken);
        }
    }

    /// <summary>Calls and type references from one member's declaration, signature and body, in one pass.</summary>
    private void IndexBody(SyntaxNode body, SemanticModel model, string callerId, bool callerIsTest, string origin, CancellationToken cancellationToken)
    {
        foreach (var node in body.DescendantNodes())
        {
            if (node is SimpleNameSyntax name)
            {
                IndexTypeReference(name, model, callerId, origin, skip: null, cancellationToken);
                IndexMemberAccess(name, model, callerId, callerIsTest, origin, cancellationToken);
                continue;
            }

            if (node is not (InvocationExpressionSyntax or ObjectCreationExpressionSyntax
                or ImplicitObjectCreationExpressionSyntax))
            {
                continue;
            }

            var info = model.GetSymbolInfo(node, cancellationToken);
            var target = info.Symbol ?? Unambiguous(info.CandidateSymbols);
            if (target is not IMethodSymbol method)
            {
                continue;
            }

            // Collapse generic instantiations and reduced extension methods onto their declaration.
            method = (method.ReducedFrom ?? method).OriginalDefinition;
            if (!IsInSource(method))
            {
                continue;
            }

            var targetId = IdOf(method);
            if (targetId is null || targetId == callerId)
            {
                continue;
            }

            AddEdge(new GraphEdge(callerId, targetId, EdgeKind.Calls), origin);
            if (callerIsTest)
            {
                AddEdge(new GraphEdge(targetId, callerId, EdgeKind.TestedBy), origin);
            }
        }
    }

    /// <summary>
    /// A binding that failed overload resolution (a compile error in the solution, say) still names the method
    /// when every candidate is an overload of the same member; candidates from different types are a guess.
    /// </summary>
    private static ISymbol? Unambiguous(System.Collections.Immutable.ImmutableArray<ISymbol> candidates) =>
        candidates.Length > 0
        && candidates.All(c => c.Name == candidates[0].Name
                               && SymbolEqualityComparer.Default.Equals(c.ContainingType, candidates[0].ContainingType))
            ? candidates[0]
            : null;

    /// <summary>
    /// A read or write of a property, field or event is a dependency as real as a call: without it, "who uses
    /// IStaging.Enabled" is empty and a test that reads it does not cover it. Recorded as a Calls edge so every
    /// caller and coverage walk sees it. Only names declared as members somewhere in the solution are bound.
    /// </summary>
    private void IndexMemberAccess(SimpleNameSyntax name, SemanticModel model, string callerId, bool callerIsTest, string origin, CancellationToken cancellationToken)
    {
        if (_sourceMemberNames?.Contains(name.Identifier.ValueText) != true || IsNameOfArgument(name))
        {
            return;
        }

        var symbol = model.GetSymbolInfo(name, cancellationToken).Symbol;
        if (symbol is not (IPropertySymbol or IEventSymbol or IFieldSymbol { ContainingType.TypeKind: not RoslynTypeKind.Enum }))
        {
            return;
        }

        symbol = symbol.OriginalDefinition;
        if (!IsInSource(symbol) || IdOf(symbol) is not { } targetId || targetId == callerId)
        {
            return;
        }

        AddEdge(new GraphEdge(callerId, targetId, EdgeKind.Calls), origin);
        if (callerIsTest)
        {
            AddEdge(new GraphEdge(targetId, callerId, EdgeKind.TestedBy), origin);
        }
    }

    /// <summary><c>nameof(Foo.Bar)</c> binds Bar but reads nothing.</summary>
    private static bool IsNameOfArgument(SyntaxNode name) =>
        name.Ancestors().TakeWhile(a => a is not MemberDeclarationSyntax)
            .Any(a => a is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } });

    private void IndexTypeReferences(SyntaxNode scope, SemanticModel model, string fromId, string origin, IReadOnlySet<string> skip, CancellationToken cancellationToken)
    {
        foreach (var name in scope.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
        {
            IndexTypeReference(name, model, fromId, origin, skip, cancellationToken);
        }
    }

    private void IndexTypeReference(SimpleNameSyntax name, SemanticModel model, string fromId, string origin, IReadOnlySet<string>? skip, CancellationToken cancellationToken)
    {
        // `var x = GetFoo()` names no type in syntax; the semantic model knows what it inferred, and that type
        // (with its generic arguments, e.g. List<Foo>) is as much a dependency as a spelled-out one.
        if (name is IdentifierNameSyntax { IsVar: true })
        {
            if (model.GetTypeInfo(name, cancellationToken).Type is { } inferred)
            {
                AddReferences(fromId, inferred, origin, skip);
            }

            return;
        }

        if (_sourceTypeNames?.Contains(name.Identifier.ValueText) != true)
        {
            return;
        }

        var info = model.GetSymbolInfo(name, cancellationToken);
        var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();

        // [Foo] binds to the attribute constructor; the reference is to its type.
        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor && name.Parent is AttributeSyntax or QualifiedNameSyntax { Parent: AttributeSyntax })
        {
            symbol = ctor.ContainingType;
        }

        // `using Alias = N.Foo;` binds to what the alias stands for, which may be a tuple or a generic
        // instantiation (`using Models = List<Foo>;`) naming source types only in its arguments.
        if (symbol is IAliasSymbol alias)
        {
            symbol = alias.Target;
        }

        if (symbol is ITypeSymbol type)
        {
            AddReferences(fromId, type, origin, skip);
        }
    }

    /// <summary>References to every source type mentioned by <paramref name="type"/>: itself, its generic arguments, element and tuple types.</summary>
    private void AddReferences(string fromId, ITypeSymbol type, string origin, IReadOnlySet<string>? skip)
    {
        foreach (var typeId in SourceTypeIds(type).Distinct(StringComparer.Ordinal))
        {
            if (skip?.Contains(typeId) != true)
            {
                AddEdge(new GraphEdge(fromId, typeId, EdgeKind.References), origin);
            }
        }
    }

    private static IEnumerable<string> SourceTypeIds(ITypeSymbol type)
    {
        switch (type)
        {
            case INamedTypeSymbol named:
                if (IsInSource(named) && IdOf(named.OriginalDefinition) is { } id)
                {
                    yield return id;
                }

                foreach (var argument in named.TypeArguments.SelectMany(SourceTypeIds))
                {
                    yield return argument;
                }

                break;
            case IArrayTypeSymbol array:
                foreach (var element in SourceTypeIds(array.ElementType))
                {
                    yield return element;
                }

                break;
        }
    }

    private static async Task<(HashSet<string> Types, HashSet<string> Members)> SourceNamesAsync(Solution solution, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in solution.Projects.Where(p => p.Language == LanguageNames.CSharp).SelectMany(p => p.Documents))
        {
            if (await document.GetSyntaxRootAsync(cancellationToken) is not { } root)
            {
                continue;
            }

            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    case BaseTypeDeclarationSyntax declaration:
                        var name = declaration.Identifier.ValueText;
                        names.Add(name);
                        if (name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > "Attribute".Length)
                        {
                            names.Add(name[..^"Attribute".Length]);
                        }

                        break;
                    case PropertyDeclarationSyntax property:
                        members.Add(property.Identifier.ValueText);
                        break;
                    case EventDeclarationSyntax @event:
                        members.Add(@event.Identifier.ValueText);
                        break;
                    case FieldDeclarationSyntax field:
                        members.UnionWith(field.Declaration.Variables.Select(v => v.Identifier.ValueText));
                        break;
                    case EventFieldDeclarationSyntax eventField:
                        members.UnionWith(eventField.Declaration.Variables.Select(v => v.Identifier.ValueText));
                        break;
                }
            }

            // Alias names (file-scoped or global) are the other way an identifier can stand for a source type.
            foreach (var directive in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
            {
                if (directive.Alias is { } alias)
                {
                    names.Add(alias.Name.Identifier.ValueText);
                }
            }
        }

        return (names, members);
    }

    private void AddSearchDocument(string nodeId, IEnumerable<(string? Text, int Weight)> parts)
    {
        // Partial types and multi-target projects produce the same id more than once; first one wins, as for nodes.
        if (!_search.ContainsKey(nodeId) && SearchText.Document(nodeId, parts) is { } document)
        {
            _search[nodeId] = document;
        }
    }

    /// <summary>The comment text in <paramref name="trivia"/>, XML tags stripped. Doc comments survive even with documentation parsing off, as plain comments.</summary>
    private static string? CommentText(SyntaxTriviaList trivia)
    {
        var comments = trivia.Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia)
                || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            .Select(t => t.ToFullString())
            .ToList();
        return comments.Count == 0 ? null : XmlTag().Replace(string.Join('\n', comments), " ");
    }

    /// <summary>Identifiers, string literals and comments inside a member: what the code does, in its own words.</summary>
    private static string BodyText(SyntaxNode member)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var token in member.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.IdentifierToken) || token.IsKind(SyntaxKind.StringLiteralToken)
                || token.IsKind(SyntaxKind.InterpolatedStringTextToken) || token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken)
                || token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken))
            {
                sb.Append(token.ValueText).Append(' ');
            }
        }

        foreach (var trivia in member.DescendantTrivia())
        {
            // Leading comments are the doc text, weighted separately; only comments inside the member count here.
            if (trivia.SpanStart >= member.SpanStart
                && (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)))
            {
                sb.Append(trivia.ToFullString()).Append(' ');
            }
        }

        return sb.ToString();
    }

    [System.Text.RegularExpressions.GeneratedRegex("<[^>]*>")]
    private static partial System.Text.RegularExpressions.Regex XmlTag();

    private void AddEdgeTo(string fromId, ISymbol target, EdgeKind kind, string origin, HashSet<string>? collect = null)
    {
        var targetId = IdOf(target.OriginalDefinition);
        if (targetId is not null)
        {
            AddEdge(new GraphEdge(fromId, targetId, kind), origin);
            collect?.Add(targetId);
        }
    }

    private void AddEdge(GraphEdge edge, string origin)
    {
        _edges.Add(edge);
        _origins.Add(new EdgeOrigin(edge, origin));
    }

    private void AddNode(GraphNode node)
    {
        // Partial types and multi-target projects produce the same id more than once; first one wins.
        _nodes.TryAdd(node.Id, node);
    }


    private static GraphNode NodeFor(ISymbol symbol, string id, NodeKind kind, SyntaxNode declaration, bool isTest) =>
        NodeFor(symbol, id, kind, declaration.GetLocation(), isTest);

    private static GraphNode NodeFor(ISymbol symbol, string id, NodeKind kind, Location location, bool isTest)
    {
        var span = location.GetLineSpan();
        return new GraphNode(
            id,
            kind,
            symbol.Name,
            symbol.ToDisplayString(),
            symbol.ToDisplayString(SignatureFormat),
            span.Path,
            span.StartLinePosition.Line + 1,
            span.EndLinePosition.Line + 1,
            isTest,
            TypeKind: symbol is INamedTypeSymbol t ? MapTypeKind(t) : CodeGraph.Core.TypeKind.None,
            Accessibility: MapAccessibility(symbol.DeclaredAccessibility),
            IsStatic: symbol.IsStatic,
            IsAbstract: symbol.IsAbstract,
            IsVirtual: symbol.IsVirtual,
            IsSealed: symbol.IsSealed,
            IsAsync: symbol is IMethodSymbol { IsAsync: true },
            IsOverride: symbol.IsOverride,
            MemberKind: symbol is IMethodSymbol m ? MapMemberKind(m) : MemberKind.Ordinary);
    }

    private static CodeGraph.Core.TypeKind MapTypeKind(INamedTypeSymbol t) => t.TypeKind switch
    {
        RoslynTypeKind.Class     => t.IsRecord ? CodeGraph.Core.TypeKind.Record     : CodeGraph.Core.TypeKind.Class,
        RoslynTypeKind.Struct    => t.IsRecord ? CodeGraph.Core.TypeKind.RecordStruct : CodeGraph.Core.TypeKind.Struct,
        RoslynTypeKind.Interface => CodeGraph.Core.TypeKind.Interface,
        RoslynTypeKind.Enum      => CodeGraph.Core.TypeKind.Enum,
        RoslynTypeKind.Delegate  => CodeGraph.Core.TypeKind.Delegate,
        _                        => CodeGraph.Core.TypeKind.None,
    };

    private static CodeGraph.Core.Accessibility MapAccessibility(RoslynAccessibility a) => a switch
    {
        RoslynAccessibility.Public                => CodeGraph.Core.Accessibility.Public,
        RoslynAccessibility.Private               => CodeGraph.Core.Accessibility.Private,
        RoslynAccessibility.Protected             => CodeGraph.Core.Accessibility.Protected,
        RoslynAccessibility.Internal              => CodeGraph.Core.Accessibility.Internal,
        RoslynAccessibility.ProtectedOrInternal   => CodeGraph.Core.Accessibility.ProtectedOrInternal,
        RoslynAccessibility.ProtectedAndInternal  => CodeGraph.Core.Accessibility.ProtectedAndInternal,
        _                                          => CodeGraph.Core.Accessibility.None,
    };

    private static MemberKind MapMemberKind(IMethodSymbol m) => m.MethodKind switch
    {
        MethodKind.Constructor or MethodKind.StaticConstructor => MemberKind.Constructor,
        MethodKind.Destructor                                  => MemberKind.Destructor,
        MethodKind.UserDefinedOperator or MethodKind.Conversion => MemberKind.Operator,
        _                                                       => MemberKind.Ordinary,
    };

    private static string? IdOf(ISymbol symbol) => symbol.GetDocumentationCommentId();

    private static NodeKind? KindOf(ISymbol symbol) => symbol switch
    {
        IMethodSymbol => NodeKind.Method,
        IPropertySymbol => NodeKind.Property,
        IFieldSymbol => NodeKind.Field,
        IEventSymbol => NodeKind.Event,
        _ => null,
    };

    private static bool IsInSource(ISymbol symbol) =>
        symbol.Locations.Any(l => l.IsInSource);

    private static bool HasTestAttribute(ISymbol symbol) =>
        symbol.GetAttributes().Any(a =>
            a.AttributeClass is { } attributeClass && TestAttributeNames.Contains(attributeClass.Name));

    private static bool HasFrameworkEntrypointAttribute(ISymbol symbol) =>
        symbol.GetAttributes().Any(a =>
            a.AttributeClass is { } attributeClass && FrameworkEntrypointAttributeNames.Contains(attributeClass.Name));
}

/// <summary>
/// Where an index walk is: project <paramref name="Project"/> of <paramref name="Projects"/>, and within it
/// file <paramref name="File"/> of <paramref name="Files"/>. <paramref name="File"/> is 0 while the project
/// compiles, which is most of a project's cost.
/// </summary>
public sealed record IndexProgress(int Project, int Projects, string ProjectName, int File, int Files)
{
    public override string ToString() =>
        File == 0 ? $"[{Project}/{Projects}] {ProjectName}: compiling" : $"[{Project}/{Projects}] {ProjectName}: {File}/{Files} files";
}
