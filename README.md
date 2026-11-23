# CodeGraph

**Overview** · [CodeGraph](codegraph/README.md) · [Docs](codegraph/docs/README.md) · [MCP server](codegraph/MCP-SERVER.md) · [Extractors](codegraph/EXTRACTORS.md) · [Benchmarks](codegraph/BENCHMARKS.md) · [Prompts](codegraph/PROMPTS.md) · [Changelog](codegraph/CHANGELOG.md) · [Claude Code plugin](codegraph/plugin/README.md) · [VS Code extension](codegraph/vscode-extension/README.md)

*Idea: [nerdcules](https://github.com/nerdcules) · Written by: Claude, in [Claude Code](https://claude.com/claude-code) · [How it was built](codegraph/docs/story.md)*

**CodeGraph gives your AI coding assistant the compiler's answers: who calls this, what breaks if I
change it, which tests guard it.**

It works with any MCP client, and sets itself up for Claude Code, GitHub Copilot (CLI, VS Code, Visual
Studio, JetBrains), Codex, Cursor, the Gemini CLI, and the Roo Code, Kilo Code and Continue extensions.
[Tested with](codegraph/docs/installation.md#tested-with) Claude Code, the Copilot CLI, Copilot in VS Code, Codex and
Cursor.

[![CI](https://github.com/nerdcules/CodeGraph/actions/workflows/ci.yml/badge.svg)](https://github.com/nerdcules/CodeGraph/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/nerdcules/CodeGraph?label=release)](https://github.com/nerdcules/CodeGraph/releases/latest)

[![A real Claude Code session on 14 November 2026, replayed from its transcript: asked what breaks if GraphStore.Replace changes, Claude calls find_symbol, impact_of, tests_for and snippet, and answers that 37 tests guard it, in 42 seconds](codegraph/docs/images/promo-answer.gif)](codegraph-promo.mp4)

*A real session, replayed from its transcript. Click for the video with sound: the same question with CodeGraph on (42 s, "37 tests guard it") and off (87 s, "about 40"). A later run-time check found 44 tests that execute the method ([why](codegraph/docs/measured-results.md#in-a-real-session)).*

It reads a codebase with its compiler (C#, TypeScript and Angular, Java, Go, Rust, Python), stores
what it finds as a graph, and serves that graph to your assistant as MCP tools. In the session above, the
assistant answered with four codegraph calls in 42 seconds; without CodeGraph it ran eight shell commands, took
87 seconds and estimated "about 40" tests. On four public repositories in Go, Python, TypeScript and C#, Claude
Code answered sooner with CodeGraph on all four, in fewer model calls, at a higher token cost
([measured](codegraph/docs/measured-results.md#four-public-repositories)). From release 1.6.0 a `codegraph` command-line
tool reads the same graph from the shell; with it in place of the MCP tools, Claude Code used fewer tokens on single
lookups (−24%) and analytical questions (−61%) in one benchmark of one repository
([measured](codegraph/docs/measured-results.md#code-mode-lite)).

## Install

One command. Run it in your repository; it asks what it needs and shows the plan before it changes anything.

```bash
curl -fsSL https://raw.githubusercontent.com/nerdcules/CodeGraph/main/codegraph/install.sh | bash
```

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/nerdcules/CodeGraph/main/codegraph/install.ps1)))
```

It asks for the install folder, the repositories to set up, the assistants to set them up for (a list:
Claude Code is picked, and so are Cursor, the Gemini CLI and Codex when it finds them), the live daemon, PATH,
and whether to add the [VS Code extension](codegraph/vscode-extension/README.md). Then start your assistant in the
repository and ask what breaks if you change something.

The VS Code extension installs CodeGraph, sets repositories up, runs each graph's live daemon, and shows each
graph's state. Release 1.3.0 is the first full release that carries it.

To skip the questions, give the answers and `--yes` (`-Yes`):
`curl ... | bash -s -- ~/dev/YourApp --add-to-path --yes`.
[Installation](codegraph/docs/installation.md) has every option, upgrade and removal.

To update, run the install command again, or use [`nerdcules-update`](https://github.com/nerdcules/nerdcules#updates):
`nerdcules-update apply codegraph`, and `nerdcules-update rollback codegraph` to go back.

The project lives in [codegraph/](codegraph/). Its [README](codegraph/README.md) shows the first five
minutes, the questions you can ask, and where to go next depending on who you are; the
[docs](codegraph/docs/README.md) have the rest, including [the story](codegraph/docs/story.md) of
how a developer and an AI assistant built this.

![Architecture: extractors write graph.db, the MCP server reads it, the live daemon keeps it current](codegraph/docs/images/architecture.svg)

---

**Overview** · [CodeGraph](codegraph/README.md) · [Docs](codegraph/docs/README.md) · [MCP server](codegraph/MCP-SERVER.md) · [Extractors](codegraph/EXTRACTORS.md) · [Benchmarks](codegraph/BENCHMARKS.md) · [Prompts](codegraph/PROMPTS.md) · [Changelog](codegraph/CHANGELOG.md) · [Claude Code plugin](codegraph/plugin/README.md) · [VS Code extension](codegraph/vscode-extension/README.md)
