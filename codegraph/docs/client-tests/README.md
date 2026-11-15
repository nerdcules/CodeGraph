# Client tests

*Author: Alan Seunarayan*

[Home](../README.md) · [Installation](../installation.md#tested-with) · **Client tests** · [Claude Code](claude-code.md) · [GitHub Copilot in VS Code](copilot-vscode.md) · [GitHub Copilot CLI](copilot-cli.md) · [Codex VS Code extension](codex-vscode.md) · [Codex CLI](codex-cli.md) · [Cursor IDE](cursor-ide.md) · [Cursor CLI](cursor-cli.md)

The evidence behind [Tested with](../installation.md#tested-with): one page for each AI coding assistant
that answered from the graph with real tool calls, on 14 November 2026.

Each one ran on a clone of this repository (3,119 nodes), with the configuration that `codegraph-index init --client`
writes. The server and `init` were built from uncommitted work on the any-mcp-client branch on 14 November 2026,
on top of release 1.1.0 (`2e1799b`), so no commit matches that build exactly; the work was first committed as
`2d13546`, with later changes. Release 1.1.0 itself has neither `--root` nor `init --client`, which the VS Code and
Cursor configurations need. Each one got the same question:

> What breaks if I change `GraphStore.Replace`, and which tests cover it?

In every session below, the `impact_of` call returned 12 dependents and 37 guarding tests, and the final answer
reported the 37 tests. Some answers also repeat the 12 dependents; the excerpts show each one's own words.

37 is what that build's graph found. At run time, 44 tests execute `GraphStore.Replace`, and the 37 are all among
them ([why it saves tokens](../why-it-saves-tokens.md)). A later change on the branch counts the tests that call it
from their test class's constructor or setup, and the same question now answers 42 guarding tests (see the
[changelog](../../CHANGELOG.md)). The two still missed run the indexer as a separate process.

These pages show that each assistant can use the graph. They do not compare time or tokens: for runs with and
without CodeGraph in four of these assistants, see
[four assistants, with and without](../why-it-saves-tokens.md#four-assistants-with-and-without).

| Assistant | Version | Time to answer |
|---|---|---|
| [Claude Code](claude-code.md) | CLI 2.1.325 (`claude -p`) | 31 s, 0 permission denials |
| [GitHub Copilot in VS Code](copilot-vscode.md) | VS Code 1.146.0, Copilot agent host 1.0.108; model Auto (Copilot Fusion chose GPT-6 Luna) | about 45 s |
| [GitHub Copilot CLI](copilot-cli.md) | 1.0.112; model Claude Sonnet 5.5 | 15 s |
| [Codex VS Code extension](codex-vscode.md) | Extension 26.1111.21308 (bundled Codex 0.169.0-alpha.11.1); model GPT-6.1 Sol, effort low | about 26 s; every lookup through codegraph, no shell commands |
| [Codex CLI](codex-cli.md) | 0.169.0; model GPT-6.1 Sol | 42 s |
| [Cursor IDE](cursor-ide.md) | Cursor 3.29.15; model Grok 4.7 High | about 60 s |
| [Cursor CLI](cursor-cli.md) | cursor-agent 2026.11.13; model Auto | 22 s |

Each page shows the configuration, any trust or approval step, the tool calls the assistant made, and a short
excerpt of its answer. The excerpts come from each assistant's own session log. Those logs are not kept in this
repository, because they hold local paths, session ids and account names; the pages leave those out.

## The configuration files

The [config](config/) folder holds each file in the form `init` writes it, with `/home/you/.codegraph/bin/codegraph-mcp` as the command.
`init` writes the real absolute path of your server there: replace `/home/you` with your home folder if you copy a
file by hand. These files do not expand `~`. `init` also adds `DOTNET_ROOT` to `env` when the default .NET cannot
run the server.

| File | Read by |
|---|---|
| [mcp.json](config/mcp.json) | Claude Code and the Copilot CLI, as `<repo>/.mcp.json` |
| [vscode-mcp.json](config/vscode-mcp.json) | Copilot in VS Code, as `<repo>/.vscode/mcp.json` |
| [cursor-mcp.json](config/cursor-mcp.json) | Cursor IDE and CLI, as `<repo>/.cursor/mcp.json` |
| [cursor-cli.json](config/cursor-cli.json) | Cursor CLI headless runs, as `<repo>/.cursor/cli.json` (written by hand) |
| [codex-config.toml](config/codex-config.toml) | Codex CLI and extension, in `~/.codex/config.toml` |

## Not tested yet

`init` also writes the configuration for Copilot in Visual Studio and JetBrains IDEs, the Gemini CLI, Roo Code,
Kilo Code and Continue. Those files follow each product's documentation and source code, but no assistant has
answered through them yet.

---

Up: [Installation](../installation.md#tested-with) · Next: [Claude Code](claude-code.md) →
