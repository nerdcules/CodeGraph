using CodeGraph.Core;

namespace CodeGraph.Tests;

// The parity seeds are split over two classes so xUnit runs them side by side: the SQL side is the slow one.
public sealed class GraphSnapshotParityTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Every_read_answers_what_the_sql_answers(int seed) => GraphSnapshotTests.Parity(seed);
}

public sealed class GraphSnapshotParityMoreTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Every_read_answers_what_the_sql_answers(int seed) => GraphSnapshotTests.Parity(seed);
}

/// <summary>
/// The in-memory snapshot must answer exactly what the SQL answers. The SQL is the reference: every read is run both
/// ways over random graphs shaped like real ones (containment, call cycles and self-calls, interface and override
/// dispatch, framework calls, tests, ids only an edge names, overloads sharing a full name) and the rows compared in
/// order.
/// </summary>
public sealed class GraphSnapshotTests
{
    private static readonly string[] SomeKinds = [nameof(EdgeKind.Calls), nameof(EdgeKind.Implements), nameof(EdgeKind.References)];

    internal static void Parity(int seed)
    {
        using var db = new TempDb();
        var (nodes, edges) = RandomGraph(seed);
        db.Store.Replace(full: true, [], nodes, edges, [], [], new Dictionary<string, string>());
        var sql = db.Store;
        var memory = sql.LoadSnapshot();

        Assert.Equal(sql.Count(), memory.Count());
        Assert.Equal(sql.CoverageStats(), memory.CoverageStats());
        Same(sql.UncoveredMethods(), memory.UncoveredMethods());
        Same(sql.UncoveredMethods("F1", limit: 7, offset: 2), memory.UncoveredMethods("F1", limit: 7, offset: 2));
        Same(sql.AllNodes(), memory.AllNodes());
        Same(sql.AllNodes(NodeKind.Method), memory.AllNodes(NodeKind.Method));
        Same(sql.RuntimeRoots(), memory.RuntimeRoots());
        foreach (var (limit, min, offset, external, name) in new[] { (100, 1, 0, false, (string?)null), (5, 0, 3, true, null), (20, 2, 0, true, "T1"), (100, 0, 0, false, "m1") })
        {
            Same(sql.MostCalled(limit, min, offset, external, name).Select(r => $"{r.Method.Id}#{r.CallerCount}"),
                 memory.MostCalled(limit, min, offset, external, name).Select(r => $"{r.Method.Id}#{r.CallerCount}"));
        }

        foreach (var publicApi in new[] { false, true })
        {
            var (sqlSummary, sqlRows) = sql.DeadCodeReport(publicApi, limit: 500);
            var (memorySummary, memoryRows) = memory.DeadCodeReport(publicApi, limit: 500);
            Assert.Equal(sqlSummary, memorySummary);
            Same(sqlRows.Select(r => r.Method.Id + r.Confidence), memoryRows.Select(r => r.Method.Id + r.Confidence));
            Same(sql.DeadCodeReport(publicApi, limit: 3, offset: 2).Candidates.Select(r => r.Method.Id),
                 memory.DeadCodeReport(publicApi, limit: 3, offset: 2).Candidates.Select(r => r.Method.Id));
        }

        foreach (var (query, match) in new[] { ("M1", "Contains"), ("m", "StartsWith"), ("2", "EndsWith"), ("Run", "Exact"), ("T3", "Contains"), ("", "Contains") })
        {
            Same(sql.FindSymbol(query, nameMatch: match, limit: 100), memory.FindSymbol(query, nameMatch: match, limit: 100));
            Same(sql.FindSymbol(query, nameMatch: match, limit: 4, offset: 3), memory.FindSymbol(query, nameMatch: match, limit: 4, offset: 3));
        }

        Same(sql.FindSymbol("M", kind: NodeKind.Method, accessibility: Accessibility.Private, isStatic: true, limit: 100),
             memory.FindSymbol("M", kind: NodeKind.Method, accessibility: Accessibility.Private, isStatic: true, limit: 100));
        Same(sql.FindSymbol("T", typeKind: TypeKind.Interface, limit: 100), memory.FindSymbol("T", typeKind: TypeKind.Interface, limit: 100));
        Same(sql.FindSymbol("M", memberKind: MemberKind.Constructor, isAsync: false, isVirtual: true, limit: 100),
             memory.FindSymbol("M", memberKind: MemberKind.Constructor, isAsync: false, isVirtual: true, limit: 100));

        // The SQL takes milliseconds per walk, so a sample of ids: the dispatch chain, ids only edges name, an unknown
        // id, the most-called members, and a random draw.
        var random = new Random(seed);
        var ids = nodes.Select(n => n.Id).ToList();
        var sample = ids.Where(id => id.StartsWith("M:N.T0.", StringComparison.Ordinal) || id.StartsWith("M:N.T1.", StringComparison.Ordinal))
            .Concat(["M:Missing.N0", "M:Missing.N3", "no-such-id", "T:N.T0", "X:System.Api.Call1", "F:/repo/F1.cs"])
            .Concat(sql.MostCalled(5, 1, 0, includeExternal: true).Select(r => r.Method.Id))
            .Concat(Enumerable.Range(0, 20).Select(_ => ids[random.Next(ids.Count)]))
            .Distinct()
            .ToList();
        foreach (var id in sample)
        {
            Assert.Equal(sql.GetNode(id), memory.GetNode(id));
            Same(sql.Children(id), memory.Children(id));
            Same(sql.MembersOf(id), memory.MembersOf(id));
            Same(sql.MembersOf(id, NodeKind.Method, isVirtual: false, limit: 2, offset: 1), memory.MembersOf(id, NodeKind.Method, isVirtual: false, limit: 2, offset: 1));
            Same(sql.EdgesFrom(id).Select(e => $"{e.Kind}>{e.Target.Id}"), memory.EdgesFrom(id).Select(e => $"{e.Kind}>{e.Target.Id}"));
            Same(sql.EdgesTo(id).Select(e => $"{e.Kind}<{e.Source.Id}"), memory.EdgesTo(id).Select(e => $"{e.Kind}<{e.Source.Id}"));
            var (sqlRefs, sqlTotal) = sql.ReferencesTo(id, 3);
            var (memoryRefs, memoryTotal) = memory.ReferencesTo(id, 3);
            Assert.Equal(sqlTotal, memoryTotal);
            Same(sqlRefs, memoryRefs);
            Same(sql.DirectTests(id), memory.DirectTests(id));
            Assert.Equal(sql.ExplainReachability(id), memory.ExplainReachability(id));
            foreach (var depth in new[] { 1, 3, 20 })
            {
                Same(sql.Callers(id, depth), memory.Callers(id, depth), $"callers {id} {depth}");
                Same(sql.Impact(id, depth), memory.Impact(id, depth), $"impact {id} {depth}");
                Same(sql.TransitiveTests(id, depth), memory.TransitiveTests(id, depth), $"tests {id} {depth}");
            }

            Same(sql.Callees(id, 3), memory.Callees(id, 3));
            Same(sql.Callees(id, 2, includeExternal: true), memory.Callees(id, 2, includeExternal: true));
            Same(sql.Walk(id, SomeKinds, inbound: true, 3), memory.Walk(id, SomeKinds, inbound: true, 3));
            Same(sql.Walk(id, GraphStore.AllEdgeKinds, inbound: false, 4), memory.Walk(id, GraphStore.AllEdgeKinds, inbound: false, 4));

            var sqlSub = sql.SubGraph(id, GraphStore.AllEdgeKinds, 2);
            var memorySub = memory.SubGraph(id, GraphStore.AllEdgeKinds, 2);
            Same(sqlSub.Nodes, memorySub.Nodes);
            Assert.Equal(sqlSub.Edges.Select(e => $"{e.FromId}>{e.ToId}:{e.Kind}").Order(StringComparer.Ordinal),
                         memorySub.Edges.Select(e => $"{e.FromId}>{e.ToId}:{e.Kind}").Order(StringComparer.Ordinal));
        }

        for (var i = 0; i < 40; i++)
        {
            var from = ids[random.Next(ids.Count)];
            var to = ids[random.Next(ids.Count)];
            foreach (var kinds in new[] { [nameof(EdgeKind.Calls)], GraphStore.AllEdgeKinds })
            {
                Assert.Equal(Paths(sql.PathBetween(from, to, kinds, 10, 3)), Paths(memory.PathBetween(from, to, kinds, 10, 3)));
            }
        }

        foreach (var file in nodes.Select(n => n.FilePath).OfType<string>().Distinct())
        {
            foreach (var line in new[] { 1, 7, 40 })
            {
                Assert.Equal(sql.NodeAt(file, line), memory.NodeAt(file, line));
            }
        }
    }

    [Fact]
    public void A_walk_through_a_self_call_reports_the_start_and_hops_are_not_reported()
    {
        using var db = new TempDb();
        db.Store.Write(
        [
            Graph.Node("M:I.Run", NodeKind.Method, "Run", "I.Run"),
            Graph.Node("M:C.Run", NodeKind.Method, "Run", "C.Run"),
            Graph.Node("M:A.Go", NodeKind.Method, "Go", "A.Go"),
        ],
        [
            new GraphEdge("M:C.Run", "M:C.Run", EdgeKind.Calls),
            new GraphEdge("M:C.Run", "M:I.Run", EdgeKind.Implements),
            new GraphEdge("M:A.Go", "M:I.Run", EdgeKind.Calls),
        ]);
        var memory = db.Store.LoadSnapshot();

        // C.Run calls itself (depth 1); A.Go calls the interface member C.Run implements, reached by a hop at depth 0.
        // I.Run is only hopped through, so it is not a caller.
        var callers = memory.Callers("M:C.Run", 1).Select(h => (h.Node.Id, h.Depth)).ToList();
        Assert.Equal([("M:A.Go", 1), ("M:C.Run", 1)], callers);
        Same(db.Store.Callers("M:C.Run", 1), memory.Callers("M:C.Run", 1));
    }

    [Fact]
    public void A_reader_with_a_snapshot_opens_the_database_only_for_what_the_snapshot_lacks()
    {
        using var db = new TempDb();
        db.Store.Replace(full: true, [], Graph.Nodes, Graph.Edges, [], [], new Dictionary<string, string>(),
            [SearchText.Document(Graph.Save, [("SendEmail", 3)])!]);
        var snapshot = db.Store.LoadSnapshot();
        var leases = 0;

        using (var reader = GraphStore.OpenReader(db.DbPath, snapshot, TimeSpan.FromSeconds(5), () => { leases++; return null; }))
        {
            Assert.Equal(3, reader.Impact(Graph.Save, 5).Count); // Handle, SaveTest, and Top through Handle
            Assert.Equal(1, reader.SearchDocumentCount());
            Assert.NotNull(reader.GetNode(Graph.Top));
            Assert.False(reader.IsConnected);
            Assert.Equal(0, leases);

            Assert.Equal(Graph.Save, Assert.Single(reader.Search("send email")).Node.Id);
            Assert.True(reader.IsConnected);
            Assert.Equal(1, leases);
        }

        using (var reader = GraphStore.OpenReader(db.DbPath, snapshot: null, TimeSpan.FromSeconds(5), () => { leases++; return null; }))
        {
            Assert.True(reader.IsConnected);
            Assert.Equal(2, leases);
            Assert.Null(reader.Snapshot);
        }
    }

    [Fact]
    public void The_cache_loads_in_the_background_and_follows_the_file_through_writes_and_swaps()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"));
        var dbPath = Path.Combine(directory, "graph.db");
        try
        {
            using (var writer = new GraphStore(dbPath))
            {
                writer.Replace(full: true, [], Graph.Nodes, Graph.Edges, [], [], new Dictionary<string, string>());
            }

            using var anchor = new GraphAnchor(dbPath, idleRelease: TimeSpan.FromMinutes(1));
            using var cache = new GraphSnapshotCache(dbPath, anchor);
            var first = cache.WaitForCurrent(TimeSpan.FromSeconds(30));
            Assert.NotNull(first);
            Assert.Same(first, cache.Current);
            Assert.Contains("in memory", cache.Describe());

            // An incremental write in place: the stamp moves, the old snapshot is no longer answered.
            anchor.Release();
            using (var writer = new GraphStore(dbPath))
            {
                writer.Replace(full: false, [], [Graph.Node("M:Demo.New", NodeKind.Method, "New", "Demo.New")], [], [], [], new Dictionary<string, string>());
            }

            var second = cache.WaitForCurrent(TimeSpan.FromSeconds(30));
            Assert.NotNull(second);
            Assert.NotSame(first, second);
            Assert.NotNull(second.GetNode("M:Demo.New"));

            // A full rebuild into a fresh file swapped over the old one, while the anchor holds the old instance.
            using (anchor.Acquire())
            {
            }

            var fresh = dbPath + ".tmp";
            using (var writer = new GraphStore(fresh))
            {
                writer.Replace(full: true, [], [Graph.Node("M:Demo.Only", NodeKind.Method, "Only", "Demo.Only")], [], [], [], new Dictionary<string, string>());
            }

            File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow.AddSeconds(5));
            if (OperatingSystem.IsWindows())
            {
                anchor.Release(); // Windows cannot replace an open file; the indexer's swap waits for this
            }

            File.Move(fresh, dbPath, overwrite: true);
            var third = cache.WaitForCurrent(TimeSpan.FromSeconds(30));
            Assert.NotNull(third);
            Assert.Equal((1, 0), third.Count());
            Assert.NotNull(third.GetNode("M:Demo.Only"));
        }
        finally
        {
            TempDb.TryDelete(directory);
        }
    }

    [Fact]
    public void Walks_cost_what_they_reach_not_the_size_of_the_graph()
    {
        // 20,000 methods in chains of ten: the SQL walk scans all of them per level, the snapshot only the chain.
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        for (var i = 0; i < 20_000; i++)
        {
            nodes.Add(Graph.Node($"M:N.M{i}", NodeKind.Method, $"M{i}", $"N.M{i}"));
            if (i % 10 != 0)
            {
                edges.Add(new GraphEdge($"M:N.M{i}", $"M:N.M{i - 1}", EdgeKind.Calls));
            }
        }

        var snapshot = GraphSnapshot.Build(nodes, edges.Select(e => (e.FromId, e.ToId, e.Kind.ToString())).ToList());
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            Assert.Equal(9, snapshot.Impact("M:N.M0", 20).Count);
        }

        // A thousand walks in well under a second; the SQL path takes milliseconds for one.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"1000 walks took {watch.Elapsed}");
    }

    private static void Same(IEnumerable<GraphNode> expected, IEnumerable<GraphNode> actual) =>
        Assert.Equal(expected.Select(n => n.Id).ToList(), actual.Select(n => n.Id).ToList());

    private static void Same(IEnumerable<GraphHit> expected, IEnumerable<GraphHit> actual, string? because = null)
    {
        var e = expected.Select(h => $"{h.Node.Id}@{h.Depth}").ToList();
        var a = actual.Select(h => $"{h.Node.Id}@{h.Depth}").ToList();
        Assert.True(e.SequenceEqual(a), $"{because}\nsql:    {string.Join(", ", e)}\nmemory: {string.Join(", ", a)}");
    }

    private static void Same(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Assert.Equal(expected.ToList(), actual.ToList());

    private static List<string> Paths(IReadOnlyList<IReadOnlyList<GraphNode>> paths) =>
        paths.Select(p => string.Join(" > ", p.Select(n => n.Id))).ToList();

    /// <summary>
    /// A graph with the shapes the walks have to get right. ASCII names only: the SQL orders text by its UTF-8 bytes
    /// and the snapshot by UTF-16 code units, which agree everywhere except between surrogate pairs and U+E000-U+FFFF.
    /// </summary>
    private static (List<GraphNode> Nodes, List<GraphEdge> Edges) RandomGraph(int seed)
    {
        var random = new Random(seed);
        var nodes = new List<GraphNode>();
        var edges = new HashSet<GraphEdge>();
        var methods = new List<string>();
        var types = new List<string>();
        var tests = new List<string>();
        var externals = new List<string>();
        var accessibilities = Enum.GetValues<Accessibility>();
        var files = Enumerable.Range(0, 6).Select(f => $"/repo/F{f}.cs").ToList();
        foreach (var file in files)
        {
            nodes.Add(Graph.Node($"F:{file}", NodeKind.File, Path.GetFileName(file), file, filePath: file));
        }

        var line = 1;
        for (var t = 0; t < 18; t++)
        {
            var file = files[t % files.Count];
            var isTests = t >= 15;
            var type = $"T:N.T{t}";
            types.Add(type);
            nodes.Add(Graph.Node(type, NodeKind.Type, $"T{t}", $"N.T{t}", filePath: file, startLine: line, endLine: line + 30,
                typeKind: t % 4 == 0 ? TypeKind.Interface : TypeKind.Class, accessibility: accessibilities[random.Next(accessibilities.Length)]));
            edges.Add(new GraphEdge($"F:{file}", type, EdgeKind.Contains));
            line++;
            for (var m = 0; m < 6; m++)
            {
                // Two members per type share a name and full name, as overloads do.
                var name = m == 1 ? "M0" : m == 5 ? "Run" : $"M{m}";
                var id = $"M:N.T{t}.M{m}({m})";
                var member = Graph.Node(
                    id,
                    m == 4 && !isTests ? NodeKind.Property : NodeKind.Method,
                    m == 3 && t % 5 == 0 ? $"<M{m}>g" : name,
                    $"N.T{t}.{name}",
                    filePath: random.Next(10) == 0 ? null : file,
                    startLine: random.Next(10) == 0 ? line - 1 : line,
                    endLine: line + random.Next(0, 4),
                    isTest: isTests && m != 0,
                    accessibility: accessibilities[random.Next(accessibilities.Length)],
                    isStatic: random.Next(3) == 0,
                    isVirtual: random.Next(3) == 0,
                    isAsync: random.Next(4) == 0,
                    memberKind: m == 0 && t % 3 == 0 ? MemberKind.Constructor : MemberKind.Ordinary);
                nodes.Add(member);
                line += 2;
                edges.Add(new GraphEdge(type, id, EdgeKind.Contains));
                (member.IsTest ? tests : methods).Add(id);
            }
        }

        for (var e = 0; e < 12; e++)
        {
            var id = $"X:System.Api.Call{e}";
            externals.Add(id);
            nodes.Add(Graph.Node(id, NodeKind.External, $"Call{e}", $"System.Api.Call{e}", filePath: null, startLine: 0, endLine: 0));
        }

        string Any(List<string> list) => list[random.Next(list.Count)];
        var callers = methods.Concat(tests).ToList();
        for (var i = 0; i < 220; i++)
        {
            edges.Add(new GraphEdge(Any(callers), random.Next(12) == 0 ? $"M:Missing.N{random.Next(4)}" : Any(methods), EdgeKind.Calls));
        }

        for (var i = 0; i < 8; i++)
        {
            var method = Any(methods);
            edges.Add(new GraphEdge(method, method, EdgeKind.Calls));
        }

        for (var i = 0; i < 25; i++)
        {
            edges.Add(new GraphEdge($"M:Missing.N{random.Next(4)}", Any(methods), EdgeKind.Calls));
            edges.Add(new GraphEdge(Any(callers), Any(externals), EdgeKind.CallsExternal));
            edges.Add(new GraphEdge(Any(methods), Any(methods), random.Next(2) == 0 ? EdgeKind.Implements : EdgeKind.Overrides));
        }

        for (var i = 0; i < 12; i++)
        {
            edges.Add(new GraphEdge(Any(types), Any(types), (EdgeKind)random.Next((int)EdgeKind.Inherits, (int)EdgeKind.Implements + 1)));
            edges.Add(new GraphEdge(Any(methods), Any(types), EdgeKind.References));
            edges.Add(new GraphEdge(Any(types), Any(types), EdgeKind.References));
            edges.Add(new GraphEdge(Any(methods), Any(tests), EdgeKind.TestedBy));
            edges.Add(new GraphEdge(random.Next(2) == 0 ? "H:Host" : Any(types), Any(methods), EdgeKind.InvokedByFramework));
            edges.Add(new GraphEdge(Any(types), Any(types), random.Next(2) == 0 ? EdgeKind.Injects : EdgeKind.Renders));
        }

        // A dispatch chain three levels deep, and a member that implements a member of a type that is a Type node.
        edges.Add(new GraphEdge(methods[0], methods[1], EdgeKind.Overrides));
        edges.Add(new GraphEdge(methods[1], methods[2], EdgeKind.Overrides));
        edges.Add(new GraphEdge(methods[2], methods[3], EdgeKind.Implements));
        edges.Add(new GraphEdge(types[0], methods[4], EdgeKind.Implements));
        return (nodes, [.. edges]);
    }
}
