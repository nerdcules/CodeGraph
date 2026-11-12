using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeGraph.Core;

namespace CodeGraph.Indexer;

/// <summary>
/// <c>codegraph-index export obsidian</c>: the graph as a folder of Markdown notes for an Obsidian vault. One note per
/// project, one per type (and one per file that declares members outside any type), members as headings so a link can
/// point at one, plus reports for architects and testers. The folder is generated: a run rewrites only notes whose
/// text changed and deletes only notes an earlier run wrote, so people keep their own notes elsewhere in the vault and
/// link into it. <c>.codegraph-export.json</c> beside the notes maps every node id to its note and heading.
/// </summary>
internal static class ObsidianExport
{
    public const string ManifestName = ".codegraph-export.json";
    public const string Marker = "codegraph: generated";
    private const int ListCap = 50;

    private static readonly EdgeKind[] UseKinds = [EdgeKind.Calls, EdgeKind.References, EdgeKind.Injects, EdgeKind.Renders];

    public static int Run(string[] args, TextWriter stdout, Action<string> log)
    {
        string? db = null;
        string? output = null;
        string? language = null;
        string? linkRoot = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db" when i + 1 < args.Length:
                    db = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case "--language" when i + 1 < args.Length:
                    language = args[++i];
                    break;
                case "--link-root" when i + 1 < args.Length:
                    linkRoot = args[++i];
                    break;
                case "-h" or "--help":
                    Usage();
                    return 1;
                default:
                    Console.Error.WriteLine($"unexpected argument: {args[i]}");
                    Usage();
                    return 1;
            }
        }

        if (output is null)
        {
            Usage();
            return 1;
        }

        var dbPath = db is not null ? Path.GetFullPath(db) : Worktree.DefaultDbPath(Directory.GetCurrentDirectory());
        if (!File.Exists(dbPath))
        {
            log($"export obsidian: no graph at {dbPath}");
            return 1;
        }

        var outDir = Path.GetFullPath(output);
        linkRoot ??= Path.GetFileName(outDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        linkRoot = linkRoot.Replace('\\', '/').Trim('/');

        var previous = ReadManifest(outDir);
        if (previous is null && Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
        {
            log($"export obsidian: {outDir} is not empty and has no {ManifestName}; refusing to write into a folder this command did not create");
            return 1;
        }

        Vault vault;
        using (var store = GraphStore.Open(dbPath, readOnly: true, TimeSpan.FromSeconds(30)))
        {
            vault = Build(store.AllNodes(), store.AllEdges(), store.ReadMeta(), language, linkRoot);
        }

        var result = Write(outDir, vault, previous);
        var summary = $"obsidian: {result.Written} written, {result.Unchanged} unchanged, {result.Deleted} deleted in {outDir}";
        if (result.Skipped > 0)
        {
            log($"export obsidian: {result.Skipped} path(s) under {outDir} go through a symbolic link and were left alone");
        }

        stdout.WriteLine(summary);
        return 0;
    }

    private static void Usage()
    {
        Console.Error.WriteLine("usage: codegraph-index export obsidian --out <vault>/<folder> [--db <path>] [--language <name>] [--link-root <path>]");
        Console.Error.WriteLine("       writes one note per project and type, members as headings, and reports, for an Obsidian vault;");
        Console.Error.WriteLine("       rewrites only changed notes and deletes only notes it wrote before (listed in " + ManifestName + ");");
        Console.Error.WriteLine("       --link-root  the folder's path inside the vault, used in links (default: the folder's name)");
    }

    // ---------------------------------------------------------------- model

    /// <summary>The notes to write, by path relative to the output folder, and where each node landed.</summary>
    internal sealed record Vault(SortedDictionary<string, string> Notes, SortedDictionary<string, NoteRef> Nodes, string LinkRoot, string? Commit, string? IndexedAt);

    internal sealed record NoteRef(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("heading")] string? Heading);

    internal sealed record Manifest(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("linkRoot")] string LinkRoot,
        [property: JsonPropertyName("commit")] string? Commit,
        [property: JsonPropertyName("indexedAt")] string? IndexedAt,
        [property: JsonPropertyName("files")] List<string> Files,
        [property: JsonPropertyName("nodes")] SortedDictionary<string, NoteRef> Nodes);

    internal sealed record WriteResult(int Written, int Unchanged, int Deleted, int Skipped = 0);

    /// <summary>A note that holds members: a type, or a file whose members sit outside any type (TypeScript, Python, Go).</summary>
    private sealed class Container(GraphNode node, string? project)
    {
        public GraphNode Node { get; } = node;
        public string? Project { get; } = project;
        public List<GraphNode> Members { get; } = [];
        public Dictionary<string, string> Headings { get; } = new(StringComparer.Ordinal);
        public string Path { get; set; } = "";
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonSerializerOptions YamlOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ---------------------------------------------------------------- build

    internal static Vault Build(IReadOnlyList<GraphNode> allNodes, IReadOnlyList<GraphEdge> allEdges, IReadOnlyDictionary<string, string> meta, string? language, string linkRoot)
    {
        var nodes = allNodes
            .Where(n => n.Kind != NodeKind.External && (language is null || n.Language == language))
            .GroupBy(n => n.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var edges = allEdges.Where(e => nodes.ContainsKey(e.FromId) || nodes.ContainsKey(e.ToId)).ToList();
        // Paths are shown relative to the worktree, or outside a git repo to the folder of what was indexed.
        var root = meta.TryGetValue(MetaKeys.WorktreeRoot, out var worktree) && worktree.Length > 0 ? worktree
            : meta.TryGetValue(MetaKeys.Solution, out var solution) && solution.Length > 0 ? Path.GetDirectoryName(solution) : null;
        meta.TryGetValue(MetaKeys.Commit, out var commit);
        meta.TryGetValue(MetaKeys.IndexedAt, out var indexedAt);

        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in edges.Where(e => e.Kind == EdgeKind.Contains && nodes.ContainsKey(e.ToId)))
        {
            parent.TryAdd(e.ToId, e.FromId);
        }

        var outgoing = edges.ToLookup(e => e.FromId, StringComparer.Ordinal);
        var incoming = edges.ToLookup(e => e.ToId, StringComparer.Ordinal);

        string? ProjectOf(string id)
        {
            for (var (current, hops) = (id, 0); hops < 64 && parent.TryGetValue(current, out var up); hops++)
            {
                if (nodes.TryGetValue(up, out var n) && n.Kind == NodeKind.Project)
                {
                    return n.Name;
                }

                current = up;
            }

            return null;
        }

        // Containers: every type, and every file with members directly under it.
        var containers = new Dictionary<string, Container>(StringComparer.Ordinal);
        foreach (var type in nodes.Values.Where(n => n.Kind == NodeKind.Type))
        {
            containers[type.Id] = new Container(type, ProjectOf(type.Id));
        }

        var memberOf = new Dictionary<string, Container>(StringComparer.Ordinal);
        // A member can sit inside another member (a test case in a describe block, a nested function): it goes to the
        // note of the nearest type or file above it, its heading prefixed with the members in between.
        var nestedTitle = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var member in nodes.Values.Where(IsMember))
        {
            var path = new List<GraphNode>();
            GraphNode? owner = null;
            for (var (current, hops) = (member.Id, 0); hops < 32 && parent.TryGetValue(current, out var up) && nodes.TryGetValue(up, out var next); hops++)
            {
                if (!IsMember(next))
                {
                    owner = next;
                    break;
                }

                path.Insert(0, next);
                current = next.Id;
            }

            if (owner is null)
            {
                continue;
            }

            if (!containers.TryGetValue(owner.Id, out var container))
            {
                if (owner.Kind != NodeKind.File)
                {
                    continue;
                }

                container = containers[owner.Id] = new Container(owner, ProjectOf(owner.Id));
            }

            container.Members.Add(member);
            memberOf[member.Id] = container;
            if (path.Count > 0)
            {
                nestedTitle[member.Id] = string.Join(" › ", path.Select(MemberTitle).Append(MemberTitle(member)));
            }
        }

        // Framework roots are Project-kind nodes too (the runtime that calls entry points); only real projects hold or reference code.
        var projects = nodes.Values.Where(n => n.Kind == NodeKind.Project
                && (outgoing[n.Id].Any(e => e.Kind is EdgeKind.Contains or EdgeKind.DependsOn) || incoming[n.Id].Any(e => e.Kind == EdgeKind.DependsOn)))
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var projectPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            projectPaths[project.Id] = Unique($"Projects/{SafeName(project.Name)}", taken);
        }

        foreach (var c in containers.Values.OrderBy(c => c.Node.FullName, StringComparer.Ordinal).ThenBy(c => c.Node.Id, StringComparer.Ordinal))
        {
            var folder = c.Node.Kind == NodeKind.File ? "Files" : "Types";
            var name = c.Node.Kind == NodeKind.File ? Path.GetFileName(c.Node.FilePath ?? c.Node.FullName) : NoteName(c.Node);
            c.Path = Unique($"{folder}/{SafeName(c.Project ?? "_unassigned")}/{SafeName(name)}", taken);
            var headings = new HashSet<string>(["Members", "Inherits and implements", "Inheritors", "Used by", "Calls into", "Tested by"], StringComparer.OrdinalIgnoreCase);
            foreach (var m in c.Members.OrderBy(m => m.StartLine).ThenBy(m => m.Id, StringComparer.Ordinal))
            {
                c.Headings[m.Id] = Unique(SafeHeading(nestedTitle.GetValueOrDefault(m.Id) ?? MemberTitle(m)), headings);
            }
        }

        var refs = new SortedDictionary<string, NoteRef>(StringComparer.Ordinal);
        foreach (var (id, path) in projectPaths)
        {
            refs[id] = new NoteRef(path + ".md", null);
        }

        foreach (var c in containers.Values)
        {
            refs[c.Node.Id] = new NoteRef(c.Path + ".md", null);
            foreach (var (id, heading) in c.Headings)
            {
                refs[id] = new NoteRef(c.Path + ".md", heading);
            }
        }

        // Links ---------------------------------------------------------------
        string Link(string id, string? label = null)
        {
            if (!refs.TryGetValue(id, out var r))
            {
                return nodes.TryGetValue(id, out var n) ? $"`{Code(n.FullName)}`" : $"`{Code(id)}`";
            }

            var target = $"{linkRoot}/{r.Path[..^3]}";
            if (r.Heading is not null)
            {
                target += "#" + r.Heading;
            }

            label ??= LabelOf(id);
            return $"[[{target}|{WikiLabel(label)}]]";
        }

        string LabelOf(string id)
        {
            if (!nodes.TryGetValue(id, out var n))
            {
                return id;
            }

            return memberOf.TryGetValue(id, out var c) ? $"{ShortName(c.Node, root)}.{nestedTitle.GetValueOrDefault(id) ?? (n.MemberKind == MemberKind.Ordinary ? n.Name : MemberTitle(n))}" : ShortName(n, root);
        }

        string? Owner(string id) => memberOf.TryGetValue(id, out var c) ? c.Node.Id : containers.ContainsKey(id) ? id : null;

        var testsOf = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var e in edges.Where(e => e.Kind == EdgeKind.TestedBy))
        {
            if (!testsOf.TryGetValue(e.FromId, out var set))
            {
                set = testsOf[e.FromId] = new SortedSet<string>(StringComparer.Ordinal);
            }

            set.Add(e.ToId);
        }

        SortedSet<string> TestsOfContainer(Container c)
        {
            var all = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var id in c.Members.Select(m => m.Id).Append(c.Node.Id))
            {
                if (testsOf.TryGetValue(id, out var set))
                {
                    all.UnionWith(set);
                }
            }

            return all;
        }

        var notes = new SortedDictionary<string, string>(StringComparer.Ordinal);

        // Projects ------------------------------------------------------------
        foreach (var project in projects)
        {
            var mine = containers.Values.Where(c => c.Project == project.Name).OrderBy(c => c.Node.FullName, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Node.Id, StringComparer.Ordinal).ToList();
            var dependsOn = outgoing[project.Id].Where(e => e.Kind == EdgeKind.DependsOn).Select(e => e.ToId).Distinct().Order(StringComparer.Ordinal).ToList();
            var dependents = incoming[project.Id].Where(e => e.Kind == EdgeKind.DependsOn).Select(e => e.FromId).Distinct().Order(StringComparer.Ordinal).ToList();
            var isTestProject = mine.Count > 0 && mine.SelectMany(c => c.Members).Any(m => m.IsTest);
            var sb = new StringBuilder();
            Frontmatter(sb, [
                ("codegraph_id", Yaml(project.Id)),
                ("kind", Yaml("Project")),
                ("language", Yaml(project.Language)),
                ("depends_on", YamlList(dependsOn.Select(d => nodes.TryGetValue(d, out var n) ? n.Name : d))),
                ("type_count", mine.Count(c => c.Node.Kind == NodeKind.Type).ToString(CultureInfo.InvariantCulture)),
                ("has_tests", isTestProject ? "true" : "false"),
            ]);
            sb.Append("# ").AppendLine(OneLine(project.Name)).AppendLine();
            Banner(sb);
            sb.AppendLine($"{LanguageName(project.Language)} project · {mine.Count(c => c.Node.Kind == NodeKind.Type)} types · [[{linkRoot}/Reports/Project map|Project map]]").AppendLine();
            Section(sb, "Depends on", dependsOn.Select(d => Link(d)));
            Section(sb, "Used by", dependents.Select(d => Link(d)));
            sb.AppendLine("## Types").AppendLine();
            if (mine.Count == 0)
            {
                sb.AppendLine("None.").AppendLine();
            }

            foreach (var group in mine.GroupBy(c => Namespace(c)).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (group.Key.Length > 0)
                {
                    sb.Append("### ").AppendLine(SafeHeading(group.Key)).AppendLine();
                }

                foreach (var c in group)
                {
                    sb.AppendLine($"- {Link(c.Node.Id)} {Describe(c.Node)}");
                }

                sb.AppendLine();
            }

            notes[projectPaths[project.Id] + ".md"] = sb.ToString();
        }

        // Containers ----------------------------------------------------------
        foreach (var c in containers.Values)
        {
            var node = c.Node;
            var ids = c.Members.Select(m => m.Id).Append(node.Id).ToHashSet(StringComparer.Ordinal);
            var bases = outgoing[node.Id].Where(e => e.Kind is EdgeKind.Inherits or EdgeKind.Implements).Select(e => e.ToId).Distinct().Order(StringComparer.Ordinal).ToList();
            var inheritors = incoming[node.Id].Where(e => e.Kind is EdgeKind.Inherits or EdgeKind.Implements).Select(e => e.FromId).Distinct().ToList();
            var uses = ids.SelectMany(id => incoming[id])
                .Where(e => UseKinds.Contains(e.Kind) && !ids.Contains(e.FromId) && nodes.ContainsKey(e.FromId))
                .Select(e => (From: e.FromId, To: e.ToId))
                .Distinct()
                .ToList();
            var callsInto = ids.SelectMany(id => outgoing[id])
                .Where(e => UseKinds.Contains(e.Kind) && !ids.Contains(e.ToId))
                .Select(e => Owner(e.ToId))
                .OfType<string>()
                .Where(o => o != node.Id)
                .Distinct()
                .OrderBy(LabelOf, StringComparer.OrdinalIgnoreCase).ThenBy(id => id, StringComparer.Ordinal)
                .ToList();
            var tests = TestsOfContainer(c);
            var isTestType = c.Members.Any(m => m.IsTest);

            var sb = new StringBuilder();
            Frontmatter(sb, [
                ("codegraph_id", Yaml(node.Id)),
                ("kind", Yaml(node.Kind.ToString())),
                ("type_kind", Yaml(node.TypeKind.ToString())),
                ("accessibility", Yaml(node.Accessibility.ToString())),
                ("project", Yaml(c.Project ?? "")),
                ("language", Yaml(node.Language)),
                ("file", Yaml(DisplayPath(node.FilePath ?? "", root))),
                ("line", node.StartLine.ToString(CultureInfo.InvariantCulture)),
                ("is_test", isTestType ? "true" : "false"),
                ("tested_by", YamlList(tests.Select(t => Owner(t) is { } o && o != t ? LabelOf(o) : LabelOf(t)).Distinct())),
                ("test_count", tests.Count.ToString(CultureInfo.InvariantCulture)),
                ("inbound_refs", uses.Count.ToString(CultureInfo.InvariantCulture)),
                ("aliases", YamlList([ShortName(node, root)])),
            ]);
            sb.Append("# ").AppendLine(OneLine(ShortName(node, root))).AppendLine();
            Banner(sb);
            var facts = new List<string> { Describe(node).Trim('(', ')') };
            if (c.Project is not null && projects.FirstOrDefault(p => p.Name == c.Project) is { } owner)
            {
                facts.Add(Link(owner.Id));
            }

            if (node.FilePath is { Length: > 0 })
            {
                facts.Add(Source(node, root));
            }

            sb.AppendLine(string.Join(" · ", facts.Where(f => f.Length > 0))).AppendLine();
            if (node.Kind == NodeKind.Type && node.FullName != node.Name)
            {
                sb.AppendLine($"`{Code(node.FullName)}`").AppendLine();
            }

            Section(sb, "Inherits and implements", bases.Select(b => Link(b)));
            Section(sb, "Inheritors", inheritors.OrderBy(LabelOf, StringComparer.OrdinalIgnoreCase).ThenBy(id => id, StringComparer.Ordinal).Select(i => Link(i)));

            sb.AppendLine("## Used by").AppendLine();
            if (uses.Count == 0)
            {
                sb.AppendLine("Nothing in the graph uses it.").AppendLine();
            }
            else
            {
                var groups = uses.GroupBy(u => Owner(u.From) ?? u.From)
                    .OrderBy(g => ProjectLabel(g.Key), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(g => LabelOf(g.Key), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(g => g.Key, StringComparer.Ordinal)
                    .ToList();
                foreach (var g in groups.Take(ListCap))
                {
                    var targetNames = g.Select(u => u.To == node.Id ? "the type" : c.Headings.GetValueOrDefault(u.To, nodes[u.To].Name)).Distinct().Order(StringComparer.Ordinal).ToList();
                    var targets = targetNames.Take(8).Append(targetNames.Count > 8 ? $"+{targetNames.Count - 8} more" : "").Where(t => t.Length > 0);
                    var callers = g.Select(u => u.From).Distinct().OrderBy(LabelOf, StringComparer.OrdinalIgnoreCase).ThenBy(id => id, StringComparer.Ordinal).Take(8)
                        .Select(f => Link(f, nodes[f].Kind == NodeKind.Type ? null : MemberTitle(nodes[f])));
                    var project = ProjectLabel(g.Key);
                    sb.AppendLine($"- {Link(g.Key)}{(project.Length > 0 ? $" ({project})" : "")}: {string.Join(", ", callers)} → {string.Join(", ", targets)}");
                }

                More(sb, groups.Count);
                sb.AppendLine();
            }

            Section(sb, "Calls into", callsInto.Select(o => Link(o)));
            Section(sb, "Tested by", tests.Select(t => Link(t)), "No test calls it directly.");

            sb.AppendLine("## Members").AppendLine();
            if (c.Members.Count == 0)
            {
                sb.AppendLine("None.").AppendLine();
            }

            foreach (var m in c.Members.OrderBy(m => m.StartLine).ThenBy(m => m.Id, StringComparer.Ordinal))
            {
                sb.Append("### ").AppendLine(c.Headings[m.Id]).AppendLine();
                if (m.Signature is { Length: > 0 })
                {
                    sb.AppendLine($"`{Code(m.Signature)}`").AppendLine();
                }

                var mFacts = new List<string> { Describe(m).Trim('(', ')') };
                if (m.FilePath is { Length: > 0 })
                {
                    mFacts.Add(Source(m, root));
                }

                var callers = incoming[m.Id].Where(e => UseKinds.Contains(e.Kind) && nodes.ContainsKey(e.FromId)).Select(e => e.FromId).Distinct().Count();
                mFacts.Add(callers == 1 ? "1 caller" : $"{callers} callers");
                sb.AppendLine(string.Join(" · ", mFacts.Where(f => f.Length > 0))).AppendLine();
                if (testsOf.TryGetValue(m.Id, out var mTests))
                {
                    sb.AppendLine($"Tested by {string.Join(", ", mTests.Take(ListCap).Select(t => Link(t)))}").AppendLine();
                }
            }

            notes[c.Path + ".md"] = sb.ToString();
        }

        string ProjectLabel(string id)
        {
            if (containers.TryGetValue(id, out var c))
            {
                return c.Project ?? "";
            }

            return memberOf.TryGetValue(id, out var mc) ? mc.Project ?? "" : "";
        }

        // Reports -------------------------------------------------------------
        notes["Reports/Project map.md"] = ProjectMap(projects, edges, nodes, Link);
        notes["Reports/Test coverage.md"] = Coverage(containers.Values, TestsOfContainer, Link);
        notes["Reports/Untested public surface.md"] = Untested(containers.Values, testsOf, Link);
        notes["Reports/Entry points.md"] = EntryPoints(edges, nodes, Link, ProjectLabel);
        notes["Reports/Most called.md"] = MostCalled(edges, nodes, Link, ProjectLabel);
        notes["Index.md"] = Index(linkRoot, projects, containers.Values, Link, commit, indexedAt);

        return new Vault(notes, refs, linkRoot, commit, indexedAt);
    }

    /// <summary>
    /// A type note's name: the namespace-qualified name (C#, Java), or, where the full name starts with a file path
    /// (TypeScript's <c>src/app/order:Order</c>, Go's <c>corpus/m0.Ledger</c>), the part after it. The note
    /// already sits in its project's folder, and a path in the name makes an unreadable file name.
    /// </summary>
    private static string NoteName(GraphNode type)
    {
        var full = type.FullName;
        var colon = full.LastIndexOf(':');
        if (colon >= 0 && colon < full.Length - 1)
        {
            return full[(colon + 1)..];
        }

        var slash = full.LastIndexOf('/');
        return slash >= 0 && slash < full.Length - 1 ? full[(slash + 1)..] : full;
    }

    private static string MemberTitle(GraphNode m) => m.MemberKind switch
    {
        MemberKind.Constructor => m.IsStatic ? "static constructor" : "constructor",
        MemberKind.Destructor => "finalizer",
        _ => m.Name,
    };

    private static bool IsMember(GraphNode n) => n.Kind is NodeKind.Method or NodeKind.Property or NodeKind.Field or NodeKind.Event;

    private static string Namespace(Container c)
    {
        if (c.Node.Kind != NodeKind.Type)
        {
            return "Files";
        }

        var suffix = "." + c.Node.Name;
        return c.Node.FullName.EndsWith(suffix, StringComparison.Ordinal) ? c.Node.FullName[..^suffix.Length] : "";
    }

    private static string ProjectMap(List<GraphNode> projects, List<GraphEdge> edges, Dictionary<string, GraphNode> nodes, Func<string, string?, string> link)
    {
        var sb = new StringBuilder();
        Frontmatter(sb, [("kind", Yaml("Report"))]);
        sb.AppendLine("# Project map").AppendLine();
        Banner(sb);
        sb.AppendLine("Project references (DependsOn edges). An arrow points at the project being used.").AppendLine();
        var index = projects.Select((p, i) => (p.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        var deps = edges.Where(e => e.Kind == EdgeKind.DependsOn && index.ContainsKey(e.FromId) && index.ContainsKey(e.ToId))
            .Select(e => (e.FromId, e.ToId)).Distinct().OrderBy(d => index[d.FromId]).ThenBy(d => index[d.ToId]).ToList();
        sb.AppendLine("```mermaid").AppendLine("graph TD");
        foreach (var p in projects)
        {
            sb.AppendLine($"  p{index[p.Id]}[\"{OneLine(p.Name).Replace("\"", "'")}\"]");
        }

        foreach (var (from, to) in deps)
        {
            sb.AppendLine($"  p{index[from]} --> p{index[to]}");
        }

        sb.AppendLine("```").AppendLine();
        sb.AppendLine("## Projects").AppendLine();
        foreach (var p in projects)
        {
            var uses = deps.Where(d => d.FromId == p.Id).Select(d => link(d.ToId, null)).ToList();
            sb.AppendLine($"- {link(p.Id, null)}{(uses.Count > 0 ? " → " + string.Join(", ", uses) : "")}");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    private static string Coverage(IEnumerable<Container> containers, Func<Container, SortedSet<string>> testsOf, Func<string, string?, string> link)
    {
        var sb = new StringBuilder();
        Frontmatter(sb, [("kind", Yaml("Report"))]);
        sb.AppendLine("# Test coverage").AppendLine();
        Banner(sb);
        sb.AppendLine("Tests that call each type or its members directly (TestedBy edges), not a coverage run. A type marked **Check** has no direct test; ask the live panel's Tests tab or `tests_for` for tests that reach it through other code.").AppendLine();
        sb.AppendLine("| Type | Project | Tests that reach it | Status |").AppendLine("| --- | --- | --- | --- |");
        foreach (var c in containers.Where(c => c.Node.Kind == NodeKind.Type && c.Members.Count > 0 && !c.Members.Any(m => m.IsTest))
                     .OrderBy(c => c.Project ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Node.FullName, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Node.Id, StringComparer.Ordinal))
        {
            var tests = testsOf(c);
            var shown = string.Join(", ", tests.Take(5).Select(t => link(t, null)));
            if (tests.Count > 5)
            {
                shown += $", +{tests.Count - 5} more";
            }

            sb.AppendLine($"| {Cell(link(c.Node.Id, null))} | {Cell(c.Project ?? "")} | {Cell(shown)} | {(tests.Count > 0 ? "Direct tests" : "**Check**")} |");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    private static string Untested(IEnumerable<Container> containers, Dictionary<string, SortedSet<string>> testsOf, Func<string, string?, string> link)
    {
        var sb = new StringBuilder();
        Frontmatter(sb, [("kind", Yaml("Report"))]);
        sb.AppendLine("# Untested public surface").AppendLine();
        Banner(sb);
        sb.AppendLine("Public methods outside test code that no test calls directly, by type.").AppendLine();
        var any = false;
        foreach (var c in containers.Where(c => !c.Members.Any(m => m.IsTest)).OrderBy(c => c.Project ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Node.FullName, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Node.Id, StringComparer.Ordinal))
        {
            var untested = c.Members.Where(m => m.Kind == NodeKind.Method && m.Accessibility == Accessibility.Public && !m.IsTest && !testsOf.ContainsKey(m.Id))
                .OrderBy(m => m.StartLine).ToList();
            if (untested.Count == 0)
            {
                continue;
            }

            any = true;
            sb.AppendLine($"- {link(c.Node.Id, null)}{(c.Project is null ? "" : $" ({c.Project})")}: {string.Join(", ", untested.Take(ListCap).Select(m => link(m.Id, MemberTitle(m))))}{(untested.Count > ListCap ? $", +{untested.Count - ListCap} more" : "")}");
        }

        if (!any)
        {
            sb.AppendLine("None.");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    private static string EntryPoints(List<GraphEdge> edges, Dictionary<string, GraphNode> nodes, Func<string, string?, string> link, Func<string, string> project)
    {
        var sb = new StringBuilder();
        Frontmatter(sb, [("kind", Yaml("Report"))]);
        sb.AppendLine("# Entry points").AppendLine();
        Banner(sb);
        sb.AppendLine("Members the runtime or a framework calls: `Main`, HTTP handlers, function triggers, hosted services and the like. A good place to start reading a feature.").AppendLine();
        var roots = edges.Where(e => e.Kind == EdgeKind.InvokedByFramework && nodes.ContainsKey(e.ToId)).Select(e => e.ToId)
            .Concat(nodes.Values.Where(n => n.Kind == NodeKind.Method && n.Name == "Main").Select(n => n.Id))
            .Distinct().Select(id => nodes[id]).OrderBy(n => project(n.Id), StringComparer.OrdinalIgnoreCase).ThenBy(n => n.FullName, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
        if (roots.Count == 0)
        {
            sb.AppendLine("None found.");
        }

        foreach (var group in roots.GroupBy(n => project(n.Id)))
        {
            sb.Append("## ").AppendLine(group.Key.Length > 0 ? SafeHeading(group.Key) : "Other").AppendLine();
            foreach (var n in group)
            {
                sb.AppendLine($"- {link(n.Id, null)}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string MostCalled(List<GraphEdge> edges, Dictionary<string, GraphNode> nodes, Func<string, string?, string> link, Func<string, string> project)
    {
        var sb = new StringBuilder();
        Frontmatter(sb, [("kind", Yaml("Report"))]);
        sb.AppendLine("# Most called").AppendLine();
        Banner(sb);
        sb.AppendLine($"The {ListCap} members with the most distinct callers. Changes here reach furthest.").AppendLine();
        sb.AppendLine("| Member | Project | Callers |").AppendLine("| --- | --- | --- |");
        var counts = edges.Where(e => e.Kind == EdgeKind.Calls && nodes.TryGetValue(e.ToId, out var n) && IsMember(n) && nodes.ContainsKey(e.FromId))
            .GroupBy(e => e.ToId).Select(g => (Id: g.Key, Count: g.Select(e => e.FromId).Distinct().Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => nodes[x.Id].FullName, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal).Take(ListCap);
        foreach (var (id, count) in counts)
        {
            sb.AppendLine($"| {Cell(link(id, null))} | {Cell(project(id))} | {count} |");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    private static string Index(string linkRoot, List<GraphNode> projects, IEnumerable<Container> containers, Func<string, string?, string> link, string? commit, string? indexedAt)
    {
        var sb = new StringBuilder();
        Frontmatter(sb, [("kind", Yaml("Index")), ("commit", Yaml(commit ?? "")), ("indexed_at", Yaml(indexedAt ?? ""))]);
        sb.AppendLine("# Code map").AppendLine();
        Banner(sb);
        var types = containers.Count(c => c.Node.Kind == NodeKind.Type);
        sb.AppendLine($"{projects.Count} projects · {types} types{(commit is { Length: > 0 } ? $" · indexed at `{commit[..Math.Min(7, commit.Length)]}`" : "")}{(indexedAt is { Length: > 0 } ? $" · {indexedAt}" : "")}").AppendLine();
        string R(string name) => $"[[{linkRoot}/Reports/{name}|{name}]]";
        sb.AppendLine("## For developers").AppendLine();
        sb.AppendLine("Open a project, then a type: its members, who uses it, what it calls and which tests guard it. The CodeGraph panel answers deeper questions live (callers, impact, references).").AppendLine();
        sb.AppendLine($"- {R("Entry points")}").AppendLine($"- {R("Most called")}").AppendLine();
        sb.AppendLine("## For architects").AppendLine();
        sb.AppendLine($"- {R("Project map")}").AppendLine("- Query type notes with Dataview: every note carries `project`, `kind`, `type_kind`, `accessibility`, `tested_by` and `inbound_refs`.").AppendLine();
        sb.AppendLine("## For testers").AppendLine();
        sb.AppendLine($"- {R("Test coverage")}").AppendLine($"- {R("Untested public surface")}").AppendLine();
        sb.AppendLine("## For product").AppendLine();
        sb.AppendLine("Feature notes live outside this folder, written by people. Link them to the code they describe with the *Insert code link* command, and each type's backlinks show which features touch it.").AppendLine();
        sb.AppendLine("## Projects").AppendLine();
        foreach (var p in projects)
        {
            sb.AppendLine($"- {link(p.Id, null)}");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    // ---------------------------------------------------------------- write

    internal static WriteResult Write(string outDir, Vault vault, Manifest? previous)
    {
        Directory.CreateDirectory(outDir);
        int written = 0, unchanged = 0, deleted = 0, skipped = 0;
        foreach (var (relative, text) in vault.Notes)
        {
            var path = Path.Combine(outDir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (CrossesLink(outDir, path))
            {
                skipped++;
                continue;
            }

            if (File.Exists(path) && File.ReadAllText(path) == text)
            {
                unchanged++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            written++;
        }

        foreach (var stale in previous?.Files.Where(f => !vault.Notes.ContainsKey(f)) ?? [])
        {
            if (stale.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(stale))
            {
                continue;
            }

            var path = Path.Combine(outDir, stale.Replace('/', Path.DirectorySeparatorChar));
            if (CrossesLink(outDir, path))
            {
                skipped++;
                continue;
            }

            if (!File.Exists(path) || !IsGenerated(path))
            {
                continue;
            }

            File.Delete(path);
            deleted++;
            for (var dir = Path.GetDirectoryName(path); dir is not null && dir.Length > outDir.Length && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any(); dir = Path.GetDirectoryName(dir))
            {
                Directory.Delete(dir);
            }
        }

        var manifest = new Manifest(1, vault.LinkRoot, vault.Commit, vault.IndexedAt, [.. vault.Notes.Keys], vault.Nodes);
        var manifestPath = Path.Combine(outDir, ManifestName);
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        if (!File.Exists(manifestPath) || File.ReadAllText(manifestPath) != json)
        {
            File.WriteAllText(manifestPath, json);
        }

        return new WriteResult(written, unchanged, deleted, skipped);
    }

    /// <summary>
    /// Whether a path under the output folder goes through a symbolic link (the file itself or a folder between):
    /// a link there would make a write or a delete land outside the folder. The folder itself may be a link.
    /// </summary>
    internal static bool CrossesLink(string outDir, string path)
    {
        for (var current = path; current is not null && current.Length > outDir.Length; current = Path.GetDirectoryName(current))
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null)
            {
                return true;
            }
        }

        return false;
    }

    internal static Manifest? ReadManifest(string outDir)
    {
        var path = Path.Combine(outDir, ManifestName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsGenerated(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            for (var i = 0; i < 3 && reader.ReadLine() is { } line; i++)
            {
                if (line.Trim() == Marker)
                {
                    return true;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable, or a link whose target is gone: not ours to delete.
        }

        return false;
    }

    // ---------------------------------------------------------------- text helpers

    private static void Frontmatter(StringBuilder sb, IEnumerable<(string Key, string Value)> fields)
    {
        sb.AppendLine("---").AppendLine(Marker);
        foreach (var (key, value) in fields)
        {
            sb.Append(key).Append(": ").AppendLine(value);
        }

        sb.AppendLine("---");
    }

    private static void Banner(StringBuilder sb) =>
        sb.AppendLine("> [!note] Generated from the code graph").AppendLine("> Rebuilt on every export, so edits here are lost. Link to this note from your own notes instead.").AppendLine();

    private static void Section(StringBuilder sb, string title, IEnumerable<string> items, string? empty = null)
    {
        var list = items.ToList();
        if (list.Count == 0 && empty is null)
        {
            return;
        }

        sb.Append("## ").AppendLine(title).AppendLine();
        if (list.Count == 0)
        {
            sb.AppendLine(empty).AppendLine();
            return;
        }

        foreach (var item in list.Take(ListCap))
        {
            sb.Append("- ").AppendLine(item);
        }

        More(sb, list.Count);
        sb.AppendLine();
    }

    private static void More(StringBuilder sb, int count)
    {
        if (count > ListCap)
        {
            sb.AppendLine($"- … {count - ListCap} more: ask the CodeGraph panel");
        }
    }

    private static string Describe(GraphNode n)
    {
        var parts = new List<string>();
        if (n.Accessibility != Accessibility.None)
        {
            parts.Add(n.Accessibility switch
            {
                Accessibility.ProtectedOrInternal => "protected internal",
                Accessibility.ProtectedAndInternal => "private protected",
                var a => a.ToString().ToLowerInvariant(),
            });
        }

        if (n.IsStatic)
        {
            parts.Add("static");
        }

        if (n.IsAbstract)
        {
            parts.Add("abstract");
        }

        if (n.IsAsync)
        {
            parts.Add("async");
        }

        parts.Add(n.Kind switch
        {
            NodeKind.Type => n.TypeKind == TypeKind.None ? "type" : n.TypeKind switch
            {
                TypeKind.RecordStruct => "record struct",
                var t => t.ToString().ToLowerInvariant(),
            },
            NodeKind.Method => n.MemberKind switch
            {
                MemberKind.Constructor => "constructor",
                MemberKind.Operator => "operator",
                MemberKind.Destructor => "destructor",
                _ => n.IsTest ? "test" : "method",
            },
            NodeKind.File => "file",
            var k => k.ToString().ToLowerInvariant(),
        });
        return $"({string.Join(" ", parts)})";
    }

    private static string Source(GraphNode n, string? root)
    {
        var display = $"{DisplayPath(n.FilePath!, root)}:{n.StartLine}";
        try
        {
            var uri = new Uri(n.FilePath!).AbsoluteUri;
            return $"[`{Code(display)}`]({uri}#L{n.StartLine})";
        }
        catch (UriFormatException)
        {
            return $"`{Code(display)}`";
        }
    }

    private static string ShortName(GraphNode n, string? root) =>
        n.Kind == NodeKind.File ? Path.GetFileName(n.FilePath ?? n.Name) : n.Name;

    private static string DisplayPath(string path, string? root)
    {
        if (root is { Length: > 0 } && path.Length > 0)
        {
            try
            {
                var relative = Path.GetRelativePath(root, path);
                if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                {
                    return relative.Replace('\\', '/');
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return path.Replace('\\', '/');
    }

    private static string LanguageName(string language) => language switch
    {
        Languages.CSharp => "C#",
        Languages.TypeScript => "TypeScript",
        Languages.JavaScript => "JavaScript",
        Languages.Java => "Java",
        var other => other.Length > 0 ? char.ToUpperInvariant(other[0]) + other[1..] : "Unknown",
    };

    /// <summary>A file or folder name Obsidian and every OS accept: no path, link or heading syntax, no trailing dot.</summary>
    internal static string SafeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(ch switch
            {
                '<' => '(',
                '>' => ')',
                '/' or '\\' or ':' or '*' or '?' or '"' or '|' or '#' or '^' or '[' or ']' or '%' => '_',
                _ when char.IsControl(ch) => '_',
                _ => ch,
            });
        }

        var safe = sb.ToString().Trim().TrimEnd('.');
        if (safe.Length > 120)
        {
            safe = safe[..120].TrimEnd('.');
        }

        return safe.Length == 0 ? "_" : safe;
    }

    private static string SafeHeading(string text)
    {
        var safe = new string(OneLine(text).Select(ch => ch is '#' or '|' or '^' or '[' or ']' or ':' or '\\' ? ' ' : ch).ToArray()).Trim();
        return safe.Length == 0 ? "_" : safe;
    }

    private static string Unique(string candidate, HashSet<string> taken)
    {
        var result = candidate;
        for (var n = 2; !taken.Add(result); n++)
        {
            result = $"{candidate} {n}";
        }

        return result;
    }

    private static string WikiLabel(string text) => OneLine(text).Replace("|", "∣").Replace("[", "(").Replace("]", ")");

    private static string Code(string text) => OneLine(text.Replace("`", "'"));

    private static string Cell(string text) => OneLine(text).Replace("|", "\\|");

    /// <summary>Control characters (line breaks included) become spaces: nothing from the code can end a line or a block.</summary>
    private static string OneLine(string text) => text.Any(char.IsControl) ? new string(text.Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray()) : text;

    private static string Yaml(string value) => JsonSerializer.Serialize(value, YamlOptions);

    private static string YamlList(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(Yaml)) + "]";
}
