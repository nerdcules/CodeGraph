using CodeGraph.Core;
using CodeGraph.Indexer;

namespace CodeGraph.Tests;

/// <summary>
/// The extractor contract: newline-delimited JSON read into a batch and written as one language's part of the
/// graph, beside a C# part that neither an import nor a C# re-index may disturb.
/// </summary>
public sealed class ImportTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"))).FullName;

    public void Dispose() => TempDb.TryDelete(_root);

    private string DbPath => Path.Combine(_root, ".codegraph", "graph.db");

    private string Source(string name, string text = "// source")
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static GraphBatch Batch(params string[] lines) => GraphBatch.Read(new StringReader(string.Join("\n", lines)));

    private string TypeScriptBatch(bool withSave = true) => string.Join("\n", new[]
    {
        $$"""{"type":"header","language":"typescript","tool":"test","root":"{{_root.Replace("\\", "/")}}","target":"tsconfig.json"}""",
        """{"type":"file","path":"src/user.service.ts"}""",
        """{"type":"node","id":"ts:src/user.service.ts#UserService","kind":"Type","typeKind":"Class","name":"UserService","fullName":"src/user.service.UserService","file":"src/user.service.ts","start":1,"end":20,"accessibility":"Public"}""",
        withSave
            ? """{"type":"node","id":"ts:src/user.service.ts#UserService.save","kind":"Method","name":"save","fullName":"src/user.service.UserService.save","signature":"save(user: User): Promise<void>","file":"src/user.service.ts","start":5,"end":9,"async":true}"""
            : "",
        """{"type":"node","id":"ts:src/user.service.ts#UserService.load","kind":"Method","name":"load","fullName":"src/user.service.UserService.load","file":"src/user.service.ts","start":10,"end":12}""",
        """{"type":"edge","from":"ts:src/user.service.ts#UserService","to":"ts:src/user.service.ts#UserService.load","kind":"Contains"}""",
        withSave ? """{"type":"edge","from":"ts:src/user.service.ts#UserService.load","to":"ts:src/user.service.ts#UserService.save","kind":"Calls"}""" : "",
        """{"type":"edge","from":"ts:src/user.service.ts#UserService.load","to":"ts:ext:@angular/common/http#HttpClient.get","kind":"CallsExternal"}""",
        """{"type":"node","id":"ts:ext:@angular/common/http#HttpClient.get","kind":"External","name":"get","fullName":"@angular/common/http.HttpClient.get"}""",
        """{"type":"search","node":"ts:src/user.service.ts#UserService.load","parts":[{"text":"load users from the server","weight":1},{"text":"load","weight":3}]}""",
        """{"type":"meta","key":"projects","value":"1"}""",
    });

    private async Task Import(string ndjson)
    {
        var batch = GraphBatch.Read(new StringReader(ndjson));
        Assert.Empty(batch.Problems);
        await GraphImport.WriteAsync(batch, DbPath, _ => { }, TestContext.Current.CancellationToken);
    }

    private void WriteCSharp(string file)
    {
        var nodes = new[]
        {
            new GraphNode("T:App.Api", NodeKind.Type, "Api", "App.Api", null, file, 1, 10, false, TypeKind.Class),
            new GraphNode("M:App.Api.Get", NodeKind.Method, "Get", "App.Api.Get", null, file, 2, 4, false),
        };
        var edge = new GraphEdge("T:App.Api", "M:App.Api.Get", EdgeKind.Contains);
        using var store = GraphStore.Open(DbPath, readOnly: false, TimeSpan.FromSeconds(5));
        store.Replace(full: false, [file], nodes, [edge], [new EdgeOrigin(edge, file)], [FileStamp.Of(file)], new Dictionary<string, string> { [MetaKeys.Solution] = "/repo/App.sln" });
    }

    [Fact]
    public async Task An_import_writes_nodes_edges_files_search_and_meta_for_its_language()
    {
        Source("src/user.service.ts");
        await Import(TypeScriptBatch());

        using var store = GraphStore.Open(DbPath, readOnly: true, TimeSpan.FromSeconds(5));
        var save = store.GetNode("ts:src/user.service.ts#UserService.save")!;
        Assert.Equal(Languages.TypeScript, save.Language);
        Assert.True(save.IsAsync);
        Assert.Equal(Path.Combine(_root, "src", "user.service.ts"), save.FilePath);
        Assert.Contains(store.Callers("ts:src/user.service.ts#UserService.save", 1), h => h.Node.Name == "load");
        Assert.Single(store.IndexedFiles(Languages.TypeScript));
        Assert.Contains(store.Search("server"), h => h.Node.Name == "load");
        var meta = store.ReadMeta();
        Assert.Equal("1", meta["lang.typescript.projects"]);
        Assert.Equal(Path.Combine(_root, "tsconfig.json"), meta[MetaKeys.Solution]);
        Assert.Equal([(Languages.TypeScript, 3)], store.LanguageCounts());
    }

    [Fact]
    public async Task A_reimport_drops_what_the_extractor_no_longer_emits()
    {
        Source("src/user.service.ts");
        await Import(TypeScriptBatch());
        await Import(TypeScriptBatch(withSave: false));

        using var store = GraphStore.Open(DbPath, readOnly: true, TimeSpan.FromSeconds(5));
        Assert.Null(store.GetNode("ts:src/user.service.ts#UserService.save"));
        Assert.NotNull(store.GetNode("ts:src/user.service.ts#UserService.load"));
        Assert.Empty(store.Callees("ts:src/user.service.ts#UserService.load", 1));
    }

    [Fact]
    public async Task CSharp_and_TypeScript_share_a_graph_without_disturbing_each_other()
    {
        var cs = Source("Api.cs");
        Source("src/user.service.ts");
        WriteCSharp(cs);
        await Import(TypeScriptBatch());
        await Import(TypeScriptBatch(withSave: false)); // a TypeScript re-import leaves C# alone

        using (var store = GraphStore.Open(DbPath, readOnly: true, TimeSpan.FromSeconds(5)))
        {
            Assert.NotNull(store.GetNode("M:App.Api.Get"));
            Assert.Single(store.IndexedFiles(Languages.CSharp));
            Assert.Single(store.IndexedFiles(Languages.TypeScript));
            Assert.Equal(2, store.IndexedFiles().Count);
            // The first language keeps the headline target.
            Assert.Equal("/repo/App.sln", store.ReadMeta()[MetaKeys.Solution]);
            Assert.Single(store.FindSymbol("Api.Get", language: Languages.CSharp));
            Assert.Empty(store.FindSymbol("Api.Get", language: Languages.TypeScript));
            Assert.Single(store.FindSymbol("HttpClient.get", language: Languages.TypeScript));
        }

        WriteCSharp(cs); // a C# incremental pass leaves TypeScript alone
        using (var store = GraphStore.Open(DbPath, readOnly: true, TimeSpan.FromSeconds(5)))
        {
            Assert.NotNull(store.GetNode("ts:src/user.service.ts#UserService.load"));
        }
    }

    [Fact]
    public async Task A_full_CSharp_rebuild_into_a_fresh_file_keeps_the_other_languages()
    {
        var cs = Source("Api.cs");
        Source("src/user.service.ts");
        WriteCSharp(cs);
        await Import(TypeScriptBatch());

        var fresh = Path.Combine(_root, "fresh.db");
        using (var store = GraphStore.Open(fresh, readOnly: false, TimeSpan.FromSeconds(5)))
        {
            var node = new GraphNode("M:App.Other", NodeKind.Method, "Other", "App.Other", null, cs, 1, 2, false);
            store.Replace(full: true, [], [node], [], [], [FileStamp.Of(cs)], new Dictionary<string, string>());
            Assert.Equal(4, store.CopyOtherLanguages(DbPath, Languages.CSharp));
        }

        using var rebuilt = GraphStore.Open(fresh, readOnly: true, TimeSpan.FromSeconds(5));
        Assert.Null(rebuilt.GetNode("M:App.Api.Get"));                       // the old C# rows are not copied
        Assert.NotNull(rebuilt.GetNode("M:App.Other"));
        Assert.NotNull(rebuilt.GetNode("ts:src/user.service.ts#UserService.save"));
        Assert.Contains(rebuilt.Callers("ts:src/user.service.ts#UserService.save", 1), h => h.Node.Name == "load");
        Assert.Contains(rebuilt.Search("server"), h => h.Node.Name == "load");
        Assert.Single(rebuilt.IndexedFiles(Languages.TypeScript));
        Assert.Equal("1", rebuilt.ReadMeta()["lang.typescript.projects"]);
    }

    [Fact]
    public void Malformed_records_are_reported_with_their_line_and_the_rest_still_read()
    {
        var batch = Batch(
            """{"type":"header","language":"java"}""",
            """{"type":"node","id":"java:A","kind":"Gadget","name":"A"}""",
            """not json""",
            """{"type":"node","id":"java:B#m()","kind":"Method","name":"m","file":"/src/B.java"}""",
            """{"type":"edge","from":"java:Nowhere","to":"java:B#m()","kind":"Calls"}""",
            """{"type":"edge","from":"java:B#m()","to":"java:B#m()","kind":"Calls"}""");

        Assert.Single(batch.Nodes);
        Assert.Single(batch.Edges);
        Assert.Equal("/src/B.java", batch.Origins.Single().FilePath);
        Assert.Contains(batch.Problems, p => p.StartsWith("line 2: unknown kind 'Gadget'", StringComparison.Ordinal));
        Assert.Contains(batch.Problems, p => p.StartsWith("line 3:", StringComparison.Ordinal));
        Assert.Contains(batch.Problems, p => p.StartsWith("line 5:", StringComparison.Ordinal) && p.Contains("no file"));
        Assert.Equal(Languages.Java, batch.Nodes[0].Language);
    }

    [Fact]
    public void A_batch_without_a_header_is_refused()
    {
        var batch = Batch("""{"type":"file","path":"/a.ts"}""");
        Assert.Contains(batch.Problems, p => p.Contains("header"));
    }
}
