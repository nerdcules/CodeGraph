# CodeGraph lite benchmark

Model `claude-sonnet-5-5`; repo `~/Projects/gh/jellyfin` at `1fbd8739292cce610231be93daf43368733edf63`; index `~/Projects/gh/jellyfin/.codegraph/graph.db` (prebuilt, index time excluded); 5 repetition(s), interleaved across modes; turn cap 30; timeout 600 s. Tasks: where-defined (single), signature (single), impact-tests (multi), callers-by-project (multi), only-test-callers (multi), interface-callers (multi), dead-code (analytical), project-cycles (analytical); baseline no-op prompt.

Total tokens = input + output + cache read + cache creation, as the claude CLI's result message reports them. Figures are median (min–max) over the counted runs: finished, and scoring at least the median direct score of the same task.

## Tokens, cost, turns and wall time per category and mode

| category | mode | runs | counted | total tokens | cost USD | turns | wall s |
|---|---|---:|---:|---|---|---|---|
| analytical | direct | 10 | 10 | 81,338 (68,951–146,348) | 0.1466 (0.0904–0.1916) | 18 (5–33) | 25.2 (13.7–39.1) |
| analytical | lite | 10 | 10 | 38,558 (23,796–56,145) | 0.0398 (0.0277–0.0562) | 5 (4–6) | 17.4 (10.4–33.4) |
| analytical | lite-cli | 10 | 10 | 32,072 (22,314–40,862) | 0.0339 (0.0267–0.0494) | 4 (3–5) | 11.9 (7.3–14.7) |
| multi | direct | 20 | 20 | 37,677 (18,704–1,662,363) | 0.1197 (0.0378–0.7554) | 3 (3–111) | 18.1 (12.4–103.4) |
| multi | lite | 20 | 20 | 35,371 (26,094–310,423) | 0.0459 (0.0293–0.2382) | 4 (4–23) | 17.0 (11.8–123.9) |
| multi | lite-cli | 20 | 20 | 23,263 (12,548–40,675) | 0.0321 (0.0149–0.0430) | 3 (2–5) | 12.1 (5.9–22.6) |
| single | direct | 10 | 10 | 15,716 (15,213–16,231) | 0.0176 (0.0166–0.0186) | 3 (3–3) | 6.0 (5.4–10.7) |
| single | lite | 10 | 10 | 31,094 (22,895–31,791) | 0.0250 (0.0229–0.0268) | 4 (4–4) | 9.3 (8.3–17.1) |
| single | lite-cli | 10 | 10 | 11,936 (11,865–12,091) | 0.0109 (0.0105–0.0117) | 2 (2–2) | 4.2 (3.7–6.0) |
| baseline | direct | 5 | 5 | 4,401 (4,401–4,401) | 0.0080 (0.0080–0.0080) | 1 (1–1) | 2.5 (2.4–4.1) |
| baseline | lite | 5 | 5 | 5,976 (5,976–5,976) | 0.0059 (0.0059–0.0059) | 1 (1–1) | 2.4 (2.3–2.9) |
| baseline | lite-cli | 5 | 5 | 5,662 (5,662–5,662) | 0.0069 (0.0069–0.0069) | 1 (1–1) | 2.3 (2.2–2.5) |

## Change from direct (median total tokens and cost of counted runs)

| category | mode | tokens change | cost change | runs (direct / mode) |
|---|---|---:|---:|---|
| analytical | lite | -53% | -73% | 10 / 10 |
| analytical | lite-cli | -61% | -77% | 10 / 10 |
| multi | lite | -6% | -62% | 20 / 20 |
| multi | lite-cli | -38% | -73% | 20 / 20 |
| single | lite | +98% | +42% | 10 / 10 |
| single | lite-cli | -24% | -38% | 10 / 10 |
| baseline | lite | +36% | -26% | 5 / 5 |
| baseline | lite-cli | +29% | -14% | 5 / 5 |

## Correctness (F1 on the listed items)

| category | mode | scored runs | F1 median (min–max) | finished but failed the bar |
|---|---|---:|---|---:|
| analytical | direct | 10 | 1.00 (1.00–1.00) | 0 |
| analytical | lite | 10 | 1.00 (1.00–1.00) | 0 |
| analytical | lite-cli | 10 | 1.00 (1.00–1.00) | 0 |
| multi | direct | 20 | 1.00 (0.00–1.00) | 0 |
| multi | lite | 20 | 1.00 (0.00–1.00) | 0 |
| multi | lite-cli | 20 | 1.00 (1.00–1.00) | 0 |
| single | direct | 10 | 1.00 (1.00–1.00) | 0 |
| single | lite | 10 | 1.00 (1.00–1.00) | 0 |
| single | lite-cli | 10 | 1.00 (1.00–1.00) | 0 |

Median direct score per task (the bar): where-defined 1.00, signature 1.00, impact-tests 0.98, callers-by-project 1.00, only-test-callers 0.00, interface-callers 1.00, dead-code 1.00, project-cycles 1.00.

Ground truth items per task: where-defined 1, signature 1, impact-tests 20, callers-by-project 16, only-test-callers 5, interface-callers 21, dead-code 12, project-cycles 0.

## Cheaper but worse

No finished run scored below the direct median of its task.

## Failures (recorded, not retried)

None.

Fields missing from the CLI's JSON (logged as null): none.

## Scripts and CLI calls in the lite modes

| mode | runs | scripts run | compile failures | CLI invocations | time inside scripts or CLI, s (sum) | median per run, s |
|---|---:|---:|---:|---:|---:|---:|
| lite | 45 | 61 | 1 (2%) | 0 | 159.0 | 2.4 |
| lite-cli | 45 | 0 | 0 | 97 | 21.7 | 0.3 |

Tool calls by type, summed per mode: direct: ToolSearch 56, mcp__codegraph__callees_of 96, mcp__codegraph__callers_of 217, mcp__codegraph__dead_code_report 5, mcp__codegraph__depends_on 98, mcp__codegraph__find_symbol 66, mcp__codegraph__graph_stats 5, mcp__codegraph__impact_of 5, mcp__codegraph__members_of 54, mcp__codegraph__search_code 5, mcp__codegraph__snippet 14, mcp__codegraph__tests_for 2, mcp__codegraph__traverse 23; lite: Bash 65, Edit 5, Read 40, Write 56; lite-cli: Bash 84.

## Question shape (issue #18)

A graph step is one codegraph MCP tool call, one `codegraph` CLI invocation or one `dotnet run` of a script. Result size is the characters the step's tool call returned. Chained is a heuristic: a later step's input reuses an id, a handle or a dotted name from an earlier step's result that is not in the prompt. Figures are median (min–max) over all finished runs; tokens over counted runs.

| task | mode | runs | graph steps | largest result, chars | total result, chars | chained |
|---|---|---:|---|---|---|---:|
| where-defined | direct | 5 | 1 (1–1) | 1,028 (1,028–1,028) | 1,028 (1,028–1,028) | 0 of 5 |
| where-defined | lite | 5 | 1 (1–1) | 131 (131–131) | 131 (131–131) | 0 of 5 |
| where-defined | lite-cli | 5 | 1 (1–1) | 131 (131–131) | 131 (131–131) | 0 of 5 |
| signature | direct | 5 | 1 (1–1) | 1,887 (1,887–1,887) | 1,887 (1,887–1,887) | 0 of 5 |
| signature | lite | 5 | 1 (1–1) | 875 (826–1,023) | 875 (826–1,023) | 0 of 5 |
| signature | lite-cli | 5 | 1 (1–1) | 369 (229–437) | 369 (229–437) | 0 of 5 |
| impact-tests | direct | 5 | 1 (1–1) | 43,366 (43,366–43,366) | 43,366 (43,366–43,366) | 0 of 5 |
| impact-tests | lite | 5 | 1 (1–1) | 3,243 (3,219–3,279) | 3,243 (3,219–3,279) | 0 of 5 |
| impact-tests | lite-cli | 5 | 1 (1–4) | 3,377 (3,370–3,395) | 3,386 (3,377–4,156) | 0 of 5 |
| callers-by-project | direct | 5 | 1 (1–1) | 31,444 (31,444–31,444) | 31,444 (31,444–31,444) | 0 of 5 |
| callers-by-project | lite | 5 | 1 (1–2) | 431 (429–673) | 431 (429–1,329) | 1 of 5 |
| callers-by-project | lite-cli | 5 | 2 (1–3) | 727 (497–1,182) | 1,603 (497–2,170) | 3 of 5 |
| only-test-callers | direct | 5 | 73 (65–107) | 18,976 (16,270–25,605) | 165,506 (110,058–190,825) | 3 of 5 |
| only-test-callers | lite | 5 | 3 (1–9) | 3,910 (1,711–7,066) | 7,348 (1,711–16,693) | 2 of 5 |
| only-test-callers | lite-cli | 5 | 5 (4–6) | 2,424 (2,424–2,424) | 4,075 (3,562–4,955) | 1 of 5 |
| interface-callers | direct | 5 | 1 (1–2) | 5,405 (5,405–5,405) | 5,405 (5,405–5,750) | 0 of 5 |
| interface-callers | lite | 5 | 1 (1–2) | 5,594 (5,055–8,132) | 5,812 (5,594–8,132) | 1 of 5 |
| interface-callers | lite-cli | 5 | 2 (2–3) | 3,848 (3,806–3,848) | 5,593 (4,557–6,320) | 5 of 5 |
| dead-code | direct | 5 | 3 (2–4) | 31,291 (31,291–31,291) | 33,941 (33,225–60,050) | 0 of 5 |
| dead-code | lite | 5 | 2 (2–2) | 2,705 (1,918–3,623) | 3,503 (2,838–4,977) | 0 of 5 |
| dead-code | lite-cli | 5 | 3 (2–3) | 3,258 (2,424–3,664) | 5,682 (4,220–6,088) | 0 of 5 |
| project-cycles | direct | 5 | 26 (25–30) | 9,270 (9,270–9,270) | 33,087 (32,949–44,592) | 0 of 5 |
| project-cycles | lite | 5 | 1 (1–1) | 17 (9–27) | 17 (9–27) | 0 of 5 |
| project-cycles | lite-cli | 5 | 4 (3–5) | 4,570 (2,424–10,489) | 7,025 (2,632–12,920) | 0 of 5 |

### Tokens by the shape direct mode needed

Tasks ordered by direct mode's median graph steps. A mode is named cheaper only when its token range does not overlap direct's.

| task | direct steps | direct largest result | direct tokens | lite tokens | lite-cli tokens | lite vs direct | lite-cli vs direct |
|---|---:|---:|---|---|---|---|---|
| where-defined | 1.0 | 1,028 | 16,231 (16,220–16,231) | 22,940 (22,895–31,112) | 11,899 (11,865–11,902) | more (+41%) | fewer (-27%) |
| signature | 1.0 | 1,887 | 15,213 (15,213–15,213) | 31,674 (23,558–31,791) | 12,079 (11,970–12,091) | more (+108%) | fewer (-21%) |
| interface-callers | 1.0 | 5,405 | 18,780 (18,704–26,784) | 36,126 (35,876–45,424) | 22,945 (22,182–30,209) | more (+92%) | overlap (+22%) |
| callers-by-project | 1.0 | 31,444 | 35,319 (35,177–35,845) | 32,279 (32,229–51,965) | 19,878 (12,548–26,763) | overlap (-9%) | fewer (-44%) |
| impact-tests | 1.0 | 43,366 | 39,602 (39,509–39,825) | 34,654 (34,562–34,866) | 15,244 (15,143–40,675) | fewer (-12%) | overlap (-62%) |
| dead-code | 3.0 | 31,291 | 71,371 (70,419–146,348) | 54,521 (44,630–56,145) | 23,319 (22,314–30,397) | fewer (-24%) | fewer (-67%) |
| project-cycles | 26.0 | 9,270 | 84,397 (68,951–112,666) | 32,281 (23,796–32,486) | 34,242 (33,747–40,862) | fewer (-62%) | fewer (-59%) |
| only-test-callers | 73.0 | 18,976 | 958,326 (718,830–1,662,363) | 65,089 (26,094–310,423) | 30,485 (29,807–39,971) | fewer (-93%) | fewer (-97%) |

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

- analytical, lite vs direct: fewer tokens, ranges do not overlap: median 81,338 → 38,558 tokens (-53%), ranges 68,951–146,348 and 23,796–56,145, 10 / 10 runs
- analytical, lite-cli vs direct: fewer tokens, ranges do not overlap: median 81,338 → 32,072 tokens (-61%), ranges 68,951–146,348 and 22,314–40,862, 10 / 10 runs
- analytical, lite-cli vs lite: ranges overlap, too noisy to say: median 38,558 → 32,072 tokens (-17%), ranges 23,796–56,145 and 22,314–40,862, 10 / 10 runs
- multi, lite vs direct: ranges overlap, too noisy to say: median 37,677 → 35,371 tokens (-6%), ranges 18,704–1,662,363 and 26,094–310,423, 20 / 20 runs
- multi, lite-cli vs direct: ranges overlap, too noisy to say: median 37,677 → 23,263 tokens (-38%), ranges 18,704–1,662,363 and 12,548–40,675, 20 / 20 runs
- multi, lite-cli vs lite: ranges overlap, too noisy to say: median 35,371 → 23,263 tokens (-34%), ranges 26,094–310,423 and 12,548–40,675, 20 / 20 runs
- single, lite vs direct: more tokens, ranges do not overlap: median 15,716 → 31,094 tokens (+98%), ranges 15,213–16,231 and 22,895–31,791, 10 / 10 runs
- single, lite-cli vs direct: fewer tokens, ranges do not overlap: median 15,716 → 11,936 tokens (-24%), ranges 15,213–16,231 and 11,865–12,091, 10 / 10 runs
- single, lite-cli vs lite: fewer tokens, ranges do not overlap: median 31,094 → 11,936 tokens (-62%), ranges 22,895–31,791 and 11,865–12,091, 10 / 10 runs

## Conclusion (written from the tables above)

**The hypothesis holds for C# scripts, not for the CLI.** Scripts (`lite`) used more tokens than the MCP tools on
single lookups (+98%) and fewer on analytical questions (−53%), ranges not overlapping in either. The CLI
(`lite-cli`, with the Part 0 snippet) used fewer tokens than the MCP tools on single lookups too (−24%), and on
analytical questions (−61%), ranges not overlapping.

**Multi-hop: no category-level claim.** The median fell (lite −6%, lite-cli −38%), but the ranges overlap: direct's
only-test-callers runs reach 1,662,363 tokens. Per task, lite-cli used fewer tokens with no overlap on
callers-by-project and only-test-callers; on impact-tests and interface-callers the ranges overlap.

**Correctness.** lite-cli scored F1 1.00 in all 40 scored runs. On only-test-callers, direct scored 0 in 3 of 5
runs and lite in 2 of 5; direct's median there is 0.00, so the bar is 0 and every run of that task counts. All 135
runs finished; no run was cheaper but worse.

**Routing (issue #18).** The number of calls direct mode needed does not decide it on its own: of the five tasks
direct answered in one call, lite-cli used fewer tokens with no overlap on the two smallest results (where-defined,
1,028 chars; signature, 1,887) and on callers-by-project (31,444), and overlapped on interface-callers and
impact-tests. Nothing here supports sending single lookups to the MCP tools while the CLI has its command
reference.

**Cost of the run.** 135 runs, 9,512,531 total tokens, $8.759 (the cost the CLI reported, summed: 8.7591564). The pilot's
estimate was 4,416,385 tokens and $6.82; the five tasks the pilot did not run, above all only-test-callers in
direct mode (718,830 to 1,662,363 tokens a run), made it low.

**Limits.** One model (`claude-sonnet-5-5`), one repository and index, 5 repetitions. Direct's only-test-callers
runs recorded 68 to 111 turns although `--max-turns` was 30, and none ended with `error_max_turns`; how the CLI
counts turns against the cap was not investigated. `plan.json` has no `BenchCommit`: the harness binary was built
before that field was added; `bench/` had no uncommitted changes at commit 8daf6a8 when the run started.

