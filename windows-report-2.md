# CodeGraph Windows re-test: branch any-mcp-client at d3350b3

Date: 2026-11-14. Machine: Windows 11 Pro 10.0.26300, x64. SDKs: 10.0.404 and 11.0.100. pwsh 7.7.0, Windows PowerShell 5.1.26100.9818, Python 3.14.7.

All logs, clones, install folders and copies are in `C:\Users\seuna\AppData\Local\Temp\cg-win2` (`$env:TEMP\cg-win2`). I did not change product code. I did not delete anything outside `$env:TEMP\cg-win2`. The only changes outside it are the `git pull` and the build outputs in the clone `Z:\CodeGraph`.

## Summary

| Step | Result |
|---|---|
| 1 Branch | HEAD is d3350b3. |
| 1 Tests | Exit 2. Total 436: 434 passed, 1 failed, 1 skipped. The failure is a file lock on `daemon.log` in `TypeScriptLiveTests`. It fails 3 of 3 times when it runs alone. |
| 1 Release build | Exit 0, 0 warnings, 0 errors. |
| 2 Restore note | Yes. A `⚠ ... NuGet packages were not restored ... Run dotnet restore and index again` line for each of 4 unrestored projects. |
| 2 Run without `--full` | Yes. The graph meta and `index_status` still show 1,187 compile errors. |
| 2 After restore | 3,232 nodes, 19,777 edges at d3350b3. A restored clone of 2d13546 gives 3,207 nodes, 19,577 edges: the same as Linux. |
| 3 Uninstall (5.1) | Exit 0. All 6 files keep `other` and `note`, with 2-space indents. |
| 4 Resilience (no shim) | 12 of 12 PASS. R4 had 0 errors. |

## Findings for the author

1. **`TypeScriptLiveTests.References_rename_and_a_what_if_edit_come_from_the_language_server` fails on Windows.** In the first Windows run, this test was skipped because the extractor's npm packages were not installed. The install in that run created `codegraph\extractors\typescript\node_modules` (at 22:10), so the test now runs. It fails after about 4 s with `IOException` at `TypeScriptIndexTests.cs:169`. The full text is in step 1. Cause, from the code (not tested): the message argument of `Assert.True` at line 169 always calls `File.ReadAllText(daemon.log)`, also when the condition is true. The daemon holds the file open for write (`Program.cs:160`, `FileShare.ReadWrite`). `File.ReadAllText` opens the file with `FileShare.Read` only, so Windows refuses it while another handle can write.
2. **The uninstall deletes the install folder.** That is the expected effect of `-InstallDir`. But step 4 needs `$env:TEMP\cg-win2\bin\codegraph-mcp.exe` after step 3. Thus I ran `.\install.ps1 -FromSource -SkipTests -InstallDir $env:TEMP\cg-win2\bin` again with no repo (exit 0) before step 4.
3. **The uninstall output does not name the files.** It prints `entry removed` 6 times and `deleted` once for the repo. It does not say which file each line is about. The full output is in step 3.
4. **R8 still falls back to the graph of the working folder.** For all 4 `--root` edge cases, the answer starts with `⚠ STALE INDEX: HEAD is d3350b3 but the graph was built at 2d13546`. That is the graph of `Z:\CodeGraph` (built at 2d13546), where the battery ran. It is not a graph under the test folder. The scenario passes. This is the same behavior as finding 8 of the first report.

## 1. Branch, tests and build

Commands, in `Z:\CodeGraph`:

| Command | Exit | Output |
|---|---|---|
| `git fetch origin` | 0 | `2d13546..d3350b3  any-mcp-client -> origin/any-mcp-client` |
| `git checkout any-mcp-client` | 0 | `Already on 'any-mcp-client'`, `Your branch is behind 'origin/any-mcp-client' by 1 commit` |
| `git pull` | 0 | `Updating 2d13546..d3350b3`, fast-forward, 11 files changed |
| `git rev-parse --short HEAD` | 0 | `d3350b3` |

### `dotnet test tests/CodeGraph.Tests` (in `codegraph`)

Exit 2, duration 1m 21s. Log: `step1-test.log`.

```
Test run summary: Failed!
  total: 436
  failed: 1
  succeeded: 434
  skipped: 1
  duration: 1m 21s 125ms
```

Skipped: `ScipRunnerTests.A_go_module_runs_its_scip_indexer_in_the_module_folder_and_imports_the_index` ("the stand-in indexer is a shell script").

The failure, full text:

```
failed CodeGraph.Tests.TypeScriptLiveTests.References_rename_and_a_what_if_edit_come_from_the_language_server (3s 969ms)
  from Z:\CodeGraph\codegraph\tests\CodeGraph.Tests\bin\Debug\net10.0\CodeGraph.Tests.dll (net10.0|x64)
  Xunit.MicrosoftTestingPlatform.XunitException: System.IO.IOException : The process cannot access the file 'C:\Users\seuna\AppData\Local\Temp\codegraph-tests\bc64e2b8bbc54806b2cc28d6630dd2e3\daemon.log' because it is being used by another process.
    at Microsoft.Win32.SafeHandles.SafeFileHandle.CreateFile(String fullPath, FileMode mode, FileAccess access, FileShare share, FileOptions options)
    at Microsoft.Win32.SafeHandles.SafeFileHandle.Open(String fullPath, FileMode mode, FileAccess access, FileShare share, FileOptions options, Int64 preallocationSize, Nullable`1 unixCreateMode)
    at System.IO.Strategies.OSFileStreamStrategy..ctor(String path, FileMode mode, FileAccess access, FileShare share, FileOptions options, Int64 preallocationSize, Nullable`1 unixCreateMode)
    at System.IO.Strategies.FileStreamHelpers.ChooseStrategyCore(String path, FileMode mode, FileAccess access, FileShare share, FileOptions options, Int64 preallocationSize, Nullable`1 unixCreateMode)
    at System.IO.StreamReader.ValidateArgsAndOpenPath(String path, Int32 bufferSize)
    at System.IO.File.ReadAllText(String path, Encoding encoding)
    at CodeGraph.Tests.TypeScriptLiveTests.References_rename_and_a_what_if_edit_come_from_the_language_server() in Z:\CodeGraph\codegraph\tests\CodeGraph.Tests\TypeScriptIndexTests.cs:169
    --- End of stack trace from previous location ---
```

I ran the test alone 3 times: `dotnet test tests/CodeGraph.Tests --no-build -- --filter-method "*References_rename_and_a_what_if_edit_come_from_the_language_server"`. Each run: exit 2, total 1, failed 1, with the same `IOException` (a different temp folder each time). Logs: `step1-rerun1.log` to `step1-rerun3.log`.

### `dotnet build CodeGraph.slnx -c Release`

Exit 0. `Build succeeded. 0 Warning(s) 0 Error(s)`, 7.3 s. The `codegraph-index.dll` and `codegraph-mcp.dll` timestamps are from this build (23:21). Log: `step1-build.log`.

## 2. The restore note

`git clone Z:\CodeGraph $env:TEMP\cg-win2\fresh`: exit 0. HEAD d3350b3, branch any-mcp-client, no `obj` folders. I did not restore or build it.

Indexer: `Z:\CodeGraph\codegraph\src\CodeGraph.Indexer\bin\Release\net10.0\codegraph-index.exe`.

### `--full`, not restored

`codegraph-index.exe fresh\codegraph\CodeGraph.slnx --full`: exit 0, 18.1 s. Full output:

```
[1/6] project: CodeGraph.Core
⚠ CodeGraph.Core: 19 compile error(s) and no obj/project.assets.json, so its NuGet packages were not restored and calls into them are missing from the graph. Run dotnet restore and index again
[2/6] project: CodeGraph.Indexer
⚠ CodeGraph.Indexer: 163 compile error(s) and no obj/project.assets.json, so its NuGet packages were not restored and calls into them are missing from the graph. Run dotnet restore and index again
[3/6] project: CodeGraph.Mcp
⚠ CodeGraph.Mcp: 76 compile error(s) and no obj/project.assets.json, so its NuGet packages were not restored and calls into them are missing from the graph. Run dotnet restore and index again
[4/6] project: CodeGraph.Web
[5/6] project: CodeGraph.Tests
⚠ CodeGraph.Tests: 929 compile error(s) and no obj/project.assets.json, so its NuGet packages were not restored and calls into them are missing from the graph. Run dotnet restore and index again
[6/6] project: CodeGraph.Bench
compiler: Roslyn 5.9.0 (up to C# 14); projects use C# 14; 1187 compile error(s) in declarations, first: GraphAnchor.cs(1): CS0246 The type or namespace name 'DuckDB' could not be found (are you missing a using directive or an assembly reference?)
indexed 2975 nodes, 17322 edges (full) in 13.1s → C:\Users\seuna\AppData\Local\Temp\cg-win2\fresh\.codegraph\graph.db
```

CodeGraph.Web and CodeGraph.Bench have no warning line.

### Again, without `--full`

Exit 0. Output:

```
incremental: 0 changed, 0 deleted, 0 dependent → walking 0 of 106 files
[1/6] project: CodeGraph.Core
...
[6/6] project: CodeGraph.Bench
compiler: Roslyn 5.9.0 (up to C# 14); no project walked; 0 compile error(s) in declarations
indexed 2975 nodes, 17322 edges (incremental, 0 file(s) walked) in 0.2s → C:\Users\seuna\AppData\Local\Temp\cg-win2\fresh\.codegraph\graph.db
```

The console line says `0 compile error(s)` for this pass. The graph keeps the earlier count:

- `codegraph-index export --db fresh\.codegraph\graph.db` (exit 0) gives `{"type":"meta","language":"csharp","key":"compile_errors","value":"1187"}`. The `notes` record holds the 4 restore notes.
- `index_status` from `codegraph-mcp.exe --root fresh` (no error):

```
size      : 2975 nodes, 17322 edges
status    : up to date with the working tree
csharp    : Roslyn 5.9.0 (up to C# 14); code uses C# 14; 1187 compile error(s), first: GraphAnchor.cs(1): CS0246 The type or namespace name 'DuckDB' could not be found (are you missing a using directive or an assembly reference?)
  note: CodeGraph.Core: 19 compile error(s) and no obj/project.assets.json, so its NuGet packages were not restored and calls into them are missing from the graph. Run dotnet restore and index again
  note: CodeGraph.Indexer: 163 compile error(s) and no obj/project.assets.json, ...
  note: CodeGraph.Mcp: 76 compile error(s) and no obj/project.assets.json, ...
  note: CodeGraph.Tests: 929 compile error(s) and no obj/project.assets.json, ...
```

The full answer is in `step2-index-status-norestore.log`.

### After `dotnet restore`

`dotnet restore fresh\codegraph\CodeGraph.slnx`: exit 0, 6 projects restored. Then `--full`: exit 0. Output:

```
compiler: Roslyn 5.9.0 (up to C# 14); projects use C# 14; 0 compile error(s) in declarations
indexed 3232 nodes, 19777 edges (full) in 13.7s → C:\Users\seuna\AppData\Local\Temp\cg-win2\fresh\.codegraph\graph.db
```

The Linux figure (3,207 and 19,577) is for 2d13546, not for d3350b3. For a direct comparison, I cloned `Z:\CodeGraph` into `fresh-2d13546`, did `git checkout 2d13546`, did `dotnet restore` (exit 0) and indexed with `--full` with the same indexer build (exit 0):

| Commit | Nodes | Edges |
|---|---|---|
| d3350b3, restored | 3,232 | 19,777 |
| 2d13546, restored | 3,207 | 19,577 |
| 2d13546, Linux | 3,207 | 19,577 |

## 3. Uninstall under Windows PowerShell 5.1

Before this step, I copied `fresh` (restored, indexed, no config files) to `template` for step 4.

Install, in `Z:\CodeGraph\codegraph`, from the pwsh 7.7.0 session:
`.\install.ps1 -FromSource -SkipTests -InstallDir $env:TEMP\cg-win2\bin -Client copilot,cursor,roo $env:TEMP\cg-win2\fresh\codegraph\CodeGraph.slnx`: exit 0. Log: `step3-install.log`. It wrote 6 files:

- `fresh\.mcp.json`
- `fresh\.vscode\mcp.json`
- `fresh\codegraph\.vs\mcp.json`
- `fresh\.github\mcp.json`
- `fresh\.cursor\mcp.json`
- `fresh\.roo\mcp.json`

In each file, I added a server `"other"` beside `"codegraph"` (in `mcpServers` or `servers`) and a top-level key `"note": "keep me"`. I wrote the files with 2-space indents. Copies of each file as init wrote it, before the uninstall and after it, are in `step3-configs`.

`powershell.exe -NoProfile -File .\uninstall.ps1 -InstallDir $env:TEMP\cg-win2\bin -Repo $env:TEMP\cg-win2\fresh -Yes`: exit 0. Full output:

```

==> Install folder: C:\Users\seuna\AppData\Local\Temp\cg-win2\bin
deleted

==> Repo: C:\Users\seuna\AppData\Local\Temp\cg-win2\fresh
entry removed
entry removed
entry removed
entry removed
entry removed
entry removed
deleted

Done.
```

After it, `bin` and `fresh\.codegraph` do not exist. All 6 files exist. Each has `other` and `note` and no `codegraph`, with 2-space indents, LF line ends and no BOM. The layout is the same as the layout that init writes.

`fresh\.cursor\mcp.json` after the uninstall:

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

`fresh\.cursor\mcp.json` as init wrote it, for the layout:

```json
{
  "mcpServers": {
    "codegraph": {
      "command": "C:/Users/seuna/AppData/Local/Temp/cg-win2/bin/codegraph-mcp.exe",
      "args": [
        "--root",
        "${workspaceFolder}"
      ]
    }
  }
}
```

## 4. Resilience battery, no shim

The uninstall deleted `bin`, so I installed the binaries again first (finding 2). `.\install.ps1 -FromSource -SkipTests -InstallDir $env:TEMP\cg-win2\bin`: exit 0. Log: `step4-reinstall.log`.

Template: `$env:TEMP\cg-win2\template`, the copy of `fresh` after the restore and the `--full` index (3,232 nodes).

`python tools\CodeGraph.Bench\resilience.py $env:TEMP\cg-win2\res $env:TEMP\cg-win2\template $env:TEMP\cg-win2\bin\codegraph-mcp.exe $env:TEMP\cg-win2\bin\codegraph-index.exe`, in `Z:\CodeGraph\codegraph`: exit 0 after 360.3 s, `12 of 12 passed`. Log: `step4-resilience.log`.

| Scenario | Result | Detail |
|---|---|---|
| R1 no graph, then built while the server runs | PASS | before: "no code graph at …" (error=True); after build: 3232 nodes, 19777 edges |
| R2a corrupt graph: server answers, stays up | PASS | alive=True; 3 answers "… failed: the graph file …" |
| R2b corrupt graph: indexer recovers | PASS | incremental exit 0; then 3232 nodes, 19777 edges |
| R3 kill -9 mid-index | PASS | full at 2 s and 9 s killed; full at 13, 14.5 and 15.5 s finished first; incremental at 1.5, 2.3 and 2.6 s killed. Each time 3232 nodes, no error. Next index exit 0, 3232 nodes. |
| R4 8 readers during 3 full + 3 incremental writes | PASS | 435,009 calls, 0 errors; writer exits [0, 0, 0, 0, 0, 0]; p50 2.6 ms, p99 6.4 ms, max 526 ms; node counts seen 3232 and 3233; new method visible after: True |
| R5 malformed requests, then a normal call | PASS | 12 probes, each with an answer or an error, 1 ms to 64 ms |
| R5b every error says what was wrong | PASS | no opaque "An error occurred" answers; graph intact after the SQL-looking query (3233 nodes) |
| R6 live daemon killed under the server | PASS | daemon pid 18920 killed; then "no live daemon for …"; graph tools 3233 nodes; `live_status start=true`: running (pid 9872) |
| R7 stdin closed: the server exits | PASS | exit 0 after 0.0 s |
| R8 `--root` edge cases | PASS | all 4 answers: "⚠ STALE INDEX: HEAD is d3350b3 but the graph was built at 2d13546; …" (finding 4) |
| R9 edited file without a daemon: answer flagged stale | PASS | "⚠ STALE INDEX: 1 indexed file(s) edited since (Worktree.cs). …" |
| R10 init with broken config files | PASS | non-object `.mcp.json`: exit 1, file untouched; JSONC with comments: exit 0, servers codegraph and other; read-only `.cursor/mcp.json`: exit 1 ("cannot update … Access to the path …") |

R4 error texts: none. R4 had 0 errors.

R3 and R6 ran with no shim. The script now uses `signal.SIGTERM` on Windows (`HARD_KILL`, `resilience.py:44`), and R3 called `p.kill()`.

## Files in `$env:TEMP\cg-win2`

| Path | Content |
|---|---|
| `step1-test.log`, `step1-rerun1.log` to `step1-rerun3.log`, `step1-build.log` | Step 1 |
| `fresh` | The clone for steps 2 and 3 |
| `fresh-2d13546` | The clone of 2d13546 for the comparison |
| `step2-*.log`, `step2-export-after-incr.ndjson` | Step 2 |
| `template` | The template for step 4 |
| `step3-install.log`, `step3-uninstall-ps51.log`, `step3-configs` | Step 3 |
| `bin` | The reinstalled binaries |
| `step4-reinstall.log`, `step4-resilience.log`, `res` | Step 4 |
