# CodeGraph Windows re-test 4: branch any-mcp-client at ef202fe

Date: 2026-11-15. Machine: Windows 11 Pro 10.0.26300, x64. SDKs: 10.0.404 and 11.0.100. pwsh 7.7.0, Windows PowerShell 5.1.26100.9818.

This re-test checks 2 fixes: unknown options in `install.ps1`, and the spinner in the `--log` file. All logs, clones and install folders are in `C:\Users\seuna\AppData\Local\Temp\cg-win4` (`$env:TEMP\cg-win4`). I did not change product code. I did not delete anything outside `$env:TEMP\cg-win4`. Each `install.ps1` command had an `-InstallDir` under `$env:TEMP\cg-win4`. `Test-Path C:\tools` gave `False` before and after the test.

## Summary

| Step | Result |
|---|---|
| 1 Branch | HEAD is ef202fe. |
| 1 Tests | Exit 0. Total 441: 440 passed, 0 failed, 1 skipped (the shell-script SCIP test). |
| 2 Unknown options | Pass. All 6 runs (4 from the plan, and 2 more with `pwsh -File` to get an exit code) stop with `unknown option: …`. No download. No `-InstallDir` folder. No `C:\tools`. |
| 3 Spinner in `--log` | Pass. 47 lines, 0 spinner frames, 0 bare carriage returns. Each of the 47 lines ends with CRLF, so each line contains a CR. Re-test 3 had 448 bare CRs and 322 spinner lines in 513. |

## Findings for the author

1. **Each line of the `--log` file ends with CRLF.** The plan expects 0 lines that contain a CR. In this log, all 47 lines contain one, but each CR is part of a CRLF line end (`Environment.NewLine` on Windows). There is no CR without an LF after it. The re-test 3 log had 65 CRLF and 448 bare CRs. Thus on Windows, a count of CR characters does not show the spinner. A count of bare CRs (a CR with no LF after it) does.

## 1. Branch and tests

Commands, in `Z:\CodeGraph`:

| Command | Exit | Output |
|---|---|---|
| `git fetch origin` | 0 | `af1eab5..ef202fe  any-mcp-client -> origin/any-mcp-client` |
| `git checkout any-mcp-client` | 0 | `Already on 'any-mcp-client'`, behind by 1 commit |
| `git pull` | 0 | `Updating af1eab5..ef202fe`, fast-forward, 5 files changed |
| `git rev-parse --short HEAD` | 0 | `ef202fe` |

`dotnet test tests/CodeGraph.Tests` (in `codegraph`): exit 0. Log: `step1-test.log`.

```
  total: 441
  failed: 0
  succeeded: 440
  skipped: 1
  duration: 1m 31s 439ms
```

- Failed: none.
- Skipped: `CodeGraph.Tests.ScipRunnerTests.A_go_module_runs_its_scip_indexer_in_the_module_folder_and_imports_the_index` ("the stand-in indexer is a shell script").

## 2. Unknown options in `install.ps1`

All runs were in `Z:\CodeGraph\codegraph`. In pwsh 7, I ran the script in the session, as `.\install.ps1 …`. It has no exit code there, so I also ran it with `pwsh -NoProfile -File` to get one.

| Shell | Arguments | Exit | Error | `Test-Path` on `-InstallDir` |
|---|---|---|---|---|
| pwsh 7, in the session | `-ObsidianVault x -InstallDir …\never-ps7` | (terminating error) | `unknown option: -ObsidianVault. …` | False |
| pwsh 7, in the session | `-Bogus -InstallDir …\never2-ps7` | (terminating error) | `unknown option: -Bogus. …` | False |
| `powershell.exe -NoProfile -File` (5.1) | `-ObsidianVault x -InstallDir …\never-ps51` | 1 | `unknown option: -ObsidianVault. …` | False |
| `powershell.exe -NoProfile -File` (5.1) | `-Bogus -InstallDir …\never2-ps51` | 1 | `unknown option: -Bogus. …` | False |
| `pwsh -NoProfile -File` | `-ObsidianVault x -InstallDir …\never-ps7file` | 1 | `unknown option: -ObsidianVault. …` | False |
| `pwsh -NoProfile -File` | `-Bogus -InstallDir …\never2-ps7file` | 1 | `unknown option: -Bogus. …` | False |

None of the runs printed `==> Downloading`. After the 6 runs, `$env:TEMP\cg-win4` held only `step1-test.log`, and `Test-Path C:\tools` gave `False`.

pwsh 7, `-ObsidianVault x`:

```
Exception: Z:\CodeGraph\codegraph\install.ps1:69
Line |
  69 |      throw "unknown option: $($unknown -join ', '). See Get-Help $PSCo …
     |      ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
     | unknown option: -ObsidianVault. See Get-Help Z:\CodeGraph\codegraph\install.ps1 -Detailed
```

pwsh 7, `-Bogus`:

```
Exception: Z:\CodeGraph\codegraph\install.ps1:69
Line |
  69 |      throw "unknown option: $($unknown -join ', '). See Get-Help $PSCo …
     |      ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
     | unknown option: -Bogus. See Get-Help Z:\CodeGraph\codegraph\install.ps1 -Detailed
```

5.1, `-ObsidianVault x` (exit 1):

```
unknown option: -ObsidianVault. See Get-Help Z:\CodeGraph\codegraph\install.ps1 -Detailed
At Z:\CodeGraph\codegraph\install.ps1:69 char:5
+     throw "unknown option: $($unknown -join ', '). See Get-Help $PSCo ...
+     ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
    + CategoryInfo          : OperationStopped: (unknown option:...l.ps1 -Detailed:String) [], RuntimeException
    + FullyQualifiedErrorId : unknown option: -ObsidianVault. See Get-Help Z:\CodeGraph\codegraph\install.ps1 -Detaile
   d
```

5.1, `-Bogus` (exit 1):

```
unknown option: -Bogus. See Get-Help Z:\CodeGraph\codegraph\install.ps1 -Detailed
At Z:\CodeGraph\codegraph\install.ps1:69 char:5
+     throw "unknown option: $($unknown -join ', '). See Get-Help $PSCo ...
+     ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
    + CategoryInfo          : OperationStopped: (unknown option:...l.ps1 -Detailed:String) [], RuntimeException
    + FullyQualifiedErrorId : unknown option: -Bogus. See Get-Help Z:\CodeGraph\codegraph\install.ps1 -Detailed
```

The `pwsh -File` runs (exit 1) printed the same text as the pwsh 7 runs in the session.

## 3. The spinner in `--log`

| Command | Exit | Result |
|---|---|---|
| `.\install.ps1 -FromSource -SkipTests -InstallDir $env:TEMP\cg-win4\bin` (pwsh 7) | 0 | `codegraph-index.exe` and `codegraph-mcp.exe` in `bin` |
| `git clone Z:\CodeGraph $env:TEMP\cg-win4\repo` | 0 | HEAD ef202fe |
| `dotnet restore repo\codegraph\CodeGraph.slnx` | 0 | 6 projects restored |
| `bin\codegraph-index.exe repo\codegraph\CodeGraph.slnx --full` | 0 | 3,245 nodes, 19,848 edges in 11.3 s |

The plan does not have the `--full` index. I added it so that the clone has a graph before the daemon starts, as in re-test 3.

The daemon: `Start-Process bin\codegraph-index.exe -ArgumentList 'repo\codegraph\CodeGraph.slnx','--watch','--log',"$env:TEMP\cg-win4\daemon.log"`, with full paths. It ran in its own console window, with no redirection. In re-test 3, I also used `-WindowStyle Hidden`. This time I did not.

A script did these steps:

1. It waited for the `watching` line in the log.
2. For each save, it added `public static void SpinnerProbe<n>() { }` before the last `}` of `codegraph\src\CodeGraph.Core\Worktree.cs`. Then it waited for one more `indexed` line in the log.
3. It restored the file with `git checkout`, and waited for one more `indexed` line.
4. It stopped the daemon with `Stop-Process`.

| Event | Time to the `indexed` line | `indexed` line |
|---|---|---|
| Start | 3.2 s to the `watching` line | `indexed 3245 nodes, 19848 edges (incremental, 0 file(s) walked) in 0.2s` |
| Save 1 | 4.8 s | `indexed 3246 nodes, 19849 edges (incremental, 10 file(s) walked) in 4.2s` |
| Save 2 | 2.7 s | `indexed 3247 nodes, 19850 edges (incremental, 10 file(s) walked) in 2.1s` |
| Save 3 | 2.7 s | `indexed 3248 nodes, 19851 edges (incremental, 10 file(s) walked) in 2.1s` |
| Restore | 2.3 s | `indexed 3245 nodes, 19848 edges (incremental, 10 file(s) walked) in 1.8s` |

After the restore, `git status --short` was empty. The daemon exited after `Stop-Process`.

### Counts

| Log | Lines | Lines that contain a CR | CRLF line ends | Bare CRs | Lines that start with a spinner frame | ESC characters |
|---|---|---|---|---|---|---|
| `cg-win4\daemon.log` (this run) | 47 | 47 | 47 | 0 | 0 | 0 |
| `cg-win4\daemon-try1.log` (first try, see below) | 11 | 11 | 11 | 0 | 0 | 0 |
| `cg-win3\step4-daemon.log` (re-test 3) | 513 | — | 65 | 448 | 322 | — |

"Bare CR" is a CR with no LF after it. "Spinner frame" is a line that starts with `-`, `\`, `|` or `/` and a space.

### First try

My first script read the log with `[IO.File]::ReadAllText` while the daemon ran. That call failed: `The process cannot access the file 'C:\Users\seuna\AppData\Local\Temp\cg-win4\daemon.log' because it is being used by another process.` The cause was my script, not the product: the daemon holds the log open for write, and `ReadAllText` does not share write access. I stopped that daemon (pid 10364) before any save, and renamed its log to `daemon-try1.log`. Then I changed the script to open the log with `FileShare.ReadWrite`, and ran it again. The table above uses the second run.

### `daemon.log`, full text

```
live: answering on pipe codegraph-d314bbcca2c1a904dcaa
incremental: 0 changed, 0 deleted, 0 dependent → walking 0 of 111 files
[1/6] project: CodeGraph.Core
[2/6] project: CodeGraph.Indexer
[3/6] project: CodeGraph.Mcp
[4/6] project: CodeGraph.Web
[5/6] project: CodeGraph.Tests
[6/6] project: CodeGraph.Bench
compiler: Roslyn 5.9.0 (up to C# 14); no project walked; 0 compile error(s) in declarations
indexed 3245 nodes, 19848 edges (incremental, 0 file(s) walked) in 0.2s → C:\Users\seuna\AppData\Local\Temp\cg-win4\repo\.codegraph\graph.db
watching C:\Users\seuna\AppData\Local\Temp\cg-win4\repo (Ctrl+C to stop)
incremental: 1 changed, 0 deleted, 9 dependent → walking 10 of 111 files
[1/6] project: CodeGraph.Core
[2/6] project: CodeGraph.Indexer
[3/6] project: CodeGraph.Mcp
[4/6] project: CodeGraph.Web
[5/6] project: CodeGraph.Tests
[6/6] project: CodeGraph.Bench
compiler: Roslyn 5.9.0 (up to C# 14); projects use C# 14; 0 compile error(s) in declarations
indexed 3246 nodes, 19849 edges (incremental, 10 file(s) walked) in 4.2s → C:\Users\seuna\AppData\Local\Temp\cg-win4\repo\.codegraph\graph.db
incremental: 1 changed, 0 deleted, 9 dependent → walking 10 of 111 files
[1/6] project: CodeGraph.Core
[2/6] project: CodeGraph.Indexer
[3/6] project: CodeGraph.Mcp
[4/6] project: CodeGraph.Web
[5/6] project: CodeGraph.Tests
[6/6] project: CodeGraph.Bench
compiler: Roslyn 5.9.0 (up to C# 14); projects use C# 14; 0 compile error(s) in declarations
indexed 3247 nodes, 19850 edges (incremental, 10 file(s) walked) in 2.1s → C:\Users\seuna\AppData\Local\Temp\cg-win4\repo\.codegraph\graph.db
incremental: 1 changed, 0 deleted, 9 dependent → walking 10 of 111 files
[1/6] project: CodeGraph.Core
[2/6] project: CodeGraph.Indexer
[3/6] project: CodeGraph.Mcp
[4/6] project: CodeGraph.Web
[5/6] project: CodeGraph.Tests
[6/6] project: CodeGraph.Bench
compiler: Roslyn 5.9.0 (up to C# 14); projects use C# 14; 0 compile error(s) in declarations
indexed 3248 nodes, 19851 edges (incremental, 10 file(s) walked) in 2.1s → C:\Users\seuna\AppData\Local\Temp\cg-win4\repo\.codegraph\graph.db
incremental: 1 changed, 0 deleted, 9 dependent → walking 10 of 111 files
[1/6] project: CodeGraph.Core
[2/6] project: CodeGraph.Indexer
[3/6] project: CodeGraph.Mcp
[4/6] project: CodeGraph.Web
[5/6] project: CodeGraph.Tests
[6/6] project: CodeGraph.Bench
compiler: Roslyn 5.9.0 (up to C# 14); projects use C# 14; 0 compile error(s) in declarations
indexed 3245 nodes, 19848 edges (incremental, 10 file(s) walked) in 1.8s → C:\Users\seuna\AppData\Local\Temp\cg-win4\repo\.codegraph\graph.db
```

## Files in `$env:TEMP\cg-win4`

| Path | Content |
|---|---|
| `step1-test.log` | Step 1 |
| `step3-install.log`, `bin` | The install for step 3 |
| `repo`, `step3-restore.log`, `step3-index-full.log` | The clone, its restore and its full index |
| `daemon.log`, `step3-run.log` | The daemon log and the script output |
| `daemon-try1.log` | The log of the first try |
