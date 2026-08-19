using CodeGraph.Core;

namespace CodeGraph.Tests;

/// <summary>
/// A small hand-built graph shared by the store tests, shaped like the real thing:
/// a project containing a file containing a type containing members, plus a call chain
/// Top → Handle → Save, an interface Service implements, and a test that guards Save.
/// </summary>
internal static class Graph
{
    public const string Project = "P:Demo";
    public const string File = "F:/repo/Service.cs";
    public const string Service = "T:Demo.Service";
    public const string IService = "T:Demo.IService";
    public const string Save = "M:Demo.Service.Save";
    public const string SaveAll = "M:Demo.Service.SaveAllAsync";
    public const string Handle = "M:Demo.Caller.Handle";
    public const string Top = "M:Demo.Outer.Top";
    public const string SaveTest = "M:Demo.Tests.SaveTest";

    public static GraphNode[] Nodes =>
    [
        Node(Project, NodeKind.Project, "Demo", "Demo"),
        Node(File, NodeKind.File, "Service.cs", "/repo/Service.cs"),
        Node(Service, NodeKind.Type, "Service", "Demo.Service"),
        Node(IService, NodeKind.Type, "IService", "Demo.IService"),
        Node(Save, NodeKind.Method, "Save", "Demo.Service.Save", signature: "public void Save()"),
        Node(SaveAll, NodeKind.Method, "SaveAllAsync", "Demo.Service.SaveAllAsync"),
        Node(Handle, NodeKind.Method, "Handle", "Demo.Caller.Handle"),
        Node(Top, NodeKind.Method, "Top", "Demo.Outer.Top"),
        Node(SaveTest, NodeKind.Method, "SaveTest", "Demo.Tests.SaveTest", isTest: true),
    ];

    public static GraphEdge[] Edges =>
    [
        new(Project, File, EdgeKind.Contains),
        new(File, Service, EdgeKind.Contains),
        new(Service, Save, EdgeKind.Contains),
        new(Service, SaveAll, EdgeKind.Contains),
        new(Handle, Save, EdgeKind.Calls),
        new(Top, Handle, EdgeKind.Calls),
        new(SaveTest, Save, EdgeKind.Calls),
        new(Save, SaveTest, EdgeKind.TestedBy),
        new(Service, IService, EdgeKind.Implements),
    ];

    public static GraphNode Node(
        string id,
        NodeKind kind,
        string name,
        string fullName,
        string? signature = null,
        string? filePath = "/repo/Service.cs",
        int startLine = 1,
        int endLine = 2,
        bool isTest = false,
        TypeKind typeKind = TypeKind.None,
        Accessibility accessibility = Accessibility.None,
        bool isStatic = false,
        bool isAbstract = false,
        bool isVirtual = false,
        bool isSealed = false,
        bool isAsync = false,
        bool isOverride = false,
        MemberKind memberKind = MemberKind.Ordinary) =>
        new(id, kind, name, fullName, signature, filePath, startLine, endLine, isTest,
            typeKind, accessibility, isStatic, isAbstract, isVirtual, isSealed, isAsync, isOverride, memberKind);

    public static GraphStore Seeded(this GraphStore store)
    {
        store.Write(Nodes, Edges);
        return store;
    }
}
