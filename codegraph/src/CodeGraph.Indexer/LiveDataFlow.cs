using CodeGraph.Core.Live;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CodeGraph.Indexer;

/// <summary>
/// "What does this parameter flow into" for one method. Roslyn's data-flow analysis says whether each parameter is
/// read, written or captured; a walk over the body then says where each use goes: an argument to which call and
/// parameter, a field or property it is stored in, a return, a lambda that captures it, a branch it decides.
/// Locals assigned from a parameter are followed too, so a value reshaped into a local is not lost. One method,
/// one body: it does not follow the value into the methods it is passed to (callees_of and this tool on the
/// callee do that).
/// </summary>
public static class LiveDataFlowQuery
{
    // Short names (WorkspaceSync.Apply(Solution, IEnumerable<string>)) keep each row readable; the id carries the full name.
    private static readonly SymbolDisplayFormat Short = SymbolDisplayFormat.CSharpShortErrorMessageFormat;

    public static async Task<LiveDataFlow> AnalyzeAsync(Solution solution, ISymbol symbol, CancellationToken cancellationToken)
    {
        if (symbol is not IMethodSymbol method)
        {
            throw new LiveException($"{symbol.ToDisplayString()} is not a method or constructor");
        }

        var reference = method.DeclaringSyntaxReferences.FirstOrDefault()
                        ?? throw new LiveException($"{method.ToDisplayString()} has no source declaration");
        var declaration = await reference.GetSyntaxAsync(cancellationToken);
        var document = solution.GetDocument(reference.SyntaxTree) ?? throw new LiveException("the declaring document is not in the solution");
        var model = await document.GetSemanticModelAsync(cancellationToken) ?? throw new LiveException("no semantic model");

        SyntaxNode? body = declaration switch
        {
            BaseMethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody?.Expression,
            LocalFunctionStatementSyntax f => (SyntaxNode?)f.Body ?? f.ExpressionBody?.Expression,
            AccessorDeclarationSyntax a => (SyntaxNode?)a.Body ?? a.ExpressionBody?.Expression,
            _ => null,
        };
        if (body is null)
        {
            throw new LiveException($"{method.ToDisplayString()} has no body to analyse");
        }

        var analysis = body is BlockSyntax block ? model.AnalyzeDataFlow(block) : body is ExpressionSyntax expression ? model.AnalyzeDataFlow(expression) : null;
        var parameters = new List<LiveParameterFlow>();
        foreach (var parameter in method.Parameters)
        {
            var flows = Trace(parameter, body, model, cancellationToken);
            parameters.Add(new LiveParameterFlow(
                parameter.Name,
                parameter.Type.ToDisplayString(Short),
                analysis?.ReadInside.Contains(parameter, SymbolEqualityComparer.Default) == true,
                analysis?.WrittenInside.Contains(parameter, SymbolEqualityComparer.Default) == true,
                analysis?.Captured.Contains(parameter, SymbolEqualityComparer.Default) == true,
                flows));
        }

        var captured = analysis?.Captured.Where(s => s is ILocalSymbol).Select(s => s.Name).Order(StringComparer.Ordinal).ToList() ?? [];
        var span = declaration.GetLocation().GetLineSpan();
        return new LiveDataFlow(method.ToDisplayString(Short), method.GetDocumentationCommentId(), span.Path, span.StartLinePosition.Line + 1, parameters, captured);
    }

    /// <summary>Uses of <paramref name="parameter"/> and of every local derived from it, to a fixed point.</summary>
    private static List<LiveFlow> Trace(IParameterSymbol parameter, SyntaxNode body, SemanticModel model, CancellationToken cancellationToken)
    {
        var flows = new List<LiveFlow>();
        var tracked = new Dictionary<ISymbol, string?>(SymbolEqualityComparer.Default) { [parameter] = null };
        var queue = new Queue<ISymbol>([parameter]);
        var names = body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().ToList();
        while (queue.TryDequeue(out var current))
        {
            var via = SymbolEqualityComparer.Default.Equals(current, parameter) ? null : current.Name;
            foreach (var name in names.Where(n => n.Identifier.ValueText == current.Name))
            {
                if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(name, cancellationToken).Symbol, current))
                {
                    continue;
                }

                var flow = Classify(name, body, model, cancellationToken);
                if (flow is not null)
                {
                    flows.Add(flow with { Via = via });
                }

                // The value lives on in a local when the use sits in its initializer, on the right of an assignment
                // to it, or in the collection of a foreach: `var path = Path.Combine(root, name)` carries `root`.
                if (DerivedLocal(name, model, cancellationToken) is { } derived && tracked.TryAdd(derived, current.Name))
                {
                    queue.Enqueue(derived);
                    if (flow?.Kind != "local")
                    {
                        var line = name.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        flows.Add(new LiveFlow(line, "local", $"flows into local '{derived.Name}'", null, via));
                    }
                }
            }
        }

        return flows.DistinctBy(f => (f.Line, f.Kind, f.Detail)).OrderBy(f => f.Line).ToList();
    }

    /// <summary>What the use of a value at <paramref name="name"/> amounts to, from the nearest construct that consumes it.</summary>
    private static LiveFlow? Classify(IdentifierNameSyntax name, SyntaxNode body, SemanticModel model, CancellationToken cancellationToken)
    {
        var line = name.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        if (name.Ancestors().TakeWhile(a => a != body).Any(a => a is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
        {
            return new LiveFlow(line, "captured", "captured by a lambda or local function", null, null);
        }

        for (SyntaxNode? node = name, child = null; node is not null && node != body.Parent; child = node, node = node.Parent)
        {
            switch (node)
            {
                case ArgumentSyntax argument when model.GetOperation(argument, cancellationToken) is IArgumentOperation { Parameter: { } target }:
                    var callee = target.ContainingSymbol;
                    return new LiveFlow(line, "argument", $"passed to {callee.ToDisplayString(Short)} as '{target.Name}'", callee.OriginalDefinition.GetDocumentationCommentId(), null);
                case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator } when model.GetDeclaredSymbol(declarator, cancellationToken) is { } declared:
                    return declared is ILocalSymbol
                        ? new LiveFlow(line, "local", $"assigned to local '{declared.Name}'", null, null)
                        : new LiveFlow(line, "stored", $"stored in {declared.ToDisplayString(Short)}", declared.GetDocumentationCommentId(), null);
                case AssignmentExpressionSyntax assignment when child == assignment.Right:
                    var targetSymbol = model.GetSymbolInfo(assignment.Left, cancellationToken).Symbol;
                    return targetSymbol switch
                    {
                        ILocalSymbol local => new LiveFlow(line, "local", $"assigned to local '{local.Name}'", null, null),
                        IFieldSymbol or IPropertySymbol or IEventSymbol => new LiveFlow(line, "stored", $"stored in {targetSymbol.ToDisplayString(Short)}", targetSymbol.OriginalDefinition.GetDocumentationCommentId(), null),
                        IParameterSymbol p => new LiveFlow(line, "local", $"assigned to parameter '{p.Name}'", null, null),
                        _ => new LiveFlow(line, "stored", $"assigned to {assignment.Left}", null, null),
                    };
                case ReturnStatementSyntax or ArrowExpressionClauseSyntax or YieldStatementSyntax:
                    return new LiveFlow(line, "returned", "returned to the caller", null, null);
                case IfStatementSyntax i when child == i.Condition:
                case WhileStatementSyntax w when child == w.Condition:
                case DoStatementSyntax d when child == d.Condition:
                case ForStatementSyntax f when child == f.Condition:
                case SwitchStatementSyntax s when child == s.Expression:
                case SwitchExpressionSyntax se when child == se.GoverningExpression:
                case ConditionalExpressionSyntax c when child == c.Condition:
                    return new LiveFlow(line, "branch", "decides a branch", null, null);
                case ThrowStatementSyntax or ThrowExpressionSyntax:
                    return new LiveFlow(line, "returned", "thrown to the caller", null, null);
                case StatementSyntax when node != body && body is not ExpressionSyntax:
                    return null; // used, but in nothing that carries the value further (a call's receiver, a discard)
            }

            if (node == body && body is ExpressionSyntax)
            {
                return new LiveFlow(line, "returned", "returned to the caller", null, null);
            }
        }

        return null;
    }

    private static ILocalSymbol? DerivedLocal(SyntaxNode name, SemanticModel model, CancellationToken cancellationToken)
    {
        foreach (var ancestor in name.Ancestors())
        {
            switch (ancestor)
            {
                case AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax:
                    return null;
                case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator }:
                    return model.GetDeclaredSymbol(declarator, cancellationToken) as ILocalSymbol;
                case AssignmentExpressionSyntax assignment when assignment.Right.Span.Contains(name.Span):
                    return model.GetSymbolInfo(assignment.Left, cancellationToken).Symbol as ILocalSymbol;
                case ForEachStatementSyntax forEach when forEach.Expression.Span.Contains(name.Span):
                    return model.GetDeclaredSymbol(forEach, cancellationToken);
                case StatementSyntax:
                    return null;
            }
        }

        return null;
    }
}
