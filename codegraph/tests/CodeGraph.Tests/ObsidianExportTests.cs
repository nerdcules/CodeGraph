using System.Text.Json;
using CodeGraph.Core;
using CodeGraph.Indexer;

namespace CodeGraph.Tests;

/// <summary>
/// <c>export obsidian</c>: a folder of notes linked the way Obsidian resolves them, rewritten only where the text
/// changed, and never touching a note it did not write.
/// </summary>
public sealed class ObsidianExportTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"))).FullName;

    public void Dispose() => TempDb.TryDelete(_root);

    private string DbPath => Path.Combine(_root, ".codegraph", "graph.db");

    private string Out => Path.Combine(_root, "vault", "_code");

    private const string App = "P:App";
    private const string Tests = "P:App.Tests";
    private const string Store = "T:App.Store";
    private const string IStore = "T:App.IStore";
    private const string SaveA = "M:App.Store.Save(System.Int32)";
    private const string SaveB = "M:App.Store.Save(System.String)";
    private const string Load = "M:App.Store.Load";
    private const string Box = "T:App.Box`1";
    private const string Client = "T:App.Client";
    private const string Run = "M:App.Client.Run";
    private const string StoreTests = "T:App.Tests.StoreTests";
    private const string SaveWorks = "M:App.Tests.StoreTests.SaveWorks";

    private static GraphNode N(string id, NodeKind kind, string name, string fullName, string file, int line, bool isTest = false,
        TypeKind typeKind = TypeKind.None, Accessibility accessibility = Accessibility.Public, string? signature = null) =>
        new(id, kind, name, fullName, signature, file, line, line + 3, isTest, typeKind, accessibility);

    private List<GraphNode> Nodes(bool withLoad = true)
    {
        var src = Path.Combine(_root, "src", "Store.cs");
        var test = Path.Combine(_root, "tests", "StoreTests.cs");
        var nodes = new List<GraphNode>
        {
            N(App, NodeKind.Project, "App", "App", null!, 0, accessibility: Accessibility.None),
            N(Tests, NodeKind.Project, "App.Tests", "App.Tests", null!, 0, accessibility: Accessibility.None),
            N("F:" + src, NodeKind.File, "Store.cs", src, src, 1, accessibility: Accessibility.None),
            N("F:" + test, NodeKind.File, "StoreTests.cs", test, test, 1, accessibility: Accessibility.None),
            N(Store, NodeKind.Type, "Store", "App.Store", src, 3, typeKind: TypeKind.Class),
            N(IStore, NodeKind.Type, "IStore", "App.IStore", src, 30, typeKind: TypeKind.Interface),
            N(SaveA, NodeKind.Method, "Save", "App.Store.Save", src, 5, signature: "public void Save(int id)"),
            N(SaveB, NodeKind.Method, "Save", "App.Store.Save", src, 10, signature: "public void Save(string name)"),
            N(Box, NodeKind.Type, "Box", "App.Box<T>", src, 40, typeKind: TypeKind.Class),
            N(Client, NodeKind.Type, "Client", "App.Client", src, 50, typeKind: TypeKind.Class),
            N(Run, NodeKind.Method, "Run", "App.Client.Run", src, 52),
            N(StoreTests, NodeKind.Type, "StoreTests", "App.Tests.StoreTests", test, 1, typeKind: TypeKind.Class),
            N(SaveWorks, NodeKind.Method, "SaveWorks", "App.Tests.StoreTests.SaveWorks", test, 3, isTest: true),
        };
        if (withLoad)
        {
            nodes.Add(N(Load, NodeKind.Method, "Load", "App.Store.Load", src, 15));
        }

        return nodes;
    }

    private List<GraphEdge> Edges(bool withLoad = true)
    {
        var src = "F:" + Path.Combine(_root, "src", "Store.cs");
        var test = "F:" + Path.Combine(_root, "tests", "StoreTests.cs");
        var edges = new List<GraphEdge>
        {
            new(App, src, EdgeKind.Contains),
            new(Tests, test, EdgeKind.Contains),
            new(src, Store, EdgeKind.Contains),
            new(src, IStore, EdgeKind.Contains),
            new(src, Box, EdgeKind.Contains),
            new(src, Client, EdgeKind.Contains),
            new(test, StoreTests, EdgeKind.Contains),
            new(Store, SaveA, EdgeKind.Contains),
            new(Store, SaveB, EdgeKind.Contains),
            new(Client, Run, EdgeKind.Contains),
            new(StoreTests, SaveWorks, EdgeKind.Contains),
            new(Store, IStore, EdgeKind.Implements),
            new(Run, SaveA, EdgeKind.Calls),
            new(SaveWorks, SaveA, EdgeKind.Calls),
            new(SaveA, SaveWorks, EdgeKind.TestedBy),
            new(Tests, App, EdgeKind.DependsOn),
        };
        if (withLoad)
        {
            edges.Add(new(Store, Load, EdgeKind.Contains));
            edges.Add(new(Run, Load, EdgeKind.Calls));
        }

        return edges;
    }

    private void Seed(bool withLoad = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        using var store = GraphStore.Open(DbPath, readOnly: false, TimeSpan.FromSeconds(5));
        store.Clear();
        store.Write(Nodes(withLoad), Edges(withLoad));
    }

    private (int Code, string Output) Export(params string[] extra)
    {
        var output = new StringWriter();
        var code = ExportCommand.Run(["obsidian", "--db", DbPath, "--out", Out, .. extra], output, _ => { });
        return (code, output.ToString());
    }

    private string Note(string relative) => File.ReadAllText(Path.Combine(Out, relative.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void Type_notes_carry_frontmatter_member_headings_and_links_that_resolve_in_the_vault()
    {
        Seed();
        var (code, output) = Export();
        Assert.Equal(0, code);
        Assert.Contains("0 deleted", output);

        var store = Note("Types/App/App.Store.md");
        Assert.StartsWith("---\ncodegraph: generated\n", store.ReplaceLineEndings("\n"));
        Assert.Contains("codegraph_id: \"T:App.Store\"", store);
        Assert.Contains("project: \"App\"", store);
        Assert.Contains("tested_by: [\"StoreTests\"]", store);
        Assert.Contains("test_count: 1", store);
        Assert.Contains("file: \"src/Store.cs\"", store.Replace("\\", "/").Replace("file: \"" + _root.Replace("\\", "/") + "/", "file: \""));

        // Overloads get distinct headings, so a link can point at either.
        Assert.Contains("### Save\n", store.ReplaceLineEndings("\n"));
        Assert.Contains("### Save 2\n", store.ReplaceLineEndings("\n"));

        // Used by groups callers by their type and links to the caller's member heading.
        Assert.Contains("[[_code/Types/App/App.Client|Client]] (App): [[_code/Types/App/App.Client#Run|Run]] → Load, Save", store);
        Assert.Contains("[[_code/Types/App/App.IStore|IStore]]", store);
        Assert.Contains("[[_code/Types/App.Tests/App.Tests.StoreTests#SaveWorks|StoreTests.SaveWorks]]", store);
        Assert.Contains("[[_code/Projects/App|App]]", store);

        var iface = Note("Types/App/App.IStore.md");
        Assert.Contains("## Inheritors", iface);
        Assert.Contains("[[_code/Types/App/App.Store|Store]]", iface);

        // A generic type's file name keeps its arity readable without characters Obsidian rejects.
        Assert.True(File.Exists(Path.Combine(Out, "Types", "App", "App.Box(T).md")));
    }

    [Fact]
    public void Projects_and_reports_cover_dependencies_coverage_and_the_index()
    {
        Seed();
        Assert.Equal(0, Export().Code);

        var map = Note("Reports/Project map.md");
        Assert.Contains("```mermaid", map);
        Assert.Contains("p1 --> p0", map); // App.Tests (p1) depends on App (p0)

        var tests = Note("Projects/App.Tests.md");
        Assert.Contains("## Depends on", tests);
        Assert.Contains("[[_code/Projects/App|App]]", tests);
        Assert.Contains("has_tests: true", tests);

        var coverage = Note("Reports/Test coverage.md");
        Assert.Contains("| [[_code/Types/App/App.Store\\|Store]] | App | [[_code/Types/App.Tests/App.Tests.StoreTests#SaveWorks\\|StoreTests.SaveWorks]] | Direct tests |", coverage);
        Assert.Contains("| [[_code/Types/App/App.Client\\|Client]] | App |  | **Check** |", coverage);
        Assert.DoesNotContain("StoreTests\\|StoreTests]]", coverage);

        var untested = Note("Reports/Untested public surface.md");
        Assert.Contains("[[_code/Types/App/App.Store#Load|Load]]", untested);
        Assert.DoesNotContain("App.Store#Save|", untested);

        Assert.Contains("[[_code/Reports/Project map|Project map]]", Note("Index.md"));
    }

    [Fact]
    public void The_manifest_maps_member_ids_to_note_and_heading()
    {
        Seed();
        Export("--link-root", "code/generated");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Out, ObsidianExport.ManifestName)));
        var nodes = manifest.RootElement.GetProperty("nodes");
        Assert.Equal("code/generated", manifest.RootElement.GetProperty("linkRoot").GetString());
        Assert.Equal("Types/App/App.Store.md", nodes.GetProperty(SaveB).GetProperty("path").GetString());
        Assert.Equal("Save 2", nodes.GetProperty(SaveB).GetProperty("heading").GetString());
        Assert.Equal(JsonValueKind.Null, nodes.GetProperty(Store).GetProperty("heading").ValueKind);
        Assert.Contains("[[code/generated/Types/App/App.IStore|IStore]]", Note("Types/App/App.Store.md"));
    }

    [Fact]
    public void A_second_run_writes_nothing_and_a_removed_node_loses_only_its_own_content()
    {
        Seed();
        Export();
        var (_, again) = Export();
        Assert.Contains("obsidian: 0 written", again);

        // A person's note inside the folder, and a generated-looking path that this command never listed.
        var mine = Path.Combine(Out, "Types", "App", "My notes.md");
        File.WriteAllText(mine, "# mine\n");

        Seed(withLoad: false);
        var (code, output) = Export();
        Assert.Equal(0, code);
        Assert.Contains("0 deleted", output);
        Assert.DoesNotContain("### Load", Note("Types/App/App.Store.md"));
        Assert.True(File.Exists(mine));
    }

    [Fact]
    public void A_deleted_type_loses_its_note_but_a_hand_edited_note_without_the_marker_is_kept()
    {
        Seed();
        Export();
        var boxNote = Path.Combine(Out, "Types", "App", "App.Box(T).md");
        var clientNote = Path.Combine(Out, "Types", "App", "App.Client.md");
        File.WriteAllText(clientNote, "# Client, rewritten by hand\n");

        using (var store = GraphStore.Open(DbPath, readOnly: false, TimeSpan.FromSeconds(5)))
        {
            store.Clear();
            store.Write(
                Nodes().Where(n => n.Id is not Box and not Client and not Run).ToList(),
                Edges().Where(e => e.FromId is not Box and not Client and not Run && e.ToId is not Box and not Client and not Run).ToList());
        }

        var (_, output) = Export();
        Assert.Contains("1 deleted", output);
        Assert.False(File.Exists(boxNote));
        Assert.True(File.Exists(clientNote));
    }

    [Fact]
    public void A_non_empty_folder_it_did_not_create_is_refused()
    {
        Seed();
        Directory.CreateDirectory(Out);
        File.WriteAllText(Path.Combine(Out, "Readme.md"), "# a person's folder\n");
        var (code, _) = Export();
        Assert.Equal(1, code);
        Assert.False(File.Exists(Path.Combine(Out, "Index.md")));
    }

    /// <summary>A test case inside a describe block (a member inside a member) lands in the file's note under a heading of its own.</summary>
    [Fact]
    public void Members_nested_in_members_get_a_heading_in_the_nearest_file_or_type_note()
    {
        var spec = Path.Combine(_root, "src", "store.spec.ts");
        var file = "ts:file:src/store.spec.ts";
        var describe = "ts:src/store.spec.ts#StoreTests";
        var it = "ts:src/store.spec.ts#StoreTests > saves";
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        using (var store = GraphStore.Open(DbPath, readOnly: false, TimeSpan.FromSeconds(5)))
        {
            store.Write(
                [
                    N(App, NodeKind.Project, "App", "App", null!, 0, accessibility: Accessibility.None),
                    N(file, NodeKind.File, "store.spec.ts", "src/store.spec.ts", spec, 1, accessibility: Accessibility.None),
                    N(describe, NodeKind.Method, "StoreTests", "src/store.spec:StoreTests", spec, 1),
                    N(it, NodeKind.Method, "saves", "src/store.spec:StoreTests > saves", spec, 2, isTest: true),
                    N(SaveA, NodeKind.Method, "Save", "App.Store.Save", spec, 20),
                    N("ts:src/store.ts#Store", NodeKind.Type, "Store", "src/store:Store", spec, 30, typeKind: TypeKind.Class),
                ],
                [new(App, file, EdgeKind.Contains), new(file, describe, EdgeKind.Contains), new(describe, it, EdgeKind.Contains),
                 new(file, "ts:src/store.ts#Store", EdgeKind.Contains),
                 new(it, SaveA, EdgeKind.Calls), new(SaveA, it, EdgeKind.TestedBy)]);
        }

        Assert.Equal(0, Export().Code);
        var note = Note("Files/App/store.spec.ts.md");
        Assert.Contains("### StoreTests\n", note.ReplaceLineEndings("\n"));
        Assert.Contains("### StoreTests › saves\n", note.ReplaceLineEndings("\n"));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Out, ObsidianExport.ManifestName)));
        Assert.Equal("StoreTests › saves", manifest.RootElement.GetProperty("nodes").GetProperty(it).GetProperty("heading").GetString());

        // A path-qualified type name (TypeScript's src/store:Store) names its note after the type alone.
        Assert.True(File.Exists(Path.Combine(Out, "Types", "App", "Store.md")));
    }

    /// <summary>
    /// The graph comes back from the database in no fixed order, and a re-index can change it. The notes must not
    /// change with it: overloads share a label, so every list needs a tie-breaker (a Java re-index found this).
    /// </summary>
    [Fact]
    public void Notes_do_not_depend_on_the_order_the_graph_is_read_in()
    {
        var nodes = Nodes();
        var edges = Edges();
        edges.Add(new(Run, SaveB, EdgeKind.Calls));
        edges.Add(new(SaveWorks, SaveB, EdgeKind.Calls));
        edges.Add(new(Tests, App, EdgeKind.DependsOn));
        edges.Add(new(Box, IStore, EdgeKind.Implements));
        var meta = new Dictionary<string, string>();
        var forward = ObsidianExport.Build(nodes, edges, meta, null, "_code");
        nodes.Reverse();
        edges.Reverse();
        var backward = ObsidianExport.Build(nodes, edges, meta, null, "_code");
        Assert.Equal(forward.Notes.Keys, backward.Notes.Keys);
        foreach (var (path, text) in forward.Notes)
        {
            Assert.True(text == backward.Notes[path], $"{path} differs:\n{text}\n---\n{backward.Notes[path]}");
        }
    }

    [Fact]
    public void Paths_are_relative_to_the_worktree_or_else_to_the_indexed_solution()
    {
        var bySolution = ObsidianExport.Build(Nodes(), Edges(), new Dictionary<string, string> { [MetaKeys.Solution] = Path.Combine(_root, "App.sln") }, null, "_code");
        Assert.Contains("file: \"src/Store.cs\"", bySolution.Notes["Types/App/App.Store.md"]);
        var byWorktree = ObsidianExport.Build(Nodes(), Edges(), new Dictionary<string, string>
        {
            [MetaKeys.WorktreeRoot] = Path.GetDirectoryName(_root)!,
            [MetaKeys.Solution] = Path.Combine(_root, "App.sln"),
        }, null, "_code");
        Assert.Contains($"file: \"{Path.GetFileName(_root)}/src/Store.cs\"", byWorktree.Notes["Types/App/App.Store.md"]);
    }

    [Fact]
    public void A_link_inside_the_folder_is_never_followed_for_a_write_or_a_delete()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // creating links needs a privilege there
        }

        Seed();
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "Gone.md"), "---\ncodegraph: generated\n---\n");
        Directory.CreateDirectory(Out);
        File.CreateSymbolicLink(Path.Combine(Out, "Linked"), outside);
        File.CreateSymbolicLink(Path.Combine(Out, "Reports"), outside);
        File.CreateSymbolicLink(Path.Combine(Out, "dangling.md"), Path.Combine(outside, "missing.md"));
        File.WriteAllText(Path.Combine(Out, ObsidianExport.ManifestName),
            "{\"version\":1,\"linkRoot\":\"_code\",\"commit\":null,\"indexedAt\":null,\"files\":[\"Linked/Gone.md\",\"dangling.md\"],\"nodes\":{}}");

        var (code, output) = Export();
        Assert.Equal(0, code);
        Assert.Contains("0 deleted", output);
        Assert.True(File.Exists(Path.Combine(outside, "Gone.md")));
        Assert.Equal(["Gone.md"], Directory.GetFiles(outside).Select(Path.GetFileName));
        Assert.True(File.Exists(Path.Combine(Out, "Index.md")));
        Assert.True(File.Exists(Path.Combine(Out, "Types", "App", "App.Store.md")));
    }

    [Fact]
    public void A_name_with_a_line_break_cannot_start_a_new_heading_or_block()
    {
        var src = Path.Combine(_root, "src", "Store.cs");
        var nodes = Nodes();
        nodes.Add(N("M:App.Store.Odd", NodeKind.Method, "odd\nname\n## Injected", "App.Store.odd", src, 20));
        nodes.Add(N("P:Odd", NodeKind.Project, "Odd\nclick p0 \"https://x\"", "Odd", null!, 0, accessibility: Accessibility.None));
        var edges = Edges();
        edges.Add(new(Store, "M:App.Store.Odd", EdgeKind.Contains));
        edges.Add(new(Run, "M:App.Store.Odd", EdgeKind.Calls));
        edges.Add(new("P:Odd", App, EdgeKind.DependsOn));
        var vault = ObsidianExport.Build(nodes, edges, new Dictionary<string, string>(), null, "_code");
        foreach (var (_, text) in vault.Notes)
        {
            Assert.DoesNotContain("\n## Injected", text);
            Assert.DoesNotContain("\nclick p0", text);
        }

        Assert.Contains("### odd name", vault.Notes["Types/App/App.Store.md"]);
        Assert.Contains("[\"Odd click p0 'https://x'\"]", vault.Notes["Reports/Project map.md"]);
    }

    [Theory]
    [InlineData("App.Box<T>", "App.Box(T)")]
    [InlineData("a/b:c#d|e", "a_b_c_d_e")]
    [InlineData("trailing.", "trailing")]
    [InlineData("", "_")]
    public void Names_are_safe_for_files_and_links(string name, string expected) =>
        Assert.Equal(expected, ObsidianExport.SafeName(name));
}
