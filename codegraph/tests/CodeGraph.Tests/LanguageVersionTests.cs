using CodeGraph.Core;
using CodeGraph.Indexer;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeGraph.Tests;

/// <summary>
/// How the indexer reads code on another C# version than its Roslyn's latest, and how it reports what it
/// could not read: a repo ahead of the compiler, or a declaration shape the walker has no mapping for.
/// </summary>
public sealed class LanguageVersionTests
{
    [Fact]
    public async Task Reports_the_compiler_and_the_version_each_project_asked_for()
    {
        var indexed = await Indexed.OfAsync(LanguageVersion.CSharp12, "namespace N; class C { void M() { } }");

        Assert.Matches(@"^Roslyn \d+\.\d+\.\d+$", RoslynIndexer.CompilerVersion);
        Assert.Matches(@"^C# \d+$", RoslynIndexer.NewestLanguageVersion);
        Assert.Equal(["C# 12"], indexed.Indexer.LanguageVersions);
        Assert.Equal(0, indexed.Indexer.CompileErrors);
        Assert.Empty(indexed.Indexer.Unmapped);
        Assert.Contains(indexed.Indexer.CompilerSummary(), line => line.Contains("projects use C# 12; 0 compile error(s) in declarations"));
    }

    [Fact]
    public async Task Default_is_this_Roslyns_latest_major_as_a_project_without_LangVersion_gets()
    {
        var indexed = await Indexed.OfAsync("namespace N; class C { }");

        Assert.Equal([RoslynIndexer.NewestLanguageVersion], indexed.Indexer.LanguageVersions);
    }

    [Fact]
    public async Task Code_newer_than_the_project_language_version_is_counted_not_hidden()
    {
        // Primary constructors are C# 12; on C# 10 the parser keeps going but reports the feature.
        var indexed = await Indexed.OfAsync(LanguageVersion.CSharp10,
            """
            namespace N;
            class C(int x) { public int X => x; }
            class Fine { void M() { } }
            """);

        Assert.True(indexed.Indexer.LanguageVersionErrors >= 1, indexed.Indexer.UnmappedSummary());
        Assert.True(indexed.Indexer.CompileErrors >= indexed.Indexer.LanguageVersionErrors);
        Assert.StartsWith("Test.cs(2): CS", indexed.Indexer.FirstLanguageVersionError);
        Assert.Contains("12.0", indexed.Indexer.FirstLanguageVersionError);
        Assert.Contains(indexed.Indexer.CompilerSummary(), line => line.StartsWith("⚠") && line.Contains("newer C#"));
        // What the parser could read is still there.
        Assert.True(indexed.Has("M:N.Fine.M"));
    }

    [Fact]
    public async Task Plain_syntax_errors_are_compile_errors_but_not_language_version_errors()
    {
        var indexed = await Indexed.OfAsync("namespace N; class C { void M( { } }");

        Assert.True(indexed.Indexer.CompileErrors >= 1);
        Assert.Equal(0, indexed.Indexer.LanguageVersionErrors);
        Assert.Null(indexed.Indexer.FirstLanguageVersionError);
    }

    [Fact]
    public void Index_status_shows_each_language_its_compiler_and_the_warnings_that_mean_missing_code()
    {
        var meta = new Dictionary<string, string>
        {
            [MetaKeys.Solution] = "/repo/App.sln",
            [MetaKeys.Language(Languages.CSharp, LanguageMetaKeys.Compiler)] = "Roslyn 5.9.0",
            [MetaKeys.Language(Languages.CSharp, LanguageMetaKeys.NewestLanguageVersion)] = "C# 14",
            [MetaKeys.Language(Languages.CSharp, LanguageMetaKeys.LanguageVersions)] = "C# 12, C# 14",
            [MetaKeys.Language(Languages.CSharp, LanguageMetaKeys.CompileErrors)] = "3",
            [MetaKeys.Language(Languages.CSharp, LanguageMetaKeys.LanguageVersionErrors)] = "2",
            [MetaKeys.Language(Languages.CSharp, LanguageMetaKeys.FirstLanguageVersionError)] = "New.cs(4): CS9260 Feature 'extensions' is not available in C# 13.0",
            [MetaKeys.Language(Languages.CSharp, LanguageMetaKeys.Unmapped)] = "TypeKind.Extension x1",
            [MetaKeys.Language(Languages.TypeScript, LanguageMetaKeys.Tool)] = "codegraph-ts 0.1.0",
            [MetaKeys.Language(Languages.TypeScript, LanguageMetaKeys.CompileErrors)] = "0",
            [MetaKeys.Language(Languages.TypeScript, LanguageMetaKeys.Unmapped)] = "",
            [MetaKeys.Language(Languages.Java, LanguageMetaKeys.Tool)] = "scip-java 0.12.3",
        };

        var lines = LanguageStatus.Lines(meta).ToList();

        Assert.Equal(
        [
            "csharp    : Roslyn 5.9.0 (up to C# 14); code uses C# 12, C# 14; 3 compile error(s)",
            "  ⚠ 2 error(s) say the code needs a newer language version than it was read with; declarations under them are missing. First: New.cs(4): CS9260 Feature 'extensions' is not available in C# 13.0",
            "  ⚠ shapes this indexer has no mapping for (a newer language feature): TypeKind.Extension x1",
            "java      : scip-java 0.12.3",
            "typescript: codegraph-ts 0.1.0",
        ], lines);
        Assert.Equal("csharp", MetaKeys.LanguageOf("lang.csharp.compiler"));
        Assert.Null(MetaKeys.LanguageOf("solution"));
        Assert.Null(MetaKeys.LanguageOf("lang.csharp"));
    }

    [Fact]
    public void A_graph_without_compiler_facts_says_so_rather_than_nothing()
    {
        var lines = LanguageStatus.Lines(new Dictionary<string, string> { ["lang.csharp.indexed_at"] = "2026-01-01" }).ToList();

        Assert.Equal(["csharp    : (no compiler facts recorded; re-index to add them)"], lines);
    }
}
