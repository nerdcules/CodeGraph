# CodeGraph

Roslyn-based code graph indexer plus an MCP server, so Claude Code can answer
"who calls this / what breaks if I change it / which tests guard it" from a small
graph query instead of re-reading source files.

```
src/CodeGraph.Core      model + DuckDB store + traversals + full-text search + telemetry
src/CodeGraph.Indexer   codegraph-index: Roslyn extractor → graph.db (one-shot or --watch)
src/CodeGraph.Mcp       codegraph-mcp: stdio MCP server over graph.db
src/CodeGraph.Web       graph visualiser UI, embedded into codegraph-mcp --web
tests/CodeGraph.Tests   store, search, telemetry, indexer extraction, watch-mode sync
install.ps1 / .sh       build, test, publish both executables, print .mcp.json
nuget.config            nuget.org only, source-mapped, audit on
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
(`path_between`), which members are called the most (`most_called`), and "where's the code
that does X" when no symbol name is known (`search_code`, full-text over names, comments and
bodies).

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

`nuget.config` makes restore the same on every machine: it clears inherited feeds, registers
nuget.org as the only source and maps every package to it, so a private feed on a developer
box can neither slow restore down nor substitute a same-named package. It also turns the
audit on explicitly, at `low` severity and over transitive packages too (`NuGetAuditMode=all`),
which is what the build break above rests on.

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

The solution load (MSBuild evaluation) is paid every one-shot run, so a no-op re-index of this
repo takes about 9s against about 50s for a full one. An edit to a widely used file still
compiles every project that depends on it. Not covered: a *new* overload or extension method in
a file that existing callers never referenced can change what an untouched file binds to. Run
`--full` after big refactors.

### Watch mode

```bash
dotnet run --project src/CodeGraph.Indexer -- /path/to/GymX.sln --watch
```

`--watch` pays the solution load once and keeps running. A `FileSystemWatcher` on the worktree
collects saves, waits for a quiet half second, applies the edits to the loaded Roslyn solution in
memory (new text, new files into the project whose directory holds them, deleted files out) and
runs the same incremental walk as above, so a one-file edit re-indexes in well under a second
instead of the 9s a cold run spends in MSBuild. A change to a `.csproj`, `.sln`, `.props` or
`.targets` file is the one thing that triggers a real reload. Build output (`bin/`, `obj/`) is
ignored. Ctrl+C stops it. The MCP server notices each swap through the db's mtime, so Claude's
next answer uses the new graph.

The incremental contract is tested directly: v1 full, then an edit and an incremental run,
must equal v2 full (`IncrementalIndexTests`).

DuckDB allows one writer *or* many readers per file. The indexer only takes the write lock
for the final swap, which is one transaction, and the MCP server opens the graph read-only
per tool call. So re-indexing works while Claude sessions are running, and each side
retries briefly if it hits the other's lock.

## Install

```powershell
.\install.ps1                                   # Windows → C:\tools\codegraph
.\install.ps1 -InstallDir D:\tools\codegraph -AddToPath
```

```bash
./install.sh                                    # macOS / Linux → ~/.codegraph/bin
./install.sh --install-dir /opt/codegraph --add-to-path
```

Both scripts build with warnings as errors, run the test suite (`-SkipTests` / `--skip-tests`
to skip), publish `codegraph-index` and `codegraph-mcp` into the install folder, and print the
`.mcp.json` below with the right path filled in. Re-run to upgrade. A running `codegraph-mcp`
holds its files open, so the script stops rather than overwrite it; close the Claude Code
sessions using it, or pass `-Force` / `--force` to stop them.

## Wire it into Claude Code

Put one `.mcp.json` in the repo (the install script prints it). Leave out the db path: the
server looks for `.codegraph/graph.db` from its working directory up to the git worktree root. Claude Code starts it in the project directory, so the same file works in
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

### Telemetry

Every tool call is recorded: tool, duration, size of the answer, and the size of the source
files the answer pointed at, which is what reading them instead would have cost. One JSON line
per call goes to `.codegraph/telemetry.jsonl` beside the graph; nothing leaves the machine.
`usage_stats` reports both this session and all time:

```
this session (3 call(s), 1201 result tokens, ~54656 source tokens saved)
tool         calls  errors   avg ms  result tok  source tok   saved tok
search_code      2       0      138         521       23443       22922
find_symbol      1       0       25         680       32414       31734
```

Tokens are estimated at four characters each, and "saved" is an upper bound: it assumes the
model would have opened every file an answer mentions. Set `CODEGRAPH_TELEMETRY=off` to keep
the counts in memory only.

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
| `search_code` | "where's the code that does X": ranked full-text search over names, signatures, comments, string literals and body identifiers |
| `outline` | children of a node, no bodies |
| `callers_of` / `callees_of` | call graph, N hops |
| `impact_of` | transitive dependents + guarding tests (for a type: every member that references it, too) |
| `tests_for` | tests that call a member directly |
| `references_to` | where a type is used: signatures, fields, locals (including `var`-inferred ones), generic args, casts, attributes, record parameters, `using` aliases |
| `implementors_of` / `inheritors_of` | who implements an interface (including subclasses of implementors) / derives from a type |
| `snippet` | just the declaration span from source |
| `graph_stats` | sanity check |
| `index_status` | which graph, built when and at which commit, which files changed since |
| `usage_stats` | calls per tool, latency, answer size and estimated source tokens saved, this session and all time |

A typical Claude turn: `find_symbol` → `impact_of` → `snippet` on the two or three
things that matter. Low thousands of tokens instead of tens of thousands.
[PROMPTS.md](PROMPTS.md) has example prompts and the tools each one uses.

Indexing this repo is the quickest end-to-end check: `tests_for` on `GraphStore.Impact`
should come back with the tests in `tests/CodeGraph.Tests`.

### Full-text search

`search_code` answers intent-style questions when no symbol name is known. At index time every
type and member gets a document: its name (weighted ×3), its doc and leading comments (×2), its
signature, and the identifiers, string literals and comments in its body. Identifiers split on
case boundaries, so `SaveWorkoutAsync` is found by "save workout"; a light stemmer folds
plurals and tenses on both sides. Queries are ranked by BM25 in plain SQL over two tables in the
same DuckDB file, so there is no extension to install and read-only queries work as for every
other tool. Results show how many of the query's terms each hit matched.

This is lexical search: it finds code that uses the same words, not code that means the same
thing. Embeddings would close that gap and are the natural next step.

## Not yet

- Embeddings for semantic "where's the code that does X" search; `search_code` is lexical.
- `--watch` re-reads a project's globs only on a project-file change, so a new file in a
  directory no project covers is picked up by the next cold run.
