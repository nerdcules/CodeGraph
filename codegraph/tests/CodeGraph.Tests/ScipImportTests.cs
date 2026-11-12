using CodeGraph.Core;
using CodeGraph.Indexer;
using Scip;

namespace CodeGraph.Tests;

/// <summary>
/// SCIP indexes as a graph source: symbol parsing, a real scip-typescript index of the Angular fixture, and a
/// synthetic scip-java index (no JVM needed) for annotations, tests, overrides and externals.
/// </summary>
public sealed class ScipImportTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"))).FullName;

    public void Dispose() => TempDb.TryDelete(_root);

    [Fact]
    public void Symbols_parse_with_escapes_methods_and_parents()
    {
        var symbol = ScipSymbol.Parse("scip-typescript npm . . src/app/`user.service.ts`/UserService#load().")!;
        Assert.Equal("load", symbol.Name);
        Assert.True(symbol.IsMethod);
        Assert.Equal("scip-typescript npm . . src/app/`user.service.ts`/UserService#", symbol.Parent);
        Assert.Equal("src/app/user.service.ts/UserService.load", symbol.FullName);

        var java = ScipSymbol.Parse("semanticdb maven maven/org.acme/shop 1.0 org/acme/Shop#price(+1).")!;
        Assert.Equal("price", java.Name);
        Assert.Equal("maven/org.acme/shop", java.Package);
        Assert.Equal("org/acme/Shop.price", java.FullName);

        Assert.Equal("a b", ScipSymbol.Parse("s m p v `a b`#")!.Name);
        Assert.Equal("x`y", ScipSymbol.Parse("s m p v `x``y`#")!.Name);
        Assert.Equal("my pkg", ScipSymbol.Parse("s m my  pkg v T#")!.Package);
        Assert.Null(ScipSymbol.Parse("local 12"));
        Assert.Null(ScipSymbol.Parse("s m p v T#")!.Parent);
        Assert.Equal(ScipSuffix.Parameter, ScipSymbol.Parse("s m p v T#m().(id)")!.Suffix);
    }

    [Fact]
    public async Task A_scip_typescript_index_gives_calls_implementations_and_externals()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "angular-app.scip");
        var batch = ScipImport.Read(source, _ => { });
        var db = Path.Combine(_root, "graph.db");
        await GraphImport.WriteAsync(batch, db, _ => { }, TestContext.Current.CancellationToken);

        const string Prefix = "scip:scip-typescript npm . . src/app/";
        using var store = GraphStore.Open(db, readOnly: true, TimeSpan.FromSeconds(5));
        Assert.Contains(store.Callers(Prefix + "`http.service.ts`/HttpService#get().", 1), h => h.Node.Id == Prefix + "`user.service.ts`/UserService#load().");
        Assert.Contains(store.Walk(Prefix + "`loader.ts`/Loader#", [nameof(EdgeKind.Implements), nameof(EdgeKind.Inherits)], inbound: true, 3), h => h.Node.Name == "UserService");
        Assert.Contains(store.EdgesFrom(Prefix + "`user.service.ts`/UserService#load()."), e => e.Kind == EdgeKind.Overrides && e.Target.Name == "load");
        Assert.Contains(store.EdgesFrom(Prefix + "`user.service.ts`/UserService#"), e => e.Kind == EdgeKind.Inherits && e.Target.Name == "Repository");
        Assert.Contains(store.EdgesFrom(Prefix + "`user.service.ts`/UserService#formatName()."), e => e.Kind == EdgeKind.CallsExternal && e.Target.Name == "toUpperCase");
        Assert.Equal(MemberKind.Constructor, store.GetNode(Prefix + "`user.service.ts`/UserService#`<constructor>`().")!.MemberKind);
        Assert.All(store.AllNodes(NodeKind.Method), n => Assert.Equal(Languages.TypeScript, n.Language));
    }

    [Fact]
    public async Task A_java_index_reads_tests_entry_points_overrides_and_accessibility_from_the_source()
    {
        const string Shop = "semanticdb maven . . org/acme/Shop#";
        const string Pricing = "semanticdb maven . . org/acme/Pricing#";
        const string ShopTest = "semanticdb maven . . org/acme/ShopTest#";
        var shopText = string.Join('\n',
            "package org.acme;",                                   // 0
            "public class Shop implements Pricing {",              // 1
            "  @Override",                                         // 2
            "  public int price(int n) { return round(n); }",      // 3
            "  private int round(int n) { return Math.abs(n); }",  // 4
            "  @GetMapping(\"/total\")",                           // 5
            "  int total() { return price(2); }",                  // 6
            "}");                                                  // 7
        var pricingText = "package org.acme;\npublic interface Pricing {\n  int price(int n);\n}";
        var testText = string.Join('\n',
            "package org.acme;",                                   // 0
            "class ShopTest {",                                    // 1
            "  @Test",                                             // 2
            "  void pricesAreRounded() { new Shop().price(3); }",  // 3
            "}");                                                  // 4

        Occurrence Def(string symbol, int line, int column, int length, int endLine) => new()
        {
            Symbol = symbol, SymbolRoles = (int)SymbolRole.Definition,
            SingleLineRange = new SingleLineRange { Line = line, StartCharacter = column, EndCharacter = column + length },
            MultiLineEnclosingRange = new MultiLineRange { StartLine = line, StartCharacter = 0, EndLine = endLine, EndCharacter = 80 },
        };
        Occurrence Ref(string symbol, int line, int column) => new()
        {
            Symbol = symbol, SingleLineRange = new SingleLineRange { Line = line, StartCharacter = column, EndCharacter = column + 3 },
        };
        SymbolInformation Info(string symbol, SymbolInformation.Types.Kind kind, params string[] implemented)
        {
            var info = new SymbolInformation { Symbol = symbol, Kind = kind };
            info.Relationships.AddRange(implemented.Select(s => new Relationship { Symbol = s, IsImplementation = true }));
            return info;
        }

        var shop = new Document { RelativePath = "src/main/java/org/acme/Shop.java", Language = "Java", Text = shopText };
        shop.Occurrences.Add(Def(Shop, 1, 13, 4, 7));
        shop.Occurrences.Add(Def(Shop + "price().", 3, 13, 5, 3));
        shop.Occurrences.Add(Def(Shop + "round().", 4, 14, 5, 4));
        shop.Occurrences.Add(Def(Shop + "total().", 6, 6, 5, 6));
        shop.Occurrences.Add(Ref(Shop + "round().", 3, 37));
        shop.Occurrences.Add(Ref("semanticdb maven jdk 21 java/lang/Math#abs().", 4, 40));
        shop.Occurrences.Add(Ref(Shop + "price().", 6, 23));
        shop.Occurrences.Add(Ref(Pricing, 1, 29));
        shop.Symbols.Add(Info(Shop, SymbolInformation.Types.Kind.Class, Pricing));
        shop.Symbols.Add(Info(Shop + "price().", SymbolInformation.Types.Kind.Method, Pricing + "price()."));
        shop.Symbols.Add(Info(Shop + "round().", SymbolInformation.Types.Kind.Method));
        shop.Symbols.Add(Info(Shop + "total().", SymbolInformation.Types.Kind.Method));

        var pricing = new Document { RelativePath = "src/main/java/org/acme/Pricing.java", Language = "Java", Text = pricingText };
        pricing.Occurrences.Add(Def(Pricing, 1, 17, 7, 3));
        pricing.Occurrences.Add(Def(Pricing + "price().", 2, 6, 5, 2));
        pricing.Symbols.Add(Info(Pricing, SymbolInformation.Types.Kind.Interface));
        pricing.Symbols.Add(Info(Pricing + "price().", SymbolInformation.Types.Kind.AbstractMethod));

        var test = new Document { RelativePath = "src/test/java/org/acme/ShopTest.java", Language = "Java", Text = testText };
        test.Occurrences.Add(Def(ShopTest, 1, 6, 8, 4));
        test.Occurrences.Add(Def(ShopTest + "pricesAreRounded().", 3, 7, 16, 3));
        test.Occurrences.Add(Ref(Shop + "price().", 3, 40));
        test.Symbols.Add(Info(ShopTest, SymbolInformation.Types.Kind.Class));
        test.Symbols.Add(Info(ShopTest + "pricesAreRounded().", SymbolInformation.Types.Kind.Method));

        var index = new Scip.Index { Metadata = new Metadata { ToolInfo = new ToolInfo { Name = "scip-java", Version = "0.10.0" }, ProjectRoot = new Uri(_root).AbsoluteUri } };
        index.Documents.AddRange([shop, pricing, test]);
        var path = Path.Combine(_root, "index.scip");
        await File.WriteAllBytesAsync(path, Google.Protobuf.MessageExtensions.ToByteArray(index), TestContext.Current.CancellationToken);

        var batch = ScipImport.Read(path, _ => { });
        Assert.Equal(Languages.Java, batch.Language);
        var db = Path.Combine(_root, "graph.db");
        await GraphImport.WriteAsync(batch, db, _ => { }, TestContext.Current.CancellationToken);

        using var store = GraphStore.Open(db, readOnly: true, TimeSpan.FromSeconds(5));
        var id = (string s) => "scip:" + s;
        Assert.True(store.GetNode(id(ShopTest + "pricesAreRounded()."))!.IsTest, "@Test marks a test");
        Assert.Contains(store.TransitiveTests(id(Shop + "round()."), 30), t => t.Node.Name == "pricesAreRounded");
        Assert.Contains(store.EdgesFrom(id(Shop + "price().")), e => e.Kind == EdgeKind.Implements && e.Target.Id == id(Pricing + "price()."));
        Assert.Contains(store.EdgesFrom(id(Shop)), e => e.Kind == EdgeKind.Implements && e.Target.Id == id(Pricing));
        Assert.Contains(store.EdgesFrom(id(Shop + "round().")), e => e.Kind == EdgeKind.CallsExternal && e.Target.Name == "abs");
        Assert.Equal(Accessibility.Private, store.GetNode(id(Shop + "round()."))!.Accessibility);
        Assert.Equal(Accessibility.Internal, store.GetNode(id(Shop + "total()."))!.Accessibility);
        Assert.Equal(Accessibility.Public, store.GetNode(id(Shop + "price()."))!.Accessibility);
        Assert.Contains(store.EdgesTo(id(Shop + "total().")), e => e.Kind == EdgeKind.InvokedByFramework);
        var (_, dead) = store.DeadCodeReport();
        Assert.DoesNotContain(dead, d => d.Method.Name is "total" or "round");
    }
}

/// <summary>codegraph-index on a go.mod runs scip-go in that folder and imports what it writes. A stand-in tool keeps it hermetic.</summary>
public sealed class ScipRunnerTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"))).FullName;

    public void Dispose() => TempDb.TryDelete(_root);

    [Fact]
    public async Task A_go_module_runs_its_scip_indexer_in_the_module_folder_and_imports_the_index()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stand-in indexer is a shell script");
        var module = Directory.CreateDirectory(Path.Combine(_root, "svc")).FullName;
        File.WriteAllText(Path.Combine(module, "go.mod"), "module example.com/svc\n");
        var tools = Directory.CreateDirectory(Path.Combine(_root, "tools")).FullName;
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "angular-app.scip");
        var fake = Path.Combine(tools, "scip-go");
        // Records its working directory, then writes the fixture index where --output says.
        File.WriteAllText(fake, $"#!/bin/sh\npwd > \"{_root}/cwd\"\ncp \"{fixture}\" \"$2\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var db = Path.Combine(_root, "graph.db");
        var dotnet = Path.Combine(DotnetRuntime.Root()!, "dotnet");
        var info = new System.Diagnostics.ProcessStartInfo(dotnet, [Path.Combine(AppContext.BaseDirectory, "codegraph-index.dll"), module, "--db", db])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.Environment["PATH"] = tools + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        using var process = System.Diagnostics.Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.True(process.ExitCode == 0, await error);
        Assert.Equal(module, File.ReadAllText(Path.Combine(_root, "cwd")).Trim());
        using var store = GraphStore.Open(db, readOnly: true, TimeSpan.FromSeconds(5));
        Assert.Equal(Path.Combine(module, "go.mod"), store.ReadMeta()[MetaKeys.Solution]);
        Assert.NotEmpty(store.FindSymbol("formatName"));
    }

    [Fact]
    public void Init_finds_build_files_when_there_is_no_solution()
    {
        File.WriteAllText(Path.Combine(_root, "pom.xml"), "<project/>");
        Assert.Equal(Path.Combine(_root, "pom.xml"), RepoSetup.FindTarget(_root, out _));
    }
}
