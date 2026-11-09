# CodeGraph

The project lives in [`codegraph/`](codegraph/) — see [codegraph/README.md](codegraph/README.md)
for build, test, indexing and Claude Code setup.

Quick start, with the .NET 10 SDK installed:

```bash
git clone https://github.com/nerdcules/CodeGraph && cd CodeGraph/codegraph
./install.sh ~/dev/YourApp --add-to-path      # Windows: .\install.ps1 C:\dev\YourApp -AddToPath
```

That installs `codegraph-index` and `codegraph-mcp`, writes the server into the repo's `.mcp.json`,
and builds its first graph. Later repos: run `codegraph-index init` inside each one.
