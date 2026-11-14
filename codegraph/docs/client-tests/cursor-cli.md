# Cursor CLI

*Author: Alan Seunarayan*

[Docs](../README.md) · [Installation](../installation.md#tested-with) · [Client tests](README.md) · [Claude Code](claude-code.md) · [GitHub Copilot in VS Code](copilot-vscode.md) · [GitHub Copilot CLI](copilot-cli.md) · [Codex VS Code extension](codex-vscode.md) · [Codex CLI](codex-cli.md) · [Cursor IDE](cursor-ide.md) · **Cursor CLI**

| | |
|---|---|
| Version | cursor-agent 2026.11.13; model Auto |
| Run as | `cursor-agent -p --trust` |
| Time to answer | 22 s |
| Result | `impact_of`: 12 dependents, 37 guarding tests; the answer reports the 37 tests |

## Setup

`<repo>/.cursor/mcp.json` ([copy](config/cursor-mcp.json)):

```json
{
  "mcpServers": {
    "codegraph": {
      "command": "/home/you/.codegraph/bin/codegraph-mcp",
      "args": ["--root", "${workspaceFolder}"]
    }
  }
}
```

`<repo>/.cursor/cli.json` ([copy](config/cursor-cli.json)):

```json
{
  "permissions": {
    "allow": ["Mcp(codegraph:*)"],
    "deny": []
  }
}
```

The CLI does not replace `${workspaceFolder}`. It starts the server in the project, and the server then ignores the unexpanded value.

## Trust and approval

`cursor-agent mcp enable codegraph` once ("Enabled and approved"). Without `--trust`, `-p` exits and asks for it. Without the `Mcp(codegraph:*)` rule in `.cursor/cli.json`, every call is refused: `User rejected MCP: codegraph-index_status`.

## The question

> What breaks if I change `GraphStore.Replace`, and which tests cover it?

## Tool calls

1. `index_status`
2. `find_symbol` `GraphStore.Replace`
3. `impact_of`, `tests_for`

## Answer, first lines

> `impact_of` reports **12 dependents** and **37 guarding tests**.

## Notes

- Cursor ignores the tools' read-only hint, so headless runs need the allow rule. `--force` also works, but it approves every shell command too. `Mcp(codegraph:*)` allows only codegraph's tools.
- Before the allow rule, the agent spent 198 s grepping and quoted the README instead of the graph.

---

← Previous: [Cursor IDE](cursor-ide.md) · Next: [Usage](../usage.md) →
