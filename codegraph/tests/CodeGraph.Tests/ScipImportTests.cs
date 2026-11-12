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
        Assert.Equal("org.acme.Shop.price", java.FullNameFor(Languages.Java));
        Assert.Equal("src/app/user.service.ts/UserService.load", symbol.FullNameFor(Languages.TypeScript));

        // Ids drop the version, so a release bump keeps them; doubled spaces stay literal.
        Assert.Equal("scip:semanticdb maven maven/org.acme/shop org/acme/Shop#price(+1).", ScipImport.IdOf("semanticdb maven maven/org.acme/shop 1.0 org/acme/Shop#price(+1)."));
        Assert.Equal("scip:s m my  pkg T#", ScipImport.IdOf("s m my  pkg 2.0 T#"));

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

        const string Prefix = "scip:scip-typescript npm . src/app/";
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
        var id = ScipImport.IdOf;
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

    /// <summary>rust-analyzer names impl members impl#[Self][Trait]member(): they belong to the self type.</summary>
    [Fact]
    public void Rust_impl_members_belong_to_their_type_and_name_their_trait()
    {
        var method = ScipSymbol.Parse("rust-analyzer cargo corpus 0.1.0 m0/ledger/impl#[Ledger][IShape]handle_shape().")!;
        Assert.Equal("m0/ledger/Ledger.handle_shape", method.FullName);
        Assert.Equal("rust-analyzer cargo corpus 0.1.0 m0/ledger/Ledger#", method.Parent);
        Assert.Equal("IShape", method.ImplTrait);

        var generic = ScipSymbol.Parse("rust-analyzer cargo corpus 0.1.0 m0/ledger/impl#[`Boxed<T>`]put().")!;
        Assert.Equal("m0/ledger/Boxed.put", generic.FullName);
        Assert.Equal("rust-analyzer cargo corpus 0.1.0 m0/ledger/Boxed#", generic.Parent);
        Assert.Null(generic.ImplTrait);
    }

    /// <summary>
    /// A Rust index carries no relationships; trait impls come from the impl descriptor and the trait its header
    /// references, not a same-named one elsewhere. #[test] marks tests. Go's
    /// interface members (MethodSpecification) are methods, and Go's generated test main outside the root is skipped.
    /// </summary>
    [Fact]
    public async Task Rust_trait_impls_and_tests_and_Go_interface_members_come_through()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        var rustText = string.Join('\n',
            "pub trait IShape { fn area(&self) -> i32; }",              // 0
            "pub struct Square { a: i32 }",                             // 1
            "impl IShape for Square { fn area(&self) -> i32 { 1 } }",   // 2
            "#[test]",                                                  // 3
            "fn squares() { Square { a: 1 }.area(); }");                // 4
        const string R = "rust-analyzer cargo corpus 0.1.0 shapes/";
        Occurrence Def(string symbol, int line, int column) => new()
        {
            Symbol = symbol, SymbolRoles = (int)SymbolRole.Definition,
            SingleLineRange = new SingleLineRange { Line = line, StartCharacter = column, EndCharacter = column + 4 },
            MultiLineEnclosingRange = new MultiLineRange { StartLine = line, StartCharacter = 0, EndLine = line, EndCharacter = 80 },
        };
        var rust = new Document { RelativePath = "src/shapes.rs", Language = "rust", Text = rustText };
        rust.Occurrences.Add(Def(R + "IShape#", 0, 10));
        rust.Occurrences.Add(Def(R + "IShape#area().", 0, 22));
        rust.Occurrences.Add(Def(R + "Square#", 1, 11));
        rust.Occurrences.Add(Def(R + "impl#[Square][IShape]area().", 2, 28));
        rust.Occurrences.Add(Def(R + "squares().", 4, 3));
        rust.Occurrences.Add(new Occurrence { Symbol = R + "impl#[Square][IShape]area().", SingleLineRange = new SingleLineRange { Line = 4, StartCharacter = 31, EndCharacter = 35 } });
        rust.Occurrences.Add(new Occurrence { Symbol = R + "IShape#", SingleLineRange = new SingleLineRange { Line = 2, StartCharacter = 5, EndCharacter = 11 } }); // the impl header
        // A trait of the same name whose module path is closer to Square's: naming alone would pick it.
        var decoy = new Document { RelativePath = "src/sq.rs", Language = "rust", Text = "pub trait IShape { fn area(&self) -> i32; }" };
        decoy.Occurrences.Add(Def(R + "Sq/IShape#", 0, 10));
        decoy.Occurrences.Add(Def(R + "Sq/IShape#area().", 0, 22));
        decoy.Symbols.Add(new SymbolInformation { Symbol = R + "Sq/IShape#", Kind = SymbolInformation.Types.Kind.Trait });
        decoy.Symbols.Add(new SymbolInformation { Symbol = R + "Sq/IShape#area().", Kind = SymbolInformation.Types.Kind.TraitMethod });
        rust.Symbols.Add(new SymbolInformation { Symbol = R + "IShape#", Kind = SymbolInformation.Types.Kind.Trait });
        rust.Symbols.Add(new SymbolInformation { Symbol = R + "IShape#area().", Kind = SymbolInformation.Types.Kind.TraitMethod });
        rust.Symbols.Add(new SymbolInformation { Symbol = R + "Square#", Kind = SymbolInformation.Types.Kind.Struct });
        rust.Symbols.Add(new SymbolInformation { Symbol = R + "impl#[Square][IShape]area().", Kind = SymbolInformation.Types.Kind.Method });
        rust.Symbols.Add(new SymbolInformation { Symbol = R + "squares().", Kind = SymbolInformation.Types.Kind.Function });

        const string G = "scip-go gomod example.com/app . `example.com/app/shapes`/";
        var go = new Document { RelativePath = "shapes/shape.go", Language = "go", Text = "package shapes\ntype IShape interface{ Area() int }\n" };
        go.Occurrences.Add(Def(G + "IShape#", 1, 5));
        go.Occurrences.Add(Def(G + "IShape#Area.", 1, 23));
        go.Symbols.Add(new SymbolInformation { Symbol = G + "IShape#", Kind = SymbolInformation.Types.Kind.Interface });
        go.Symbols.Add(new SymbolInformation { Symbol = G + "IShape#Area.", Kind = SymbolInformation.Types.Kind.MethodSpecification });
        var generated = new Document { RelativePath = "../../.cache/go-build/ab/main-d", Language = "go", Text = "package main\nfunc main() {}\n" };
        generated.Occurrences.Add(Def("scip-go gomod example.com/app . `example.com/app/shapes.test`/main().", 1, 5));

        var index = new Scip.Index { Metadata = new Metadata { ToolInfo = new ToolInfo { Name = "rust-analyzer", Version = "1.99" }, ProjectRoot = new Uri(_root).AbsoluteUri } };
        index.Documents.AddRange([rust, decoy, go, generated]);
        var path = Path.Combine(_root, "index.scip");
        await File.WriteAllBytesAsync(path, Google.Protobuf.MessageExtensions.ToByteArray(index), TestContext.Current.CancellationToken);
        var db = Path.Combine(_root, "graph.db");
        await GraphImport.WriteAsync(ScipImport.Read(path, _ => { }), db, _ => { }, TestContext.Current.CancellationToken);

        using var store = GraphStore.Open(db, readOnly: true, TimeSpan.FromSeconds(5));
        var area = ScipImport.IdOf(R + "impl#[Square][IShape]area().");
        Assert.Equal("shapes/Square.area", store.GetNode(area)!.FullName);
        Assert.Contains(store.EdgesTo(area), e => e.Kind == EdgeKind.Contains && e.Source.Id == ScipImport.IdOf(R + "Square#"));
        Assert.Contains(store.EdgesFrom(area), e => e.Kind == EdgeKind.Implements && e.Target.Id == ScipImport.IdOf(R + "IShape#area()."));
        Assert.DoesNotContain(store.EdgesFrom(area), e => e.Target.Id == ScipImport.IdOf(R + "Sq/IShape#area()."));
        Assert.Contains(store.EdgesFrom(ScipImport.IdOf(R + "Square#")), e => e.Kind == EdgeKind.Implements && e.Target.Id == ScipImport.IdOf(R + "IShape#"));
        Assert.True(store.GetNode(ScipImport.IdOf(R + "squares()."))!.IsTest, "#[test] marks a Rust test");
        Assert.Equal(NodeKind.Method, store.GetNode(ScipImport.IdOf(G + "IShape#Area."))!.Kind);
        Assert.Null(store.GetNode(ScipImport.IdOf("scip-go gomod example.com/app . `example.com/app/shapes.test`/main().")));
    }

    [Fact]
    public void A_go_workspace_is_indexed_through_each_used_module()
    {
        File.WriteAllText(Path.Combine(_root, "go.work"), "go 1.22\n\nuse (\n\t./svc\n\tlib // shared\n)\nuse ./tools\n");
        Assert.Equal(["./svc/...", "./lib/...", "./tools/..."], ScipIndexers.GoWorkspacePatterns(_root));
        File.WriteAllText(Path.Combine(_root, "go.mod"), "module example.com/app\n");
        Assert.Empty(ScipIndexers.GoWorkspacePatterns(_root));
    }

    /// <summary>
    /// A real scip-java 0.12.3 index of <c>Fixtures/java-shop</c> (JDK 27, Maven). scip-java lists a method's
    /// relationship in both directions and a type's supertypes transitively; the graph keeps C#'s shape.
    /// </summary>
    [Fact]
    public async Task A_real_scip_java_index_gives_one_way_overrides_direct_supertypes_tests_and_dead_code()
    {
        // The fixture's project root is neutral; point it at the sources copied beside the tests, which hold the annotations.
        var sources = Path.Combine(AppContext.BaseDirectory, "Fixtures", "java-shop");
        var index = Scip.Index.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "java-shop.scip")));
        index.Metadata.ProjectRoot = new Uri(sources + Path.DirectorySeparatorChar).AbsoluteUri;
        var path = Path.Combine(_root, "index.scip");
        await File.WriteAllBytesAsync(path, Google.Protobuf.MessageExtensions.ToByteArray(index), TestContext.Current.CancellationToken);

        var batch = ScipImport.Read(path, _ => { });
        Assert.Equal(Languages.Java, batch.Language);
        var db = Path.Combine(_root, "graph.db");
        await GraphImport.WriteAsync(batch, db, _ => { }, TestContext.Current.CancellationToken);

        const string P = "scip:semanticdb maven maven/org.acme/shop org/acme/shop/";
        using var store = GraphStore.Open(db, readOnly: true, TimeSpan.FromSeconds(5));
        Assert.Equal("org.acme.shop.Cart.total", store.GetNode(P + "Cart#total().")!.FullName);

        // Overrides point from the override to the base, never back.
        Assert.Contains(store.EdgesFrom(P + "DiscountPricing#unitPrice()."), e => e.Kind == EdgeKind.Overrides && e.Target.Id == P + "StandardPricing#unitPrice().");
        Assert.DoesNotContain(store.EdgesFrom(P + "StandardPricing#unitPrice()."), e => e.Kind == EdgeKind.Overrides);
        Assert.Contains(store.EdgesFrom(P + "StandardPricing#price()."), e => e.Kind == EdgeKind.Implements && e.Target.Id == P + "Pricing#price().");
        Assert.DoesNotContain(store.EdgesFrom(P + "Pricing#price()."), e => e.Kind is EdgeKind.Overrides or EdgeKind.Implements);
        Assert.True(store.GetNode(P + "DiscountPricing#unitPrice().")!.IsOverride, "@Override sets the flag");

        // Direct supertypes only: DiscountPricing reaches Pricing through StandardPricing.
        var discountSupertypes = store.EdgesFrom(P + "DiscountPricing#").Where(e => e.Kind is EdgeKind.Inherits or EdgeKind.Implements).Select(e => e.Target.Id).ToList();
        Assert.Equal([P + "StandardPricing#"], discountSupertypes);
        Assert.Contains(store.Walk(P + "Pricing#", [nameof(EdgeKind.Implements), nameof(EdgeKind.Inherits)], inbound: true, 3), h => h.Node.Name == "DiscountPricing" && h.Depth == 2);

        // The JUnit test reaches the base method three calls down; JDK calls are externals with stable ids.
        Assert.True(store.GetNode(P + "CartTest#totalAppliesTheDiscount().")!.IsTest);
        Assert.Contains(store.TransitiveTests(P + "StandardPricing#unitPrice().", 30), t => t.Node.Name == "totalAppliesTheDiscount");
        Assert.Contains(store.EdgesFrom(P + "StandardPricing#unitPrice()."), e => e.Kind == EdgeKind.CallsExternal && e.Target.Id == "scip:semanticdb maven jdk java/lang/Math#abs(+1).");
        Assert.Contains(store.EdgesFrom(P + "DiscountPricing#`<init>`()."), e => e.Kind == EdgeKind.Calls && e.Target.Id == P + "StandardPricing#`<init>`().");

        // main is an entry point; overrides are reached through dispatch; only the uncalled helper is dead.
        Assert.Contains(store.EdgesTo(P + "Main#main()."), e => e.Kind == EdgeKind.InvokedByFramework);
        Assert.DoesNotContain(store.EdgesTo(P + "DiscountPricing#unitPrice()."), e => e.Kind == EdgeKind.InvokedByFramework);
        var (_, dead) = store.DeadCodeReport(includePublicApi: true);
        Assert.Equal(["unused"], dead.Select(d => d.Method.Name));
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
        // Records its working directory and arguments, then writes the fixture index to the path after --output.
        File.WriteAllText(fake, $"#!/bin/sh\npwd > \"{_root}/cwd\"\necho \"$@\" > \"{_root}/args\"\nwhile [ $# -gt 0 ]; do if [ \"$1\" = --output ]; then cp \"{fixture}\" \"$2\"; fi; shift; done\n");
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

/// <summary>Tools installed by coursier, go install or cargo are found even when their folder is not on PATH.</summary>
public sealed class ToolLocatorTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"))).FullName;

    public void Dispose() => TempDb.TryDelete(_root);

    [Fact]
    public void A_tool_outside_PATH_is_found_in_an_installer_folder()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "coursier", "bin")).FullName;
        var name = "codegraph-fake-" + Guid.NewGuid().ToString("n")[..8];
        File.WriteAllText(Path.Combine(bin, OperatingSystem.IsWindows() ? name + ".exe" : name), "");
        Assert.Equal(Path.Combine(bin, OperatingSystem.IsWindows() ? name + ".exe" : name), ToolLocator.Find(name, [bin]));
        Assert.Null(ToolLocator.Find(name, [_root]));
        Assert.Contains(ToolLocator.WellKnownDirectories(), d => d.Contains("oursier", StringComparison.Ordinal));
    }
}
