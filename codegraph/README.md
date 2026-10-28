# CodeGraph

Roslyn-based code graph indexer plus an MCP server, so Claude Code can answer
"who calls this / what breaks if I change it / which tests guard it" from a small
graph query instead of re-reading source files.

```
src/CodeGraph.Core      model + SQLite store + traversals
src/CodeGraph.Indexer   codegraph-index: Roslyn extractor → graph.db
src/CodeGraph.Mcp       codegraph-mcp: stdio MCP server over graph.db
tests/CodeGraph.Tests   store traversals + indexer extraction
```

## Why use the Code-Graph MCP?

Without a graph, Claude answers structural questions by reading source. "Who calls
`SaveAsync`?" becomes a grep for the name, a read of every file that matched, and a guess
about which matches are the one you mean. That is slow, it is expensive in context, and it
is wrong in the ways that matter most in C#: it finds unrelated methods with the same name,
misses calls that go through an interface or a base class, and never finds the indirect
implementor two levels down.

The graph fixes the accuracy problem first. The indexer runs the real Roslyn compiler over
the solution, so every edge is a resolved symbol, not a text match. `impact_of` walks
callers, overrides and implementations transitively; `implementors_of` follows `Inherits`
as well as `Implements`; `references_to` sees a type in signatures, generic arguments,
casts and attributes. Claude gets the same answer the compiler would give.

It also fixes the cost problem. A typical turn is `find_symbol` → `impact_of` → `snippet`
on the two or three declarations that matter, which is low thousands of tokens instead of
the tens of thousands spent opening files. Context stays free for the actual change, and
long sessions degrade less.

Beyond navigation, the graph answers questions that are impractical from source at all:
which tests guard a member (`tests_for`), what is unreachable from any entry point
(`dead_code_report`, `uncovered`), the shortest call path between two methods
(`path_between`), and which members are called the most (`most_called`).

The practical properties that make it usable day to day:

- **Trustworthy when stale.** Every answer is checked against the working tree first. If
  indexed files changed, the answer says so and marks the affected rows, so Claude never
  silently reasons from an old graph.
- **Cheap to keep fresh.** Re-indexing is incremental: only changed files and their
  dependents are re-walked, and it runs safely while Claude sessions are open.
- **One config for every worktree.** The server discovers `.codegraph/graph.db` from its
  working directory, so a single `.mcp.json` serves every checkout, each with its own graph.
- **Read-only and local.** The server only ever reads the graph file. No source leaves the
  machine and nothing is written during a query.

## Build

```bash
dotnet build -warnaserror
```

`Directory.Build.props` pins `RoslynVersion`; the other versions live in each `.csproj`.
`TreatWarningsAsErrors` also promotes NuGet audit warnings, so a package picking up a
published advisory fails the build until it is bumped — that is deliberate.

`CodeGraph.Indexer` references `Microsoft.Build.Framework` with `ExcludeAssets="runtime"`
and `PrivateAssets="all"`. MSBuildLocator loads MSBuild from the installed SDK, and shipping
the package copy alongside it breaks assembly loading at runtime; Build.Locator enforces
this as error MSBL001.

## Test

```bash
dotnet test
```

`global.json` selects the Microsoft.Testing.Platform runner, which .NET 10 requires for
xunit.v3. The suite needs no database, no SDK discovery and no network: the store tests run
against a throwaway SQLite file, and the indexer tests drive the real extractor over an
`AdhocWorkspace`, so calls resolve through a genuine semantic model without MSBuild.

## Index a solution

```bash
dotnet run --project src/CodeGraph.Indexer -- /path/to/GymX.sln
# → <git worktree root>/.codegraph/graph.db   (solution dir when not in a repo; override with --db)
```

Progress and warnings go to stderr. Multi-target projects (net10.0-android;net10.0-ios)
open as one project per TFM with the same name; nodes and edges dedupe, first one wins.

### Incremental re-indexing

Re-running the indexer is incremental. Each file's SHA-256 is stored, and only files whose
hash changed are re-walked, plus their **dependents**: files with an edge into a node declared
in a changed file (callers, subclasses, referencers, tests), and the other parts of any partial
type. Every edge records the file that produced it (`edge_origins`), so one file's contribution
can be removed without touching edges that another file still produces. Projects with no
re-walked files skip compilation entirely. `--full` rebuilds from scratch, and so does a
schema-version or solution mismatch.

The solution load (MSBuild evaluation) is paid every run, so a no-op re-index of this repo
takes about 9s against about 50s for a full one. An edit to a widely used file still compiles
every project that depends on it. Not covered: a *new* overload or extension method in a file
that existing callers never referenced can change what an untouched file binds to. Run `--full`
after big refactors.

The incremental contract is tested directly: v1 full, then an edit and an incremental run,
must equal v2 full (`IncrementalIndexTests`).

DuckDB allows one writer *or* many readers per file. The indexer only takes the write lock
for the final swap, which is one transaction, and the MCP server opens the graph read-only
per tool call. So re-indexing works while Claude sessions are running, and each side
retries briefly if it hits the other's lock.

## Wire it into Claude Code

`dotnet publish -c Release` once, then put one `.mcp.json` in the repo. Leave out the db
path: the server looks for `.codegraph/graph.db` from its working directory up to the git
worktree root. Claude Code starts it in the project directory, so the same file works in
every worktree (`C:/dev/sa-1`, `C:/dev/sa-2`, …), each reading its own graph:

```json
{
  "mcpServers": {
    "codegraph": {
      "command": "C:/tools/codegraph/codegraph-mcp.exe"
    }
  }
}
```

A worktree that hasn't been indexed yet still starts. Its tools answer "no code graph at …,
build it with codegraph-index". An explicit path argument or `CODEGRAPH_DB` still wins over
discovery. Two solutions in one worktree need `--db` to keep separate graphs.

Add `.codegraph/` to `.gitignore`.

### Staleness

The graph stores the commit it was built at and every file's hash. Before each answer the
server checks the working tree; this is cached for 3s and costs one `stat` per indexed file,
with a re-hash only when size or mtime changed. If anything moved, the answer starts with a
line like:

```
⚠ STALE INDEX: 2 indexed file(s) edited since (ChainRunner.cs, RouteExecutor.cs). Rows marked
STALE come from changed files, and new files are missing. Refresh with: codegraph-index "…/App.sln"
```

and every result row from an edited file ends in `STALE`. A moved HEAD is reported too, since
files added by a checkout aren't in the graph. `index_status` shows the full picture.

## Tools exposed

| tool | what it answers |
|---|---|
| `find_symbol` | id, kind, signature, location for a name |
| `outline` | children of a node, no bodies |
| `callers_of` / `callees_of` | call graph, N hops |
| `impact_of` | transitive dependents + guarding tests (for a type: every member that references it, too) |
| `tests_for` | tests that call a member directly |
| `references_to` | where a type is used: signatures, fields, locals, generic args, casts, attributes, record parameters |
| `implementors_of` / `inheritors_of` | who implements an interface (including subclasses of implementors) / derives from a type |
| `snippet` | just the declaration span from source |
| `graph_stats` | sanity check |
| `index_status` | which graph, built when and at which commit, which files changed since |

A typical Claude turn: `find_symbol` → `impact_of` → `snippet` on the two or three
things that matter. Low thousands of tokens instead of tens of thousands.
[PROMPTS.md](PROMPTS.md) has example prompts and the tools each one uses.

Indexing this repo is the quickest end-to-end check: `tests_for` on `GraphStore.Impact`
should come back with the tests in `tests/CodeGraph.Tests`.

## Not yet

- Skipping the MSBuild solution load on re-index (the remaining fixed cost), e.g. a
  long-running indexer that watches the worktree.
- `References` from `var`-inferred types (`var x = GetFoo()` names no type) and through
  `using` aliases.
- FTS5 / embeddings for "where's the code that does X" style search.
- Telemetry (query counts, tokens saved) — the software-factory feedback loop.
