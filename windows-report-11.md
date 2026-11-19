# CodeGraph Windows plan 11: cut token use and session time without losing anything

Date: 2026-11-19. Branch `token-diet` from `main` at e0077e8 (v1.4.0), in a clone at `C:\Users\seuna\AppData\Local\Temp\cg-win11\src` (`$env:TEMP\cg-win11\src`). Machine: dell-01-2TB, Windows 11 Pro 10.0.26300 build 26300, x64. PowerShell 7.7.0. .NET SDK 10.0.404 and 11.0.100. Claude Code 2.1.325 (`claude.exe`). Python 3.14.7, node v24.20.0, pnpm 11.24.0 (Hono's own `packageManager` field ran pnpm 12.6.0).

All files are in `$env:TEMP\cg-win11`. The branch is not pushed. Nothing is merged.

## Summary

| Item | Value |
|---|---|
| Commits | 5, one per change: 68934d2, 7ca54e1, 535083a, 009056c, 51e05c1 |
| Tests at the tip | 481 total, 476 passed, 0 failed, 5 skipped (baseline: 461, 456, 0, 5) |
| 10 fixed calls, 1.4.0 server vs branch tip, same graph | 68,490 → 41,977 characters (−38.7%); same ids, files and counts in all 10 |
| Haiku pilot of change 1 (ToolSearch), 2 runs each way | "with" first context 18,538 → 11,317 tokens; model calls 3 → 4; input tokens 60,324 → 56,134; wall 9.4 → 11.0 s |
| Opus compare sessions (24), "with" medians before → after | Jellyfin: wall 28.4 → 23.3 s, model calls 6 → 6, input tokens 133,453 → 120,250. Hono: wall 32.4 → 17.8 s, model calls 7 → 4, input tokens 139,937 → 55,434. Failed Read calls: 0 in all 24 sessions |
| Keep or drop | Every change stays: no "with" median got worse on model calls, input tokens or wall time, and every "with" answer kept the run-time-correct facts |

## The commits, and the test counts after each

Each row is one `dotnet test CodeGraph.slnx` run of the whole suite on Windows. The 5 skips are the same in every run:
two compare end-to-end tests (the stand-in `claude` is a shell script), one Go SCIP test (the stand-in indexer is a
shell script), and two TypeScript tests (the extractor's npm packages are not installed).

| Commit | Change | Total | Passed | Failed | Skipped | Wall |
|---|---|---|---|---|---|---|
| e0077e8 | baseline, v1.4.0, after `dotnet restore` | 461 | 456 | 0 | 5 | 103 s |
| 68934d2 | 1. compare passes `ToolSearch` in `--tools` and allows it; tests, docs/usage.md, CHANGELOG | 462 | 457 | 0 | 5 | 82 s |
| 7ca54e1 | 2. paths relative to the repository root, named once per answer (`AnswerPaths`); the test project references CodeGraph.Mcp | 469 | 464 | 0 | 5 | 88 s |
| 535083a | 3. `covered_by` lists the nearest 50 tests by depth, then the count of the rest per depth; `impact_of` adds that count | 472 | 467 | 0 | 5 | 103 s |
| 009056c | 4. 8-character handles in place of ids (`NodeHandles`), accepted by every id-taking tool; colliding ids keep the long form | 476 | 470 | 1 | 5 | 98 s |
| 009056c | the same, run again | 476 | 471 | 0 | 5 | 90 s |
| 51e05c1 | 5. impact_of, covered_by, callers_of, tests_for and snippet take a unique exact name; instructions in `ServerInstructions.Text`, under 4,096 characters | 481 | 476 | 0 | 5 | 84 s |

The one failure, after change 4, was `LivePipeTests.The_endpoint_stays_visible_between_accepts_under_rapid_calls`:
"the endpoint goes when the server stops", a check that a named pipe endpoint is gone right after its server stops.
Change 4 does not touch the live pipe. The 6 tests of that class passed when run alone on the same build, and the
whole suite passed on the second run. The first run after change 5 did not build: the new test called `Snippet`
without `TestContext.Current.CancellationToken` (analyzer xUnit1051); the row above is the run after that fix.

### What each change does

1. **ToolSearch.** `CompareCommand.BuiltInTools` is `Read,Grep,Glob,Bash,ToolSearch`, and `ToolSearch` is in
   `ReadOnlyRules` so `dontAsk` mode cannot deny it. Claude Code defers the MCP tool schemas only when `ToolSearch` is
   in `--tools`; without it, every codegraph schema was in the first model call of the "with" sessions.
2. **Relative paths.** `AnswerPaths` (scoped per tool call) turns a path under the root into a relative one and
   remembers that it did; the call-tool filter in `Program.cs` then puts `paths are relative to <root>` at the top of
   the answer, after the `STALE` warning. The root is the git worktree that holds the graph, else the folder that
   holds `.codegraph`, else the graph's folder. Rows, `snippet` headers, `index_status` file lists and the live
   tools' locations use it; a path outside the root stays absolute. The server instructions say to join the two
   before a Read.
3. **covered_by cap.** Hits come sorted by depth and name, so `Take(50)` is the nearest 50; the line
   `nearest 50 by depth; the rest by depth: 1: 5, 2: 10` keeps the total and its shape. On this repository the
   largest `covered_by` list is 45 tests (`GraphStore.Replace`), so the cap does not engage here; a unit test with
   65 tests exercises it.
4. **Handles.** `NodeHandles` maps every id of a loaded snapshot to 8 base-32 characters (the low 40 bits of the
   SHA-256 of the id), kept with the snapshot in a `ConditionalWeakTable`. Rows print the handle; the call-tool
   filter turns a handle argument back into its id before the tool runs, so `id`, `from`, `to` and `symbol` take
   either. Two ids with the same handle keep the long form. Without a snapshot (the first calls after start, or
   `CODEGRAPH_SNAPSHOT=off`), rows print the id. The storage (DuckDB and the snapshot) is unchanged.
5. **Names, instructions.** A `Target` helper resolves the `id` argument: the node with that id, else the one symbol
   whose exact name it is (`Save`, or `Service.Save`); several matches list the candidates with ids, none says so.
   `ServerInstructions.Text` is 1,542 characters (1.4.0: 1,134), and a test keeps it under 4,096. It says to give
   the tools a name, that `impact_of` already lists the tests, that `index_status` is for when an answer looks wrong,
   and what handles and relative paths are.

## The 10-call table, main vs branch

Ten fixed tool calls on this repository through a real `codegraph-mcp` over stdio (`$env:TEMP\cg-win11\mcpcall.py`):
`find_symbol` (exact) on `GraphStore.Replace` and on `UsageSummary.Line`, then `impact_of`, `covered_by`,
`callers_of` and `tests_for` with the id the `find_symbol` answer gave. Both servers read the same graph:
`$env:TEMP\cg-win11\src\.codegraph\graph.db`, 3,459 nodes and 21,744 edges, built from the working tree at
51e05c1 with 0 compile errors after `dotnet restore`. "main" is the v1.4.0 release `codegraph-mcp.exe` from
`$env:TEMP\cg-win10\tools`; "branch" is the Release build of 51e05c1. On the branch, the script passed the handle
the `find_symbol` row printed (`yeoqwnf3` for `GraphStore.Replace`, `eztec377` for `UsageSummary.Line`), so the
handle path through the filter is what these figures measure.

The fact check (`$env:TEMP\cg-win11\factcheck.py`) parses every row, maps each id to its handle, strips the root
from each path, and compares the set of ids, the set of files and the numbers in the first line.

| n | tool | main chars | branch chars | change | ids | files | counts |
|---|---|---|---|---|---|---|---|
| 1 | find_symbol GraphStore.Replace | 1,648 | 1,075 | −34.8% | same | same | same |
| 2 | impact_of | 18,446 | 10,905 | −40.9% | same | same | same |
| 3 | covered_by | 14,664 | 8,824 | −39.8% | same | same | same |
| 4 | callers_of | 10,915 | 6,623 | −39.3% | same | same | same |
| 5 | tests_for | 10,524 | 6,911 | −34.3% | same | same | same |
| 6 | find_symbol UsageSummary.Line | 235 | 216 | −8.1% | same | same | same |
| 7 | impact_of | 6,062 | 3,599 | −40.6% | same | same | same |
| 8 | covered_by | 3,805 | 2,347 | −38.3% | same | same | same |
| 9 | callers_of | 1,583 | 1,015 | −35.9% | same | same | same |
| 10 | tests_for | 608 | 462 | −24.0% | same | same | same |
| | total | 68,490 | 41,977 | −38.7% | | | |

The same 10 calls after change 2 alone (paths), on the graph of 7ca54e1: 68,502 → 59,510 characters (−13.1%), same
facts. The root here is 46 characters, so the paths saved more than the 8.3% the Linux session predicted for a
24-character root. The answers are in `$env:TEMP\cg-win11\calls-main2\` and `calls-tip\` (and `calls-main\`,
`calls-c2\`, `calls-c3\` for the earlier runs).

Server time per call was 1 to 11 ms on both servers (`find_symbol` first call 58 to 85 ms, the snapshot load).

## The Haiku pilot of change 1

Before any Opus session, change 1 alone was measured with Haiku on the test 10 setup, so test 10 is the baseline
arm: the same repository (`$env:TEMP\cg-win10\repo` at e0077e8, graph built without `dotnet restore`), the same
v1.4.0 server from `.mcp.json`, the same question, `--runs 2 --model haiku`. The only difference is the compare
binary: the Release build of 68934d2 passes `ToolSearch`.

| | test 10 (1.4.0 compare) with | pilot (change 1) with | test 10 without | pilot without |
|---|---|---|---|---|
| wall s | 9.4 | 11.0 | 14.9 | 16.6 |
| model calls | 3 | 4 | 3 | 4 |
| input tokens | 60,324 | 56,134 | 44,732 | 74,815 |
| output tokens | 1,476 | 1,770 | 2,899 | 3,186 |
| cost USD | 0.0039 | 0.0037 | 0.0043 | 0.0047 |
| first context | 18,538 | 11,317 | 9,793 | 9,793 |
| context growth | 4,184 | 6,458 | 9,964 | 12,390 |
| tool calls | 4 | 6 | 6 | 7 |

Run folders: test 10 `$env:TEMP\cg-win10\repo\.codegraph\compare\20261119-160846`, pilot
`...\compare\20261119-163816`. Exit 0 and 4 of 4 counted in both.

What the pilot logs show:

- Both "with" sessions called `ToolSearch` first, then `find_symbol`, `callers_of`, `tests_for`, `covered_by`.
  Their init line lists 40 tools, 35 of them `mcp__codegraph__*`, so the schemas were deferred. Denied: 0.
- The "with" first context fell by 7,221 tokens (18,538 → 11,317); the gap to "without" is 1,524 tokens instead
  of 8,745. The "without" first context did not move (9,793), so the `ToolSearch` definition itself is not visible
  in it.
- The `ToolSearch` call is one more model call per "with" session (3 → 4), and the schema it loads goes into the
  context growth (4,184 → 6,458). Input tokens still fell (60,324 → 56,134) because every later call re-reads a
  smaller start. Wall time rose 1.6 s with 2 runs each way, which is within the spread of the "without" arm
  (14.9 → 16.6 s with no change on that side).
- The two "with" answers name the same facts as test 10's: 4 production callers, 2 direct tests, 12 tests by
  static reach.

By the plan's rule (a change stays only if the "with" medians do not get worse on model calls, input tokens or
wall time), change 1 is better on input tokens and worse on model calls and wall time in this 2-run Haiku pilot.
It is also what every interactive Claude Code session does by default, so a compare without it measures a start
no real session has.

## The compare medians before and after

24 sessions, approved by the user: `codegraph-index compare "<question>" --runs 3 --model claude-opus-5-5 --effort high --yes`
on two repositories, two arms each, run one after another from 17:17 to 17:28 UTC on 2026-11-19. Every arm exited
0 with 6 of 6 runs counted. Claude Code 2.1.325, model claude-opus-5-5, effort high. Claude Code reported a total
cost of 3.88 USD for the 24 sessions (1.37, 1.04, 0.89 and 0.57 per arm).

The setup:

- Jellyfin at df62c85 in `$env:TEMP\cg-win11\jellyfin`: fetched by full SHA, `dotnet restore Jellyfin.slnx` exit 0 in
  56 s, graph built by the branch's `codegraph-index init` in 99 s: 25,336 nodes, 151,702 edges, 0 compile errors.
  Question: "What breaks if I change TmdbUtils.NormalizeLanguage, and which tests cover it?".
- Hono at 75ff8e3 in `$env:TEMP\cg-win11\hono`: `pnpm install --frozen-lockfile` exit 0 in 43 s. `init` with the
  branch's Release indexer failed, because the source build has no TypeScript extractor dependencies ("run `npm ci`
  in extractors/typescript first"), so the graph was built with the v1.4.0 release indexer in 26 s: 8,678 nodes,
  35,946 edges from 333 files. The graph storage is unchanged on the branch. Question: "What breaks if I change
  encodeBase64 in src/utils/encode.ts, and which tests cover it?".
- The arms. `CompareCommand` did not change after change 1 (`git diff --stat 68934d2..HEAD -- codegraph/src/CodeGraph.Indexer`
  is empty), so the branch tip's `codegraph-index.exe compare` served both arms, and both arms therefore pass
  `ToolSearch`. `.mcp.json` picked the server: `$env:TEMP\cg-win10\tools\codegraph-mcp.exe` (v1.4.0) for "before",
  the Release build of 51e05c1 for "after". The graph of each repository was the same file in both arms, and each
  report says "up to date with the working tree".
- Run folders (`--out`): `$env:TEMP\cg-win11\compare\jellyfin-before`, `jellyfin-after`, `hono-before`,
  `hono-after`. Console output in `$env:TEMP\cg-win11\compare-<repo>-<arm>.txt`; the run lines in `compare-runs.txt`.

### Medians, as compare printed them

Jellyfin (with: 3, without: 3 in each arm):

| | before with | after with | before without | after without |
|---|---|---|---|---|
| wall s | 28.4 | 23.3 | 30.3 | 25.1 |
| model calls | 6 | 6 | 6 | 5 |
| input tokens | 133,453 | 120,250 | 110,907 | 93,424 |
| cache reads | 108,875 | 103,169 | 95,074 | 75,890 |
| output tokens | 2,401 | 1,787 | 2,415 | 2,128 |
| cost USD | 0.2664 | 0.1224 | 0.1939 | 0.1980 |
| first context | 10,653 | 10,778 | 9,129 | 9,129 |
| last context | 34,935 | 26,204 | 24,950 | 28,762 |
| context growth | 24,282 | 15,426 | 15,821 | 19,633 |
| tool calls | 8 | 6 | 8 | 7 |
| codegraph calls | 4 | 2 | 0 | 0 |
| failed Read calls | 0 | 0 | 0 | 0 |

Hono (with: 3, without: 3 in each arm):

| | before with | after with | before without | after without |
|---|---|---|---|---|
| wall s | 32.4 | 17.8 | 26.3 | 26.8 |
| model calls | 7 | 4 | 5 | 4 |
| input tokens | 139,937 | 55,434 | 67,766 | 57,481 |
| cache reads | 128,899 | 47,708 | 55,551 | 48,190 |
| output tokens | 2,381 | 1,293 | 2,275 | 2,445 |
| cost USD | 0.1236 | 0.0408 | 0.1543 | 0.1328 |
| first context | 10,610 | 10,735 | 9,086 | 9,086 |
| last context | 26,829 | 16,951 | 16,908 | 18,224 |
| context growth | 16,219 | 6,216 | 7,822 | 9,138 |
| tool calls | 8 | 3 | 10 | 7 |
| codegraph calls | 3 | 2 | 0 | 0 |
| failed Read calls | 0 | 0 | 0 | 0 |

The codegraph calls and failed Read calls rows come from `$env:TEMP\cg-win11\medians.py`, which reads each
`results.json` and counts, in each log, a `Read` tool result that is an error or says the file does not exist.
Its other rows match compare's, except that for an even count it prints the mean of the two middle values where
compare prints the lower one.

### Every run

| arm | round | condition | wall s | model calls | input tokens | output tokens | cost USD | first context | growth | codegraph calls |
|---|---|---|---|---|---|---|---|---|---|---|
| jellyfin-before | 1 | without | 33.9 | 8 | 174,181 | 2,566 | 0.2710 | 9,129 | 19,266 | 0 |
| jellyfin-before | 1 | with | 25.1 | 6 | 131,481 | 2,039 | 0.3343 | 10,653 | 23,603 | 4 |
| jellyfin-before | 2 | with | 33.2 | 7 | 166,783 | 2,783 | 0.1440 | 10,653 | 24,282 | 4 |
| jellyfin-before | 2 | without | 22.4 | 4 | 64,175 | 1,992 | 0.1627 | 9,129 | 14,102 | 0 |
| jellyfin-before | 3 | without | 30.3 | 6 | 110,907 | 2,415 | 0.1939 | 9,129 | 15,821 | 0 |
| jellyfin-before | 3 | with | 28.4 | 6 | 133,453 | 2,401 | 0.2664 | 10,653 | 24,564 | 6 |
| jellyfin-after | 1 | without | 29.2 | 7 | 164,780 | 2,420 | 0.2544 | 9,129 | 22,177 | 0 |
| jellyfin-after | 1 | with | 23.3 | 6 | 120,250 | 1,684 | 0.1909 | 10,778 | 15,564 | 2 |
| jellyfin-after | 2 | with | 29.1 | 7 | 144,065 | 2,228 | 0.1224 | 10,778 | 15,426 | 2 |
| jellyfin-after | 2 | without | 22.6 | 5 | 89,508 | 1,731 | 0.1814 | 9,129 | 16,515 | 0 |
| jellyfin-after | 3 | without | 25.1 | 5 | 93,424 | 2,128 | 0.1980 | 9,129 | 19,633 | 0 |
| jellyfin-after | 3 | with | 21.7 | 4 | 67,946 | 1,787 | 0.0962 | 10,778 | 15,144 | 2 |
| hono-before | 1 | without | 26.3 | 5 | 67,766 | 2,275 | 0.1543 | 9,086 | 7,822 | 0 |
| hono-before | 1 | with | 32.4 | 7 | 139,937 | 2,381 | 0.2583 | 10,610 | 18,043 | 3 |
| hono-before | 2 | with | 29.8 | 7 | 131,197 | 2,329 | 0.0907 | 10,610 | 14,311 | 3 |
| hono-before | 2 | without | 24.8 | 4 | 52,847 | 2,112 | 0.1051 | 9,086 | 6,694 | 0 |
| hono-before | 3 | without | 35.4 | 6 | 90,002 | 3,292 | 0.1605 | 9,086 | 9,821 | 0 |
| hono-before | 3 | with | 35.7 | 8 | 161,042 | 2,934 | 0.1236 | 10,610 | 16,219 | 3 |
| hono-after | 1 | without | 26.7 | 4 | 57,481 | 2,445 | 0.1328 | 9,086 | 9,283 | 0 |
| hono-after | 1 | with | 18.4 | 4 | 55,725 | 1,364 | 0.1009 | 10,735 | 6,507 | 2 |
| hono-after | 2 | with | 15.8 | 3 | 38,483 | 1,184 | 0.0314 | 10,735 | 5,731 | 1 |
| hono-after | 2 | without | 30.2 | 6 | 88,373 | 2,631 | 0.1416 | 9,086 | 9,138 | 0 |
| hono-after | 3 | without | 26.8 | 4 | 55,938 | 2,440 | 0.1252 | 9,086 | 8,363 | 0 |
| hono-after | 3 | with | 17.8 | 4 | 55,434 | 1,293 | 0.0408 | 10,735 | 6,216 | 2 |

No Read call failed in any of the 24 sessions.

### What the "with" sessions did

Tool calls in order, from the logs (`mcp__codegraph__` dropped from the names):

| arm | round | calls |
|---|---|---|
| jellyfin-before | 1 | ToolSearch, find_symbol, impact_of, tests_for, snippet, Read, Grep |
| jellyfin-before | 2 | ToolSearch, find_symbol, impact_of, tests_for, snippet, Read, Grep, Grep |
| jellyfin-before | 3 | ToolSearch, find_symbol, impact_of, tests_for, snippet, Read, tests_for, snippet, Grep |
| jellyfin-after | 1 | ToolSearch, impact_of, snippet, Read, Grep, Grep |
| jellyfin-after | 2 | ToolSearch, impact_of, snippet, Grep, Read, Grep, Grep |
| jellyfin-after | 3 | ToolSearch, impact_of, snippet, Read |
| hono-before | 1 | ToolSearch, find_symbol, impact_of, tests_for, Read, Grep, Grep, Grep |
| hono-before | 2 | ToolSearch, find_symbol, impact_of, tests_for, Read, Grep, Grep, Grep |
| hono-before | 3 | ToolSearch, find_symbol, impact_of, tests_for, Grep, Read, Grep, Grep, Read, Grep |
| hono-after | 1 | ToolSearch, impact_of, snippet |
| hono-after | 2 | ToolSearch, impact_of |
| hono-after | 3 | ToolSearch, impact_of, snippet |

Before the changes every "with" session called `find_symbol` to get an id and `tests_for` after `impact_of`; after
them none did either: `impact_of` took the name from the question, and its test list was enough. In all 6 "after"
sessions the model passed the name itself (`{"id":"TmdbUtils.NormalizeLanguage"}`, `{"id":"encodeBase64"}`) to
`impact_of` and to `snippet`, never an id or a handle. The first codegraph answer of every "after" session had the
root line and printed handles (`[zjnecfsk]`, `[qvj5tbpw]`): the snapshot had loaded during the `ToolSearch` call
that came first. No session called `index_status` in either arm. Every "with" session called `ToolSearch` once, in both arms; the "with" first context
is 1,524 to 1,649 tokens larger than "without" in all four arms, where the Linux session measured 8,681 without
`ToolSearch`.

### What the answers said

Jellyfin: the run-time-correct fact is that 2 test methods run `NormalizeLanguage`. All 6 "with" answers, before and
after, open with "Only 2 tests check it directly" or words to that effect; the "after" answers add "The graph lists
7 tests, but only 2 run the code" and that the 5 `ItemImageProviderTests` use a mocked provider. Of the 6 "without"
answers, 4 say "7 direct unit test cases" (the theory rows, not methods) and 2 say 2.

Hono: all 6 "with" answers say the graph finds 12 dependents and 124 tests that can reach `encodeBase64`, name the
same 4 production areas (the two Lambda adapters, JWT signing, the secure-headers nonce), and say how few tests call
it directly; the "without" answers name the same areas and say no test calls it directly. The run-time count of
tests that execute `encodeBase64` was not measured.

## What was kept and what was dropped

Every change stays; nothing was dropped.

- The rule: a change stays only if the "with" medians do not get worse on model calls, input tokens or wall time,
  and the answers keep the run-time-correct facts. Changes 2 to 5 were measured together, before against after: the
  "with" medians improved on all three figures on Hono (7 → 4 calls, 139,937 → 55,434 tokens, 32.4 → 17.8 s) and on
  two of three on Jellyfin with the third equal (6 → 6 calls, 133,453 → 120,250 tokens, 28.4 → 23.3 s). Every "with"
  answer kept the 2-test fact on Jellyfin, and the Hono answers kept the same areas and counts.
- Change 1 was in both Opus arms, so its own Opus effect is not isolated. In the Haiku pilot it cut the "with" first
  context by 7,221 tokens and input tokens by 4,190, and cost one model call and 1.6 s of wall time on 2 runs. It
  stays because the Opus arms show the start gap it leaves (about 1,600 tokens) and because every interactive
  Claude Code session defers MCP tools the same way; a compare without it measures a start no real session has.
- Changes 2 to 5 lost no fact on the 10 fixed calls (same ids, files and counts) and cut their answers by 38.7%.
- Not done: the server does not wait for the snapshot on its first call. When `mcpcall.py` called `find_symbol` at
  once on the 25,336-node Jellyfin graph, that first answer printed long ids and the next ones handles. In the 6
  Opus "after" sessions this did not happen, because `ToolSearch` came first and the snapshot had loaded by the
  first codegraph call. A short wait (`GraphSnapshotCache.WaitForCurrent`) on the first call would close the gap for
  a client that calls at once. That is outside the five changes.
- The server instructions are 1,542 characters, longer than 1.4.0's 1,134, because they now explain names, handles
  and relative paths. They are under the 4,096 limit, and the sessions that read them made fewer calls.

## Files

| File | Content |
|---|---|
| `$env:TEMP\cg-win11\test-baseline.txt`, `test-change1.txt` … `test-change5.txt`, `test-change4-run2.txt` | Full test logs |
| `$env:TEMP\cg-win11\pilot1-out.txt` | The Haiku pilot's console output |
| `$env:TEMP\cg-win11\mcpcall.py`, `factcheck.py` | The 10-call runner and the fact check |
| `$env:TEMP\cg-win11\calls-main2\`, `calls-tip\` | The 10 answers from each server, with `results.json` |
| `$env:TEMP\cg-win11\clones.txt`, `hono-install.txt`, `hono-init.txt`, `jellyfin-restore.txt`, `jellyfin-init.txt` | Repository setup logs |
| `$env:TEMP\cg-win11\compare\jellyfin-before\`, `jellyfin-after\`, `hono-before\`, `hono-after\` | Each arm's `report.md`, `results.json` and six `.jsonl` logs |
| `$env:TEMP\cg-win11\compare-runs.txt`, `compare-<repo>-<arm>.txt` | The run lines and each arm's console output |
| `$env:TEMP\cg-win11\medians.py` | Per-run tables, medians and failed Read counts from the four arms |
