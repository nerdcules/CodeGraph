using CodeGraph.Core;
using CodeGraph.Indexer;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeGraph.Tests;

/// <summary>
/// The C# version axis. Each feature below names the version that introduced it, a file using it, and what the
/// graph must hold for it. Every version this Roslyn knows indexes every feature valid at it with no errors and
/// no unmapped shape; a feature used one version too early is reported, not hidden. When Roslyn learns a new C#,
/// <see cref="The_matrix_covers_every_version_this_Roslyn_knows"/> fails until its features are added here.
/// </summary>
public sealed class CSharpVersionMatrixTests
{
    private static readonly LanguageVersion[] Ladder =
    [
        LanguageVersion.CSharp9, LanguageVersion.CSharp10, LanguageVersion.CSharp11, LanguageVersion.CSharp12, LanguageVersion.CSharp13, LanguageVersion.CSharp14,
    ];

    /// <param name="Declared">True when the feature gate is in syntax or a declaration, which the indexer's error count sees. Body-level gates cost at most an edge and are not counted.</param>
    private sealed record Feature(string Name, LanguageVersion Min, string Source, Action<Indexed> Check, bool Declared = true)
    {
        public override string ToString() => Name;
    }

    private static readonly Feature[] Features =
    [
        new("file-scoped namespace", LanguageVersion.CSharp10, "namespace N;\nclass FileScoped { void M() { } }",
            g => Assert.True(g.HasEdge("T:N.FileScoped", "M:N.FileScoped.M", EdgeKind.Contains))),
        new("record struct", LanguageVersion.CSharp10, "namespace N { record struct Point(int X, int Y); }",
            g => Assert.Equal(TypeKind.RecordStruct, g.Node("T:N.Point").TypeKind)),
        new("raw string literal", LanguageVersion.CSharp11, "namespace N { class Raw { string S = \"\"\"\n  hi\n  \"\"\"; } }",
            g => Assert.True(g.Has("F:N.Raw.S")), Declared: false),
        new("required member", LanguageVersion.CSharp11, "namespace N { class Req { public required int X { get; set; } } }",
            g => Assert.Equal(NodeKind.Property, g.Node("P:N.Req.X").Kind)),
        new("generic attribute", LanguageVersion.CSharp11, "namespace N { class GA<T> : System.Attribute { } [GA<int>] class UsesGA { } }",
            g => Assert.True(g.Has("T:N.UsesGA"))),
        new("static abstract interface member", LanguageVersion.CSharp11, "namespace N { interface IZero<T> { static abstract T Zero { get; } } }",
            g => Assert.True(g.Node("P:N.IZero`1.Zero").IsStatic)),
        new("list pattern", LanguageVersion.CSharp11, "namespace N { class LP { bool M(int[] a) => a is [1, ..]; } }",
            g => Assert.True(g.Has("M:N.LP.M(System.Int32[])")), Declared: false),
        new("primary constructor", LanguageVersion.CSharp12, "namespace N { class PC(int x) { public int X => x; } }",
            g => Assert.True(g.HasEdge("T:N.PC", "M:N.PC.#ctor(System.Int32)", EdgeKind.Contains))),
        new("collection expression", LanguageVersion.CSharp12, "namespace N { class CE { int[] A = [1, 2]; } }",
            g => Assert.True(g.Has("F:N.CE.A")), Declared: false),
        new("default lambda parameter", LanguageVersion.CSharp12, "namespace N { class DL { System.Func<int, int> F = (int a = 1) => a; } }",
            g => Assert.True(g.Has("F:N.DL.F")), Declared: false),
        new("params collection", LanguageVersion.CSharp13, "namespace N { class PS { void M(params System.ReadOnlySpan<int> xs) { } } }",
            g => Assert.True(g.Has("M:N.PS.M(System.ReadOnlySpan{System.Int32})"))),
        new("partial property", LanguageVersion.CSharp13, "namespace N { partial class PP { public partial int P { get; } } partial class PP { public partial int P => 1; } }",
            g => Assert.True(g.HasEdge("T:N.PP", "P:N.PP.P", EdgeKind.Contains))),
        new("extension block", LanguageVersion.CSharp14,
            """
            namespace N
            {
                static class Ext { extension(int x) { public int Twice() => x * 2; public bool IsEven => x % 2 == 0; } }
                class ExtUser { int M() => 3.Twice() + (4.IsEven ? 1 : 0); }
            }
            """,
            g =>
            {
                var block = Assert.Single(g.Nodes, n => n.TypeKind == TypeKind.Extension);
                Assert.Equal("extension(int)", block.Name);
                Assert.Equal("N.Ext.extension(int)", block.FullName);
                Assert.True(g.HasEdge("T:N.Ext", block.Id, EdgeKind.Contains));
                var twice = Assert.Single(g.Nodes, n => n.Name == "Twice");
                Assert.True(g.HasEdge(block.Id, twice.Id, EdgeKind.Contains));
                Assert.True(g.HasEdge("M:N.ExtUser.M", twice.Id, EdgeKind.Calls));
                Assert.Contains(g.SearchDocuments, d => d.NodeId == block.Id && d.Terms.ContainsKey("extension"));
            }),
        new("field keyword", LanguageVersion.CSharp14, "namespace N { class FK { public int P { get => field; set => field = value; } } }",
            g => Assert.True(g.Has("P:N.FK.P")), Declared: false),
        new("null-conditional assignment", LanguageVersion.CSharp14, "namespace N { class NC { public int X; void M(NC? o) { o?.X = 1; } } }",
            g => Assert.True(g.Has("M:N.NC.M(N.NC)")), Declared: false),
        new("nameof of an unbound generic", LanguageVersion.CSharp14, "namespace N { class NG { string S = nameof(System.Collections.Generic.List<>); } }",
            g => Assert.True(g.Has("F:N.NG.S")), Declared: false),
    ];

    public static TheoryData<LanguageVersion> Versions => [.. Ladder];

    public static TheoryData<string> DeclaredFeatures => [.. Features.Where(f => f.Declared).Select(f => f.Name)];

    private static (string Name, string Source) FileOf(Feature feature, int i) => ($"F{i}.cs", feature.Source);

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task Every_feature_valid_at_a_version_indexes_with_no_errors_and_no_unmapped_shape(LanguageVersion version)
    {
        var valid = Features.Where(f => f.Min <= version).ToList();
        if (valid.Count == 0)
        {
            return;
        }

        var indexed = await Indexed.OfAsync(walk: null, version, [.. valid.Select(FileOf)]);

        Assert.Equal([RoslynIndexer.Display(version)], indexed.Indexer.LanguageVersions);
        Assert.Equal(0, indexed.Indexer.CompileErrors);
        Assert.Equal("", indexed.Indexer.UnmappedSummary());
        foreach (var feature in valid)
        {
            feature.Check(indexed);
        }
    }

    [Theory]
    [MemberData(nameof(DeclaredFeatures))]
    public async Task A_feature_used_one_version_too_early_is_reported_not_hidden(string name)
    {
        var feature = Features.Single(f => f.Name == name);
        var tooEarly = Ladder[Array.IndexOf(Ladder, feature.Min) - 1];

        var indexed = await Indexed.OfAsync(tooEarly, feature.Source);

        Assert.True(indexed.Indexer.LanguageVersionErrors >= 1, $"{name} on {RoslynIndexer.Display(tooEarly)} was not reported");
        Assert.Contains(feature.Min.ToDisplayString(), indexed.Indexer.FirstLanguageVersionError);
        Assert.StartsWith("Test.cs(", indexed.Indexer.FirstLanguageVersionError);
    }

    [Fact]
    public void The_matrix_covers_every_version_this_Roslyn_knows()
    {
        var newest = LanguageVersion.Latest.MapSpecifiedToEffectiveVersion();

        Assert.True(Ladder[^1] == newest,
            $"Roslyn now knows {RoslynIndexer.Display(newest)}: add it to the ladder and its declaration features to the matrix, then check what the indexer reports as unmapped");
    }

    [Fact]
    public async Task The_same_code_gives_the_same_graph_on_every_version_that_accepts_it()
    {
        // Backward compatibility in one line: a repo moving its LangVersion up (or down) must not change its graph.
        var common = Features.Where(f => f.Min <= LanguageVersion.CSharp10).Select(FileOf).ToArray();
        var graphs = new List<string>();
        foreach (var version in Ladder.Where(v => v >= LanguageVersion.CSharp10))
        {
            var indexed = await Indexed.OfAsync(walk: null, version, common);
            graphs.Add(string.Join("\n", indexed.Nodes.Select(n => $"{n.Id} {n.Kind} {n.TypeKind} {n.Signature}").Concat(indexed.Edges.Select(e => $"{e.FromId} {e.Kind} {e.ToId}")).Order(StringComparer.Ordinal)));
        }

        Assert.All(graphs, g => Assert.Equal(graphs[0], g));
    }
}
