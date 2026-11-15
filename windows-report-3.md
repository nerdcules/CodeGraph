# CodeGraph Windows re-test 3: branch any-mcp-client at af1eab5

Date: 2026-11-15. Machine: Windows 11 Pro 10.0.26300, x64. SDKs: 10.0.404 and 11.0.100. pwsh 7.7.0, Windows PowerShell 5.1.26100.9818, Python 3.14.7, Node v24.20.0.

All logs, clones, install folders and copies are in `C:\Users\seuna\AppData\Local\Temp\cg-win3` (`$env:TEMP\cg-win3`). I did not change product code. I did not change the user PATH, and I did not use `-AddToPath`. The changes outside `$env:TEMP\cg-win3` are: the `git pull`, `npm ci` and build outputs in the clone `Z:\CodeGraph`, and `C:\tools\codegraph` (finding 1). The user deleted `C:\tools` after the test.

## Summary

| Step | Result |
|---|---|
| 1 Branch | HEAD is af1eab5. |
| 1 `npm ci` | Exit 0. |
| 1 Tests | Exit 0. Total 441: 440 passed, 0 failed, 1 skipped. The TypeScript live test and all 5 `LspPathTests` (with the 2 Windows tests) run and pass. |
| 1 Release build, `-warnaserror` | Exit 0, 0 warnings, 0 errors. |
| 2 Install, pwsh 7 | Exit 0. No `note:` line. |
| 2 Install, 5.1 | Exit 0. No `note:` line. |
| 2 `-ObsidianVault x` | **No parameter error.** The script installed release 1.1.0 into `C:\tools\codegraph`, then threw `2 repo setup(s) failed`. See finding 1. |
| 3 Restored clone, `--full` | 3,244 nodes, 19,843 edges. 11.2 s (indexer), 14.6 s (wall clock). |
| 4 Live daemon, 5 saves | Each new method found 1.5 s to 4.4 s after the save. Memory: 140 MB working set before the saves, 393 MB at the end. |
| 5 Uninstall, 5.1 | Exit 0. One line for each file and folder, with its path. All 6 files keep `other` and `note`, with 2-space indents. |
| 6 Resilience, no shim | 12 of 12 PASS. R4: 438,207 calls, 0 errors, p99 6.0 ms, max 528 ms. |

## Findings for the author

1. **`-ObsidianVault` does not cause a parameter error. It starts a release install into the default folder.** `$Repo` has `Position = 0, ValueFromRemainingArguments = $true`, so PowerShell binds `-ObsidianVault` and `x` as 2 repo paths. With no `-InstallDir` and no `-FromSource`, `.\install.ps1 -ObsidianVault x` downloaded `codegraph-win-x64.zip` (latest, 1.1.0) and installed it into `C:\tools\codegraph`. Then it ran `init` on `-ObsidianVault` (`unknown option: -ObsidianVault`) and on `x` (`not found: Z:\CodeGraph\codegraph\x`), and threw `2 repo setup(s) failed; see above`. The full output is in step 2.
   - `C:\tools` and `C:\tools\codegraph` did not exist before. Both have the creation time 10:31:16, which is the time of this run. Thus the run deleted nothing that was there before.
   - The user PATH has no `codegraph` entry.
   - The user approved the deletion of `C:\tools`, but the Claude Code harness blocks `Remove-Item` on that path. After the report, the user deleted it with `rm -rf /c/tools`. `Test-Path C:\tools` then gave `False`.
   - I did not run the 5.1 variant, because it does the same thing. The user agreed to skip it.
2. **`find_symbol` waits for the new graph.** For each of the 5 saves, the first `find_symbol` call after the save already found the new method. That one call took 1.5 s to 4.4 s. Thus the time in step 4 is the time of one call, not a poll loop.
3. **The daemon's `--log` file holds the progress spinner.** The `--log` file of the step 4 daemon has 513 lines for 1 start, 5 saves and 1 restore. 322 of them are spinner frames, such as `| indexing: [2/6] CodeGraph.Indexer: 3/6 files (1s)`.

## 1. Branch, tests and build

Commands, in `Z:\CodeGraph`:

| Command | Exit | Output |
|---|---|---|
| `git fetch origin` | 0 | `d3350b3..af1eab5  any-mcp-client -> origin/any-mcp-client` |
| `git checkout any-mcp-client` | 0 | `Already on 'any-mcp-client'`, behind by 7 commits |
| `git pull` | 0 | `Updating d3350b3..af1eab5`, fast-forward, 56 files changed |
| `git rev-parse --short HEAD` | 0 | `af1eab5` |

`npm ci` in `codegraph\extractors\typescript`: exit 0. `added 4 packages, and audited 5 packages in 3s`, `found 0 vulnerabilities`.

### `dotnet test tests/CodeGraph.Tests` (in `codegraph`)

Exit 0. Log: `step1-test.log`.

```
  total: 441
  failed: 0
  succeeded: 440
  skipped: 1
  duration: 1m 14s 835ms
```

- Failed: none.
- Skipped: `CodeGraph.Tests.ScipRunnerTests.A_go_module_runs_its_scip_indexer_in_the_module_folder_and_imports_the_index` ("the stand-in indexer is a shell script").

The summary does not list the tests that passed. Thus I ran the named tests again with `--no-build --output Detailed`:

- `dotnet test tests/CodeGraph.Tests --no-build --output Detailed -- --filter-class "*LspPathTests"`: exit 0, total 5, succeeded 5, skipped 0.

```
passed CodeGraph.Tests.LspPathTests.A_file_uri_round_trips_to_its_full_path (13ms)
passed CodeGraph.Tests.LspPathTests.A_uri_that_is_not_a_file_comes_back_unchanged (0ms)
passed CodeGraph.Tests.LspPathTests.A_canonical_uri_is_unchanged_by_canonicalising_it_again (0ms)
passed CodeGraph.Tests.LspPathTests.A_servers_windows_uri_is_keyed_as_ours (0ms)
passed CodeGraph.Tests.LspPathTests.A_lower_case_drive_letter_comes_back_upper_case_on_windows (0ms)
```

The 2 Windows tests are `A_servers_windows_uri_is_keyed_as_ours` and `A_lower_case_drive_letter_comes_back_upper_case_on_windows`. They skip unless `OperatingSystem.IsWindows()`, and here they passed.

- `dotnet test tests/CodeGraph.Tests --no-build --output Detailed -- --filter-method "*References_rename_and_a_what_if_edit_come_from_the_language_server"`: exit 0, total 1, succeeded 1.

```
passed CodeGraph.Tests.TypeScriptLiveTests.References_rename_and_a_what_if_edit_come_from_the_language_server (5s 831ms)
```

My first try put `--filter-class` and `--filter-method` in one command. That matched 0 tests (exit 8), so I ran the two filters separately.

### `dotnet build CodeGraph.slnx -c Release -warnaserror`

Exit 0. `Build succeeded. 0 Warning(s) 0 Error(s)`, 6.4 s. Log: `step1-build.log`.

## 2. Install

| Shell | Command | Exit | `note:` lines |
|---|---|---|---|
| pwsh 7.7.0 | `.\install.ps1 -FromSource -SkipTests -InstallDir $env:TEMP\cg-win3\bin-ps7` | 0 | none |
| 5.1 | `powershell.exe -NoProfile -File .\install.ps1 -FromSource -SkipTests -InstallDir $env:TEMP\cg-win3\bin-ps51` | 0 | none |

Both logs have these steps: `Installing the TypeScript extractor's packages (Node v24.20.0)`, `Building (warnings are errors)` with `0 Warning(s)` and `0 Error(s)`, `Publishing to … (win-x64, ReadyToRun)`, `Checking the executables start`, `Installed`. Both install folders have `codegraph-index.exe` and `codegraph-mcp.exe`. Logs: `step2-install-ps7.log`, `step2-install-ps51.log`.

### `.\install.ps1 -ObsidianVault x` (pwsh 7.7.0, in `Z:\CodeGraph\codegraph`)

The script threw a terminating error. Full output (log `step2-obsidian-ps7.log`, then the error record):

```
==> Downloading codegraph-win-x64.zip (latest)
  checksum ok

==> Installing to C:\tools\codegraph
  codegraph 1.1.0

==> Checking the executables start
ok: codegraph 1.1.0

==> Setting up -ObsidianVault
unknown option: -ObsidianVault
usage: codegraph-index <target> [--db <path>] [--full] [--watch] [--log <file>]
       (… 21 more lines of the 1.1.0 usage text; the full text is in the log …)

==> Setting up x
codegraph init: not found: Z:\CodeGraph\codegraph\x

==> Installed
  C:\tools\codegraph\codegraph-index.exe
  C:\tools\codegraph\codegraph-mcp.exe

Set up a repo (writes .mcp.json and .gitignore at its root, then builds the graph):
  & 'C:\tools\codegraph\codegraph-index.exe' init <repo-or-solution> [--live] [--client copilot,cursor,gemini,codex,roo,kilo,continue]
Keep a graph fresh while you work:
  & 'C:\tools\codegraph\codegraph-index.exe' <App.sln> --watch
```

```
Exception: Z:\CodeGraph\codegraph\install.ps1:281
Line |
 281 |  … f ($failed -gt 0) { throw "$failed repo setup(s) failed; see above" }
     |                        ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
     | 2 repo setup(s) failed; see above
```

There was no error about the parameter `-ObsidianVault`. Finding 1 gives the effects on `C:\tools`.

## 3. Restored clone, full index

| Command | Exit | Result |
|---|---|---|
| `git clone Z:\CodeGraph $env:TEMP\cg-win3\repo` | 0 | HEAD af1eab5 |
| `dotnet restore repo\codegraph\CodeGraph.slnx` | 0 | 6 projects restored |
| `bin-ps7\codegraph-index.exe repo\codegraph\CodeGraph.slnx --full` | 0 | see below |

```
compiler: Roslyn 5.9.0 (up to C# 14); projects use C# 14; 0 compile error(s) in declarations
indexed 3244 nodes, 19843 edges (full) in 11.2s → C:\Users\seuna\AppData\Local\Temp\cg-win3\repo\.codegraph\graph.db
```

Wall-clock time of the command: 14.6 s. `graph.db`: 23,605,248 bytes.

After this step, I copied `repo` to `template` for step 6.

## 4. C# live daemon

Daemon: `bin-ps7\codegraph-index.exe repo\codegraph\CodeGraph.slnx --watch --log step4-daemon.log`, started with `Start-Process` (pid 13988).

A Python script (`mcpclient.Client` from `tools\CodeGraph.Bench`) kept one `bin-ps7\codegraph-mcp.exe --root repo` open. It waited until `live_status` said `running` and not `loading`. Then, for each save, it added `public static void WinLiveProbe<n>() { }` before the last `}` of `codegraph\src\CodeGraph.Core\Worktree.cs`, wrote the file, and called `find_symbol` (`query` = the method name, `nameMatch` = `Exact`) every 50 ms until the answer held the name. The time is from the end of the write to that answer.

| Save | Method | Found after | `find_symbol` calls | Daemon index (from its log) | Graph after |
|---|---|---|---|---|---|
| 1 | WinLiveProbe1 | 4,433 ms | 1 | 10 files walked in 4.4 s | 3,245 nodes, 19,844 edges |
| 2 | WinLiveProbe2 | 2,239 ms | 1 | 10 files walked in 2.1 s | 3,246 nodes, 19,845 edges |
| 3 | WinLiveProbe3 | 2,107 ms | 1 | 10 files walked in 2.0 s | 3,247 nodes, 19,846 edges |
| 4 | WinLiveProbe4 | 1,915 ms | 1 | 10 files walked in 1.9 s | 3,248 nodes, 19,847 edges |
| 5 | WinLiveProbe5 | 1,485 ms | 1 | 10 files walked in 1.4 s | 3,249 nodes, 19,848 edges |

Restore: `git -C repo checkout -- codegraph/src/CodeGraph.Core/Worktree.cs`, exit 0. Then `git status --short` was empty. The daemon indexed again (10 files walked in 1.4 s, 3,244 nodes, 19,843 edges). 5 s after the restore, `find_symbol WinLiveProbe1` answered `no symbols match 'WinLiveProbe1'. …`.

`live_status` memory figure:

| When | `memory` line |
|---|---|
| Before the saves | `140 MB working set, 8 MB managed heap` |
| After the 5 saves | `369 MB working set, 80 MB managed heap` |
| After the restore | `393 MB working set, 86 MB managed heap` |

`live_status` before the saves, full text:

```
live daemon : running (pid 13988) since 2026-11-15 11:22:38Z
solution    : C:\Users\seuna\AppData\Local\Temp\cg-win3\repo\codegraph\CodeGraph.slnx (6 project(s), 112 document(s))
graph       : C:\Users\seuna\AppData\Local\Temp\cg-win3\repo\.codegraph\graph.db
last index  : 2026-11-15 11:22:40Z (0.21s)
state       : idle, graph matches the working tree
memory      : 140 MB working set, 8 MB managed heap
lifetime    : runs until stopped
```

I stopped the daemon with `Stop-Process` after the step. Logs: `step4-saves.log`, `step4-daemon.log`.

## 5. Uninstall under Windows PowerShell 5.1

`bin-ps51\codegraph-index.exe init repo\codegraph\CodeGraph.slnx --client copilot,cursor,roo`: exit 0. It wrote 6 files:

- `repo\.mcp.json`
- `repo\.vscode\mcp.json`
- `repo\codegraph\.vs\mcp.json`
- `repo\.github\mcp.json`
- `repo\.cursor\mcp.json`
- `repo\.roo\mcp.json`

In each file, I added a server `"other"` beside `"codegraph"` and a top-level key `"note": "keep me"`, with 2-space indents. Copies as init wrote them, before the uninstall and after it, are in `step5-configs`.

`powershell.exe -NoProfile -File .\uninstall.ps1 -InstallDir $env:TEMP\cg-win3\bin-ps51 -Repo $env:TEMP\cg-win3\repo -Yes`: exit 0. Full output:

```

==> Install folder: C:\Users\seuna\AppData\Local\Temp\cg-win3\bin-ps51
deleted C:\Users\seuna\AppData\Local\Temp\cg-win3\bin-ps51

==> Repo: C:\Users\seuna\AppData\Local\Temp\cg-win3\repo
removed the codegraph entry from C:\Users\seuna\AppData\Local\Temp\cg-win3\repo\.mcp.json
removed the codegraph entry from C:\Users\seuna\AppData\Local\Temp\cg-win3\repo\.cursor\mcp.json
removed the codegraph entry from C:\Users\seuna\AppData\Local\Temp\cg-win3\repo\.vscode\mcp.json
removed the codegraph entry from C:\Users\seuna\AppData\Local\Temp\cg-win3\repo\.github\mcp.json
removed the codegraph entry from C:\Users\seuna\AppData\Local\Temp\cg-win3\repo\.roo\mcp.json
removed the codegraph entry from C:\Users\seuna\AppData\Local\Temp\cg-win3\repo\codegraph\.vs\mcp.json
deleted C:\Users\seuna\AppData\Local\Temp\cg-win3\repo\.codegraph

Done.
```

After it, `bin-ps51` and `repo\.codegraph` do not exist, and `bin-ps7` is still there. All 6 files exist. Each has only `other` in its server list, and `"note": "keep me"`. Each has 2-space indents and LF line ends.

`repo\.roo\mcp.json` after the uninstall:

```json
{
  "mcpServers": {
    "other": {
      "type": "stdio",
      "command": "other-server",
      "args": [
        "--flag"
      ]
    }
  },
  "note": "keep me"
}
```

## 6. Resilience battery, no shim

Template: `$env:TEMP\cg-win3\template`, the copy of `repo` after the restore and the `--full` index of step 3 (3,244 nodes). It has no config files.

`python tools\CodeGraph.Bench\resilience.py $env:TEMP\cg-win3\res $env:TEMP\cg-win3\template $env:TEMP\cg-win3\bin-ps7\codegraph-mcp.exe $env:TEMP\cg-win3\bin-ps7\codegraph-index.exe`, in `Z:\CodeGraph\codegraph`, with no shim: exit 0 after 354.9 s, `12 of 12 passed`. Log: `step6-resilience.log`.

| Scenario | Result | Detail |
|---|---|---|
| R1 no graph, then built while the server runs | PASS | before: "no code graph at …" (error=True); after build: 3244 nodes, 19843 edges |
| R2a corrupt graph: server answers, stays up | PASS | alive=True; 3 answers "… failed: the graph file …" |
| R2b corrupt graph: indexer recovers | PASS | incremental exit 0; then 3244 nodes, 19843 edges |
| R3 kill -9 mid-index | PASS | full at 2 s and 9 s killed; full at 13, 14.5 and 15.5 s finished first; incremental at 1.5, 2.3 and 2.6 s killed. Each time 3244 nodes, no error. Next index exit 0, 3244 nodes. |
| R4 8 readers during 3 full + 3 incremental writes | PASS | see below |
| R5 malformed requests, then a normal call | PASS | 12 probes, each with an answer or an error, 1 ms to 64 ms |
| R5b every error says what was wrong | PASS | no opaque "An error occurred" answers; graph intact after the SQL-looking query (3245 nodes) |
| R6 live daemon killed under the server | PASS | daemon pid 8764 killed; then "no live daemon for …"; graph tools 3245 nodes; `live_status start=true`: running (pid 28748) |
| R7 stdin closed: the server exits | PASS | exit 0 after 0.0 s |
| R8 `--root` edge cases | PASS | all 4 answers: "⚠ STALE INDEX: HEAD is af1eab5 but the graph was built at 2d13546; 16 …" |
| R9 edited file without a daemon: answer flagged stale | PASS | "⚠ STALE INDEX: 1 indexed file(s) edited since (Worktree.cs). …" |
| R10 init with broken config files | PASS | non-object `.mcp.json`: exit 1, file untouched; JSONC with comments: exit 0, servers codegraph and other; read-only `.cursor/mcp.json`: exit 1 ("cannot update … Access to the path …") |

R4:

| Item | Value |
|---|---|
| Calls | 438,207 |
| Errors | 0 |
| Writer exits | 0, 0, 0, 0, 0, 0 |
| Latency p50 | 2.5 ms |
| Latency p99 | 6.0 ms |
| Latency max | 528 ms |
| Node counts seen | 3244, 3245 |
| New method visible after | True |

R4 error texts: none, because there were 0 errors.

R8 answers come from the graph of `Z:\CodeGraph` (built at 2d13546), where the battery ran, not from a graph under the test folder. This is the same behavior as in the first 2 reports.

## Files in `$env:TEMP\cg-win3`

| Path | Content |
|---|---|
| `step1-npm-ci.log`, `step1-test.log`, `step1-focus-*.log`, `step1-build.log` | Step 1 |
| `step2-install-ps7.log`, `step2-install-ps51.log`, `step2-obsidian-ps7.log` | Step 2 |
| `bin-ps7` | The pwsh 7 install, used in steps 3, 4 and 6 |
| `repo`, `step3-*.log` | Step 3 |
| `step4-saves.log`, `step4-daemon.log`, `step4-daemon.pid` | Step 4 |
| `step5-init.log`, `step5-uninstall-ps51.log`, `step5-configs` | Step 5 |
| `template`, `res`, `step6-resilience.log` | Step 6 |
