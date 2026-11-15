# Claude Code

*Author: Alan Seunarayan*

[Home](../README.md) · [Installation](../installation.md#tested-with) · [Client tests](README.md) · **Claude Code** · [GitHub Copilot in VS Code](copilot-vscode.md) · [GitHub Copilot CLI](copilot-cli.md) · [Codex VS Code extension](codex-vscode.md) · [Codex CLI](codex-cli.md) · [Cursor IDE](cursor-ide.md) · [Cursor CLI](cursor-cli.md)

| | |
|---|---|
| Version | CLI 2.1.325 (`claude -p`) |
| Run as | Claude Opus 5.5, effort high |
| Time to answer | 31 s, 0 permission denials |
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

Claude Code asks once to approve the project's servers. To skip the dialog in a script, `.claude/settings.local.json` can hold `"enabledMcpjsonServers": ["codegraph"]` and `"permissions": {"allow": ["mcp__codegraph"]}`.

## Trust and approval

None during the run, once the server was approved.

## The question

> What breaks if I change `GraphStore.Replace`, and which tests cover it?

## Tool calls

1. `ToolSearch` (loads the codegraph tool schemas)
2. `find_symbol` `GraphStore.Replace`, exact name
3. `impact_of`: 12 dependents, 37 guarding tests
4. `tests_for`
5. `snippet`

## Answer, first lines

> Short answer: `Replace` is the only way an index run writes the graph. [...] About 37 tests guard it, and they cover it well.

## Notes

- The VS Code extension (2.1.323) also answered from the graph on 13 November, on the older graph of that day (2,885 nodes), so its count was that graph's: 33 tests. It is not counted in the 12 and 37 above, which come from the run of 14 November on the 3,119-node graph.

---

← Previous: [Client tests](README.md) · Next: [GitHub Copilot in VS Code](copilot-vscode.md) →
