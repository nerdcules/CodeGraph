namespace CodeGraph.Contracts;

/// <summary>The first <c>limit</c> items of a list, how many there are in all, and whether some were left out.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, int Total, bool Truncated)
{
    public static Page<T> Of(IReadOnlyList<T> all, int limit)
    {
        var cap = Page.Clamp(limit);
        return all.Count <= cap ? new(all, all.Count, false) : new(all.Take(cap).ToArray(), all.Count, true);
    }
}

public static class Page
{
    public const int DefaultLimit = 25;

    /// <summary>A limit below 1 is 1; int.MaxValue means every item.</summary>
    public static int Clamp(int limit) => Math.Max(1, limit);
}

/// <summary>
/// A node of the graph. <see cref="Kind"/> is Project, File, Type, Method, Property, Field, Event or External;
/// <see cref="TypeKind"/> (types only) is Class, Struct, Record, RecordStruct, Interface, Enum, Delegate, …;
/// <see cref="Accessibility"/> is Public, Private, Protected, Internal, ProtectedOrInternal or ProtectedAndInternal.
/// <see cref="File"/> is relative to <see cref="ICodeGraph.Root"/>. <see cref="Project"/> is the name of the project
/// whose file declares it, when the graph knows one.
/// </summary>
public sealed record Symbol(
    string Id,
    string Kind,
    string Name,
    string FullName,
    string? Signature,
    string? File,
    int StartLine,
    int EndLine,
    bool IsTest,
    string? TypeKind,
    string? Accessibility,
    bool IsStatic,
    bool IsAbstract,
    bool IsVirtual,
    bool IsOverride,
    bool IsAsync,
    string Language,
    string? Project);

/// <summary>A symbol found by a walk, <see cref="Depth"/> hops from where it started.</summary>
public sealed record Hit(Symbol Symbol, int Depth);

/// <summary>What <see cref="ICodeGraph.FindAsync"/> matches. <see cref="Match"/> is Contains, StartsWith, EndsWith or Exact.</summary>
public sealed record SymbolQuery(string Text)
{
    /// <summary>Project, File, Type, Method, Property, Field, Event or External.</summary>
    public string? Kind { get; init; }

    public string Match { get; init; } = "Contains";

    /// <summary>Types only: Class, Struct, Record, RecordStruct, Interface, Enum, Delegate, …</summary>
    public string? TypeKind { get; init; }

    public string? Accessibility { get; init; }

    public bool? IsStatic { get; init; }

    public bool? IsAbstract { get; init; }

    public bool? IsVirtual { get; init; }

    public bool? IsAsync { get; init; }

    public bool? IsOverride { get; init; }

    /// <summary>Methods only: Ordinary, Constructor, Destructor or Operator.</summary>
    public string? MemberKind { get; init; }

    /// <summary>csharp, typescript, javascript or java, for a graph holding several.</summary>
    public string? Language { get; init; }

    public int Limit { get; init; } = Page.DefaultLimit;

    public int Offset { get; init; }
}

public sealed record Outline(Symbol Target, Page<Symbol> Children);

/// <summary>A walk from <see cref="Target"/>: callers or callees, nearest first.</summary>
public sealed record Traversal(Symbol Target, int Depth, Page<Hit> Hits);

/// <summary>What could break if <see cref="Target"/> changes: production <see cref="Dependents"/> and the <see cref="Tests"/> that reach it.</summary>
public sealed record Impact(Symbol Target, int Depth, Page<Hit> Dependents, Page<Hit> Tests);

public sealed record TestList(Symbol Target, Page<Symbol> Tests);

/// <summary>Source lines <see cref="StartLine"/>-<see cref="EndLine"/> of <see cref="File"/>; <see cref="Lines"/> is null when the file cannot be read.</summary>
public sealed record Snippet(Symbol Target, string? File, int StartLine, int EndLine, IReadOnlyList<string>? Lines);

/// <summary>
/// The rows of a query. Cells are string, long, double, bool or null. <see cref="Total"/> counts every row the
/// query returned; <see cref="Items"/> holds the first <c>limit</c>.
/// </summary>
public sealed record QueryResult(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Items, int Total, bool Truncated);

public sealed record Column(string Name, string Type);

public sealed record Table(string Name, IReadOnlyList<Column> Columns);

public sealed record Schema(IReadOnlyList<Table> Tables, IReadOnlyList<string> NodeKinds, IReadOnlyList<string> EdgeKinds, string Notes);

/// <summary>Which graph this is: its file, the folder paths are relative to, its size, and when and at which commit it was built.</summary>
public sealed record Status(
    string Db,
    string? Root,
    int Nodes,
    int Edges,
    string? SchemaVersion,
    string? IndexedAt,
    string? Commit,
    IReadOnlyList<string> Languages);

/// <summary>No node has this id and no symbol has this exact name.</summary>
public sealed class SymbolNotFoundException(string query)
    : Exception($"no node has the id '{query}' and no symbol has that exact name; use FindAsync with a partial name")
{
    public string Query { get; } = query;
}

/// <summary>Several symbols have this exact name; pass the id of one. <see cref="Candidates"/> holds up to 21 of them.</summary>
public sealed class AmbiguousSymbolException(string query, IReadOnlyList<Symbol> candidates)
    : Exception($"{(candidates.Count > 20 ? "more than 20" : candidates.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))} symbols are named {query}; pass the id of one: "
                + string.Join(", ", candidates.Take(20).Select(c => c.Id)))
{
    public string Query { get; } = query;

    public IReadOnlyList<Symbol> Candidates { get; } = candidates;
}
