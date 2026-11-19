# With and without CodeGraph

[CodeGraph](../../README.md) · [Docs](../../docs/README.md) · [Contributing](../../docs/contributing.md) · [Corpus](../corpus/README.md) · [Older toolchains](../corpus/toolchains/README.md) · **With and without**

Four AI coding assistants got the same question about this repository, three times with the codegraph server and
three times without it, on 14 November 2026. This folder holds the answer key, the scripts, and one row for each
of the 24 runs. [Why it saves tokens](../../docs/why-it-saves-tokens.md#four-assistants-with-and-without) has the
summary. On 19 November, Claude Code got the same kind of question on four public repositories, in Go, Python,
TypeScript and C#: see [Four public repositories](#four-public-repositories-19-november-2026).

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

## Four public repositories, 19 November 2026

The same kind of question on four public repositories, one language each, asked with
[`codegraph-index compare`](../../docs/usage.md#measure-it-with-and-without): 3 rounds each way, Claude Code 2.1.328,
`claude-opus-5-5` at effort high, the codegraph server built from commit `3ff9f83`. Each repository was cloned at the
commit below and indexed with `codegraph-index init` (Jellyfin after `dotnet restore`, Hono after `pnpm install`).
All 24 sessions counted: every "with" session had the server connected, and no working tree changed.
[results/compare-2026-11-19.csv](results/compare-2026-11-19.csv) has one row per session.

| Repository | Language | Commit | Question: "What breaks if I change …, and which tests cover it?" |
|---|---|---|---|
| spf13/cobra | Go | `adbc881` | `Command.Find` |
| encode/httpx | Python | `7cff5f7` | `URL.copy_with` |
| honojs/hono | TypeScript | `75ff8e3` | `encodeBase64` in `src/utils/encode.ts` |
| jellyfin/jellyfin | C# | `df62c85` | `TmdbUtils.NormalizeLanguage` |

### What the sessions cost

Medians of 3 sessions each way, from the session logs; wall time measured by compare.

| Repository | Wall s, with / without | Model calls | Input tokens, cache reads included | Cost USD as Claude Code reports it |
|---|---|---|---|---|
| Cobra | 40.5 / 60.5 | 7 / 9 | 192,608 / 158,018 | 0.3280 / 0.3124 |
| HTTPX | 40.4 / 52.3 | 6 / 10 | 175,886 / 156,151 | 0.3372 / 0.2538 |
| Hono | 35.0 / 52.4 | 5 / 8 | 218,574 / 130,045 | 0.5592 / 0.2348 |
| Jellyfin | 34.6 / 40.1 | 6 / 7 | 218,172 / 155,706 | 0.3852 / 0.2810 |

- On all four, the median "with" session took less wall time and fewer model calls, and processed more input tokens
  at a higher reported cost.
- The median first model call of the "with" sessions was 8,681 tokens larger on each repository (medians 16,541 to
  16,604 against 7,860 to 7,923). The server is the only difference in the configuration, so that is what its tool
  definitions and instructions add.
- Wall time for Cobra and HTTPX was measured while the answer-key test suites below ran on the same machine.
  Tokens, model calls and cost do not depend on that. Hono and Jellyfin ran with the machine otherwise idle, apart
  from two short graph queries during the Hono runs.

### Which tests run the method

The key does not come from CodeGraph. In a separate copy of each repository, a probe at the top of the method appended
the name of the running test to a file, and the test suite ran:

- **Cobra:** each of the 279 tests and examples of both packages ran alone (`-test.run '^Name$'`); a test counts when
  it wrote the file. 208 do.
- **HTTPX:** the probe wrote `PYTEST_CURRENT_TEST`; one `pytest` run of 1,418 tests. 46 test functions (63 test cases
  with their parameters) run it. One test, `test_download`, failed with and without the probe (a
  `DeprecationWarning`).
- **Hono:** the probe wrote vitest's `expect.getState().currentTestName`; the `main`, `jsx-runtime-default` and
  `jsx-runtime-dom` projects, 6,204 tests. 133 run it. 5 type-declaration tests failed with and without the probe.
  The Deno, Bun, Workerd and other runtime projects, and the separate packages under `adapters/`, were not run.
- **Jellyfin:** the probe walked the stack to the method with `[Fact]` or `[Theory]`; all 17 test projects, 4,271
  test cases. 2 test methods (7 cases) run it, both in `TmdbUtilsTests`.

What the graph's `covered_by` (depth 10) lists, against that key:

| Repository | Tests that run it | Graph lists | Of those, run it | Run it, not listed |
|---|---|---|---|---|
| Cobra | 208 | 226 | 208 | 0 |
| HTTPX | 46 | 225 | 40 | 6 |
| Hono | 133 | 124 | at least 85 | at most 48 |
| Jellyfin | 2 | 17 | 2 | 0 |

The graph lists the tests that can reach the method through calls, not the ones that execute it. It listed more than
run it in three of the four; on HTTPX, 225 against 46. The Hono row matches by exact test name, so 85 is a
lower bound: the graph holds a generated test under its source name (`base64 > encode` for the 21 cases of a
`describe.each` in `encode.test.ts`, `JWT > ${tc.alg} sign & verify` in `jwt.test.ts`), while the run records each
generated name. [answer-key/](answer-key/) has each list.

### What the answers said

Read by hand from each session's answer; the `stated_test_count` column of the CSV quotes it.

- **With CodeGraph:** every answer gave a total, the graph's: 225 or 226 for Cobra, 213 or 225 for HTTPX (one also
  said 29 tests call it directly), 124 for Hono, and for Jellyfin "the graph lists 17, 2 run it" in all three.
- **Without:** Cobra, HTTPX and Hono answers named tests but gave no total (one Cobra answer counted 340 calls to
  `Execute*` in the test files; two HTTPX answers said "about 20 tests" build URLs with it). Two Jellyfin answers
  named the 2 test methods the key has; the third said "7 unit-test cases", the key's count of cases.
- All three Hono answers without the graph found copies of the method and their tests under `adapters/`, which the
  graph does not index; one answer with the graph mentioned those copies too.

So on these four questions the graph made the sessions faster and gave every answer a number, but the number was
the call graph's reach: 18 above the run-time count on Cobra, 9 below on Hono, 179 above on HTTPX and 15 above on
Jellyfin, where all three answers said only 2 of the 17 run it. Without the graph the sessions cost less, and the
Jellyfin answers were as right.

## Files

| File | What it does |
|---|---|
| [run.py](run.py) | One run: `run.py <claude\|codex\|copilot\|cursor> <on\|off> <n>`. Needs `CODEGRAPH_MCP`, the server to test. The 24 runs used an earlier copy of this script that read the server from config files (`cfg/*.json`) naming the Debug build of `codegraph-mcp`; the commands were otherwise the same. |
| [batch.sh](batch.sh) | The 24 runs, in the order above. |
| [analyze.py](analyze.py) | A table of the runs, and medians for each assistant and condition. `--answers` prints every answer. |
| [answer-key/](answer-key/) | The 44 tests from the run-time check, and the graph's 37 and 42. For 19 November, each repository's run-time list (`*-runtime-N.txt`) and the graph's `covered_by` list (`*-graph-N.txt`). |
| [results/runs.csv](results/runs.csv) | The 24 runs of 14 November, scored. |
| [results/compare-2026-11-19.csv](results/compare-2026-11-19.csv) | The 24 sessions on four public repositories, 19 November. |

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
