# CodeGraph lite: answer with a C# script

Write a C# file in `{{SCRATCH}}` and run it with `dotnet run {{SCRATCH}}/name.cs -- args`. It calls the code graph's typed API (no Roslyn, read-only).

1. **Read-only.** Write only in `{{SCRATCH}}`; never write to the repository being analysed.
2. **Filter in code.** Pass `limit: int.MaxValue` to see everything; group, count and filter in C#, not in your context.
3. **Print at most 40 lines**: the summary that answers the question, with full names and `File:line` where useful.

```csharp
#:project {{CLIENT_PROJECT}}
#:property PublishAot=false
using CodeGraph.Client;
using CodeGraph.Contracts;
using var graph = CodeGraphClient.Open();   // the graph is already configured
var impact = await graph.ImpactAsync("Type.Member", depth: 5, limit: int.MaxValue);
Console.WriteLine($"{impact.Dependents.Total} dependents, {impact.Tests.Total} tests");
```

`symbol` is an id (`M:Ns.Type.Method(System.Guid)`) or an exact name (`Method`, `Type.Method`); a name several symbols have throws `AmbiguousSymbolException` (`.Candidates`), none throws `SymbolNotFoundException`. Lists are `Page<T>(Items, Total, Truncated)`, default limit 25.

```csharp
Task<Symbol?> GetAsync(string id);                              // exact id only
Task<Page<Symbol>> FindAsync(string text, string? kind = null, string match = "Contains", int limit = 25);
Task<Page<Symbol>> FindAsync(SymbolQuery q);  // q: Text, Kind, Match, TypeKind, Accessibility, IsStatic, IsAbstract, IsVirtual, IsAsync, IsOverride, MemberKind, Limit, Offset
Task<Outline> OutlineAsync(string symbol, int limit = 25);      // Outline(Target, Page<Symbol> Children)
Task<Snippet> SnippetAsync(string symbol, int context = 0);     // Snippet(Target, File, StartLine, EndLine, Lines)
Task<Traversal> CallersAsync(string symbol, int depth = 1, int limit = 25);  // Traversal(Target, Depth, Page<Hit> Hits)
Task<Traversal> CalleesAsync(string symbol, int depth = 1, bool includeExternal = false, int limit = 25);
Task<Impact> ImpactAsync(string symbol, int depth = 5, int limit = 25);      // Impact(Target, Depth, Page<Hit> Dependents, Page<Hit> Tests)
Task<TestList> TestsAsync(string symbol, int limit = 25);       // TestList(Target, Page<Symbol> Tests): direct callers that are tests
Task<QueryResult> QueryAsync(string sql, int limit = 25, int timeoutSeconds = 10);  // one SELECT; QueryResult(Columns, Items: object?[] rows, Total, Truncated)
Task<Schema> SchemaAsync();  Task<Status> StatusAsync();
record Hit(Symbol Symbol, int Depth);
record Symbol(string Id, string Kind, string Name, string FullName, string? Signature, string? File, int StartLine, int EndLine,
  bool IsTest, string? TypeKind, string? Accessibility, bool IsStatic, bool IsAbstract, bool IsVirtual, bool IsOverride, bool IsAsync,
  string Language, string? Project);
```

Kinds: Project, File, Type, Method, Property, Field, Event, External. SQL tables: `nodes(id, kind, name, full_name, signature, file_path, start_line, end_line, is_test, type_kind, accessibility, is_static, is_abstract, is_virtual, is_sealed, is_async, is_override, member_kind, language)` and `edges(from_id, to_id, kind)`; edge kinds Contains (Project→File→Type→member), Calls (caller→callee), Implements/Inherits (type→base), Overrides (override→base member), TestedBy (member→test), DependsOn (project→project), References.
