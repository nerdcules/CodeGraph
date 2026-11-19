<p align="center"><img src="assets/logo.svg" width="96" alt="CodeGraph"></p>

# MCP Server: codegraph

[CodeGraph](README.md) · [Docs](docs/README.md) · **MCP server** · [Extractors](EXTRACTORS.md) · [Benchmarks](BENCHMARKS.md) · [Prompts](PROMPTS.md) · [Changelog](CHANGELOG.md) · [Claude Code plugin](plugin/README.md) · [VS Code extension](vscode-extension/README.md)

A read-only [Model Context Protocol](https://modelcontextprotocol.io) server that answers structural
questions about a codebase from a compiler-built graph: who calls this, what breaks if it changes,
which tests guard it, where is the code that does X. It works with any MCP client that speaks stdio;
`codegraph-index init --client` writes the config for Claude Code, GitHub Copilot (CLI, VS Code, Visual
Studio, JetBrains), Codex, Cursor, the Gemini CLI, Roo Code, Kilo Code and Continue.

| | |
|---|---|
| **Name** | `codegraph` (title "CodeGraph") |
| **Version** | from `Directory.Build.props`; shown by clients as the server version |
| **Transport** | stdio (JSON-RPC over stdin/stdout; logs go to stderr) |
| **Executable** | `codegraph-mcp` (`codegraph-mcp.exe` on Windows) |
| **Data** | `.codegraph/graph.db`, a DuckDB file built by `codegraph-index` |
| **Side effects** | every tool is read-only and idempotent, and the server never writes the graph or your files; it appends one line per call to `.codegraph/telemetry.jsonl` ([Telemetry](#telemetry)), and `live_status start=true` or `CODEGRAPH_DAEMON=auto` starts `codegraph-index --watch` ([Live daemon](#live-daemon)) |
| **Network** | none by default. With `CODEGRAPH_EMBEDDINGS_URL` set, `search_code` sends each query to that endpoint ([Semantic search](#semantic-search)); `--web` serves on `localhost` |
| **Runtime** | .NET 10, bundled in the release archive |
| **Source** | [src/CodeGraph.Mcp](src/CodeGraph.Mcp) |

## Install

```bash
curl -fsSL https://raw.githubusercontent.com/nerdcules/CodeGraph/main/codegraph/install.sh | bash -s -- ~/dev/App     # macOS / Linux → ~/.codegraph/bin
```

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/nerdcules/CodeGraph/main/codegraph/install.ps1))) C:\dev\App    # Windows → C:\tools\codegraph
```

The script downloads the release archive for the platform (the executables carry their own .NET
runtime), verifies its checksum, and runs `codegraph-index init` on the repo: that writes the
configuration below with the real path, adds `.codegraph/` to `.gitignore`, and builds the first
graph. In a terminal it asks for what the command leaves out (repos, assistants, PATH, the VS Code
extension) and shows the plan first; `--yes` / `-Yes` asks nothing. Set up another repo later with
`codegraph-index init` from inside it. See
[docs/installation.md](docs/installation.md) for options, the Claude Code plugin, and building
from source (`--from-source`).

`uninstall.ps1` / `uninstall.sh` reverses the install: folder, PATH entry, and with `-Repo` /
`--repo` a repo's MCP config entries and `.codegraph/` graph, Codex's global entry, and the VS Code extension.

Languages: C# through Roslyn, TypeScript and Angular through the bundled Node extractor (needs Node.js), and
Java, Kotlin, Scala, Python, Go and Rust through their SCIP indexers. [EXTRACTORS.md](EXTRACTORS.md) lists
what each records and which live tools answer for it. `find_symbol`, `search_code` and `most_called` take a
`language` filter when one graph holds several.

## Configure

`codegraph-index init` writes this. By hand, put one `.mcp.json` at the repo root. Leave out the database path: the server finds
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

Other clients read other files. `codegraph-index init --client <names>` writes each one
([installation](docs/installation.md#use-another-assistant)):

| Client | File | Key | The project folder reaches the server through |
|---|---|---|---|
| Claude Code, the Copilot CLI | `.mcp.json` | `mcpServers` | the working directory |
| Copilot in VS Code | `.vscode/mcp.json` | `servers`, with `"type": "stdio"` | `--root ${workspaceFolder}` |
| Copilot in Visual Studio | `.vs/mcp.json` beside the solution | `servers`, with `"type": "stdio"` | `--root <absolute worktree root>` |
| Copilot for JetBrains | `.github/mcp.json` | `servers`, with `"type": "stdio"` | `--root <absolute worktree root>` |
| Cursor | `.cursor/mcp.json` | `mcpServers` | `--root ${workspaceFolder}` (IDE), the working directory (CLI) |
| Gemini CLI | `.gemini/settings.json` | `mcpServers` | the working directory |
| Codex | `~/.codex/config.toml`, global | `mcp_servers` | the working directory |
| Roo Code | `.roo/mcp.json` | `mcpServers` | the working directory (the first workspace folder) |
| Kilo Code | `.kilo/kilo.json` | `mcp`, with `"type": "local"`, one `command` array, `environment` | the working directory |
| Continue | `.continue/mcpServers/codegraph.json` | `mcpServers` | the working directory (the first workspace folder) |

Add `.codegraph/` to `.gitignore`. A repo that has not been indexed still starts the server;
every tool answers `no code graph at <path>. Build it from this worktree with: codegraph-index <solution.sln>` until one exists.

### Command line

```
codegraph-mcp [path-to-graph.db] [--root DIR] [--web PORT]
```

| | |
|---|---|
| `path-to-graph.db` | explicit graph; otherwise discovered from the working directory |
| `CODEGRAPH_DB` | environment alternative to the positional path |
| `--root DIR` | find the graph from `DIR` instead of the working directory, for a client that starts servers elsewhere; an unexpanded `${...}` placeholder is ignored |
| `CODEGRAPH_ROOT` | environment alternative to `--root` |
| `--web PORT` | also serve the graph visualiser on `http://localhost:PORT` |
| `CODEGRAPH_TELEMETRY=off` | keep usage counts in memory only; see [Telemetry](#telemetry) |
| `CODEGRAPH_INDEXER` | path of `codegraph-index` for `live_status start=true`, when it is not next to the server; see [Live daemon](#live-daemon) |
| `CODEGRAPH_EMBEDDINGS_URL`, `CODEGRAPH_EMBEDDINGS_MODEL`, `CODEGRAPH_EMBEDDINGS_KEY` | an OpenAI-compatible embeddings endpoint for semantic search; set the same values for `codegraph-index`. See [Semantic search](#semantic-search) |
| `CODEGRAPH_DAEMON=auto` | start the live daemon on the first tool call when none runs; it stops itself after 120 min unused |
| `CODEGRAPH_SNAPSHOT=off` | answer every graph read with SQL on the file instead of from the in-memory graph; see [Concurrency](#concurrency) |

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
| `instructions` | a short guide for the model: typical turn, ids are passed back verbatim without the brackets, what `STALE` means |

Which of these a given client renders is up to the client; VS Code adds support for newer
fields such as icons as it tracks the protocol.

## How answers look

Every tool returns one compact text block. Node rows share one shape:

```
kind  full name  [extra]  file:start-end  [id]  [STALE]
```

```
paths are relative to C:\dev\GymX
method  GymX.Core.WorkoutService.SaveAsync(GymX.Core.Workout, System.Threading.CancellationToken)  depth 1  src\GymX.Core\WorkoutService.cs:41-67  [k7m2qa4z]
```

File paths are relative to the repository root: the git worktree that holds the graph, else the folder that holds
`.codegraph`. An answer that has such a path names the root once, in its first line (after the `STALE` warning, if
any). A path outside the root stays absolute. Join the root and the path before opening a file.

While the graph is in memory (the normal case once the server has loaded it), rows print an 8-character handle in
place of the id, such as `[k7m2qa4z]`: 40 bits of the SHA-256 of the id in base 32, so the handle is the same in
every answer and every session for as long as the id exists. Every tool that takes an id accepts the handle or the
id. Two ids whose handles collide print in full. Until the graph is in memory, rows print the id itself.

For C#, the `[id]` is the Roslyn documentation-comment id; ids from the TypeScript extractor start with `ts:`,
and ids from a SCIP indexer with `scip:`. Pass it back verbatim, without the brackets, to any tool that takes
an `id` (the server strips a pair of brackets if they come along). C# ids are stable across runs and readable:
`T:` type, `M:` method, `P:` property, `F:` field, `E:` event, `F:<path>` file, `P:<name>` project.

### Staleness

Before each answer the server compares the graph with the working tree (cached for 3s, one
`stat` per indexed file, a re-hash only when size or mtime moved). If anything differs, the
answer starts with a warning and rows from edited files end in `STALE`:

```
⚠ STALE INDEX: 2 indexed file(s) edited since (ChainRunner.cs, RouteExecutor.cs). Rows marked
STALE come from changed files, and new files are missing. Refresh with: codegraph-index "…/App.sln"
```

A moved `HEAD` is reported too. `index_status` shows the full picture, including what each language was read
with and whether the code asked for more than the indexer knows:

```
csharp    : Roslyn 5.9.0 (up to C# 14); code uses C# 12, C# 14; 2 compile error(s), first: New.cs(4): CS9260 …
  ⚠ 2 error(s) say the code needs a newer language version than it was read with; declarations under them are missing. First: New.cs(4): CS9260 …
typescript: typescript 6.0.3; code uses ES2022
```

The warning lines are the signal that an answer is missing code rather than the code being absent: a repo on a
newer language version than this build's compiler, or a declaration shape the indexer has no mapping for yet
(see [Language versions](EXTRACTORS.md#language-versions)).

When the [live daemon](#live-daemon) is running, the server asks it before every answer whether saves are
pending, waits for them to be indexed (up to 8 s), and answers from the refreshed graph; `STALE` then only
appears when no daemon is running.

## Tools

Traversal answers (`impact_of`, `callers_of`, `callees_of`, the type and project walks, `traverse`) list the
nearest 200 rows and, past that, the count at each depth, so a hub's tens of thousands of dependents do not
flood the context; `impact_of` lists up to 50 guarding tests. `path_between` returns shortest paths only.

All 35 tools are read-only and idempotent. Seven of them (`live_status`, `symbol_at`, `live_references`,
`diagnostics`, `check_change`, `rename_preview`, `data_flow`) answer from the [live daemon](#live-daemon): its Roslyn
solution for C#, a language server for the other languages. `data_flow` answers for C# only.
`id` always means a node id from `find_symbol` or
`search_code`. Depth and limit arguments are clamped to the ranges shown. `Calls` edges cover
method calls, constructions, property, field and event accesses, and the members a `using`,
`foreach` or `await` calls without naming them (Dispose, GetEnumerator, GetAwaiter); the caller, test and
coverage walks treat a call to an interface or base member as a call to every implementation
and override.

### Finding things

| tool | arguments | answers |
|---|---|---|
| `find_symbol` | `query`, `kind?`, `limit` (1–100, 20), `typeKind?`, `accessibility?`, `isStatic?`, `isAbstract?`, `isVirtual?`, `isAsync?`, `isOverride?`, `memberKind?`, `nameMatch` (Contains / StartsWith / EndsWith / Exact), `offset`, `language?` | Types and members by name, case-insensitive. Exact name matches first, then shorter names. `Exact` also takes a qualified tail such as `GraphStore.Replace`, after a `.`, or after the `/` or `:` that ends a package path in Go, Python and TypeScript names (`Command.Find` for `github.com/spf13/cobra/Command.Find`). |
| `search_code` | `query`, `kind?`, `file?`, `limit` (1–100, 20), `mode` (auto / lexical / semantic / hybrid), `language?` | "Where's the code that does X": BM25 full-text over names, signatures, comments, string literals and body identifiers, fused with vector similarity when the graph has [embeddings](#semantic-search). Each hit shows how many query terms it matched and its score (lexical), its similarity (semantic), or its rank by words and by meaning (hybrid). |
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
| `most_called` | `limit` (1–100, 20), `minCallers` (1), `offset`, `includeExternal` (false), `nameFilter?`, `language?` | Methods ranked by direct caller count. With `includeExternal` and e.g. `nameFilter: "System.IO"`, which framework APIs the solution leans on. |
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
| `tests_for` | `id` | Test methods that call or read this member directly, including through an interface or base member it implements. In C#, what the framework runs around a test counts as called by it: the test class's constructors and `Dispose`, `IAsyncLifetime`, and `[SetUp]` / `[TestInitialize]`-style methods. |
| `covered_by` | `id`, `depth` (1–30, 10) | Tests that reach this member transitively, following interface and virtual dispatch, with hop count: the nearest 50 by depth, then the count of the rest at each depth. This is static reach, and the answer says so: on four public repositories (19 November 2026) the list held every test that runs the method on Cobra and Jellyfin, and on HTTPX listed 225 where 46 run it ([measured](docs/measured-results.md#four-public-repositories)). |
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

### Live (from `codegraph-index --watch`: the Roslyn solution for C#, a language server otherwise)

| tool | arguments | answers |
|---|---|---|
| `live_status` | `start` (false) | Whether a daemon holds this solution: pid, projects, last index, pending saves. `start=true` launches one. |
| `symbol_at` | `file`, `line`, `column` | The symbol under a position: its graph id, the enclosing member, and (C#) the type of the expression there. Metadata symbols are reported as not in the graph. |
| `live_references` | `symbol` (id or dotted name), `limit` (1–200, 50) | Every use of a symbol in the solution; for C#, including framework and NuGet members the graph does not hold ("who calls `File.Delete`"). Each row names the enclosing member and shows the line. |
| `diagnostics` | `file?`, `includeWarnings` (false), `limit` (1–100, 30) | Compiler errors (and warnings) for one file or the whole solution as it is on disk. |
| `check_change` | `file` + `text`, or `files` (`[{file, text}]`) | What-if compile: the proposed full text of one or more files is applied in memory, their projects and every dependent are compiled (for other languages, the language server rechecks the edited files and the files the graph says depend on them), and the new and fixed errors come back. Each declaration the edit removes, re-signs or re-implements is listed with its dependents and guarding tests from the graph. Nothing is written. |
| `data_flow` | `symbol` (a method id) | Where each parameter goes in the method body: passed to which call as which parameter, stored in which field or property, returned, captured, or deciding a branch; locals derived from a parameter are followed. One method deep. |
| `rename_preview` | `symbol`, `newName`, `overloads` (false), `limit` (200) | The compiler's rename (Roslyn's on a fork, or the language server's): every line that would change across the solution, through interfaces and overrides, and any compile errors the rename would cause. Nothing is written. |

### The server itself

| tool | arguments | answers |
|---|---|---|
| `graph_stats` | | Node and edge counts; confirms the graph loaded. |
| `index_status` | | Which graph, built when and at which commit, which compiler and language versions each language was read with (and warnings when the code is ahead of it), which indexed files changed or vanished since. |
| `usage_stats` | | Opens with the telemetry file's path and how tokens are estimated, then the last seven days in one line, then calls per tool, latency, answer size and the size of the files the answers name, this session and all time. Sizes only: no saving is measured. `codegraph-index usage` prints a summary from a terminal: the last days in one line, all time in one line, and a table of the calls in that period. |

Edge kinds, for `traverse` and `path_between`: `Calls`, `Inherits`, `Implements`, `Overrides`,
`Contains`, `DependsOn`, `TestedBy`, `InvokedByFramework`, `References`, `CallsExternal`, and from the
TypeScript and Angular extractor `Imports`, `Injects` and `Renders`.

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

Three calls. In [BENCHMARKS round 12](BENCHMARKS.md#round-12-the-docs-re-measured) the same three calls for
`GraphStore.Replace` in this repo came to about 5,500 tokens; the 15 files they name come to about 65,000, and the
25 files that match `.Replace(` to about 123,000.
[PROMPTS.md](PROMPTS.md) has example prompts for common questions and the tools Claude reaches for.

## Telemetry

Each call is appended as one JSON line to `.codegraph/telemetry.jsonl` beside the graph:

```json
{"at":"2026-10-29T19:56:39Z","tool":"impact_of","ms":25,"result_chars":2717,"source_chars":129653,"error":false}
```

`result_chars` is the answer's length in characters. `source_chars` is the total size in bytes of the files the
answer names, each counted once per call. It is a size, not what a session without the graph would have read;
that is not recorded. `usage_stats` turns the file into per-tool totals. The file
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
Roslyn solution (for other languages, a language server) and answers the seven live tools over a named pipe (a Unix domain socket on Linux and macOS, a
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
  pings before every tool call, so a session in use keeps its daemon. `codegraph-index status` asks with a
  `status` request instead, which does not count, so a tool that polls it (the VS Code extension) never keeps an
  unused daemon alive. Daemons the server starts use 120 min.
- **Memory.** `live_status` shows the working set and managed heap; this repo's daemon uses about 405 MB ([BENCHMARKS round 13](BENCHMARKS.md#round-13-performance-and-resilience-after-the-client-work)).

The server falls back to graph-only answers with `STALE` marks whenever no daemon is running.

What it costs: the memory of a loaded Roslyn solution (hundreds of MB for a large one) in one process per
worktree. What it gives: positions, metadata symbols, diagnostics, the what-if compile, and answers that
reflect the working tree within one debounce of a save. [BENCHMARKS.md](BENCHMARKS.md) has the numbers.

## Concurrency

The server answers graph reads (finds, walks, tests, coverage, dead code) from a snapshot of the graph held
in memory: node records and adjacency arrays in both directions, loaded in the background when the server
starts and again whenever the file's stamp changes (a re-index, an incremental write, a swap). A snapshot that
does not match the file is never answered; until the new one has loaded, calls read through DuckDB as below,
so a call is never slower than it was without snapshots. `index_status` says which is in use on its `reads`
line. On Jellyfin (21,174 nodes) the snapshot loads in 0.3 s into 17 MB; `impact_of` at depth 20 went from
0.86 s to under 10 ms through the server. `CODEGRAPH_SNAPSHOT=off` turns it off.

DuckDB allows one writer or many readers per file. A tool call that needs the database (full-text search,
vectors, metadata, or any read before the snapshot has loaded) opens its own read-only connection.
Between calls the server keeps one extra read-only connection, the anchor, which keeps the database
instance loaded so a call's open costs 0.025 ms instead of 9.9 ms ([BENCHMARKS round 2](BENCHMARKS.md#round-2-held-reader-and-a-probe-regression-found-and-fixed)). The anchor blocks a writer, so it lets
go after 1.5 s without a call, after 10 s however busy the server is, and immediately when the daemon
reports pending saves. A full rebuild writes a new file and swaps it in; the server notices the new file
on its next call and reloads. `codegraph-index` (including `--watch`) therefore writes while Claude
sessions are open, waiting at most a couple of seconds for the anchor. Two writers on one graph take turns: each
holds `graph.db.lock` from reading the old graph to swapping in the new one. Without it, the daemon re-indexing C#
while an import rewrites the TypeScript rows could have dropped the other's rows. A writer waits up to 15 minutes. The freshness check reloads its
file list whenever the database's mtime changes, regardless of its 3 s cache.

## Troubleshooting

| symptom | cause and fix |
|---|---|
| `no code graph at …` | Nothing indexed in this worktree yet. Run `codegraph-index <solution>` from inside it. |
| Every answer starts with `⚠ STALE INDEX` | Files changed since the index. Re-run `codegraph-index`, or leave `--watch` running. |
| `existing graph is from another solution, schema version or embedding model; rebuilding` | Expected after upgrading codegraph or pointing the indexer at a different solution. The full rebuild runs once. |
| `this graph has no full-text index` | The graph predates `search_code`. Run `codegraph-index --full`. |
| `this needs the live daemon, which is not running` | Start `codegraph-index <solution> --watch`, or call `live_status` with `start=true`. |
| `live daemon: the solution is still loading` | The daemon is in its MSBuild load; retry in a few seconds. |
| Two solutions in one worktree | Give each its own graph with `codegraph-index … --db <path>` and pass that path (or `CODEGRAPH_DB`) to the server. |
| `You must install or update .NET to run this application` | From-source installs only (`--from-source`): the release archives carry their own runtime. A from-source build looks for .NET in `DOTNET_ROOT` or the machine's default location, not on `PATH`; with a version-managed dotnet (mise, asdf) that location may lack ASP.NET Core. `codegraph-index init` detects this and adds `"env": { "DOTNET_ROOT": … }` to each entry it writes; set the same variable in your shell to run `codegraph-index` by hand. `codegraph-mcp --version` is a quick check. |
| The server will not start after an upgrade | A running `codegraph-mcp` held its files open during publish. Close the assistant sessions and re-run the install script, or use `-Force` / `--force`. |
| Server logs | stderr. Claude Code shows them under the MCP server's output. In VS Code, Copilot's agent records them in `~/.copilot/session-state/<id>/events.jsonl`. |

---

[CodeGraph](README.md) · [Docs](docs/README.md) · **MCP server** · [Extractors](EXTRACTORS.md) · [Benchmarks](BENCHMARKS.md) · [Prompts](PROMPTS.md) · [Changelog](CHANGELOG.md) · [Claude Code plugin](plugin/README.md) · [VS Code extension](vscode-extension/README.md)
