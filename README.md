# CodeGraph

A compiler-built code graph with an MCP server for Claude Code, and a live daemon that answers
from the compiled solution. An optional Obsidian plugin shows the same graph to people as linked
notes; CodeGraph doesn't need it. The project lives in [`codegraph/`](codegraph/);
[codegraph/README.md](codegraph/README.md) explains what it does, with diagrams and screenshots,
and links to the guides.

Why: without a graph, an AI assistant finds callers and tests by searching text and opening
files, and everything it opens costs tokens. CodeGraph works out those relationships once, with
the compiler, and answers each question with only the rows that answer it. On this repository,
"what breaks if I change `GraphStore.Replace`?" takes about 5,300 tokens of answers, where
opening the 23 files that match `.Replace(` would take about 105,000.
[Why it saves tokens](codegraph/README.md#why-it-saves-tokens) has the details.

![Architecture: extractors write graph.db, the MCP server reads it, the live daemon keeps it current](codegraph/docs/images/architecture.svg)

Quick start, with the .NET 10 SDK installed:

```bash
git clone https://github.com/nerdcules/CodeGraph && cd CodeGraph/codegraph
./install.sh ~/dev/YourApp --add-to-path      # Windows: .\install.ps1 C:\dev\YourApp -AddToPath
```

That installs `codegraph-index` and `codegraph-mcp`, writes the server into the repo's `.mcp.json`,
and builds its first graph. Later repos: run `codegraph-index init` inside each one. Add
`--obsidian-vault <vault>` only if you also want the Obsidian plugin.
