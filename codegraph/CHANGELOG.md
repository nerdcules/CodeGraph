# Changelog

[CodeGraph](README.md) · [Docs](docs/README.md) · [MCP server](MCP-SERVER.md) · [Extractors](EXTRACTORS.md) · [Benchmarks](BENCHMARKS.md) · [Prompts](PROMPTS.md) · **Changelog** · [Claude Code plugin](plugin/README.md) · [VS Code extension](vscode-extension/README.md)

Each release is a tag `v<version>`, built and published by the [release workflow](../.github/workflows/release.yml).
The executables report their version with `--version`; the MCP server shows it to its client.

## Unreleased

- Rows print the 8-character handle on every read path, not only while the graph is in memory. In 1.5.0 an answer
  from the database printed full ids. The first answer of a session and the answers during a reload were thus
  longer than the others.
  - Before the first snapshot loads, the server makes the handles from the id list of the database.
  - While a reload after a re-index runs, the server uses the handles of the snapshot that loaded last. A node
    that is new since that snapshot prints its full id until the reload ends.
  - No call waits for the snapshot. The 2 s wait of the first tool call is gone. The 2 s wait for an argument with
    the shape of a handle is gone too; it also delayed a name of 8 lowercase letters such as `validate`.
  - `CODEGRAPH_SNAPSHOT=off` prints full ids, as in 1.5.0.

## 1.5.0

Answers from the codegraph server are shorter, with the same facts: relative paths, 8-character handles in
place of node ids, a cap on `covered_by`, and five tools that take a name. In 24 Opus sessions on Jellyfin and Hono,
the "with" cost fell from 0.2664 to 0.1224 USD and from 0.1236 to 0.0408 USD (medians).

- `codegraph-index compare` passes `ToolSearch` in `--tools` and allows it in both conditions, so Claude Code
  defers the codegraph tool schemas and loads one when a session asks for the tool, as it does in an interactive
  session. Before, the "with" sessions got every schema in their first model call.
- File paths in answers are relative to the repository root (the git worktree that holds the graph, else the folder
  that holds `.codegraph`), and an answer with paths names the root once in its first line, after the `STALE`
  warning if there is one. Rows, `snippet` headers, `index_status` file lists and the live tools' locations all use
  it; a path outside the root stays absolute. The server instructions say to join the two before a Read.
- `covered_by` lists the nearest 50 tests by depth and then the count of the rest at each depth, as `impact_of`
  does for its tests; `impact_of` adds that count too. On HTTPX, 4 of 10 `covered_by` answers in the 19 November
  sessions were over Claude Code's 50,000-character limit and were saved to a file and read back in pieces.
- Rows print an 8-character handle in place of the node id while the graph is in memory: 40 bits of the SHA-256 of
  the id in base 32, made when the snapshot loads, the same in every answer and session. Every tool that takes an
  id accepts the handle or the id; two ids whose handles collide print in full. A C# id repeated the full name with
  every parameter type: `GraphStore.Replace`'s id is over 400 characters, on every row that names it. While a
  reload after a re-index runs, the snapshot that loaded last still resolves a handle. The first tool call of a
  server process waits up to 2 s for the graph to load, so its answer prints handles as the answers after it do.
- `impact_of`, `covered_by`, `callers_of`, `tests_for` and `snippet` take a name (`Save`, or `Service.Save`) where
  they take an id: when exactly one symbol has that exact name, the tool answers for it; otherwise it lists the
  candidates with their ids. The server instructions are shorter and say so, say that `impact_of` already lists the
  tests that reach the member, and that `index_status` is for when an answer looks wrong; a test keeps them under
  4,096 characters, Claude Code's limit. In the 19 November sessions, `find_symbol` ran 17 times in 12 sessions
  only to get ids, and `index_status` in 10 of 12.

## 1.4.0

`codegraph-index compare` measures a question with the graph and without it, usage figures are sizes only, and
the docs report four public repositories measured that way.

- `find_symbol` with `nameMatch: "Exact"` finds a qualified tail after the `/` or `:` that ends a package path,
  as well as after a `.`. Go, Python and TypeScript full names put the package before the type that way
  (`github.com/spf13/cobra/Command.Find`, `httpx._urls/URL.join`, `src/context:Context.header`), so `Command.Find`,
  `URL.join` and `Context.header` found nothing before. Found while building the answer keys for the four public
  repositories.
- `covered_by` opens "N test(s) reach X through calls (static reach: a run may execute it in fewer)" instead of
  "N test(s) transitively cover X", and its description and `impact_of`'s say that the tests are the ones that can
  reach the member. On HTTPX the list held 225 tests where 46 run the method, and the answers reported 213 or 225
  as the tests that cover it.
- The page "Why it saves tokens" is now [Measured results](docs/measured-results.md) (`docs/measured-results.md`):
  on the four public repositories, Claude Code with CodeGraph saved time and model calls, not tokens. Every link and
  nav bar points to the new name.
- [With and without](tools/eval/README.md#four-public-repositories-19-november-2026) adds 24 compare sessions on
  four public repositories (Cobra, HTTPX, Hono, Jellyfin), with a run-time list of the tests that execute each
  method. With the graph, Claude Code answered sooner on all four (median 5.5 to 20.0 seconds), made fewer model
  calls in all 12 rounds and gave a test total in all 12 answers. Each session started 8,681 tokens larger, so
  input tokens and cost were higher on all four. The graph's test totals are reach, not execution: its list held
  every test that runs the method on Cobra and Jellyfin, and on HTTPX listed 225 where 46 run it. The README,
  [Measured results](docs/measured-results.md#four-public-repositories) and
  [What is missing](docs/roadmap.md) say so.
- `codegraph-index compare "<prompt>"` asks Claude Code the same question with the codegraph server and without
  it, three rounds by default, and reports what each session's own log says: wall time, model calls, input tokens
  with cache reads, output tokens, cost, the context at the first and last model call, and tool calls, with the
  medians of each condition. Each run is a new `claude -p` session with no saved session, so no run sees another's
  context; the rounds alternate which condition goes first. Both conditions get the same settings, CLAUDE.md,
  memory and read-only tools (Claude Code's `dontAsk` mode), so no session can edit a file; a run whose server did
  not connect, or during which the working tree changed, is reported and left out of the medians. The answers are
  not scored. Tested with a stand-in `claude` in the unit tests, and twice for real on this repository (Claude Code
  2.1.328, Haiku, one round each). See [Measure it with and without](docs/usage.md#measure-it-with-and-without).
- The usage figures state sizes only. `codegraph-index usage`, the line the indexer prints after a run, the
  `usage_stats` tool and the extension's Usage item no longer say how many tokens "stayed out of the context" or
  were "saved", and the `saved tok` column is gone; the status JSON drops `tokensSaved`. That figure was the size
  of every file an answer names minus the answer, which assumes a session without the graph reads those files
  whole. The measured pair in [Measured results](docs/measured-results.md#in-a-real-session) did not: the
  context grew 18,460 tokens with CodeGraph and 21,579 without. The line now reads "answers N tokens, the files
  they name M tokens (estimated at 4 characters a token)"; the table's columns are `answer tok` and `files tok`.

## 1.3.1

- The VS Code extension uses the CodeGraph logo ([assets/logo.svg](assets/logo.svg)): as its icon in the Extensions
  view (a 256 px PNG of the logo), in the Overview when CodeGraph is not installed or there is nothing to index, and,
  in one colour, in the activity bar. The Marketplace banner colour is the logo's background.

## 1.3.0

The first full release with the [VS Code extension](vscode-extension/README.md). It came in the pre-release
1.3.0-beta.1 (17 November 2026); that section of this changelog lists what it does. GitHub's latest release moves from 1.2.0 to 1.3.0, so the install one-liners and the Claude Code
plugin now install 1.3.0, and the install scripts offer the extension without `--version`.

### Install scripts

- In a terminal, `install.sh` and `install.ps1` ask for what the command leaves out: the install folder, the
  repositories (the git repository you are in first), the assistants, the live daemon, PATH and the VS Code
  extension. The assistants are a list: arrow keys or `j`/`k` move, space selects, `a` selects all, Enter accepts.
  Claude Code is selected, and Cursor, the Gemini CLI and Codex are when their programs are found. Then the script
  shows the plan and changes nothing unless you say yes. In a terminal without cursor control (`TERM=dumb`) the
  list is numbered and you type the numbers.
  - This also asks when a repository is given on the command line, as the README one-liner does. `--yes` / `-Yes`
    asks nothing, as before; so does `CODEGRAPH_NONINTERACTIVE=1` or no terminal.
- `--vscode` / `-VSCode` installs the VS Code extension: `codegraph-vscode.vsix` from the same release, checked
  against `SHA256SUMS`, with `code --install-extension` (and `code-insiders` when it is there). With
  `--from-source` it builds the extension from the checkout. The questions offer it when `code` is found and the
  release carries it, and name the installed version. `--no-vscode` / `-NoVSCode` skips the question. A release
  without the extension (1.2.0 and earlier) stops `--vscode` before anything is installed.
- `SHA256SUMS` is downloaded first, so a wrong `--version` fails before any question.
- A running `codegraph-mcp` is offered to be stopped in a terminal, instead of only refused.
- `--add-to-path` also writes the rc file of the login shell when it does not exist yet (a new Mac has no `~/.zshrc`);
  before, nothing was written and nothing said so.
- The script ends with a list of what it did, with a mark for each part that failed.
- `CODEGRAPH_RELEASE_URL` replaces `https://github.com/nerdcules/CodeGraph/releases`, for a mirror with the same layout.
- `install.ps1` under PowerShell 7 off Windows resolved a `dotnet` link to an absolute path as a relative one
  (`/usr/bin/usr/share/dotnet/dotnet`) and stopped after the archive was unpacked.
- `uninstall.sh` and `uninstall.ps1` uninstall the extension when `code` or `code-insiders` lists it, asking first.
- Tested on Linux (18 November 2026), release 1.3.0-beta.1 served from a local mirror, the assistant answers typed
  into a pseudo-terminal, VS Code 1.146.0 with its own extensions folder:
  - `install.sh`: the questions end to end, with the repository set up and the extension installed; declining the
    plan (nothing created); a running `codegraph-mcp` declined and then stopped; `latest` without the extension
    (not offered; `--vscode` stops); piped into `bash -s` with a repository given; `TERM=dumb`; no terminal;
    `--from-source --skip-tests --vscode --yes` (built the extension and installed it).
  - `install.ps1` in PowerShell 7.7.0: the questions end to end; `-Yes -VSCode` with a repository.
  - `uninstall.sh --yes` and `uninstall.ps1 -Yes`: each uninstalled the extension.
  - On Windows 11 (windows-report-6, 18 November 2026, commit `12b8c14`), with
    [tests/install-questions.py](tests/install-questions.py) driving the scripts through ConPTY: PowerShell 7.7.0
    passed 9 of 9 cases. Windows PowerShell 5.1.26100 passed 2 of 9: run from the harness under pwsh, it inherited
    PowerShell 7's `PSModulePath` and stopped at `Get-FileHash` ("not recognized"); with `PSModulePath` cleared it
    passed 9 of 9. `install.ps1` now hashes with .NET instead of `Get-FileHash`, and the harness starts 5.1 with its
    own `PSModulePath` and checks the inherited one in a case of its own (N5).
  - windows-report-7 (commit `b302cc9`): 19 of 20 cases passed. N5 printed `checksum ok`, then `Expand-Archive`
    failed ("Cannot find an overload for ExtractToDirectory"), because 5.1 loaded `Microsoft.PowerShell.Archive`
    1.2.7 from PowerShell 7's folder instead of its own 1.0.1.0. Under Windows PowerShell 5.1, `install.ps1` and
    `uninstall.ps1` now drop PowerShell 7's module folders from `PSModulePath` for their own process before any module
    loads, as pwsh does when it starts `powershell.exe`.
  - windows-report-8 (commit `5857063`): 20 of 20 cases passed, N5 included. windows-report-9 (commit `c919b87`,
    which adds case U5: `uninstall.ps1` in 5.1 with the inherited path): 21 of 21 passed, in Windows PowerShell
    5.1.26100 and PowerShell 7.7.0 on Windows 11, with the user PATH unchanged.
  - Not tested: macOS (bash 3.2), and `install.ps1 -FromSource`.

## 1.3.0-beta.1

A pre-release, the first with the VS Code extension. GitHub's latest release stays 1.2.0, so the installers and the
Claude Code plugin keep installing 1.2.0 unless given `--version v1.3.0-beta.1` (`-Version` on Windows).

- A tag with a `-suffix` is published as a pre-release, and its release notes name the tag in the install commands.
- The extension installs its own release while GitHub's latest is older, because 1.2.0 lacks `codegraph-index
  status` and `stop`. It ranks a pre-release before its release (1.3.0-beta.1 < 1.3.0), so it offers 1.3.0 once
  that is out.

### VS Code extension

- A VS Code extension ([vscode-extension/README.md](vscode-extension/README.md)) installs CodeGraph, sets
  repositories up, and runs each graph's live daemon. It shows each graph's state in an Overview, a Graphs tree and
  the status bar.
  - It installs from the GitHub release after checking the archive against `SHA256SUMS`, and checks for a newer
    release once a day. It unpacks the release beside the install folder and then swaps the two folders, so a
    failed update leaves the old installation whole. It never replaces a folder that holds other programs (a
    `codegraph-index` on `PATH` in `~/.local/bin` is followed to its own folder, or the default folder is used).
  - A graph takes one target of each language, because an index replaces all of a language's rows. A second
    TypeScript workspace or C# solution in the same repository shows as not indexed, and indexing it asks first.
    A solution hides only the projects it lists.
  - It starts a daemon with `--idle-exit` for each graph that exists, and stops the daemons it started when the
    window closes.
  - It re-indexes on save the targets the daemon does not watch, such as an Angular front end beside a C# solution.
  - It offers `codegraph-mcp` to VS Code's chat clients, unless `.vscode/mcp.json` already names one.
  - Each release carries it as `codegraph-vscode.vsix`. It is not on the Marketplace.
- Tested: 13 unit tests, and an integration test that drives the extension inside VS Code 1.146.0 against the
  indexer built from this repository. It runs 7 steps: find the solution, index it, see an edit go stale, start the
  daemon and wait for it to index the edit, stop it, and rebuild. CI run 43972862634 (16 November 2026) passed the
  9 unit tests of that commit and all 7 steps on Linux, macOS and Windows; after the review fixes, CI run
  44033732682 (17 November 2026) passed all 13 unit tests and all 7 steps on all three.
  - An 8th step checks the MCP server the extension offers. It starts the server with the offered arguments, calls
    `graph_stats` and gets the graph's counts. It also checks that a `.vscode/mcp.json` entry withdraws the offer
    and that removing the entry brings it back.
  - An install test installs the 1.2.0 release with the extension's code, then updates it while `codegraph-mcp`
    runs from the folder: refused with nothing changed on Windows, replaced on macOS and Linux.
  - CI run 44035151104 (commit `9b623cf`, 17 November 2026) passed both on Linux, macOS and Windows, with all
    13 unit tests and all 8 steps.
- `codegraph-index status [--db PATH] [--json]` reports, in one call:
  - the graph and how it compares with the working tree: edited and deleted files, and a moved HEAD;
  - nodes and files for each language, with each language's compiler facts;
  - the daemon serving the graph;
  - the last 7 days of tool calls.

  With `--json` it is for tools; the extension reads it.
- A graph file that is not a DuckDB database fails to open at once. Before, `GraphStore.Open` retried it as if it
  were locked, every 100 ms for the whole wait: `codegraph-index status` took 5 s to say the file was damaged, and
  the test for that took 5.06 s and now takes 36 ms. On macOS CI the whole suite stalled the runner on this branch
  until this changed; a macOS bisect passed the suite when that test was left out (and when either of two others
  was), and three runs of the whole suite passed after the change.
- Both daemons answer a `status` request like `ping`, but it does not reset the idle-exit clock;
  `codegraph-index status` uses it, so the extension's poll every 15 s no longer keeps an unused daemon running.
  A daemon from 1.2.0 or earlier does not know it and is asked with `ping`.
- Index runs on one graph take turns across processes: each holds `graph.db.lock` from reading the old graph to
  swapping in the new one. Before, the daemon re-indexing C# and an import of the TypeScript rows could overlap,
  share `graph.db.tmp`, and drop rows the other had just written.
- `codegraph-index stop [--db PATH]` asks the daemon serving a graph to stop and waits until its pipe is gone. Both
  daemons, C# and the language-server one, answer a new `stop` request on the pipe. A daemon from 1.2.0 or earlier
  does not know it: `stop` says so, names its process id, exits 1 and leaves it running.

## 1.2.0

### Any MCP client

- `codegraph-index init --client <names>` writes the server's config for other assistants. `copilot` covers every
  GitHub Copilot that reads a project's servers: the CLI (`.mcp.json`), VS Code (`.vscode/mcp.json`), Visual
  Studio (`.vs/mcp.json` beside the solution) and JetBrains IDEs (`.github/mcp.json`); each also has its own name.
  The others: `cursor` (`.cursor/mcp.json`), `gemini` (`.gemini/settings.json`), `codex` (its global config,
  through `codex mcp add`, keeping hand-added env), and the AI extensions `roo` (`.roo/mcp.json`), `kilo`
  (`.kilo/kilo.json`, in opencode 1.x's shape) and `continue` (`.continue/mcpServers/codegraph.json`).
  `claude` (`.mcp.json`) stays the default; `all` writes every one. Other servers and settings in those files are
  kept; a re-run with nothing new changes nothing. `install.sh --client` and `install.ps1 -Client` pass it on.
- `codegraph-mcp --root DIR` (or `CODEGRAPH_ROOT`) finds the graph from `DIR` instead of the working directory,
  for IDEs that do not start servers in the project. VS Code and Cursor get `--root ${workspaceFolder}`; Visual
  Studio and JetBrains, which neither start servers in the project nor expand variables, get the absolute
  worktree root. A placeholder left unexpanded, as the Cursor CLI does, is ignored.
- Checked with real tool calls from the Cursor IDE 3.29 (`${workspaceFolder}` expanded), Copilot in VS Code 1.146 (agent mode, `${workspaceFolder}` expanded), the Codex
  VS Code extension 26.1111 (it starts servers in the workspace, so the global entry needs no root), the Codex CLI
  and the GitHub Copilot CLI, and the Cursor CLI (headless runs need `Mcp(codegraph:*)` allowed in
  `.cursor/cli.json`; the docs say how). Where each client starts its servers and which variables it expands was
  read from the Roo Code, Kilo Code and Continue sources, and the Visual Studio and Copilot for JetBrains docs and
  issue tracker.
- Tests guard what their framework runs around them. In C#, a test now calls its class's constructors (instance
  and static), `Dispose`/`DisposeAsync`, `IAsyncLifetime.InitializeAsync`, and methods marked `[SetUp]`,
  `[TearDown]`, `[OneTimeSetUp]`, `[OneTimeTearDown]` (NUnit), `[TestInitialize]`, `[TestCleanup]`,
  `[ClassInitialize]`, `[ClassCleanup]` (MSTest), on the class and its base classes. A run-time check found the
  gap: on release 1.1.0, 44 tests execute `GraphStore.Replace` and `impact_of` named 37 (all real); 5 of the
  missing ones reach it only through `IndexFreshnessTests`' constructor. With this change it names 42 of the 44.
  The other 2 run the indexer as a child process, which a static graph cannot see.
- A damaged graph file no longer crashes the indexer. An incremental run that cannot read the old graph rebuilds
  it from source; `--full` (which read the old file to keep other languages' rows) skips what it cannot read. Both
  say so. Before, both aborted, and the only cure was deleting the file.
- A tool call that fails says why. A missing or mistyped argument gets the problem and the tool's parameters
  ("impact_of takes: id (string, required), depth (integer, default 5)"); a damaged graph file gets the command
  that rebuilds it. Before, every one was "An error occurred invoking 'x'".
- BENCHMARKS.md round 13: a full index of this repo took 16.7–17.6 s against round 12's 16.2–16.3 s, which the
  round puts down to the graph's growth; Jellyfin's took 44.9–45.0 s by the indexer's own figure (round 12's 44.3–45.3 s did not record which figure); parity clean; and a resilience battery
  (`tools/CodeGraph.Bench/resilience.py`, 12 of 12 after the two fixes above). It found the graph file creeping
  under the live daemon, about 3 MB over the last 50 of 100 saves; fixed below. A later Linux run of the battery
  (round 15) passed 11 of 12: R4 had 8 errors, and their cause is open. The Windows re-test passed 12 of 12, and so
  did the first macOS run (round 16). R4 run on its own 26 more times, on Linux, Windows and macOS, idle and under CPU
  load, had no error (round 16).
- An incremental pass writes only the rows that changed. Before, it deleted and re-inserted every row of each
  re-walked file and project file, and all the stamps and meta. DuckDB rewrites every row group and index those
  touch, so under `--watch` the file grew with each pass and kept creeping. Over 150 saves on this repo: 1.1.0
  went from 32.3 to 51.0 MB, still rising; this build stays at 30–38 MB, with no trend. A no-op pass on Jellyfin
  now adds 2 MB instead of 47. After the passes, the graph equals a fresh full index in every table, on both
  repos. BENCHMARKS.md round 14 has the figures; `tools/CodeGraph.Bench/file-growth.sh` repeats them.
- Windows 11, checked by a Claude Code session there (BENCHMARKS.md round 15): 425 tests passed, 3 skipped; graph
  swaps under a reading server, a from-source install and the uninstall in PowerShell 7 and 5.1, and the live
  daemon all worked.
  It found four things, now fixed:
  - With only a newer SDK installed (.NET 11), the indexer died with an unhandled MSBuildLocator exception. It now
    says to install the .NET 10 SDK, or to set `DOTNET_ROLL_FORWARD=LatestMajor` for a build from source.
  - A project whose NuGet packages were never restored, such as a fresh clone, indexed incompletely with no clear
    warning when the framework still resolved. It now gets a note saying to run `dotnet restore`. A pass that
    walks no file keeps the last walk's compile errors and notes instead of writing zeros over them.
  - `resilience.py` runs on Windows (no `SIGKILL` there), and reads the node count after a stale-index warning.
  - `uninstall.ps1` under Windows PowerShell 5.1 keeps the 2-space layout `init` writes, rather than rewriting
    every line in 5.1's own layout.
  A second run there, at `d3350b3`, confirmed the restore note, the kept compile errors, the 5.1 layout and the
  battery (12 of 12 with no shim). It found two more:
  - The live TypeScript test failed on Windows: it read the daemon's log while the daemon held it open for
    writing. It now opens the log sharing read and write. The next Windows CI run got past the log and failed on
    the references: each had no enclosing member. The language server sends `file:///d%3A/…` (drive letter
    lower-case, colon encoded), which .NET read as `D:\d:\…`, and the graph's paths are `D:\…`. On Windows the
    live tools now drop the slash before the drive, upper-case it, and compare paths without case. The same
    mismatch hid every diagnostic from `check_change` and `diagnostics` on Windows and made the language server's
    start-up wait run out (66.6 s); diagnostics are now stored under the same URI form they are looked up by.
    The live TypeScript test then passed in Windows CI (run 43702208790).
  A third run there, at `af1eab5`, passed the suite (440, 1 skipped) and the battery (12 of 12), and found two more:
  - `install.ps1` read an unknown option, such as the retired `-ObsidianVault`, as a repo, and installed into the
    default folder. It now stops with "unknown option" before it downloads or writes anything.
  - The daemon's `--log` file held its progress spinner (322 of 513 lines). `--log` now turns the spinner off.
  A fourth run there, at `ef202fe`, confirmed both: unknown options stop the install in pwsh 7 and 5.1, and the
  daemon's log had no spinner frame in 47 lines.
  - `uninstall.ps1` and `uninstall.sh` name each file and folder they remove, not just "entry removed".
- The server accepts the id under the names models guess for it (`symbolId`, `nodeId`, `memberId`, and their
  snake-case forms, or `id` for the tools that take `symbol`). A Cursor agent passed `symbolId` to every tool, got
  only "An error occurred invoking", and answered without the graph.
- [tools/eval](tools/eval/README.md): four assistants (Claude Code, Codex CLI, Copilot CLI, Cursor CLI), three runs
  each with and without CodeGraph, on a copy of this repo without its docs, scored against a run-time answer key.
  With CodeGraph 7 of 12 answers gave a test total (each 42, the graph's count; 44 tests execute the method);
  without it none did. Claude Code was faster and cheaper with it; Codex and the Copilot CLI were not, and
  Cursor's runs varied too much to call. Summary in
  [Measured results](docs/measured-results.md#four-assistants-with-and-without).
- The server accepts an id with the brackets its answers print around ids (`[M:App.Run]`): a Cursor agent
  copied them and got "no node with id". The server's instructions now say to drop them.
- `find_symbol` with `nameMatch: Exact` also takes a qualified tail of the full name without its parameter list:
  `GraphStore.Replace` finds `CodeGraph.Core.GraphStore.Replace(bool, …)`. A Cursor agent tried exactly that,
  found nothing, and fell back to two broader searches. The in-memory and SQL paths agree (parity tests).
- The uninstall scripts remove the entry from every config `init` can write, and from Codex.
- The README and docs say "your AI coding assistant" rather than Claude Code alone; the usage page lists each
  assistant's instructions file. [Tested with](docs/installation.md#tested-with) lists the assistants that
  answered with real tool calls, and [docs/client-tests](docs/client-tests/README.md) has the configuration, tool
  calls and answer of each run.
- The promo video (118.6 s) says "your AI coding assistant", and a new card lists the assistants it was tested with.
  The on/off sessions are still Claude Code. Its token figures are now the two measured sessions (180,134 against
  459,907 input tokens), not the README's example week or the size of the matching files.
- [Measured results](docs/measured-results.md#in-a-real-session) adds the measured sessions: 42 s against
  87 s, 4 model calls against 9, and a context that grew by about the same amount. The README no longer says "one
  tool call instead of twenty file reads".

### Obsidian, retired for now

- The Obsidian plugin and export are retired for now. Releases no longer include `codegraph-obsidian.zip`,
  `install.sh` and `install.ps1` no longer take `--obsidian-vault` / `-ObsidianVault`, CI no longer runs the
  plugin's tests, the CLI help no longer lists `export obsidian`, and no product page links to it. The code stays in
  the repo: `codegraph-index export obsidian` still runs, its .NET tests still run with the suite, the corpus still
  checks the export, and its pages are kept, marked retired.

### Build from a path through a symlink

- A checkout reached through a symlink builds once. The OS reports the working directory of a process with symlinks
  resolved, and on macOS `/tmp` is a symlink to `/private/tmp`. MSBuild tasks resolve a relative item against that
  directory, so `dotnet build /tmp/.../CodeGraph.slnx` restored and built `CodeGraph.Core` and `CodeGraph.Indexer`
  twice, once per spelling, and `install.sh --from-source` failed at random (CS0006, CS0103). The same build put
  the test fixtures in the output root instead of `Fixtures/` and embedded the web UI as `CodeGraph.Mcp.index.html`
  instead of `CodeGraph.Mcp.wwwroot.index.html`. Found by the first macOS run (BENCHMARKS round 16), and shown on
  Linux too: through a symlink, the suite at `a3150ec` ran 421 of 441 tests and 331 failed; with the fix, 439 passed
  and 2 skipped. A second Mac run at `1a9ee1c` installed from source through `/tmp` 3 times in 3.
  `Directory.Build.targets` now makes each project reference, embedded resource and
  copied None or Content item absolute, with the spelling of the solution; `install.sh` and
  `tools/release/package.sh` resolve their root with `pwd -P`; and `tests/install-through-symlink.sh` runs restore,
  a Release build and the install through a symlink, in the macOS and Linux CI jobs.

## 1.1.0

### Faster reads

- The MCP server and the web view hold the graph in memory (`GraphSnapshot`): node records and adjacency arrays
  in both directions, loaded in the background at start and after every re-index. Finds, walks, tests, coverage,
  dead code, paths and subgraphs answer from it; the database is opened only for full-text search, vectors and
  metadata. Until a snapshot matching the file has loaded, calls read through DuckDB as before.
  On Jellyfin (21,174 nodes), through the server at a model's pace: `impact_of` depth 20 from 860 ms to 9.7 ms,
  `covered_by` depth 30 from 1,173 ms to 5.1 ms, `find_symbol` from 42 ms to 8.8 ms. `CODEGRAPH_SNAPSHOT=off`
  turns it off; `index_status` says which path answers.
- The recursive SQL stays as the reference: parity tests run every read both ways over random graphs, and
  `codegraph-bench --parity N` does the same over a real graph (0 differences in 6,096 checks on Jellyfin).
  SQL orderings now break ties by id, so both answer in one defined order.
- The freshness check reads HEAD from `.git` (loose refs, `packed-refs`, linked worktrees) instead of starting
  `git rev-parse` on most calls; it falls back to git for anything unusual, such as a reftable repository.
- `codegraph-bench` times every query through DuckDB and from the snapshot, and with `--server` times real
  tool calls through one or more `codegraph-mcp` builds over stdio.

### Docs

- Every figure re-measured on 14 November 2026 ([BENCHMARKS.md](BENCHMARKS.md), round 12): this repo is now
  3,119 nodes and 405 tests, the README's question answers 12 dependents and 37 tests (`impact_of` 1.8 ms; with `find_symbol` first, 3.3 ms), with
  about 5,500 tokens of answers against about 123,000 for the files that match. A save reaches a fresh answer in 0.9 to 1.3 seconds on this repo, not
  "well under a second" as the docs said: the graph doubled since that was measured.
- New Obsidian screenshots; redrawn live-daemon and index-time charts; an install diagram that shows the release
  download rather than the build from source; a corpus chart that no longer cuts off the Rust line. Jellyfin,
  NestJS, RealWorld and the corpus's large profile were measured again for the performance page.
- `tools/corpus/run.py` mounts whichever of `GOBIN` and `GOPATH/bin` holds scip-go. Under mise, `GOBIN` is Go's own
  bin and every Go run failed with "scip-go is not on PATH".
- `tools/obsidian-e2e/screenshots.py` approves the repo's `.mcp.json` entry, as `run_e2e.sh` does. Without it
  every panel in the screenshots said `spawn codegraph-mcp ENOENT` and the run stopped at Find symbol.
- The promo video shows the new figures. Its two sessions were run again headless (`claude -p`, Claude Opus 5.5, effort
  high, Auto mode) and are replayed from their transcripts: with CodeGraph, 42 s, 4 API calls, 180,134 tokens processed and
  exactly 37 tests; without, 87 s, 9 calls, 459,907 tokens and "about 40". The README's GIF is the first of them.

## 1.0.0

The first packaged release. Everything below was built between August and November 2026.

### Install

- Prebuilt archives for Linux, macOS and Windows on x64 and arm64, each with its own .NET runtime, the
  TypeScript extractor and its packages. `install.sh` and `install.ps1` download the one for your platform,
  verify its checksum and set up a repository; `--from-source` builds a checkout as before.
- The Obsidian plugin ships as `codegraph-obsidian.zip`.
- `codegraph-index init` writes the repository's `.mcp.json`, ignores `.codegraph/` and builds the first graph.

### Graph

- C# through Roslyn, in process; TypeScript, JavaScript and Angular through the TypeScript compiler; Java,
  Kotlin, Scala, Go, Rust and Python through their SCIP indexers. One graph holds several languages.
- Calls, references, inheritance, implementation, overrides, tests, project dependencies and framework
  entry points as compiler-resolved edges. A call to an interface member counts for every implementation.
- Incremental re-indexing by file hash; a full rebuild swaps a fresh file in with one rename.
- Each language records its compiler, the language versions the code uses and the errors it hit, so
  `index_status` says when a repository is newer than the compiler that read it.

### Tools

- 35 MCP tools over stdio: find, callers, callees, impact, tests, dead code, full-text and semantic search,
  graph statistics and usage statistics.
- Every answer is checked against the working tree first and marked `STALE` where it is behind.
- Walk answers are capped at the nearest 200 rows per depth, with a count of the rest.

### Live

- `codegraph-index --watch` keeps the compiled solution in memory, re-indexes a save in well under a second
  and answers `check_change`, `rename_preview`, `diagnostics`, `live_references` and `data_flow` over a pipe.
- For TypeScript, Java, Go, Rust and Python the same tools go through the language server when one is on PATH.

### Tested

- 390 .NET tests, 11 extractor tests and the Obsidian plugin's tests run on Linux, macOS and Windows on each push
  to `main` and on each pull request.
- A generated corpus in eight languages, with an answer key, scores precision and recall at 100% on current
  toolchains in a modern and a classic dialect, and on older SDK images in Docker (.NET 8, Node 20, JDK 17,
  Go 1.21, Rust 1.70, Python 3.8). [BENCHMARKS.md](BENCHMARKS.md) has every number.

---

[CodeGraph](README.md) · [Docs](docs/README.md) · [MCP server](MCP-SERVER.md) · [Extractors](EXTRACTORS.md) · [Benchmarks](BENCHMARKS.md) · [Prompts](PROMPTS.md) · **Changelog** · [Claude Code plugin](plugin/README.md) · [VS Code extension](vscode-extension/README.md)
