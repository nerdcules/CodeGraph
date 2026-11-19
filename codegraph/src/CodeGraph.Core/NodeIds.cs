namespace CodeGraph.Core;

/// <summary>Node ids as a model hands them back.</summary>
public static class NodeIds
{
    /// <summary>The tool arguments that carry a node id.</summary>
    public static readonly IReadOnlySet<string> Arguments = new HashSet<string>(StringComparer.Ordinal) { "id", "from", "to", "symbol" };

    /// <summary>The tools whose id argument is called <c>symbol</c>, because it also takes a dotted name.</summary>
    public static readonly IReadOnlySet<string> SymbolTools = new HashSet<string>(StringComparer.Ordinal) { "live_references", "data_flow", "rename_preview" };

    /// <summary>
    /// Names models give the id argument when they guess it: a Cursor agent called every tool with <c>symbolId</c>,
    /// got "An error occurred invoking", and gave up on the graph.
    /// </summary>
    public static readonly IReadOnlyList<string> Aliases = ["id", "symbol", "symbolId", "symbol_id", "nodeId", "node_id", "memberId", "member_id"];

    /// <summary>
    /// Renames the first alias in <paramref name="arguments"/> to the id argument <paramref name="tool"/> takes,
    /// when the call lacks it. Returns whether it renamed one.
    /// </summary>
    public static bool NameTheId<T>(string tool, IDictionary<string, T> arguments)
    {
        var expected = SymbolTools.Contains(tool) ? "symbol" : "id";
        if (arguments.ContainsKey(expected))
        {
            return false;
        }

        foreach (var alias in Aliases)
        {
            if (alias != expected && arguments.Remove(alias, out var value))
            {
                arguments[expected] = value;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <paramref name="id"/> without the brackets answers print around ids (<c>[M:App.Run]</c>), which a model
    /// sometimes copies along with the id, and without surrounding spaces. No id kind starts with <c>[</c>
    /// (Roslyn doc-comment ids, <c>ts:</c>, <c>java:</c>, <c>scip:</c>), so stripping one pair is safe.
    /// </summary>
    public static string Unwrap(string id)
    {
        var trimmed = id.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']' ? trimmed[1..^1].Trim() : trimmed;
    }

    /// <summary>
    /// The id an argument stands for: <see cref="Unwrap"/>, then a handle becomes its id when <paramref name="handles"/>
    /// knows it. Anything else, a long id or a name, comes back as it was.
    /// </summary>
    public static string Normalize(string raw, NodeHandles? handles)
    {
        var id = Unwrap(raw);
        return handles is not null && NodeHandles.LooksLikeHandle(id) && handles.Resolve(id) is { } full ? full : id;
    }

    /// <summary>
    /// <see cref="Normalize(string, NodeHandles?)"/> with the handles that <paramref name="snapshots"/> has for the
    /// graph. It never waits for a load: the cache has handles on every read path. Only an argument that has the
    /// shape of a handle asks for them; the tool's own lookup then says if the node is still there.
    /// </summary>
    public static string Resolve(string raw, GraphSnapshotCache snapshots)
    {
        var id = Unwrap(raw);
        return NodeHandles.LooksLikeHandle(id) ? Normalize(id, snapshots.Handles()) : id;
    }
}
