# CodeGraph Windows test 6: install script questions

Date: 2026-11-18. Branch install-questions-test at 12b8c14. Machine: Windows 11 Pro 10.0.26300, x64. uv 0.12.7 (on PATH at `C:\Users\seuna\.local\bin\uv.exe`).

All files are in `C:\Users\seuna\AppData\Local\Temp\cg-win6` (`$env:TEMP\cg-win6`). I did not change product code or the test harness. Before and after the runs, `C:\tools\codegraph` did not exist, and the user PATH was the same.

## Summary

| Item | Value |
|---|---|
| Command | `uv run tests\install-questions.py --work $T\iq`, from `$T\src\codegraph`, in pwsh 7.7.0 |
| Exit code | 1 |
| Duration | 1 min 40 s |
| Result | 12 passed, 7 failed, 0 skipped, of 19 cases |
| pwsh 7.7.0 | 9 of 9 cases passed |
| Windows PowerShell 5.1 | 2 of 9 cases passed. All 7 failures have one cause: `Get-FileHash` is not recognized. |
| P1 (user PATH) | Passed |

## Facts to note

1. **All 7 failures in 5.1 come from one error.** `install.ps1` stops after `==> Downloading codegraph-win-x64.zip` with `The term 'Get-FileHash' is not recognized`. N4, I3a and I3b fail because N1 and I1 installed nothing. I2 fails one check because I1 did not install the extension.
2. **The cause is the PSModulePath that the 5.1 process got from pwsh 7.** The harness ran under pwsh 7, through uv and Python. Thus `powershell.exe` got the PSModulePath of pwsh 7, with the pwsh 7 module folders first. 5.1 then finds `Microsoft.PowerShell.Utility` 7.0.0.0 from pwsh 7 before its own 3.1.0.0, and has no `Get-FileHash`. See the diagnostic below.
3. **With PSModulePath cleared, all 9 cases pass in 5.1.** This was a second run, after the run that the plan asked for. Its results are in `$T\iq-clean`.
4. **Before the failure, the questions in 5.1 worked.** In I1 and I2, all the questions came up, the list folded into one line, and the plan showed the folder that the harness typed. I2 said `Nothing was changed.` and exited 0.

## Information lines (from results.md)

- started: 2026-11-18T22:05:46+00:00
- platform: Windows-11-10.0.26300-SP0
- python: 3.13.15
- release: v1.3.0-beta.1
- repository kind: typescript
- commit: 12b8c14 Test the install scripts' questions in a pseudo-terminal
- code: 1.146.0
- shell powershell: 5.1.26100.9818 / 65001
- shell pwsh: 7.7.0 / 65001
- finished: 2026-11-18T22:07:24+00:00

12 passed, 7 failed, 0 skipped, of 19 cases.

## Results table (from results.md)

| Shell | Case | What | Result | Checks |
|---|---|---|---|---|
| powershell | N1 | no terminal, -Yes and a repository | fail | FAIL exit code 0 (was 1)<br>ok no question was printed<br>FAIL .mcp.json written in the repository<br>FAIL codegraph-index installed |
| powershell | N2 | no terminal, -VSCode -Yes | fail | FAIL exit code 0 (was 1)<br>FAIL the extension is installed (None)<br>FAIL the summary says to set codegraph.installDir (not the default folder) |
| powershell | N3 | -VSCode with a release without the extension | pass | ok non-zero exit code (was 1)<br>ok says the release has no VS Code extension<br>ok the install folder was not created |
| powershell | N4 | a running codegraph-mcp, no terminal | fail | FAIL C:\Users\seuna\AppData\Local\Temp\cg-win6\iq\powershell\n1\bin has codegraph-mcp to start (N1 did not install it) |
| powershell | I1 | every question, in a terminal | fail | ok the list folded into one line naming Claude Code and Continue<br>ok no row of the list is left on the screen after it folded<br>ok the extension question says Install (none installed)<br>FAIL exit code 0 (was 1)<br>FAIL .mcp.json written<br>FAIL .continue/mcpServers/codegraph.json written<br>ok nothing written for Kilo Code (not picked)<br>FAIL the extension is installed |
| powershell | I2 | a 14-row window, No to the plan | fail | ok the list folded into one line naming Claude Code and VS Code chat<br>ok no row of the list is left on the screen<br>FAIL the extension question names the installed version<br>ok says Nothing was changed.<br>ok exit code 0 (was 0)<br>ok the install folder was not created |
| powershell | I3a | a running codegraph-mcp, answer n to Stop it? | fail | FAIL C:\Users\seuna\AppData\Local\Temp\cg-win6\iq\powershell\i1\bin has codegraph-mcp to start (I1 did not install it) |
| powershell | I3b | a running codegraph-mcp, answer y to Stop it? | fail | FAIL C:\Users\seuna\AppData\Local\Temp\cg-win6\iq\powershell\i1\bin has codegraph-mcp to start (I1 did not install it) |
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

## Console output

```
Downloading pywinpty (2.0MiB)
 Downloaded pywinpty
Installed 3 packages in 88ms
downloading codegraph-win-x64.zip (v1.3.0-beta.1) ...
downloading codegraph-vscode.vsix (v1.3.0-beta.1) ...
== powershell
  fail  N1   no terminal, -Yes and a repository
          FAIL exit code 0 (was 1)
          FAIL .mcp.json written in the repository
          FAIL codegraph-index installed
  fail  N2   no terminal, -VSCode -Yes
          FAIL exit code 0 (was 1)
          FAIL the extension is installed (None)
          FAIL the summary says to set codegraph.installDir (not the default folder)
  pass  N3   -VSCode with a release without the extension
  fail  N4   a running codegraph-mcp, no terminal
          FAIL C:\Users\seuna\AppData\Local\Temp\cg-win6\iq\powershell\n1\bin has codegraph-mcp to start (N1 did not install it)
  fail  I1   every question, in a terminal
          FAIL exit code 0 (was 1)
          FAIL .mcp.json written
          FAIL .continue/mcpServers/codegraph.json written
          FAIL the extension is installed
  fail  I2   a 14-row window, No to the plan
          FAIL the extension question names the installed version
  fail  I3a  a running codegraph-mcp, answer n to Stop it?
          FAIL C:\Users\seuna\AppData\Local\Temp\cg-win6\iq\powershell\i1\bin has codegraph-mcp to start (I1 did not install it)
  fail  I3b  a running codegraph-mcp, answer y to Stop it?
          FAIL C:\Users\seuna\AppData\Local\Temp\cg-win6\iq\powershell\i1\bin has codegraph-mcp to start (I1 did not install it)
  pass  U1   uninstall -Yes
== pwsh
  pass  N1   no terminal, -Yes and a repository
  pass  N2   no terminal, -VSCode -Yes
  pass  N3   -VSCode with a release without the extension
  pass  N4   a running codegraph-mcp, no terminal
  pass  I1   every question, in a terminal
  pass  I2   a 14-row window, No to the plan
  pass  I3a  a running codegraph-mcp, answer n to Stop it?
  pass  I3b  a running codegraph-mcp, answer y to Stop it?
  pass  U1   uninstall -Yes

12 passed, 7 failed, 0 skipped, of 19 cases.
results: C:\Users\seuna\AppData\Local\Temp\cg-win6\iq\results.md
```

## Failure: powershell N1

`screens\powershell-N1.txt`:

```
==> Downloading codegraph-win-x64.zip (v1.3.0-beta.1)
C:\Users\seuna\AppData\Local\Temp\cg-win6\src\codegraph\install.ps1 : The term 'Get-FileHash' is not recognized as the 
name of a cmdlet, function, script file, or operable program. Check the spelling of the name, or if a path was 
included, verify that the path is correct and try again.
    + CategoryInfo          : ObjectNotFound: (Get-FileHash:String) [install.ps1], CommandNotFoundException
    + FullyQualifiedErrorId : CommandNotFoundException,install.ps1
```

## Failure: powershell N2

`screens\powershell-N2.txt` has the same text as N1:

```
==> Downloading codegraph-win-x64.zip (v1.3.0-beta.1)
C:\Users\seuna\AppData\Local\Temp\cg-win6\src\codegraph\install.ps1 : The term 'Get-FileHash' is not recognized as the 
name of a cmdlet, function, script file, or operable program. Check the spelling of the name, or if a path was 
included, verify that the path is correct and try again.
    + CategoryInfo          : ObjectNotFound: (Get-FileHash:String) [install.ps1], CommandNotFoundException
    + FullyQualifiedErrorId : CommandNotFoundException,install.ps1
```

## Failure: powershell N4

The harness wrote no screen for N4. The case did not run the script, because N1 did not install `codegraph-mcp.exe` in `iq\powershell\n1\bin`.

## Failure: powershell I1

`screens\powershell-I1.txt`. All the questions came up. The script stopped after `Go ahead? Yes`:

```
? Install folder (C:\tools\codegraph) C:\Users\seuna\AppData\Local\Temp\cg-win6\iq\powershell\i1\bin
? Set up C:\Users\seuna\AppData\Local\Temp\cg-win6\iq\powershell\i1-repo? (Y/n) Yes
? Another repository? (Enter when done)
? Assistants to set each repository up for Claude Code, Copilot CLI, Continue
  The live daemon answers from the compiler between indexes; it holds the loaded solution in memory.
? Let the MCP server start the live daemon on its first call? (y/N) No
? Add C:\Users\seuna\AppData\Local\Temp\cg-win6\iq\powershell\i1\bin to PATH, so codegraph-index works in any shell? (Y/n) No
  The extension shows each graph's state, runs the live daemons, and offers the MCP server to VS Code's chat.
? Install the CodeGraph extension for VS Code? (Y/n) Yes

Plan
  Install      CodeGraph v1.3.0-beta.1 (win-x64) to C:\Users\seuna\AppData\Local\Temp\cg-win6\iq\powershell\i1\bin
  Set up       C:\Users\seuna\AppData\Local\Temp\cg-win6\iq\powershell\i1-repo  claude,continue
  VS Code      install the extension with code.cmd

? Go ahead? (Y/n) Yes

==> Downloading codegraph-win-x64.zip (v1.3.0-beta.1)
C:\Users\seuna\AppData\Local\Temp\cg-win6\src\codegraph\install.ps1 : The term 'Get-FileHash' is not recognized as the name of a cmdlet, function, script
file, or operable program. Check the spelling of the name, or if a path was included, verify that the path is correct and try again.
    + CategoryInfo          : ObjectNotFound: (Get-FileHash:String) [install.ps1], CommandNotFoundException
    + FullyQualifiedErrorId : CommandNotFoundException,install.ps1
```

## Failure: powershell I2

`screens\powershell-I2.txt`. The failed check is `the extension question names the installed version`. I1 did not install the extension, so the question says `Install`, not `Update ... (installed: ...)`:

```
? Install the CodeGraph extension for VS Code? (Y/n) No
```

In pwsh, the same line was:

```
? Update the CodeGraph extension for VS Code (installed: 1.3.0-beta.1)? (Y/n) No
```

The other checks in I2 passed. The screen ends with:

```
? Go ahead? (Y/n) No
Nothing was changed.
```

## Failure: powershell I3a and I3b

The harness wrote no screens for I3a and I3b. The cases did not run the script, because I1 did not install `codegraph-mcp.exe` in `iq\powershell\i1\bin`.

## Diagnostic: Get-FileHash in Windows PowerShell 5.1

I ran these commands after the test, in the same pwsh 7.7.0 session that started the harness.

The PSModulePath of that session:

```
C:\Users\seuna\OneDrive\Documents\PowerShell\Modules
C:\Program Files\PowerShell\Modules
c:\program files\windowsapps\microsoft.powershell_7.7.0.0_x64__8wekyb3d8bbwe\Modules
C:\Program Files\WindowsPowerShell\Modules
C:\WINDOWS\system32\WindowsPowerShell\v1.0\Modules
```

| How `powershell.exe` started | Is `Get-FileHash` found? |
|---|---|
| Through `cmd /c`, with that PSModulePath (as the harness starts it) | No |
| Through `cmd /c`, with PSModulePath cleared | Yes |
| Directly from pwsh 7 | Yes |

With the PSModulePath of pwsh 7, 5.1 lists 2 versions of `Microsoft.PowerShell.Utility`:

```
7.0.0.0 C:\program files\windowsapps\microsoft.powershell_7.7.0.0_x64__8wekyb3d8bbwe\Modules\Microsoft.PowerShell.Utility
3.1.0.0 C:\WINDOWS\system32\WindowsPowerShell\v1.0\Modules\Microsoft.PowerShell.Utility
```

Not tested here: the PowerShell 7 documentation says that pwsh changes PSModulePath when it starts `powershell.exe` itself. The harness starts `powershell.exe` from Python, not from pwsh.

## Second run: Windows PowerShell 5.1 with PSModulePath cleared

This run is not in the plan. I did it to find if the 5.1 failures come from the scripts or from the environment.

Command, in pwsh 7.7.0, from `$T\src\codegraph`:

```
$env:PSModulePath = $null
uv run tests\install-questions.py --work $T\iq-clean --shells powershell
```

Exit code: 0. Output:

```
downloading codegraph-win-x64.zip (v1.3.0-beta.1) ...
downloading codegraph-vscode.vsix (v1.3.0-beta.1) ...
== powershell
  pass  N1   no terminal, -Yes and a repository
  pass  N2   no terminal, -VSCode -Yes
  pass  N3   -VSCode with a release without the extension
  pass  N4   a running codegraph-mcp, no terminal
  pass  I1   every question, in a terminal
  pass  I2   a 14-row window, No to the plan
  pass  I3a  a running codegraph-mcp, answer n to Stop it?
  pass  I3b  a running codegraph-mcp, answer y to Stop it?
  pass  U1   uninstall -Yes

10 passed, 0 failed, 0 skipped, of 10 cases.
results: C:\Users\seuna\AppData\Local\Temp\cg-win6\iq-clean\results.md
```

After this run, `C:\tools\codegraph` did not exist.

## Files

| File | Content |
|---|---|
| `$T\console-6.txt` | Console output of the run in the plan |
| `$T\iq\results.md`, `$T\iq\results.json` | Results of the run in the plan |
| `$T\iq\screens\` | Screens and raw logs of the run in the plan |
| `$T\console-6-clean.txt` | Console output of the second run |
| `$T\iq-clean\` | Results, screens and logs of the second run |
