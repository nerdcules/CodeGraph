# Changelog

[CodeGraph](README.md) · [Docs](docs/README.md) · [MCP server](MCP-SERVER.md) · [Extractors](EXTRACTORS.md) · [Benchmarks](BENCHMARKS.md) · [Prompts](PROMPTS.md) · **Changelog** · [Claude Code plugin](plugin/README.md) · [Obsidian plugin](obsidian-plugin/README.md)

Each release is a tag `v<version>`, built and published by the [release workflow](../.github/workflows/release.yml).
The executables report their version with `--version`; the MCP server shows it to its client.

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
  3,119 nodes and 405 tests, the README's question answers 12 dependents and 37 tests in about 3 ms, and costs
  about 5,500 tokens against about 123,000. A save reaches a fresh answer in about a second on this repo, not
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
