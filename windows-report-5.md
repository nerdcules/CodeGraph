# CodeGraph Windows experiment 5: R4 alone, with and without CPU load

Date: 2026-11-15. Branch any-mcp-client at a3150ec. Machine: Windows 11 Pro 10.0.26300, x64, Intel Core i7-8700 at 3.20 GHz. Python 3.14.7.

All files are in `C:\Users\seuna\AppData\Local\Temp\cg-win5` (`$env:TEMP\cg-win5`). I did not change product code. I did not delete anything outside `$env:TEMP\cg-win5`. The only `install.ps1` command had `-InstallDir $env:TEMP\cg-win5\bin`.

## Summary

| Item | Value |
|---|---|
| Logical CPUs (`[Environment]::ProcessorCount`) | 12 (6 cores) |
| Template | 3,245 nodes, 19,848 edges |
| Idle: 3 runs | 0 errors. Every writer exit 0. p99 6.0 ms to 6.2 ms, max 547 ms to 921 ms. |
| Loaded (36 burners): 6 runs | 0 errors. Every writer exit 0. p99 123.8 ms to 229.7 ms, max 6,280 ms to 10,343 ms. 1 call over 10 s (loaded run 1). |
| CPU during the loaded runs | 100% in all 12 samples. Processor queue length 99 to 168. |
| `errs` entries | None in `idle.jsonl` and none in `loaded.jsonl`. |
| Crashes | None. Both commands exit 0. |
| Processes left after the runs | None (no `python*`, no `codegraph*`). |

## Facts to note

1. **No run had an error.** All 9 runs (3 idle, 6 loaded) had 0 errors in R4, so both files have no `errs` entries.
2. **Under load, calls are slower, but none fail.** In the loaded runs, the p99 latency went from about 6 ms to 124 ms to 230 ms. The slowest call took 10,343 ms. Index passes under load took 31.2 s to 138.3 s, and 16.8 s to 39.1 s when idle.
3. **A full index with 8 readers takes about 3 times as long.** With the 8 readers and no load, each `--full` pass took 34.8 s to 39.1 s. The same full index of the template with no readers took 11.5 s (step 2).
4. **The `last` field shows `Ã—` where the indexer printed `×`.** `r4loop.py` calls `subprocess.run(..., text=True)`. On Windows, that decodes the indexer output with the ANSI code page, not UTF-8. The text below has the field as the file stores it.
5. **The `last` field is the usage summary, not the `indexed` line.** The last line of each index pass is `last 7 days: … tool calls answered …`. Thus the phase lists do not show the node count of each pass.
6. **`load` is `null` in every record.** Windows has no `os.getloadavg`. The CPU counters in step 4 replace it.

## 1. Branch and install

In `Z:\CodeGraph`:

| Command | Exit | Output |
|---|---|---|
| `git fetch origin` | 0 | `ef202fe..a3150ec  any-mcp-client -> origin/any-mcp-client` |
| `git checkout any-mcp-client` | 0 | `Already on 'any-mcp-client'`, behind by 1 commit |
| `git pull` | 0 | `Updating ef202fe..a3150ec`, fast-forward, 2 files changed (`BENCHMARKS.md`, `CHANGELOG.md`) |
| `git rev-parse --short HEAD` | 0 | `a3150ec` |

In `codegraph`: `.\install.ps1 -FromSource -SkipTests -InstallDir $env:TEMP\cg-win5\bin` (pwsh 7.7.0): exit 0. `bin` holds `codegraph-index.exe` and `codegraph-mcp.exe`. Log: `step1-install.log`.

## 2. Template

| Command | Exit | Result |
|---|---|---|
| `git clone Z:\CodeGraph $env:TEMP\cg-win5\tpl` | 0 | HEAD a3150ec |
| `dotnet restore tpl\codegraph\CodeGraph.slnx` | 0 | 6 projects restored |
| `bin\codegraph-index.exe tpl\codegraph\CodeGraph.slnx --full` | 0 | `indexed 3245 nodes, 19848 edges (full) in 11.5s` |

## 3. The script

`$env:TEMP\cg-win5\r4loop.py` has the exact content from the plan.

## 4. Runs

Before the runs, no `python*` or `codegraph*` process was running, and the CPU was at about 4%.

### Idle

`python $env:TEMP\cg-win5\r4loop.py Z:\CodeGraph\codegraph\tools\CodeGraph.Bench $env:TEMP\cg-win5\work $env:TEMP\cg-win5\tpl $env:TEMP\cg-win5\bin\codegraph-mcp.exe $env:TEMP\cg-win5\bin\codegraph-index.exe 3 0 $env:TEMP\cg-win5\idle.jsonl`: exit 0 after 493.9 s.

```
run 0: burners 0 load None calls 443802 errors 0 writes [0, 0, 0, 0, 0, 0] p99 6.0 ms max 567 ms calls over 10 s 0
run 1: burners 0 load None calls 446691 errors 0 writes [0, 0, 0, 0, 0, 0] p99 6.2 ms max 547 ms calls over 10 s 0
run 2: burners 0 load None calls 452862 errors 0 writes [0, 0, 0, 0, 0, 0] p99 6.2 ms max 921 ms calls over 10 s 0
```

### Loaded

Burners: 3 × 12 = 36.

`python $env:TEMP\cg-win5\r4loop.py Z:\CodeGraph\codegraph\tools\CodeGraph.Bench $env:TEMP\cg-win5\work $env:TEMP\cg-win5\tpl $env:TEMP\cg-win5\bin\codegraph-mcp.exe $env:TEMP\cg-win5\bin\codegraph-index.exe 6 36 $env:TEMP\cg-win5\loaded.jsonl`: exit 0 after 2,316.5 s.

```
run 0: burners 36 load None calls 212754 errors 0 writes [0, 0, 0, 0, 0, 0] p99 229.7 ms max 8978 ms calls over 10 s 0
run 1: burners 36 load None calls 267450 errors 0 writes [0, 0, 0, 0, 0, 0] p99 123.8 ms max 10343 ms calls over 10 s 1
run 2: burners 36 load None calls 368403 errors 0 writes [0, 0, 0, 0, 0, 0] p99 124.1 ms max 9926 ms calls over 10 s 0
run 3: burners 36 load None calls 250857 errors 0 writes [0, 0, 0, 0, 0, 0] p99 222.5 ms max 8126 ms calls over 10 s 0
run 4: burners 36 load None calls 263565 errors 0 writes [0, 0, 0, 0, 0, 0] p99 128.3 ms max 6280 ms calls over 10 s 0
run 5: burners 36 load None calls 257826 errors 0 writes [0, 0, 0, 0, 0, 0] p99 124.6 ms max 8645 ms calls over 10 s 0
```

### CPU counters during loaded run 0

I started the sample when 37 `python` processes (the script and 36 burners) and 8 `codegraph-mcp` processes ran. `Get-Counter '\Processor(_Total)\% Processor Time','\System\Processor Queue Length' -SampleInterval 5 -MaxSamples 12` (UTC):

| Time | % Processor Time | Processor Queue Length |
|---|---|---|
| 12:56:50 | 100.0 | 110 |
| 12:56:55 | 100.0 | 128 |
| 12:57:00 | 100.0 | 151 |
| 12:57:06 | 100.0 | 126 |
| 12:57:11 | 100.0 | 127 |
| 12:57:16 | 100.0 | 147 |
| 12:57:21 | 100.0 | 99 |
| 12:57:26 | 100.0 | 168 |
| 12:57:32 | 100.0 | 150 |
| 12:57:37 | 100.0 | 139 |
| 12:57:42 | 100.0 | 138 |
| 12:57:47 | 100.0 | 118 |

The sample covers the end of `incr0` and the start of `full1` of loaded run 0.

### After the runs

`Get-Process python*, codegraph*` found no process. The CPU was at 10.3% and 14.6% in 2 samples.

## 5. Errors and phases

`idle.jsonl` has 3 records and `loaded.jsonl` has 6. Every record has `"errors": 0` and an empty `"errs"` list. Thus there is no error text to paste.

The phase lists follow, one table for each run. "Start" is the `start` field as UTC time. "Last line" is the `last` field as stored (fact 4).

### idle.jsonl, run 0

calls 443802, errors 0, writes [0, 0, 0, 0, 0, 0], p99 6.0 ms, max 567 ms, over 10 s 0, errs entries 0

| Phase | Start (UTC) | Seconds | Exit | Last line |
|---|---|---|---|---|
| full0 | 12:47:03.363 | 36.0 | 0 | `last 7 days: 85435 tool calls answered; the files they pointed at come to about 2,987,500,039 tokens, the answers to 209,244,788, so about 2,778,430,409 tokens stayed out of the context; most used: graph_stats Ã—29193, find_symbol Ã—28281, impact_of Ã—27961 (codegraph-index usage for details)` |
| incr0 | 12:47:39.394 | 16.8 | 0 | `last 7 days: 123078 tool calls answered; the files they pointed at come to about 4,302,711,525 tokens, the answers to 304,332,778, so about 3,999,263,102 tokens stayed out of the context; most used: graph_stats Ã—42048, find_symbol Ã—40774, impact_of Ã—40256 (codegraph-index usage for details)` |
| full1 | 12:47:56.214 | 35.0 | 0 | `last 7 days: 205789 tool calls answered; the files they pointed at come to about 7,194,138,506 tokens, the answers to 509,450,027, so about 6,685,743,042 tokens stayed out of the context; most used: graph_stats Ã—70416, find_symbol Ã—68020, impact_of Ã—67353 (codegraph-index usage for details)` |
| incr1 | 12:48:31.264 | 17.5 | 0 | `last 7 days: 244177 tool calls answered; the files they pointed at come to about 8,534,043,674 tokens, the answers to 606,520,705, so about 7,929,328,706 tokens stayed out of the context; most used: graph_stats Ã—83610, find_symbol Ã—80662, impact_of Ã—79905 (codegraph-index usage for details)` |
| full2 | 12:48:48.801 | 34.8 | 0 | `last 7 days: 324837 tool calls answered; the files they pointed at come to about 11,348,455,123 tokens, the answers to 805,988,225, so about 10,544,438,703 tokens stayed out of the context; most used: graph_stats Ã—111288, find_symbol Ã—107307, impact_of Ã—106242 (codegraph-index usage for details)` |
| incr2 | 12:49:23.622 | 17.9 | 0 | `last 7 days: 363495 tool calls answered; the files they pointed at come to about 12,699,694,957 tokens, the answers to 903,900,393, so about 11,798,547,441 tokens stayed out of the context; most used: graph_stats Ã—124509, find_symbol Ã—120099, impact_of Ã—118887 (codegraph-index usage for details)` |

### idle.jsonl, run 1

calls 446691, errors 0, writes [0, 0, 0, 0, 0, 0], p99 6.2 ms, max 547 ms, over 10 s 0, errs entries 0

| Phase | Start (UTC) | Seconds | Exit | Last line |
|---|---|---|---|---|
| full0 | 12:49:44.301 | 36.0 | 0 | `last 7 days: 82548 tool calls answered; the files they pointed at come to about 2,891,767,257 tokens, the answers to 202,663,744, so about 2,689,272,515 tokens stayed out of the context; most used: graph_stats Ã—28167, find_symbol Ã—27290, impact_of Ã—27091 (codegraph-index usage for details)` |
| incr0 | 12:50:20.282 | 18.1 | 0 | `last 7 days: 123138 tool calls answered; the files they pointed at come to about 4,305,787,013 tokens, the answers to 304,966,054, so about 4,001,773,059 tokens stayed out of the context; most used: graph_stats Ã—42119, find_symbol Ã—40704, impact_of Ã—40315 (codegraph-index usage for details)` |
| full1 | 12:50:38.397 | 35.1 | 0 | `last 7 days: 204096 tool calls answered; the files they pointed at come to about 7,138,729,409 tokens, the answers to 505,870,753, so about 6,633,976,866 tokens stayed out of the context; most used: graph_stats Ã—69804, find_symbol Ã—67441, impact_of Ã—66851 (codegraph-index usage for details)` |
| incr1 | 12:51:13.524 | 18.6 | 0 | `last 7 days: 244577 tool calls answered; the files they pointed at come to about 8,553,060,444 tokens, the answers to 608,397,875, so about 7,946,586,669 tokens stayed out of the context; most used: graph_stats Ã—83700, find_symbol Ã—80773, impact_of Ã—80104 (codegraph-index usage for details)` |
| full2 | 12:51:32.171 | 35.2 | 0 | `last 7 days: 324739 tool calls answered; the files they pointed at come to about 11,355,624,154 tokens, the answers to 807,141,562, so about 10,550,571,434 tokens stayed out of the context; most used: graph_stats Ã—111157, find_symbol Ã—107228, impact_of Ã—106354 (codegraph-index usage for details)` |
| incr2 | 12:52:07.324 | 19.1 | 0 | `last 7 days: 364989 tool calls answered; the files they pointed at come to about 12,757,999,778 tokens, the answers to 908,711,460, so about 11,852,203,084 tokens stayed out of the context; most used: graph_stats Ã—124970, find_symbol Ã—120558, impact_of Ã—119461 (codegraph-index usage for details)` |

### idle.jsonl, run 2

calls 452862, errors 0, writes [0, 0, 0, 0, 0, 0], p99 6.2 ms, max 921 ms, over 10 s 0, errs entries 0

| Phase | Start (UTC) | Seconds | Exit | Last line |
|---|---|---|---|---|
| full0 | 12:52:29.096 | 39.1 | 0 | `last 7 days: 88778 tool calls answered; the files they pointed at come to about 3,114,993,276 tokens, the answers to 218,277,178, so about 2,896,897,208 tokens stayed out of the context; most used: graph_stats Ã—30185, find_symbol Ã—29417, impact_of Ã—29176 (codegraph-index usage for details)` |
| incr0 | 12:53:08.189 | 17.8 | 0 | `last 7 days: 127878 tool calls answered; the files they pointed at come to about 4,481,240,831 tokens, the answers to 317,160,517, so about 4,165,018,522 tokens stayed out of the context; most used: graph_stats Ã—43560, find_symbol Ã—42359, impact_of Ã—41959 (codegraph-index usage for details)` |
| full1 | 12:53:26.001 | 36.0 | 0 | `last 7 days: 211241 tool calls answered; the files they pointed at come to about 7,391,651,475 tokens, the answers to 523,317,285, so about 6,869,443,524 tokens stayed out of the context; most used: graph_stats Ã—72081, find_symbol Ã—69989, impact_of Ã—69171 (codegraph-index usage for details)` |
| incr1 | 12:54:02.051 | 17.3 | 0 | `last 7 days: 249453 tool calls answered; the files they pointed at come to about 8,730,279,959 tokens, the answers to 620,348,083, so about 8,111,791,226 tokens stayed out of the context; most used: graph_stats Ã—85141, find_symbol Ã—82593, impact_of Ã—81719 (codegraph-index usage for details)` |
| full2 | 12:54:19.397 | 35.5 | 0 | `last 7 days: 329993 tool calls answered; the files they pointed at come to about 11,546,581,333 tokens, the answers to 820,003,871, so about 10,728,602,100 tokens stayed out of the context; most used: graph_stats Ã—112689, find_symbol Ã—109219, impact_of Ã—108085 (codegraph-index usage for details)` |
| incr2 | 12:54:54.858 | 19.5 | 0 | `last 7 days: 370258 tool calls answered; the files they pointed at come to about 12,955,233,332 tokens, the answers to 922,130,377, so about 12,035,951,948 tokens stayed out of the context; most used: graph_stats Ã—126447, find_symbol Ã—122539, impact_of Ã—121272 (codegraph-index usage for details)` |

### loaded.jsonl, run 0

calls 212754, errors 0, writes [0, 0, 0, 0, 0, 0], p99 229.7 ms, max 8978 ms, over 10 s 0, errs entries 0

| Phase | Start (UTC) | Seconds | Exit | Last line |
|---|---|---|---|---|
| full0 | 12:55:29.430 | 82.5 | 0 | `last 7 days: 47927 tool calls answered; the files they pointed at come to about 1,699,536,173 tokens, the answers to 119,033,928, so about 1,580,597,843 tokens stayed out of the context; most used: find_symbol Ã—16087, graph_stats Ã—15933, impact_of Ã—15907 (codegraph-index usage for details)` |
| incr0 | 12:56:51.911 | 55.1 | 0 | `last 7 days: 84228 tool calls answered; the files they pointed at come to about 2,989,474,440 tokens, the answers to 212,736,673, so about 2,777,586,558 tokens stayed out of the context; most used: find_symbol Ã—28202, graph_stats Ã—28018, impact_of Ã—28008 (codegraph-index usage for details)` |
| full1 | 12:57:47.016 | 75.9 | 0 | `last 7 days: 124263 tool calls answered; the files they pointed at come to about 4,408,400,477 tokens, the answers to 313,428,566, so about 4,095,901,006 tokens stayed out of the context; most used: find_symbol Ã—41548, graph_stats Ã—41402, impact_of Ã—41313 (codegraph-index usage for details)` |
| incr1 | 12:59:02.929 | 56.1 | 0 | `last 7 days: 151850 tool calls answered; the files they pointed at come to about 5,388,258,066 tokens, the answers to 384,602,562, so about 5,005,144,899 tokens stayed out of the context; most used: find_symbol Ã—50725, graph_stats Ã—50612, impact_of Ã—50513 (codegraph-index usage for details)` |
| full2 | 12:59:59.055 | 66.8 | 0 | `last 7 days: 181489 tool calls answered; the files they pointed at come to about 6,440,149,569 tokens, the answers to 459,265,857, so about 5,982,432,447 tokens stayed out of the context; most used: find_symbol Ã—60607, graph_stats Ã—60502, impact_of Ã—60380 (codegraph-index usage for details)` |
| incr2 | 13:01:05.882 | 39.3 | 0 | `last 7 days: 198337 tool calls answered; the files they pointed at come to about 7,038,458,396 tokens, the answers to 502,713,111, so about 6,537,641,985 tokens stayed out of the context; most used: find_symbol Ã—66232, graph_stats Ã—66114, impact_of Ã—65991 (codegraph-index usage for details)` |

### loaded.jsonl, run 1

calls 267450, errors 0, writes [0, 0, 0, 0, 0, 0], p99 123.8 ms, max 10343 ms, over 10 s 1, errs entries 0

| Phase | Start (UTC) | Seconds | Exit | Last line |
|---|---|---|---|---|
| full0 | 13:01:57.458 | 55.4 | 0 | `last 7 days: 41090 tool calls answered; the files they pointed at come to about 1,462,161,468 tokens, the answers to 102,516,880, so about 1,359,726,284 tokens stayed out of the context; most used: find_symbol Ã—13766, impact_of Ã—13708, graph_stats Ã—13616 (codegraph-index usage for details)` |
| incr0 | 13:02:52.836 | 52.7 | 0 | `last 7 days: 84620 tool calls answered; the files they pointed at come to about 3,006,999,615 tokens, the answers to 214,637,025, so about 2,793,352,115 tokens stayed out of the context; most used: find_symbol Ã—28354, impact_of Ã—28176, graph_stats Ã—28090 (codegraph-index usage for details)` |
| full1 | 13:03:45.585 | 55.1 | 0 | `last 7 days: 127910 tool calls answered; the files they pointed at come to about 4,540,587,464 tokens, the answers to 323,352,490, so about 4,218,311,097 tokens stayed out of the context; most used: find_symbol Ã—42854, impact_of Ã—42533, graph_stats Ã—42523 (codegraph-index usage for details)` |
| incr1 | 13:04:40.714 | 61.8 | 0 | `last 7 days: 175795 tool calls answered; the files they pointed at come to about 6,238,265,489 tokens, the answers to 446,551,086, so about 5,793,794,412 tokens stayed out of the context; most used: find_symbol Ã—58906, graph_stats Ã—58463, impact_of Ã—58426 (codegraph-index usage for details)` |
| full2 | 13:05:42.503 | 68.1 | 0 | `last 7 days: 218119 tool calls answered; the files they pointed at come to about 7,745,941,707 tokens, the answers to 553,602,377, so about 7,194,503,549 tokens stayed out of the context; most used: find_symbol Ã—73045, impact_of Ã—72576, graph_stats Ã—72498 (codegraph-index usage for details)` |
| incr2 | 13:06:50.605 | 40.6 | 0 | `last 7 days: 240743 tool calls answered; the files they pointed at come to about 8,550,026,608 tokens, the answers to 611,936,345, so about 7,940,714,465 tokens stayed out of the context; most used: find_symbol Ã—80630, impact_of Ã—80109, graph_stats Ã—80004 (codegraph-index usage for details)` |

### loaded.jsonl, run 2

calls 368403, errors 0, writes [0, 0, 0, 0, 0, 0], p99 124.1 ms, max 9926 ms, over 10 s 0, errs entries 0

| Phase | Start (UTC) | Seconds | Exit | Last line |
|---|---|---|---|---|
| full0 | 13:07:42.361 | 83.1 | 0 | `last 7 days: 77117 tool calls answered; the files they pointed at come to about 2,731,527,533 tokens, the answers to 191,414,948, so about 2,540,267,043 tokens stayed out of the context; most used: find_symbol Ã—25787, graph_stats Ã—25743, impact_of Ã—25587 (codegraph-index usage for details)` |
| incr0 | 13:09:05.447 | 46.1 | 0 | `last 7 days: 119915 tool calls answered; the files they pointed at come to about 4,248,181,422 tokens, the answers to 301,228,796, so about 3,947,927,819 tokens stayed out of the context; most used: find_symbol Ã—40132, graph_stats Ã—39999, impact_of Ã—39784 (codegraph-index usage for details)` |
| full1 | 13:09:51.516 | 138.3 | 0 | `last 7 days: 240627 tool calls answered; the files they pointed at come to about 8,526,564,901 tokens, the answers to 604,668,385, so about 7,923,113,359 tokens stayed out of the context; most used: find_symbol Ã—80486, graph_stats Ã—80274, impact_of Ã—79867 (codegraph-index usage for details)` |
| incr1 | 13:12:09.786 | 48.9 | 0 | `last 7 days: 276629 tool calls answered; the files they pointed at come to about 9,805,747,446 tokens, the answers to 697,497,139, so about 9,110,195,298 tokens stayed out of the context; most used: find_symbol Ã—92523, graph_stats Ã—92246, impact_of Ã—91860 (codegraph-index usage for details)` |
| full2 | 13:12:58.712 | 66.1 | 0 | `last 7 days: 306978 tool calls answered; the files they pointed at come to about 10,883,046,946 tokens, the answers to 773,976,909, so about 10,111,075,802 tokens stayed out of the context; most used: find_symbol Ã—102635, graph_stats Ã—102375, impact_of Ã—101968 (codegraph-index usage for details)` |
| incr2 | 13:14:04.773 | 57.3 | 0 | `last 7 days: 331042 tool calls answered; the files they pointed at come to about 11,736,972,462 tokens, the answers to 835,877,277, so about 10,903,586,617 tokens stayed out of the context; most used: find_symbol Ã—110716, graph_stats Ã—110366, impact_of Ã—109960 (codegraph-index usage for details)` |

### loaded.jsonl, run 3

calls 250857, errors 0, writes [0, 0, 0, 0, 0, 0], p99 222.5 ms, max 8126 ms, over 10 s 0, errs entries 0

| Phase | Start (UTC) | Seconds | Exit | Last line |
|---|---|---|---|---|
| full0 | 13:15:14.035 | 73.9 | 0 | `last 7 days: 47726 tool calls answered; the files they pointed at come to about 1,694,172,253 tokens, the answers to 118,687,776, so about 1,575,579,559 tokens stayed out of the context; most used: find_symbol Ã—16016, impact_of Ã—15863, graph_stats Ã—15847 (codegraph-index usage for details)` |
| incr0 | 13:16:27.926 | 72.1 | 0 | `last 7 days: 95626 tool calls answered; the files they pointed at come to about 3,392,251,916 tokens, the answers to 241,970,074, so about 3,151,387,220 tokens stayed out of the context; most used: find_symbol Ã—32045, graph_stats Ã—31813, impact_of Ã—31768 (codegraph-index usage for details)` |
| full1 | 13:17:40.000 | 78.2 | 0 | `last 7 days: 145396 tool calls answered; the files they pointed at come to about 5,155,458,978 tokens, the answers to 366,999,225, so about 4,789,664,785 tokens stayed out of the context; most used: find_symbol Ã—48692, graph_stats Ã—48422, impact_of Ã—48282 (codegraph-index usage for details)` |
| incr1 | 13:18:58.243 | 55.2 | 0 | `last 7 days: 173180 tool calls answered; the files they pointed at come to about 6,146,245,085 tokens, the answers to 439,005,464, so about 5,709,018,481 tokens stayed out of the context; most used: find_symbol Ã—57970, graph_stats Ã—57625, impact_of Ã—57585 (codegraph-index usage for details)` |
| full2 | 13:19:53.490 | 72.9 | 0 | `last 7 days: 210736 tool calls answered; the files they pointed at come to about 7,479,837,670 tokens, the answers to 533,583,411, so about 6,948,108,005 tokens stayed out of the context; most used: find_symbol Ã—70552, graph_stats Ã—70106, impact_of Ã—70078 (codegraph-index usage for details)` |
| incr2 | 13:21:06.367 | 49.3 | 0 | `last 7 days: 232952 tool calls answered; the files they pointed at come to about 8,267,932,844 tokens, the answers to 590,859,818, so about 7,679,402,251 tokens stayed out of the context; most used: find_symbol Ã—77964, graph_stats Ã—77520, impact_of Ã—77468 (codegraph-index usage for details)` |

### loaded.jsonl, run 4

calls 263565, errors 0, writes [0, 0, 0, 0, 0, 0], p99 128.3 ms, max 6280 ms, over 10 s 0, errs entries 0

| Phase | Start (UTC) | Seconds | Exit | Last line |
|---|---|---|---|---|
| full0 | 13:22:08.240 | 72.2 | 0 | `last 7 days: 41502 tool calls answered; the files they pointed at come to about 1,469,167,605 tokens, the answers to 102,861,864, so about 1,366,388,703 tokens stayed out of the context; most used: find_symbol Ã—13932, graph_stats Ã—13827, impact_of Ã—13743 (codegraph-index usage for details)` |
| incr0 | 13:23:20.427 | 74.0 | 0 | `last 7 days: 87146 tool calls answered; the files they pointed at come to about 3,089,067,236 tokens, the answers to 220,496,886, so about 2,869,595,212 tokens stayed out of the context; most used: find_symbol Ã—29157, graph_stats Ã—29053, impact_of Ã—28936 (codegraph-index usage for details)` |
| full1 | 13:24:34.450 | 63.0 | 0 | `last 7 days: 133234 tool calls answered; the files they pointed at come to about 4,723,910,821 tokens, the answers to 336,449,053, so about 4,388,578,748 tokens stayed out of the context; most used: find_symbol Ã—44575, graph_stats Ã—44406, impact_of Ã—44253 (codegraph-index usage for details)` |
| incr1 | 13:25:37.433 | 31.2 | 0 | `last 7 days: 154811 tool calls answered; the files they pointed at come to about 5,489,041,492 tokens, the answers to 391,812,771, so about 5,098,741,301 tokens stayed out of the context; most used: find_symbol Ã—51796, graph_stats Ã—51595, impact_of Ã—51420 (codegraph-index usage for details)` |
| full2 | 13:26:08.681 | 73.8 | 0 | `last 7 days: 204693 tool calls answered; the files they pointed at come to about 7,261,326,482 tokens, the answers to 517,499,342, so about 6,745,439,062 tokens stayed out of the context; most used: find_symbol Ã—68519, graph_stats Ã—68152, impact_of Ã—68022 (codegraph-index usage for details)` |
| incr2 | 13:27:22.456 | 55.4 | 0 | `last 7 days: 242986 tool calls answered; the files they pointed at come to about 8,619,690,955 tokens, the answers to 616,078,940, so about 8,006,027,162 tokens stayed out of the context; most used: find_symbol Ã—81361, graph_stats Ã—80886, impact_of Ã—80739 (codegraph-index usage for details)` |

### loaded.jsonl, run 5

calls 257826, errors 0, writes [0, 0, 0, 0, 0, 0], p99 124.6 ms, max 8645 ms, over 10 s 0, errs entries 0

| Phase | Start (UTC) | Seconds | Exit | Last line |
|---|---|---|---|---|
| full0 | 13:28:28.220 | 82.8 | 0 | `last 7 days: 72198 tool calls answered; the files they pointed at come to about 2,553,835,400 tokens, the answers to 178,797,640, so about 2,375,182,288 tokens stayed out of the context; most used: find_symbol Ã—24222, graph_stats Ã—24088, impact_of Ã—23888 (codegraph-index usage for details)` |
| incr0 | 13:29:51.060 | 51.3 | 0 | `last 7 days: 118148 tool calls answered; the files they pointed at come to about 4,180,241,303 tokens, the answers to 296,769,033, so about 3,884,560,805 tokens stayed out of the context; most used: find_symbol Ã—39593, graph_stats Ã—39439, impact_of Ã—39116 (codegraph-index usage for details)` |
| full1 | 13:30:42.320 | 53.1 | 0 | `last 7 days: 150059 tool calls answered; the files they pointed at come to about 5,314,656,338 tokens, the answers to 377,318,448, so about 4,938,490,181 tokens stayed out of the context; most used: find_symbol Ã—50231, graph_stats Ã—50065, impact_of Ã—49763 (codegraph-index usage for details)` |
| incr1 | 13:31:35.388 | 49.0 | 0 | `last 7 days: 182599 tool calls answered; the files they pointed at come to about 6,473,632,267 tokens, the answers to 461,543,129, so about 6,013,919,669 tokens stayed out of the context; most used: find_symbol Ã—61101, graph_stats Ã—60858, impact_of Ã—60640 (codegraph-index usage for details)` |
| full2 | 13:32:24.421 | 41.4 | 0 | `last 7 days: 204085 tool calls answered; the files they pointed at come to about 7,234,937,352 tokens, the answers to 515,501,385, so about 6,721,309,434 tokens stayed out of the context; most used: find_symbol Ã—68306, graph_stats Ã—68014, impact_of Ã—67765 (codegraph-index usage for details)` |
| incr2 | 13:33:05.836 | 52.1 | 0 | `last 7 days: 236428 tool calls answered; the files they pointed at come to about 8,385,547,659 tokens, the answers to 599,071,658, so about 7,789,025,896 tokens stayed out of the context; most used: find_symbol Ã—79135, graph_stats Ã—78741, impact_of Ã—78552 (codegraph-index usage for details)` |

## Files in `$env:TEMP\cg-win5`

| Path | Content |
|---|---|
| `windows-report-5.md` | This report |
| `idle.jsonl`, `loaded.jsonl` | The records of the 3 idle and 6 loaded runs. On the `windows-test-results` branch, they are in `windows-5/`. |
| `idle.log`, `loaded.log` | The printed lines and the exit lines |
| `loaded-counters.json` | The 12 counter samples |
| `r4loop.py` | The script |
| `bin`, `tpl`, `work` | The install, the template and the last run copy |
| `step1-install.log`, `step2-*.log` | Steps 1 and 2 |
