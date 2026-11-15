# Codex VS Code extension

*Author: Alan Seunarayan*

[Home](../README.md) · [Installation](../installation.md#tested-with) · [Client tests](README.md) · [Claude Code](claude-code.md) · [GitHub Copilot in VS Code](copilot-vscode.md) · [GitHub Copilot CLI](copilot-cli.md) · **Codex VS Code extension** · [Codex CLI](codex-cli.md) · [Cursor IDE](cursor-ide.md) · [Cursor CLI](cursor-cli.md)

| | |
|---|---|
| Version | Extension 26.1111.21308 (bundled Codex 0.169.0-alpha.11.1); model GPT-6.1 Sol, effort low |
| Run as | A chat in the Codex panel, approval mode "Ask for approval" |
| Time to answer | about 26 s; every lookup through codegraph, no shell commands |
| Result | `impact_of`: 12 dependents, 37 guarding tests; the answer reports the 37 tests |

## Setup

`~/.codex/config.toml` ([copy](config/codex-config.toml)):

```toml
[mcp_servers.codegraph]
command = "/home/you/.codegraph/bin/codegraph-mcp"
```

One global entry serves every repository: the extension starts the server in the open folder, and the server finds that folder's graph. No `--root` is needed.

## Trust and approval

None. The tools are read-only, so they ran without approval even in "Ask for approval" mode.

## The question

> What breaks if I change `GraphStore.Replace`, and which tests cover it?

## Tool calls

1. `find_symbol` `GraphStore.Replace`
2. `tests_for`
3. `covered_by`
4. `impact_of`
5. `snippet`
6. `impact_of`, `covered_by` again

## Answer, first lines

> Codegraph reports **12 dependents and 37 guarding test methods: 25 direct, 12 transitive**.

## Notes

- A first prompt asked for `index_status`. It returned the open folder's `.codegraph/graph.db`.

---

← Previous: [GitHub Copilot CLI](copilot-cli.md) · Next: [Codex CLI](codex-cli.md) →
