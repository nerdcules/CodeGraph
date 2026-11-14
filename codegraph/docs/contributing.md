# Contributing

*Author: Alan Seunarayan*

[Home](README.md) · [Installation](installation.md) · [Usage](usage.md) · [Obsidian](obsidian.md) · [Use cases](use-cases.md) · [How it works](how-it-works.md) · [Language versions](language-versions.md) · [Performance](performance.md) · [Architecture](architecture.md)

---

This page is for people who build CodeGraph from source, add a language, or cut a release.
Users do not need it: [Installation](installation.md) installs a prebuilt release.

## Contents

- [Build and test](#build-and-test)
- [Run from a checkout](#run-from-a-checkout)
- [Repository layout](#repository-layout)
- [The slower test layers](#the-slower-test-layers)
- [Add a language](#add-a-language)
- [Cut a release](#cut-a-release)
- [Conventions](#conventions)

## Build and test

You need the .NET 10 SDK and git. Node.js 22 adds the TypeScript extractor and the Obsidian plugin.

```bash
cd codegraph
dotnet build -warnaserror
dotnet test
(cd extractors/typescript && npm ci && npm test)
(cd obsidian-plugin && node build.mjs --check && npm test)
```

The build treats warnings and NuGet audit findings as errors, and `nuget.config` pins nuget.org as the
only source. The .NET suite needs no SDK discovery and no network: the store tests use a throwaway DuckDB
file and the indexer tests drive the real Roslyn extractor over an `AdhocWorkspace`.
[CI](../../.github/workflows/ci.yml) runs the same commands on Linux, macOS and Windows for every push.

## Run from a checkout

Two ways. `install.sh --from-source` (or `install.ps1 -FromSource`) builds, tests and publishes the
executables into the install folder, exactly like a release but from your working tree. `setup.sh`
(`setup.ps1`) skips publishing: it writes the repo's `.mcp.json` to run the server with `dotnet run
--no-build` from the checkout, so a `git pull` and a build is an upgrade.

```bash
./install.sh --from-source ~/dev/App       # publish this checkout to ~/.codegraph/bin
./setup.sh --solution ~/dev/App/App.sln     # or point the repo at the checkout
```

## Repository layout

```
src/CodeGraph.Core      model, DuckDB store, traversals, full-text search, telemetry, live protocol
src/CodeGraph.Indexer   codegraph-index: Roslyn extractor, record and SCIP import, watch mode, live daemon, exports
extractors/typescript   codegraph-ts: TypeScript and Angular extractor (Node), with typescript-language-server
src/CodeGraph.Mcp       codegraph-mcp: stdio MCP server over graph.db
src/CodeGraph.Web       graph viewer UI and its API routes, embedded into codegraph-mcp --web
obsidian-plugin         Obsidian plugin: the graph as linked notes, plus a live panel over codegraph-mcp
plugin                  Claude Code plugin: the server, a skill, and a launcher that installs the release
tests/CodeGraph.Tests   store, search, telemetry, extraction, incremental and watch-mode sync, live daemon, exports
tools/CodeGraph.Bench   codegraph-bench: the numbers in BENCHMARKS.md
tools/corpus            generated codebases in eight languages, run in Docker, with answer keys
tools/obsidian-e2e      the plugin end to end in real Obsidian, headless, per Obsidian version
tools/release           package.sh builds the release archives; notes.sh writes the release notes
install.* / setup.*     install a release (or --from-source) and set up repos / run from a checkout
uninstall.*             remove the install folder, PATH entry, a repo's .mcp.json entry and graph
```

[Architecture](architecture.md) explains the components, the graph file and the processes.

## The slower test layers

Two layers run in Docker and are not part of CI:

- [tools/corpus](../tools/corpus/README.md) generates codebases in eight languages with answer keys and
  scores precision and recall, in a modern and a classic dialect, on the current toolchains and on older
  SDK images. Run it after a change to an extractor or to the importer:

  ```bash
  python3 tools/corpus/run.py --base <local image> --dialects modern,classic
  python3 tools/corpus/run.py --toolchains dotnet8,node20,jdk17,go121,rust170,python38
  ```

- [tools/obsidian-e2e](../tools/obsidian-e2e/README.md) runs the plugin inside real Obsidian, headless,
  per Obsidian version.

[Language versions](language-versions.md) tells how to check a new compiler or language version, and
[BENCHMARKS.md](../BENCHMARKS.md) records a round per change that touched performance or coverage.

## Add a language

The indexer reads records in the format in [EXTRACTORS.md](../EXTRACTORS.md): an extractor writes nodes,
edges and file stamps as NDJSON, and `codegraph-index import` writes them into the graph. The TypeScript
extractor is the worked example. A language with a SCIP indexer needs no extractor at all: add it to
`ScipIndexers.All` with its marker file and its tested version, and extend the corpus generator so the
answer key covers it.

## Cut a release

1. Add a section to [CHANGELOG.md](../CHANGELOG.md) headed `## <version>`.
2. Set `Version` in `Directory.Build.props` and `version` in `plugin/.claude-plugin/plugin.json` to the
   same number.
3. Commit, then tag and push:

   ```bash
   git tag v1.2.0
   git push origin main v1.2.0
   ```

The [release workflow](../../.github/workflows/release.yml) builds and tests on Linux, packages the six
platforms on their own runners with `tools/release/package.sh`, writes `SHA256SUMS`, and publishes a
GitHub Release whose notes are the CHANGELOG section plus the install one-liners. The installers download
`releases/latest`, so a published release is live at once. To try the packaging locally:

```bash
CODEGRAPH_VERSION=v1.2.0 tools/release/package.sh linux-x64     # → dist/codegraph-linux-x64.tar.gz
```

A release archive holds the two executables with their own .NET runtime (ReadyToRun, self-contained), the
TypeScript extractor with its packages, `LICENSE` and `VERSION`. C# indexing still needs the .NET SDK on
the machine, because MSBuild loads the projects.

## Conventions

- The user-facing pages under `docs/` use short sentences and one instruction per step. The README and
  the reference pages (`MCP-SERVER.md`, `EXTRACTORS.md`, `BENCHMARKS.md`) are written for people who skim.
- A number in a page comes from a measurement in `BENCHMARKS.md` or from the corpus. When you change
  one, change the other.
- Commit messages name the area first: `feat(scip): ...`, `docs(install): ...`, `fix: ...`.

---

← Previous: [What is missing](roadmap.md) · Next: [Architecture](architecture.md) →
