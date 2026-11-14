# Codex CLI

*Author: Alan Seunarayan*

[Docs](../README.md) · [Installation](../installation.md#tested-with) · [Client tests](README.md) · [Claude Code](claude-code.md) · [GitHub Copilot in VS Code](copilot-vscode.md) · [GitHub Copilot CLI](copilot-cli.md) · [Codex VS Code extension](codex-vscode.md) · **Codex CLI** · [Cursor IDE](cursor-ide.md) · [Cursor CLI](cursor-cli.md)

| | |
|---|---|
| Version | 0.169.0; model GPT-6.1 Sol |
| Run as | `codex exec`, approval policy never |
| Time to answer | 42 s |
| Result | `impact_of`: 12 dependents, 37 guarding tests; the answer reports the 37 tests |

## Setup

`~/.codex/config.toml` ([copy](config/codex-config.toml)):

```toml
[mcp_servers.codegraph]
command = "/home/you/.codegraph/bin/codegraph-mcp"
```

`codegraph-index init --client codex` adds this entry with `codex mcp add`.

## Trust and approval

None.

## The question

> What breaks if I change `GraphStore.Replace`, and which tests cover it?

## Tool calls

1. Codex calls MCP tools from its code tool (`exec`) as `mcp__codegraph__<tool>`
2. `find_symbol` `GraphStore.Replace`
3. `impact_of`: 12 dependents, 37 guarding tests
4. `tests_for` and `covered_by`
5. `callers_of`, `snippet`

## Answer, first lines

> `GraphStore.Replace` is shared by C# indexing and graph imports. Codegraph reports **37 reachable tests: 25 direct and 12 indirect**.

## Notes

- Because the calls run inside `exec`, the session log has no `mcp_tool_call` events for them. The calls and their results are in the `exec` input and output.

---

← Previous: [Codex VS Code extension](codex-vscode.md) · Next: [Cursor IDE](cursor-ide.md) →
