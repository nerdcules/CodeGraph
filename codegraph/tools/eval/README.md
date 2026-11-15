# With and without CodeGraph

[CodeGraph](../../README.md) · [Docs](../../docs/README.md) · [Contributing](../../docs/contributing.md) · [Corpus](../corpus/README.md) · [Older toolchains](../corpus/toolchains/README.md) · **With and without**

Four AI coding assistants got the same question about this repository, three times with the codegraph server and
three times without it, on 14 November 2026. This folder holds the answer key, the scripts, and one row for each
of the 24 runs. [Why it saves tokens](../../docs/why-it-saves-tokens.md#four-assistants-with-and-without) has the
summary.

The question:

> What breaks if I change GraphStore.Replace, and which tests cover it?

## The answer key

The key does not come from CodeGraph, so the test is not circular.

- **Tests.** A copy of the repository had one probe at the top of `GraphStore.Replace`: if `CG_HIT` is set, append
  the call stack to that file. Each test method then ran alone (`CodeGraph.Tests -method <name>`), four at a
  time, and a test counts when its file exists. Child processes inherit `CG_HIT`, so tests that start the indexer
  as a process count too. All tests passed.
  - On release 1.1.0 (commit `2e1799b`): 44 of 300 test methods execute `Replace`.
  - On the evaluated snapshot, this branch's code: the same 44, of 311.
  - [runtime-tests.txt](answer-key/runtime-tests.txt) lists them.
- **What the graph said.** `impact_of` named 37 guarding tests with the 1.1.0 indexer
  ([list](answer-key/codegraph-1.1.0-style-37.txt)), all among the 44. After the change that counts a test
  class's constructor and setup methods, it names 42 on the evaluated snapshot
  ([list](answer-key/codegraph-with-setup-42.txt)). The 2 it cannot see start `codegraph-index` as a child
  process. Two tests added to `BulkWriteTests` after the snapshot also reach `Replace` in the graph; the run-time
  check has not been repeated on them.
- **Production callers.** The stacks show two: `GraphImport.WriteAsync` and the indexer's `Program` (its
  `IndexAsync` local function).

## How the runs were made

- **Repository.** A snapshot of this branch with every Markdown file, the docs, the plugins, the corpus and the
  images removed, so no answer could be read from the documentation. One git commit. The "with" copy holds the
  graph (3,170 nodes); the "without" copy has no `.codegraph/` folder.
- **Server.** The Debug build of this branch's `codegraph-mcp`, which includes the setup-method change.
- **Order.** Three rounds. In each round every assistant ran once with and once without; odd rounds ran "with"
  first, even rounds "without" first.
- **Assistants.** Each one had a fixed model, and the same tool permissions in both conditions.

| Assistant | Model | With CodeGraph | Without |
|---|---|---|---|
| Claude Code 2.1.325 | `claude-opus-5-5`, effort high | `--strict-mcp-config` with the codegraph server | `--strict-mcp-config` with no server |
| Codex CLI 0.169.0 | `gpt-6.1-sol` | its global codegraph entry, read-only sandbox | `-c mcp_servers.codegraph.enabled=false` |
| GitHub Copilot CLI 1.0.112 | `claude-sonnet-5.5` | `--additional-mcp-config`, `--allow-tool codegraph` | no MCP config |
| Cursor CLI 2026.11.13 | `composer-2.5` | `.cursor/mcp.json`, `Mcp(codegraph:*)` allowed | no `.cursor/mcp.json` |

The other MCP servers of Codex and the Copilot CLI stayed as configured in both conditions.

## How the answers were scored

The rules were written before any run.

- **Stated test total.** The number of tests the answer says exercise the method, read by hand from each
  answer. A count of call sites or files is not a test total; the note column quotes those.
- **Real tests named.** How many of the 44 test method names appear in the answer.
- **Tests named that do not run it.** Test method names in the answer that are not among the 44.
- **Both production callers.** Whether the answer names `GraphImport` and `Program` or `Main`.
- **Usage.** Input tokens, cache reads included, as each CLI reports them, and Claude Code's cost. The Copilot CLI
  reports AI units, not tokens. Units differ between CLIs, so compare within one assistant only.

[results/runs.csv](results/runs.csv) has one row for each run. The raw logs are not kept here, because they hold
local paths.

## Files

| File | What it does |
|---|---|
| [run.py](run.py) | One run: `run.py <claude\|codex\|copilot\|cursor> <on\|off> <n>`. Needs `CODEGRAPH_MCP`, the server to test. The 24 runs used an earlier copy of this script that read the server from config files (`cfg/*.json`) naming the Debug build of `codegraph-mcp`; the commands were otherwise the same. |
| [batch.sh](batch.sh) | The 24 runs, in the order above. |
| [analyze.py](analyze.py) | A table of the runs, and medians for each assistant and condition. `--answers` prints every answer. |
| [answer-key/](answer-key/) | The 44 tests from the run-time check, and the graph's 37 and 42. |
| [results/runs.csv](results/runs.csv) | The 24 runs, scored. |

## Limits

- One question, one repository, three runs for each assistant and condition. The ranges are wide; read the
  medians as a direction, not a precise figure.
- The models differ between assistants, so the assistants cannot be compared with each other.
- In one Cursor run with CodeGraph, the 7 calls that take a node id all failed: the model passed `symbolId` where
  the tools take `id`, and the server answered only "An error occurred". Its `find_symbol`, `search_code` and
  `index_status` calls worked. The server now accepts those names. The run
  is counted as it happened.

---

[CodeGraph](../../README.md) · [Docs](../../docs/README.md) · [Contributing](../../docs/contributing.md) · [Corpus](../corpus/README.md) · [Older toolchains](../corpus/toolchains/README.md) · **With and without**
