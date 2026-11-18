# CodeGraph Windows test 8: install script questions, re-run

Date: 2026-11-18. Branch install-questions-test at 5857063. Machine: Windows 11 Pro 10.0.26300, x64. uv 0.12.7 (on PATH).

All files are in `C:\Users\seuna\AppData\Local\Temp\cg-win6` (`$env:TEMP\cg-win6`). I did not change product code or the test harness. Before and after the run, `C:\tools\codegraph` did not exist, and the user PATH was the same.

## Summary

| Item | Value |
|---|---|
| Command | `uv run tests\install-questions.py --work $T\iq8`, from `$T\src\codegraph`, in pwsh 7.7.0 |
| PSModulePath of the harness | The pwsh 7 path, as in test 7 |
| Exit code | 0 |
| Duration | 3 min 13 s |
| Result | 20 passed, 0 failed, 0 skipped, of 20 cases |

## Facts to note

1. **All cases passed.** These are the 10 powershell cases (N5 included), the 9 pwsh cases, and P1.
2. **N5 completed the unzip step and set up the repository.** `screens\powershell-N5.txt` shows `checksum ok`, the install, `ok: codegraph 1.3.0-beta.1`, the set-up of `n5-repo`, and `==> Done`.
3. **The N5 install folder is complete.** `iq8\powershell\n5\bin` holds 635 files, `codegraph-index.exe` and `codegraph-mcp.exe` included. `iq8\powershell\n5-repo\.mcp.json` exists.

## Information lines (from results.md)

- started: 2026-11-18T22:33:32+00:00
- platform: Windows-11-10.0.26300-SP0
- python: 3.13.15
- release: v1.3.0-beta.1
- repository kind: typescript
- commit: 5857063 Drop PowerShell 7's module folders when the scripts run in Windows PowerShell 5.1
- code: 1.146.0
- shell powershell: 5.1.26100.9818 / 65001
- shell pwsh: 7.7.0 / 65001
- finished: 2026-11-18T22:36:45+00:00

20 passed, 0 failed, 0 skipped, of 20 cases.

## Results table (from results.md)

| Shell | Case | What | Result | Checks |
|---|---|---|---|---|
| powershell | N1 | no terminal, -Yes and a repository | pass | ok exit code 0 (was 0)<br>ok no question was printed<br>ok .mcp.json written in the repository<br>ok codegraph-index installed |
| powershell | N5 | 5.1 started with PowerShell 7's PSModulePath | pass | ok exit code 0 (was 0)<br>ok the checksum was checked<br>ok .mcp.json written in the repository |
| powershell | N2 | no terminal, -VSCode -Yes | pass | ok exit code 0 (was 0)<br>ok the extension is installed (1.3.0-beta.1)<br>ok the summary says to set codegraph.installDir (not the default folder) |
| powershell | N3 | -VSCode with a release without the extension | pass | ok non-zero exit code (was 1)<br>ok says the release has no VS Code extension<br>ok the install folder was not created |
| powershell | N4 | a running codegraph-mcp, no terminal | pass | ok refused without -Force (exit 1)<br>ok the server still runs after the refusal<br>ok exit code 0 with -Force (was 0)<br>ok the server was stopped |
| powershell | I1 | every question, in a terminal | pass | ok the list folded into one line naming Claude Code and Continue<br>ok no row of the list is left on the screen after it folded<br>ok the extension question says Install (none installed)<br>ok exit code 0 (was 0)<br>ok .mcp.json written<br>ok .continue/mcpServers/codegraph.json written<br>ok nothing written for Kilo Code (not picked)<br>ok the extension is installed |
| powershell | I2 | a 14-row window, No to the plan | pass | ok the list folded into one line naming Claude Code and VS Code chat<br>ok no row of the list is left on the screen<br>ok the extension question names the installed version<br>ok says Nothing was changed.<br>ok exit code 0 (was 0)<br>ok the install folder was not created |
| powershell | I3a | a running codegraph-mcp, answer n to Stop it? | pass | ok non-zero exit code (was 1)<br>ok the server still runs |
| powershell | I3b | a running codegraph-mcp, answer y to Stop it? | pass | ok exit code 0 (was 0)<br>ok the server was stopped |
| powershell | U1 | uninstall -Yes | pass | ok exit code 0 (was 0)<br>ok the install folder was deleted<br>ok the codegraph entry is gone from .mcp.json<br>ok the extension was uninstalled |
| pwsh | N1 | no terminal, -Yes and a repository | pass | ok exit code 0 (was 0)<br>ok no question was printed<br>ok .mcp.json written in the repository<br>ok codegraph-index installed |
| pwsh | N2 | no terminal, -VSCode -Yes | pass | ok exit code 0 (was 0)<br>ok the extension is installed (1.3.0-beta.1)<br>ok the summary says to set codegraph.installDir (not the default folder) |
| pwsh | N3 | -VSCode with a release without the extension | pass | ok non-zero exit code (was 1)<br>ok says the release has no VS Code extension<br>ok the install folder was not created |
| pwsh | N4 | a running codegraph-mcp, no terminal | pass | ok refused without -Force (exit 1)<br>ok the server still runs after the refusal<br>ok exit code 0 with -Force (was 0)<br>ok the server was stopped |
| pwsh | I1 | every question, in a terminal | pass | ok the list folded into one line naming Claude Code and Continue<br>ok no row of the list is left on the screen after it folded<br>ok the extension question says Install (none installed)<br>ok exit code 0 (was 0)<br>ok .mcp.json written<br>ok .continue/mcpServers/codegraph.json written<br>ok nothing written for Kilo Code (not picked)<br>ok the extension is installed |
| pwsh | I2 | a 14-row window, No to the plan | pass | ok the list folded into one line naming Claude Code and VS Code chat<br>ok no row of the list is left on the screen<br>ok the extension question names the installed version<br>ok says Nothing was changed.<br>ok exit code 0 (was 0)<br>ok the install folder was not created |
| pwsh | I3a | a running codegraph-mcp, answer n to Stop it? | pass | ok non-zero exit code (was 1)<br>ok the server still runs |
| pwsh | I3b | a running codegraph-mcp, answer y to Stop it? | pass | ok exit code 0 (was 0)<br>ok the server was stopped |
| pwsh | U1 | uninstall -Yes | pass | ok exit code 0 (was 0)<br>ok the install folder was deleted<br>ok the codegraph entry is gone from .mcp.json<br>ok the extension was uninstalled |
| - | P1 | the user PATH did not change | pass | ok the user PATH is byte-identical before and after |

## Failures

None.

## powershell N5 screen

N5 passed. This is the full `screens\powershell-N5.txt`, because the plan asks if the unzip step completed:

```
==> Downloading codegraph-win-x64.zip (v1.3.0-beta.1)
  checksum ok

==> Installing to C:\Users\seuna\AppData\Local\Temp\cg-win6\iq8\powershell\n5\bin
  codegraph 1.3.0-beta.1

==> Checking the executables start
  ok: codegraph 1.3.0-beta.1

==> Setting up C:\Users\seuna\AppData\Local\Temp\cg-win6\iq8\powershell\n5-repo
codegraph init: C:\Users\seuna\AppData\Local\Temp\cg-win6\iq8\powershell\n5-repo\tsconfig.json
  .mcp.json      created with the codegraph server for Claude Code, Copilot CLI (C:\Users\seuna\AppData\Local\Temp\cg-win6\iq8\powershell\n5-repo\.mcp.json)
  .gitignore     added .codegraph/
  other clients  add --client copilot, cursor, gemini, codex, roo, kilo or continue (or all)

Ready. Start Claude Code in C:\Users\seuna\AppData\Local\Temp\cg-win6\iq8\powershell\n5-repo (or run /mcp in an open session) and approve the codegraph server.
Keep the graph fresh while you work:  codegraph-index "C:\Users\seuna\AppData\Local\Temp\cg-win6\iq8\powershell\n5-repo\tsconfig.json" --watch

==> Done
  ✓ CodeGraph 1.3.0-beta.1 in C:\Users\seuna\AppData\Local\Temp\cg-win6\iq8\powershell\n5\bin
  ✓ Set up C:\Users\seuna\AppData\Local\Temp\cg-win6\iq8\powershell\n5-repo for claude

Set up another repo (writes .mcp.json and .gitignore at its root, then builds the graph):
  & 'C:\Users\seuna\AppData\Local\Temp\cg-win6\iq8\powershell\n5\bin\codegraph-index.exe' init <repo-or-solution> [--live] [--client copilot,cursor,gemini,codex,roo,kilo,continue]
Keep a graph fresh while you work:
  & 'C:\Users\seuna\AppData\Local\Temp\cg-win6\iq8\powershell\n5\bin\codegraph-index.exe' <App.sln> --watch
codegraph-ts: n5-repo: 1 file(s) from tsconfig.json
codegraph-ts: compiler: typescript 6.0.3, @angular/compiler 22.2.6; targets ES2022; 0 syntax error(s)
codegraph-ts: 1 file(s), 5 nodes, 5 edges, 22 records in 0.4s
indexed 5 typescript nodes, 5 edges from 2 files in 1.1s → C:\Users\seuna\AppData\Local\Temp\cg-win6\iq8\powershell\n5-repo\.codegraph\graph.db
```

## Files

| File | Content |
|---|---|
| `$T\console-8.txt` | Console output and exit code |
| `$T\iq8\results.md`, `$T\iq8\results.json` | Results |
| `$T\iq8\screens\` | Screens and raw logs |
