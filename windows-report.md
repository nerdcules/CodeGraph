# CodeGraph Windows test run: branch any-mcp-client at 2d13546

Date: 2026-11-14. Machine: Windows 11 Pro 10.0.26300, x64. All scratch, install and copy folders are in `C:\Users\seuna\AppData\Local\Temp\cg-win` (`$env:TEMP\cg-win`). I did not change product code. I did not touch `C:\tools\codegraph` or the user PATH, and I did not use `-AddToPath`.

## Summary

| Step | Result |
|---|---|
| 0 Environment | At the start, the only SDK was 11.0.100. The user then installed SDK 10.0.404. |
| 1 Branch | HEAD is 2d13546. |
| 2 Tests | With SDK 11 only: 3 failed (MSBuild not found). With SDK 10.0.404: 425 passed, 0 failed, 3 skipped (Windows or Node causes). |
| 3 Index | With SDK 11 only: every run crashed. With SDK 10: 3,207 nodes, 19,577 edges, 13.8 s to 14.7 s for a full index, 20.3 MB to 22.3 MB. |
| 4 Swap under readers | 3 of 3 indexes finished. No lock error. The bench answered all calls, and its timings stayed the same. |
| 5 Install | pwsh and 5.1 both exit 0. All 6 configs are correct. The index of the fresh clone shows 1,168 compile errors (no NuGet restore). |
| 6 Live daemon | The new graph was ready 4.6 s after the save. find_symbol: 105 ms for the first call, 4 ms for the second. |
| 7 Uninstall | pwsh and 5.1: all 6 entries are gone, and the other content stays. 5.1 changes the JSON layout. |
| 8 Resilience | The script stops at R3 because Windows has no SIGKILL. With a SIGTERM shim: 11 of 12 pass. R3 fails because of how the script reads the STALE warning, not because a graph broke. |

## Findings for the author

1. **The indexer crashes if the machine has SDK 11 but no SDK 10.** The indexer targets net10.0 and runs on the .NET 10 runtime. On that runtime, `MSBuildLocator.RegisterDefaults()` does not accept SDK 11, so it throws "No instances of MSBuild could be detected" (`Program.cs:231`). The process ends with exit -532462766 (0xE0434352, an unhandled CLR exception). There is no clear message. `DOTNET_ROLL_FORWARD=LatestMajor` is a workaround: the indexer then runs on runtime 11 and finds SDK 11. After the user installed SDK 10.0.404, the problem stopped. `global.json` has `"rollForward": "latestMajor"`, but that applies to the SDK that `dotnet` uses. It does not apply to the runtime that the indexer selects.
2. **A fresh clone gets an incomplete index because nobody restores its NuGet packages.** `install.ps1 ... target\codegraph\CodeGraph.slnx` indexed a new `git clone` and reported `1168 compile error(s) in declarations, first: GraphAnchor.cs(1): CS0246 The type or namespace name 'DuckDB' could not be found`. The result was 2,951 nodes and 17,155 edges. The checkout that has a build gives 3,207 nodes and 19,577 edges. Install and init do not restore packages, and they do not tell the user to restore. The incremental runs after that report `0 compile error(s)` because they walk no project, so they hide the problem.
3. **Node counts are different from Linux.** On Windows the checkout gives 3,207 nodes and 19,577 edges. Linux gives 3,170 and 19,297. I did not find the cause.
4. **graph.db grows after a no-op index.** In both sets of runs, the file grew by about 2 MB after the incremental run that walked 0 files. The sizes are in step 3.
5. **resilience.py stops on Windows at R3.** `signal.SIGKILL` does not exist on Windows, so `p.send_signal(signal.SIGKILL)` raises `AttributeError`, and the scenarios after R3 do not run. R6 has the same problem (`os.kill(pid, signal.SIGKILL)`). On Windows, `signal.SIGTERM` gives the same hard kill (`TerminateProcess`).
6. **R3 fails on Windows because of the script, not the graph.** After each kill, the graph was whole (2,951 nodes, no error). `nodes()` does not remove the "⚠ STALE INDEX" warning that comes before the count after a killed incremental run. Step 8 gives the details.
7. **The 5.1 uninstall changes the layout of each config file.** It keeps the content, but it uses the wide `ConvertTo-Json` indentation of Windows PowerShell 5.1. Step 7 gives the details.
8. **R8 `--root` edge cases fall back to the graph of the working folder.** For a missing folder, a file, an unexpanded placeholder and an empty value, the server answered with `3207 nodes, 19577 edges`. That is the graph of `Z:\CodeGraph`, where the battery ran, and not a graph under the test copy. The scenario passes. Make sure that this fallback is the behavior you want.

## 0. Environment

| Item | Value |
|---|---|
| `claude --version` | 2.1.325 (Claude Code) |
| `dotnet --list-sdks` at the start | 11.0.100 only |
| `dotnet --list-sdks` after the user installed SDK 10 | 10.0.404, 11.0.100 |
| `dotnet --list-runtimes` (NETCore.App) | 8.0.34, 10.0.15, 11.0.0 |
| `git --version` | git version 2.56.0.windows.4 |
| pwsh `$PSVersionTable.PSVersion` | 7.7.0 |
| `powershell.exe -NoProfile -c '$PSVersionTable.PSVersion'` | 5.1.26100.9818 |
| Python 3 | Yes: `C:\Python314\python.exe`, Python 3.14.7. `py` is also installed. `python3` is the WindowsApps alias. |
| Node.js | v24.20.0 (from the install log) |
| codex | Not installed |
| copilot | Not installed |
| cursor-agent | Installed: `C:\Users\seuna\AppData\Local\cursor-agent\cursor-agent.ps1` |

## 1. Branch

`git fetch origin; git checkout any-mcp-client; git pull` gave "Already on 'any-mcp-client'" and "Already up to date." `git rev-parse --short HEAD` gave `2d13546`. The working tree was clean.

## 2. Test suite: `dotnet test tests/CodeGraph.Tests`

### 2a. SDK 11.0.100 only (the machine at the start)

Exit 2, duration 1m 28s. Total 428: 422 passed, 3 failed, 3 skipped.

Failures:

```
failed CodeGraph.Tests.FirstIndexTests.A_damaged_graph_is_rebuilt_rather_than_crashing_the_indexer(full: True) (425ms)
  Assert.Equal() Failure: Values differ
  Expected: 0
  Actual:   -532462766
    at CodeGraph.Tests.FirstIndexTests.A_damaged_graph_is_rebuilt_rather_than_crashing_the_indexer(Boolean full) in Z:\CodeGraph\codegraph\tests\CodeGraph.Tests\RepoSetupTests.cs:316

failed CodeGraph.Tests.FirstIndexTests.A_damaged_graph_is_rebuilt_rather_than_crashing_the_indexer(full: False) (273ms)
  Assert.Equal() Failure: Values differ
  Expected: 0
  Actual:   -532462766
    at CodeGraph.Tests.FirstIndexTests.A_damaged_graph_is_rebuilt_rather_than_crashing_the_indexer(Boolean full) in Z:\CodeGraph\codegraph\tests\CodeGraph.Tests\RepoSetupTests.cs:316

failed CodeGraph.Tests.FirstIndexTests.The_first_index_of_a_repo_creates_the_graph_folder (281ms)
  Unhandled exception. System.InvalidOperationException: No instances of MSBuild could be detected.
  Try calling RegisterInstance or RegisterMSBuildPath to manually register one.
     at Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults()
     at Program.<Main>$(String[] args) in Z:\CodeGraph\codegraph\src\CodeGraph.Indexer\Program.cs:line 231
     at Program.<Main>(String[] args)
    at CodeGraph.Tests.FirstIndexTests.The_first_index_of_a_repo_creates_the_graph_folder() in Z:\CodeGraph\codegraph\tests\CodeGraph.Tests\RepoSetupTests.cs:306
```

The same run with `$env:DOTNET_ROLL_FORWARD='LatestMajor'`: exit 0, 1m 50s. Total 428: 425 passed, 0 failed, 3 skipped.

### 2b. SDK 10.0.404 installed, no environment variable

Exit 0, duration 1m 35s. Total 428: 425 passed, 0 failed, 3 skipped.

Skipped tests (the same in every run):

| Test | Reason that the test gave |
|---|---|
| `ScipRunnerTests.A_go_module_runs_its_scip_indexer_in_the_module_folder_and_imports_the_index` | the stand-in indexer is a shell script |
| `TypeScriptLiveTests.References_rename_and_a_what_if_edit_come_from_the_language_server` | the extractor's npm packages are not installed |
| `TypeScriptIndexTests.An_Angular_workspace_answers_impact_tests_and_dead_code` | Node.js or the extractor's npm packages are not installed |

Note: these test runs came before step 5. In step 5, `install.ps1` installed the extractor's npm packages ("added 4 packages"). I did not run the tests again after that.

## 3. Release build and index of this repo

`dotnet build CodeGraph.slnx -c Release`: Build succeeded, 0 warnings, 0 errors.

### 3a. SDK 11.0.100 only

All 3 runs (`--full`, `--full`, no flag) ended in about 1 s with exit -532462766 and the same MSBuildLocator exception as in 2a. No graph.db was made.

### 3b. SDK 10.0.404, no environment variable

| Run | Exit | Wall time | Last line | graph.db after |
|---|---|---|---|---|
| `--full` | 0 | 18.2 s | `indexed 3207 nodes, 19577 edges (full) in 14.7s → Z:\CodeGraph\.codegraph\graph.db` | 21,508,096 bytes |
| `--full` | 0 | 17.0 s | `indexed 3207 nodes, 19577 edges (full) in 13.8s → Z:\CodeGraph\.codegraph\graph.db` | 21,245,952 bytes |
| no flag | 0 | 3.3 s | `indexed 3207 nodes, 19577 edges (incremental, 0 file(s) walked) in 0.2s → Z:\CodeGraph\.codegraph\graph.db` | 23,343,104 bytes |

The compiler line on the full runs was `compiler: Roslyn 5.9.0 (up to C# 14); projects use C# 14; 0 compile error(s) in declarations`. The `.codegraph` folder held only graph.db after each run.

Before SDK 10 was installed, I also ran the same 3 runs with `DOTNET_ROLL_FORWARD=LatestMajor`. They gave the same counts: full 13.6 s (20,983,808 bytes), full 16.0 s (20,983,808 bytes), and no-op 0.3 s (23,080,960 bytes).

## 4. Graph swap while servers read it

Command change: I added `--no-build` to the bench command. Without it, each `dotnet run` builds again, and that build can copy files into the `bin` folders that the indexer and the server use. That gives a lock problem that the product does not cause. I built the bench one time before (it is in `CodeGraph.slnx`). The command was:

```
dotnet run --no-build -c Release --project tools/CodeGraph.Bench -- ..\.codegraph\graph.db --server src\CodeGraph.Mcp\bin\Release\net10.0\codegraph-mcp.dll
```

The bench loop ran for 130 s in one process. A second process ran 3 `codegraph-index.exe CodeGraph.slnx --full` back to back, starting 8 s after the bench.

| Index | Start | Exit | Wall time | Last line |
|---|---|---|---|---|
| 1 | 22:07:48.192 | 0 | 16.4 s | `indexed 3207 nodes, 19577 edges (full) in 13.5s` |
| 2 | 22:08:04.630 | 0 | 16.6 s | `indexed 3207 nodes, 19577 edges (full) in 13.5s` |
| 3 | 22:08:21.212 | 0 | 16.7 s | `indexed 3207 nodes, 19577 edges (full) in 13.8s` |

- Every index finished.
- I searched the indexer output and the bench output for "denied", "another process", "IOException", "exception" and "error". There were no matches, apart from "0 compile error(s)".
- The bench answered all calls. Bench iteration 1 ran from 22:07:40.472 to 22:08:47.434 (exit 0). It covers all 3 indexes (22:07:48 to 22:08:38). Iteration 2 ran from 22:08:47.449 to 22:09:54.296 (exit 0), after the indexes.

Bench timings:

| Tool call | Baseline (no index running) | Iteration 1 (during the 3 indexes) | Iteration 2 (after) |
|---|---|---|---|
| first call after start (find_symbol) | 72 ms | 72 ms | 77 ms |
| graph_stats, back to back | 0.56 ms | 0.55 ms | 0.53 ms |
| graph_stats, 2 s apart | 1.2 ms | 1.1 ms | 1.1 ms |
| find_symbol 'Dispose', back to back | 1.2 ms | 1.1 ms | 1.0 ms |
| find_symbol 'Dispose', 2 s apart | 2.3 ms | 2.7 ms | 2.8 ms |
| callers_of depth 1 (most-called), back to back | 1.1 ms | 1.1 ms | 1.2 ms |
| callers_of depth 1 (most-called), 2 s apart | 1.7 ms | 2.1 ms | 1.9 ms |
| impact_of depth 5, back to back | 0.92 ms | 1.1 ms | 0.88 ms |
| impact_of depth 5, 2 s apart | 2.4 ms | 2.7 ms | 2.3 ms |
| impact_of depth 20, back to back | 1.1 ms | 1.2 ms | 1.00 ms |
| impact_of depth 20, 2 s apart | 1.9 ms | 2.1 ms | 1.7 ms |
| covered_by depth 30, back to back | 1.3 ms | 1.8 ms | 1.2 ms |
| covered_by depth 30, 2 s apart | 2.0 ms | 2.1 ms | 2.0 ms |

One bench run takes about 67 s of wall time. The bench output does not show if the server loaded the new graph during iteration 1. R4 in step 8 tests that part.

## 5. Install from source into a scratch folder

`git clone Z:\CodeGraph $env:TEMP\cg-win\target`: HEAD 2d13546, branch any-mcp-client.

### pwsh 7.7.0

`.\install.ps1 -FromSource -SkipTests -InstallDir $env:TEMP\cg-win\bin -Client copilot,cursor,roo $env:TEMP\cg-win\target\codegraph\CodeGraph.slnx`: exit 0, 54.8 s.

```
==> Checking the executables start
ok: codegraph 1.1.0
codegraph init: C:\Users\seuna\AppData\Local\Temp\cg-win\target\codegraph\CodeGraph.slnx
  .mcp.json      created with the codegraph server for Claude Code, Copilot CLI (...\target\.mcp.json)
  .vscode/mcp.json created with the codegraph server for VS Code (...\target\.vscode\mcp.json)
  .vs/mcp.json   created with the codegraph server for Visual Studio (...\target\codegraph\.vs\mcp.json)
  .github/mcp.json created with the codegraph server for Copilot for JetBrains (...\target\.github\mcp.json)
  .cursor/mcp.json created with the codegraph server for Cursor (...\target\.cursor\mcp.json)
  .roo/mcp.json  created with the codegraph server for Roo Code (...\target\.roo\mcp.json)
  .gitignore     already ignores .codegraph/
compiler: Roslyn 5.9.0 (up to C# 14); projects use C# 14; 1168 compile error(s) in declarations, first: GraphAnchor.cs(1): CS0246 The type or namespace name 'DuckDB' could not be found (are you missing a using directive or an assembly reference?)
indexed 2951 nodes, 17155 edges (full) in 10.2s → C:\Users\seuna\AppData\Local\Temp\cg-win\target\.codegraph\graph.db
==> Installed
  C:\Users\seuna\AppData\Local\Temp\cg-win\bin\codegraph-index.exe
  C:\Users\seuna\AppData\Local\Temp\cg-win\bin\codegraph-mcp.exe
```

### Windows PowerShell 5.1

`powershell.exe -NoProfile -File .\install.ps1` with the same arguments: exit 0, 9.6 s. It built, published to the same folder, and reported each config as "codegraph entry already up to date". All 6 files were byte-for-byte the same as after the pwsh run (I compared SHA-256 hashes). Its incremental index line was `indexed 2951 nodes, 17155 edges (incremental, 0 file(s) walked) in 0.2s`.

### Config entries

| File | codegraph entry |
|---|---|
| `target\.mcp.json` | `"mcpServers": { "codegraph": { "command": "C:/Users/seuna/AppData/Local/Temp/cg-win/bin/codegraph-mcp.exe" } }` |
| `target\.vscode\mcp.json` | `"servers": { "codegraph": { "type": "stdio", "command": "C:/Users/seuna/AppData/Local/Temp/cg-win/bin/codegraph-mcp.exe", "args": ["--root", "${workspaceFolder}"] } }` |
| `target\codegraph\.vs\mcp.json` | `"servers": { "codegraph": { "type": "stdio", "command": "C:/Users/seuna/AppData/Local/Temp/cg-win/bin/codegraph-mcp.exe", "args": ["--root", "C:/Users/seuna/AppData/Local/Temp/cg-win/target"] } }` |
| `target\.github\mcp.json` | `"servers": { "codegraph": { "type": "stdio", "command": "C:/Users/seuna/AppData/Local/Temp/cg-win/bin/codegraph-mcp.exe", "args": ["--root", "C:/Users/seuna/AppData/Local/Temp/cg-win/target"] } }` |
| `target\.cursor\mcp.json` | `"mcpServers": { "codegraph": { "command": "C:/Users/seuna/AppData/Local/Temp/cg-win/bin/codegraph-mcp.exe", "args": ["--root", "${workspaceFolder}"] } }` |
| `target\.roo\mcp.json` | `"mcpServers": { "codegraph": { "command": "C:/Users/seuna/AppData/Local/Temp/cg-win/bin/codegraph-mcp.exe" } }` |

The results match the expected values. VS Code and Cursor get `--root ${workspaceFolder}`. `.vs` (beside the .slnx) and `.github` get the absolute repo root. All paths use the exe in `$env:TEMP\cg-win\bin`, with forward slashes. Roo gets no `--root`. `git status` in target shows `.cursor/`, `.github/mcp.json`, `.mcp.json` and `.roo/` as untracked. `.vscode` and `.vs` do not show, because .gitignore ignores them.

### stdio call from target

I started `codegraph-mcp.exe` with no arguments and the working folder set to target, as `.mcp.json` does. Then I sent `initialize`, `notifications/initialized` and `tools/call graph_stats`:

- initialize: 426 ms, serverInfo `codegraph` 1.1.0
- graph_stats: 57.6 ms, `2951 nodes, 17155 edges`
- The server stopped with exit 0 when stdin closed.

## 6. Live daemon

From target: `$env:TEMP\cg-win\bin\codegraph-index.exe codegraph\CodeGraph.slnx --watch`. I added a timestamp to each line.

```
22:12:04.070 live: answering on pipe codegraph-20866a29b6563eb036de
22:12:06.645 incremental: 0 changed, 0 deleted, 0 dependent → walking 0 of 104 files
22:12:06.800 indexed 2951 nodes, 17155 edges (incremental, 0 file(s) walked) in 0.2s → ...\target\.codegraph\graph.db
22:12:06.801 watching C:\Users\seuna\AppData\Local\Temp\cg-win\target (Ctrl+C to stop)
```

At 22:12:20.578 I added `    internal static int WinProbe() => 1;` before the last `}` of `codegraph\src\CodeGraph.Core\Worktree.cs`, with the same line endings as the file, and saved it. Log for that pass:

```
22:12:21.653 incremental: 1 changed, 0 deleted, 9 dependent → walking 10 of 104 files
22:12:21.653 [1/6] project: CodeGraph.Core
22:12:23.413 [2/6] project: CodeGraph.Indexer
22:12:24.393 [3/6] project: CodeGraph.Mcp
22:12:24.752 [4/6] project: CodeGraph.Web
22:12:24.752 [5/6] project: CodeGraph.Tests
22:12:24.800 [6/6] project: CodeGraph.Bench
22:12:24.801 compiler: Roslyn 5.9.0 (up to C# 14); projects use C# 14; 26 compile error(s) in declarations, first: Program.cs(7): CS0234 The type or namespace name 'Build' does not exist in the namespace 'Microsoft' (are you missing an assembly reference?)
22:12:25.221 indexed 2952 nodes, 17156 edges (incremental, 10 file(s) walked) in 4.1s → ...\target\.codegraph\graph.db
```

- The pass started 1.1 s after the save.
- The new graph was ready 4.6 s after the save.
- The 26 compile errors come from the missing NuGet restore (finding 2).

find_symbol through a new server (no arguments, working folder target), with `{"query":"WinProbe"}`:

- first call: 105.4 ms; second call: 4.0 ms
- answer: `method  CodeGraph.Core.Worktree.WinProbe()  internal static int WinProbe()  ...\target\codegraph\src\CodeGraph.Core\Worktree.cs:200-200  [M:CodeGraph.Core.Worktree.WinProbe]`
- Note: the argument is `query`, not `name`. A first try with `{"name":"WinProbe"}` gave a clear error that lists the parameters of find_symbol.

I stopped the daemon (I stopped its shell task). After that, no `codegraph-index` or `codegraph-mcp` process was left. I reverted the file with `git checkout -- codegraph/src/CodeGraph.Core/Worktree.cs`.

## 7. Uninstall

`Get-Help .\uninstall.ps1` shows `-InstallDir`, `-Repo` (repeatable), `-Force` and `-Yes`. Before the run, I added a second server `"other": { "command": "other-server.exe", "args": ["--keep", "me"] }` and a top-level key `"note": "kept by uninstall?"` to each of the 6 config files. With these, I can see if the uninstall keeps other content.

### pwsh 7.7.0

`.\uninstall.ps1 -InstallDir $env:TEMP\cg-win\bin -Repo $env:TEMP\cg-win\target -Yes`: no error ($? True). The output:

```
==> Install folder: C:\Users\seuna\AppData\Local\Temp\cg-win\bin
deleted
==> Repo: C:\Users\seuna\AppData\Local\Temp\cg-win\target
entry removed   (×6)
deleted
Done.
```

- The codegraph entry is gone from all 6 files (`.mcp.json`, `.vscode\mcp.json`, `codegraph\.vs\mcp.json`, `.github\mcp.json`, `.cursor\mcp.json`, `.roo\mcp.json`).
- In all 6 files, the `other` server and the `note` key stayed with no change.
- The install folder and `target\.codegraph` are gone.
- The user PATH did not change. I compared the value before and after the run.

### Windows PowerShell 5.1

The pwsh run left nothing. Step 8 needed the executables again, so I ran the pwsh install again. It reported "updated with the codegraph server" for each file and merged the entry in beside `other`. After step 8, I ran `powershell.exe -NoProfile -File .\uninstall.ps1 -InstallDir $env:TEMP\cg-win\bin -Repo $env:TEMP\cg-win\target -Yes`. No codegraph process was running at that time.

- Exit 0. The output is the same as in pwsh: "deleted", 6 × "entry removed", "deleted", "Done."
- All 6 codegraph entries are gone. The `other` server and the `note` key stayed in all 6 files.
- The install folder and `target\.codegraph` are gone. The user PATH did not change.
- **Cosmetic difference:** 5.1 `ConvertTo-Json` writes each file again with its own wide indentation (4 spaces, values aligned after `":  "`). The JSON is the same, but a user sees a diff for each line. pwsh 7 writes 2-space indentation, which is the same as the init output. Example from `.mcp.json` after the 5.1 run:

```
{
    "mcpServers":  {
                       "other":  {
                                     "command":  "other-server.exe",
```

## 8. Resilience battery

Template: `$env:TEMP\cg-win\template` is a copy of target (with its `.codegraph`) that I made after step 6, before the uninstall. Its packages are not restored, so its graph has 2,951 nodes (finding 2).

### Run 1: the script with no change

`python tools\CodeGraph.Bench\resilience.py $env:TEMP\cg-win\res $env:TEMP\cg-win\template $env:TEMP\cg-win\bin\codegraph-mcp.exe $env:TEMP\cg-win\bin\codegraph-index.exe`: exit 1 after 33.1 s.

```
PASS  R1 no graph, then built while the server runs
PASS  R2a corrupt graph: server answers, stays up
PASS  R2b corrupt graph: indexer recovers
Traceback (most recent call last):
  File "Z:\CodeGraph\codegraph\tools\CodeGraph.Bench\resilience.py", line 84, in <module>
    p.send_signal(signal.SIGKILL)
                  ^^^^^^^^^^^^^^
AttributeError: module 'signal' has no attribute 'SIGKILL'. Did you mean: 'SIGILL'?
```

The scenarios that use SIGKILL are R3 (`resilience.py:84`, `p.send_signal(signal.SIGKILL)`) and R6 (`resilience.py:161`, `os.kill(pid, signal.SIGKILL)`). The script does not catch the error, so R3 to R10 did not run.

### Run 2 and run 3: the same script through a SIGKILL shim

I did not change the script. A wrapper in my scratchpad sets `signal.SIGKILL = signal.SIGTERM`, then runs `resilience.py` with `runpy.run_path`. On Windows, `Popen.send_signal(SIGTERM)` and `os.kill(pid, SIGTERM)` both call `TerminateProcess`. That is a hard kill, the same as `kill -9`.

Run 2 had an error in my setup. I copied the template after the step-6 revert of `Worktree.cs`, but I did not re-index it. Thus its graph still had WinProbe (2,952 nodes) and was stale. R3 took that count as its baseline, and the complete indexes gave 2,951, so R3 failed. Before run 3, I ran `codegraph-index.exe template\codegraph\CodeGraph.slnx --full` (exit 0). Run 3 is the result to use. The other scenarios had the same result in run 2 and in run 3.

Run 3: exit 0 after 372.3 s, `11 of 12 passed`.

| Scenario | Result | Detail |
|---|---|---|
| R1 no graph, then built while the server runs | PASS | before: "no code graph at …" (error=True); after build: 2951 nodes, 17155 edges |
| R2a corrupt graph: server answers, stays up | PASS | alive=True; 3 answers, each "… failed: the graph file …" |
| R2b corrupt graph: indexer recovers | PASS | incremental exit 0; then graph_stats 2951 nodes, 17155 edges |
| R3 kill -9 mid-index: graph stays readable and whole | FAIL (see below) | full kills at 2 s, 9 s and 13 s: 2951 nodes. Full at 14.5 s and 15.5 s finished first: 2951. Incremental kills at 1.5 s, 2.3 s and 2.6 s: a STALE warning, then 2951 nodes. Next index exit 0, 2951 nodes. |
| R4 8 readers during 3 full + 3 incremental writes | PASS | 424,812 calls, 0 errors; writer exits [0, 0, 0, 0, 0, 0]; latency p50 2.8 ms, p99 6.9 ms, max 608 ms; new method visible after: True |
| R5 malformed requests, then a normal call | PASS | 12 of 12 probes answered, 1 ms to 95 ms |
| R5b every error says what was wrong | PASS | no opaque "An error occurred" answers; graph intact after the SQL-looking query |
| R6 live daemon killed under the server | PASS (through the shim) | daemon pid 22244 killed; graph tools still answered 2952 nodes; `live_status start=true` started a new daemon (pid 24276) |
| R7 stdin closed: the server exits | PASS | exit 0 after 0.0 s |
| R8 --root edge cases | PASS | missing folder, file, unexpanded placeholder, empty: each answered `3207 nodes, 19577 edges` (finding 8) |
| R9 edited file without a daemon | PASS | answer starts with "⚠ STALE INDEX" |
| R10 init with broken config files | PASS | non-object .mcp.json: exit 1, file untouched; JSONC with comments: exit 0, servers [codegraph, other]; read-only .cursor/mcp.json: exit 1, "Access to the path …" |

**Why R3 fails.** All 8 graphs were readable after a kill, and none gave an error. The 3 incremental runs were still busy when the kill came at 1.5 s, 2.3 s and 2.6 s, so `Worktree.cs` stayed newer than the graph. The server then correctly starts its graph_stats answer with "⚠ STALE INDEX: 1 indexed file(s) edited since (Worktree.cs) …" and puts "2951 nodes" on the next line. `nodes()` in `resilience.py:37` takes all the text before " nodes", so it returns the warning plus "2951". That is not equal to the baseline "2951", so the check fails. The node count itself was correct each time. On Windows, an incremental pass on this repo takes longer than 2.6 s: the step-6 daemon needed 4.1 s for 10 files. Thus no incremental run finished before its kill. On Linux, they possibly finish first. I did not test that.

## Logs

All raw logs are in `C:\Users\seuna\AppData\Local\Temp\cg-win`: `step2-test*.log`, `step4-*.log`, `step5-*.log`, `step6-*.log`, `step7-*.log` and `step8-*.log`.
