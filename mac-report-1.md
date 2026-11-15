# CodeGraph macOS test 1 — branch any-mcp-client at a3150ec

Date: 2026-11-15. Everything ran under /tmp/cg-mac1. No product code was changed.

## Incident: the Mac rebooted mid-run

The machine rebooted at 15:58 (`last reboot`: "reboot time Sun Nov 15 15:58"), right after step 7 (uninstall)
finished and as step 8 (resilience) was starting. macOS clears /tmp on boot, so every file under /tmp/cg-mac1
from steps 1–7 (logs, the first daemon.log, the installs, the clones) was lost. No kernel panic report was found in
/Library/Logs/DiagnosticReports or ~/Library/Logs/DiagnosticReports; the cause of the reboot is unknown. Load
averages at 16:01, three minutes after boot: 205.17 156.99 69.57.

Consequences for this report:

- The results for steps 1–7 below are copied from the session transcript (the commands' outputs as they printed).
  Their log files no longer exist.
- Steps 8 and 9 ran after the reboot on a rebuilt /tmp/cg-mac1: a fresh clone (still a3150ec), a fresh from-source
  install into /tmp/cg-mac1/bin, a fresh /tmp/cg-mac1/repo and /tmp/cg-mac1/tpl. The rebuild is described under
  "Rebuild after the reboot".
- Step 7's uninstall had already deleted /tmp/cg-mac1/bin-release before the reboot; it was not reinstalled.

## 0. Machine

```
sw_vers:        ProductName: macOS   ProductVersion: 15.7.9   BuildVersion: 24G830
uname -m:       x86_64
hw.logicalcpu:  8
hw.memsize:     17179869184
dotnet --list-sdks:  10.0.302 [/usr/local/share/dotnet/sdk]
node --version:      not on PATH in a non-login shell ("command not found: node", exit 127).
                     nvm has v24.19.0 at ~/.nvm/versions/node/v24.19.0; every step below ran with that folder
                     prepended to PATH (nothing was installed). npm 11.17.0.
python3 --version:   Python 3.9.6 (Xcode's)
git --version:       git version 2.55.0
```

Note: on this Mac /tmp is a symlink (`/tmp -> private/tmp`). Several results below depend on whether a path is
spelled /tmp/... or /private/tmp/....

## 1. Clone, tests, build

```
git clone https://github.com/nerdcules/CodeGraph.git /tmp/cg-mac1/src   exit 0
git checkout any-mcp-client                                               exit 0
git rev-parse --short HEAD                                                a3150ec
(cd extractors/typescript && npm ci)                                      exit 0  (added 4 packages, 0 vulnerabilities)
dotnet test tests/CodeGraph.Tests                                         exit 0
```

Test run summary (Microsoft.Testing.Platform, xunit.v3):

```
total: 441   failed: 0   succeeded: 439   skipped: 2   duration: 1m 26s 652ms
```

Skipped tests (both say "drive letters exist only on Windows"):

- CodeGraph.Tests.LspPathTests.A_lower_case_drive_letter_comes_back_upper_case_on_windows
- CodeGraph.Tests.LspPathTests.A_servers_windows_uri_is_keyed_as_ours

Failed tests: none.

A first attempt with VSTest-style `--logger` arguments appended by me ended with "Zero tests ran", exit 5; that was
my argument mistake (the project uses Microsoft.Testing.Platform), not a product failure. The plain command above
is the one that counts.

```
dotnet build CodeGraph.slnx -c Release -warnaserror   (cwd /tmp/cg-mac1/src/codegraph, relative path)
exit 0 — Build succeeded. 0 Warning(s) 0 Error(s). Time Elapsed 00:00:14.03
```

## 2. Install

### 2a. From source: `./install.sh --from-source --skip-tests --install-dir /tmp/cg-mac1/bin`

Run from `cd /tmp/cg-mac1/src/codegraph` (the script's own root resolves to /tmp/cg-mac1/src/codegraph):

- Attempt 1: exit 1. No "note:" lines. The build step failed:

```
==> Building (warnings are errors)
  ...
  CodeGraph.Core -> /tmp/cg-mac1/src/codegraph/src/CodeGraph.Core/bin/Release/net10.0/CodeGraph.Core.dll
  CodeGraph.Core -> /tmp/cg-mac1/src/codegraph/src/CodeGraph.Core/bin/Debug/net10.0/CodeGraph.Core.dll
CSC : error CS0006: Metadata file '/tmp/cg-mac1/src/codegraph/src/CodeGraph.Core/obj/Debug/net10.0/ref/CodeGraph.Core.dll' could not be found [/tmp/cg-mac1/src/codegraph/src/CodeGraph.Indexer/CodeGraph.Indexer.csproj]
CSC : error CS0006: Metadata file '/tmp/cg-mac1/src/codegraph/src/CodeGraph.Core/obj/Debug/net10.0/ref/CodeGraph.Core.dll' could not be found [/tmp/cg-mac1/src/codegraph/tools/CodeGraph.Bench/CodeGraph.Bench.csproj]
CSC : error CS0006: Metadata file '/tmp/cg-mac1/src/codegraph/src/CodeGraph.Core/obj/Debug/net10.0/ref/CodeGraph.Core.dll' could not be found [/tmp/cg-mac1/src/codegraph/src/CodeGraph.Mcp/CodeGraph.Mcp.csproj]
CSC : error CS0006: Metadata file '/tmp/cg-mac1/src/codegraph/src/CodeGraph.Core/obj/Debug/net10.0/ref/CodeGraph.Core.dll' could not be found [/tmp/cg-mac1/src/codegraph/src/CodeGraph.Web/CodeGraph.Web.csproj]
CSC : error CS0006: Metadata file '/tmp/cg-mac1/src/codegraph/src/CodeGraph.Core/obj/Debug/net10.0/ref/CodeGraph.Core.dll' could not be found [/tmp/cg-mac1/src/codegraph/src/CodeGraph.Indexer/CodeGraph.Indexer.csproj]
Build FAILED.  0 Warning(s)  5 Error(s)
```

- Attempt 2 (same command, same cwd): exit 1. The build step passed this time (0 Error(s); it still built
  CodeGraph.Core and CodeGraph.Indexer in Debug as well as Release), then the publish step failed:

```
==> Publishing to /tmp/cg-mac1/bin (osx-x64, ReadyToRun)
  Restored /private/tmp/cg-mac1/src/codegraph/src/CodeGraph.Core/CodeGraph.Core.csproj (in 10 sec).
  Restored /tmp/cg-mac1/src/codegraph/src/CodeGraph.Indexer/CodeGraph.Indexer.csproj (in 10.04 sec).
  CodeGraph.Core -> /tmp/cg-mac1/src/codegraph/src/CodeGraph.Core/bin/Release/net10.0/CodeGraph.Core.dll
/private/tmp/cg-mac1/src/codegraph/src/CodeGraph.Indexer/Lsp/LspLiveService.cs(59,80): error CS0103: The name 'DuckDB' does not exist in the current context [/tmp/cg-mac1/src/codegraph/src/CodeGraph.Indexer/CodeGraph.Indexer.csproj]
/private/tmp/cg-mac1/src/codegraph/src/CodeGraph.Indexer/Lsp/LspLiveService.cs(128,84): error CS0103: The name 'DuckDB' does not exist in the current context [/tmp/cg-mac1/src/codegraph/src/CodeGraph.Indexer/CodeGraph.Indexer.csproj]
```

- Attempt 3, run from `cd /private/tmp/cg-mac1/src/codegraph` (same checkout, real path): exit 0. No "note:" lines.
  Output ended with:

```
==> Checking the executables start
ok: codegraph 1.1.0

==> Installed
  /tmp/cg-mac1/bin/codegraph-index
  /tmp/cg-mac1/bin/codegraph-mcp
```

/tmp/cg-mac1/bin held 58 entries including extractors/typescript (cli.mjs, lib, node_modules, package.json,
package-lock.json). After the reboot the same attempt-3 command was run again to rebuild /tmp/cg-mac1/bin: exit 0,
"ok: codegraph 1.1.0", 58 entries, `codegraph-index --version` prints 1.1.0.

What the failures correlate with (facts observed, no fix applied). With the solution addressed through the
/tmp symlink, the SDK treats some projects as two projects, one per path spelling:

```
dotnet build /private/tmp/cg-mac1/src/codegraph/CodeGraph.slnx -c Release -warnaserror --nologo   exit 0, 0 "bin/Debug" lines
dotnet build /tmp/cg-mac1/src/codegraph/CodeGraph.slnx -c Release -warnaserror --nologo           exit 0, 2 "bin/Debug" lines
dotnet build /tmp/cg-mac1/src/codegraph/CodeGraph.slnx -c Release -warnaserror                    exit 0, 2 "bin/Debug" lines
  (the two lines: CodeGraph.Core -> .../bin/Debug/net10.0/CodeGraph.Core.dll and
                  CodeGraph.Indexer -> .../bin/Debug/net10.0/codegraph-index.dll)
dotnet build /tmp/cg-mac1/src/codegraph/CodeGraph.slnx -c Release -warnaserror --nologo (another run) exit 1:
  CSC : error CS0006: Metadata file '/tmp/cg-mac1/src/codegraph/src/CodeGraph.Indexer/obj/Debug/net10.0/ref/codegraph-index.dll' could not be found [/tmp/cg-mac1/src/codegraph/tests/CodeGraph.Tests/CodeGraph.Tests.csproj]
dotnet restore (either spelling) lists 8 "Restored" lines for 6 projects: CodeGraph.Core and CodeGraph.Indexer
  appear once as /tmp/... and once as /private/tmp/....
```

So the from-source install fails intermittently when the checkout is addressed as /tmp/..., and passes when
addressed as /private/tmp/.... The step-1 build (relative path, cwd /tmp/cg-mac1/src/codegraph) printed only
/private/tmp/... paths and passed.

### 2b. Published release: `./install.sh --install-dir /tmp/cg-mac1/bin-release`

exit 0. Full output (escape codes removed):

```
==> Downloading codegraph-osx-x64.tar.gz (latest)
  checksum ok

==> Installing to /tmp/cg-mac1/bin-release
  codegraph 1.1.0

==> Checking the executables start
ok: codegraph 1.1.0

==> Installed
  /tmp/cg-mac1/bin-release/codegraph-index
  /tmp/cg-mac1/bin-release/codegraph-mcp

Set up a repo (writes .mcp.json and .gitignore at its root, then builds the graph):
  /tmp/cg-mac1/bin-release/codegraph-index init /path/to/repo-or-solution [--live] [--client copilot,cursor,gemini,codex,roo,kilo,continue]
Keep a graph fresh while you work:
  /tmp/cg-mac1/bin-release/codegraph-index /path/to/App.sln --watch
```

Archive downloaded: codegraph-osx-x64.tar.gz (latest = v1.1.0; VERSION file reads 1.1.0).
`/tmp/cg-mac1/bin-release/codegraph-index --version` printed `1.1.0`, exit 0. No Gatekeeper or "cannot be
opened" message appeared; `xattr -l` on codegraph-index printed nothing. Node was on PATH (nvm) during this run,
so the script's "note: node is not on PATH" line did not apply.

### 2c. Unknown option: `./install.sh --bogus --install-dir /tmp/cg-mac1/never`

```
unknown option: --bogus
```

exit 1. `/tmp/cg-mac1/never` was not created ("ls: /tmp/cg-mac1/never: No such file or directory"). As expected.

## 3. Restored clone and full index

```
git clone /tmp/cg-mac1/src /tmp/cg-mac1/repo                       exit 0
git -C /tmp/cg-mac1/repo checkout any-mcp-client                     exit 0 (a3150ec)
dotnet restore /tmp/cg-mac1/repo/codegraph/CodeGraph.slnx            exit 0 (8 "Restored" lines for 6 projects, see 2a)
time /tmp/cg-mac1/bin/codegraph-index /tmp/cg-mac1/repo/codegraph/CodeGraph.slnx --full   exit 0
```

```
compiler: Roslyn 5.9.0 (up to C# 14); projects use C# 14; 0 compile error(s) in declarations
indexed 3245 nodes, 19848 edges (full) in 16.2s → /tmp/cg-mac1/repo/.codegraph/graph.db
real 21.11   user 22.62   sys 1.96
```

Nodes 3245, edges 19848; indexer's own time 16.2 s; wall clock 21.11 s. Then `cp -R /tmp/cg-mac1/repo
/tmp/cg-mac1/tpl` (done).

## 4. C# live daemon (5 saves)

Daemon: `/tmp/cg-mac1/bin/codegraph-index /tmp/cg-mac1/repo/codegraph/CodeGraph.slnx --watch --log /tmp/cg-mac1/daemon.log`,
started in the background by a helper script that also held one `codegraph-mcp --root /tmp/cg-mac1/repo` client
open and polled `find_symbol(query=<name>, nameMatch=Exact, limit=5)` every 0.2 s after each save until the answer
contained the id `[M:CodeGraph.Core.Worktree.<name>]`. Each save appended one more `public static void LiveProbeN() { }`
to the Worktree class (cumulative), and the file was restored byte-for-byte at the end (verified identical).

live_status 4.3 s after the daemon started (before the saves):

```
live daemon : running (pid 29605) since 2026-11-15 15:56:17Z
solution    : /tmp/cg-mac1/repo/codegraph/CodeGraph.slnx (6 project(s), 112 document(s))
graph       : /tmp/cg-mac1/repo/.codegraph/graph.db
last index  : 2026-11-15 15:56:21Z (0.25s)
state       : idle, graph matches the working tree
memory      : 162 MB working set, 11 MB managed heap
lifetime    : runs until stopped
```

| save | method | save → find_symbol answers | polls | daemon's own re-index time (daemon.log) |
|---|---|---|---|---|
| 1 | LiveProbe1 | 5.49 s | 2 | 5.2 s (10 of 111 files walked) |
| 2 | LiveProbe2 | 2.80 s | 2 | 2.4 s |
| 3 | LiveProbe3 | 2.90 s | 2 | 2.6 s |
| 4 | LiveProbe4 | 2.81 s | 2 | 2.6 s |
| 5 | LiveProbe5 | 2.33 s | 2 | 2.5 s |

In every case the first poll (sent right after the save) returned "no symbols match ..." only after the re-index
had finished, and the second poll had the method; no answer was error-flagged and none carried the
"⚠ STALE INDEX" banner. Restoring the file triggered a sixth incremental index (1.7 s) back to 3245 nodes,
19848 edges.

live_status at the end:

```
live daemon : running (pid 29605) since 2026-11-15 15:56:17Z
solution    : /tmp/cg-mac1/repo/codegraph/CodeGraph.slnx (6 project(s), 112 document(s))
graph       : /tmp/cg-mac1/repo/.codegraph/graph.db
last index  : 2026-11-15 15:56:45Z (1.51s)
state       : idle, graph matches the working tree
memory      : 356 MB working set, 134 MB managed heap
lifetime    : runs until stopped
```

Memory line before: `memory      : 162 MB working set, 11 MB managed heap`.
Memory line after:  `memory      : 356 MB working set, 134 MB managed heap`.

SIGTERM: the daemon exited with code 0; the log's last line is `stopped`. daemon.log: 3366 bytes, 66 LF, 0 CRLF,
**bare CR (a CR not followed by LF): 0**. No codegraph-index or codegraph-mcp process was left.

An earlier run of the same helper (daemon pid 29478) was discarded: its "found" check was `name in answer`, which
"no symbols match 'LiveProbe1'" satisfies, so it reported 0.00–7 s timings that meant nothing. Its daemon.log also
had 0 bare CRs and the daemon exited 0 on SIGTERM. The table above is from the corrected run.

## 5. Uninstall

`/tmp/cg-mac1/bin/codegraph-index init /tmp/cg-mac1/repo --client claude,copilot,cursor,roo` — exit 0. It wrote
six config files (its own lines):

```
  .mcp.json      created with the codegraph server for Claude Code, Copilot CLI (/tmp/cg-mac1/repo/.mcp.json)
  .vscode/mcp.json created with the codegraph server for VS Code (/tmp/cg-mac1/repo/.vscode/mcp.json)
  .vs/mcp.json   created with the codegraph server for Visual Studio (/tmp/cg-mac1/repo/codegraph/.vs/mcp.json)
  .github/mcp.json created with the codegraph server for Copilot for JetBrains (/tmp/cg-mac1/repo/.github/mcp.json)
  .cursor/mcp.json created with the codegraph server for Cursor (/tmp/cg-mac1/repo/.cursor/mcp.json)
  .roo/mcp.json  created with the codegraph server for Roo Code (/tmp/cg-mac1/repo/.roo/mcp.json)
  .gitignore     already ignores .codegraph/
```

The codegraph entry it wrote was `"command": "/private/tmp/cg-mac1/bin/codegraph-mcp"` (real path). To each of the
six files I added a server `"other": {"type": "stdio", "command": "other-server"}` under the servers key and a
top-level `"note": "kept by the user"`.

`./uninstall.sh --install-dir /tmp/cg-mac1/bin-release --repo /tmp/cg-mac1/repo --yes` — exit 0. Full output
(escape codes removed):

```

==> Install folder: /tmp/cg-mac1/bin-release
deleted /tmp/cg-mac1/bin-release

==> Repo: /tmp/cg-mac1/repo
removed the codegraph entry from /tmp/cg-mac1/repo/.mcp.json
removed the codegraph entry from /tmp/cg-mac1/repo/.cursor/mcp.json
removed the codegraph entry from /tmp/cg-mac1/repo/.vscode/mcp.json
removed the codegraph entry from /tmp/cg-mac1/repo/.github/mcp.json
removed the codegraph entry from /tmp/cg-mac1/repo/.roo/mcp.json
removed the codegraph entry from /tmp/cg-mac1/repo/codegraph/.vs/mcp.json
deleted /tmp/cg-mac1/repo/.codegraph

Done.
```

One line per file and folder, each naming its path: as expected. /tmp/cg-mac1/bin-release and
/tmp/cg-mac1/repo/.codegraph were gone afterwards; /tmp/cg-mac1/bin was untouched. All six configs kept "other"
and "note". /tmp/cg-mac1/repo/.mcp.json after the uninstall:

```json
{
  "mcpServers": {
    "other": {
      "type": "stdio",
      "command": "other-server"
    }
  },
  "note": "kept by the user"
}
```

(.vscode/mcp.json, codegraph/.vs/mcp.json and .github/mcp.json were the same shape under "servers"; .cursor and
.roo the same under "mcpServers".) The codex CLI is not installed on this Mac, so the script's Codex section did
not run; ~/.zshrc and ~/.bashrc contained no codegraph PATH line, so they were not modified.

## Rebuild after the reboot (for steps 8 and 9)

```
mkdir -p /tmp/cg-mac1; git clone https://github.com/nerdcules/CodeGraph.git /tmp/cg-mac1/src; checkout any-mcp-client   HEAD a3150ec
cd /private/tmp/cg-mac1/src/codegraph && ./install.sh --from-source --skip-tests --install-dir /tmp/cg-mac1/bin          exit 0, ok: codegraph 1.1.0
git clone /tmp/cg-mac1/src /tmp/cg-mac1/repo; checkout any-mcp-client                                                     a3150ec
dotnet restore /tmp/cg-mac1/repo/codegraph/CodeGraph.slnx                                                                 exit 1 (below); retry exit 0
/tmp/cg-mac1/bin/codegraph-index /tmp/cg-mac1/repo/codegraph/CodeGraph.slnx --full   indexed 3245 nodes, 19848 edges (full) in 14.4s; real 18.76 user 20.73 sys 1.61
cp -R /tmp/cg-mac1/repo /tmp/cg-mac1/tpl
```

The first restore after the reboot failed with the same two-paths effect as 2a:

```
  Restored /private/tmp/cg-mac1/repo/codegraph/src/CodeGraph.Core/CodeGraph.Core.csproj (in 570 ms).
  ...
  Restored /tmp/cg-mac1/repo/codegraph/src/CodeGraph.Indexer/CodeGraph.Indexer.csproj (in 613 ms).
  Restored /private/tmp/cg-mac1/repo/codegraph/src/CodeGraph.Indexer/CodeGraph.Indexer.csproj (in 613 ms).
/usr/local/share/dotnet/sdk/10.0.302/NuGet.targets(198,5): error : The file '/tmp/cg-mac1/repo/codegraph/src/CodeGraph.Core/obj/CodeGraph.Core.csproj.nuget.g.props' already exists. [/tmp/cg-mac1/repo/codegraph/CodeGraph.slnx]
```

The immediate retry of the same command exited 0. Load average at the time of the index: 18.18 (boot had just
finished).

## 6. Resilience (after the reboot)

`python3 tools/CodeGraph.Bench/resilience.py /tmp/cg-mac1/res /tmp/cg-mac1/tpl /tmp/cg-mac1/bin/codegraph-mcp /tmp/cg-mac1/bin/codegraph-index`
(cwd /tmp/cg-mac1/src/codegraph) — exit 0, **12 of 12 passed**. Ran 16:05–16:12 while the post-boot load
average was still 40–50 (fileproviderd and WindowServer were busy).

| scenario | result |
|---|---|
| R1 no graph, then built while the server runs | PASS |
| R2a corrupt graph: server answers, stays up | PASS |
| R2b corrupt graph: indexer recovers | PASS |
| R3 kill -9 mid-index: graph stays readable and whole | PASS (8 kills, 3245 nodes every time; next index exit 0) |
| R4 8 readers during 3 full + 3 incremental writes | PASS |
| R5 malformed requests, then a normal call | PASS |
| R5b every error says what was wrong | PASS |
| R6 live daemon killed under the server | PASS |
| R7 stdin closed: the server exits | PASS (exit 0 after 0.0 s) |
| R8 --root edge cases: no crash, a clear answer | PASS |
| R9 edited file without a daemon: answer flagged stale | PASS |
| R10 init with broken config files | PASS |

R4 in full: 1,094,370 calls, 0 errors (none to paste), writer exits [0, 0, 0, 0, 0, 0], latency p50 1.5 ms,
**p99 4.3 ms, max 582 ms**, node counts seen 3245 and 3246, Probe2 visible afterwards.

Every scenario's printed line:

```
PASS  R1 no graph, then built while the server runs: before: 'no code graph at /tmp/cg-mac1/res/r1/.codegraph/graph.db. Build it fro' (error=True); after build: '3245 nodes, 19848 edges'
PASS  R2a corrupt graph: server answers, stays up: alive=True; answers: ['3245 nodes, 19848 edges', 'find_symbol failed: the graph file /tmp/cg-mac1/res/r2/.code', '3245 nodes, 19848 edges']
PASS  R2b corrupt graph: indexer recovers: incremental exit 0 (last 7 days: 3 tool calls answered (1 failed); the files they pointed at come to about 0 tokens, the answers t); then graph_stats: '3245 nodes, 19848 edges'
PASS  R3 kill -9 mid-index: graph stays readable and whole: full at 2s killed: 3245 nodes; full at 9s killed: 3245 nodes; full at 13s killed: 3245 nodes; full at 14.5s killed: 3245 nodes; full at 15.5s killed: 3245 nodes; incremental at 1.5s killed: 3245 nodes; incremental at 2.3s killed: 3245 nodes; incremental at 2.6s killed: 3245 nodes; next index exit 0, 3245 nodes
PASS  R4 8 readers during 3 full + 3 incremental writes: 1094370 calls, 0 errors []; writer exits [0, 0, 0, 0, 0, 0]; latency p50 1.5 ms, p99 4.3 ms, max 582 ms; node counts seen ['3245', '3246']; new method visible after: True
PASS  R5 malformed requests, then a normal call: not JSON -> answer 79 ms; unknown tool -> error 12 ms; missing required argument -> error 13 ms; wrong type -> error 10 ms; negative depth -> answer 6 ms; huge depth -> answer 5 ms; 1 MB query -> answer 51 ms; unicode and control characters -> answer 3 ms; unknown id -> answer 1 ms; SQL-looking query -> answer 69 ms; id with brackets -> answer 4 ms; id as symbolId -> answer 1 ms
PASS  R5b every error says what was wrong: no opaque 'An error occurred' answers
      graph still intact after the SQL-looking query: 3246 nodes
PASS  R6 live daemon killed under the server: daemon pid 2904; after kill: 'no live daemon for /tmp/cg-mac1/res/r4/.codegraph/graph.db. Start one with: codegraph-index "/tmp/cg-mac1/res/r4/codegraph/CodeGraph.slnx" --watch (or call live_status with start=true)'; graph tools: 3246 nodes; live tool: 'this needs the live daemon, which is not running for /tmp/cg-mac1/res/r4/.codegraph/graph.'; live_status start=true: 'live daemon : running (pid 2920) since 2026-11-15 16:11:51Z'
PASS  R7 stdin closed: the server exits: exit 0 after 0.0 s
PASS  R8 --root edge cases: no crash, a clear answer: missing folder: 'no code graph at /private/tmp/cg-mac1/src/.codegraph/graph.db. Build i'; a file: 'no code graph at /private/tmp/cg-mac1/src/.codegraph/graph.db. Build i'; unexpanded placeholder: 'no code graph at /private/tmp/cg-mac1/src/.codegraph/graph.db. Build i'; empty: 'no code graph at /private/tmp/cg-mac1/src/.codegraph/graph.db. Build i'
PASS  R9 edited file without a daemon: answer flagged stale: ⚠ STALE INDEX: 1 indexed file(s) edited since (Worktree.cs). Rows marked STALE come from changed files, and new files ar
PASS  R10 init with broken config files: non-object .mcp.json: exit 1, file untouched True ('  cannot update /tmp/cg-mac1/res/r10/.mcp.json: the file is not a JSON object. Fix or move it, then re-run.'); JSONC with comments: exit 0, servers ['codegraph', 'other']; read-only .cursor/mcp.json: exit 1 ("  cannot update /tmp/cg-mac1/res/r10/.cursor/mcp.json: Access to the path '/tmp/cg-mac1/res/r10/.cursor/mcp.js")

12 of 12 passed
```

Observation on R8: for every bad `--root` the server fell back to the client's working directory's worktree
(/private/tmp/cg-mac1/src, the clone, which has no graph) and answered "no code graph at ...". No codegraph process
was left running afterwards.

## 7. R4 on its own: idle and under CPU load (after the reboot)

Script: /tmp/cg-mac1/r4loop.py (next to this report). The pasted text had lost its indentation, the `*args` / `**args`
splat operators (markdown italics ate the asterisks; resilience.py's own identical lines have them) and the `\n`
escapes inside two string literals; those were restored, nothing else was changed. Both runs were launched from
/tmp/cg-mac1/src/codegraph through a wrapper (/tmp/cg-mac1/helpers/run_r4.sh) that, for the idle runs only, first
waited for the 1-minute load average to drop under 4, and that ran `uptime` 150 s into each batch.

### Idle (3 runs, 0 burners)

```
python3 /tmp/cg-mac1/r4loop.py tools/CodeGraph.Bench /tmp/cg-mac1/work /tmp/cg-mac1/tpl /tmp/cg-mac1/bin/codegraph-mcp /tmp/cg-mac1/bin/codegraph-index 3 0 /tmp/cg-mac1/idle.jsonl
```

Wrapper log (load wait, uptime before and during, exit, leftover-process check):

```
16:12:39 load1 26.54
16:13:10 load1 17.06
16:13:40 load1 10.75
16:14:10 load1 7.15
16:14:40 load1 4.98
16:15:11 load1 3.51
16:15:11 start: uptime: 16:15  up 17 mins, 2 users, load averages: 3.51 29.85 40.79
16:17:41 uptime during the runs: 16:17  up 19 mins, 2 users, load averages: 58.34 44.04 44.92
16:26:32 r4loop exit 0
leftover processes:
none
```

Each run's printed line:

```
run 0: burners 0 load 56.46435546875 calls 1089801 errors 0 writes [0, 0, 0, 0, 0, 0] p99 4.2 ms max 458 ms calls over 10 s 0
run 1: burners 0 load 62.7802734375 calls 1064526 errors 0 writes [0, 0, 0, 0, 0, 0] p99 4.4 ms max 475 ms calls over 10 s 0
run 2: burners 0 load 63.43505859375 calls 1043673 errors 0 writes [0, 0, 0, 0, 0, 0] p99 4.3 ms max 418 ms calls over 10 s 0
```

| run | calls | errors | writes (exit codes) | p99 ms | max ms | calls over 10 s | load avg at end |
|---|---|---|---|---|---|---|---|
| 0 | 1,089,801 | 0 | [0, 0, 0, 0, 0, 0] | 4.2 | 458 | 0 | 56.5 |
| 1 | 1,064,526 | 0 | [0, 0, 0, 0, 0, 0] | 4.4 | 475 | 0 | 62.8 |
| 2 | 1,043,673 | 0 | [0, 0, 0, 0, 0, 0] | 4.3 | 418 | 0 | 63.4 |

Phases (indexer passes while the 8 readers ran; `last` is the indexer's last output line, which is the usage
summary codegraph-index prints after indexing):

| run | phase | seconds | exit |
|---|---|---|---|
| 0 | full0 | 48.1 | 0 |
| 0 | incr0 | 24.5 | 0 |
| 0 | full1 | 46.8 | 0 |
| 0 | incr1 | 28.1 | 0 |
| 0 | full2 | 49.9 | 0 |
| 0 | incr2 | 32.4 | 0 |
| 1 | full0 | 48.3 | 0 |
| 1 | incr0 | 24.0 | 0 |
| 1 | full1 | 45.0 | 0 |
| 1 | incr1 | 27.6 | 0 |
| 1 | full2 | 49.0 | 0 |
| 1 | incr2 | 29.0 | 0 |
| 2 | full0 | 47.5 | 0 |
| 2 | incr0 | 23.1 | 0 |
| 2 | full1 | 43.6 | 0 |
| 2 | incr1 | 27.1 | 0 |
| 2 | full2 | 48.0 | 0 |
| 2 | incr2 | 30.3 | 0 |

Run 0 phases list, verbatim from the jsonl:

```json
{"phase": "full0", "start": 1794759313.905, "seconds": 48.1, "exit": 0, "last": ["last 7 days: 189134 tool calls answered; the files they pointed at come to about 6,565,718,147 tokens, the answers to 429,479,586, so about 6,136,618,163 tokens stayed out of the context; most used: graph_stats \u00d763267, find_symbol \u00d762945, impact_of \u00d762922 (codegraph-index usage for details)"]}
{"phase": "incr0", "start": 1794759361.996, "seconds": 24.5, "exit": 0, "last": ["last 7 days: 292818 tool calls answered; the files they pointed at come to about 10,165,093,487 tokens, the answers to 671,954,501, so about 9,495,260,096 tokens stayed out of the context; most used: graph_stats \u00d797973, find_symbol \u00d797424, impact_of \u00d797421 (codegraph-index usage for details)"]}
{"phase": "full1", "start": 1794759386.46, "seconds": 46.8, "exit": 0, "last": ["last 7 days: 504931 tool calls answered; the files they pointed at come to about 17,527,847,121 tokens, the answers to 1,158,167,213, so about 16,372,227,132 tokens stayed out of the context; most used: graph_stats \u00d7168992, impact_of \u00d7167993, find_symbol \u00d7167946 (codegraph-index usage for details)"]}
{"phase": "incr1", "start": 1794759433.227, "seconds": 28.1, "exit": 0, "last": ["last 7 days: 628352 tool calls answered; the files they pointed at come to about 21,818,565,743 tokens, the answers to 1,445,947,532, so about 20,376,765,497 tokens stayed out of the context; most used: graph_stats \u00d7210301, impact_of \u00d7209169, find_symbol \u00d7208882 (codegraph-index usage for details)"]}
{"phase": "full2", "start": 1794759461.281, "seconds": 49.9, "exit": 0, "last": ["last 7 days: 859312 tool calls answered; the files they pointed at come to about 29,825,008,368 tokens, the answers to 1,974,568,293, so about 27,855,052,223 tokens stayed out of the context; most used: graph_stats \u00d7287778, impact_of \u00d7285887, find_symbol \u00d7285647 (codegraph-index usage for details)"]}
{"phase": "incr2", "start": 1794759511.224, "seconds": 32.4, "exit": 0, "last": ["last 7 days: 1004299 tool calls answered; the files they pointed at come to about 34,857,934,738 tokens, the answers to 2,311,589,826, so about 32,552,712,158 tokens stayed out of the context; most used: graph_stats \u00d7336371, impact_of \u00d7334151, find_symbol \u00d7333777 (codegraph-index usage for details)"]}
```

Run 1 phases list, verbatim from the jsonl:

```json
{"phase": "full0", "start": 1794759546.561, "seconds": 48.3, "exit": 0, "last": ["last 7 days: 189531 tool calls answered; the files they pointed at come to about 6,587,985,950 tokens, the answers to 431,004,780, so about 6,157,360,832 tokens stayed out of the context; most used: graph_stats \u00d763277, impact_of \u00d763153, find_symbol \u00d763101 (codegraph-index usage for details)"]}
{"phase": "incr0", "start": 1794759594.893, "seconds": 24.0, "exit": 0, "last": ["last 7 days: 294002 tool calls answered; the files they pointed at come to about 10,217,005,478 tokens, the answers to 675,442,510, so about 9,543,673,768 tokens stayed out of the context; most used: graph_stats \u00d798222, impact_of \u00d797945, find_symbol \u00d797835 (codegraph-index usage for details)"]}
{"phase": "full1", "start": 1794759618.936, "seconds": 45.0, "exit": 0, "last": ["last 7 days: 500812 tool calls answered; the files they pointed at come to about 17,395,392,577 tokens, the answers to 1,149,570,157, so about 16,248,349,002 tokens stayed out of the context; most used: graph_stats \u00d7167519, impact_of \u00d7166771, find_symbol \u00d7166522 (codegraph-index usage for details)"]}
{"phase": "incr1", "start": 1794759663.917, "seconds": 27.6, "exit": 0, "last": ["last 7 days: 623264 tool calls answered; the files they pointed at come to about 21,650,452,392 tokens, the answers to 1,434,685,296, so about 20,219,861,624 tokens stayed out of the context; most used: graph_stats \u00d7208424, impact_of \u00d7207553, find_symbol \u00d7207287 (codegraph-index usage for details)"]}
{"phase": "full2", "start": 1794759691.523, "seconds": 49.0, "exit": 0, "last": ["last 7 days: 849958 tool calls answered; the files they pointed at come to about 29,517,924,327 tokens, the answers to 1,954,203,913, so about 27,568,270,432 tokens stayed out of the context; most used: graph_stats \u00d7284339, impact_of \u00d7282957, find_symbol \u00d7282662 (codegraph-index usage for details)"]}
{"phase": "incr2", "start": 1794759740.501, "seconds": 29.0, "exit": 0, "last": ["last 7 days: 980623 tool calls answered; the files they pointed at come to about 34,044,335,745 tokens, the answers to 2,257,394,943, so about 31,793,126,640 tokens stayed out of the context; most used: graph_stats \u00d7328271, impact_of \u00d7326346, find_symbol \u00d7326006 (codegraph-index usage for details)"]}
```

Run 2 phases list, verbatim from the jsonl:

```json
{"phase": "full0", "start": 1794759772.434, "seconds": 47.5, "exit": 0, "last": ["last 7 days: 187620 tool calls answered; the files they pointed at come to about 6,518,321,943 tokens, the answers to 426,621,061, so about 6,092,077,616 tokens stayed out of the context; most used: graph_stats \u00d762789, impact_of \u00d762524, find_symbol \u00d762307 (codegraph-index usage for details)"]}
{"phase": "incr0", "start": 1794759819.961, "seconds": 23.1, "exit": 0, "last": ["last 7 days: 287662 tool calls answered; the files they pointed at come to about 9,989,115,144 tokens, the answers to 660,308,714, so about 9,330,850,490 tokens stayed out of the context; most used: graph_stats \u00d796268, impact_of \u00d795769, find_symbol \u00d795625 (codegraph-index usage for details)"]}
{"phase": "full1", "start": 1794759843.02, "seconds": 43.6, "exit": 0, "last": ["last 7 days: 487108 tool calls answered; the files they pointed at come to about 16,905,203,285 tokens, the answers to 1,117,040,243, so about 15,790,608,616 tokens stayed out of the context; most used: graph_stats \u00d7163187, impact_of \u00d7162063, find_symbol \u00d7161858 (codegraph-index usage for details)"]}
{"phase": "incr1", "start": 1794759886.666, "seconds": 27.1, "exit": 0, "last": ["last 7 days: 604504 tool calls answered; the files they pointed at come to about 20,985,010,321 tokens, the answers to 1,391,049,385, so about 19,598,066,288 tokens stayed out of the context; most used: graph_stats \u00d7202480, impact_of \u00d7201203, find_symbol \u00d7200821 (codegraph-index usage for details)"]}
{"phase": "full2", "start": 1794759913.755, "seconds": 48.0, "exit": 0, "last": ["last 7 days: 822429 tool calls answered; the files they pointed at come to about 28,541,603,100 tokens, the answers to 1,890,183,146, so about 26,655,964,386 tokens stayed out of the context; most used: graph_stats \u00d7275660, impact_of \u00d7273661, find_symbol \u00d7273108 (codegraph-index usage for details)"]}
{"phase": "incr2", "start": 1794759961.741, "seconds": 30.3, "exit": 0, "last": ["last 7 days: 957324 tool calls answered; the files they pointed at come to about 33,225,680,950 tokens, the answers to 2,203,608,164, so about 31,028,195,702 tokens stayed out of the context; most used: graph_stats \u00d7320800, impact_of \u00d7318561, find_symbol \u00d7317963 (codegraph-index usage for details)"]}
```

**errs: none in any run** (every `errs` list is empty).

### Loaded (6 runs, 24 burners = 3 × hw.logicalcpu 8)

```
python3 /tmp/cg-mac1/r4loop.py tools/CodeGraph.Bench /tmp/cg-mac1/work /tmp/cg-mac1/tpl /tmp/cg-mac1/bin/codegraph-mcp /tmp/cg-mac1/bin/codegraph-index 6 24 /tmp/cg-mac1/loaded.jsonl
```

Wrapper log (load wait, uptime before and during, exit, leftover-process check):

```
16:26:53 start: uptime: 16:26  up 28 mins, 2 users, load averages: 45.80 56.75 52.62
16:29:23 uptime during the runs: 16:29  up 31 mins, 2 users, load averages: 115.76 83.73 64.42
17:28:34 r4loop exit 0
leftover processes:
none
```

Each run's printed line:

```
run 0: burners 24 load 115.15380859375 calls 745689 errors 0 writes [0, 0, 0, 0, 0, 0] p99 12.8 ms max 1462 ms calls over 10 s 0
run 1: burners 24 load 115.27734375 calls 731154 errors 0 writes [0, 0, 0, 0, 0, 0] p99 12.8 ms max 1544 ms calls over 10 s 0
run 2: burners 24 load 105.58251953125 calls 704100 errors 0 writes [0, 0, 0, 0, 0, 0] p99 12.8 ms max 1551 ms calls over 10 s 0
run 3: burners 24 load 112.5390625 calls 699690 errors 0 writes [0, 0, 0, 0, 0, 0] p99 12.8 ms max 1459 ms calls over 10 s 0
run 4: burners 24 load 105.81005859375 calls 708210 errors 0 writes [0, 0, 0, 0, 0, 0] p99 12.8 ms max 1607 ms calls over 10 s 0
run 5: burners 24 load 113.330078125 calls 795456 errors 0 writes [0, 0, 0, 0, 0, 0] p99 12.7 ms max 1558 ms calls over 10 s 0
```

| run | calls | errors | writes (exit codes) | p99 ms | max ms | calls over 10 s | load avg at end |
|---|---|---|---|---|---|---|---|
| 0 | 745,689 | 0 | [0, 0, 0, 0, 0, 0] | 12.8 | 1462 | 0 | 115.2 |
| 1 | 731,154 | 0 | [0, 0, 0, 0, 0, 0] | 12.8 | 1544 | 0 | 115.3 |
| 2 | 704,100 | 0 | [0, 0, 0, 0, 0, 0] | 12.8 | 1551 | 0 | 105.6 |
| 3 | 699,690 | 0 | [0, 0, 0, 0, 0, 0] | 12.8 | 1459 | 0 | 112.5 |
| 4 | 708,210 | 0 | [0, 0, 0, 0, 0, 0] | 12.8 | 1607 | 0 | 105.8 |
| 5 | 795,456 | 0 | [0, 0, 0, 0, 0, 0] | 12.7 | 1558 | 0 | 113.3 |

Phases (indexer passes while the 8 readers ran; `last` is the indexer's last output line, which is the usage
summary codegraph-index prints after indexing):

| run | phase | seconds | exit |
|---|---|---|---|
| 0 | full0 | 153.0 | 0 |
| 0 | incr0 | 63.0 | 0 |
| 0 | full1 | 128.0 | 0 |
| 0 | incr1 | 70.5 | 0 |
| 0 | full2 | 133.7 | 0 |
| 0 | incr2 | 73.4 | 0 |
| 1 | full0 | 145.5 | 0 |
| 1 | incr0 | 64.4 | 0 |
| 1 | full1 | 126.9 | 0 |
| 1 | incr1 | 68.3 | 0 |
| 1 | full2 | 133.0 | 0 |
| 1 | incr2 | 73.5 | 0 |
| 2 | full0 | 141.2 | 0 |
| 2 | incr0 | 63.4 | 0 |
| 2 | full1 | 126.7 | 0 |
| 2 | incr1 | 68.0 | 0 |
| 2 | full2 | 131.7 | 0 |
| 2 | incr2 | 72.1 | 0 |
| 3 | full0 | 143.0 | 0 |
| 3 | incr0 | 64.3 | 0 |
| 3 | full1 | 124.5 | 0 |
| 3 | incr1 | 68.2 | 0 |
| 3 | full2 | 130.4 | 0 |
| 3 | incr2 | 72.7 | 0 |
| 4 | full0 | 143.5 | 0 |
| 4 | incr0 | 63.3 | 0 |
| 4 | full1 | 125.7 | 0 |
| 4 | incr1 | 65.7 | 0 |
| 4 | full2 | 133.7 | 0 |
| 4 | incr2 | 73.7 | 0 |
| 5 | full0 | 140.1 | 0 |
| 5 | incr0 | 63.6 | 0 |
| 5 | full1 | 136.3 | 0 |
| 5 | incr1 | 71.3 | 0 |
| 5 | full2 | 135.8 | 0 |
| 5 | incr2 | 78.6 | 0 |

Run 0 phases list, verbatim from the jsonl:

```json
{"phase": "full0", "start": 1794760017.541, "seconds": 153.0, "exit": 0, "last": ["last 7 days: 201676 tool calls answered; the files they pointed at come to about 7,018,937,962 tokens, the answers to 459,001,356, so about 6,560,339,338 tokens stayed out of the context; most used: find_symbol \u00d767288, impact_of \u00d767266, graph_stats \u00d767122 (codegraph-index usage for details)"]}
{"phase": "incr0", "start": 1794760170.577, "seconds": 63.0, "exit": 0, "last": ["last 7 days: 260359 tool calls answered; the files they pointed at come to about 9,063,863,770 tokens, the answers to 596,692,777, so about 8,468,544,577 tokens stayed out of the context; most used: find_symbol \u00d786903, impact_of \u00d786858, graph_stats \u00d786598 (codegraph-index usage for details)"]}
{"phase": "full1", "start": 1794760233.571, "seconds": 128.0, "exit": 0, "last": ["last 7 days: 404660 tool calls answered; the files they pointed at come to about 14,088,918,568 tokens, the answers to 928,472,657, so about 13,162,107,345 tokens stayed out of the context; most used: find_symbol \u00d7135077, impact_of \u00d7135010, graph_stats \u00d7134573 (codegraph-index usage for details)"]}
{"phase": "incr1", "start": 1794760361.527, "seconds": 70.5, "exit": 0, "last": ["last 7 days: 478115 tool calls answered; the files they pointed at come to about 16,648,274,975 tokens, the answers to 1,100,586,667, so about 15,550,503,202 tokens stayed out of the context; most used: find_symbol \u00d7159648, impact_of \u00d7159524, graph_stats \u00d7158943 (codegraph-index usage for details)"]}
{"phase": "full2", "start": 1794760432.004, "seconds": 133.7, "exit": 0, "last": ["last 7 days: 628876 tool calls answered; the files they pointed at come to about 21,897,333,597 tokens, the answers to 1,446,916,617, so about 20,453,531,952 tokens stayed out of the context; most used: find_symbol \u00d7210154, impact_of \u00d7209766, graph_stats \u00d7208956 (codegraph-index usage for details)"]}
{"phase": "incr2", "start": 1794760565.715, "seconds": 73.4, "exit": 0, "last": ["last 7 days: 697823 tool calls answered; the files they pointed at come to about 24,297,606,239 tokens, the answers to 1,608,103,273, so about 22,693,636,328 tokens stayed out of the context; most used: find_symbol \u00d7233215, impact_of \u00d7232751, graph_stats \u00d7231857 (codegraph-index usage for details)"]}
```

Run 1 phases list, verbatim from the jsonl:

```json
{"phase": "full0", "start": 1794760644.061, "seconds": 145.5, "exit": 0, "last": ["last 7 days: 194413 tool calls answered; the files they pointed at come to about 6,769,232,315 tokens, the answers to 442,815,429, so about 6,326,805,170 tokens stayed out of the context; most used: impact_of \u00d764903, find_symbol \u00d764796, graph_stats \u00d764714 (codegraph-index usage for details)"]}
{"phase": "incr0", "start": 1794760789.584, "seconds": 64.4, "exit": 0, "last": ["last 7 days: 257654 tool calls answered; the files they pointed at come to about 8,970,916,428 tokens, the answers to 591,289,254, so about 8,381,115,860 tokens stayed out of the context; most used: impact_of \u00d786013, find_symbol \u00d785862, graph_stats \u00d785779 (codegraph-index usage for details)"]}
{"phase": "full1", "start": 1794760854.01, "seconds": 126.9, "exit": 0, "last": ["last 7 days: 396974 tool calls answered; the files they pointed at come to about 13,820,061,827 tokens, the answers to 911,338,852, so about 12,910,489,485 tokens stayed out of the context; most used: impact_of \u00d7132452, find_symbol \u00d7132439, graph_stats \u00d7132083 (codegraph-index usage for details)"]}
{"phase": "incr1", "start": 1794760980.943, "seconds": 68.3, "exit": 0, "last": ["last 7 days: 466470 tool calls answered; the files they pointed at come to about 16,239,202,071 tokens, the answers to 1,073,821,405, so about 15,168,229,422 tokens stayed out of the context; most used: find_symbol \u00d7155801, impact_of \u00d7155581, graph_stats \u00d7155088 (codegraph-index usage for details)"]}
{"phase": "full2", "start": 1794761049.273, "seconds": 133.0, "exit": 0, "last": ["last 7 days: 610975 tool calls answered; the files they pointed at come to about 21,268,545,068 tokens, the answers to 1,405,604,139, so about 19,866,077,367 tokens stayed out of the context; most used: find_symbol \u00d7204232, impact_of \u00d7203708, graph_stats \u00d7203035 (codegraph-index usage for details)"]}
{"phase": "incr2", "start": 1794761182.239, "seconds": 73.5, "exit": 0, "last": ["last 7 days: 686800 tool calls answered; the files they pointed at come to about 23,908,297,665 tokens, the answers to 1,582,903,632, so about 22,329,692,079 tokens stayed out of the context; most used: find_symbol \u00d7229673, impact_of \u00d7228962, graph_stats \u00d7228165 (codegraph-index usage for details)"]}
```

Run 2 phases list, verbatim from the jsonl:

```json
{"phase": "full0", "start": 1794761260.683, "seconds": 141.2, "exit": 0, "last": ["last 7 days: 178814 tool calls answered; the files they pointed at come to about 6,221,947,489 tokens, the answers to 406,807,096, so about 5,815,497,351 tokens stayed out of the context; most used: find_symbol \u00d759713, impact_of \u00d759608, graph_stats \u00d759493 (codegraph-index usage for details)"]}
{"phase": "incr0", "start": 1794761401.874, "seconds": 63.4, "exit": 0, "last": ["last 7 days: 239222 tool calls answered; the files they pointed at come to about 8,322,325,152 tokens, the answers to 548,217,361, so about 7,775,477,625 tokens stayed out of the context; most used: find_symbol \u00d779900, impact_of \u00d779719, graph_stats \u00d779603 (codegraph-index usage for details)"]}
{"phase": "full1", "start": 1794761465.287, "seconds": 126.7, "exit": 0, "last": ["last 7 days: 377704 tool calls answered; the files they pointed at come to about 13,136,447,583 tokens, the answers to 865,771,218, so about 12,272,322,433 tokens stayed out of the context; most used: find_symbol \u00d7126283, impact_of \u00d7125779, graph_stats \u00d7125642 (codegraph-index usage for details)"]}
{"phase": "incr1", "start": 1794761591.97, "seconds": 68.0, "exit": 0, "last": ["last 7 days: 446159 tool calls answered; the files they pointed at come to about 15,517,845,103 tokens, the answers to 1,025,936,074, so about 14,494,654,627 tokens stayed out of the context; most used: find_symbol \u00d7149183, impact_of \u00d7148577, graph_stats \u00d7148399 (codegraph-index usage for details)"]}
{"phase": "full2", "start": 1794761659.967, "seconds": 131.7, "exit": 0, "last": ["last 7 days: 590284 tool calls answered; the files they pointed at come to about 20,533,536,495 tokens, the answers to 1,356,939,939, so about 19,179,629,518 tokens stayed out of the context; most used: find_symbol \u00d7197389, impact_of \u00d7196602, graph_stats \u00d7196293 (codegraph-index usage for details)"]}
{"phase": "incr2", "start": 1794761791.676, "seconds": 72.1, "exit": 0, "last": ["last 7 days: 661081 tool calls answered; the files they pointed at come to about 22,997,493,625 tokens, the answers to 1,522,552,660, so about 21,479,048,359 tokens stayed out of the context; most used: find_symbol \u00d7221009, impact_of \u00d7220213, graph_stats \u00d7219859 (codegraph-index usage for details)"]}
```

Run 3 phases list, verbatim from the jsonl:

```json
{"phase": "full0", "start": 1794761868.697, "seconds": 143.0, "exit": 0, "last": ["last 7 days: 178016 tool calls answered; the files they pointed at come to about 6,192,261,834 tokens, the answers to 404,818,809, so about 5,787,798,453 tokens stayed out of the context; most used: find_symbol \u00d759466, impact_of \u00d759312, graph_stats \u00d759238 (codegraph-index usage for details)"]}
{"phase": "incr0", "start": 1794762011.734, "seconds": 64.3, "exit": 0, "last": ["last 7 days: 239526 tool calls answered; the files they pointed at come to about 8,336,539,646 tokens, the answers to 549,231,296, so about 7,788,731,094 tokens stayed out of the context; most used: find_symbol \u00d780138, impact_of \u00d779824, graph_stats \u00d779564 (codegraph-index usage for details)"]}
{"phase": "full1", "start": 1794762075.987, "seconds": 124.5, "exit": 0, "last": ["last 7 days: 368189 tool calls answered; the files they pointed at come to about 12,818,093,402 tokens, the answers to 844,990,478, so about 11,974,781,766 tokens stayed out of the context; most used: find_symbol \u00d7123206, impact_of \u00d7122736, graph_stats \u00d7122247 (codegraph-index usage for details)"]}
{"phase": "incr1", "start": 1794762200.448, "seconds": 68.2, "exit": 0, "last": ["last 7 days: 437838 tool calls answered; the files they pointed at come to about 15,243,515,642 tokens, the answers to 1,007,925,145, so about 14,238,354,629 tokens stayed out of the context; most used: find_symbol \u00d7146606, impact_of \u00d7145932, graph_stats \u00d7145300 (codegraph-index usage for details)"]}
{"phase": "full2", "start": 1794762268.681, "seconds": 130.4, "exit": 0, "last": ["last 7 days: 582207 tool calls answered; the files they pointed at come to about 20,268,634,183 tokens, the answers to 1,339,477,640, so about 18,932,208,183 tokens stayed out of the context; most used: find_symbol \u00d7194959, impact_of \u00d7194030, graph_stats \u00d7193218 (codegraph-index usage for details)"]}
{"phase": "incr2", "start": 1794762399.113, "seconds": 72.7, "exit": 0, "last": ["last 7 days: 653280 tool calls answered; the files they pointed at come to about 22,746,771,172 tokens, the answers to 1,505,978,942, so about 21,244,933,548 tokens stayed out of the context; most used: find_symbol \u00d7218806, impact_of \u00d7217749, graph_stats \u00d7216725 (codegraph-index usage for details)"]}
```

Run 4 phases list, verbatim from the jsonl:

```json
{"phase": "full0", "start": 1794762476.728, "seconds": 143.5, "exit": 0, "last": ["last 7 days: 180183 tool calls answered; the files they pointed at come to about 6,268,024,064 tokens, the answers to 409,831,077, so about 5,858,552,933 tokens stayed out of the context; most used: find_symbol \u00d760137, impact_of \u00d760055, graph_stats \u00d759991 (codegraph-index usage for details)"]}
{"phase": "incr0", "start": 1794762620.207, "seconds": 63.3, "exit": 0, "last": ["last 7 days: 241181 tool calls answered; the files they pointed at come to about 8,391,959,144 tokens, the answers to 553,019,153, so about 7,840,354,351 tokens stayed out of the context; most used: find_symbol \u00d780477, impact_of \u00d780414, graph_stats \u00d780290 (codegraph-index usage for details)"]}
{"phase": "full1", "start": 1794762683.491, "seconds": 125.7, "exit": 0, "last": ["last 7 days: 377352 tool calls answered; the files they pointed at come to about 13,129,228,369 tokens, the answers to 865,512,743, so about 12,265,401,240 tokens stayed out of the context; most used: find_symbol \u00d7126112, impact_of \u00d7125741, graph_stats \u00d7125499 (codegraph-index usage for details)"]}
{"phase": "incr1", "start": 1794762809.148, "seconds": 65.7, "exit": 0, "last": ["last 7 days: 439069 tool calls answered; the files they pointed at come to about 15,282,032,545 tokens, the answers to 1,010,364,111, so about 14,274,343,712 tokens stayed out of the context; most used: find_symbol \u00d7146770, impact_of \u00d7146364, graph_stats \u00d7145935 (codegraph-index usage for details)"]}
{"phase": "full2", "start": 1794762874.882, "seconds": 133.7, "exit": 0, "last": ["last 7 days: 590766 tool calls answered; the files they pointed at come to about 20,561,736,461 tokens, the answers to 1,358,889,145, so about 19,205,825,312 tokens stayed out of the context; most used: find_symbol \u00d7197438, impact_of \u00d7196940, graph_stats \u00d7196388 (codegraph-index usage for details)"]}
{"phase": "incr2", "start": 1794763008.552, "seconds": 73.7, "exit": 0, "last": ["last 7 days: 663764 tool calls answered; the files they pointed at come to about 23,100,714,270 tokens, the answers to 1,529,557,444, so about 21,575,279,922 tokens stayed out of the context; most used: find_symbol \u00d7221865, impact_of \u00d7221243, graph_stats \u00d7220656 (codegraph-index usage for details)"]}
```

Run 5 phases list, verbatim from the jsonl:

```json
{"phase": "full0", "start": 1794763087.146, "seconds": 140.1, "exit": 0, "last": ["last 7 days: 177419 tool calls answered; the files they pointed at come to about 6,181,459,062 tokens, the answers to 404,287,426, so about 5,777,525,330 tokens stayed out of the context; most used: impact_of \u00d759253, find_symbol \u00d759217, graph_stats \u00d758949 (codegraph-index usage for details)"]}
{"phase": "incr0", "start": 1794763227.247, "seconds": 63.6, "exit": 0, "last": ["last 7 days: 238778 tool calls answered; the files they pointed at come to about 8,314,237,127 tokens, the answers to 547,947,679, so about 7,767,688,726 tokens stayed out of the context; most used: find_symbol \u00d779703, impact_of \u00d779678, graph_stats \u00d779397 (codegraph-index usage for details)"]}
{"phase": "full1", "start": 1794763290.843, "seconds": 136.3, "exit": 0, "last": ["last 7 days: 406267 tool calls answered; the files they pointed at come to about 14,145,871,354 tokens, the answers to 933,067,385, so about 13,214,537,729 tokens stayed out of the context; most used: impact_of \u00d7135579, find_symbol \u00d7135544, graph_stats \u00d7135144 (codegraph-index usage for details)"]}
{"phase": "incr1", "start": 1794763427.193, "seconds": 71.3, "exit": 0, "last": ["last 7 days: 491398 tool calls answered; the files they pointed at come to about 17,107,651,436 tokens, the answers to 1,132,312,968, so about 15,978,407,718 tokens stayed out of the context; most used: impact_of \u00d7163970, find_symbol \u00d7163905, graph_stats \u00d7163523 (codegraph-index usage for details)"]}
{"phase": "full2", "start": 1794763498.523, "seconds": 135.8, "exit": 0, "last": ["last 7 days: 656864 tool calls answered; the files they pointed at come to about 22,868,957,533 tokens, the answers to 1,512,614,681, so about 21,359,741,976 tokens stayed out of the context; most used: find_symbol \u00d7219206, impact_of \u00d7219156, graph_stats \u00d7218502 (codegraph-index usage for details)"]}
{"phase": "incr2", "start": 1794763634.367, "seconds": 78.6, "exit": 0, "last": ["last 7 days: 748707 tool calls answered; the files they pointed at come to about 26,067,336,587 tokens, the answers to 1,727,307,999, so about 24,344,739,538 tokens stayed out of the context; most used: find_symbol \u00d7249888, impact_of \u00d7249798, graph_stats \u00d7249021 (codegraph-index usage for details)"]}
```

**errs: none in any run** (every `errs` list is empty).


## 8. Leftover processes

After the loaded batch finished (17:28), `pgrep -fl` for python3 burners, r4loop, codegraph-index and codegraph-mcp
found nothing. The wrapper's own check at the end of each batch also printed "none". Load averages at that moment:
90.76 109.87 112.30 (falling from the burners).

## What did not run, and what differed from the plan

- **Lost evidence:** the reboot at 15:58 erased every log file from steps 1–7 (dotnet-test.log, build logs, the
  install logs, index-full.log, the first daemon.log, step6.out, init.log, uninstall.log). Their contents in this
  report are the console outputs captured during the session, not re-read from disk.
- **bin-release after the reboot:** the uninstall in step 7 had already deleted it; it was not reinstalled, so the
  release build was not exercised in steps 8 and 9 (they use /tmp/cg-mac1/bin, the from-source build, as planned).
- **From-source install as literally specified** (cwd /tmp/cg-mac1/src/codegraph): failed twice with different
  errors (section 2a). The /tmp/cg-mac1/bin used by steps 5–9 came from the same command run from
  /private/tmp/cg-mac1/src/codegraph.
- **"Mac otherwise idle":** the idle batch started when the 1-minute load had fallen to 3.51 (17 minutes after the
  reboot). The load averages reported "during the idle runs" (58.34 44.04 44.92) include the test's own 8 readers,
  8 servers and indexer, plus whatever post-boot work was still running; the 5- and 15-minute figures still carry
  the boot storm.
- **r4loop.py**: created from the pasted text with indentation, `*args`/`**args` and `\n` escapes restored
  (section 7). It did not crash; there is no traceback to paste.
- **Step 6 was run twice** (section 4): the first run's timings were invalid because of my check, and were discarded.
- **Steps 1–7 ran before the reboot, 8–9 after it**, on a rebuilt tree at the same commit a3150ec.

## Files

- /tmp/cg-mac1/mac-report-1.md (this file)
- /tmp/cg-mac1/idle.jsonl (3 records)
- /tmp/cg-mac1/loaded.jsonl (6 records)
- also kept under /tmp/cg-mac1: r4loop.py, idle.log, loaded.log, resilience.out, the post-reboot install, restore
  and index logs, and helpers/ (step6.py, run_r4.sh, step9_section.py)
