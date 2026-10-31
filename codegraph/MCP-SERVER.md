<p align="center"><img src="assets/logo.svg" width="96" alt="CodeGraph"></p>

# MCP Server: codegraph

A read-only [Model Context Protocol](https://modelcontextprotocol.io) server that answers structural
questions about a C# solution from a compiler-built graph: who calls this, what breaks if it changes,
which tests guard it, where is the code that does X. Built for Claude Code; works with any MCP client
that speaks stdio.

| | |
|---|---|
| **Name** | `codegraph` (title "CodeGraph") |
| **Version** | from `Directory.Build.props`; shown by clients as the server version |
| **Transport** | stdio (JSON-RPC over stdin/stdout; logs go to stderr) |
| **Executable** | `codegraph-mcp` (`codegraph-mcp.exe` on Windows) |
| **Data** | `.codegraph/graph.db`, a DuckDB file built by `codegraph-index` |
| **Side effects** | none: every tool is read-only and idempotent; the server never writes the graph |
| **Network** | none; nothing leaves the machine |
| **Runtime** | .NET 10 |
| **Source** | [src/CodeGraph.Mcp](src/CodeGraph.Mcp) |

## Install

```powershell
.\install.ps1          # Windows → C:\tools\codegraph
```

```bash
./install.sh           # macOS / Linux → ~/.codegraph/bin
```

The script builds, runs the tests, publishes `codegraph-index` and `codegraph-mcp`, and prints
the configuration below with the real path. See the [README](README.md#install) for options.

`uninstall.ps1` / `uninstall.sh` reverses the install: folder, PATH entry, and with `-Repo` /
`--repo` a repo's `.mcp.json` entry and `.codegraph/` graph.

From a source checkout, `setup.ps1 -Solution <path>` / `setup.sh --solution <path>` pulls the
latest CodeGraph on request, builds, writes the `.mcp.json` entry as `dotnet run --project
<checkout>/src/CodeGraph.Mcp -c Release --no-build`, and indexes with `dotnet run`. See the
[README](README.md#from-a-source-checkout).

## Configure

Put one `.mcp.json` at the repo root. Leave out the database path: the server finds
`.codegraph/graph.db` from its working directory up to the git worktree root, so the same file
serves every worktree of the repo, each reading its own graph.

```json
{
  "mcpServers": {
    "codegraph": {
      "command": "C:/tools/codegraph/codegraph-mcp.exe"
    }
  }
}
```

Then build the graph once from inside the repo, and keep it fresh while you work:

```bash
codegraph-index path/to/App.sln            # full the first time, incremental after
codegraph-index path/to/App.sln --watch    # re-index on every save, no MSBuild reload
```

Add `.codegraph/` to `.gitignore`. A repo that has not been indexed still starts the server;
every tool answers `no code graph at …, build it with codegraph-index` until one exists.

### Command line

```
codegraph-mcp [path-to-graph.db] [--web PORT]
```

| | |
|---|---|
| `path-to-graph.db` | explicit graph; otherwise discovered from the working directory |
| `CODEGRAPH_DB` | environment alternative to the positional path |
| `--web PORT` | also serve the graph visualiser on `http://localhost:PORT` |
| `CODEGRAPH_TELEMETRY=off` | keep usage counts in memory only; see [Telemetry](#telemetry) |

### What clients show for the server

The `initialize` response carries everything a client's server page can display, so VS Code's
"MCP Server: codegraph" page and Claude Code's `/mcp` listing need no gallery entry:

| field | value |
|---|---|
| `serverInfo.name` / `title` | `codegraph` / CodeGraph |
| `serverInfo.version` | `<Version>` from `Directory.Build.props` |
| `serverInfo.description` | one-paragraph summary of what the graph answers |
| `serverInfo.websiteUrl` | the GitHub repository |
| `serverInfo.icons` | [assets/logo.svg](assets/logo.svg), embedded in the executable and sent as a data URI |
| `instructions` | a short guide for the model: typical turn, ids are passed back verbatim, what `STALE` means |

Which of these a given client renders is up to the client; VS Code adds support for newer
fields such as icons as it tracks the protocol.

## How answers look

Every tool returns one compact text block. Node rows share one shape:

```
kind  full name  [extra]  file:start-end  [id]  [STALE]
```

```
method  GymX.Core.WorkoutService.SaveAsync(Workout, CancellationToken)  depth 1  C:\dev\GymX\src\GymX.Core\WorkoutService.cs:41-67  [M:GymX.Core.WorkoutService.SaveAsync(GymX.Core.Workout,System.Threading.CancellationToken)]
```

The `[id]` is the Roslyn documentation-comment id. Pass it back verbatim to any tool that takes
an `id`. Ids are stable across runs and readable: `T:` type, `M:` method, `P:` property, `F:`
field, `E:` event, `F:<path>` file, `P:<name>` project.

### Staleness

Before each answer the server compares the graph with the working tree (cached for 3s, one
`stat` per indexed file, a re-hash only when size or mtime moved). If anything differs, the
answer starts with a warning and rows from edited files end in `STALE`:

```
⚠ STALE INDEX: 2 indexed file(s) edited since (ChainRunner.cs, RouteExecutor.cs). Rows marked
STALE come from changed files, and new files are missing. Refresh with: codegraph-index "…/App.sln"
```

A moved `HEAD` is reported too. `index_status` shows the full picture.

## Tools

All 28 tools are read-only and idempotent. `id` always means a node id from `find_symbol` or
`search_code`. Depth and limit arguments are clamped to the ranges shown. `Calls` edges cover
method calls, constructions, and property, field and event accesses; the caller, test and
coverage walks treat a call to an interface or base member as a call to every implementation
and override.

### Finding things

| tool | arguments | answers |
|---|---|---|
| `find_symbol` | `query`, `kind?`, `limit` (1–100, 20), `typeKind?`, `accessibility?`, `isStatic?`, `isAbstract?`, `isVirtual?`, `isAsync?`, `isOverride?`, `memberKind?`, `nameMatch` (Contains / StartsWith / EndsWith / Exact), `offset` | Types and members by name, case-insensitive. Exact name matches first, then shorter names. |
| `search_code` | `query`, `kind?`, `file?`, `limit` (1–100, 20) | "Where's the code that does X": BM25 full-text over names, signatures, comments, string literals and body identifiers. Each hit shows how many query terms it matched. |
| `outline` | `id` | Direct children, no bodies: members of a type, types in a file, files in a project. |
| `members_of` | `id`, `kind?`, `accessibility?`, `isStatic?`, `isAbstract?`, `isAsync?`, `isVirtual?`, `isOverride?`, `limit` (500), `offset` | Members of a type filtered by kind and modifiers. |
| `snippet` | `id`, `context` (0–50 extra lines) | The declaration span from source, line-numbered. Read this instead of the file. |
| `neighbors` | `id` | Every edge in and out of a node across all kinds. |

### Call graph and impact

| tool | arguments | answers |
|---|---|---|
| `callers_of` | `id`, `depth` (1–20, 1) | Who calls or reads this member, transitively. A call to an interface or base member counts for every implementation and override. |
| `callees_of` | `id`, `depth` (1–20, 1) | What this member calls (source only, not framework or NuGet). |
| `impact_of` | `id`, `depth` (1–20, 5) | Everything that could break: transitive callers, overriders, implementers, subclasses, and for a type every member that references it. Guarding tests are listed separately. Call before editing anything shared. |
| `path_between` | `from`, `to`, `edgeKinds` (Calls), `maxDepth` (1–20, 10), `maxPaths` (1–5, 3) | Shortest call chains from one method to another. |
| `most_called` | `limit` (1–100, 20), `minCallers` (1), `offset` | Methods ranked by direct caller count. |
| `traverse` | `id`, `edgeKind`, `direction` (inbound / outbound), `depth` (1–10, 1) | Generic walk over one edge kind when no named tool fits. |

### Types

| tool | arguments | answers |
|---|---|---|
| `references_to` | `id` (a type), `limit` (1–1000, 200) | Where a type is used: signatures, fields, locals (including `var`-inferred), generic arguments, casts, attributes, record parameters, `using` aliases. |
| `implementors_of` | `id`, `depth` (1–10, 3) | Who implements an interface, including subclasses of implementors. |
| `inheritors_of` | `id`, `depth` (1–10, 3) | Who derives from a type. |
| `overrides_of` | `id`, `depth` (1–10, 3) | Who overrides a member. |

### Tests and coverage

| tool | arguments | answers |
|---|---|---|
| `tests_for` | `id` | Test methods that call or read this member directly, including through an interface or base member it implements. |
| `covered_by` | `id`, `depth` (1–30, 10) | Tests that reach this member transitively, following interface and virtual dispatch, with hop count. |
| `coverage_stats` | | Share of production methods reachable from any test through the call graph. Static reachability, not line coverage. |
| `uncovered` | `file?`, `limit` (1–500, 100), `offset` | Production methods no test reaches. |

### Dead code and entry points

| tool | arguments | answers |
|---|---|---|
| `dead_code_report` | `includePublicApi` (false), `limit` (1–500, 100), `offset` | Methods with no callers, adjusted for interface dispatch and framework entry points. |
| `explain_reachability` | `id` | Why a method counts as reachable, or why it is a candidate. |
| `list_roots` | `limit` (1–500, 100) | Framework-invoked entry points, e.g. Azure Functions triggers. |

### Projects

| tool | arguments | answers |
|---|---|---|
| `depends_on` | `id` (a project), `depth` (1–10, 5) | Projects this project references, transitively. |
| `dependents_of` | `id` (a project), `depth` (1–10, 5) | Projects that reference this one. |

### The server itself

| tool | arguments | answers |
|---|---|---|
| `graph_stats` | | Node and edge counts; confirms the graph loaded. |
| `index_status` | | Which graph, built when and at which commit, which indexed files changed or vanished since. |
| `usage_stats` | | Calls per tool, latency, answer size and estimated source tokens saved, this session and all time. |

Edge kinds, for `traverse` and `path_between`: `Calls`, `Inherits`, `Implements`, `Overrides`,
`Contains`, `DependsOn`, `TestedBy`, `InvokedByFramework`, `References`.

## A typical turn

```
find_symbol  "SaveAsync"                  → one row, the id
impact_of    M:GymX.Core.WorkoutService.SaveAsync(…)   → 6 dependents, 3 guarding tests
snippet      M:GymX.Api.SessionsController.Complete(…)  → the one caller that needs a change
```

Three calls and low thousands of tokens, against opening every file that mentions `SaveAsync`.
[PROMPTS.md](PROMPTS.md) has prompts for each tool and when Claude reaches for it.

## Telemetry

Each call is appended as one JSON line to `.codegraph/telemetry.jsonl` beside the graph:

```json
{"at":"2026-10-29T19:56:39Z","tool":"impact_of","ms":25,"result_chars":2717,"source_chars":129653,"error":false}
```

`source_chars` is the total size of the files the answer pointed at, which is what reading
them instead would have cost. `usage_stats` turns the file into per-tool totals. The file
never leaves the machine; `CODEGRAPH_TELEMETRY=off` disables it.

## Concurrency

DuckDB allows one writer or many readers per file. The server opens the graph read-only for
the duration of each tool call and closes it again, so `codegraph-index` (including `--watch`)
can swap in a new graph while Claude sessions are open. Each side retries briefly if it meets
the other's lock. The freshness check reloads its file list when the database's mtime changes.

## Troubleshooting

| symptom | cause and fix |
|---|---|
| `no code graph at …` | Nothing indexed in this worktree yet. Run `codegraph-index <solution>` from inside it. |
| Every answer starts with `⚠ STALE INDEX` | Files changed since the index. Re-run `codegraph-index`, or leave `--watch` running. |
| `existing graph is from another solution or schema version; rebuilding` | Expected after upgrading codegraph or pointing the indexer at a different solution. The full rebuild runs once. |
| `this graph has no full-text index` | The graph predates `search_code`. Run `codegraph-index --full`. |
| Two solutions in one worktree | Give each its own graph with `codegraph-index … --db <path>` and pass that path (or `CODEGRAPH_DB`) to the server. |
| The server will not start after an upgrade | A running `codegraph-mcp` held its files open during publish. Close Claude Code sessions and re-run the install script, or use `-Force` / `--force`. |
| Server logs | stderr. Claude Code shows them under the MCP server's output. |
