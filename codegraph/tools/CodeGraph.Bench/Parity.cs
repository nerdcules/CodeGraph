using System.Diagnostics;
using CodeGraph.Core;

/// <summary>
/// Runs the graph reads both ways over a real graph, DuckDB and the in-memory snapshot, and reports any answer that
/// differs. The unit tests do this on random graphs; this does it on whatever a real repository's indexer produced.
/// </summary>
internal static class Parity
{
    public static int Run(string dbPath, int sample)
    {
        using var sql = GraphStore.Open(dbPath, readOnly: true, TimeSpan.FromSeconds(5));
        var snapshot = sql.LoadSnapshot();
        var mismatches = new List<string>();
        var checks = 0;
        var sqlTime = TimeSpan.Zero;
        var memoryTime = TimeSpan.Zero;

        void Check<T>(string what, Func<IGraphReads, IEnumerable<T>> query, Func<T, string> key)
        {
            var watch = Stopwatch.StartNew();
            var expected = query(new Reads(sql)).Select(key).ToList();
            sqlTime += watch.Elapsed;
            watch.Restart();
            var actual = query(new Reads(snapshot)).Select(key).ToList();
            memoryTime += watch.Elapsed;
            checks++;
            if (!expected.SequenceEqual(actual))
            {
                var at = expected.Zip(actual).TakeWhile(p => p.First == p.Second).Count();
                mismatches.Add($"{what}: database {expected.Count} rows, memory {actual.Count}; first difference at row {at}: " +
                               $"{expected.ElementAtOrDefault(at) ?? "(none)"} vs {actual.ElementAtOrDefault(at) ?? "(none)"}");
            }
        }

        static string Id(GraphNode n) => n.Id;
        static string Hit(GraphHit h) => $"{h.Node.Id}@{h.Depth}";

        Check("count", r => new[] { r.Count() }, c => c.ToString());
        Check("coverage_stats", r => new[] { r.CoverageStats() }, c => c.ToString());
        Check("uncovered", r => r.UncoveredMethods(null, 500, 0), Id);
        Check("most_called", r => r.MostCalled(100, 1, 0, false, null, null), m => $"{m.Method.Id}#{m.CallerCount}");
        Check("most_called external", r => r.MostCalled(100, 1, 0, true, null, null), m => $"{m.Method.Id}#{m.CallerCount}");
        Check("dead_code", r => new[] { r.DeadCodeReport(false, 500, 0) }, d => d.Summary + string.Join(",", d.Candidates.Select(c => c.Method.Id)));
        Check("dead_code public", r => new[] { r.DeadCodeReport(true, 500, 0) }, d => d.Summary + string.Join(",", d.Candidates.Select(c => c.Method.Id)));
        Check("runtime_roots", r => r.RuntimeRoots(500), Id);
        foreach (var query in new[] { "Dispose", "Get", "Async", "Controller", "Test" })
        {
            Check($"find_symbol {query}", r => r.FindSymbol(query, null, 100, null, null, null, null, null, null, null, null, "Contains", 0, null), Id);
            Check($"find_symbol exact {query}", r => r.FindSymbol(query, null, 100, null, null, null, null, null, null, null, null, "Exact", 0, null), Id);
        }

        var all = snapshot.AllNodes();
        var step = Math.Max(1, all.Count / Math.Max(1, sample));
        var ids = all.Where((_, i) => i % step == 0).Select(n => n.Id)
            .Concat(snapshot.MostCalled(20, 1, 0, includeExternal: true).Select(m => m.Method.Id))
            .Distinct()
            .ToList();
        foreach (var id in ids)
        {
            Check($"callers {id}", r => r.Callers(id, 1), Hit);
            Check($"callers d5 {id}", r => r.Callers(id, 5), Hit);
            Check($"impact d5 {id}", r => r.Impact(id, 5), Hit);
            Check($"impact d20 {id}", r => r.Impact(id, 20), Hit);
            Check($"tests {id}", r => r.TransitiveTests(id, 10), Hit);
            Check($"callees {id}", r => r.Callees(id, 3, true), Hit);
            Check($"edges from {id}", r => r.EdgesFrom(id), e => $"{e.Kind}>{e.Node.Id}");
            Check($"edges to {id}", r => r.EdgesTo(id), e => $"{e.Kind}<{e.Node.Id}");
            Check($"children {id}", r => r.Children(id), Id);
            Check($"members {id}", r => r.MembersOf(id), Id);
            Check($"references {id}", r => new[] { r.ReferencesTo(id, 50) }, x => $"{x.Total}:{string.Join(",", x.Nodes.Select(Id))}");
            Check($"direct tests {id}", r => r.DirectTests(id), Id);
            Check($"explain {id}", r => new[] { r.ExplainReachability(id) }, x => x);
            Check($"subgraph {id}", r => new[] { r.SubGraph(id) }, x => string.Join(",", x.Nodes.Select(Id)) + "|" +
                string.Join(",", x.Edges.Select(e => $"{e.FromId}>{e.ToId}:{e.Kind}").Order(StringComparer.Ordinal)));
        }

        var random = new Random(7);
        for (var i = 0; i < Math.Min(100, ids.Count); i++)
        {
            var from = ids[random.Next(ids.Count)];
            var to = ids[random.Next(ids.Count)];
            Check($"path {from} -> {to}", r => r.PathBetween(from, to), p => string.Join(">", p.Select(Id)));
        }

        Console.WriteLine($"parity: {checks} checks over {ids.Count} nodes of {all.Count}; {mismatches.Count} mismatch(es)");
        Console.WriteLine($"time in DuckDB: {sqlTime.TotalSeconds:F1} s; in memory: {memoryTime.TotalSeconds:F2} s; snapshot load {snapshot.LoadTime.TotalMilliseconds:F0} ms");
        foreach (var mismatch in mismatches.Take(20))
        {
            Console.WriteLine("  " + mismatch);
        }

        return mismatches.Count == 0 ? 0 : 2;
    }

    /// <summary>The reads both sides share, so one lambda can ask either.</summary>
    private interface IGraphReads
    {
        (int Nodes, int Edges) Count();
        (int Total, int Covered, int TestMethods) CoverageStats();
        IReadOnlyList<GraphNode> UncoveredMethods(string? file, int limit, int offset);
        IReadOnlyList<(GraphNode Method, int CallerCount)> MostCalled(int limit, int min, int offset, bool external, string? name, string? language);
        (DeadCodeSummary Summary, IReadOnlyList<DeadCodeCandidate> Candidates) DeadCodeReport(bool publicApi, int limit, int offset);
        IReadOnlyList<GraphNode> RuntimeRoots(int limit);
        IReadOnlyList<GraphNode> FindSymbol(string q, NodeKind? k, int limit, TypeKind? tk, Accessibility? a, bool? s, bool? ab, bool? v, bool? asy, bool? o, MemberKind? mk, string match, int offset, string? language);
        IReadOnlyList<GraphHit> Callers(string id, int depth);
        IReadOnlyList<GraphHit> Impact(string id, int depth);
        IReadOnlyList<GraphHit> TransitiveTests(string id, int depth);
        IReadOnlyList<GraphHit> Callees(string id, int depth, bool external);
        IReadOnlyList<(EdgeKind Kind, GraphNode Node)> EdgesFrom(string id);
        IReadOnlyList<(EdgeKind Kind, GraphNode Node)> EdgesTo(string id);
        IReadOnlyList<GraphNode> Children(string id);
        IReadOnlyList<GraphNode> MembersOf(string id);
        (IReadOnlyList<GraphNode> Nodes, int Total) ReferencesTo(string id, int limit);
        IReadOnlyList<GraphNode> DirectTests(string id);
        string ExplainReachability(string id);
        SubGraphResult SubGraph(string id);
        IReadOnlyList<IReadOnlyList<GraphNode>> PathBetween(string from, string to);
    }

    private sealed class Reads : IGraphReads
    {
        private readonly GraphStore? _sql;
        private readonly GraphSnapshot? _memory;

        public Reads(GraphStore sql) => _sql = sql;

        public Reads(GraphSnapshot memory) => _memory = memory;

        public (int Nodes, int Edges) Count() => _sql?.Count() ?? _memory!.Count();
        public (int Total, int Covered, int TestMethods) CoverageStats() => _sql?.CoverageStats() ?? _memory!.CoverageStats();
        public IReadOnlyList<GraphNode> UncoveredMethods(string? file, int limit, int offset) => _sql?.UncoveredMethods(file, limit, offset) ?? _memory!.UncoveredMethods(file, limit, offset);
        public IReadOnlyList<(GraphNode Method, int CallerCount)> MostCalled(int limit, int min, int offset, bool external, string? name, string? language) =>
            _sql?.MostCalled(limit, min, offset, external, name, language) ?? _memory!.MostCalled(limit, min, offset, external, name, language);
        public (DeadCodeSummary Summary, IReadOnlyList<DeadCodeCandidate> Candidates) DeadCodeReport(bool publicApi, int limit, int offset) =>
            _sql?.DeadCodeReport(publicApi, limit, offset) ?? _memory!.DeadCodeReport(publicApi, limit, offset);
        public IReadOnlyList<GraphNode> RuntimeRoots(int limit) => _sql?.RuntimeRoots(limit) ?? _memory!.RuntimeRoots(limit);
        public IReadOnlyList<GraphNode> FindSymbol(string q, NodeKind? k, int limit, TypeKind? tk, Accessibility? a, bool? s, bool? ab, bool? v, bool? asy, bool? o, MemberKind? mk, string match, int offset, string? language) =>
            _sql?.FindSymbol(q, k, limit, tk, a, s, ab, v, asy, o, mk, match, offset, language) ?? _memory!.FindSymbol(q, k, limit, tk, a, s, ab, v, asy, o, mk, match, offset, language);
        public IReadOnlyList<GraphHit> Callers(string id, int depth) => _sql?.Callers(id, depth) ?? _memory!.Callers(id, depth);
        public IReadOnlyList<GraphHit> Impact(string id, int depth) => _sql?.Impact(id, depth) ?? _memory!.Impact(id, depth);
        public IReadOnlyList<GraphHit> TransitiveTests(string id, int depth) => _sql?.TransitiveTests(id, depth) ?? _memory!.TransitiveTests(id, depth);
        public IReadOnlyList<GraphHit> Callees(string id, int depth, bool external) => _sql?.Callees(id, depth, external) ?? _memory!.Callees(id, depth, external);
        public IReadOnlyList<(EdgeKind Kind, GraphNode Node)> EdgesFrom(string id) => _sql?.EdgesFrom(id) ?? _memory!.EdgesFrom(id);
        public IReadOnlyList<(EdgeKind Kind, GraphNode Node)> EdgesTo(string id) => _sql?.EdgesTo(id) ?? _memory!.EdgesTo(id);
        public IReadOnlyList<GraphNode> Children(string id) => _sql?.Children(id) ?? _memory!.Children(id);
        public IReadOnlyList<GraphNode> MembersOf(string id) => _sql?.MembersOf(id) ?? _memory!.MembersOf(id);
        public (IReadOnlyList<GraphNode> Nodes, int Total) ReferencesTo(string id, int limit) => _sql?.ReferencesTo(id, limit) ?? _memory!.ReferencesTo(id, limit);
        public IReadOnlyList<GraphNode> DirectTests(string id) => _sql?.DirectTests(id) ?? _memory!.DirectTests(id);
        public string ExplainReachability(string id) => _sql?.ExplainReachability(id) ?? _memory!.ExplainReachability(id);
        public SubGraphResult SubGraph(string id) => _sql?.SubGraph(id, GraphStore.AllEdgeKinds, 2) ?? _memory!.SubGraph(id, GraphStore.AllEdgeKinds, 2);
        public IReadOnlyList<IReadOnlyList<GraphNode>> PathBetween(string from, string to) =>
            _sql?.PathBetween(from, to, [nameof(EdgeKind.Calls)], 10, 3) ?? _memory!.PathBetween(from, to, [nameof(EdgeKind.Calls)], 10, 3);
    }
}
