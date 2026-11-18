# CodeGraph Windows test 7: install script questions, re-run

Date: 2026-11-18. Branch install-questions-test at b302cc9. Machine: Windows 11 Pro 10.0.26300, x64. uv 0.12.7 (on PATH).

All files are in `C:\Users\seuna\AppData\Local\Temp\cg-win6` (`$env:TEMP\cg-win6`). I did not change product code or the test harness. Before and after the run, `C:\tools\codegraph` did not exist, and the user PATH was the same.

## Summary

| Item | Value |
|---|---|
| Command | `uv run tests\install-questions.py --work $T\iq7`, from `$T\src\codegraph`, in pwsh 7.7.0 |
| PSModulePath of the harness | The pwsh 7 path (first entry `C:\Users\seuna\OneDrive\Documents\PowerShell\Modules`) |
| Exit code | 1 |
| Duration | 3 min 10 s |
| Result | 19 passed, 1 failed, 0 skipped, of 20 cases |
| Failed case | powershell N5 |

## Facts to note

1. **The `Get-FileHash` failure of test 6 is gone.** In N5, 5.1 ran with the PSModulePath of pwsh 7 and printed `checksum ok`.
2. **N5 now fails at the next step, `Expand-Archive`.** The error is `Cannot find an overload for "ExtractToDirectory" and the argument count: "3".`
3. **The cause is of the same type as in test 6.** With the PSModulePath of pwsh 7, 5.1 loads `Microsoft.PowerShell.Archive` 1.2.7 from pwsh 7, not its own 1.0.1.0. See the diagnostic below.
4. **After the failure, `iq7\powershell\n5\bin` exists and holds 0 files.**
5. **All other cases passed.** These are the 9 other cases in 5.1 (PSModulePath cleared by the harness) and all 9 cases in pwsh.

## Information lines (from results.md)

- started: 2026-11-18T22:17:39+00:00
- platform: Windows-11-10.0.26300-SP0
- python: 3.13.15
- release: v1.3.0-beta.1
- repository kind: typescript
- commit: b302cc9 Hash with .NET in install.ps1; start Windows PowerShell 5.1 as a user would
- code: 1.146.0
- shell powershell: 5.1.26100.9818 / 65001
- shell pwsh: 7.7.0 / 65001
- finished: 2026-11-18T22:20:48+00:00

19 passed, 1 failed, 0 skipped, of 20 cases.

## Results table (from results.md)

| Shell | Case | What | Result | Checks |
|---|---|---|---|---|
| powershell | N1 | no terminal, -Yes and a repository | pass | ok exit code 0 (was 0)<br>ok no question was printed<br>ok .mcp.json written in the repository<br>ok codegraph-index installed |
| powershell | N5 | 5.1 started with PowerShell 7's PSModulePath | fail | FAIL exit code 0 (was 1)<br>ok the checksum was checked<br>FAIL .mcp.json written in the repository |
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

## Failure: powershell N5

`screens\powershell-N5.txt`, in full:

```
==> Downloading codegraph-win-x64.zip (v1.3.0-beta.1)
  checksum ok

==> Installing to C:\Users\seuna\AppData\Local\Temp\cg-win6\iq7\powershell\n5\bin
Expand-Archive : Cannot find an overload for "ExtractToDirectory" and the argument count: "3".
At C:\Users\seuna\AppData\Local\Temp\cg-win6\src\codegraph\install.ps1:522 char:13
+             Expand-Archive -Path $archive -DestinationPath $InstallDi ...
+             ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
    + CategoryInfo          : NotSpecified: (:) [Expand-Archive], MethodException
    + FullyQualifiedErrorId : MethodCountCouldNotFindBest,Expand-Archive
```

## Diagnostic: Expand-Archive in Windows PowerShell 5.1

I ran these commands after the test, in the same pwsh 7.7.0 session. `cmd /c` starts `powershell.exe` with the environment of that session, as the harness does.

With the PSModulePath of pwsh 7, 5.1 lists 2 versions of `Microsoft.PowerShell.Archive`:

```
1.2.7 C:\program files\windowsapps\microsoft.powershell_7.7.0.0_x64__8wekyb3d8bbwe\Modules\Microsoft.PowerShell.Archive
1.0.1.0 C:\WINDOWS\system32\WindowsPowerShell\v1.0\Modules\Microsoft.PowerShell.Archive
```

| PSModulePath of `powershell.exe` | Module that `Expand-Archive` comes from |
|---|---|
| The pwsh 7 path (inherited) | 1.2.7, `C:\program files\windowsapps\microsoft.powershell_7.7.0.0_x64__8wekyb3d8bbwe\Modules\Microsoft.PowerShell.Archive` |
| Cleared | 1.0.1.0, `C:\WINDOWS\system32\WindowsPowerShell\v1.0\Modules\Microsoft.PowerShell.Archive` |

## Files

| File | Content |
|---|---|
| `$T\console-7.txt` | Console output and exit code |
| `$T\iq7\results.md`, `$T\iq7\results.json` | Results |
| `$T\iq7\screens\` | Screens and raw logs |
