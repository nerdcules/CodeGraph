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
# → /path/to/.codegraph/graph.db
```

Progress and warnings go to stderr. Multi-target projects (net10.0-android;net10.0-ios)
open as one project per TFM with the same name; nodes and edges dedupe, first one wins.

## Wire it into Claude Code

`.mcp.json` in the repo root (or `claude mcp add codegraph -- dotnet run --project ...`):

```json
{
  "mcpServers": {
    "codegraph": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/codegraph/src/CodeGraph.Mcp", "--", "/path/to/GymX/.codegraph/graph.db"]
    }
  }
}
```

For faster startup, `dotnet publish -c Release` once and point `command` at the
published `codegraph-mcp` binary instead of `dotnet run`.

Add `.codegraph/` to `.gitignore`.

## Tools exposed

| tool | what it answers |
|---|---|
| `find_symbol` | id, kind, signature, location for a name |
| `outline` | children of a node, no bodies |
| `callers_of` / `callees_of` | call graph, N hops |
| `impact_of` | transitive dependents + guarding tests |
| `tests_for` | tests that call a member directly |
| `snippet` | just the declaration span from source |
| `graph_stats` | sanity check |

A typical Claude turn: `find_symbol` → `impact_of` → `snippet` on the two or three
things that matter. Low thousands of tokens instead of tens of thousands.

Indexing this repo is the quickest end-to-end check: `tests_for` on `GraphStore.Impact`
should come back with the tests in `tests/CodeGraph.Tests`.

## Not yet

- Incremental re-index (file hash → re-walk one compilation unit). Whole-solution
  re-index is fine at GymX scale; revisit when it isn't.
- `References` edges (types used in signatures/fields).
- FTS5 / embeddings for "where's the code that does X" style search.
- Telemetry (query counts, tokens saved) — the software-factory feedback loop.
