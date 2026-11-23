namespace CodeGraph.Contracts;

/// <summary>
/// The code graph as a typed API: one method per operation. The MCP server's find_symbol, outline, callers_of,
/// impact_of, tests_for and snippet tools, agent-written C# scripts (<c>dotnet run script.cs</c>) and the
/// <c>codegraph</c> CLI all call the same implementation, <c>CodeGraph.Client.CodeGraphClient</c>.
/// <para>
/// A <c>symbol</c> argument is a node id (<c>M:Ns.Type.Method(System.String)</c>) or an exact name that one symbol
/// has (<c>Save</c>, or a qualified tail such as <c>Service.Save</c>). A name that no symbol has throws
/// <see cref="SymbolNotFoundException"/>; one that several have throws <see cref="AmbiguousSymbolException"/> with
/// the candidates. Lists are <see cref="Page{T}"/>: the first <c>limit</c> items (default 25), the total, and
/// whether items were left out.
/// </para>
/// </summary>
public interface ICodeGraph : IDisposable
{
    /// <summary>The folder that <see cref="Symbol.File"/> paths are relative to; null when paths are absolute.</summary>
    string? Root { get; }

    /// <summary>The symbol with exactly this id, or null. Names are not resolved.</summary>
    Task<Symbol?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Types and members whose name or full name matches, exact names first, then shorter names.</summary>
    Task<Page<Symbol>> FindAsync(SymbolQuery query, CancellationToken cancellationToken = default);

    /// <summary>The direct children of a node: members of a type, types in a file, files in a project.</summary>
    Task<Outline> OutlineAsync(string symbol, int limit = Page.DefaultLimit, CancellationToken cancellationToken = default);

    /// <summary>The source lines of one declaration, with <paramref name="context"/> extra lines above and below (0-50).</summary>
    Task<Snippet> SnippetAsync(string symbol, int context = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Who calls or reads this member, nearest first, up to <paramref name="depth"/> hops (1-20). A call to an
    /// interface or base member counts as a call to every implementation and override.
    /// </summary>
    Task<Traversal> CallersAsync(string symbol, int depth = 1, int limit = Page.DefaultLimit, CancellationToken cancellationToken = default);

    /// <summary>What this member calls, nearest first, up to <paramref name="depth"/> hops (1-20); framework members only with <paramref name="includeExternal"/>.</summary>
    Task<Traversal> CalleesAsync(string symbol, int depth = 1, bool includeExternal = false, int limit = Page.DefaultLimit, CancellationToken cancellationToken = default);

    /// <summary>
    /// What could break if this changes (transitive callers, overriders, implementers, subclasses, and for a type
    /// every member that references it), split into production dependents and the tests that reach it, up to
    /// <paramref name="depth"/> hops (1-20). Test reach is static: a run may execute fewer.
    /// </summary>
    Task<Impact> ImpactAsync(string symbol, int depth = 5, int limit = Page.DefaultLimit, CancellationToken cancellationToken = default);

    /// <summary>Test methods that call or read this member directly, including through an interface or base member it implements or overrides.</summary>
    Task<TestList> TestsAsync(string symbol, int limit = Page.DefaultLimit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs one read-only SELECT (or WITH … SELECT) against the graph's tables (see <see cref="SchemaAsync"/>) on a
    /// read-only connection that cannot read or write other files. Returns at most <paramref name="limit"/> rows
    /// (1-10,000) and gives up after <paramref name="timeoutSeconds"/> (1-60).
    /// </summary>
    Task<QueryResult> QueryAsync(string sql, int limit = Page.DefaultLimit, int timeoutSeconds = 10, CancellationToken cancellationToken = default);

    /// <summary>The graph's tables and columns, and the node and edge kinds they hold.</summary>
    Task<Schema> SchemaAsync(CancellationToken cancellationToken = default);

    /// <summary>Which graph this reads, its size, and when and at which commit it was built.</summary>
    Task<Status> StatusAsync(CancellationToken cancellationToken = default);
}

/// <summary>Shorthands over <see cref="ICodeGraph"/> for scripts.</summary>
public static class CodeGraphExtensions
{
    /// <summary><see cref="ICodeGraph.FindAsync"/> with the common options.</summary>
    public static Task<Page<Symbol>> FindAsync(
        this ICodeGraph graph, string text, string? kind = null, string match = "Contains", int limit = Page.DefaultLimit,
        CancellationToken cancellationToken = default) =>
        graph.FindAsync(new SymbolQuery(text) { Kind = kind, Match = match, Limit = limit }, cancellationToken);
}
