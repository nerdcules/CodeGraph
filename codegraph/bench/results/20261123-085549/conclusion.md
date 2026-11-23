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
