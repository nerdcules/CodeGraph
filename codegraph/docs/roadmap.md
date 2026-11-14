# What is missing

*Author: Alan Seunarayan*

[Home](README.md) · [Installation](installation.md) · [Usage](usage.md) · [Obsidian](obsidian.md) · [Use cases](use-cases.md) · [How it works](how-it-works.md) · [Language versions](language-versions.md) · [Performance](performance.md) · [Architecture](architecture.md)

---

A frank list, in order of what would change adoption most. The engineering is ahead of the product:
the graph is precise, fast and tested, and the gap is everything between a stranger reading the README
and that stranger still using it a week later. Version 1.0.0 closed the first items; the rest are open.

## The analysis, applied

The list comes from a frank review of a sister project, [sankofa](https://github.com/nerdcules/sankofa),
whose seven findings fit CodeGraph almost one for one. Each row is the finding as written there, what it
means here, and where it stands.

| Finding, from sankofa | For CodeGraph | Status |
|---|---|---|
| "The core only runs on Linux." | The daemon uses .NET's file watcher and named pipes, which are cross-platform; the author uses Linux and Windows. | **Partly closed.** Releases, builds and the suite run on macOS in CI. No daily use there yet. |
| "No way to install it without building it." | Nothing but a .NET SDK, a checkout and a build. | **Closed.** Prebuilt archives on six platforms with their own runtime; one-line installers; a Claude Code plugin. |
| "No proof anyone else can trust." | No CI, no badge, no changelog, numbers buried in BENCHMARKS.md. | **Closed.** CI badge on three platforms, a CHANGELOG, release notes and checksums; the token and corpus numbers in the first paragraph of the README. |
| "The first five minutes are not designed." | The README opened with architecture. | **Closed.** Install, one question, the real answer, and the usage line a week later, with a picture of the terminal. The repository's front page opens with a real Claude Code session, replayed from its transcript, and a video of the same question with CodeGraph on and off. The install itself is not recorded yet. |
| "The value is invisible once installed." | Every call went silently into `telemetry.jsonl`. | **Closed.** `codegraph-index usage`, the same line after each index run, and at the top of `usage_stats`. |
| "One sentence of positioning." | "A compiler-built code graph with an MCP server" was accurate and abstract. | **Closed.** "CodeGraph gives Claude Code the compiler's answers: who calls this, what breaks if I change it, which tests guard it." Everywhere, including the GitHub description. |
| "Identity for teams." | The graph is per machine; the only shared piece is `.mcp.json`. | **Open.** A graph built in CI for the team, and a pull-request check on language-version errors. |

## Closed in 1.0.0

| Gap | What closed it |
|---|---|
| No way to install it without building it | Prebuilt archives for Linux, macOS and Windows on x64 and arm64, each with its own .NET runtime; `install.sh` and `install.ps1` download and verify them; a Claude Code [plugin](../plugin/README.md). |
| No proof anyone else can trust | CI on three platforms with a badge on the README; a [CHANGELOG](../CHANGELOG.md); release notes on each tag; checksums on each release. |
| The first five minutes were not designed | The README opens with the one-line install, one question, and the answer it gets. |
| The value was invisible once installed | `codegraph-index usage` prints what the graph did in the last week; the indexer prints the same line after each run; `usage_stats` opens with it. |
| No one-sentence positioning | "CodeGraph gives Claude Code the compiler's answers: who calls this, what breaks if I change it, which tests guard it." Used everywhere. |

## Open

1. **macOS is tested by CI only.** The daemon uses .NET's file watcher and named pipes, which are
   cross-platform, and the suite passes on the macOS runner. Nobody has used it there day to day. The
   executables are not signed or notarised, so an archive unpacked from a browser download needs
   `xattr -dr com.apple.quarantine`. The installer avoids this by downloading with curl.
2. **No package managers.** A Homebrew tap, a winget manifest and a `dotnet tool` package would each be
   one more one-liner. The plugin covers Claude Code users on macOS and Linux; on Windows it still needs
   the installer first, because a plugin's MCP command is a shell script.
3. **The first answer still needs a graph and an SDK.** `init` builds the graph, but a C# repository on
   a machine without its SDK, or a Java repository without scip-java, indexes nothing, and the tools answer
   "no code graph". The server could say which tool is missing and how to install it.
4. **Nothing for teams.** The graph is per machine and `.mcp.json` is the only shared piece. A graph built
   in CI and downloaded by teammates, or a check that fails a pull request on `language_version_errors`,
   would be small work on top of `codegraph-index export` and `index_status`.
5. **TypeScript and the SCIP languages re-index the whole workspace on each save.** C# re-walks one file.
   A few seconds for 1,000 files.
6. **One daemon per language.** In a repo with C# and Angular, `--watch` on the solution refreshes C# only;
   the front end needs a second daemon or a re-run of `init`.
7. **`data_flow` answers for C# only.** Java goes through scip-java, which tells calls and reads apart
   by the target's kind only.
8. **A recording of the install.** The [front page](../../README.md) opens with a real Claude Code session,
   replayed from its transcript, and a video of the same question with CodeGraph on and off. The install and the
   usage line a week later are still a picture of a terminal.

## How to help

Pick an item, open an issue to say so, and read [Contributing](contributing.md). The corpus and the test
suite will tell you when you are done.

---

← Previous: [The story](story.md) · Next: [Contributing](contributing.md) →
