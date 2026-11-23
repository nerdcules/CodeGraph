# What is missing

*Author: Alan Seunarayan*

[Home](README.md) · [Installation](installation.md) · [Usage](usage.md) · [Use cases](use-cases.md) · [How it works](how-it-works.md) · [Language versions](language-versions.md) · [Performance](performance.md) · [Architecture](architecture.md) · [Measured results](measured-results.md) · [The story](story.md) · **What is missing** · [Contributing](contributing.md)

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
| "The core only runs on Linux." | The daemon uses .NET's file watcher and named pipes, which are cross-platform; the author uses Linux and Windows. | **Partly closed.** Releases, builds and the suite run on macOS in CI, and one run on an Intel Mac covered the installers, the live daemon and the resilience battery and found a symlink build bug, since fixed (BENCHMARKS round 16). No daily use there yet. |
| "No way to install it without building it." | Nothing but a .NET SDK, a checkout and a build. | **Closed.** Prebuilt archives on six platforms with their own runtime; one-line installers; a Claude Code plugin. |
| "No proof anyone else can trust." | No CI, no badge, no changelog, numbers buried in BENCHMARKS.md. | **Closed.** CI badge on three platforms, a CHANGELOG, release notes and checksums; the token and corpus numbers at the top of the README. |
| "The first five minutes are not designed." | The README opened with architecture. | **Closed.** Install, one question, the real answer, and the usage line a week later, with a picture of the terminal. The repository's front page opens with a real Claude Code session, replayed from its transcript, and a video of the same question with CodeGraph on and off. The install itself is not recorded yet. |
| "The value is invisible once installed." | Every call went silently into `telemetry.jsonl`. | **Closed.** `codegraph-index usage`, the same line after each index run, and at the top of `usage_stats`. |
| "One sentence of positioning." | "A compiler-built code graph with an MCP server" was accurate and abstract. | **Closed.** "CodeGraph gives your AI coding assistant the compiler's answers: who calls this, what breaks if I change it, which tests guard it." Everywhere. It named only Claude Code until `init --client` set up the other assistants too. |
| "Identity for teams." | The graph is per machine; the only shared piece is `.mcp.json`. | **Open.** A graph built in CI for the team, and a pull-request check on language-version errors. |

## Closed in 1.0.0

| Gap | What closed it |
|---|---|
| No way to install it without building it | Prebuilt archives for Linux, macOS and Windows on x64 and arm64, each with its own .NET runtime; `install.sh` and `install.ps1` download and verify them; a Claude Code [plugin](../plugin/README.md). |
| No proof anyone else can trust | CI on three platforms with a badge on the README; a [CHANGELOG](../CHANGELOG.md); release notes on each tag; checksums on each release. |
| The first five minutes were not designed | The README opens with the one-line install, one question, and the answer it gets. |
| The value was invisible once installed | `codegraph-index usage` prints what the graph did in the last week; the indexer prints the same line after each run; `usage_stats` opens with it. |
| No one-sentence positioning | "CodeGraph gives your AI coding assistant the compiler's answers: who calls this, what breaks if I change it, which tests guard it." Used everywhere. |

## Open

1. **macOS is tested by CI and two runs on one Mac.** The daemon uses .NET's file watcher and named pipes, which
   are cross-platform, and the suite passes on the macOS runner. One run on an Intel Mac (macOS 15) covered the
   installers, the live daemon and the resilience battery, and found a build bug in checkouts reached through a
   symlink; the fix is checked on Linux, in macOS CI and in a second run on that Mac (BENCHMARKS round 16).
   Nobody has used it there day to day. The executables are not signed or notarised, so an archive unpacked from
   a browser download needs `xattr -dr com.apple.quarantine`. The installer avoids this by downloading with curl.
2. **No package managers.** A Homebrew tap, a winget manifest and a `dotnet tool` package would each be
   one more one-liner. The plugin covers Claude Code users on macOS and Linux; on Windows it still needs
   the installer first, because a plugin's MCP command is a shell script.
3. **The first answer still needs a graph and an SDK.** `init` builds the graph, but a C# repository on
   a machine without its SDK, or a Java repository without scip-java, indexes nothing, and the tools answer
   "no code graph". The [VS Code extension](../vscode-extension/README.md) names the missing tool for each target
   and how to install it; the server still does not.
4. **Nothing for teams.** The graph is per machine and `.mcp.json` is the only shared piece. A graph built
   in CI and downloaded by teammates, or a check that fails a pull request on `language_version_errors`,
   would be small work on top of `codegraph-index export` and `index_status`.
5. **TypeScript and the SCIP languages re-index the whole workspace on each save.** C# re-walks the
   changed files and their dependents (10 files for a save to `Worktree.cs`, BENCHMARKS round 12). A full
   index of NestJS's 1,180 files takes 7.7 to 7.8 seconds (round 12).
6. **One daemon per graph.** In a repo with C# and Angular, `--watch` on the solution refreshes C# only, and a
   second `--watch` on the front end does not start, because one daemon serves each graph. The front end needs a
   re-run of `codegraph-index` on it. The [VS Code extension](../vscode-extension/README.md) does that on each save;
   other editors and terminals do not.
7. **One target per language in a graph.** An import replaces every row of its language, so two TypeScript
   workspaces (or two C# solutions) indexed into one graph keep only the last one's rows. `init` on a C# solution
   indexes every front end it finds under the repository the same way, so with two front ends only the last stays.
   The [VS Code extension](../vscode-extension/README.md) indexes one target of each language and marks the others
   as not indexed. A fix is to index the workspaces of a language in one run, or to replace a language's rows per target.
8. **`data_flow` answers for C# only.** Java goes through scip-java, which tells calls and reads apart
   by the target's kind only.
9. **A recording of the install.** The [front page](../../README.md) opens with a real Claude Code session,
   replayed from its transcript, and a video of the same question with CodeGraph on and off. The install and the
   usage line a week later are still a picture of a terminal.
10. **A smaller start for each session.** On four public repositories on 19 November 2026
    ([measured](measured-results.md#four-public-repositories)), Claude Code answered sooner with the graph on all
    four and made fewer model calls in all 12 rounds, but the server's tool definitions and instructions made each
    session start 8,681 tokens larger, and every model call carries them. So the median input tokens and cost were
    higher on all four. Shorter tool descriptions would shrink that start; how much has not been measured.
    Offering no tools was measured in 1.6.0: with `CODEGRAPH_MODE=lite-cli` and the `codegraph` CLI, Claude Code
    used 24% fewer tokens than with the tools on single lookups and 61% fewer on analytical questions, on one
    repository with one model ([measured](measured-results.md#code-mode-lite)). Still open: the CLI's instructions
    are in the benchmark's profile, not set up by `init`; no other assistant and no other repository was measured;
    and the [question log](../MCP-SERVER.md#question-log-opt-in-claude-code) has not yet recorded real sessions
    to show which questions are single lookups.

## How to help

Pick an item, open an issue to say so, and read [Contributing](contributing.md). The corpus and the test
suite will tell you when you are done.

---

← Previous: [The story](story.md) · Next: [Contributing](contributing.md) →
