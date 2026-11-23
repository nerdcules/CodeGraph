# CodeGraph lite benchmark

Model `claude-sonnet-5-5`; repo `~/Projects/gh/jellyfin` at `1fbd8739292cce610231be93daf43368733edf63`; index `~/Projects/gh/jellyfin/.codegraph/graph.db` (prebuilt, index time excluded); 2 repetition(s), interleaved across modes; turn cap 30; timeout 600 s. Tasks: where-defined (single), impact-tests (multi), dead-code (analytical); baseline no-op prompt.

Total tokens = input + output + cache read + cache creation, as the claude CLI's result message reports them. Figures are median (min–max) over the counted runs: finished, and scoring at least the median direct score of the same task.

## Tokens, cost, turns and wall time per category and mode

| category | mode | runs | counted | total tokens | cost USD | turns | wall s |
|---|---|---:|---:|---|---|---|---|
| analytical | direct | 2 | 2 | 91,903 (71,068–112,738) | 0.1000 (0.0933–0.1067) | 8 (7–10) | 15.2 (13.1–17.2) |
| analytical | lite | 2 | 2 | 57,509 (45,716–69,302) | 0.0648 (0.0535–0.0761) | 6 (6–6) | 21.4 (20.7–22.0) |
| analytical | lite-cli | 2 | 2 | 33,889 (29,463–38,315) | 0.0355 (0.0336–0.0374) | 4 (4–5) | 11.5 (10.3–12.8) |
| multi | direct | 2 | 2 | 39,825 (39,622–40,028) | 0.1274 (0.1254–0.1295) | 3 (3–3) | 17.4 (16.4–18.4) |
| multi | lite | 2 | 2 | 34,910 (34,848–34,971) | 0.0454 (0.0452–0.0456) | 4 (4–4) | 16.9 (16.4–17.5) |
| multi | lite-cli | 2 | 2 | 21,696 (21,671–21,720) | 0.0350 (0.0348–0.0351) | 3 (3–3) | 12.2 (12.0–12.5) |
| single | direct | 2 | 2 | 16,238 (16,222–16,255) | 0.0185 (0.0183–0.0186) | 3 (3–3) | 5.7 (5.7–5.8) |
| single | lite | 2 | 2 | 23,058 (23,055–23,061) | 0.0236 (0.0235–0.0236) | 4 (4–4) | 8.6 (8.5–8.6) |
| single | lite-cli | 2 | 2 | 18,333 (18,320–18,346) | 0.0139 (0.0138–0.0140) | 3 (3–3) | 5.1 (5.1–5.2) |
| baseline | direct | 2 | 2 | 4,409 (4,409–4,409) | 0.0080 (0.0080–0.0080) | 1 (1–1) | 2.5 (2.5–2.5) |
| baseline | lite | 2 | 2 | 6,000 (6,000–6,000) | 0.0060 (0.0060–0.0060) | 1 (1–1) | 2.4 (2.4–2.5) |
| baseline | lite-cli | 2 | 2 | 5,287 (5,287–5,287) | 0.0054 (0.0054–0.0054) | 1 (1–1) | 2.3 (2.3–2.4) |

## Change from direct (median total tokens and cost of counted runs)

| category | mode | tokens change | cost change | runs (direct / mode) |
|---|---|---:|---:|---|
| analytical | lite | -37% | -35% | 2 / 2 |
| analytical | lite-cli | -63% | -64% | 2 / 2 |
| multi | lite | -12% | -64% | 2 / 2 |
| multi | lite-cli | -46% | -73% | 2 / 2 |
| single | lite | +42% | +28% | 2 / 2 |
| single | lite-cli | +13% | -25% | 2 / 2 |
| baseline | lite | +36% | -25% | 2 / 2 |
| baseline | lite-cli | +20% | -33% | 2 / 2 |

## Correctness (F1 on the listed items)

| category | mode | scored runs | F1 median (min–max) | finished but failed the bar |
|---|---|---:|---|---:|
| analytical | direct | 2 | 1.00 (1.00–1.00) | 0 |
| analytical | lite | 2 | 1.00 (1.00–1.00) | 0 |
| analytical | lite-cli | 2 | 1.00 (1.00–1.00) | 0 |
| multi | direct | 2 | 0.98 (0.98–0.98) | 0 |
| multi | lite | 2 | 1.00 (1.00–1.00) | 0 |
| multi | lite-cli | 2 | 1.00 (1.00–1.00) | 0 |
| single | direct | 2 | 1.00 (1.00–1.00) | 0 |
| single | lite | 2 | 1.00 (1.00–1.00) | 0 |
| single | lite-cli | 2 | 1.00 (1.00–1.00) | 0 |

Median direct score per task (the bar): where-defined 1.00, impact-tests 0.98, dead-code 1.00.

Ground truth items per task: where-defined 1, impact-tests 20, dead-code 12.

## Cheaper but worse

No finished run scored below the direct median of its task.

## Failures (recorded, not retried)

None.

Fields missing from the CLI's JSON (logged as null): none.

## Scripts and CLI calls in the lite modes

| mode | runs | scripts run | compile failures | CLI invocations | time inside scripts or CLI, s (sum) | median per run, s |
|---|---:|---:|---:|---:|---:|---:|
| lite | 8 | 8 | 0 (0%) | 0 | 20.2 | 2.7 |
| lite-cli | 8 | 0 | 0 | 17 | 1.9 | 0.3 |

Tool calls by type, summed per mode: direct: ToolSearch 9, mcp__codegraph__dead_code_report 2, mcp__codegraph__find_symbol 2, mcp__codegraph__impact_of 2, mcp__codegraph__outline 1, mcp__codegraph__snippet 7; lite: Bash 8, Read 6, Write 8; lite-cli: Bash 15.

## Static overhead (Stage 4)

From `overhead-20261122-205829/overhead.md`:

| mode | part | tokens | how |
|---|---|---:|---|
| direct | first context, whole profile | 4,431 | tools ToolSearch |
| direct | MCP server (codegraph) | 2,034 | with minus without the server |
| direct | CLAUDE.md snippet | 202 | with minus without CLAUDE.md |
| lite | first context, whole profile | 6,020 | tools Bash,Read,Write,Edit,ToolSearch |
| lite | MCP server (codegraph) | -30 | with minus without the server |
| lite | CLAUDE.md snippet | 580 | with minus without CLAUDE.md |
| lite-cli | first context, whole profile | 5,301 | tools Bash,Read,ToolSearch |
| lite-cli | MCP server (codegraph) | -4 | with minus without the server |
| lite-cli | CLAUDE.md snippet | 416 | with minus without CLAUDE.md |
| direct | MCP tool schemas loaded up front | 9,406 | no ToolSearch: with minus without the server |
| lite | codegraph-lite.md (text) | 1,512 | text in a prompt; a Read result adds line numbers |
| lite-cli | codegraph --help (text) | 830 | text in a prompt |
| lite | compile error output (compile-error-output.txt) | 200 | text in a prompt |

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

- analytical, lite vs direct: fewer tokens, ranges do not overlap: median 91,903 → 57,509 tokens (-37%), ranges 71,068–112,738 and 45,716–69,302, 2 / 2 runs
- analytical, lite-cli vs direct: fewer tokens, ranges do not overlap: median 91,903 → 33,889 tokens (-63%), ranges 71,068–112,738 and 29,463–38,315, 2 / 2 runs
- analytical, lite-cli vs lite: fewer tokens, ranges do not overlap: median 57,509 → 33,889 tokens (-41%), ranges 45,716–69,302 and 29,463–38,315, 2 / 2 runs
- multi, lite vs direct: fewer tokens, ranges do not overlap: median 39,825 → 34,910 tokens (-12%), ranges 39,622–40,028 and 34,848–34,971, 2 / 2 runs
- multi, lite-cli vs direct: fewer tokens, ranges do not overlap: median 39,825 → 21,696 tokens (-46%), ranges 39,622–40,028 and 21,671–21,720, 2 / 2 runs
- multi, lite-cli vs lite: fewer tokens, ranges do not overlap: median 34,910 → 21,696 tokens (-38%), ranges 34,848–34,971 and 21,671–21,720, 2 / 2 runs
- single, lite vs direct: more tokens, ranges do not overlap: median 16,238 → 23,058 tokens (+42%), ranges 16,222–16,255 and 23,055–23,061, 2 / 2 runs
- single, lite-cli vs direct: more tokens, ranges do not overlap: median 16,238 → 18,333 tokens (+13%), ranges 16,222–16,255 and 18,320–18,346, 2 / 2 runs
- single, lite-cli vs lite: fewer tokens, ranges do not overlap: median 23,058 → 18,333 tokens (-20%), ranges 23,055–23,061 and 18,320–18,346, 2 / 2 runs

## Full-matrix estimate

Full matrix: 8 tasks + baseline, 3 modes, 5 repetitions = 135 runs. Estimated from the pilot's medians per category and mode (each pilot cell is one task, so other tasks in a category may cost more or less): about 4,416,385 total tokens, $6.82, 0.5 h of sequential wall time.
