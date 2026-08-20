namespace CodeGraph.Core;

public enum NodeKind
{
    Project,
    File,
    Type,
    Method,
    Property,
    Field,
    Event,
}

/// <summary>More precise classification of a Type node.</summary>
public enum TypeKind { None, Class, Struct, Record, RecordStruct, Interface, Enum, Delegate }

/// <summary>Declared accessibility of a member or type.</summary>
public enum Accessibility { None, Public, Private, Protected, Internal, ProtectedOrInternal, ProtectedAndInternal }

/// <summary>Distinguishes constructors, destructors and operators from ordinary methods.</summary>
public enum MemberKind { Ordinary, Constructor, Destructor, Operator }

public enum EdgeKind
{
    /// <summary>Project → File → Type → Member containment.</summary>
    Contains,
    /// <summary>Member invokes another member (or constructs a type).</summary>
    Calls,
    /// <summary>Type derives from base type.</summary>
    Inherits,
    /// <summary>Type implements interface.</summary>
    Implements,
    /// <summary>Method/property overrides a base member.</summary>
    Overrides,
    /// <summary>Project → Project reference.</summary>
    DependsOn,
    /// <summary>Subject → test method that exercises it (direct call from a test).</summary>
    TestedBy,
    /// <summary>Framework/runtime invokes a member entrypoint (e.g. Azure Functions trigger method).</summary>
    InvokedByFramework,
}

/// <summary>
/// A node in the code graph. <see cref="Id"/> is the Roslyn documentation-comment id
/// (e.g. <c>M:GymX.Api.Foo.Bar(System.String)</c>) so it is stable across runs and readable by a model.
/// </summary>
public sealed record GraphNode(
    string Id,
    NodeKind Kind,
    string Name,
    string FullName,
    string? Signature,
    string? FilePath,
    int StartLine,
    int EndLine,
    bool IsTest,
    TypeKind TypeKind = TypeKind.None,
    Accessibility Accessibility = Accessibility.None,
    bool IsStatic = false,
    bool IsAbstract = false,
    bool IsVirtual = false,
    bool IsSealed = false,
    bool IsAsync = false,
    bool IsOverride = false,
    MemberKind MemberKind = MemberKind.Ordinary);

public sealed record GraphEdge(string FromId, string ToId, EdgeKind Kind);

/// <summary>A node returned from a traversal, tagged with its distance from the origin.</summary>
public sealed record GraphHit(GraphNode Node, int Depth);

public sealed record SubGraphEdge(string FromId, string ToId, string Kind);
public sealed record SubGraphResult(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<SubGraphEdge> Edges);

public sealed record DeadCodeSummary(
    int TotalMethods,
    int DirectlyUncalled,
    int DiLikelyUsed,
    int FrameworkEntrypoints,
    int LikelyDead);

public sealed record DeadCodeCandidate(
    GraphNode Method,
    string Confidence,
    string Reason);
