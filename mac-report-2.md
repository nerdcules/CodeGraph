# CodeGraph macOS test 2 — branch any-mcp-client at 1a9ee1c

Date: 2026-11-15. It checks the symlink build fix (f007cdd) on this Mac and the uninstall prompt that test 1 could
not run. No product code was changed. Logs and clones are under ~/cg-mac2; step 2 used /tmp/cg-mac2 (srcN, binN).
Nothing outside those two folders was deleted. Every install.sh and uninstall.sh command had an `--install-dir`
under one of them. `--add-to-path` was not used; ~/.codegraph, ~/.zshrc, ~/.bashrc and ~/.codex were not touched.
~/.nvm/versions/node/v24.19.0/bin was first on PATH for every step.

Machine: the Mac of mac-report-1.md (macOS 15.7.9, x86_64, 8 logical CPUs). `dotnet --version` 10.0.302,
`node --version` v24.19.0, git 2.55.0. `/tmp` is a symlink to `/private/tmp`.

Every step ran. Steps 2 and 3 overlapped in time (step 2 in the background); nothing else ran at the same time.

## 1. Clone, packages, tests

```
git clone https://github.com/nerdcules/CodeGraph.git ~/cg-mac2/src     exit 0
git checkout any-mcp-client                                              exit 0
git rev-parse --short HEAD                                               1a9ee1c
(cd extractors/typescript && npm ci)                                     exit 0  (added 4 packages, and audited 5 packages in 1s)
dotnet test tests/CodeGraph.Tests   (cwd ~/cg-mac2/src/codegraph)        exit 0
```

Logs: step1-clone.log, step1-dotnet-test.log.

```
Test run summary: Passed!
  total: 441
  failed: 0
  succeeded: 439
  skipped: 2
  duration: 1m 22s 015ms
```

Skipped tests (both say "drive letters exist only on Windows"):

- CodeGraph.Tests.LspPathTests.A_servers_windows_uri_is_keyed_as_ours
- CodeGraph.Tests.LspPathTests.A_lower_case_drive_letter_comes_back_upper_case_on_windows

Failed tests: none.

## 2. From-source install from a /tmp path, three times

For N = 1, 2, 3:

```
git clone https://github.com/nerdcules/CodeGraph.git /tmp/cg-mac2/srcN     exit 0
git -C /tmp/cg-mac2/srcN checkout any-mcp-client                           exit 0, HEAD 1a9ee1c
cd /tmp/cg-mac2/srcN/codegraph        (pwd = /tmp/cg-mac2/srcN/codegraph, pwd -P = /private/tmp/cg-mac2/srcN/codegraph)
./install.sh --from-source --skip-tests --install-dir /tmp/cg-mac2/binN
/tmp/cg-mac2/binN/codegraph-index --version
```

Logs: step2-cloneN.log, step2-installN.log, step2-versionN.log.

| N | install.sh exit | lines with "bin/Debug" | "Restored" lines | errors | last lines | `--version` |
|---|---|---|---|---|---|---|
| 1 | 0 | 0 | 9 (6 in the build, 3 in the publish; see below) | none; "0 Warning(s) 0 Error(s)" | `ok: codegraph 1.1.0`, Installed | 1.1.0, exit 0 |
| 2 | 0 | 0 | 9 (same) | none | same | 1.1.0, exit 0 |
| 3 | 0 | 0 | 9 (same) | none | same | 1.1.0, exit 0 |

The "Restored" lines of run 1 by step (runs 2 and 3 list the same projects, in another order). Every path has the
`/private/tmp` spelling: install.sh now resolves its root with `pwd -P`. Within one dotnet command each project
appears once; the build restores the six projects, and the publish of codegraph-index and codegraph-mcp restores
Core, Indexer and Mcp again for the osx-x64 runtime.

```
==> Installing the TypeScript extractor's packages (Node v24.19.0)
==> Building (warnings are errors)
  Restored /private/tmp/cg-mac2/src1/codegraph/src/CodeGraph.Core/CodeGraph.Core.csproj
  Restored /private/tmp/cg-mac2/src1/codegraph/src/CodeGraph.Mcp/CodeGraph.Mcp.csproj
  Restored /private/tmp/cg-mac2/src1/codegraph/tools/CodeGraph.Bench/CodeGraph.Bench.csproj
  Restored /private/tmp/cg-mac2/src1/codegraph/src/CodeGraph.Web/CodeGraph.Web.csproj
  Restored /private/tmp/cg-mac2/src1/codegraph/src/CodeGraph.Indexer/CodeGraph.Indexer.csproj
  Restored /private/tmp/cg-mac2/src1/codegraph/tests/CodeGraph.Tests/CodeGraph.Tests.csproj
==> Publishing to /tmp/cg-mac2/bin1 (osx-x64, ReadyToRun)
  Restored /private/tmp/cg-mac2/src1/codegraph/src/CodeGraph.Core/CodeGraph.Core.csproj
  Restored /private/tmp/cg-mac2/src1/codegraph/src/CodeGraph.Indexer/CodeGraph.Indexer.csproj
  Restored /private/tmp/cg-mac2/src1/codegraph/src/CodeGraph.Mcp/CodeGraph.Mcp.csproj
==> Checking the executables start
==> Installed
```

No "note:" lines. The install folders hold 58 entries each (bin1 and bin2 still exist; bin3 was deleted in step 4b).

## 3. tests/install-through-symlink.sh

```
cd ~/cg-mac2/src/codegraph && TMPDIR=~/cg-mac2/t tests/install-through-symlink.sh     exit 0
```

`TMPDIR` was set so that the script's clone and its `--install-dir` stay under ~/cg-mac2 (its default is the
system temporary folder). Log: step3-symlink-test.log. The lines asked for:

```
  each of the 6 projects restored once
  Release output only
ok: restore, Release build and install through /Users/alanseunarayan/cg-mac2/t/codegraph-symlink.paP6tB/link
```

The other step lines, in order: Cloning ~/cg-mac2/src into .../real/src; Restoring .../link/src/codegraph/CodeGraph.slnx;
Building ... in Release; Installing from source into .../link/bin; Installing the TypeScript extractor's packages
(Node v24.19.0); Building (warnings are errors); Publishing to .../link/bin (osx-x64, ReadyToRun); Checking the
executables start; `ok: codegraph 1.1.0`; Installed; `1.1.0` (codegraph-index --version). The script deleted its
work folder at the end.

## 4. The uninstall prompt

```
git clone ~/cg-mac2/src ~/cg-mac2/repo                                   exit 0
git -C ~/cg-mac2/repo checkout any-mcp-client                            exit 0, HEAD 1a9ee1c
/tmp/cg-mac2/bin3/codegraph-index init ~/cg-mac2/repo --client claude,cursor --no-index     exit 0
```

init output (step4-init.log):

```
codegraph init: /Users/alanseunarayan/cg-mac2/repo/codegraph/CodeGraph.slnx
  .mcp.json      created with the codegraph server for Claude Code, Copilot CLI (/Users/alanseunarayan/cg-mac2/repo/.mcp.json)
  .cursor/mcp.json created with the codegraph server for Cursor (/Users/alanseunarayan/cg-mac2/repo/.cursor/mcp.json)
  .gitignore     already ignores .codegraph/
```

Files it wrote (`git status --short --untracked-files=all`): `.mcp.json` and `.cursor/mcp.json`. No `.codegraph/`
folder (`--no-index`). Both files name the server by its physical path, `/private/tmp/cg-mac2/bin3/codegraph-mcp`,
although the install dir was given as `/tmp/cg-mac2/bin3`:

```
.mcp.json:        {"mcpServers": {"codegraph": {"command": "/private/tmp/cg-mac2/bin3/codegraph-mcp"}}}
.cursor/mcp.json: {"mcpServers": {"codegraph": {"command": "/private/tmp/cg-mac2/bin3/codegraph-mcp", "args": ["--root", "${workspaceFolder}"]}}}
```

Both uninstall commands ran from ~/cg-mac2/src/codegraph. `script -q /dev/null` gives uninstall.sh a terminal; the
answers come from `yes`. The terminal echoes each answer, so the raw logs start with many lines of `n` (or `y`);
they are left out below. After uninstall.sh exits, script prints `script: write master: Input/output error` (the
rest of the `yes` stream has nowhere to go) and script's own exit code is 1 in both runs; uninstall.sh's exit code
is not visible through script.

### 4a. `yes n | script -q /dev/null ./uninstall.sh --install-dir /tmp/cg-mac2/bin3 --repo ~/cg-mac2/repo`

Run twice (the second run only to capture script's exit code; the first has no exit code line). Logs:
step4a-uninstall-n.log, step4a-uninstall-n-rerun.log. Full output, both runs the same:

```
==> Install folder: /tmp/cg-mac2/bin3
Delete /tmp/cg-mac2/bin3 and everything in it? [y/N] n

==> Repo: /Users/alanseunarayan/cg-mac2/repo
Remove the codegraph server entry from /Users/alanseunarayan/cg-mac2/repo/.mcp.json? [y/N] n
Remove the codegraph server entry from /Users/alanseunarayan/cg-mac2/repo/.cursor/mcp.json? [y/N] n

Done.
script: write master: Input/output error
```

It asked three times and removed nothing. After both runs: /tmp/cg-mac2/bin3 still has 58 entries, including
codegraph-index and codegraph-mcp; `.mcp.json` and `.cursor/mcp.json` both still have their `codegraph` entry
(checked by reading the JSON).

### 4b. `yes y | script -q /dev/null ./uninstall.sh --install-dir /tmp/cg-mac2/bin3 --repo ~/cg-mac2/repo`

Log: step4b-uninstall-y.log. Full output:

```
==> Install folder: /tmp/cg-mac2/bin3
Delete /tmp/cg-mac2/bin3 and everything in it? [y/N] y
deleted /tmp/cg-mac2/bin3

==> Repo: /Users/alanseunarayan/cg-mac2/repo
Remove the codegraph server entry from /Users/alanseunarayan/cg-mac2/repo/.mcp.json? [y/N] y
removed the codegraph entry from /Users/alanseunarayan/cg-mac2/repo/.mcp.json
Remove the codegraph server entry from /Users/alanseunarayan/cg-mac2/repo/.cursor/mcp.json? [y/N] y
removed the codegraph entry from /Users/alanseunarayan/cg-mac2/repo/.cursor/mcp.json

Done.
script: write master: Input/output error
```

It asked three times, printed each path, and removed all three. After the run: `/tmp/cg-mac2/bin3` does not exist
(`ls: /tmp/cg-mac2/bin3: No such file or directory`); `.mcp.json` and `.cursor/mcp.json` both contain
`{"mcpServers": {}}`. /tmp/cg-mac2/bin1 and bin2 were not touched. No prompt about `.codegraph/` (none existed)
and none about Codex (`codex` is not on PATH, so that part of the script did not run).

## 5. Leftover processes

`pgrep -fl "codegraph-index|codegraph-mcp|build-server|VBCSCompiler|MSBuild.dll"`, run right after step 4b:

- No codegraph-index or codegraph-mcp process.
- 10 MSBuild worker nodes (`dotnet .../sdk/10.0.302/MSBuild.dll /nodemode:1 /nodeReuse:true`, pids 43189 and
  43536-43559) started 21:14:35-21:16:31, and 1 Roslyn compiler server (`VBCSCompiler -pipename:...`, pid 43204)
  started 21:14:39. These are the .NET build servers kept for reuse by the builds of steps 2 and 3; they exit on
  their own after their idle timeout, or with `dotnet build-server shutdown`. They were left as found (not killed).
- `pgrep -fl cg-mac2`: no process has a cg-mac2 path on its command line.

## 6. What did not run

Nothing was skipped. Pushed to mac-test-results after the user agreed; ~/cg-mac2 and /tmp/cg-mac2 were deleted after that, also with the user's agreement.

## Files

- ~/cg-mac2/mac-report-2.md: this report.
- ~/cg-mac2/step1-*.log, step2-*.log, step3-symlink-test.log, step4-*.log, step4a-*.log, step4b-*.log.
- ~/cg-mac2/src (the clone, 1a9ee1c), ~/cg-mac2/repo (the init/uninstall repo), ~/cg-mac2/t (empty; the test
  script's work folder was deleted by the script).
- /tmp/cg-mac2/src1, src2, src3 (the clones), bin1, bin2 (installs; bin3 deleted by step 4b).
- Also under ~/cg-mac2 from the fix session earlier today: mac-fix-1.md, logs/, run-installs.sh; under /tmp/cg-mac2:
  src, base, bin, probe, t, dg.json.
