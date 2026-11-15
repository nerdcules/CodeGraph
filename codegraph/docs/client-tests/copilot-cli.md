# GitHub Copilot CLI

*Author: Alan Seunarayan*

[Home](../README.md) · [Installation](../installation.md#tested-with) · [Client tests](README.md) · [Claude Code](claude-code.md) · [GitHub Copilot in VS Code](copilot-vscode.md) · **GitHub Copilot CLI** · [Codex VS Code extension](codex-vscode.md) · [Codex CLI](codex-cli.md) · [Cursor IDE](cursor-ide.md) · [Cursor CLI](cursor-cli.md)

| | |
|---|---|
| Version | 1.0.112; model Claude Sonnet 5.5 |
| Run as | `copilot -p` |
| Time to answer | 15 s |
| Result | `impact_of`: 12 dependents, 37 guarding tests; the answer reports the 37 tests |

## Setup

`<repo>/.mcp.json` ([copy](config/mcp.json)):

```json
{
  "mcpServers": {
    "codegraph": {
      "command": "/home/you/.codegraph/bin/codegraph-mcp"
    }
  }
}
```

The Copilot CLI reads `.mcp.json` only in a trusted folder.

## Trust and approval

None with `--trust-folder --allow-tool codegraph`.

## The question

> What breaks if I change `GraphStore.Replace`, and which tests cover it?

## Tool calls

1. `tool_search_tool`
2. `codegraph-find_symbol` `GraphStore.Replace`, exact name
3. `codegraph-impact_of`
4. `codegraph-tests_for`

## Answer, first lines

> `GraphStore.Replace` (`GraphStore.cs:269-355`) has 12 dependents and 37 guarding tests, according to `impact_of`.

## Notes

- Command: `copilot -p "<question>" --trust-folder --allow-tool codegraph`. `--trust-folder` trusts the folder for this one session and saves nothing.
- Without trust the CLI ignores `.mcp.json` without a warning. The model then says it has no codegraph tools and greps instead. To trust a folder for good, add it to `trustedFolders` in `~/.copilot/config.json`.

---

← Previous: [GitHub Copilot in VS Code](copilot-vscode.md) · Next: [Codex VS Code extension](codex-vscode.md) →
