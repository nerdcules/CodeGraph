# GitHub Copilot in VS Code

*Author: Alan Seunarayan*

[Home](../README.md) · [Installation](../installation.md#tested-with) · [Client tests](README.md) · [Claude Code](claude-code.md) · **GitHub Copilot in VS Code** · [GitHub Copilot CLI](copilot-cli.md) · [Codex VS Code extension](codex-vscode.md) · [Codex CLI](codex-cli.md) · [Cursor IDE](cursor-ide.md) · [Cursor CLI](cursor-cli.md)

| | |
|---|---|
| Version | VS Code 1.146.0, Copilot agent host 1.0.108; model Auto (Copilot Fusion chose GPT-6 Luna) |
| Run as | Agent mode |
| Time to answer | about 45 s |
| Result | `impact_of`: 12 dependents, 37 guarding tests; the answer reports the 37 tests |

## Setup

`<repo>/.vscode/mcp.json` ([copy](config/vscode-mcp.json)):

```json
{
  "servers": {
    "codegraph": {
      "type": "stdio",
      "command": "/home/you/.codegraph/bin/codegraph-mcp",
      "args": ["--root", "${workspaceFolder}"]
    }
  }
}
```

VS Code replaces `${workspaceFolder}` with the open folder, so the server reads that folder's graph.

## Trust and approval

VS Code's own workspace trust (Restricted Mode) for the folder. No prompt for the server or its tools.

## The question

> What breaks if I change `GraphStore.Replace`, and which tests cover it?

## Tool calls

1. `tool_search_tool` (Copilot finds MCP tools on demand)
2. `codegraph-find_symbol` `GraphStore.Replace`
3. `codegraph-impact_of`
4. `codegraph-tests_for`
5. one file `view`

## Answer, first lines

> Changing `GraphStore.Replace` can affect **12 dependent methods and 37 guarding tests**, according to codegraph.

## Notes

- Tools appear as `codegraph-<tool>`.
- The agent host starts the server itself. Its log is in `~/.copilot/session-state/<session>/events.jsonl`, not in VS Code's MCP output channel, which stays empty.
- `index_status` reported the open folder's `.codegraph/graph.db`, so `${workspaceFolder}` was expanded.

---

← Previous: [Claude Code](claude-code.md) · Next: [GitHub Copilot CLI](copilot-cli.md) →
