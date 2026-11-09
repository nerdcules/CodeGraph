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
| `CODEGRAPH_INDEXER` | path of `codegraph-index` for `live_status start=true`, when it is not next to the server; see [Live daemon](#live-daemon) |
| `CODEGRAPH_EMBEDDINGS_URL`, `CODEGRAPH_EMBEDDINGS_MODEL`, `CODEGRAPH_EMBEDDINGS_KEY` | an OpenAI-compatible embeddings endpoint for semantic search; set the same values for `codegraph-index`. See [Semantic search](#semantic-search) |
| `CODEGRAPH_DAEMON=auto` | start the live daemon on the first tool call when none runs; it stops itself after 120 min unused |

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

When the [live daemon](#live-daemon) is running, the server asks it before every answer whether saves are
pending, waits for them to be indexed (up to 8 s), and answers from the refreshed graph; `STALE` then only
appears when no daemon is running.

## Tools

Traversal answers (`impact_of`, `callers_of`, `callees_of`, the type and project walks, `traverse`) list the
nearest 200 rows and, past that, the count at each depth, so a hub's tens of thousands of dependents do not
flood the context; `impact_of` lists up to 50 guarding tests. `path_between` returns shortest paths only.

All 35 tools are read-only and idempotent. Seven of them (`live_status`, `symbol_at`, `live_references`,
`diagnostics`, `check_change`, `rename_preview`, `data_flow`) answer from the live Roslyn solution and need the [daemon](#live-daemon).
`id` always means a node id from `find_symbol` or
`search_code`. Depth and limit arguments are clamped to the ranges shown. `Calls` edges cover
method calls, constructions, property, field and event accesses, and the members a `using`,
`foreach` or `await` calls without naming them (Dispose, GetEnumerator, GetAwaiter); the caller, test and
coverage walks treat a call to an interface or base member as a call to every implementation
and override.

### Finding things

| tool | arguments | answers |
|---|---|---|
| `find_symbol` | `query`, `kind?`, `limit` (1–100, 20), `typeKind?`, `accessibility?`, `isStatic?`, `isAbstract?`, `isVirtual?`, `isAsync?`, `isOverride?`, `memberKind?`, `nameMatch` (Contains / StartsWith / EndsWith / Exact), `offset` | Types and members by name, case-insensitive. Exact name matches first, then shorter names. |
| `search_code` | `query`, `kind?`, `file?`, `limit` (1–100, 20), `mode` (auto / lexical / semantic / hybrid) | "Where's the code that does X": BM25 full-text over names, signatures, comments, string literals and body identifiers, fused with vector similarity when the graph has [embeddings](#semantic-search). Each hit shows its rank by words and by meaning. |
| `outline` | `id` | Direct children, no bodies: members of a type, types in a file, files in a project. |
| `members_of` | `id`, `kind?`, `accessibility?`, `isStatic?`, `isAbstract?`, `isAsync?`, `isVirtual?`, `isOverride?`, `limit` (500), `offset` | Members of a type filtered by kind and modifiers. |
| `snippet` | `id`, `context` (0–50 extra lines) | The declaration span from source, line-numbered. Read this instead of the file. |
| `neighbors` | `id` | Every edge in and out of a node across all kinds. |

### Call graph and impact

| tool | arguments | answers |
|---|---|---|
| `callers_of` | `id`, `depth` (1–20, 1) | Who calls or reads this member, transitively. A call to an interface or base member counts for every implementation and override. |
| `callees_of` | `id`, `depth` (1–20, 1), `includeExternal` (false) | What this member calls; with `includeExternal`, the framework and NuGet members too. |
| `impact_of` | `id`, `depth` (1–20, 5) | Everything that could break: transitive callers, overriders, implementers, subclasses, and for a type every member that references it. Guarding tests are listed separately. Call before editing anything shared. |
| `path_between` | `from`, `to`, `edgeKinds` (Calls), `maxDepth` (1–20, 10), `maxPaths` (1–5, 3) | Shortest call chains from one method to another: up to `maxPaths`, all of the shortest length. |
| `most_called` | `limit` (1–100, 20), `minCallers` (1), `offset`, `includeExternal` (false), `nameFilter?` | Methods ranked by direct caller count. With `includeExternal` and e.g. `nameFilter: "System.IO"`, which framework APIs the solution leans on. |
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
| `dead_code_report` | `includePublicApi` (false), `limit` (1–500, 100), `offset` | Methods with no callers that also implement or override nothing that is called, and are not framework entry points. |
| `explain_reachability` | `id` | Why a method counts as reachable (callers, framework root, or the called interface/base member it implements or overrides), or why it is a candidate. |
| `list_roots` | `limit` (1–500, 100) | Framework-invoked entry points, e.g. Azure Functions triggers. |

### Projects

| tool | arguments | answers |
|---|---|---|
| `depends_on` | `id` (a project), `depth` (1–10, 5) | Projects this project references, transitively. |
| `dependents_of` | `id` (a project), `depth` (1–10, 5) | Projects that reference this one. |

### Live (from the Roslyn solution held by `codegraph-index --watch`)

| tool | arguments | answers |
|---|---|---|
| `live_status` | `start` (false) | Whether a daemon holds this solution: pid, projects, last index, pending saves. `start=true` launches one. |
| `symbol_at` | `file`, `line`, `column` | The symbol under a position: its graph id, the enclosing member, the type of the expression there. Metadata symbols are reported as not in the graph. |
| `live_references` | `symbol` (id or dotted name), `limit` (1–200, 50) | Every use of a symbol in the solution, including framework and NuGet members the graph does not hold ("who calls `File.Delete`"). Each row names the enclosing member and shows the line. |
| `diagnostics` | `file?`, `includeWarnings` (false), `limit` (1–100, 30) | Compiler errors (and warnings) for one file or the whole solution as it is on disk. |
| `check_change` | `file` + `text`, or `files` (`[{file, text}]`) | What-if compile: the proposed full text of one or more files is applied in memory, their projects and every dependent are compiled, and the new and fixed errors come back. Each declaration the edit removes, re-signs or re-implements is listed with its dependents and guarding tests from the graph. Nothing is written. |
| `data_flow` | `symbol` (a method id) | Where each parameter goes in the method body: passed to which call as which parameter, stored in which field or property, returned, captured, or deciding a branch; locals derived from a parameter are followed. One method deep. |
| `rename_preview` | `symbol`, `newName`, `overloads` (false), `limit` (200) | Roslyn's rename on a fork: every line that would change across the solution, through interfaces and overrides, and any compile errors the rename would cause. Nothing is written. |

### The server itself

| tool | arguments | answers |
|---|---|---|
| `graph_stats` | | Node and edge counts; confirms the graph loaded. |
| `index_status` | | Which graph, built when and at which commit, which indexed files changed or vanished since. |
| `usage_stats` | | Calls per tool, latency, answer size and estimated source tokens saved, this session and all time. |

Edge kinds, for `traverse` and `path_between`: `Calls`, `Inherits`, `Implements`, `Overrides`,
`Contains`, `DependsOn`, `TestedBy`, `InvokedByFramework`, `References`, `CallsExternal`.

Framework and NuGet members the solution calls or constructs are `external` nodes (no file), reached by
`CallsExternal` edges, so `find_symbol "File.Delete"` → `callers_of` / `impact_of` / `covered_by` answer
"who deletes files and which tests reach that" from the graph alone. Property reads on framework types
are not recorded; `live_references` covers those. The separate edge kind keeps every walk over the
solution's own call graph exactly as cheap as before.

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

## Semantic search

`search_code` is lexical by default: BM25 over the words in names, comments and bodies. With an embeddings
endpoint it also ranks by meaning, so "remove a cached entry" finds `Purge` even though no word matches.

```bash
export CODEGRAPH_EMBEDDINGS_URL=http://localhost:11434/v1/embeddings   # Ollama, after `ollama pull nomic-embed-text`
export CODEGRAPH_EMBEDDINGS_MODEL=nomic-embed-text
codegraph-index path/to/App.sln
```

Give the server the same two variables (in `.mcp.json` under `"env"`). Any OpenAI-compatible
`/v1/embeddings` endpoint works; `CODEGRAPH_EMBEDDINGS_KEY` is sent as a bearer token.

- **What is embedded.** Each type and member's name, container, doc comment, signature and the first
  400 characters of its body, capped at 1,200 characters. A remote endpoint therefore receives that text;
  the log line says so. With a local endpoint nothing leaves the machine.
- **Cost.** Vectors live in the graph file beside the full-text tables, one signed byte per component, and
  the server searches them in memory: 8 ms over 40,000 768-dimension vectors. An index pass embeds only
  declarations whose text changed (by hash), so a no-op re-index sends nothing. Changing the model, or
  turning embeddings on or off, rebuilds the graph once so vectors from different models never mix.
- **Ranking.** `mode=auto` is `hybrid` when the graph has vectors: the top 50 by BM25 and the top 50 by
  cosine similarity are merged by reciprocal rank fusion. `semantic` and `lexical` use one ranking only.
- **Failure.** An unreachable endpoint never fails an index or a search: indexing logs it and writes the
  graph without new vectors, and `search_code` answers lexically with a note.

## Live daemon

`codegraph-index <solution> --watch` is the daemon. Besides re-indexing on every save, it keeps the loaded
Roslyn solution and answers the seven live tools over a named pipe (a Unix domain socket on Linux and macOS, a
real pipe on Windows) whose name is derived from the graph's path, so no port or discovery file is involved and
one daemon serves every MCP server reading that graph. A request is one JSON line each way over a fresh
connection. The server probes for the daemon before every call by checking that the pipe exists, without
connecting (a connect to a missing pipe would wait out its timeout), so the probe costs microseconds when
none runs; a socket left by a killed daemon costs one 150 ms timeout and is then remembered for 3 s.

Start it in a terminal, or let the server start it: `live_status` with `start=true` launches `codegraph-index`
from next to the server executable (or `CODEGRAPH_INDEXER`), with `--log .codegraph/daemon.log` so nothing it
prints can touch the MCP protocol on stdio. With `CODEGRAPH_DAEMON=auto` in the server's environment the first
tool call does that by itself. The pipe answers from the start; during the solution load, which takes seconds
to a minute, `live_status` says "loading" and the graph tools answer from the last graph without waiting.

Lifecycle:

- **One per graph.** A second `--watch` on the same graph sees the first answering and exits.
- **Clean stop.** Ctrl+C, SIGTERM and SIGHUP stop it gracefully and take the pipe down. A daemon killed with
  SIGKILL leaves its socket file; the next daemon removes it, and meanwhile the server's probe spends one
  150 ms timeout on it and then remembers it as dead for 3 s.
- **Idle exit.** `--idle-exit <minutes>` stops it after that long with no save and no live request. The server
  pings before every tool call, so a session in use keeps its daemon. Daemons the server starts use 120 min.
- **Memory.** `live_status` shows the working set and managed heap; this repo's daemon uses about 220 MB.

The server falls back to graph-only answers with `STALE` marks whenever no daemon is running.

What it costs: the memory of a loaded Roslyn solution (hundreds of MB for a large one) in one process per
worktree. What it gives: positions, metadata symbols, diagnostics, the what-if compile, and answers that
reflect the working tree within one debounce of a save. [BENCHMARKS.md](BENCHMARKS.md) has the numbers.

## Concurrency

DuckDB allows one writer or many readers per file. Each tool call opens its own read-only connection.
Between calls the server keeps one extra read-only connection, the anchor, which keeps the database
instance loaded so a call's open costs about 1 ms instead of 11. The anchor blocks a writer, so it lets
go after 1.5 s without a call, after 10 s however busy the server is, and immediately when the daemon
reports pending saves. A full rebuild writes a new file and swaps it in; the server notices the new file
on its next call and reloads. `codegraph-index` (including `--watch`) therefore writes while Claude
sessions are open, waiting at most a couple of seconds for the anchor. The freshness check reloads its
file list whenever the database's mtime changes, regardless of its 3 s cache.

## Troubleshooting

| symptom | cause and fix |
|---|---|
| `no code graph at …` | Nothing indexed in this worktree yet. Run `codegraph-index <solution>` from inside it. |
| Every answer starts with `⚠ STALE INDEX` | Files changed since the index. Re-run `codegraph-index`, or leave `--watch` running. |
| `existing graph is from another solution or schema version; rebuilding` | Expected after upgrading codegraph or pointing the indexer at a different solution. The full rebuild runs once. |
| `this graph has no full-text index` | The graph predates `search_code`. Run `codegraph-index --full`. |
| `this needs the live daemon, which is not running` | Start `codegraph-index <solution> --watch`, or call `live_status` with `start=true`. |
| `live daemon: the solution is still loading` | The daemon is in its MSBuild load; retry in a few seconds. |
| Two solutions in one worktree | Give each its own graph with `codegraph-index … --db <path>` and pass that path (or `CODEGRAPH_DB`) to the server. |
| `You must install or update .NET to run this application` | The executables look for .NET in `DOTNET_ROOT` or the machine's default location, not on `PATH`; with a version-managed dotnet (mise, asdf) that location may lack ASP.NET Core. The install scripts detect this and add `"env": { "DOTNET_ROOT": … }` to the printed `.mcp.json`; set the same variable in your shell to run `codegraph-index` by hand. `codegraph-mcp --version` is a quick check. |
| The server will not start after an upgrade | A running `codegraph-mcp` held its files open during publish. Close Claude Code sessions and re-run the install script, or use `-Force` / `--force`. |
| Server logs | stderr. Claude Code shows them under the MCP server's output. |
