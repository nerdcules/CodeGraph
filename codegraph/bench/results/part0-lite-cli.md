# Part 0: lite-cli without the `--help` call (issue #18)

[Code mode lite](../README.md) · **Part 0**

The pilot's lite-cli snippet said "Run `codegraph --help` first", and all 6 lite-cli pilot runs did, as their first
step. The help text is 830 tokens (`overhead-20261122-205829`), and every later API call of the run re-reads it.
Part 0 replaced that line with a command reference in `profiles/lite-cli/CLAUDE.md`.

Same model (`claude-sonnet-5-5`), Jellyfin commit and index as the pilot; lite-cli only; where-defined,
impact-tests and dead-code (the pilot's three tasks) and the no-op baseline, 2 repetitions each. The pilot ran on
2026-11-22, v1 and v2 on 2026-11-23. Total tokens, median (min–max); every run finished and scored F1 1.00.

| task | pilot, `--help` (`pilot-20261122-205919`) | v1 (`part0-lite-cli-20261123-083405`) | v2 (`part0-lite-cli-v2-20261123-083615`) |
|---|---|---|---|
| baseline | 5,287 | 5,609 | 5,686 |
| where-defined | 18,333 (18,320–18,346) | 11,786 (11,780–11,793) | 11,904 (11,885–11,924) |
| impact-tests | 21,696 (21,671–21,720) | 46,716 (35,691–57,742) | 28,778 (23,335–34,221) |
| dead-code | 33,889 (29,463–38,315) | 26,662 (22,633–30,691) | 23,001 (22,525–23,477) |

Cost, median USD: where-defined 0.0139 / 0.0106 / 0.0106; impact-tests 0.0350 / 0.0589 / 0.0403; dead-code
0.0355 / 0.0330 / 0.0321.

The snippet measured 416 tokens with `--help` (`overhead-20261122-205829`), 726 for v1
(`overhead-20261123-083324`) and 798 for v2 (`overhead-20261123-083542`).

- v1 called `hits`, `dependents` and `tests` "lists of {symbol, depth}". Both impact-tests agents read them as
  arrays: their first two jq filters failed (`Cannot index array with string "symbol"`), and they spent 4 and 5
  graph steps. v2 says every list is `{items, total, truncated}` and gives the jq paths.
- v2 against the pilot: fewer tokens on where-defined and dead-code with ranges that do not overlap; more on
  impact-tests, also without overlap. The v2 impact-tests agents made 4 and 2 graph steps (an extra check that
  no dependent is a test), the pilot's 2 and 2 (`--help`, then one `impact`).

Decision: keep v2. It used fewer tokens on 2 of the 3 tasks and correctness held; impact-tests is measured
again with 5 repetitions in the full run. (Full run, `20261123-085549`: lite-cli impact-tests 15,244 median,
15,143–40,675; direct 39,509–39,825, so the ranges overlap.) Two repetitions per cell, on different days from the pilot, so this
is a direction, not a result.

---

[Code mode lite](../README.md) · **Part 0**
