using CodeGraph.Core;

namespace CodeGraph.Tests;

public sealed class RoslynIndexerTests
{
    [Fact]
    public async Task Emits_a_project_node()
    {
        var indexed = await Indexed.OfAsync("namespace N; class C { }");

        Assert.Equal(NodeKind.Project, indexed.Node("P:Demo").Kind);
    }

    [Fact]
    public async Task Nests_project_to_file_to_type_to_member_by_containment()
    {
        var indexed = await Indexed.OfAsync("namespace N; class C { void M() { } }");

        var file = indexed.Nodes.Single(n => n.Kind == NodeKind.File);

        Assert.True(indexed.HasEdge("P:Demo", file.Id, EdgeKind.Contains));
        Assert.True(indexed.HasEdge(file.Id, "T:N.C", EdgeKind.Contains));
        Assert.True(indexed.HasEdge("T:N.C", "M:N.C.M", EdgeKind.Contains));
    }

    [Fact]
    public async Task Uses_documentation_comment_ids_including_parameter_types()
    {
        var indexed = await Indexed.OfAsync("namespace N; class C { void M(string s, int i) { } }");

        Assert.True(indexed.Has("M:N.C.M(System.String,System.Int32)"));
    }

    [Fact]
    public async Task Records_each_member_kind()
    {
        var indexed = await Indexed.OfAsync(
            """
            namespace N;
            class C
            {
                void M() { }
                int P { get; set; }
                int F;
                event System.Action E;
            }
            """);

        Assert.Equal(NodeKind.Method, indexed.Node("M:N.C.M").Kind);
        Assert.Equal(NodeKind.Property, indexed.Node("P:N.C.P").Kind);
        Assert.Equal(NodeKind.Field, indexed.Node("F:N.C.F").Kind);
        Assert.Equal(NodeKind.Event, indexed.Node("E:N.C.E").Kind);
    }

    [Fact]
    public async Task Captures_the_declaration_span_and_file_of_a_member()
    {
        var indexed = await Indexed.OfAsync(
            """
            namespace N;
            class C
            {
                void M()
                {
                }
            }
            """);

        var method = indexed.Node("M:N.C.M");

        Assert.Equal(4, method.StartLine);
        Assert.Equal(6, method.EndLine);
        Assert.EndsWith("Test.cs", method.FilePath);
    }

    [Fact]
    public async Task Records_a_readable_signature()
    {
        var indexed = await Indexed.OfAsync("namespace N; class C { public int M(string s) => 0; }");

        Assert.Equal("public int M(string s)", indexed.Node("M:N.C.M(System.String)").Signature);
    }

    [Fact]
    public async Task Resolves_a_call_to_the_callee_symbol()
    {
        var indexed = await Indexed.OfAsync(
            """
            namespace N;
            class C
            {
                void Caller() { Callee(); }
                void Callee() { }
            }
            """);

        Assert.True(indexed.HasEdge("M:N.C.Caller", "M:N.C.Callee", EdgeKind.Calls));
    }

    [Fact]
    public async Task Resolves_a_call_across_files()
    {
        var indexed = await Indexed.OfAsync(
            ("A.cs", "namespace N; class A { public void Run() { new B().Work(); } }"),
            ("B.cs", "namespace N; class B { public void Work() { } }"));

        Assert.True(indexed.HasEdge("M:N.A.Run", "M:N.B.Work", EdgeKind.Calls));
    }

    [Fact]
    public async Task Distinguishes_overloads_by_parameter_types()
    {
        var indexed = await Indexed.OfAsync(
            """
            namespace N;
            class C
            {
                void Caller() { M("x"); }
                void M(string s) { }
                void M(int i) { }
            }
            """);

        Assert.True(indexed.HasEdge("M:N.C.Caller", "M:N.C.M(System.String)", EdgeKind.Calls));
        Assert.False(indexed.HasEdge("M:N.C.Caller", "M:N.C.M(System.Int32)", EdgeKind.Calls));
    }

    [Fact]
    public async Task Does_not_emit_calls_into_the_framework()
    {
        var indexed = await Indexed.OfAsync(
            "namespace N; class C { void M() { System.Console.WriteLine(\"hi\"); } }");

        // Only source in the indexed solution becomes an edge; framework and NuGet targets are dropped.
        Assert.Empty(indexed.TargetsOf("M:N.C.M", EdgeKind.Calls));
    }

    [Fact]
    public async Task Records_inheritance()
    {
        var indexed = await Indexed.OfAsync(
            """
            namespace N;
            class Base { }
            class Derived : Base { }
            """);

        Assert.True(indexed.HasEdge("T:N.Derived", "T:N.Base", EdgeKind.Inherits));
    }

    [Fact]
    public async Task Records_interface_implementation()
    {
        var indexed = await Indexed.OfAsync(
            """
            namespace N;
            interface IThing { }
            class Thing : IThing { }
            """);

        Assert.True(indexed.HasEdge("T:N.Thing", "T:N.IThing", EdgeKind.Implements));
    }

    [Fact]
    public async Task Records_an_override()
    {
        var indexed = await Indexed.OfAsync(
            """
            namespace N;
            class Base { public virtual void M() { } }
            class Derived : Base { public override void M() { } }
            """);

        Assert.True(indexed.HasEdge("M:N.Derived.M", "M:N.Base.M", EdgeKind.Overrides));
    }

    [Theory]
    [InlineData("Fact")]
    [InlineData("Theory")]
    [InlineData("Test")]
    [InlineData("TestCase")]
    [InlineData("TestMethod")]
    public async Task Flags_a_method_carrying_a_known_test_attribute(string attribute)
    {
        var indexed = await Indexed.OfAsync(
            $$"""
            namespace N;
            class {{attribute}}Attribute : System.Attribute { }
            class Tests
            {
                [{{attribute}}]
                public void Guards() { }
            }
            """);

        Assert.True(indexed.Node("M:N.Tests.Guards").IsTest);
    }

    [Fact]
    public async Task Leaves_an_ordinary_method_unflagged()
    {
        var indexed = await Indexed.OfAsync("namespace N; class C { public void M() { } }");

        Assert.False(indexed.Node("M:N.C.M").IsTest);
    }

    [Fact]
    public async Task A_call_from_a_test_also_emits_a_TestedBy_edge_pointing_back_at_the_test()
    {
        var indexed = await Indexed.OfAsync(
            """
            namespace N;
            class FactAttribute : System.Attribute { }
            class Service { public void Save() { } }
            class Tests
            {
                [Fact]
                public void SaveWorks() { new Service().Save(); }
            }
            """);

        Assert.True(indexed.HasEdge("M:N.Tests.SaveWorks", "M:N.Service.Save", EdgeKind.Calls));
        Assert.True(indexed.HasEdge("M:N.Service.Save", "M:N.Tests.SaveWorks", EdgeKind.TestedBy));
    }

    [Fact]
    public async Task A_call_from_a_non_test_emits_no_TestedBy_edge()
    {
        var indexed = await Indexed.OfAsync(
            """
            namespace N;
            class Service { public void Save() { } }
            class Caller { public void Run() { new Service().Save(); } }
            """);

        Assert.Empty(indexed.TargetsOf("M:N.Service.Save", EdgeKind.TestedBy));
    }

    [Fact]
    public async Task Indexing_is_idempotent_for_a_repeated_type_name()
    {
        var indexed = await Indexed.OfAsync(
            """
            namespace N;
            partial class C { void A() { } }
            partial class C { void B() { } }
            """);

        // One node per symbol id even though the type is declared twice.
        Assert.Single(indexed.Nodes, n => n.Id == "T:N.C");
        Assert.True(indexed.Has("M:N.C.A"));
        Assert.True(indexed.Has("M:N.C.B"));
    }

    [Theory]
    [InlineData("class C { }", "T:N.C", TypeKind.Class)]
    [InlineData("struct S { }", "T:N.S", TypeKind.Struct)]
    [InlineData("record R(int X);", "T:N.R", TypeKind.Record)]
    [InlineData("record struct RS(int X);", "T:N.RS", TypeKind.RecordStruct)]
    [InlineData("interface I { }", "T:N.I", TypeKind.Interface)]
    [InlineData("enum E { A }", "T:N.E", TypeKind.Enum)]
    public async Task Sets_TypeKind_on_type_nodes(string decl, string id, TypeKind expected)
    {
        var indexed = await Indexed.OfAsync($"namespace N; {decl}");

        Assert.Equal(expected, indexed.Node(id).TypeKind);
    }

    [Theory]
    [InlineData("public class C { }", "T:N.C", Accessibility.Public)]
    [InlineData("internal class C { }", "T:N.C", Accessibility.Internal)]
    [InlineData("class C { private void M() { } }", "M:N.C.M", Accessibility.Private)]
    [InlineData("class C { protected void M() { } }", "M:N.C.M", Accessibility.Protected)]
    public async Task Sets_Accessibility_on_nodes(string decl, string id, Accessibility expected)
    {
        var indexed = await Indexed.OfAsync($"namespace N; {decl}");

        Assert.Equal(expected, indexed.Node(id).Accessibility);
    }

    [Fact]
    public async Task Sets_IsStatic_IsAbstract_IsVirtual_IsSealed_IsAsync_IsOverride()
    {
        var indexed = await Indexed.OfAsync(
            """
            namespace N;
            abstract class Base
            {
                public static void S() { }
                public abstract void Ab();
                public virtual void Vi() { }
                public async System.Threading.Tasks.Task As() { await System.Threading.Tasks.Task.Yield(); }
            }
            class Derived : Base
            {
                public override void Ab() { }
                public sealed override void Vi() { }
            }
            """);

        Assert.True(indexed.Node("M:N.Base.S").IsStatic);
        Assert.True(indexed.Node("M:N.Base.Ab").IsAbstract);
        Assert.True(indexed.Node("M:N.Base.Vi").IsVirtual);
        Assert.True(indexed.Node("M:N.Base.As").IsAsync);
        Assert.True(indexed.Node("M:N.Derived.Ab").IsOverride);
        Assert.True(indexed.Node("M:N.Derived.Vi").IsSealed);
    }

    [Fact]
    public async Task Sets_MemberKind_Constructor_for_constructors()
    {
        var indexed = await Indexed.OfAsync("namespace N; class C { public C() { } }");

        Assert.Equal(MemberKind.Constructor, indexed.Node("M:N.C.#ctor").MemberKind);
        Assert.Equal(MemberKind.Ordinary, indexed.Node("T:N.C").MemberKind);
    }

    [Fact]
    public async Task Nested_type_has_Contains_edge_from_parent_type()
    {
        var indexed = await Indexed.OfAsync(
            """
            namespace N;
            class Outer
            {
                class Inner { }
            }
            """);

        Assert.True(indexed.HasEdge("T:N.Outer", "T:N.Outer.Inner", EdgeKind.Contains));
    }

    [Fact]
    public async Task The_indexed_graph_answers_impact_through_the_store()
    {
        var indexed = await Indexed.OfAsync(
            """
            namespace N;
            class FactAttribute : System.Attribute { }
            class Service { public void Save() { } }
            class Caller { public void Run() { new Service().Save(); } }
            class Tests
            {
                [Fact]
                public void SaveWorks() { new Service().Save(); }
            }
            """);

        using var db = new TempDb();
        db.Store.Write(indexed.Nodes, indexed.Edges);

        var impact = db.Store.Impact("M:N.Service.Save");

        Assert.Contains(impact, h => h.Node.Id == "M:N.Caller.Run");
        Assert.Contains(impact, h => h.Node.IsTest && h.Node.Id == "M:N.Tests.SaveWorks");
        Assert.Equal(["M:N.Tests.SaveWorks"], db.Store.DirectTests("M:N.Service.Save").Select(n => n.Id));
    }
}
