using CodeGraph.Core;
using CodeGraph.Indexer;
using Scip;

namespace CodeGraph.Tests;

/// <summary>
/// The SCIP route's version facts: the indexer is the compiler, its diagnostics are the compile errors, a symbol
/// kind the importer has no case for is counted, the build file names the language version, and an indexer
/// older than the tested one is a note.
/// </summary>
public sealed class ScipVersionTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"))).FullName;

    public void Dispose() => TempDb.TryDelete(_root);

    [Fact]
    public void The_tool_is_the_compiler_and_its_diagnostics_and_unknown_kinds_are_counted()
    {
        const string A = "semanticdb maven . . org/acme/A#";
        var document = new Document { RelativePath = "src/main/java/org/acme/A.java", Language = "Java", Text = "package org.acme;\npublic class A {\n  void m() { }\n  sealed interface S permits A { }\n}" };
        var definition = new Occurrence { Symbol = A, SymbolRoles = (int)SymbolRole.Definition, SingleLineRange = new SingleLineRange { Line = 1, StartCharacter = 13, EndCharacter = 14 } };
        var method = new Occurrence { Symbol = A + "m().", SymbolRoles = (int)SymbolRole.Definition, SingleLineRange = new SingleLineRange { Line = 2, StartCharacter = 7, EndCharacter = 8 } };
        method.Diagnostics.Add(new Diagnostic { Severity = Severity.Error, Code = "E001", Message = "cannot find symbol" });
        method.Diagnostics.Add(new Diagnostic { Severity = Severity.Warning, Code = "W001", Message = "unused" });
        // A kind from a scip.proto newer than the vendored one arrives as a number; the suffix still shapes the node.
        var newer = new Occurrence { Symbol = A + "S#", SymbolRoles = (int)SymbolRole.Definition, SingleLineRange = new SingleLineRange { Line = 3, StartCharacter = 19, EndCharacter = 20 } };
        document.Occurrences.Add(definition);
        document.Occurrences.Add(method);
        document.Occurrences.Add(newer);
        document.Symbols.Add(new SymbolInformation { Symbol = A, Kind = SymbolInformation.Types.Kind.Class });
        document.Symbols.Add(new SymbolInformation { Symbol = A + "m().", Kind = SymbolInformation.Types.Kind.Method });
        document.Symbols.Add(new SymbolInformation { Symbol = A + "S#", Kind = (SymbolInformation.Types.Kind)9999 });
        var index = new Scip.Index { Metadata = new Metadata { ToolInfo = new ToolInfo { Name = "scip-java", Version = "0.10.0" }, ProjectRoot = new Uri(_root).AbsoluteUri } };
        index.Documents.Add(document);
        var path = Path.Combine(_root, "index.scip");
        using (var file = File.Create(path))
        {
            Google.Protobuf.MessageExtensions.WriteTo(index, file);
        }

        var batch = ScipImport.Read(path, _ => { });

        Assert.Equal("scip-java 0.10.0", batch.Meta[LanguageMetaKeys.Compiler]);
        Assert.Equal("1", batch.Meta[LanguageMetaKeys.CompileErrors]);
        Assert.Equal("src/main/java/org/acme/A.java(3): E001 cannot find symbol", batch.Meta[LanguageMetaKeys.FirstCompileError]);
        Assert.Equal("SymbolKind.9999 x1", batch.Meta[LanguageMetaKeys.Unmapped]);
        Assert.Contains(batch.Nodes, n => n.Name == "S" && n.Kind == NodeKind.Type);
        Assert.Contains(ScipImport.CompilerSummary(batch), line => line.StartsWith("compiler: scip-java 0.10.0;"));
        Assert.Contains(ScipImport.CompilerSummary(batch), line => line.Contains("SymbolKind.9999 x1"));
    }

    [Theory]
    [InlineData("scip-java", "scip-java 0.10.0", true)]
    [InlineData("scip-java", "scip-java 0.12.3", false)]
    [InlineData("scip-java", "scip-java 0.13.0", false)]
    [InlineData("rust-analyzer", "rust-analyzer 1.80.0 (abc 2024-01-01)", true)]
    [InlineData("rust-analyzer", "rust-analyzer 1.99.1 (58cf8f9 2026-10-13)", false)]
    [InlineData("scip-go", "scip-go 0.2.9", false)]
    [InlineData("scip-go", "scip-go", false)]
    public void An_indexer_older_than_the_tested_one_is_a_note(string tool, string toolInfo, bool noted)
    {
        var indexer = ScipIndexers.All.Single(i => i.Tool == tool);

        var note = ScipIndexers.VersionNote(indexer, toolInfo);

        if (noted)
        {
            Assert.Contains($"older than the {indexer.Tested}", note);
        }
        else
        {
            Assert.Null(note);
        }
    }

    [Theory]
    [InlineData("java", "pom.xml", "<project><properties><maven.compiler.release>17</maven.compiler.release></properties></project>", "Java 17")]
    [InlineData("java", "pom.xml", "<project><build><plugins><plugin><configuration><source>1.8</source><target>1.8</target></configuration></plugin></plugins></build></project>", "Java 8")]
    [InlineData("java", "pom.xml", "<project><properties><java.version>21</java.version></properties></project>", "Java 21")]
    [InlineData("java", "build.gradle", "java {\n  toolchain { languageVersion = JavaLanguageVersion.of(21) }\n}", "Java 21")]
    [InlineData("java", "build.gradle.kts", "kotlin { jvmToolchain(17) }", "Java 17")]
    [InlineData("java", "build.gradle", "sourceCompatibility = JavaVersion.VERSION_11", "Java 11")]
    [InlineData("java", "build.gradle", "sourceCompatibility = 1.8", "Java 8")]
    [InlineData("java", "build.sbt", "scalaVersion := \"3.3.1\"", "Scala 3.3.1")]
    [InlineData("go", "go.mod", "module example.com/m\n\ngo 1.18\n", "Go 1.18")]
    [InlineData("go", "go.work", "go 1.22.1\n\nuse ./a\n", "Go 1.22.1")]
    [InlineData("rust", "Cargo.toml", "[package]\nname = \"x\"\nedition = \"2018\"\n", "Rust edition 2018")]
    [InlineData("rust", "Cargo.toml", "[package]\nname = \"x\"\nrust-version = \"1.70\"\nedition = \"2021\"\n", "Rust 1.70, edition 2021")]
    [InlineData("python", "pyproject.toml", "[project]\nname = \"x\"\nrequires-python = \">=3.8\"\n", "Python >=3.8")]
    [InlineData("python", "setup.py", "setup(name='x', python_requires='>=3.6')", "Python >=3.6")]
    [InlineData("python", "setup.cfg", "[options]\npython_requires = >=3.7\n", "Python >=3.7")]
    [InlineData("java", "pom.xml", "<project></project>", null)]
    public void The_build_file_names_the_language_version(string language, string file, string text, string? expected)
    {
        var marker = Path.Combine(_root, file);
        File.WriteAllText(marker, text);

        Assert.Equal(expected, ScipIndexers.LanguageVersionOf(language, marker));
    }
}

public sealed class LegacyIndexerTests
{
    [Theory]
    [InlineData("rust-analyzer", "unexpected flag: `--output`\n", true)]
    [InlineData("rust-analyzer", "error: could not find Cargo.toml", false)]
    [InlineData("scip-go", "unexpected flag: `--output`", false)]
    public void An_old_rust_analyzer_without_output_is_run_the_old_way(string tool, string stderr, bool legacy)
    {
        var indexer = ScipIndexers.All.Single(i => i.Tool == tool);

        Assert.Equal(legacy, ScipIndexers.NeedsLegacyInvocation(indexer, stderr));
    }
}

public sealed class OldRustAnalyzerTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codegraph-tests", Guid.NewGuid().ToString("n"))).FullName;

    public void Dispose() => TempDb.TryDelete(_root);

    [Theory]
    [InlineData("src/m0/invoice0.rs", "m0,invoice0")]
    [InlineData("src/m0/mod.rs", "m0")]
    [InlineData("src/main.rs", "")]
    [InlineData("m1/src/lib.rs", "")]
    [InlineData("m1/src/m1/order1.rs", "m1,order1")]
    public void A_file_names_its_module_path(string path, string modules)
    {
        Assert.Equal(modules.Split(',', StringSplitOptions.RemoveEmptyEntries), ScipImport.RustModulesOf(path));
    }

    [Fact]
    public void A_trait_impl_member_in_the_old_Type_Trait_member_form_belongs_to_the_type_and_implements_the_trait()
    {
        const string H = "rust-analyzer cargo corpus 0.1.0 ";
        var shape = new Document { RelativePath = "src/m0/i_shape0.rs", Language = "rust", Text = "pub trait IShape0 {\n    fn area(&self) -> i32;\n}" };
        shape.Occurrences.Add(new Occurrence { Symbol = H + "i_shape0/m0/IShape0#", SymbolRoles = (int)SymbolRole.Definition, SingleLineRange = new SingleLineRange { Line = 0, StartCharacter = 10, EndCharacter = 17 } });
        shape.Occurrences.Add(new Occurrence { Symbol = H + "i_shape0/m0/IShape0#area().", SymbolRoles = (int)SymbolRole.Definition, SingleLineRange = new SingleLineRange { Line = 1, StartCharacter = 7, EndCharacter = 11 } });
        // rust-analyzer 1.70 wrote no symbol kinds at all: the trait is told from `pub trait` on its line.
        shape.Symbols.Add(new SymbolInformation { Symbol = H + "i_shape0/m0/IShape0#" });
        shape.Symbols.Add(new SymbolInformation { Symbol = H + "i_shape0/m0/IShape0#area()." });
        var order = new Document { RelativePath = "src/m0/order0.rs", Language = "rust", Text = "pub struct Order0 { a: i32 }\nimpl crate::m0::i_shape0::IShape0 for Order0 {\n    fn area(&self) -> i32 { self.a }\n}" };
        order.Occurrences.Add(new Occurrence { Symbol = H + "order0/m0/Order0#", SymbolRoles = (int)SymbolRole.Definition, SingleLineRange = new SingleLineRange { Line = 0, StartCharacter = 11, EndCharacter = 17 } });
        order.Occurrences.Add(new Occurrence { Symbol = H + "i_shape0/m0/IShape0#", SingleLineRange = new SingleLineRange { Line = 1, StartCharacter = 26, EndCharacter = 33 } }); // the impl header
        order.Occurrences.Add(new Occurrence { Symbol = H + "order0/m0/Order0#IShape0#area().", SymbolRoles = (int)SymbolRole.Definition, SingleLineRange = new SingleLineRange { Line = 2, StartCharacter = 7, EndCharacter = 11 } });
        order.Symbols.Add(new SymbolInformation { Symbol = H + "order0/m0/Order0#", Kind = SymbolInformation.Types.Kind.Struct });
        order.Symbols.Add(new SymbolInformation { Symbol = H + "order0/m0/Order0#IShape0#area().", Kind = SymbolInformation.Types.Kind.Method });
        var index = new Scip.Index { Metadata = new Metadata { ToolInfo = new ToolInfo { Name = "rust-analyzer", Version = "0.1" }, ProjectRoot = new Uri(_root).AbsoluteUri } };
        index.Documents.Add(shape);
        index.Documents.Add(order);
        var path = Path.Combine(_root, "index.scip");
        using (var file = File.Create(path))
        {
            Google.Protobuf.MessageExtensions.WriteTo(index, file);
        }

        var batch = ScipImport.Read(path, _ => { });

        Assert.Equal(TypeKind.Interface, Assert.Single(batch.Nodes, n => n.Name == "IShape0").TypeKind);
        Assert.Equal(TypeKind.Struct, Assert.Single(batch.Nodes, n => n.Name == "Order0").TypeKind);
        var area = Assert.Single(batch.Nodes, n => n.Name == "area" && n.FullName.Contains("Order0"));
        Assert.Equal("scip:rust-analyzer cargo corpus m0/order0/impl#[Order0][IShape0]area().", area.Id);
        Assert.Equal("m0/order0/Order0.area", area.FullName);
        Assert.Contains(batch.Edges, e => e.Kind == EdgeKind.Contains && e.FromId == "scip:rust-analyzer cargo corpus m0/order0/Order0#" && e.ToId == area.Id);
        Assert.Contains(batch.Edges, e => e.Kind == EdgeKind.Implements && e.FromId == area.Id && e.ToId == "scip:rust-analyzer cargo corpus m0/i_shape0/IShape0#area().");
        Assert.Contains(batch.Edges, e => e.Kind == EdgeKind.Implements && e.FromId == "scip:rust-analyzer cargo corpus m0/order0/Order0#" && e.ToId == "scip:rust-analyzer cargo corpus m0/i_shape0/IShape0#");
    }

    [Fact]
    public void An_inline_test_module_inside_a_file_module_is_restored_too_and_a_body_is_found_without_enclosing_ranges()
    {
        // mod.rs of m0 holds `mod m0_tests { fn test0() {...} }`; the old rust-analyzer wrote m0_tests/m0/test0().
        var mod = new Document { RelativePath = "src/m0/mod.rs", Language = "rust", Text = "pub mod invoice0;\n#[cfg(test)]\nmod m0_tests {\n    #[test]\n    fn test0() {\n        let _ = crate::m0::invoice0::Invoice0::new(1);\n    }\n}" };
        mod.Occurrences.Add(new Occurrence { Symbol = "rust-analyzer cargo corpus 0.1.0 m0_tests/m0/test0().", SymbolRoles = (int)SymbolRole.Definition, SingleLineRange = new SingleLineRange { Line = 4, StartCharacter = 7, EndCharacter = 12 } });
        mod.Occurrences.Add(new Occurrence { Symbol = "rust-analyzer cargo corpus 0.1.0 invoice0/m0/Invoice0#new().", SingleLineRange = new SingleLineRange { Line = 5, StartCharacter = 47, EndCharacter = 50 } });
        mod.Symbols.Add(new SymbolInformation { Symbol = "rust-analyzer cargo corpus 0.1.0 m0_tests/m0/test0().", Kind = SymbolInformation.Types.Kind.Function });
        var invoice = new Document { RelativePath = "src/m0/invoice0.rs", Language = "rust", Text = "pub struct Invoice0 { a: i32 }\nimpl Invoice0 { pub fn new(a: i32) -> Self { Invoice0 { a } } }" };
        invoice.Occurrences.Add(new Occurrence { Symbol = "rust-analyzer cargo corpus 0.1.0 invoice0/m0/Invoice0#", SymbolRoles = (int)SymbolRole.Definition, SingleLineRange = new SingleLineRange { Line = 0, StartCharacter = 11, EndCharacter = 19 } });
        invoice.Occurrences.Add(new Occurrence { Symbol = "rust-analyzer cargo corpus 0.1.0 invoice0/m0/Invoice0#new().", SymbolRoles = (int)SymbolRole.Definition, SingleLineRange = new SingleLineRange { Line = 1, StartCharacter = 23, EndCharacter = 26 } });
        invoice.Symbols.Add(new SymbolInformation { Symbol = "rust-analyzer cargo corpus 0.1.0 invoice0/m0/Invoice0#", Kind = SymbolInformation.Types.Kind.Struct });
        invoice.Symbols.Add(new SymbolInformation { Symbol = "rust-analyzer cargo corpus 0.1.0 invoice0/m0/Invoice0#new().", Kind = SymbolInformation.Types.Kind.Method });
        var index = new Scip.Index { Metadata = new Metadata { ToolInfo = new ToolInfo { Name = "rust-analyzer", Version = "0.1" }, ProjectRoot = new Uri(_root).AbsoluteUri } };
        index.Documents.Add(mod);
        index.Documents.Add(invoice);
        var path = Path.Combine(_root, "index.scip");
        using (var file = File.Create(path))
        {
            Google.Protobuf.MessageExtensions.WriteTo(index, file);
        }

        var batch = ScipImport.Read(path, _ => { });

        var test = Assert.Single(batch.Nodes, n => n.Name == "test0");
        Assert.Equal("m0/m0_tests/test0", test.FullName);
        Assert.True(test.IsTest);
        Assert.Equal(5, test.StartLine);
        Assert.Equal(7, test.EndLine);
        Assert.Contains(batch.Edges, e => e.Kind == EdgeKind.Calls && e.FromId == test.Id && e.ToId == "scip:rust-analyzer cargo corpus m0/invoice0/Invoice0#new().");
        Assert.Contains("brace matching", batch.Meta[LanguageMetaKeys.Notes]);
    }

    [Fact]
    public void A_reversed_module_path_from_an_old_rust_analyzer_is_restored_for_definitions_and_references()
    {
        const string Old = "rust-analyzer cargo corpus 0.1.0 invoice0/m0/Invoice0#";
        var invoice = new Document { RelativePath = "src/m0/invoice0.rs", Language = "rust", Text = "pub struct Invoice0 { a: i32 }\nimpl Invoice0 { pub fn new(a: i32) -> Self { Invoice0 { a } } }" };
        invoice.Occurrences.Add(new Occurrence { Symbol = Old, SymbolRoles = (int)SymbolRole.Definition, SingleLineRange = new SingleLineRange { Line = 0, StartCharacter = 11, EndCharacter = 19 } });
        invoice.Occurrences.Add(new Occurrence { Symbol = Old + "new().", SymbolRoles = (int)SymbolRole.Definition, SingleLineRange = new SingleLineRange { Line = 1, StartCharacter = 23, EndCharacter = 26 } });
        invoice.Symbols.Add(new SymbolInformation { Symbol = Old, Kind = SymbolInformation.Types.Kind.Struct });
        invoice.Symbols.Add(new SymbolInformation { Symbol = Old + "new().", Kind = SymbolInformation.Types.Kind.Method });
        var main = new Document { RelativePath = "src/main.rs", Language = "rust", Text = "mod m0;\nfn main() { let _ = crate::m0::invoice0::Invoice0::new(1); }" };
        main.Occurrences.Add(new Occurrence { Symbol = "rust-analyzer cargo corpus 0.1.0 main().", SymbolRoles = (int)SymbolRole.Definition, SingleLineRange = new SingleLineRange { Line = 1, StartCharacter = 3, EndCharacter = 7 }, MultiLineEnclosingRange = new MultiLineRange { StartLine = 1, StartCharacter = 0, EndLine = 1, EndCharacter = 70 } });
        main.Occurrences.Add(new Occurrence { Symbol = Old + "new().", SingleLineRange = new SingleLineRange { Line = 1, StartCharacter = 51, EndCharacter = 54 } });
        main.Symbols.Add(new SymbolInformation { Symbol = "rust-analyzer cargo corpus 0.1.0 main().", Kind = SymbolInformation.Types.Kind.Function });
        var index = new Scip.Index { Metadata = new Metadata { ToolInfo = new ToolInfo { Name = "rust-analyzer", Version = "0.1" }, ProjectRoot = new Uri(_root).AbsoluteUri } };
        index.Documents.Add(invoice);
        index.Documents.Add(main);
        var path = Path.Combine(_root, "index.scip");
        using (var file = File.Create(path))
        {
            Google.Protobuf.MessageExtensions.WriteTo(index, file);
        }

        var batch = ScipImport.Read(path, _ => { });

        var ctor = Assert.Single(batch.Nodes, n => n.Name == "new");
        Assert.Equal("scip:rust-analyzer cargo corpus m0/invoice0/Invoice0#new().", ctor.Id);
        Assert.Equal("m0/invoice0/Invoice0.new", ctor.FullName);
        Assert.Contains(batch.Edges, e => e.Kind == EdgeKind.Calls && e.FromId == "scip:rust-analyzer cargo corpus main()." && e.ToId == ctor.Id);
    }
}

public sealed class GuessedScopeTests
{
    private static ScipImport.Span? Guess(string[] lines, int line, int start, int end, string language, out int guessed)
    {
        guessed = 0;
        return ScipImport.GuessEnclosing(lines, new ScipImport.Span(line, start, line, end), language, ref guessed);
    }

    [Fact]
    public void A_brace_body_is_found_after_the_name_and_nested_braces_and_strings_do_not_end_it()
    {
        string[] lines = ["impl Order {", "    pub fn total(&self) -> i32 {", "        let s = \"}\"; let c = '}';", "        if true { 1 } else { 2 }", "    }", "    fn other(&self) {}", "}"];

        var span = Guess(lines, 1, 11, 16, "rust", out var guessed);

        Assert.Equal(new ScipImport.Span(1, 0, 4, 5), span);
        Assert.Equal(1, guessed);
        Assert.Equal(new ScipImport.Span(5, 0, 5, 22), Guess(lines, 5, 7, 12, "rust", out _));
    }

    [Fact]
    public void A_declaration_without_a_body_has_no_scope()
    {
        string[] lines = ["pub trait Shape {", "    fn area(&self) -> i32;", "    fn name(&self) -> String { String::new() }", "}"];

        Assert.Null(Guess(lines, 1, 7, 11, "rust", out var guessed));
        Assert.Equal(0, guessed);
        Assert.Equal(new ScipImport.Span(2, 0, 2, 46), Guess(lines, 2, 7, 11, "rust", out _));
    }

    [Fact]
    public void A_python_body_is_the_indented_block()
    {
        string[] lines = ["class A:", "    def m(self):", "        x = 1", "", "        return x", "    def n(self):", "        pass"];

        Assert.Equal(new ScipImport.Span(1, 0, 4, 16), Guess(lines, 1, 8, 9, "python", out var guessed));
        Assert.Equal(1, guessed);
        Assert.Equal(new ScipImport.Span(5, 0, 6, 12), Guess(lines, 5, 8, 9, "python", out _));
    }
}
