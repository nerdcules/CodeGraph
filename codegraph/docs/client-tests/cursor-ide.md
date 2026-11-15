# Cursor IDE

*Author: Alan Seunarayan*

[Home](../README.md) · [Installation](../installation.md#tested-with) · [Client tests](README.md) · [Claude Code](claude-code.md) · [GitHub Copilot in VS Code](copilot-vscode.md) · [GitHub Copilot CLI](copilot-cli.md) · [Codex VS Code extension](codex-vscode.md) · [Codex CLI](codex-cli.md) · **Cursor IDE** · [Cursor CLI](cursor-cli.md)

| | |
|---|---|
| Version | Cursor 3.29.15; model Grok 4.7 High |
| Run as | A chat in Agent mode |
| Time to answer | about 60 s |
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

Cursor replaces `${workspaceFolder}` with the open folder.

## Trust and approval

Cursor lists a new project server as **Disabled** and does not ask about it. Enable it once in Settings, Tools & MCPs (command palette: *Open MCPs*): click the codegraph row. It then shows "35 tools enabled". After that, no prompt for any call.

## The question

> What breaks if I change `GraphStore.Replace`, and which tests cover it?

## Tool calls

1. `GetDynamicTools` (Cursor finds MCP tools on demand, namespace `project-0-<folder>-codegraph`)
2. `index_status`
3. `find_symbol` `GraphStore.Replace` (an exact-name search, then two broader ones)
4. `impact_of`
5. `tests_for`

## Answer, first lines

> `impact_of` on `GraphStore.Replace` reports **12 dependents** and **37 guarding tests**.

## Notes

- While the server is disabled, the agent says the tool catalog does not list codegraph and searches files instead.
- `cursor-agent mcp enable codegraph` approves the server for the CLI only. The IDE keeps its own setting.
- The switch can be reached with the mouse only, not the keyboard.

---

← Previous: [Codex CLI](codex-cli.md) · Next: [Cursor CLI](cursor-cli.md) →
