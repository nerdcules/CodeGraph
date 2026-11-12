# CodeGraph documentation

*Author: Alan Seunarayan*

**Home** · [Installation](installation.md) · [Usage](usage.md) · [Use cases](use-cases.md) · [How it works](how-it-works.md) · [Performance](performance.md) · [Architecture](architecture.md)

---

## Contents

- [What CodeGraph is](#what-codegraph-is)
- [The pages](#the-pages)
- [Supported languages](#supported-languages)
- [Conventions in these pages](#conventions-in-these-pages)
- [Other reference pages](#other-reference-pages)

## What CodeGraph is

CodeGraph is a code graph and an MCP server for Claude Code.
The indexer reads your code with the compiler of the language.
It writes the types, members, calls, overrides and tests to a graph file.
The MCP server answers questions from that file.

An AI assistant can then ask "who calls this method" or "what breaks if I change this".
It gets a short, exact answer from the graph.
It does not have to read many source files to find the answer.

![Architecture: extractors write graph.db, the MCP server reads it, the live daemon keeps it current](images/architecture.svg)

## The pages

| Page | Read it to |
|---|---|
| [Installation](installation.md) | Install CodeGraph and connect a repository to Claude Code. |
| [Usage](usage.md) | Build a graph, keep it current, and use the tools. |
| [Use cases](use-cases.md) | Find the tool for a question about your code. |
| [How it works](how-it-works.md) | Learn how the indexer builds the graph and how the server answers. |
| [Performance](performance.md) | See the measured times for indexing and for tool calls. |
| [Architecture](architecture.md) | Learn the components, the data model and the extension points. |

## Supported languages

| Language | Route | Live tools |
|---|---|---|
| C# | Roslyn, in process | Roslyn |
| TypeScript, JavaScript, Angular | codegraph-ts extractor (Node.js) | typescript-language-server |
| Java, Kotlin, Scala | scip-java | jdtls, when it is on PATH |
| Go | scip-go | gopls, when it is on PATH |
| Rust | rust-analyzer | rust-analyzer |
| Python | scip-python | pyright, when it is on PATH |

One graph can hold more than one language.
For example, a .NET API and its Angular front end can share one graph.

## Conventions in these pages

These pages use ASD-STE100 Simplified Technical English.
Sentences are short, and each instruction is one action.
A word has one meaning in all pages:

| Term | Meaning |
|---|---|
| the indexer | The `codegraph-index` executable. |
| the server | The `codegraph-mcp` executable, an MCP server. |
| the graph | The `.codegraph/graph.db` file of a worktree. |
| the live daemon | The indexer when it runs with `--watch`. |
| the client | Claude Code, or another MCP client. |
| a node | One type, member, file or project in the graph. |
| an edge | One relation between two nodes, for example `Calls`. |

Commands for macOS and Linux use bash.
Commands for Windows use PowerShell.

## Other reference pages

- [MCP-SERVER.md](../MCP-SERVER.md) gives the arguments of all 35 tools.
- [EXTRACTORS.md](../EXTRACTORS.md) gives the record format for a new language.
- [BENCHMARKS.md](../BENCHMARKS.md) gives all the measurements, round by round.
- [PROMPTS.md](../PROMPTS.md) gives prompts that make the client use the tools.
- [tools/corpus/README.md](../tools/corpus/README.md) describes the generated test codebases.

---

**Home** · Next: [Installation](installation.md) →
