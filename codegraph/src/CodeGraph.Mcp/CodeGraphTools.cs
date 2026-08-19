using System.ComponentModel;
using System.Text;
using CodeGraph.Core;
using ModelContextProtocol.Server;

namespace CodeGraph.Mcp;

/// <summary>
/// The tools Claude sees. Every result is a compact, line-oriented text block: one node per line as
/// <c>kind  full name  file:start-end  [id]</c>. Ids are Roslyn doc-comment ids and are passed back verbatim.
/// </summary>
[McpServerToolType]
public sealed class CodeGraphTools(GraphStore store)
{
    [McpServerTool(Name = "find_symbol", ReadOnly = true, Idempotent = true)]
    [Description("Find types and members by name (case-insensitive). Returns id, kind, signature and location. Use the id with the other tools.")]
    public string FindSymbol(
        [Description("Name or partial name, e.g. 'SaveWorkout' or 'GymX.Api.Sessions'")] string query,
        [Description("Optional kind filter: Type, Method, Property, Field, Event, File, Project")] string? kind = null,
        [Description("Max results, default 20")] int limit = 20,
        [Description("Type nodes only — Class, Struct, Record, RecordStruct, Interface, Enum, Delegate")] string? typeKind = null,
        [Description("Accessibility filter — Public, Private, Protected, Internal, ProtectedOrInternal, ProtectedAndInternal")] string? accessibility = null,
        [Description("Filter by static modifier")] bool? isStatic = null,
        [Description("Filter by abstract modifier")] bool? isAbstract = null,
        [Description("Filter by virtual modifier")] bool? isVirtual = null,
        [Description("Filter by async modifier")] bool? isAsync = null,
        [Description("Filter by override modifier")] bool? isOverride = null,
        [Description("Method nodes only — Ordinary, Constructor, Destructor, Operator")] string? memberKind = null,
        [Description("How to match the query against name/full_name — Contains (default), StartsWith, EndsWith, Exact")] string nameMatch = "Contains",
        [Description("Skip this many results for paging, default 0")] int offset = 0)
    {
        NodeKind? kindFilter = null;
        if (kind is not null)
        {
            if (!Enum.TryParse<NodeKind>(kind, ignoreCase: true, out var parsed))
            {
                return $"unknown kind '{kind}'. Use one of: {string.Join(", ", Enum.GetNames<NodeKind>())}";
            }

            kindFilter = parsed;
        }

        TypeKind? typeKindFilter = null;
        if (typeKind is not null)
        {
            if (!Enum.TryParse<TypeKind>(typeKind, ignoreCase: true, out var parsedTk))
                return $"unknown typeKind '{typeKind}'. Use one of: {string.Join(", ", Enum.GetNames<TypeKind>())}";
            typeKindFilter = parsedTk;
        }

        Accessibility? accessibilityFilter = null;
        if (accessibility is not null)
        {
            if (!Enum.TryParse<Accessibility>(accessibility, ignoreCase: true, out var parsedAcc))
                return $"unknown accessibility '{accessibility}'. Use one of: {string.Join(", ", Enum.GetNames<Accessibility>())}";
            accessibilityFilter = parsedAcc;
        }

        MemberKind? memberKindFilter = null;
        if (memberKind is not null)
        {
            if (!Enum.TryParse<MemberKind>(memberKind, ignoreCase: true, out var parsedMk))
                return $"unknown memberKind '{memberKind}'. Use one of: {string.Join(", ", Enum.GetNames<MemberKind>())}";
            memberKindFilter = parsedMk;
        }

        var results = store.FindSymbol(
            query, kindFilter, Math.Clamp(limit, 1, 100),
            typeKindFilter, accessibilityFilter, isStatic, isAbstract, isVirtual, isAsync, isOverride, memberKindFilter,
            nameMatch, Math.Max(0, offset));
        return results.Count == 0
            ? $"no symbols match '{query}'. Try a shorter substring or check the index is up to date."
            : Format(results.Select(n => (n, n.Signature)));
    }

    [McpServerTool(Name = "members_of", ReadOnly = true, Idempotent = true)]
    [Description("Members of a type filtered by kind and/or modifiers. More focused than outline — use when you want e.g. only public async methods, or all private fields, of a specific class.")]
    public string MembersOf(
        [Description("Node id of the type from find_symbol")] string id,
        [Description("Member kind: Method, Property, Field, Event")] string? kind = null,
        [Description("Accessibility: Public, Private, Protected, Internal, ProtectedOrInternal, ProtectedAndInternal")] string? accessibility = null,
        [Description("Filter by static modifier")] bool? isStatic = null,
        [Description("Filter by abstract modifier")] bool? isAbstract = null,
        [Description("Filter by async modifier")] bool? isAsync = null,
        [Description("Filter by virtual modifier")] bool? isVirtual = null,
        [Description("Filter by override modifier")] bool? isOverride = null,
        [Description("Max results, default 500")] int limit = 500,
        [Description("Skip this many results for paging, default 0")] int offset = 0)
    {
        var node = store.GetNode(id);
        if (node is null) return NotFound(id);

        NodeKind? kindFilter = null;
        if (kind is not null)
        {
            if (!Enum.TryParse<NodeKind>(kind, ignoreCase: true, out var parsed))
                return $"unknown kind '{kind}'. Use one of: Method, Property, Field, Event";
            kindFilter = parsed;
        }

        Accessibility? accessibilityFilter = null;
        if (accessibility is not null)
        {
            if (!Enum.TryParse<Accessibility>(accessibility, ignoreCase: true, out var parsedAcc))
                return $"unknown accessibility '{accessibility}'. Use one of: {string.Join(", ", Enum.GetNames<Accessibility>())}";
            accessibilityFilter = parsedAcc;
        }

        var members = store.MembersOf(id, kindFilter, accessibilityFilter, isStatic, isAbstract, isAsync, isVirtual, isOverride, limit, offset);
        var header = $"{node.Kind} {node.FullName}";
        return members.Count == 0
            ? $"{header}\n(no members match the filters)"
            : $"{header}\n{Format(members.Select(m => (m, m.Signature)))}";
    }

    [McpServerTool(Name = "outline", ReadOnly = true, Idempotent = true)]
    [Description("List the direct children of a node without bodies: members of a type, types in a file, files in a project. Cheap way to see structure before reading source.")]
    public string Outline([Description("Node id from find_symbol")] string id)
    {
        var node = store.GetNode(id);
        if (node is null)
        {
            return NotFound(id);
        }

        var children = store.Children(id);
        var header = $"{node.Kind} {node.FullName}";
        return children.Count == 0
            ? $"{header}\n(no children)"
            : $"{header}\n{Format(children.Select(c => (c, c.Signature)))}";
    }

    [McpServerTool(Name = "callers_of", ReadOnly = true, Idempotent = true)]
    [Description("Who calls this member. depth 1 = direct callers; higher depth walks the call graph outward.")]
    public string CallersOf(
        [Description("Node id from find_symbol")] string id,
        [Description("How many hops to follow, 1-5, default 1")] int depth = 1) =>
        Traversal(id, store.Callers(id, Math.Clamp(depth, 1, 5)), "callers");

    [McpServerTool(Name = "callees_of", ReadOnly = true, Idempotent = true)]
    [Description("What this member calls (only source in the indexed solution, not framework/NuGet). depth 1 = direct callees.")]
    public string CalleesOf(
        [Description("Node id from find_symbol")] string id,
        [Description("How many hops to follow, 1-5, default 1")] int depth = 1) =>
        Traversal(id, store.Callees(id, Math.Clamp(depth, 1, 5)), "callees");

    [McpServerTool(Name = "impact_of", ReadOnly = true, Idempotent = true)]
    [Description("Everything that could break if this changes: transitive callers, overriders, implementers, subclasses — and which tests guard it. Call this before editing a shared member.")]
    public string ImpactOf(
        [Description("Node id from find_symbol")] string id,
        [Description("Max hops, 1-10, default 5")] int depth = 5)
    {
        var node = store.GetNode(id);
        if (node is null)
        {
            return NotFound(id);
        }

        var hits = store.Impact(id, Math.Clamp(depth, 1, 10));
        var tests = hits.Where(h => h.Node.IsTest).ToList();
        var others = hits.Where(h => !h.Node.IsTest).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"impact of {node.FullName}: {others.Count} dependents, {tests.Count} guarding tests");
        if (others.Count > 0)
        {
            sb.AppendLine("dependents:");
            sb.Append(Format(others.Select(h => (h.Node, (string?)$"depth {h.Depth}"))));
        }

        if (tests.Count > 0)
        {
            sb.AppendLine("tests:");
            sb.Append(Format(tests.Select(h => (h.Node, (string?)$"depth {h.Depth}"))));
        }
        else
        {
            sb.AppendLine("tests: none reach this member — consider adding one before changing it");
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "tests_for", ReadOnly = true, Idempotent = true)]
    [Description("Test methods that call this member directly.")]
    public string TestsFor([Description("Node id from find_symbol")] string id)
    {
        if (store.GetNode(id) is null)
        {
            return NotFound(id);
        }

        var tests = store.DirectTests(id);
        return tests.Count == 0
            ? "no test calls this member directly (try impact_of for transitive coverage)"
            : Format(tests.Select(t => (t, t.Signature)));
    }

    [McpServerTool(Name = "snippet", ReadOnly = true, Idempotent = true)]
    [Description("The source text of one node — just its declaration span, not the whole file. Use after find_symbol/outline once you know exactly what you need.")]
    public async Task<string> Snippet(
        [Description("Node id from find_symbol")] string id,
        [Description("Extra lines of context above and below, default 0")] int context = 0,
        CancellationToken cancellationToken = default)
    {
        var node = store.GetNode(id);
        if (node is null)
        {
            return NotFound(id);
        }

        if (node.FilePath is null || !File.Exists(node.FilePath))
        {
            return $"{node.FullName} has no readable source location ({node.FilePath ?? "no path"})";
        }

        var lines = await File.ReadAllLinesAsync(node.FilePath, cancellationToken);
        var pad = Math.Clamp(context, 0, 50);
        var start = Math.Max(1, node.StartLine - pad);
        var end = Math.Min(lines.Length, node.EndLine + pad);

        var sb = new StringBuilder();
        sb.AppendLine($"// {node.FilePath}:{start}-{end}");
        for (var line = start; line <= end; line++)
        {
            sb.Append(line).Append(": ").AppendLine(lines[line - 1]);
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "graph_stats", ReadOnly = true, Idempotent = true)]
    [Description("Node and edge counts. Useful to confirm the index loaded.")]
    public string GraphStats()
    {
        var (nodes, edges) = store.Count();
        return $"{nodes} nodes, {edges} edges";
    }

    [McpServerTool(Name = "most_called", ReadOnly = true, Idempotent = true)]
    [Description("The most called methods in the codebase, ranked by direct caller count.")]
    public string MostCalled(
        [Description("Max results, default 20, max 100")] int limit = 20,
        [Description("Minimum caller threshold to include, default 1")] int minCallers = 1,
        [Description("Skip this many results for paging, default 0")] int offset = 0)
    {
        var top = store.MostCalled(Math.Clamp(limit, 1, 100), minCallers, offset);

        if (top.Count == 0)
            return $"no methods with at least {minCallers} callers";

        var sb = new StringBuilder();
        sb.AppendLine($"Top {top.Count} most called methods (min {minCallers} callers, offset {offset}):");
        foreach (var (m, count) in top)
        {
            sb.Append("method  ").Append(m.FullName).Append("  ").Append(count).Append(" callers");
            if (m.FilePath is not null) sb.Append("  ").Append(m.FilePath).Append(':').Append(m.StartLine);
            sb.Append("  [").Append(m.Id).Append(']');
            sb.AppendLine();
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "inheritors_of", ReadOnly = true, Idempotent = true)]
    [Description("Types that directly or transitively inherit from this type.")]
    public string InheritorsOf(
        [Description("Node id from find_symbol")] string id,
        [Description("Max hops, 1-10, default 3")] int depth = 3) =>
        Traversal(id, store.Walk(id, [nameof(EdgeKind.Inherits)], inbound: true, Math.Clamp(depth, 1, 10)), "inheritors");

    [McpServerTool(Name = "implementors_of", ReadOnly = true, Idempotent = true)]
    [Description("Types that implement this interface, directly or transitively.")]
    public string ImplementorsOf(
        [Description("Node id from find_symbol")] string id,
        [Description("Max hops, 1-10, default 3")] int depth = 3) =>
        Traversal(id, store.Walk(id, [nameof(EdgeKind.Implements)], inbound: true, Math.Clamp(depth, 1, 10)), "implementors");

    [McpServerTool(Name = "overrides_of", ReadOnly = true, Idempotent = true)]
    [Description("Methods that override this member, directly or transitively.")]
    public string OverridesOf(
        [Description("Node id from find_symbol")] string id,
        [Description("Max hops, 1-10, default 3")] int depth = 3) =>
        Traversal(id, store.Walk(id, [nameof(EdgeKind.Overrides)], inbound: true, Math.Clamp(depth, 1, 10)), "overrides");

    [McpServerTool(Name = "depends_on", ReadOnly = true, Idempotent = true)]
    [Description("Projects (and their transitive dependencies) that this project references.")]
    public string DependsOn(
        [Description("Node id from find_symbol (a Project node)")] string id,
        [Description("Max hops, 1-10, default 5")] int depth = 5) =>
        Traversal(id, store.Walk(id, [nameof(EdgeKind.DependsOn)], inbound: false, Math.Clamp(depth, 1, 10)), "dependencies");

    [McpServerTool(Name = "dependents_of", ReadOnly = true, Idempotent = true)]
    [Description("Projects that depend on this project, directly or transitively.")]
    public string DependentsOf(
        [Description("Node id from find_symbol (a Project node)")] string id,
        [Description("Max hops, 1-10, default 5")] int depth = 5) =>
        Traversal(id, store.Walk(id, [nameof(EdgeKind.DependsOn)], inbound: true, Math.Clamp(depth, 1, 10)), "dependents");

    [McpServerTool(Name = "neighbors", ReadOnly = true, Idempotent = true)]
    [Description("All direct edges in and out of a node across every edge kind. Good for exploring an unfamiliar node without committing to a traversal direction.")]
    public string Neighbors([Description("Node id from find_symbol")] string id)
    {
        var node = store.GetNode(id);
        if (node is null) return NotFound(id);

        var from = store.EdgesFrom(id);
        var to = store.EdgesTo(id);

        if (from.Count == 0 && to.Count == 0)
            return $"{node.FullName}: no edges";

        var sb = new StringBuilder();
        sb.AppendLine($"neighbors of {node.Kind.ToString().ToLowerInvariant()} {node.FullName}:");
        if (from.Count > 0)
        {
            sb.AppendLine($"  outbound ({from.Count}):");
            foreach (var (kind, target) in from)
                sb.Append("    ").Append(kind.ToString().ToLowerInvariant()).Append(" → ")
                  .Append(target.Kind.ToString().ToLowerInvariant()).Append("  ").Append(target.FullName)
                  .Append("  [").Append(target.Id).Append(']').AppendLine();
        }
        if (to.Count > 0)
        {
            sb.AppendLine($"  inbound ({to.Count}):");
            foreach (var (kind, source) in to)
                sb.Append("    ").Append(kind.ToString().ToLowerInvariant()).Append(" ← ")
                  .Append(source.Kind.ToString().ToLowerInvariant()).Append("  ").Append(source.FullName)
                  .Append("  [").Append(source.Id).Append(']').AppendLine();
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "traverse", ReadOnly = true, Idempotent = true)]
    [Description("Generic graph walk: pick any edge kind and direction. Use when the named tools don't cover what you need. Edge kinds: Calls, Inherits, Implements, Overrides, Contains, DependsOn, TestedBy.")]
    public string Traverse(
        [Description("Node id from find_symbol")] string id,
        [Description("Edge kind to follow: Calls, Inherits, Implements, Overrides, Contains, DependsOn, TestedBy")] string edgeKind,
        [Description("Direction: 'inbound' (who points at this node) or 'outbound' (where this node points)")] string direction = "outbound",
        [Description("Max hops, 1-10, default 1")] int depth = 1)
    {
        if (!Enum.TryParse<EdgeKind>(edgeKind, ignoreCase: true, out var parsedKind))
            return $"unknown edge kind '{edgeKind}'. Valid kinds: {string.Join(", ", Enum.GetNames<EdgeKind>())}";

        var inbound = direction.StartsWith("in", StringComparison.OrdinalIgnoreCase);
        var hits = store.Walk(id, [parsedKind.ToString()], inbound, Math.Clamp(depth, 1, 10));
        return Traversal(id, hits, $"{direction} {parsedKind}");
    }

    [McpServerTool(Name = "coverage_stats", ReadOnly = true, Idempotent = true)]
    [Description("Static call-graph reachability coverage: how many production methods are transitively reachable from at least one test method via Calls edges. Not actual line/branch coverage — a method is 'covered' if any test can reach it, regardless of which branches execute.")]
    public string CoverageStats()
    {
        var (total, covered, testMethods) = store.CoverageStats();
        var pct = total == 0 ? 0.0 : Math.Round(covered * 100.0 / total, 1);
        var uncovered = total - covered;
        return
            $"""
             static reachability coverage (production methods reachable from tests via call graph)

             test methods      : {testMethods}
             production methods: {total}
             reachable         : {covered} ({pct}%)
             not reachable     : {uncovered} ({Math.Round(100.0 - pct, 1)}%)

             note: 'reachable' means a test calls the method transitively — it does not mean
             every line or branch executes. Use covered_by to see which tests reach a method,
             or uncovered to list the methods no test path reaches at all.
             """;
    }

    [McpServerTool(Name = "uncovered", ReadOnly = true, Idempotent = true)]
    [Description("Production methods that no test reaches transitively via the call graph. Useful for finding completely untested code. Scope to a file/folder with the 'file' filter.")]
    public string Uncovered(
        [Description("Optional substring to filter by file path, e.g. 'WorkoutService' or 'GymX.Core'")] string? file = null,
        [Description("Max results, default 100")] int limit = 100,
        [Description("Skip this many results for paging, default 0")] int offset = 0)
    {
        var methods = store.UncoveredMethods(file, Math.Clamp(limit, 1, 500), offset);
        if (methods.Count == 0)
            return file is null
                ? "all production methods are reachable from at least one test"
                : $"no uncovered methods matching file pattern '{file}'";

        var sb = new StringBuilder();
        sb.AppendLine($"{methods.Count} uncovered method(s){(file is not null ? $" in '{file}'" : "")}:");
        sb.Append(Format(methods.Select(m => (m, m.Signature))));
        return sb.ToString();
    }

    [McpServerTool(Name = "covered_by", ReadOnly = true, Idempotent = true)]
    [Description("Test methods that transitively reach this member via the call graph. Shows depth (hop count from test to target). Complements tests_for, which only shows direct callers.")]
    public string CoveredBy(
        [Description("Node id from find_symbol")] string id,
        [Description("Max hops to search, 1-15, default 10")] int depth = 10)
    {
        var node = store.GetNode(id);
        if (node is null) return NotFound(id);

        var hits = store.TransitiveTests(id, Math.Clamp(depth, 1, 15));
        if (hits.Count == 0)
            return $"{node.FullName}: not reached by any test within {Math.Clamp(depth, 1, 15)} hops — consider adding coverage";

        var sb = new StringBuilder();
        sb.AppendLine($"{hits.Count} test(s) transitively cover {node.FullName}:");
        sb.Append(Format(hits.Select(h => (h.Node, (string?)$"depth {h.Depth}"))));
        return sb.ToString();
    }

    private string Traversal(string id, IReadOnlyList<GraphHit> hits, string label)
    {
        var node = store.GetNode(id);
        if (node is null)
        {
            return NotFound(id);
        }

        return hits.Count == 0
            ? $"{node.FullName}: no {label} in the indexed source"
            : $"{label} of {node.FullName}:\n{Format(hits.Select(h => (h.Node, (string?)$"depth {h.Depth}")))}";
    }

    private static string NotFound(string id) =>
        $"no node with id '{id}'. Use find_symbol to look it up; ids look like M:Namespace.Type.Method(System.String)";

    private static string Format(IEnumerable<(GraphNode Node, string? Extra)> rows)
    {
        var sb = new StringBuilder();
        foreach (var (node, extra) in rows)
        {
            sb.Append(node.Kind.ToString().ToLowerInvariant()).Append("  ");
            sb.Append(node.FullName);
            if (!string.IsNullOrEmpty(extra))
            {
                sb.Append("  ").Append(extra);
            }

            if (node.FilePath is not null)
            {
                sb.Append("  ").Append(node.FilePath).Append(':').Append(node.StartLine).Append('-').Append(node.EndLine);
            }

            sb.Append("  [").Append(node.Id).Append(']');
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
