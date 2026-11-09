# Benchmarks

Numbers from `codegraph-bench` (tools/CodeGraph.Bench) against this repository's own graph, Release build,
Linux x64, .NET 10. Query figures are the median of 7 runs after a warm-up. Index figures are one run of the
published `codegraph-index`, so they include the MSBuild solution load.

```bash
dotnet build -c Release
dotnet run -c Release --no-build --project tools/CodeGraph.Bench -- /tmp/bench.db \
    --index CodeGraph.slnx --indexer src/CodeGraph.Indexer/bin/Release/net10.0/codegraph-index [--live]
```

## Baseline (before the live daemon)

Commit: working tree after the code-review fixes, 2026-11-09.

| measurement | value |
|---|---|
| graph | 595 nodes, 3558 edges |
| full index | 11.5 s |
| graph file after full index | 7,692 KB |
| no-op incremental index | 3.5 s |
| graph file after 3 more full rebuilds | 15,884 KB |
| open read-only (per tool call) | 11 ms |
| get_node | 0.6 ms |
| callers_of depth 1 (most-called method) | 6.8 ms |
| impact_of depth 5 | 8.2 ms |
| impact_of depth 20 | 7.9 ms |
| covered_by depth 30 | 7.8 ms |
| explain_reachability | 1.3 ms |
| neighbors | 5.0 ms |
| subgraph depth 2 | 21 ms |
| path_between depth 10 (test → most-called) | 11 ms |
| find_symbol 'Dispose' | 3.5 ms |
| search_code 'open store read only' | 9.3 ms |
| most_called | 5.1 ms |
| coverage_stats | 9.9 ms |
| uncovered | 11 ms |
| dead_code_report | 12 ms |
| freshness check, cold | 4.5 ms |
| freshness check, warm | 1.2 ms |

What the baseline says: every graph query is single-digit to low double-digit milliseconds, so a tool call's
cost is the read-only open plus the query. The two things the architecture work targets are not in this table
at all, because the baseline cannot do them: answering from the live Roslyn solution (positions, metadata
symbols, diagnostics, what-if compiles), and the one-debounce freshness that makes `STALE` rare. The file
growth row (7.7 MB to 15.9 MB over four rebuilds of the same graph) is the storage leak to fix alongside.

## After the live daemon and the file-growth fix

Same machine, same method, 2026-11-09. The graph is larger because `tools/CodeGraph.Bench` joined the
solution (6 projects, 63 documents).

### Indexing and graph queries

| measurement | before | after |
|---|---|---|
| graph | 595 nodes, 3558 edges | 687 nodes, 3978 edges |
| full index | 11.5 s | 9.0 s |
| graph file after full index | 7,692 KB | 8,716 KB |
| no-op incremental index | 3.5 s | 2.5 s |
| graph file after 3 more full rebuilds | 15,884 KB | 8,716 KB |
| graph file after 9 incremental runs (watch-mode shape) | grows ~2.7 MB per run, unbounded | plateaus at 13–14 MB |
| open read-only (per tool call) | 11 ms | 10 ms |
| get_node | 0.6 ms | 0.7 ms |
| callers_of depth 1 (most-called method) | 6.8 ms | 6.8 ms |
| impact_of depth 5 | 8.2 ms | 7.9 ms |
| impact_of depth 20 | 7.9 ms | 7.7 ms |
| covered_by depth 30 | 7.8 ms | 7.7 ms |
| explain_reachability | 1.3 ms | 1.3 ms |
| neighbors | 5.0 ms | 4.9 ms |
| subgraph depth 2 | 21 ms | 21 ms |
| path_between depth 10 (test → most-called) | 11 ms | 12 ms |
| find_symbol 'Dispose' | 3.5 ms | 3.0 ms |
| search_code 'open store read only' | 9.3 ms | 8.3 ms |
| most_called | 5.1 ms | 5.1 ms |
| coverage_stats | 9.9 ms | 11 ms |
| uncovered | 11 ms | 12 ms |
| dead_code_report | 12 ms | 11 ms |
| freshness check, cold | 4.5 ms | 3.8 ms |
| freshness check, warm | 1.2 ms | 1.2 ms |

Graph queries are unchanged within noise, as intended: the daemon adds capability beside the graph rather than
changing how the graph is queried. The index-time differences are run-to-run variance of the MSBuild load.
The storage fix is the real change in this table: a full rebuild now writes a fresh file and swaps it in, so
repeated rebuilds no longer double the file, and `CHECKPOINT` after every incremental write lets DuckDB reuse
freed blocks, so a long `--watch` session plateaus instead of growing without bound.

### Live daemon (`codegraph-index --watch`, measured with `--live`)

These had no "before": the baseline could not answer them at all.

| measurement | value |
|---|---|
| live ping (pipe round trip) | 0.1–0.2 ms |
| live flush when idle | 0.1–0.2 ms |
| live symbol_at (declaration line) | 0.2–0.3 ms |
| live references, source symbol (most-called method) | 2–7 ms |
| live references, metadata symbol (System.IO.File) | 1.6–3.5 ms |
| live diagnostics, one file | 0.2–0.4 ms |
| live diagnostics, whole solution | 7–29 ms |
| live check_change, unchanged file | 96–305 ms |
| live check_change, edited file | 96–385 ms |
| check_change renaming a Core method used across the solution (6 projects) | 1.08 s, 6 new errors found |
| save → flush → fresh `find_symbol` answer, through the MCP server | 0.64 s (5 files re-walked in 0.3 s) |

Ranges are the spread across two runs minutes apart; the lower figures are with Roslyn's caches warm. The
what-if compile is the expensive one by design: it is an incremental build of the file's project and every
dependent, and it is still cheaper than finding out from `dotnet build` after the edit is on disk.

The staleness story changed shape. Before, a save made within the 3-second freshness cache was invisible until
the cache expired, and any save at all meant `⚠ STALE INDEX` until someone re-ran the indexer. Now the server
asks the daemon first (a 0.1 ms pipe call), and when a save is pending or being indexed it waits for that pass,
so the answer reflects the working tree. `STALE` remains only for the case where no daemon is running.

## Round 2: held reader, and a probe regression found and fixed

Measured 2026-11-09 on a 758-node, 4,397-edge graph of this repo. Tool-call latency is the median wall time of
a `tools/call` round trip over stdio against the published server, driven by a script, with no daemon running.
Three builds were measured on the same graph: the original server before any of this work (`1dfea57`), the
server after the live-daemon round (`c60a16b`), and this round.

| tool call, back-to-back | original `1dfea57` | after daemon round `c60a16b` | now |
|---|---|---|---|
| `graph_stats` | 12.1 ms | 513 ms | 1.3 ms |
| `find_symbol 'Dispose'` | 15.7 ms | 517 ms | 4.0 ms |
| `impact_of` depth 5 | 27.7 ms | 530 ms | 12.9 ms |

| tool call, 0.5 s apart (a model's pace) | original `1dfea57` | after daemon round `c60a16b` | now |
|---|---|---|---|
| `graph_stats` | 13.3 ms | 514 ms | 2.2 ms |
| `find_symbol 'Dispose'` | 17.9 ms | 519 ms | 5.6 ms |
| `impact_of` depth 5 | 31.4 ms | 532 ms | 17.2 ms |

| component | value |
|---|---|
| open read-only, nothing holding the database | 9.9 ms |
| open read-only with the anchor held | 0.025 ms |
| daemon probe when none runs | 0.012 ms |
| full index while the server answers a call every 0.2 s | completes, 9.4 s wall; the server sees the new graph on its next call |

**The regression.** The daemon round added a probe before every tool call. On Linux a named-pipe connect to
a socket that does not exist does not fail; .NET retries until the connect timeout, which was 500 ms. So
every tool call without a daemon cost half a second, and the round's own benchmark missed it because it timed
store queries, not tool calls through the server. The probe now checks that the endpoint exists before
connecting, uses a 150 ms timeout for a socket left by a killed daemon, and remembers a dead socket for 3 s.

**The anchor.** One read-only connection held between calls keeps DuckDB's instance loaded, so each call's
own open drops from about 10 ms to 25 µs. It is released after 1.5 s idle, after 10 s of continuous use, and
whenever the daemon reports pending saves, so the indexer's write lock waits at most a couple of seconds.
Net against the original server: a cheap call is about 10× faster and `impact_of` about 2× faster.

## Round 3: framework calls as External nodes

Measured 2026-11-09 on this repo, three builds back to back: before the change, a first attempt that used
ordinary `Calls` edges for framework targets, and the final design with a separate `CallsExternal` kind.

| measurement | before | first try (`Calls`) | final (`CallsExternal`) |
|---|---|---|---|
| graph | 833 nodes, 4892 edges | 1348 nodes, 7527 edges | 1352 nodes, 7564 edges |
| full index | 9.4 s | 9.7 s | 9.6 s |
| graph file after full index | 9,228 KB | 10,508 KB | 10,508 KB |
| no-op incremental index | 2.5 s | 2.5 s | 2.5 s |
| callers_of depth 1 | 6.8 ms | 7.4 ms | 6.6 ms |
| impact_of depth 5 | 8.5 ms | 9.3 ms | 8.6 ms |
| covered_by depth 30 | 8.2 ms | 8.8 ms | 7.8 ms |
| path_between depth 10 | 12 ms | 15 ms | 11 ms |
| coverage_stats | 13 ms | 13 ms | 11 ms |
| uncovered | 13 ms | 14 ms | 11 ms |
| dead_code_report | 12 ms | 12 ms | 11 ms |

The first attempt added 58% more `Calls` edges, and every recursive walk scans the edges of its kinds, so the
solution-only walks slowed by up to 50% in an earlier run. Giving framework calls their own edge kind leaves those
scans exactly as large as before; a walk that starts at an external member seeds its callers through the `to_id`
index instead. Indexing costs nothing measurable because the targets were already bound for the call edges.
The graph gains 516 framework members for this repo; `Path.Combine(string, string)` is the most called, with 37
callers.

## Round 4: multi-target projects compiled once

A scratch copy of `CodeGraph.Core` targeting `net10.0;net10.0-windows`, with one file compiled only for the
Windows target. Full index, three runs each, same machine.

| mode | graph | full index (3 runs) |
|---|---|---|
| `--all-targets` (the old behaviour) | 421 nodes, 1252 edges | 3.7 s, 3.7 s, 3.7 s |
| default: later targets walk only their own files | 421 nodes, 1252 edges | 3.3 s, 3.2 s, 3.2 s |

The graphs are identical: the Windows-only file and its call are indexed, and both project nodes contain their
files. About 2.5 s of each run is the MSBuild load, so the second compilation was most of the remaining time;
the saving grows with each extra target (an `net10.0;net10.0-android;net10.0-ios` app skips two of three).
The trade-off: code that differs between targets under `#if` is indexed as the first target sees it.
`--all-targets` restores the old behaviour.

## Round 5: data_flow

`data_flow` on the most-called method with parameters, through the daemon on this repo: **0.42 ms** median
(7 runs). The same daemon's other live calls in that run: ping 0.25 ms, symbol_at 0.31 ms, source references
7.1 ms, metadata references 4.3 ms, one-file diagnostics 0.39 ms, whole-solution diagnostics 37 ms,
check_change 0.4–0.5 s.
