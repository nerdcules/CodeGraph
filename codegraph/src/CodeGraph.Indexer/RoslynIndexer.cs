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
    private readonly Action<string> _log;

    public RoslynIndexer(Action<string> log)
    {
        _log = log;
    }

    public IReadOnlyCollection<GraphNode> Nodes => _nodes.Values;

    public IReadOnlyCollection<GraphEdge> Edges => _edges;

    public async Task IndexAsync(string solutionOrProjectPath, CancellationToken cancellationToken)
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

        foreach (var project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await IndexProjectAsync(project, solution, cancellationToken);
        }
    }

    /// <summary>Indexes one already-open Roslyn project. Internal so tests can drive it without MSBuild.</summary>
    internal async Task IndexProjectAsync(Project project, Solution solution, CancellationToken cancellationToken)
    {
        _log($"project: {project.Name}");

        var projectId = $"P:{project.Name}";
        AddNode(new GraphNode(projectId, NodeKind.Project, project.Name, project.Name,
            Signature: null, FilePath: project.FilePath, StartLine: 0, EndLine: 0, IsTest: false));

        foreach (var reference in project.ProjectReferences)
        {
            var target = solution.GetProject(reference.ProjectId);
            if (target is not null)
            {
                _edges.Add(new GraphEdge(projectId, $"P:{target.Name}", EdgeKind.DependsOn));
            }
        }

        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is null)
        {
            _log($"  no compilation for {project.Name}, skipping");
            return;
        }

        foreach (var document in project.Documents.Where(d => d.FilePath is not null))
        {
            var tree = await document.GetSyntaxTreeAsync(cancellationToken);
            if (tree is null)
            {
                continue;
            }

            var fileId = $"F:{document.FilePath}";
            AddNode(new GraphNode(fileId, NodeKind.File, Path.GetFileName(document.FilePath!), document.FilePath!,
                Signature: null, FilePath: document.FilePath, StartLine: 1,
                EndLine: tree.GetText(cancellationToken).Lines.Count, IsTest: false));
            _edges.Add(new GraphEdge(projectId, fileId, EdgeKind.Contains));

            var model = compilation.GetSemanticModel(tree);
            var root = await tree.GetRootAsync(cancellationToken);
            IndexDocument(root, model, fileId, cancellationToken);
        }
    }

    private void IndexDocument(SyntaxNode root, SemanticModel model, string fileId, CancellationToken cancellationToken)
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

            AddNode(NodeFor(type, typeId, NodeKind.Type, typeDeclaration, isTest: false));
            _edges.Add(new GraphEdge(fileId, typeId, EdgeKind.Contains));

            // also wire parent type → nested type so outline(outer) shows nested types
            if (type.ContainingType is { } parentType && IsInSource(parentType)
                && IdOf(parentType) is { } parentTypeId)
                _edges.Add(new GraphEdge(parentTypeId, typeId, EdgeKind.Contains));

            if (type.BaseType is { SpecialType: SpecialType.None } baseType && IsInSource(baseType))
            {
                AddEdgeTo(typeId, baseType, EdgeKind.Inherits);
            }

            foreach (var iface in type.Interfaces.Where(IsInSource))
            {
                AddEdgeTo(typeId, iface, EdgeKind.Implements);
            }

            if (typeDeclaration is TypeDeclarationSyntax { Members: var members })
            {
                foreach (var member in members)
                {
                    IndexMember(member, model, typeId, cancellationToken);
                }
            }
        }
    }

    private void IndexMember(MemberDeclarationSyntax member, SemanticModel model, string typeId, CancellationToken cancellationToken)
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
            _edges.Add(new GraphEdge(typeId, memberId, EdgeKind.Contains));

            var overridden = symbol switch
            {
                IMethodSymbol { OverriddenMethod: { } m } => (ISymbol)m,
                IPropertySymbol { OverriddenProperty: { } p } => p,
                _ => null,
            };
            if (overridden is not null && IsInSource(overridden))
            {
                AddEdgeTo(memberId, overridden, EdgeKind.Overrides);
            }

            IndexCalls(member, model, memberId, isTest, cancellationToken);
        }
    }

    private void IndexCalls(SyntaxNode body, SemanticModel model, string callerId, bool callerIsTest, CancellationToken cancellationToken)
    {
        foreach (var node in body.DescendantNodes())
        {
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

            _edges.Add(new GraphEdge(callerId, targetId, EdgeKind.Calls));
            if (callerIsTest)
            {
                _edges.Add(new GraphEdge(targetId, callerId, EdgeKind.TestedBy));
            }
        }
    }

    private void AddEdgeTo(string fromId, ISymbol target, EdgeKind kind)
    {
        var targetId = IdOf(target.OriginalDefinition);
        if (targetId is not null)
        {
            _edges.Add(new GraphEdge(fromId, targetId, kind));
        }
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
}
