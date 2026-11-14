# Changelog

[CodeGraph](README.md) · [Docs](docs/README.md) · [MCP server](MCP-SERVER.md) · [Extractors](EXTRACTORS.md) · [Benchmarks](BENCHMARKS.md) · [Prompts](PROMPTS.md) · **Changelog** · [Claude Code plugin](plugin/README.md) · [Obsidian plugin](obsidian-plugin/README.md)

Each release is a tag `v<version>`, built and published by the [release workflow](../.github/workflows/release.yml).
The executables report their version with `--version`; the MCP server shows it to its client.

## Unreleased

### Any MCP client

- `codegraph-index init --client <names>` writes the server's config for other assistants. `copilot` covers every
  GitHub Copilot that reads a project's servers: the CLI (`.mcp.json`), VS Code (`.vscode/mcp.json`), Visual
  Studio (`.vs/mcp.json` beside the solution) and JetBrains IDEs (`.github/mcp.json`); each also has its own name.
  The others: `cursor` (`.cursor/mcp.json`), `gemini` (`.gemini/settings.json`), `codex` (its global config,
  through `codex mcp add`, keeping hand-added env), and the AI extensions `roo` (`.roo/mcp.json`), `kilo`
  (`.kilo/kilo.json`, in opencode 1.x's shape) and `continue` (`.continue/mcpServers/codegraph.json`).
  `claude` (`.mcp.json`) stays the default; `all` writes every one. Other servers and settings in those files are
  kept; a re-run with nothing new changes nothing. `install.sh --client` and `install.ps1 -Client` pass it on.
- `codegraph-mcp --root DIR` (or `CODEGRAPH_ROOT`) finds the graph from `DIR` instead of the working directory,
  for IDEs that do not start servers in the project. VS Code and Cursor get `--root ${workspaceFolder}`; Visual
  Studio and JetBrains, which neither start servers in the project nor expand variables, get the absolute
  worktree root. A placeholder left unexpanded, as the Cursor CLI does, is ignored.
- Checked with real tool calls from the Cursor IDE 3.29 (`${workspaceFolder}` expanded), Copilot in VS Code 1.146 (agent mode, `${workspaceFolder}` expanded), the Codex
  VS Code extension 26.1111 (it starts servers in the workspace, so the global entry needs no root), the Codex CLI
  and the GitHub Copilot CLI, and the Cursor CLI (headless runs need `Mcp(codegraph:*)` allowed in
  `.cursor/cli.json`; the docs say how).
- Tests guard what their framework runs around them. In C#, a test now calls its class's constructors (instance
  and static), `Dispose`/`DisposeAsync`, `IAsyncLifetime.InitializeAsync`, and methods marked `[SetUp]`,
  `[TearDown]`, `[OneTimeSetUp]`, `[OneTimeTearDown]` (NUnit), `[TestInitialize]`, `[TestCleanup]`,
  `[ClassInitialize]`, `[ClassCleanup]` (MSTest), on the class and its base classes. A run-time check found the
  gap: on release 1.1.0, 44 tests execute `GraphStore.Replace` and `impact_of` named 37 (all real); 5 of the
  missing ones reach it only through `IndexFreshnessTests`' constructor. With this change it names 42 of the 44.
  The other 2 run the indexer as a child process, which a static graph cannot see.
- A damaged graph file no longer crashes the indexer. An incremental run that cannot read the old graph rebuilds
  it from source; `--full` (which read the old file to keep other languages' rows) skips what it cannot read. Both
  say so. Before, both aborted, and the only cure was deleting the file.
- A tool call that fails says why. A missing or mistyped argument gets the problem and the tool's parameters
  ("impact_of takes: id (string, required), depth (integer, default 5)"); a damaged graph file gets the command
  that rebuilds it. Before, every one was "An error occurred invoking 'x'".
- BENCHMARKS.md round 13: no speed lost on this repo or Jellyfin, parity clean, and a resilience battery
  (`tools/CodeGraph.Bench/resilience.py`, 12 of 12 after the two fixes above). It found the graph file creeping
  under the live daemon, about 3 MB over the last 50 of 100 saves; fixed below.
- An incremental pass writes only the rows that changed. Before, it deleted and re-inserted every row of each
  re-walked file and project file, and all the stamps and meta. DuckDB rewrites every row group and index those
  touch, so under `--watch` the file grew with each pass and kept creeping. Over 150 saves on this repo: 1.1.0
  went from 32.3 to 51.0 MB, still rising; this build stays at 30–38 MB, with no trend. A no-op pass on Jellyfin
  now adds 2 MB instead of 47. After the passes, the graph equals a fresh full index in every table, on both
  repos. BENCHMARKS.md round 14 has the figures; `tools/CodeGraph.Bench/file-growth.sh` repeats them.
- Windows 11, checked by a Claude Code session there (BENCHMARKS.md round 15): 425 tests passed, 3 skipped; graph
  swaps under a reading server, install and uninstall in PowerShell 7 and 5.1, and the live daemon all worked.
  It found four things, now fixed:
  - With only a newer SDK installed (.NET 11), the indexer died with an unhandled MSBuildLocator exception. It now
    says to install the .NET 10 SDK, or to set `DOTNET_ROLL_FORWARD=LatestMajor` for a build from source.
  - A project whose NuGet packages were never restored, such as a fresh clone, indexed incompletely with no clear
    warning when the framework still resolved. It now gets a note saying to run `dotnet restore`. A pass that
    walks no file keeps the last walk's compile errors and notes instead of writing zeros over them.
  - `resilience.py` runs on Windows (no `SIGKILL` there), and reads the node count after a stale-index warning.
  - `uninstall.ps1` under Windows PowerShell 5.1 keeps the 2-space layout `init` writes, rather than rewriting
    every line in 5.1's own layout.
- The server accepts the id under the names models guess for it (`symbolId`, `nodeId`, `memberId`, and their
  snake-case forms, or `id` for the tools that take `symbol`). A Cursor agent passed `symbolId` to every tool, got
  only "An error occurred invoking", and answered without the graph.
- [tools/eval](tools/eval/README.md): four assistants (Claude Code, Codex CLI, Copilot CLI, Cursor CLI), three runs
  each with and without CodeGraph, on a copy of this repo without its docs, scored against a run-time answer key.
  With CodeGraph 7 of 12 answers gave a test total (each 42, the graph's count; 44 tests execute the method);
  without it none did. Claude Code was faster and cheaper with it; the other three were not. Summary in
  [Why it saves tokens](docs/why-it-saves-tokens.md#four-assistants-with-and-without).
- The server accepts an id with the brackets its answers print around ids (`[M:App.Run]`): a Cursor agent
  copied them and got "no node with id". The instructions now say to drop them.
- `find_symbol` with `nameMatch: Exact` also takes a qualified tail of the full name without its parameter list:
  `GraphStore.Replace` finds `CodeGraph.Core.GraphStore.Replace(bool, …)`. A Cursor agent tried exactly that,
  found nothing, and fell back to two broader searches. The in-memory and SQL paths agree (parity tests).
  Where each client starts its servers and which variables it expands was read from the Roo Code, Kilo Code and
  Continue sources, and the Visual Studio and Copilot for JetBrains docs and issue tracker.
- The uninstall scripts remove the entry from every config `init` can write, and from Codex.
- The README and docs say "your AI coding assistant" rather than Claude Code alone; the usage page lists each
  assistant's instructions file. [Tested with](docs/installation.md#tested-with) lists the assistants that
  answered with real tool calls, and [docs/client-tests](docs/client-tests/README.md) has the configuration, tool
  calls and answer of each run.
- The promo video (118.6 s) says "your AI coding assistant", and a new card lists the assistants it was tested with.
  The on/off sessions are still Claude Code. Its token figures are now the two measured sessions (180,134 against
  459,907 input tokens), not the README's example week or the size of the matching files.
- [Why it saves tokens](docs/why-it-saves-tokens.md#in-a-real-session) adds the measured sessions: 42 s against
  87 s, 4 model calls against 9, and a context that grew by about the same amount. The README no longer says "one
  tool call instead of twenty file reads".

## 1.1.0

### Faster reads

- The MCP server and the web view hold the graph in memory (`GraphSnapshot`): node records and adjacency arrays
  in both directions, loaded in the background at start and after every re-index. Finds, walks, tests, coverage,
  dead code, paths and subgraphs answer from it; the database is opened only for full-text search, vectors and
  metadata. Until a snapshot matching the file has loaded, calls read through DuckDB as before.
  On Jellyfin (21,174 nodes), through the server at a model's pace: `impact_of` depth 20 from 860 ms to 9.7 ms,
  `covered_by` depth 30 from 1,173 ms to 5.1 ms, `find_symbol` from 42 ms to 8.8 ms. `CODEGRAPH_SNAPSHOT=off`
  turns it off; `index_status` says which path answers.
- The recursive SQL stays as the reference: parity tests run every read both ways over random graphs, and
  `codegraph-bench --parity N` does the same over a real graph (0 differences in 6,096 checks on Jellyfin).
  SQL orderings now break ties by id, so both answer in one defined order.
- The freshness check reads HEAD from `.git` (loose refs, `packed-refs`, linked worktrees) instead of starting
  `git rev-parse` on most calls; it falls back to git for anything unusual, such as a reftable repository.
- `codegraph-bench` times every query through DuckDB and from the snapshot, and with `--server` times real
  tool calls through one or more `codegraph-mcp` builds over stdio.

### Docs

- Every figure re-measured on 14 November 2026 ([BENCHMARKS.md](BENCHMARKS.md), round 12): this repo is now
  3,119 nodes and 405 tests, the README's question answers 12 dependents and 37 tests in about 3 ms, with
  about 5,500 tokens of answers against about 123,000 for the files that match. A save reaches a fresh answer in about a second on this repo, not
  "well under a second" as the docs said: the graph doubled since that was measured.
- New Obsidian screenshots; redrawn live-daemon and index-time charts; an install diagram that shows the release
  download rather than the build from source; a corpus chart that no longer cuts off the Rust line. Jellyfin,
  NestJS, RealWorld and the corpus's large profile were measured again for the performance page.
- `tools/corpus/run.py` mounts whichever of `GOBIN` and `GOPATH/bin` holds scip-go. Under mise, `GOBIN` is Go's own
  bin and every Go run failed with "scip-go is not on PATH".
- `tools/obsidian-e2e/screenshots.py` approves the repo's `.mcp.json` entry, as `run_e2e.sh` does. Without it
  every panel in the screenshots said `spawn codegraph-mcp ENOENT` and the run stopped at Find symbol.
- The promo video shows the new figures. Its two sessions were run again headless (`claude -p`, Claude Opus 5.5, effort
  high, Auto mode) and are replayed from their transcripts: with CodeGraph, 42 s, 4 API calls, 180,134 tokens processed and
  exactly 37 tests; without, 87 s, 9 calls, 459,907 tokens and "about 40". The README's GIF is the first of them.

## 1.0.0

The first packaged release. Everything below was built between August and November 2026.

### Install

- Prebuilt archives for Linux, macOS and Windows on x64 and arm64, each with its own .NET runtime, the
  TypeScript extractor and its packages. `install.sh` and `install.ps1` download the one for your platform,
  verify its checksum and set up a repository; `--from-source` builds a checkout as before.
- The Obsidian plugin ships as `codegraph-obsidian.zip`.
- `codegraph-index init` writes the repository's `.mcp.json`, ignores `.codegraph/` and builds the first graph.

### Graph

- C# through Roslyn, in process; TypeScript, JavaScript and Angular through the TypeScript compiler; Java,
  Kotlin, Scala, Go, Rust and Python through their SCIP indexers. One graph holds several languages.
- Calls, references, inheritance, implementation, overrides, tests, project dependencies and framework
  entry points as compiler-resolved edges. A call to an interface member counts for every implementation.
- Incremental re-indexing by file hash; a full rebuild swaps a fresh file in with one rename.
- Each language records its compiler, the language versions the code uses and the errors it hit, so
  `index_status` says when a repository is newer than the compiler that read it.

### Tools

- 35 MCP tools over stdio: find, callers, callees, impact, tests, dead code, full-text and semantic search,
  graph statistics and usage statistics.
- Every answer is checked against the working tree first and marked `STALE` where it is behind.
- Walk answers are capped at the nearest 200 rows per depth, with a count of the rest.

### Live

- `codegraph-index --watch` keeps the compiled solution in memory, re-indexes a save in well under a second
  and answers `check_change`, `rename_preview`, `diagnostics`, `live_references` and `data_flow` over a pipe.
- For TypeScript, Java, Go, Rust and Python the same tools go through the language server when one is on PATH.

### Tested

- 390 .NET tests, 11 extractor tests and the Obsidian plugin's tests run on every push on Linux, macOS and Windows.
- A generated corpus in eight languages, with an answer key, scores precision and recall at 100% on current
  toolchains in a modern and a classic dialect, and on older SDK images in Docker (.NET 8, Node 20, JDK 17,
  Go 1.21, Rust 1.70, Python 3.8). [BENCHMARKS.md](BENCHMARKS.md) has every number.

---

[CodeGraph](README.md) · [Docs](docs/README.md) · [MCP server](MCP-SERVER.md) · [Extractors](EXTRACTORS.md) · [Benchmarks](BENCHMARKS.md) · [Prompts](PROMPTS.md) · **Changelog** · [Claude Code plugin](plugin/README.md) · [Obsidian plugin](obsidian-plugin/README.md)
