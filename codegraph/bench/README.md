# Code mode lite benchmark

[CodeGraph](../README.md) · [Docs](../docs/README.md) · [MCP server](../MCP-SERVER.md) · [Benchmarks](../BENCHMARKS.md) · [With and without](../tools/eval/README.md) · **Code mode lite**

Does an agent use fewer tokens when it reaches the graph through code it writes, and prints only a summary, than
when every MCP tool result enters its context? Three modes, one setting, `CODEGRAPH_MODE`:

| mode | the agent | what it is given |
|---|---|---|
| `direct` (default) | calls the codegraph MCP tools | the server's 35 tools (deferred behind ToolSearch, as Claude Code does by default) |
| `lite` | writes a C# file-based app and runs it with `dotnet run` | `docs/codegraph-lite.md` (the API, a template, three rules); the allow rules let Bash run only `dotnet run` on scripts in its scratch folder |
| `lite-cli` | calls the Native AOT `codegraph` CLI and filters its JSON with jq | `codegraph --help`; the allow rules let Bash run codegraph, jq, grep, head, tail, wc, cut and tr |

Claude Code also runs some read-only commands without a rule (`ls` ran in the lite permissions probe); writes,
redirects outside the scratch folder, `dotnet build` and other commands were refused in that probe.

All three call the same `ICodeGraph` implementation (`CodeGraphClient`), so they answer from the same queries.
Nothing writes to the repository being analysed: the profiles deny it, the MCP server runs with telemetry off,
and every run checks the repo's git status and the graph file before and after.

## Layout

| path | what |
|---|---|
| `bench.config.json` | the pinned model, the target repo and commit, the prebuilt index, turn cap, timeout, tool paths |
| `tasks.json` | 8 tasks (2 single lookup, 4 multi-hop, 2 analytical) with their ground-truth specs |
| `profiles/<mode>/` | `CLAUDE.md` (the mode's snippet), `.claude/settings.json` (permissions), `mcp.json` (the server with `CODEGRAPH_MODE`) |
| `scripts/` | `_template.cs` and three examples: impact plus tests, callers by project, an ad hoc SQL query |
| `CodeGraph.ModeBench/` | the harness, `codegraph-modebench` |
| `results/` | `startup.md`, `overhead-*/`, `pilot-*/`, and full runs: `raw.csv`, `summary.csv`, `report.md`, `runs.jsonl`, `truth.json` |

## Build

The MCP server needs the ASP.NET Core runtime, which the distro .NET 10 SDK (10.0.113) does not ship; this machine
builds and runs it with the mise-installed SDK 10.0.403 (runtime 10.0.14). Scripts and the CLI use the distro SDK.

```sh
# from codegraph/
DOTNET_ROOT=~/.local/share/mise/installs/dotnet/10.0.403 dotnet publish src/CodeGraph.Mcp -c Release -o bench/.out/mcp
/usr/bin/dotnet publish src/CodeGraph.Cli -c Release -r linux-x64 -o bench/.out/cli       # Native AOT, needs clang and zlib headers
dotnet build bench/CodeGraph.ModeBench
alias modebench='dotnet bench/CodeGraph.ModeBench/bin/Debug/net10.0/codegraph-modebench.dll'
```

The CLI publish fails on any trim or AOT warning. win-x64 must be published on Windows (Native AOT does not
cross-compile between operating systems; it needs the Visual Studio C++ build tools):
`dotnet publish src/CodeGraph.Cli -c Release -r win-x64 -o bench/.out/cli`. Not yet built or measured.
The release archives (from 1.6.0-beta.1) carry `codegraph` ReadyToRun, not Native AOT; the start-up figures in
`results/startup.md` are for the source-built binaries, not that one.

## The command for each stage

| stage | command |
|---|---|
| 1. client library | `dotnet run --project tests/CodeGraph.Tests -- -class CodeGraph.Tests.ClientParityTests -class CodeGraph.Tests.ClientTests` |
| 2. C# scripts | `CODEGRAPH_DB=<graph.db> dotnet run bench/scripts/impact-tests.cs -- IUserManager.GetUserById 2` (copy to a scratch folder outside any repo, with an absolute `#:project` path) |
| 3. AOT CLI | `bench/.out/cli/codegraph status`; parity: `dotnet run --project tests/CodeGraph.Tests -- -class CodeGraph.Tests.CliParityTests` |
| 4. the toggle | `modebench switch lite <dir>` (one line; `direct` and `lite-cli` the same way); `modebench overhead --compile-error bench/results/compile-error-output.txt` |
| 5. harness | `modebench run --mode direct,lite,lite-cli --tasks impact-tests --reps 1` |
| 6. tasks and truth | `modebench truth` |
| 7. pilot | `modebench run --pilot` (one task per category, every mode, 2 repetitions, the no-op baseline, then the full-matrix estimate) |
| 8. full run | `modebench run` (every task, every mode, `repetitions` from the config, interleaved) |

`modebench report <results dir>` rewrites `summary.csv` and `report.md` from `runs.jsonl`.

For an interactive session, `CODEGRAPH_MODE=lite` in the MCP server's environment turns its tools off; a copy
made with `switch` also has the snippet, the permissions and (lite) the doc. Claude Code applies a project's
`permissions.allow` only once the folder is trusted: open it interactively once, or pass
`--settings <dir>/.claude/settings.json`, as the harness does.

## How a run is made

Each task × mode × repetition is one `claude -p` session in a fresh copy of the mode's profile:
`--model` from the config, `--setting-sources project` (no user settings, plugins, hooks or user CLAUDE.md),
`--settings` with the profile's permissions, `--strict-mcp-config --mcp-config <copy>/.mcp.json`,
`--tools` per mode, `--permission-mode dontAsk`, `--no-session-persistence`, `--max-turns`, a timeout, and auto
memory off (`CLAUDE_CODE_DISABLE_AUTO_MEMORY=1` and `autoMemoryEnabled: false`, both measured to remove the memory
section). Repetitions run interleaved: every mode runs a task before the next repetition. A failure (timeout, turn
cap, error, repo changed) is recorded and not retried.

From the stream-json output it records input, output, cache-read and cache-creation tokens, cost, turns, tool calls
by type, wall time and the answer; a field the CLI did not print is logged as null and named in the report. In the
lite modes it counts scripts run, compile failures (`error CS` or "The build failed" in the result) and CLI
invocations, and sums the time from each such tool call to its result.

## Scoring

Every task asks for an `ANSWER` block, one item per line. The truth is computed from the index before any agent
runs (`truth.json`): a reference SQL query or a check over `ICodeGraph`. The score is the F1 of precision and recall
on the listed items: names compare without parameters or generic arguments, and a qualified tail (`Type.Method`)
matches the full name; files compare relative to the repo; projects by name (the callers-by-project task also
reports how many counts are right). A run counts toward the token comparison only if it finished and scored at
least the median score of the direct runs of the same task; a finished run below that is listed as "cheaper but
worse". A mode wins or loses a category only when the min–max ranges of total tokens do not overlap.

The dead-code task is defined by the graph's edges, and the prompt asks for the graph's answer. In Jellyfin all 12
of its truth items are used in source, checked by grep: 8 are event handlers subscribed with `+=`, and 4 are passed
as method groups (`cancellationToken.Register(Stop)`, `Services.Select(GetServiceInfo)`, `.Where(IsLiveTvEnabled)`,
`new Timer(TimerCallback, …)`). The indexer records no edge for either, so the graph calls them uncalled.

---

[CodeGraph](../README.md) · [Docs](../docs/README.md) · [MCP server](../MCP-SERVER.md) · [Benchmarks](../BENCHMARKS.md) · [With and without](../tools/eval/README.md) · **Code mode lite**
