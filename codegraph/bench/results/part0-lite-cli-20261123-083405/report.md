# CodeGraph lite benchmark

Model `claude-sonnet-5-5`; repo `~/Projects/gh/jellyfin` at `1fbd8739292cce610231be93daf43368733edf63`; index `~/Projects/gh/jellyfin/.codegraph/graph.db` (prebuilt, index time excluded); 2 repetition(s), interleaved across modes; turn cap 30; timeout 600 s. Tasks: where-defined (single), impact-tests (multi), dead-code (analytical); baseline no-op prompt.

Total tokens = input + output + cache read + cache creation, as the claude CLI's result message reports them. Figures are median (min–max) over the counted runs: finished, and scoring at least the median direct score of the same task.

## Tokens, cost, turns and wall time per category and mode

| category | mode | runs | counted | total tokens | cost USD | turns | wall s |
|---|---|---:|---:|---|---|---|---|
| analytical | lite-cli | 2 | 2 | 26,662 (22,633–30,691) | 0.0330 (0.0309–0.0350) | 4 (3–4) | 10.7 (9.0–12.3) |
| multi | lite-cli | 2 | 2 | 46,716 (35,691–57,742) | 0.0589 (0.0423–0.0755) | 6 (5–6) | 16.6 (15.9–17.2) |
| single | lite-cli | 2 | 2 | 11,786 (11,780–11,793) | 0.0106 (0.0105–0.0107) | 2 (2–2) | 3.5 (3.4–3.6) |
| baseline | lite-cli | 2 | 2 | 5,609 (5,609–5,609) | 0.0067 (0.0067–0.0067) | 1 (1–1) | 2.0 (2.0–2.0) |

## Change from direct (median total tokens and cost of counted runs)

| category | mode | tokens change | cost change | runs (direct / mode) |
|---|---|---:|---:|---|
| analytical | lite-cli | – | – | 0 / 2 |
| multi | lite-cli | – | – | 0 / 2 |
| single | lite-cli | – | – | 0 / 2 |
| baseline | lite-cli | – | – | 0 / 2 |

## Correctness (F1 on the listed items)

| category | mode | scored runs | F1 median (min–max) | finished but failed the bar |
|---|---|---:|---|---:|
| analytical | lite-cli | 2 | 1.00 (1.00–1.00) | 0 |
| multi | lite-cli | 2 | 1.00 (1.00–1.00) | 0 |
| single | lite-cli | 2 | 1.00 (1.00–1.00) | 0 |

Median direct score per task (the bar): .

Ground truth items per task: where-defined 1, impact-tests 20, dead-code 12.

## Cheaper but worse

No finished run scored below the direct median of its task.

## Failures (recorded, not retried)

None.

Fields missing from the CLI's JSON (logged as null): none.

## Scripts and CLI calls in the lite modes

| mode | runs | scripts run | compile failures | CLI invocations | time inside scripts or CLI, s (sum) | median per run, s |
|---|---:|---:|---:|---:|---:|---:|
| lite-cli | 8 | 0 | 0 | 18 | 3.7 | 0.2 |

Tool calls by type, summed per mode: lite-cli: Bash 16.

## Question shape (issue #18)

A graph step is one codegraph MCP tool call, one `codegraph` CLI invocation or one `dotnet run` of a script. Result size is the characters the step's tool call returned. Chained is a heuristic: a later step's input reuses an id, a handle or a dotted name from an earlier step's result that is not in the prompt. Figures are median (min–max) over all finished runs; tokens over counted runs.

| task | mode | runs | graph steps | largest result, chars | total result, chars | chained |
|---|---|---:|---|---|---|---:|
| where-defined | lite-cli | 2 | 1 (1–1) | 149 (131–167) | 149 (131–167) | 0 of 2 |
| impact-tests | lite-cli | 2 | 4 (4–5) | 10,178 (3,205–17,150) | 12,328 (4,863–19,793) | 0 of 2 |
| dead-code | lite-cli | 2 | 4 (2–5) | 2,557 (2,424–2,690) | 4,966 (4,818–5,114) | 0 of 2 |

## Static overhead (Stage 4)

From `overhead-20261123-083324/overhead.md`:

| mode | part | tokens | how |
|---|---|---:|---|
| direct | first context, whole profile | 4,463 | tools ToolSearch |
| direct | MCP server (codegraph) | 2,072 | with minus without the server |
| direct | CLAUDE.md snippet | 198 | with minus without CLAUDE.md |
| lite | first context, whole profile | 6,050 | tools Bash,Read,Write,Edit,ToolSearch |
| lite | MCP server (codegraph) | 12 | with minus without the server |
| lite | CLAUDE.md snippet | 571 | with minus without CLAUDE.md |
| lite-cli | first context, whole profile | 5,609 | tools Bash,Read,ToolSearch |
| lite-cli | MCP server (codegraph) | -4 | with minus without the server |
| lite-cli | CLAUDE.md snippet | 726 | with minus without CLAUDE.md |
| direct | MCP tool schemas loaded up front | 9,406 | no ToolSearch: with minus without the server |
| lite | codegraph-lite.md (text) | 1,508 | text in a prompt; a Read result adds line numbers |
| lite-cli | codegraph --help (text) | 829 | text in a prompt |
| lite | compile error output (compile-error-output.txt) | 199 | text in a prompt |

## Start-up (Stages 2 and 3)

From `results/startup.md`:

Measured 2026-11-22 on this Linux machine (linux-x64), Jellyfin graph (21,174 nodes, 109,420 edges) at 1fbd873.

| what | runs | median | min–max |
|---|---:|---:|---|
| `dotnet run` of a new script (cold build; CodeGraph.Client already built), SDK 10.0.113 | 10 | 3,777 ms | 2,658–4,067 ms |
| `dotnet run` of the same script again (build cache) | 10 | 639 ms | 593–685 ms |
| `codegraph status`, Native AOT build | 20 | 51.7 ms | 47.6–67.1 ms |
| `codegraph status`, JIT build (framework-dependent, runtime 10.0.13) | 20 | 209.8 ms | 186.9–268.8 ms |
| `codegraph callers GuidExtensions.IsEmpty --depth 2 --limit 0`, AOT | 20 | 317.7 ms | 257.9–332.0 ms |
| the same, JIT | 20 | 473.6 ms | 400.2–601.0 ms |

Both `dotnet run` rows run the script `callers-by-project.cs -- GuidExtensions.IsEmpty 2`, the same query as the `callers` rows.
The callers JSON with `--limit 0` is 418,051 bytes. Sizes: AOT `codegraph` 3,456,648 bytes plus `libduckdb.so`
70,529,912 bytes. win-x64: not built or measured (Native AOT cannot cross-compile between operating systems).
A compile error of 2 errors is 737 bytes of output, 205 tokens (claude-sonnet-5-5, overhead command).

## Conclusion (computed from the ranges above)

- analytical, lite-cli vs direct: no counted runs to compare (0 / 2)
- multi, lite-cli vs direct: no counted runs to compare (0 / 2)
- single, lite-cli vs direct: no counted runs to compare (0 / 2)
