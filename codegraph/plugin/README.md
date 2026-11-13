# CodeGraph plugin for Claude Code

One install, every repository: the plugin adds the `codegraph` MCP server to Claude Code and a skill
that tells Claude when to use it. On the first call it installs the latest CodeGraph release into
`~/.codegraph/bin`, so there is nothing to download by hand.

```
/plugin marketplace add nerdcules/CodeGraph
/plugin install codegraph@codegraph
```

From a shell: `claude plugin marketplace add nerdcules/CodeGraph && claude plugin install codegraph@codegraph --scope user`.

Then open a repository and ask a question. When a tool answers "no code graph", let Claude run
`~/.codegraph/bin/codegraph-index init`, or run it yourself; that writes the repo's `.mcp.json`, ignores
`.codegraph/` and builds the graph. C# repositories need the .NET SDK they build with; TypeScript needs
Node.js 22; Java, Go, Rust and Python need their SCIP indexer on PATH
([installation](../docs/installation.md#install-the-language-tools)).

## What is in it

| File | Function |
|---|---|
| `.mcp.json` | The `codegraph` server, started through `bin/codegraph-mcp`. |
| `bin/codegraph-mcp` | A launcher: installs the release if `~/.codegraph/bin/codegraph-mcp` is missing, moves to the project directory, starts the server. `CODEGRAPH_HOME` overrides the folder. |
| `skills/codegraph/SKILL.md` | When to use the tools, the typical turn, what to do about a missing or stale graph. |

The server shows in `/mcp` as `plugin:codegraph:codegraph`; its tools are `mcp__plugin_codegraph_codegraph__<tool>`.

## Windows

A plugin's MCP command is a shell script, so on Windows install CodeGraph with `install.ps1` first and
let `codegraph-index init` write the repo's `.mcp.json`. The skill still helps once the server is there.

## Updating

The plugin pins no binary version: the launcher installs the latest release once and leaves it. Re-run
the install one-liner to upgrade the executables, and `/plugin update codegraph@codegraph` to update the
plugin itself.

## Testing a change to the plugin

```bash
claude --plugin-dir codegraph/plugin
claude plugin validate codegraph/plugin
```
