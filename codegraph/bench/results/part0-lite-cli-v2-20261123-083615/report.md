# CodeGraph lite benchmark

Model `claude-sonnet-5-5`; repo `~/Projects/gh/jellyfin` at `1fbd8739292cce610231be93daf43368733edf63`; index `~/Projects/gh/jellyfin/.codegraph/graph.db` (prebuilt, index time excluded); 2 repetition(s), interleaved across modes; turn cap 30; timeout 600 s. Tasks: where-defined (single), impact-tests (multi), dead-code (analytical); baseline no-op prompt.

Total tokens = input + output + cache read + cache creation, as the claude CLI's result message reports them. Figures are median (min–max) over the counted runs: finished, and scoring at least the median direct score of the same task.

## Tokens, cost, turns and wall time per category and mode

| category | mode | runs | counted | total tokens | cost USD | turns | wall s |
|---|---|---:|---:|---|---|---|---|
| analytical | lite-cli | 2 | 2 | 23,001 (22,525–23,477) | 0.0321 (0.0301–0.0341) | 3 (3–3) | 9.8 (9.7–9.9) |
| multi | lite-cli | 2 | 2 | 28,778 (23,335–34,221) | 0.0403 (0.0345–0.0461) | 4 (3–4) | 13.9 (12.2–15.7) |
| single | lite-cli | 2 | 2 | 11,904 (11,885–11,924) | 0.0106 (0.0104–0.0108) | 2 (2–2) | 3.5 (3.5–3.5) |
| baseline | lite-cli | 2 | 2 | 5,686 (5,686–5,686) | 0.0070 (0.0070–0.0070) | 1 (1–1) | 2.2 (2.0–2.3) |

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
| lite-cli | 8 | 0 | 0 | 12 | 2.8 | 0.2 |

Tool calls by type, summed per mode: lite-cli: Bash 11.

## Question shape (issue #18)

A graph step is one codegraph MCP tool call, one `codegraph` CLI invocation or one `dotnet run` of a script. Result size is the characters the step's tool call returned. Chained is a heuristic: a later step's input reuses an id, a handle or a dotted name from an earlier step's result that is not in the prompt. Figures are median (min–max) over all finished runs; tokens over counted runs.

| task | mode | runs | graph steps | largest result, chars | total result, chars | chained |
|---|---|---:|---|---|---|---:|
| where-defined | lite-cli | 2 | 1 (1–1) | 128 (128–128) | 128 (128–128) | 0 of 2 |
| impact-tests | lite-cli | 2 | 3 (2–4) | 3,674 (3,381–3,966) | 4,972 (3,384–6,561) | 0 of 2 |
| dead-code | lite-cli | 2 | 2 (2–2) | 2,970 (2,424–3,515) | 5,044 (4,148–5,939) | 0 of 2 |

## Static overhead (Stage 4)

From `overhead-20261123-083542/overhead.md`:

| mode | part | tokens | how |
|---|---|---:|---|
| direct | first context, whole profile | 4,423 | tools ToolSearch |
| direct | MCP server (codegraph) | 2,028 | with minus without the server |
| direct | CLAUDE.md snippet | 201 | with minus without CLAUDE.md |
| lite | first context, whole profile | 6,032 | tools Bash,Read,Write,Edit,ToolSearch |
| lite | MCP server (codegraph) | -18 | with minus without the server |
| lite | CLAUDE.md snippet | 581 | with minus without CLAUDE.md |
| lite-cli | first context, whole profile | 5,692 | tools Bash,Read,ToolSearch |
| lite-cli | MCP server (codegraph) | 6 | with minus without the server |
| lite-cli | CLAUDE.md snippet | 798 | with minus without CLAUDE.md |
| direct | MCP tool schemas loaded up front | 9,414 | no ToolSearch: with minus without the server |
| lite | codegraph-lite.md (text) | 1,505 | text in a prompt; a Read result adds line numbers |
| lite-cli | codegraph --help (text) | 830 | text in a prompt |
| lite | compile error output (compile-error-output.txt) | 198 | text in a prompt |

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
