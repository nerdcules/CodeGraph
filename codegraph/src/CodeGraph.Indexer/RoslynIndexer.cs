using CodeGraph.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynAccessibility = Microsoft.CodeAnalysis.Accessibility;
using RoslynTypeKind = Microsoft.CodeAnalysis.TypeKind;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

namespace CodeGraph.Indexer;

/// <summary>
/// Walks every C# project in a solution and emits <see cref="GraphNode"/>s and <see cref="GraphEdge"/>s.
/// Uses the semantic model, so calls are resolved symbols rather than text matches.
/// Every edge also records the file whose walk produced it (<see cref="Origins"/>), so an incremental run
/// can drop and redo one file's contribution.
/// </summary>
public sealed class RoslynIndexer
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
    private readonly HashSet<string> _documentPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _log;

    // Names of every type declared in the solution (plus "Foo" for "FooAttribute"). Binding every identifier in
    // every body is the expensive part of finding type references; most identifiers are locals and members, so
    // only names that could be a source type are handed to the semantic model.
    private HashSet<string>? _sourceTypeNames;

    public RoslynIndexer(Action<string> log)
    {
        _log = log;
    }

    public IReadOnlyCollection<GraphNode> Nodes => _nodes.Values;

    public IReadOnlyCollection<GraphEdge> Edges => _edges;

    public IReadOnlyCollection<EdgeOrigin> Origins => _origins;

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
        using var workspace = MSBuildWorkspace.Create();
        using var workspaceFailures = workspace.RegisterWorkspaceFailedHandler(e =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
            {
                _log($"workspace: {e.Diagnostic.Message}");
            }
        });

        var extension = Path.GetExtension(solutionOrProjectPath);
        var solution = extension is ".sln" or ".slnx"
            ? await workspace.OpenSolutionAsync(solutionOrProjectPath, cancellationToken: cancellationToken)
            : (await workspace.OpenProjectAsync(solutionOrProjectPath, cancellationToken: cancellationToken)).Solution;

        var projects = solution.Projects.Where(p => p.Language == LanguageNames.CSharp).ToList();
        foreach (var path in projects.SelectMany(p => p.Documents).Select(d => d.FilePath).OfType<string>())
        {
            _documentPaths.Add(path);
        }

        var files = selectFiles?.Invoke(_documentPaths);
        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await IndexProjectAsync(project, solution, cancellationToken, files);
        }
    }

    /// <summary>
    /// Indexes one already-open Roslyn project, optionally only the documents in <paramref name="files"/>.
    /// Internal so tests can drive it without MSBuild.
    /// </summary>
    internal async Task IndexProjectAsync(Project project, Solution solution, CancellationToken cancellationToken, IReadOnlySet<string>? files = null)
    {
        _log($"project: {project.Name}");

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

        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is null)
        {
            _log($"  no compilation for {project.Name}, skipping");
            return;
        }

        _sourceTypeNames ??= await SourceTypeNamesAsync(solution, cancellationToken);

        foreach (var document in documents)
        {
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
                continue;
            }

            if (node is not (InvocationExpressionSyntax or ObjectCreationExpressionSyntax
                or ImplicitObjectCreationExpressionSyntax))
            {
                continue;
            }

            var info = model.GetSymbolInfo(node, cancellationToken);
            var target = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
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

    private void IndexTypeReferences(SyntaxNode scope, SemanticModel model, string fromId, string origin, IReadOnlySet<string> skip, CancellationToken cancellationToken)
    {
        foreach (var name in scope.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
        {
            IndexTypeReference(name, model, fromId, origin, skip, cancellationToken);
        }
    }

    private void IndexTypeReference(SimpleNameSyntax name, SemanticModel model, string fromId, string origin, IReadOnlySet<string>? skip, CancellationToken cancellationToken)
    {
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

        if (symbol is not INamedTypeSymbol type || !IsInSource(type) || IdOf(type.OriginalDefinition) is not { } typeId
            || skip?.Contains(typeId) == true)
        {
            return;
        }

        AddEdge(new GraphEdge(fromId, typeId, EdgeKind.References), origin);
    }

    private static async Task<HashSet<string>> SourceTypeNamesAsync(Solution solution, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in solution.Projects.Where(p => p.Language == LanguageNames.CSharp).SelectMany(p => p.Documents))
        {
            if (await document.GetSyntaxRootAsync(cancellationToken) is not { } root)
            {
                continue;
            }

            foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                var name = declaration.Identifier.ValueText;
                names.Add(name);
                if (name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > "Attribute".Length)
                {
                    names.Add(name[..^"Attribute".Length]);
                }
            }
        }

        return names;
    }

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


    private static GraphNode NodeFor(ISymbol symbol, string id, NodeKind kind, SyntaxNode declaration, bool isTest)
    {
        var span = declaration.GetLocation().GetLineSpan();
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
