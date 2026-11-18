# CodeGraph Windows test 9: install script questions, with uninstall in 5.1

Date: 2026-11-18. Branch install-questions-test at c919b87. Machine: Windows 11 Pro 10.0.26300, x64. uv 0.12.7 (on PATH).

All files are in `C:\Users\seuna\AppData\Local\Temp\cg-win6` (`$env:TEMP\cg-win6`). I did not change product code or the test harness. Before and after the run, `C:\tools\codegraph` did not exist, and the user PATH was the same.

## Summary

| Item | Value |
|---|---|
| Command | `uv run tests\install-questions.py --work $T\iq9`, from `$T\src\codegraph`, in pwsh 7.7.0 |
| PSModulePath of the harness | The pwsh 7 path, as in tests 7 and 8 |
| Exit code | 0 |
| Duration | 4 min 10 s |
| Result | 21 passed, 0 failed, 0 skipped, of 21 cases |

## Facts to note

1. **All cases passed.** These are the 11 powershell cases (N5 and the new U5 included), the 9 pwsh cases, and P1.
2. **U5 passed.** `uninstall.ps1` in 5.1 with the PSModulePath of pwsh 7 deleted `n5\bin` and removed the codegraph entry from `n5-repo\.mcp.json`. It also deleted `n5-repo\.codegraph`.
3. **After U5, `n5-repo\.mcp.json` is valid JSON with an empty server list:**

   ```
   {
     "mcpServers": {}
   }
   ```

## Information lines (from results.md)

- started: 2026-11-18T22:40:48+00:00
- platform: Windows-11-10.0.26300-SP0
- python: 3.13.15
- release: v1.3.0-beta.1
- repository kind: typescript
- commit: c919b87 Test uninstall.ps1 in Windows PowerShell 5.1 with PowerShell 7's PSModulePath
- code: 1.146.0
- shell powershell: 5.1.26100.9818 / 65001
- shell pwsh: 7.7.0 / 65001
- finished: 2026-11-18T22:44:57+00:00

21 passed, 0 failed, 0 skipped, of 21 cases.

## Results table (from results.md)

| Shell | Case | What | Result | Checks |
|---|---|---|---|---|
| powershell | N1 | no terminal, -Yes and a repository | pass | ok exit code 0 (was 0)<br>ok no question was printed<br>ok .mcp.json written in the repository<br>ok codegraph-index installed |
| powershell | N5 | 5.1 started with PowerShell 7's PSModulePath | pass | ok exit code 0 (was 0)<br>ok the checksum was checked<br>ok .mcp.json written in the repository |
| powershell | U5 | uninstall in 5.1 started with PowerShell 7's PSModulePath | pass | ok exit code 0 (was 0)<br>ok the install folder was deleted<br>ok the codegraph entry is gone from .mcp.json, and the file is still valid JSON |
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

## powershell U5 screen

`screens\powershell-U5.txt`, in full:

```

==> Install folder: C:\Users\seuna\AppData\Local\Temp\cg-win6\iq9\powershell\n5\bin
deleted C:\Users\seuna\AppData\Local\Temp\cg-win6\iq9\powershell\n5\bin

==> Repo: C:\Users\seuna\AppData\Local\Temp\cg-win6\iq9\powershell\n5-repo
removed the codegraph entry from C:\Users\seuna\AppData\Local\Temp\cg-win6\iq9\powershell\n5-repo\.mcp.json
deleted C:\Users\seuna\AppData\Local\Temp\cg-win6\iq9\powershell\n5-repo\.codegraph

Done.
```

## Files

| File | Content |
|---|---|
| `$T\console-9.txt` | Console output and exit code |
| `$T\iq9\results.md`, `$T\iq9\results.json` | Results |
| `$T\iq9\screens\` | Screens and raw logs |
