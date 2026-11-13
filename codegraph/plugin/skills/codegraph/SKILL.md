---
name: codegraph
description: Structural questions about the code from the compiler-built graph. Use before changing shared code, and whenever asked who calls something, what breaks if it changes, which tests guard it, who implements an interface, what is dead, or where a symbol is. Prefer it to grep for these questions.
---

# CodeGraph

The `codegraph` MCP server answers from a graph the compiler built, so every edge is a resolved symbol.
Prefer its tools to grepping or opening files for structural questions, and open source only for the
members you are going to change.

## The typical turn

1. `find_symbol` (or `search_code` when you only know what the code does) to get a node id.
2. `impact_of`, `callers_of`, `references_to` or `tests_for` with that id.
3. `snippet` on the two or three declarations that matter.

Ids in `[brackets]` are opaque; pass them back verbatim. An answer that starts with `⚠ STALE INDEX` comes
from a graph older than the working tree; rows ending in `STALE` come from edited files. Call `impact_of`
before changing anything shared, and `tests_for` or `covered_by` to find the tests that guard it.

## When there is no graph

A tool that answers "no code graph at ..." means the repository has not been indexed. Build the graph
once, from the repository root (it writes `.mcp.json` and `.gitignore` there and indexes):

```bash
~/.codegraph/bin/codegraph-index init
```

On Windows: `C:\tools\codegraph\codegraph-index.exe init`. Re-run `~/.codegraph/bin/codegraph-index <target>`
after large changes, or start `codegraph-index <target> --watch` to keep the graph current on every save
and enable the live tools (`check_change`, `diagnostics`, `rename_preview`, `live_references`).

## When the graph is stale

Trust the `STALE` marks: re-index, or read the marked files. `index_status` says what changed and which
compiler read each language.
