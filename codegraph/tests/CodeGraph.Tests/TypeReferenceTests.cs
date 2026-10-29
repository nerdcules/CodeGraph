using CodeGraph.Core;

namespace CodeGraph.Tests;

public sealed class TypeReferenceTests
{
    private const string Model = "namespace N; public class Model { public static Model Empty => new(); }";

    [Theory]
    [InlineData("field type", "class C { Model? F; }", "F:N.C.F")]
    [InlineData("property type", "class C { Model? P { get; set; } }", "P:N.C.P")]
    [InlineData("parameter type", "class C { void M(Model m) { } }", "M:N.C.M(N.Model)")]
    [InlineData("return type", "class C { Model? M() => null; }", "M:N.C.M")]
    [InlineData("generic argument", "class C { System.Collections.Generic.List<Model>? F; }", "F:N.C.F")]
    [InlineData("local declaration", "class C { void M() { Model? m = null; _ = m; } }", "M:N.C.M")]
    [InlineData("object creation", "class C { object M() => new Model(); }", "M:N.C.M")]
    [InlineData("static member access", "class C { object M() => Model.Empty; }", "M:N.C.M")]
    [InlineData("cast and pattern", "class C { bool M(object o) => o is Model; }", "M:N.C.M(System.Object)")]
    [InlineData("typeof", "class C { System.Type M() => typeof(Model); }", "M:N.C.M")]
    [InlineData("qualified name", "class C { N.Model? F; }", "F:N.C.F")]
    public async Task Records_a_reference_from_the_member_that_mentions_the_type(string _, string source, string fromId)
    {
        var indexed = await Indexed.OfAsync(("Model.cs", Model), ("C.cs", $"namespace N; {source}"));

        Assert.True(indexed.HasEdge(fromId, "T:N.Model", EdgeKind.References),
            $"edges from {fromId}: {string.Join(", ", indexed.Edges.Where(e => e.FromId == fromId))}");
    }

    [Theory]
    [InlineData("var local", "class C { static Model Get() => new(); void M() { var m = Get(); _ = m; } }", "M:N.C.M")]
    [InlineData("var generic argument", "class C { static System.Collections.Generic.List<Model> Get() => new(); void M() { var l = Get(); _ = l; } }", "M:N.C.M")]
    [InlineData("var foreach", "class C { static Model[] All() => []; void M() { foreach (var m in All()) { _ = m; } } }", "M:N.C.M")]
    [InlineData("var deconstruction", "class C { static (Model, int) Pair() => default; void M() { var (m, i) = Pair(); _ = m; _ = i; } }", "M:N.C.M")]
    [InlineData("out var", "class C { static bool TryGet(out Model m) { m = new(); return true; } void M() { if (TryGet(out var m)) _ = m; } }", "M:N.C.M")]
    public async Task Records_a_reference_from_a_var_declaration_to_the_inferred_type(string _, string source, string fromId)
    {
        var indexed = await Indexed.OfAsync(("Model.cs", Model), ("C.cs", $"namespace N; {source}"));

        Assert.True(indexed.HasEdge(fromId, "T:N.Model", EdgeKind.References),
            $"edges from {fromId}: {string.Join(", ", indexed.Edges.Where(e => e.FromId == fromId))}");
    }

    [Fact]
    public async Task Does_not_record_a_reference_from_var_to_a_framework_type()
    {
        var indexed = await Indexed.OfAsync("namespace N; class C { void M() { var sb = new System.Text.StringBuilder(); _ = sb; } }");

        Assert.Empty(indexed.TargetsOf("M:N.C.M", EdgeKind.References));
    }

    [Theory]
    [InlineData("field through alias", "using M = N.Model;\nnamespace N; class C { M? F; }", "F:N.C.F")]
    [InlineData("static access through alias", "using M = N.Model;\nnamespace N; class C { object X() => M.Empty; }", "M:N.C.X")]
    [InlineData("generic alias (C# 12 alias any type)", "using Models = System.Collections.Generic.List<N.Model>;\nnamespace N; class C { Models? F; }", "F:N.C.F")]
    [InlineData("tuple alias", "using Pair = (N.Model First, int Second);\nnamespace N; class C { Pair F; }", "F:N.C.F")]
    [InlineData("global alias from another file", "namespace N; class C { M? F; }", "F:N.C.F")]
    public async Task Records_a_reference_through_a_using_alias(string _, string source, string fromId)
    {
        var indexed = await Indexed.OfAsync(
            ("Model.cs", Model),
            ("Globals.cs", "global using M = N.Model;"),
            ("C.cs", source));

        Assert.True(indexed.HasEdge(fromId, "T:N.Model", EdgeKind.References),
            $"edges from {fromId}: {string.Join(", ", indexed.Edges.Where(e => e.FromId == fromId))}");
    }

    [Fact]
    public async Task Records_a_reference_through_an_attribute_without_its_suffix()
    {
        var indexed = await Indexed.OfAsync(
            """
            namespace N;
            class MarkerAttribute : System.Attribute { }
            [Marker] class C { [Marker] void M() { } }
            """);

        Assert.True(indexed.HasEdge("T:N.C", "T:N.MarkerAttribute", EdgeKind.References));
        Assert.True(indexed.HasEdge("M:N.C.M", "T:N.MarkerAttribute", EdgeKind.References));
    }

    [Fact]
    public async Task Records_type_level_references_from_record_parameters_and_base_list_generic_arguments()
    {
        var indexed = await Indexed.OfAsync(
            ("Model.cs", Model),
            ("C.cs",
             """
             namespace N;
             interface IHandler<T> { }
             record R(Model M);
             class H : IHandler<Model> { }
             """));

        Assert.True(indexed.HasEdge("T:N.R", "T:N.Model", EdgeKind.References));
        Assert.True(indexed.HasEdge("T:N.H", "T:N.Model", EdgeKind.References));
    }

    [Fact]
    public async Task Does_not_duplicate_inheritance_as_a_reference()
    {
        var indexed = await Indexed.OfAsync("namespace N; class Base { } interface I { } class D : Base, I { }");

        Assert.True(indexed.HasEdge("T:N.D", "T:N.Base", EdgeKind.Inherits));
        Assert.DoesNotContain("T:N.Base", indexed.TargetsOf("T:N.D", EdgeKind.References));
        Assert.DoesNotContain("T:N.I", indexed.TargetsOf("T:N.D", EdgeKind.References));
    }

    [Fact]
    public async Task Ignores_framework_types()
    {
        var indexed = await Indexed.OfAsync("namespace N; class C { System.Text.StringBuilder? F; }");

        Assert.Empty(indexed.TargetsOf("F:N.C.F", EdgeKind.References));
    }

    [Fact]
    public async Task Records_the_file_that_produced_each_edge()
    {
        var indexed = await Indexed.OfAsync(("Model.cs", Model), ("C.cs", "namespace N; class C { Model? F; }"));

        Assert.Contains(new EdgeOrigin(new GraphEdge("F:N.C.F", "T:N.Model", EdgeKind.References), Indexed.PathOf("C.cs")), indexed.Origins);
    }

    [Fact]
    public async Task Locates_a_partial_type_at_its_primary_declaration_whichever_part_is_walked()
    {
        (string, string)[] files =
        [
            ("P1.cs", "namespace N; partial class P { }"),
            ("P2.cs", "namespace N;\n\npartial class P { }"),
        ];

        var onlySecond = await Indexed.OfAsync(["P2.cs"], files);

        Assert.Equal(Indexed.PathOf("P1.cs"), onlySecond.Node("T:N.P").FilePath);
    }

    [Fact]
    public void Impact_of_a_type_includes_members_that_reference_it()
    {
        using var db = new TempDb();
        db.Store.Write(
            [
                Graph.Node("T:N.Model", NodeKind.Type, "Model", "N.Model"),
                Graph.Node("M:N.C.Use", NodeKind.Method, "Use", "N.C.Use"),
                Graph.Node("M:N.D.CallsUse", NodeKind.Method, "CallsUse", "N.D.CallsUse"),
            ],
            [
                new GraphEdge("M:N.C.Use", "T:N.Model", EdgeKind.References),
                new GraphEdge("M:N.D.CallsUse", "M:N.C.Use", EdgeKind.Calls),
            ]);

        var impact = db.Store.Impact("T:N.Model").Select(h => h.Node.Id).ToList();

        Assert.Equal(["M:N.C.Use", "M:N.D.CallsUse"], impact);
    }
}
