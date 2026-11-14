namespace CodeGraph.Core;

/// <summary>
/// The whole graph held in memory, for answering reads the way an IDE does: by following arrays rather than asking a
/// database. Nodes keep their records; edges are compressed adjacency lists (CSR) over integer node numbers, one
/// array pair per direction, with the edge kind beside each entry. A walk is a breadth-first search over those
/// arrays, so its cost is the part of the graph it reaches, not the size of the edges table.
/// <para>
/// Immutable once built and safe to share between concurrent calls. Every method answers exactly what the SQL
/// method of the same name on <see cref="GraphStore"/> answers; the SQL stays as the reference the parity tests
/// compare against, and as the path for graphs without a snapshot. Where SQL leaves the order of ties undefined,
/// this breaks them by id.
/// </para>
/// </summary>
public sealed class GraphSnapshot
{
    private static readonly Dictionary<string, EdgeKind> EdgeKindNames =
        Enum.GetValues<EdgeKind>().ToDictionary(kind => kind.ToString(), StringComparer.Ordinal);

    private static readonly uint DispatchKinds = Bit(EdgeKind.Implements) | Bit(EdgeKind.Overrides);

    private static readonly string[] ImpactEdgeKinds =
    [
        nameof(EdgeKind.Calls),
        nameof(EdgeKind.Overrides),
        nameof(EdgeKind.Implements),
        nameof(EdgeKind.Inherits),
        nameof(EdgeKind.References),
        nameof(EdgeKind.Injects),
        nameof(EdgeKind.Renders),
    ];

    // Every id the graph mentions: node rows first, then ids only an edge names (an implicit constructor, say).
    private readonly Dictionary<string, int> _index;
    private readonly string[] _ids;
    private readonly GraphNode?[] _nodes;
    private readonly int _nodeRows;
    private readonly int _edgeRows;

    // Outbound edges of id i are _outTo[_outStart[i].._outStart[i + 1]], inbound ones _inFrom[...] likewise.
    private readonly int[] _outStart;
    private readonly int[] _outTo;
    private readonly EdgeKind[] _outKind;
    private readonly int[] _inStart;
    private readonly int[] _inFrom;
    private readonly EdgeKind[] _inKind;

    // Each node's place in (full name, id) order, so a walk's hits sort on integers rather than strings.
    private readonly int[] _rank;

    private readonly Lazy<bool[]> _reachedFromTests;
    private readonly Lazy<bool[]> _usedThroughDispatch;

    // Scratch arrays for walks, one set per concurrent walk, sized to this graph and freed with it.
    private readonly System.Collections.Concurrent.ConcurrentBag<Marks> _marks = [];

    private GraphSnapshot(
        Dictionary<string, int> index, string[] ids, GraphNode?[] nodes, int nodeRows, int edgeRows,
        int[] outStart, int[] outTo, EdgeKind[] outKind, int[] inStart, int[] inFrom, EdgeKind[] inKind,
        int searchDocuments, int embeddings)
    {
        _index = index;
        _ids = ids;
        _nodes = nodes;
        _nodeRows = nodeRows;
        _edgeRows = edgeRows;
        _outStart = outStart;
        _outTo = outTo;
        _outKind = outKind;
        _inStart = inStart;
        _inFrom = inFrom;
        _inKind = inKind;
        _rank = new int[nodes.Length];
        var order = Enumerable.Range(0, nodes.Length).ToArray();
        Array.Sort(order, (a, b) =>
        {
            var c = string.CompareOrdinal(nodes[a]?.FullName, nodes[b]?.FullName);
            return c != 0 ? c : string.CompareOrdinal(ids[a], ids[b]);
        });
        for (var r = 0; r < order.Length; r++)
        {
            _rank[order[r]] = r;
        }

        SearchDocuments = searchDocuments;
        Embeddings = embeddings;
        _reachedFromTests = new Lazy<bool[]>(ReachFromTests);
        _usedThroughDispatch = new Lazy<bool[]>(UseThroughDispatch);
    }

    /// <summary>Rows in <c>search_docs</c> when the snapshot was taken.</summary>
    public int SearchDocuments { get; }

    /// <summary>Rows in <c>embeddings</c> when the snapshot was taken.</summary>
    public int Embeddings { get; }

    /// <summary>How long reading the graph and building the arrays took.</summary>
    public TimeSpan LoadTime { get; internal set; }

    /// <summary>
    /// Builds a snapshot from node rows and edge rows as the tables hold them. Edge kinds arrive as stored text; a
    /// kind this build does not know still counts as a row but joins no walk, as it would match no SQL filter.
    /// </summary>
    public static GraphSnapshot Build(
        IReadOnlyCollection<GraphNode> nodes,
        IReadOnlyCollection<(string From, string To, string Kind)> edges,
        int searchDocuments = 0,
        int embeddings = 0)
    {
        var index = new Dictionary<string, int>(nodes.Count + 64, StringComparer.Ordinal);
        var ids = new List<string>(nodes.Count + 64);
        var records = new List<GraphNode?>(nodes.Count + 64);
        foreach (var node in nodes)
        {
            if (index.TryGetValue(node.Id, out var existing))
            {
                records[existing] = node; // the primary key makes this impossible from a table; last wins otherwise
                continue;
            }

            index[node.Id] = records.Count;
            ids.Add(node.Id);
            records.Add(node);
        }

        var nodeRows = records.Count;
        int IdOf(string id)
        {
            if (!index.TryGetValue(id, out var at))
            {
                at = records.Count;
                index[id] = at;
                ids.Add(id);
                records.Add(null);
            }

            return at;
        }

        var from = new List<int>(edges.Count);
        var to = new List<int>(edges.Count);
        var kinds = new List<EdgeKind>(edges.Count);
        foreach (var edge in edges)
        {
            var f = IdOf(edge.From);
            var t = IdOf(edge.To);
            if (EdgeKindNames.TryGetValue(edge.Kind, out var kind))
            {
                from.Add(f);
                to.Add(t);
                kinds.Add(kind);
            }
        }

        var count = records.Count;
        var (outStart, outTo, outKind) = Compress(count, from, to, kinds);
        var (inStart, inFrom, inKind) = Compress(count, to, from, kinds);
        return new GraphSnapshot(index, [.. ids], [.. records], nodeRows, edges.Count, outStart, outTo, outKind, inStart, inFrom, inKind, searchDocuments, embeddings);
    }

    private static (int[] Start, int[] Other, EdgeKind[] Kind) Compress(int count, List<int> keys, List<int> others, List<EdgeKind> kinds)
    {
        var start = new int[count + 1];
        foreach (var key in keys)
        {
            start[key + 1]++;
        }

        for (var i = 0; i < count; i++)
        {
            start[i + 1] += start[i];
        }

        var fill = (int[])start.Clone();
        var other = new int[keys.Count];
        var kind = new EdgeKind[keys.Count];
        for (var e = 0; e < keys.Count; e++)
        {
            var at = fill[keys[e]]++;
            other[at] = others[e];
            kind[at] = kinds[e];
        }

        return (start, other, kind);
    }

    // ── Lookups ──────────────────────────────────────────────────────────────────────────────────────────────────

    public (int Nodes, int Edges) Count() => (_nodeRows, _edgeRows);

    public GraphNode? GetNode(string id) => _index.TryGetValue(id, out var i) ? _nodes[i] : null;

    /// <summary>The node records among <paramref name="ids"/> that exist, by id.</summary>
    public Dictionary<string, GraphNode> NodesById(IEnumerable<string> ids)
    {
        var found = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (GetNode(id) is { } node)
            {
                found[id] = node;
            }
        }

        return found;
    }

    /// <inheritdoc cref="GraphStore.FindSymbol"/>
    public IReadOnlyList<GraphNode> FindSymbol(
        string query,
        NodeKind? kind = null,
        int limit = 20,
        TypeKind? typeKind = null,
        Accessibility? accessibility = null,
        bool? isStatic = null,
        bool? isAbstract = null,
        bool? isVirtual = null,
        bool? isAsync = null,
        bool? isOverride = null,
        MemberKind? memberKind = null,
        string nameMatch = "Contains",
        int offset = 0,
        string? language = null)
    {
        Func<string, bool> matches = nameMatch.ToLowerInvariant() switch
        {
            "startswith" => text => text.StartsWith(query, StringComparison.OrdinalIgnoreCase),
            "endswith" => text => text.EndsWith(query, StringComparison.OrdinalIgnoreCase),
            "exact" => text => text.Equals(query, StringComparison.OrdinalIgnoreCase),
            _ => text => text.Contains(query, StringComparison.OrdinalIgnoreCase),
        };

        var hits = new List<GraphNode>();
        for (var i = 0; i < _nodeRows; i++)
        {
            var n = _nodes[i]!;
            if ((matches(n.Name) || matches(n.FullName))
                && (kind is null || n.Kind == kind)
                && (typeKind is null || n.TypeKind == typeKind)
                && (accessibility is null || n.Accessibility == accessibility)
                && (isStatic is null || n.IsStatic == isStatic)
                && (isAbstract is null || n.IsAbstract == isAbstract)
                && (isVirtual is null || n.IsVirtual == isVirtual)
                && (isAsync is null || n.IsAsync == isAsync)
                && (isOverride is null || n.IsOverride == isOverride)
                && (memberKind is null || n.MemberKind == memberKind)
                && (language is null || n.Language == language))
            {
                hits.Add(n);
            }
        }

        return hits
            .OrderBy(n => n.Name.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(n => n.Name.Length)
            .ThenBy(n => n.FullName, StringComparer.Ordinal)
            .ThenBy(n => n.Id, StringComparer.Ordinal)
            .Skip(Math.Max(0, offset))
            .Take(Math.Max(0, limit))
            .ToList();
    }

    /// <inheritdoc cref="GraphStore.NodeAt"/>
    public GraphNode? NodeAt(string file, int line)
    {
        GraphNode? best = null;
        for (var i = 0; i < _nodeRows; i++)
        {
            var n = _nodes[i]!;
            if (n.FilePath == file && n.StartLine <= line && n.EndLine >= line && n.Kind is not (NodeKind.File or NodeKind.Project or NodeKind.External)
                && (best is null
                    || n.EndLine - n.StartLine < best.EndLine - best.StartLine
                    || (n.EndLine - n.StartLine == best.EndLine - best.StartLine && (n.StartLine > best.StartLine
                        || (n.StartLine == best.StartLine && string.CompareOrdinal(n.Id, best.Id) < 0)))))
            {
                best = n;
            }
        }

        return best;
    }

    /// <inheritdoc cref="GraphStore.Children"/>
    public IReadOnlyList<GraphNode> Children(string id) =>
        Targets(id, EdgeKind.Contains)
            .OrderBy(n => n.StartLine)
            .ThenBy(n => n.Name, StringComparer.Ordinal)
            .ThenBy(n => n.Id, StringComparer.Ordinal)
            .ToList();

    /// <inheritdoc cref="GraphStore.MembersOf"/>
    public IReadOnlyList<GraphNode> MembersOf(
        string typeId,
        NodeKind? kind = null,
        Accessibility? accessibility = null,
        bool? isStatic = null,
        bool? isAbstract = null,
        bool? isAsync = null,
        bool? isVirtual = null,
        bool? isOverride = null,
        int limit = 500,
        int offset = 0) =>
        Targets(typeId, EdgeKind.Contains)
            .Where(n => n.Kind != NodeKind.Type
                        && (kind is null || n.Kind == kind)
                        && (accessibility is null || n.Accessibility == accessibility)
                        && (isStatic is null || n.IsStatic == isStatic)
                        && (isAbstract is null || n.IsAbstract == isAbstract)
                        && (isAsync is null || n.IsAsync == isAsync)
                        && (isVirtual is null || n.IsVirtual == isVirtual)
                        && (isOverride is null || n.IsOverride == isOverride))
            .OrderBy(n => n.StartLine)
            .ThenBy(n => n.Name, StringComparer.Ordinal)
            .ThenBy(n => n.Id, StringComparer.Ordinal)
            .Skip(Math.Max(0, offset))
            .Take(Math.Max(0, limit))
            .ToList();

    /// <inheritdoc cref="GraphStore.AllNodes"/>
    public IReadOnlyList<GraphNode> AllNodes(NodeKind? kind = null) =>
        kind is null
            ? Rows().OrderBy(n => n.Kind.ToString(), StringComparer.Ordinal).ThenBy(n => n.FullName, StringComparer.Ordinal).ThenBy(n => n.Id, StringComparer.Ordinal).ToList()
            : Rows().Where(n => n.Kind == kind).OrderBy(n => n.FullName, StringComparer.Ordinal).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();

    /// <inheritdoc cref="GraphStore.EdgesFrom"/>
    public IReadOnlyList<(EdgeKind Kind, GraphNode Target)> EdgesFrom(string id) => Neighbours(id, inbound: false);

    /// <inheritdoc cref="GraphStore.EdgesTo"/>
    public IReadOnlyList<(EdgeKind Kind, GraphNode Source)> EdgesTo(string id) => Neighbours(id, inbound: true);

    private List<(EdgeKind, GraphNode)> Neighbours(string id, bool inbound)
    {
        var rows = new List<(EdgeKind Kind, GraphNode Node)>();
        if (_index.TryGetValue(id, out var i))
        {
            var (start, other, kinds) = inbound ? (_inStart, _inFrom, _inKind) : (_outStart, _outTo, _outKind);
            for (var p = start[i]; p < start[i + 1]; p++)
            {
                if (_nodes[other[p]] is { } node)
                {
                    rows.Add((kinds[p], node));
                }
            }
        }

        // SQL orders by the stored kind text, not the enum's numbering.
        return rows
            .OrderBy(r => r.Kind.ToString(), StringComparer.Ordinal)
            .ThenBy(r => r.Node.FullName, StringComparer.Ordinal)
            .ThenBy(r => r.Node.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc cref="GraphStore.ReferencesTo"/>
    public (IReadOnlyList<GraphNode> Nodes, int Total) ReferencesTo(string id, int limit)
    {
        var sources = Sources(id, EdgeKind.References).ToList();
        var page = sources
            .OrderBy(n => n.FilePath is null ? 1 : 0)
            .ThenBy(n => n.FilePath, StringComparer.Ordinal)
            .ThenBy(n => n.StartLine)
            .ThenBy(n => n.FullName, StringComparer.Ordinal)
            .ThenBy(n => n.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .ToList();
        return (page, page.Count == 0 ? 0 : sources.Count);
    }

    // ── Walks ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc cref="GraphStore.Callers"/>
    public IReadOnlyList<GraphHit> Callers(string id, int maxDepth = 1) =>
        Walk(id, maxDepth, inbound: true, [nameof(EdgeKind.Calls)], dispatch: true);

    /// <inheritdoc cref="GraphStore.Callees"/>
    public IReadOnlyList<GraphHit> Callees(string id, int maxDepth = 1, bool includeExternal = false) =>
        includeExternal
            ? Walk(id, maxDepth, inbound: false, [nameof(EdgeKind.Calls), nameof(EdgeKind.CallsExternal)])
            : Walk(id, maxDepth, inbound: false, [nameof(EdgeKind.Calls)]);

    /// <inheritdoc cref="GraphStore.Impact"/>
    public IReadOnlyList<GraphHit> Impact(string id, int maxDepth = 5) =>
        Walk(id, maxDepth, inbound: true, ImpactEdgeKinds, dispatch: true);

    /// <inheritdoc cref="GraphStore.Walk(string, string[], bool, int)"/>
    public IReadOnlyList<GraphHit> Walk(string id, string[] edgeKinds, bool inbound, int maxDepth) =>
        Walk(id, maxDepth, inbound, edgeKinds);

    /// <inheritdoc cref="GraphStore.TransitiveTests"/>
    public IReadOnlyList<GraphHit> TransitiveTests(string id, int maxDepth = 10) =>
        Walk(id, maxDepth, inbound: true, [nameof(EdgeKind.Calls)], dispatch: true, testsOnly: true);

    /// <summary>
    /// The SQL walk's semantics, as a breadth-first search. The recursive CTE keeps a row per (id, depth, hop) and
    /// reports each node at its smallest non-hop depth; only nodes below the depth limit are expanded, and dispatch
    /// hops (member → the interface or base member it implements or overrides) cost no depth and are never reported
    /// themselves. So: first the smallest depth of every node, with hops as zero-cost steps; then each node's
    /// reported depth is one more than the smallest depth of a node with an ordinary step into it.
    /// </summary>
    private List<GraphHit> Walk(string id, int maxDepth, bool inbound, string[] edgeKinds, bool dispatch = false, bool testsOnly = false)
    {
        if (!_index.TryGetValue(id, out var origin))
        {
            return [];
        }

        // An external member's callers come in over CallsExternal, which ordinary steps leave out; they seed depth 1.
        var seeds = new List<int>();
        if (inbound && edgeKinds.Contains(nameof(EdgeKind.Calls)) && maxDepth >= 1)
        {
            for (var p = _inStart[origin]; p < _inStart[origin + 1]; p++)
            {
                if (_inKind[p] == EdgeKind.CallsExternal)
                {
                    seeds.Add(_inFrom[p]);
                }
            }
        }

        var marks = _marks.TryTake(out var pooled) ? pooled : new Marks(_nodes.Length);
        try
        {
            return Walk(origin, maxDepth, inbound, edgeKinds, dispatch, testsOnly, marks, seeds);
        }
        finally
        {
            _marks.Add(marks);
        }
    }

    private List<GraphHit> Walk(int origin, int maxDepth, bool inbound, string[] edgeKinds, bool dispatch, bool testsOnly, Marks marks, List<int> seeds)
    {
        marks.Next();
        var mask = Mask(edgeKinds);
        var hops = dispatch && inbound;
        var (start, other, kinds) = inbound ? (_inStart, _inFrom, _inKind) : (_outStart, _outTo, _outKind);
        marks.SetDepth(origin, 0);
        var reached = new List<int> { origin };
        var level = new List<int> { origin };
        for (var d = 0; level.Count > 0; d++)
        {
            if (d >= maxDepth)
            {
                break;
            }

            if (hops)
            {
                for (var i = 0; i < level.Count; i++) // the list grows as hops land in the same level
                {
                    var v = level[i];
                    if (_nodes[v] is not { Kind: not NodeKind.Type })
                    {
                        continue; // a type's Implements and Inherits are not dispatch
                    }

                    for (var p = _outStart[v]; p < _outStart[v + 1]; p++)
                    {
                        var up = _outTo[p];
                        if ((DispatchKinds & Bit(_outKind[p])) != 0 && marks.SetDepth(up, d))
                        {
                            reached.Add(up);
                            level.Add(up);
                        }
                    }
                }
            }

            var next = new List<int>();
            if (d == 0)
            {
                foreach (var seed in seeds)
                {
                    if (marks.SetDepth(seed, 1))
                    {
                        reached.Add(seed);
                        next.Add(seed);
                    }
                }
            }

            foreach (var v in level)
            {
                for (var p = start[v]; p < start[v + 1]; p++)
                {
                    if ((mask & Bit(kinds[p])) != 0 && marks.SetDepth(other[p], d + 1))
                    {
                        reached.Add(other[p]);
                        next.Add(other[p]);
                    }
                }
            }

            level = next;
        }

        var reported = new List<int>();
        foreach (var seed in seeds)
        {
            if (marks.Report(seed, 1))
            {
                reported.Add(seed);
            }
        }

        foreach (var v in reached)
        {
            var dv = marks.Depth(v);
            if (dv >= maxDepth)
            {
                continue;
            }

            for (var p = start[v]; p < start[v + 1]; p++)
            {
                if ((mask & Bit(kinds[p])) != 0 && marks.Report(other[p], dv + 1))
                {
                    reported.Add(other[p]);
                }
            }
        }

        // Sort on (depth, rank): the rank is the node's place in (full name, id) order, fixed when the snapshot was built.
        var keys = new List<long>(reported.Count);
        foreach (var at in reported)
        {
            if (_nodes[at] is { } node && (!testsOnly || node.IsTest))
            {
                keys.Add(((long)marks.Reported(at) << 32) | (uint)_rank[at]);
            }
        }

        var sorted = keys.ToArray();
        Array.Sort(sorted);
        var hits = new List<GraphHit>(sorted.Length);
        var byRank = RankToNode;
        foreach (var key in sorted)
        {
            var node = _nodes[byRank[(int)(key & 0xFFFFFFFF)]]!;
            hits.Add(new GraphHit(node, (int)(key >> 32)));
        }

        return hits;
    }

    private int[]? _rankToNode;

    // Built on first use; two threads racing build the same array, so the race is harmless.
    private int[] RankToNode
    {
        get
        {
            if (_rankToNode is { } known)
            {
                return known;
            }

            var map = new int[_rank.Length];
            for (var i = 0; i < _rank.Length; i++)
            {
                map[_rank[i]] = i;
            }

            return _rankToNode = map;
        }
    }

    /// <summary>
    /// Scratch arrays for one walk at a time, reused across walks: a value counts only when its stamp is this walk's,
    /// so nothing is cleared or allocated per walk however large the graph.
    /// </summary>
    private sealed class Marks(int count)
    {
        private readonly int[] _depthStamp = new int[count];
        private readonly int[] _depth = new int[count];
        private readonly int[] _reportStamp = new int[count];
        private readonly int[] _reported = new int[count];
        private int _walk;

        /// <summary>Starts a walk: every stamp from earlier walks stops counting.</summary>
        public void Next()
        {
            if (_walk == int.MaxValue)
            {
                Array.Clear(_depthStamp);
                Array.Clear(_reportStamp);
                _walk = 0;
            }

            _walk++;
        }

        /// <summary>Records the first (smallest, as levels run in order) depth of <paramref name="node"/>; false if it had one.</summary>
        public bool SetDepth(int node, int depth)
        {
            if (_depthStamp[node] == _walk)
            {
                return false;
            }

            _depthStamp[node] = _walk;
            _depth[node] = depth;
            return true;
        }

        public int Depth(int node) => _depth[node];

        /// <summary>Lowers the reported depth of <paramref name="node"/>; true the first time it is reported at all.</summary>
        public bool Report(int node, int depth)
        {
            if (_reportStamp[node] != _walk)
            {
                _reportStamp[node] = _walk;
                _reported[node] = depth;
                return true;
            }

            if (depth < _reported[node])
            {
                _reported[node] = depth;
            }

            return false;
        }

        public int Reported(int node) => _reported[node];
    }

    /// <inheritdoc cref="GraphStore.PathBetween"/>
    public IReadOnlyList<IReadOnlyList<GraphNode>> PathBetween(string fromId, string toId, string[] edgeKinds, int maxDepth = 10, int maxPaths = 3)
    {
        if (fromId == toId)
        {
            return GetNode(fromId) is { } self ? [[self]] : [];
        }

        if (!_index.TryGetValue(fromId, out var source) || !_index.TryGetValue(toId, out var target))
        {
            return [];
        }

        var mask = Mask(edgeKinds);
        var parents = new Dictionary<int, List<int>> { [source] = [] };
        var frontier = new List<int> { source };
        var found = false;
        for (var depth = 0; depth < maxDepth && frontier.Count > 0 && !found; depth++)
        {
            var level = new Dictionary<int, List<int>>();
            foreach (var from in frontier)
            {
                for (var p = _outStart[from]; p < _outStart[from + 1]; p++)
                {
                    var to = _outTo[p];
                    if ((mask & Bit(_outKind[p])) == 0 || parents.ContainsKey(to))
                    {
                        continue;
                    }

                    if (!level.TryGetValue(to, out var list))
                    {
                        level[to] = list = [];
                    }

                    list.Add(from);
                }
            }

            foreach (var (node, from) in level)
            {
                parents[node] = from;
            }

            found = level.ContainsKey(target);
            frontier = [.. level.Keys];
        }

        if (!found)
        {
            return [];
        }

        var paths = new List<List<int>>();
        var stack = new Stack<List<int>>([[target]]);
        while (stack.Count > 0 && paths.Count < maxPaths)
        {
            var partial = stack.Pop();
            var head = partial[^1];
            if (head == source)
            {
                paths.Add(Enumerable.Reverse(partial).ToList());
                continue;
            }

            foreach (var parent in parents[head].OrderByDescending(i => _ids[i], StringComparer.Ordinal))
            {
                stack.Push([.. partial, parent]);
            }
        }

        return paths
            .Where(p => p.All(i => _nodes[i] is not null))
            .Select(p => (IReadOnlyList<GraphNode>)p.Select(i => _nodes[i]!).ToList())
            .ToList();
    }

    /// <inheritdoc cref="GraphStore.SubGraph"/>
    public SubGraphResult SubGraph(string id, string[] edgeKinds, int maxDepth)
    {
        var outHits = Walk(id, Math.Clamp(maxDepth, 1, 5), inbound: false, edgeKinds);
        var inHits = Walk(id, Math.Clamp(maxDepth, 1, 5), inbound: true, edgeKinds);
        var allNodes = new[] { GetNode(id) }
            .OfType<GraphNode>()
            .Concat(outHits.Concat(inHits).OrderBy(h => h.Depth).Select(h => h.Node))
            .DistinctBy(n => n.Id)
            .Take(150)
            .ToList();

        var mask = Mask(edgeKinds);
        var members = allNodes.Select(n => _index[n.Id]).ToHashSet();
        var edges = new List<SubGraphEdge>();
        foreach (var from in members)
        {
            for (var p = _outStart[from]; p < _outStart[from + 1]; p++)
            {
                if ((mask & Bit(_outKind[p])) != 0 && members.Contains(_outTo[p]))
                {
                    edges.Add(new SubGraphEdge(_nodes[from]!.Id, _nodes[_outTo[p]]!.Id, _outKind[p].ToString()));
                }
            }
        }

        return new SubGraphResult(allNodes, edges);
    }

    // ── Tests, coverage, dead code ───────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc cref="GraphStore.DirectTests"/>
    public IReadOnlyList<GraphNode> DirectTests(string id)
    {
        if (!_index.TryGetValue(id, out var origin))
        {
            return [];
        }

        // The member and, transitively, every interface or base member it implements or overrides.
        var up = new HashSet<int> { origin };
        var queue = new Queue<int>([origin]);
        while (queue.Count > 0)
        {
            foreach (var next in DispatchUp(queue.Dequeue()))
            {
                if (up.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        var tests = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        foreach (var member in up)
        {
            for (var p = _outStart[member]; p < _outStart[member + 1]; p++)
            {
                if (_outKind[p] == EdgeKind.TestedBy && _nodes[_outTo[p]] is { } test)
                {
                    tests[test.Id] = test;
                }
            }
        }

        return tests.Values.OrderBy(n => n.FullName, StringComparer.Ordinal).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
    }

    /// <inheritdoc cref="GraphStore.CoverageStats"/>
    public (int Total, int Covered, int TestMethods) CoverageStats()
    {
        var reached = _reachedFromTests.Value;
        int total = 0, covered = 0, tests = 0;
        for (var i = 0; i < _nodeRows; i++)
        {
            var n = _nodes[i]!;
            if (n.Kind != NodeKind.Method)
            {
                continue;
            }

            if (n.IsTest)
            {
                tests++;
            }
            else
            {
                total++;
                if (reached[i])
                {
                    covered++;
                }
            }
        }

        return (total, covered, tests);
    }

    /// <inheritdoc cref="GraphStore.UncoveredMethods"/>
    public IReadOnlyList<GraphNode> UncoveredMethods(string? filePattern = null, int limit = 200, int offset = 0)
    {
        var reached = _reachedFromTests.Value;
        var rows = new List<GraphNode>();
        for (var i = 0; i < _nodeRows; i++)
        {
            var n = _nodes[i]!;
            if (n.Kind == NodeKind.Method && !n.IsTest && !reached[i]
                && (filePattern is null || n.FilePath?.Contains(filePattern, StringComparison.OrdinalIgnoreCase) == true))
            {
                rows.Add(n);
            }
        }

        return rows
            .OrderBy(n => n.FullName, StringComparer.Ordinal)
            .ThenBy(n => n.Id, StringComparer.Ordinal)
            .Skip(Math.Max(0, offset))
            .Take(Math.Max(0, limit))
            .ToList();
    }

    /// <inheritdoc cref="GraphStore.MostCalled"/>
    public IReadOnlyList<(GraphNode Method, int CallerCount)> MostCalled(int limit, int minCallers, int offset, bool includeExternal = false, string? nameFilter = null, string? language = null)
    {
        var rows = new List<(GraphNode Method, int CallerCount)>();
        for (var i = 0; i < _nodeRows; i++)
        {
            var n = _nodes[i]!;
            if ((n.Kind == NodeKind.Method || (includeExternal && n.Kind == NodeKind.External))
                && (nameFilter is null || n.FullName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                && (language is null || n.Language == language))
            {
                var callers = CountInbound(i, EdgeKind.Calls) + CountInbound(i, EdgeKind.CallsExternal);
                if (callers >= minCallers)
                {
                    rows.Add((n, callers));
                }
            }
        }

        return rows
            .OrderByDescending(r => r.CallerCount)
            .ThenBy(r => r.Method.FullName, StringComparer.Ordinal)
            .ThenBy(r => r.Method.Id, StringComparer.Ordinal)
            .Skip(Math.Max(0, offset))
            .Take(Math.Clamp(limit, 1, 100))
            .ToList();
    }

    /// <inheritdoc cref="GraphStore.DeadCodeReport"/>
    public (DeadCodeSummary Summary, IReadOnlyList<DeadCodeCandidate> Candidates) DeadCodeReport(bool includePublicApi = false, int limit = 100, int offset = 0)
    {
        var dispatched = _usedThroughDispatch.Value;
        int total = 0, uncalled = 0, viaDispatch = 0, framework = 0, dead = 0;
        var candidates = new List<GraphNode>();
        for (var i = 0; i < _nodeRows; i++)
        {
            var n = _nodes[i]!;
            if (n.Kind != NodeKind.Method || n.MemberKind != MemberKind.Ordinary || n.IsTest || n.Name.StartsWith('<')
                || (!includePublicApi && n.Accessibility is not (Accessibility.Private or Accessibility.Internal)))
            {
                continue;
            }

            total++;
            if (CountInbound(i, EdgeKind.Calls) > 0)
            {
                continue;
            }

            uncalled++;
            var byDispatch = dispatched[i];
            var byFramework = CountInbound(i, EdgeKind.InvokedByFramework) > 0;
            if (byDispatch)
            {
                viaDispatch++;
            }

            if (byFramework)
            {
                framework++;
            }

            if (!byDispatch && !byFramework)
            {
                dead++;
                candidates.Add(n);
            }
        }

        var page = candidates
            .OrderBy(n => n.Accessibility.ToString(), StringComparer.Ordinal)
            .ThenBy(n => n.FilePath is null ? 1 : 0)
            .ThenBy(n => n.FilePath, StringComparer.Ordinal)
            .ThenBy(n => n.StartLine)
            .ThenBy(n => n.Id, StringComparer.Ordinal)
            .Skip(Math.Max(0, offset))
            .Take(Math.Clamp(limit, 1, 500))
            .Select(n => new DeadCodeCandidate(
                n,
                n.Accessibility is Accessibility.Private or Accessibility.Internal ? "high" : "medium",
                "no direct callers, implements or overrides nothing that is called, and no framework-entrypoint edge"))
            .ToList();
        return (new DeadCodeSummary(total, uncalled, viaDispatch, framework, dead), page);
    }

    /// <inheritdoc cref="GraphStore.RuntimeRoots"/>
    public IReadOnlyList<GraphNode> RuntimeRoots(int limit = 200)
    {
        // One row per edge, as the SQL join gives: a member two entry points invoke is listed twice.
        var rows = new List<GraphNode>();
        for (var p = 0; p < _outTo.Length; p++)
        {
            if (_outKind[p] == EdgeKind.InvokedByFramework && _nodes[_outTo[p]] is { } node)
            {
                rows.Add(node);
            }
        }

        return rows
            .OrderBy(n => n.FullName, StringComparer.Ordinal)
            .ThenBy(n => n.Id, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, 500))
            .ToList();
    }

    /// <inheritdoc cref="GraphStore.ExplainReachability"/>
    public string ExplainReachability(string id)
    {
        if (!_index.TryGetValue(id, out var i) || _nodes[i] is null)
        {
            return $"no node with id '{id}'";
        }

        var directCallers = CountInbound(i, EdgeKind.Calls);
        if (directCallers > 0)
        {
            return $"reachable: {directCallers} direct caller(s) in indexed source";
        }

        if (CountInbound(i, EdgeKind.InvokedByFramework) > 0)
        {
            return "reachable: marked as framework/runtime entrypoint";
        }

        if (DispatchedThrough(i) is { } via)
        {
            return $"likely reachable via dispatch: implements or overrides {via.FullName}, which has {via.Callers} caller(s)";
        }

        var overrides = CountInbound(i, EdgeKind.Overrides);
        return overrides > 0
            ? $"possibly reachable via polymorphism: {overrides} override(s) in source"
            : "no direct callers and no dispatch/framework reachability evidence found";
    }

    /// <summary>
    /// The SQL climbs Implements and Overrides edges up to depth 10, keeping a row per (id, depth), and answers the
    /// shallowest depth holding a called node, callers summed per full name at that depth.
    /// </summary>
    private (string FullName, int Callers)? DispatchedThrough(int member)
    {
        var level = new HashSet<int> { member };
        for (var depth = 1; depth <= 10; depth++)
        {
            var next = new HashSet<int>();
            foreach (var v in level)
            {
                for (var p = _outStart[v]; p < _outStart[v + 1]; p++)
                {
                    if ((DispatchKinds & Bit(_outKind[p])) != 0)
                    {
                        next.Add(_outTo[p]);
                    }
                }
            }

            if (next.Count == 0)
            {
                return null;
            }

            var called = next
                .Where(v => _nodes[v] is not null)
                .Select(v => (_nodes[v]!.FullName, Callers: CountInbound(v, EdgeKind.Calls)))
                .Where(r => r.Callers > 0)
                .GroupBy(r => r.FullName, StringComparer.Ordinal)
                .Select(g => (FullName: g.Key, Callers: g.Sum(r => r.Callers)))
                .OrderBy(r => r.FullName, StringComparer.Ordinal)
                .ToList();
            if (called.Count > 0)
            {
                return called[0];
            }

            level = next;
        }

        return null;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────────────

    private IEnumerable<GraphNode> Rows()
    {
        for (var i = 0; i < _nodeRows; i++)
        {
            yield return _nodes[i]!;
        }
    }

    private IEnumerable<GraphNode> Targets(string id, EdgeKind kind)
    {
        if (!_index.TryGetValue(id, out var i))
        {
            yield break;
        }

        for (var p = _outStart[i]; p < _outStart[i + 1]; p++)
        {
            if (_outKind[p] == kind && _nodes[_outTo[p]] is { } node)
            {
                yield return node;
            }
        }
    }

    private IEnumerable<GraphNode> Sources(string id, EdgeKind kind)
    {
        if (!_index.TryGetValue(id, out var i))
        {
            yield break;
        }

        for (var p = _inStart[i]; p < _inStart[i + 1]; p++)
        {
            if (_inKind[p] == kind && _nodes[_inFrom[p]] is { } node)
            {
                yield return node;
            }
        }
    }

    private int CountInbound(int i, EdgeKind kind)
    {
        var count = 0;
        for (var p = _inStart[i]; p < _inStart[i + 1]; p++)
        {
            if (_inKind[p] == kind)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Members <paramref name="v"/> implements or overrides; a type's Implements and Inherits are not dispatch.</summary>
    private IEnumerable<int> DispatchUp(int v)
    {
        if (_nodes[v] is not { Kind: not NodeKind.Type })
        {
            yield break;
        }

        for (var p = _outStart[v]; p < _outStart[v + 1]; p++)
        {
            if ((DispatchKinds & Bit(_outKind[p])) != 0)
            {
                yield return _outTo[p];
            }
        }
    }

    /// <summary>Members that implement or override <paramref name="v"/>: the forward direction of dispatch.</summary>
    private IEnumerable<int> DispatchDown(int v)
    {
        for (var p = _inStart[v]; p < _inStart[v + 1]; p++)
        {
            if ((DispatchKinds & Bit(_inKind[p])) != 0 && _nodes[_inFrom[p]] is { Kind: not NodeKind.Type })
            {
                yield return _inFrom[p];
            }
        }
    }

    /// <summary>Everything test methods reach over Calls, a call to an interface or base member reaching its implementations.</summary>
    private bool[] ReachFromTests()
    {
        var seeds = new List<int>();
        for (var i = 0; i < _nodeRows; i++)
        {
            if (_nodes[i] is { IsTest: true, Kind: NodeKind.Method })
            {
                seeds.Add(i);
            }
        }

        return Closure(seeds, v =>
        {
            var next = new List<int>();
            for (var p = _outStart[v]; p < _outStart[v + 1]; p++)
            {
                if (_outKind[p] == EdgeKind.Calls)
                {
                    next.Add(_outTo[p]);
                }
            }

            next.AddRange(DispatchDown(v));
            return next;
        });
    }

    /// <summary>Everything something calls, and every implementation or override of it, transitively.</summary>
    private bool[] UseThroughDispatch()
    {
        var seeds = new List<int>();
        for (var i = 0; i < _nodes.Length; i++)
        {
            if (CountInbound(i, EdgeKind.Calls) > 0)
            {
                seeds.Add(i);
            }
        }

        return Closure(seeds, DispatchDown);
    }

    private bool[] Closure(List<int> seeds, Func<int, IEnumerable<int>> step)
    {
        var seen = new bool[_nodes.Length];
        var queue = new Queue<int>();
        foreach (var seed in seeds)
        {
            if (!seen[seed])
            {
                seen[seed] = true;
                queue.Enqueue(seed);
            }
        }

        while (queue.Count > 0)
        {
            foreach (var next in step(queue.Dequeue()))
            {
                if (!seen[next])
                {
                    seen[next] = true;
                    queue.Enqueue(next);
                }
            }
        }

        return seen;
    }

    private static uint Bit(EdgeKind kind) => 1u << (int)kind;

    private static uint Mask(IEnumerable<string> kinds)
    {
        var mask = 0u;
        foreach (var name in kinds)
        {
            if (EdgeKindNames.TryGetValue(name, out var kind))
            {
                mask |= Bit(kind);
            }
        }

        return mask;
    }
}
