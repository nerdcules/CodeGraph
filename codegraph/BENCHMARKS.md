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

## Round 6: semantic search plumbing

No embedding model runs on this machine, so these numbers use a local stand-in endpoint (a Python HTTP server
that hashes words into 256 dimensions). They measure what CodeGraph adds: request batching, storage, cache
reuse and the vector query. A real model adds its own inference time on top, once per changed declaration at
index time and once per distinct query at search time. Ranking quality with a real model is not measured here.

| measurement | without embeddings | with embeddings |
|---|---|---|
| full index (2 runs) | 10.2 s, 10.1 s | 10.3 s, 10.4 s |
| embedding step inside it | | 0.2 s for 839 declarations |
| no-op incremental index | 2.5 s | 2.5 s (0 declarations sent) |

| `search_code` through the server, median of 20 queries | latency |
|---|---|
| `mode=lexical` | 11.1 ms |
| `mode=semantic` | 4.5 ms |
| `mode=hybrid` (the default when vectors exist) | 14.2 ms |

**Storage and scale.** A first version stored float lists in DuckDB and searched with `list_cosine_similarity`.
Measured at a larger scale it did not hold up, so vectors are now quantised to one signed byte per component
(scaled per vector so each uses the full range, with the scale stored beside it) and searched in memory with
SIMD dot products; the server loads them once per graph file. Synthetic random 768-dimension vectors, the
hardest case for ranking because their cosines bunch together:

| vectors × 768 | design | write | search | file | top-10 agreement with exact cosine |
|---|---|---|---|---|---|
| 839 | float list in DuckDB | 0.1 s | 7.7 ms | 3 MB | exact |
| 839 | 8-bit, in memory | 0.1 s | 2.9 ms (43 ms first, loading) | 2 MB | 100 / 100 |
| 40,000 | float list in DuckDB | 2.4 s | 197 ms | 195 MB | exact |
| 40,000 | fixed `FLOAT[768]` in DuckDB | 2.0 s | 123 ms | 189 MB | exact |
| 40,000 | 8-bit, in memory | 0.5 s | 8.0 ms (131 ms first, loading) | 37 MB | 100 / 100 |

A first quantiser scaled every unit vector by a fixed 127, which left 768-dimension components about a dozen
levels; a test comparing against exact cosine caught it at 179 / 200 agreement, and per-vector scaling fixed it.

## Round 7: large graphs (synthetic, up to 165,000 methods)

Synthetic call graphs shaped like layered application code: 12 layers, each method calling five in the next
layers, 10% of calls going to 20 hub methods per layer, and tests calling into random layers.

**Store queries before this round:**

| query | 11k nodes, 49k edges | 55k nodes, 244k edges | 165k nodes, 732k edges |
|---|---|---|---|
| callers depth 1 (hub) | 9 ms | 23 ms | 48 ms |
| impact depth 5 | 19 ms | 50 ms | 95 ms |
| impact depth 20 (hub) | 70 ms | 242 ms | 662 ms |
| covered_by depth 30 (hub) | 44 ms | 142 ms | 330 ms |
| callees depth 10 | 58 ms | 149 ms | 298 ms |
| coverage_stats | 21 ms | 68 ms | 164 ms |
| path_between depth 10 | 2,368 ms | 1,553 ms | 1,061 ms |

**DuckDB `USING KEY` recursion** (keeps one row per node instead of one per node and depth) was the planned
fix for the walks. On the 150k graph it was slower, 426 ms against 251 ms for the same walk, because the
duplicate rows it removes are only 3.3 per node; most of the walk's cost is the 105,000 nodes a hub genuinely
reaches. It is not adopted.

**What was slow instead** was `path_between`, which enumerated every simple path up to the depth limit before
keeping three. It is now a breadth-first search, one indexed query per level and each node visited once, then
a walk back over parents. It returns up to `maxPaths` paths of the shortest length only.

| path_between | 11k nodes | 55k nodes | 165k nodes |
|---|---|---|---|
| before | 2,368 ms | 1,553 ms | 1,061 ms |
| after | 38 ms | 105 ms | 60 ms |
| after, no path exists | 1.9 ms | 3.9 ms | 4.9 ms |

**And the answers were too big.** On a hub, `impact_of` and `callers_of` printed every reachable node. Traversal
tools now list the nearest 200 rows (50 for guarding tests) and summarise the rest as counts per depth.
Through the server on the 55k graph:

| tool call | before: latency, answer | after: latency, answer |
|---|---|---|
| `impact_of` hub, depth 20 | 452 ms, 2,473,442 chars (~618,000 tokens) | 334 ms, 16,231 chars (~4,100 tokens) |
| `callers_of` hub, depth 5 | 124 ms, 1,379,921 chars (~345,000 tokens) | 139 ms, 13,151 chars (~3,300 tokens) |
| `path_between` | 1,685 ms, 709 chars | 84 ms, 711 chars |

The old `impact_of` answer would not fit in any model's context window.

Two defects surfaced while measuring this round and are fixed: a flush arriving while the daemon's debounce was
already running waited out the debounce (up to 500 ms), and a test's fake embedder used .NET's per-process string
hash, which made one ranking test fail about one run in ten.

## Round 8: ReadyToRun publish

The install scripts now publish ReadyToRun for the machine's platform (`-r <rid> -p:PublishReadyToRun=true`)
instead of a plain framework-dependent build. Native AOT was measured first and rejected. The indexer cannot use
it: MSBuildLocator loads MSBuild from the SDK at runtime, and Roslyn's workspace services are composed by
reflection. The server failed to publish: the web routes return anonymous types, and there were 27 trim and AOT
warnings from reflection-based JSON and tool discovery. AOT would also only shorten start-up, since warm tool calls
are spent in DuckDB, which is native already.

Same machine, this repo's graph; index times are three runs each.

| measurement | plain publish | ReadyToRun |
|---|---|---|
| full index, wall time | 11.3–11.6 s | 8.7–8.8 s |
| index work inside that | 8.9–9.0 s | 6.5–6.6 s |
| server start to `initialize` answered | 327 ms | 257 ms |
| first `find_symbol` after start | 141 ms | 103 ms |
| first `impact_of` after start | 31 ms | 28 ms |
| warm `find_symbol` | 6.0 ms | 5.9 ms |
| server publish folder | 318 MB (DuckDB for every OS) | 75 MB (this platform only) |

Most of the index time is JIT compilation of Roslyn and MSBuild code paths, which ReadyToRun removes; every
one-shot index and every daemon start pays it. Warm calls are unchanged.

## Where it ended up

This repo's own graph, first baseline against the final build of this work, same machine and method. The graph
is 2.5 times larger now: the repo grew, and framework calls are External nodes.

| measurement | first baseline | final |
|---|---|---|
| graph | 595 nodes, 3,558 edges | 1,583 nodes, 8,784 edges |
| full index | 11.5 s | 10.5 s |
| no-op incremental index | 3.5 s | 2.5 s |
| graph file after 3 more full rebuilds | 15,884 KB (and growing) | 12,556 KB (flat) |
| open per tool call | 11 ms | 0.024 ms while the anchor holds the graph |
| impact_of depth 20 | 7.9 ms | 8.6 ms |
| path_between depth 10 | 11 ms | 3.9 ms |
| coverage_stats | 9.9 ms | 11 ms |
| dead_code_report | 12 ms | 11 ms |
| `find_symbol` through the server | 15.7 ms | 4.7 ms |
| `impact_of` through the server | 27.7 ms | 15.1 ms |
| `graph_stats` through the server | 12.1 ms | 1.3 ms |

Store queries held steady while the graph grew 2.5 times; tool calls through the server got 2 to 9 times faster;
and on large solutions `path_between` is 15 to 60 times faster and traversal answers are about 150 times smaller.
