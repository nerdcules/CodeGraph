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
