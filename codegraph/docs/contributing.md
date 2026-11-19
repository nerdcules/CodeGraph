# Contributing

*Author: Alan Seunarayan*

[Home](README.md) · [Installation](installation.md) · [Usage](usage.md) · [Use cases](use-cases.md) · [How it works](how-it-works.md) · [Language versions](language-versions.md) · [Performance](performance.md) · [Architecture](architecture.md) · [Measured results](measured-results.md) · [The story](story.md) · [What is missing](roadmap.md) · **Contributing**

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

You need the .NET 10 SDK and git. Node.js adds the TypeScript extractor: 22.22.3 or later,
or 24.15 or later, which is what the extractor's packages declare (round 10 in BENCHMARKS.md also indexed on Node 20).

```bash
cd codegraph
(cd extractors/typescript && npm ci)
dotnet build -warnaserror
dotnet test
(cd extractors/typescript && npm test)
```

The build treats warnings and NuGet audit findings as errors, and `nuget.config` pins nuget.org as the
only source. Most of the .NET suite needs no SDK discovery and no network: the store tests use a throwaway DuckDB
file and most indexer tests drive the real Roslyn extractor over an `AdhocWorkspace`. `FirstIndexTests` run
`codegraph-index` on a C# project as a process, which loads MSBuild, so they need the .NET 10 SDK: on Windows with
only the .NET 11 SDK, its 3 tests failed (`windows-report.md` on the `windows-test-results` branch). The
TypeScript index and live tests need Node.js and the extractor's packages, so `npm ci` comes before `dotnet test`;
without them, those tests skip.
[CI](../../.github/workflows/ci.yml) runs the same commands on Linux, macOS and Windows, on each push to `main` and
on each pull request. On macOS and Linux it also runs `tests/install-through-symlink.sh`, which restores, builds and
installs from a clone reached through a symlink: on macOS `/tmp` is one, and a checkout there once built its projects
twice.

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
obsidian-plugin         Obsidian plugin (retired; kept in the repo): the graph as linked notes, plus a live panel
plugin                  Claude Code plugin: the server, a skill, and a launcher that installs the release
tests/CodeGraph.Tests   store, search, telemetry, extraction, incremental and watch-mode sync, live daemon, exports
tools/CodeGraph.Bench   codegraph-bench: the numbers in BENCHMARKS.md
tools/corpus            generated codebases in eight languages, run in Docker, with answer keys
tools/eval              runs of four assistants with and without CodeGraph, scored against a run-time answer key
tools/obsidian-e2e      the Obsidian plugin end to end in real Obsidian, headless (retired; kept in the repo)
tools/release           package.sh builds the release archives; notes.sh writes the release notes
install.* / setup.*     install a release (or --from-source) and set up repos / run from a checkout
uninstall.*             remove the install folder, PATH entry, a repo's .mcp.json entry and graph
```

[Architecture](architecture.md) explains the components, the graph file and the processes.

## The slower test layers

One layer runs in Docker and is not part of CI.
[tools/corpus](../tools/corpus/README.md) generates codebases in eight languages with answer keys and
scores precision and recall, in a modern and a classic dialect, on the current toolchains and on older
SDK images. Run it after a change to an extractor or to the importer:

```bash
python3 tools/corpus/run.py --base <local image> --dialects modern,classic
python3 tools/corpus/run.py --toolchains dotnet8,node20,jdk17,go121,rust170,python38
```

Another layer tests the questions the install scripts ask, and is not part of CI either.
[tests/install-questions.py](../tests/install-questions.py) runs `install.ps1` and `install.sh` in a pseudo-terminal
(ConPTY on Windows), answers each question, reads the screen back, and checks what the script did: the list redraws
and folds, a 14-row window, No to the plan, a running server, `-VSCode` / `--vscode`, a release without the
extension, and uninstall. It downloads one release once and serves it from a folder, installs only under its work
folder, and puts the extension in a VS Code extensions folder of its own. Run it after a change to either script:

```bash
uv run tests/install-questions.py                     # Windows: powershell (5.1) and pwsh; elsewhere: bash and pwsh
uv run tests/install-questions.py --shells bash --work /tmp/cg-iq
```

It writes `results.md`, `results.json` and one screen per case in the work folder, and exits 1 when a case fails.
On Linux on 18 November 2026 (commit `5f27e99`, release 1.3.0-beta.1, VS Code 1.146.0) all 19 cases passed in bash
5.3.20 and PowerShell 7.7.0. With the line that folds the list removed from `install.sh`, the two cases that check
the fold failed. On Windows 11 the same day (commit `12b8c14`), PowerShell 7.7.0 passed 9 of 9 and Windows
PowerShell 5.1 passed 9 of 9 once the `PSModulePath` it inherited from pwsh was cleared; the harness now clears it
for 5.1, and its case N5 runs 5.1 with the inherited path on purpose. At `b302cc9`, 19 of 20 passed: N5 failed at
`Expand-Archive`, which the scripts now fix by dropping PowerShell 7's module folders when they run in 5.1. At
`c919b87` all 21 cases passed on Windows 11 (windows-report-9), including U5, the uninstall in 5.1 with the inherited
path.

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
2. Set `Version` in `Directory.Build.props`, `version` in `vscode-extension/package.json` and `version` in
   `plugin/.claude-plugin/plugin.json` to the same number. For a pre-release, leave `plugin.json`: the plugin
   installs the latest full release.
3. Commit, then tag and push:

   ```bash
   git tag v1.2.0
   git push origin main v1.2.0
   ```

The [release workflow](../../.github/workflows/release.yml) builds and tests on Linux, packages the six
platforms on three runners (Linux, macOS and Windows, two architectures each) with `tools/release/package.sh`, packs
the VS Code extension as `codegraph-vscode.vsix` with the tag's version, writes `SHA256SUMS`, and publishes a
GitHub Release whose notes are the CHANGELOG section plus the install one-liners. The installers download
`releases/latest`, so a published release is live at once.

A tag with a `-suffix`, such as `v1.3.0-beta.1`, is published as a pre-release. GitHub's latest release does not move
to it, so the installers, the Claude Code plugin and `releases/latest/download/` links keep the last full release; its
notes add `--version <tag>` to the install one-liners. The VS Code extension installs its own release while GitHub's
latest is older. To try the packaging locally:

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

← Previous: [What is missing](roadmap.md) · [Home](README.md)
