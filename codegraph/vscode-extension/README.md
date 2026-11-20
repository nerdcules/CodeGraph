# CodeGraph for VS Code

[CodeGraph](../README.md) · [Docs](../docs/README.md) · [MCP server](../MCP-SERVER.md) · [Extractors](../EXTRACTORS.md) · [Benchmarks](../BENCHMARKS.md) · [Prompts](../PROMPTS.md) · [Changelog](../CHANGELOG.md) · [Claude Code plugin](../plugin/README.md) · **VS Code extension**

CodeGraph gives your AI coding assistant the compiler's answers: who calls this, what breaks if I change it, which
tests guard it. This extension installs CodeGraph, builds each repository's graph, and runs the live daemon that keeps
the graph fresh. It also shows how each graph stands, in a sidebar and in the status bar.

![The Overview view of this repository's graph, in the Dark Modern and Light Modern colours](media/screenshot-overview.png)

*The Overview of this repository's graph at commit `63f502c`, with its daemon running. It was drawn from the output
of `codegraph-index status --json` by the extension's own page script and stylesheet, in a browser, with the colour
tokens of VS Code's Dark Modern and Light Modern themes.*

## Contents

- [What it does](#what-it-does)
- [Install](#install)
- [First run](#first-run)
- [The views](#the-views)
- [The live daemon](#the-live-daemon)
- [GitHub Copilot and other chat clients](#github-copilot-and-other-chat-clients)
- [Commands](#commands)
- [Settings](#settings)
- [What it sends over the network](#what-it-sends-over-the-network)
- [Tested](#tested)
- [Build it from source](#build-it-from-source)

## What it does

- **Installs and updates CodeGraph.** It downloads the release archive for your machine from GitHub and checks it
  against the release's `SHA256SUMS`. It unpacks it beside the install folder and then swaps the two folders, so a
  failed update leaves the old installation whole. On Windows the install folder is a junction to a folder for each
  release, and an update points the junction at the new one. It never replaces a folder that holds other programs,
  such as `~/.local/bin`. Once a day it checks for a newer release.
- **Finds what to index.** It finds the C# solutions, TypeScript and Angular workspaces, Maven and Gradle builds,
  Python projects, Go modules and Rust crates in the workspace. It groups them by the graph they feed: one graph
  for each git worktree. A solution hides the projects it lists; a project it does not list stays a target.
- **One target per language.** Indexing a target replaces every row of its language in the graph. So a graph
  takes one target of each language: the one it was built from, else the daemon's, else a solution. A second
  TypeScript workspace or C# solution in the same repository shows as **not indexed**. Indexing it replaces the
  first one's rows, after you confirm.
- **Sets a repository up.** **Set Up Repository** runs `codegraph-index init` with the assistants you pick.
  It writes their MCP configuration and a `.gitignore` entry, then indexes.
- **Runs the live daemon.** One daemon for each graph (`codegraph-index --watch`). It starts and stops the daemon
  and shows its state, memory and last index.
- **Keeps the other targets fresh.** One daemon serves one graph, so in a repository with C# and an Angular front
  end, the daemon watches the solution. A save in the front end re-indexes the front end.
- **Shows each graph's state.** Fresh or stale and why, nodes, edges and files for each language, compile errors,
  and what the graph answered this week.
- **Says what is missing.** A target whose tool is not on `PATH` says which tool, and how to install it: the .NET
  SDK, Node.js, scip-java, scip-python, scip-go or rust-analyzer.

## Install

The extension is not on the Visual Studio Marketplace or Open VSX yet. Releases from 1.3.0 on carry it as
`codegraph-vscode.vsix` (the pre-release 1.3.0-beta.1 did too); 1.2.0 and earlier do not have it.

The install scripts offer it when `code` is on `PATH` and the release carries it, or install it with `--vscode`
(`-VSCode` on Windows). They download it from the same release, check it against `SHA256SUMS`, and run
`code --install-extension`:

```bash
curl -fsSL https://raw.githubusercontent.com/nerdcules/CodeGraph/main/codegraph/install.sh | bash -s -- --vscode
```

Or download it and install it yourself:

```bash
curl -fsSLO https://github.com/nerdcules/CodeGraph/releases/latest/download/codegraph-vscode.vsix
code --install-extension codegraph-vscode.vsix
```

Or, in VS Code: **Extensions** › **⋯** › **Install from VSIX…**, and pick the file.

The extension needs VS Code 1.101 or later, and a CodeGraph that has `codegraph-index status`: 1.2.0 and earlier do
not, so the views show an error until you update.

## First run

1. Open a repository. The CodeGraph icon appears in the activity bar.
2. If CodeGraph is not installed, choose **Install CodeGraph**. It installs into `~/.codegraph/bin` on macOS and
   Linux, and `C:\tools\codegraph` on Windows: the same folders `install.sh` and `install.ps1` use. If
   `codegraph-index` is already on `PATH`, the extension uses that one, following a link to the folder it points
   into. An update writes to that folder too. If the folder holds other programs, the update installs into the
   default folder instead and sets `codegraph.installDir` to it.
   On Windows a folder that a program runs from cannot be moved. Thus the install folder is a junction to a folder
   for each release, in `.codegraph.versions` beside it. An update unpacks the new release into a new folder and
   points the junction at it. MCP servers and daemons that run keep the old release until they restart, and a later
   update deletes the old folder. Only the first update of a real folder, from before this layout, must move it. If
   an MCP server or a daemon still runs from that folder, the update stops before it changes anything and says so.
3. Choose **Set up repository…** and pick your assistants. Claude Code is selected by default. VS Code's own chat
   gets the server from the extension (see [below](#github-copilot-and-other-chat-clients)).
4. When the index is done, choose **Start Live Daemon**.

## The views

- **Overview** (the top of the CodeGraph sidebar) is a dashboard for one graph at a time. It shows whether the
  graph is up to date, with nodes, edges and files, and when and at which commit it was built. Below that come the
  daemon, the files changed since indexing, the languages with their share of the nodes, and this week's tool calls.
  Its buttons index, rebuild, set up, and start or stop the daemon. With more than one graph in the workspace, tabs
  switch between them.
- **Graphs** is a tree with each graph's daemon, changed files, languages, targets and usage. Hover over an entry
  for the details. The buttons on each row run the same commands as the Overview.
- **The status bar** shows the graph of the file you are editing: a tick when it is up to date, a warning and the
  number of changed files when it is not, and a pulse when its daemon is watching. Click it to open the sidebar.

All three use the colours of your theme, so they work in light, dark and high-contrast themes.

## The live daemon

The extension starts `codegraph-index <target> --watch --idle-exit <minutes>` for each graph that exists when the
workspace opens (`codegraph.daemon.autoStart`). It writes the log to `.codegraph/daemon.log`, the same file the MCP
server's launcher uses.

- A daemon holds the loaded solution in memory. On this repository its working set was 217 MB (16 November 2026). The extension stops the daemons
  it started when the window closes. A daemon stops itself after `codegraph.daemon.idleExitMinutes` (120) with no
  save and no tool call. The extension reads each graph's status every 15 seconds while the window has focus. Those
  reads use a `status` request that does not count as use, so they never keep an idle daemon running.
- One daemon serves each graph; a second one would fight the first for the graph file and the pipe. If a daemon is
  already running, started from a terminal or by the MCP server, the extension shows it and leaves it running.
- **Stop** asks the daemon to stop over its pipe (`codegraph-index stop`) and waits until it has gone.
- The daemon watches its own target. A save in another target of the same graph, such as the Angular front end
  beside a C# solution, re-indexes that target one and a half seconds after the last save
  (`codegraph.reindexOnSave`). Without a daemon, the daemon's target is not re-indexed on save, because each run
  would load the whole solution again. The graph shows as stale instead, with **Index now**.

## GitHub Copilot and other chat clients

For each graph, the extension offers `codegraph-mcp --root <worktree>` to VS Code's chat as an MCP server called
**CodeGraph**. GitHub Copilot's agent mode can use it without a `.vscode/mcp.json`. If `.vscode/mcp.json` already has
a `codegraph` server, which `codegraph-index init --client vscode` writes, the extension does not offer a second
one. Turn it off with `codegraph.registerMcpServer`.

## Commands

| Command | What it does |
|---|---|
| CodeGraph: Install or Update CodeGraph | Downloads and installs the latest release after checking it against `SHA256SUMS`; while the extension is newer than that release, its own release instead |
| CodeGraph: Set Up Repository… | `codegraph-index init <target> --client <your picks>` |
| CodeGraph: Index Now | `codegraph-index <target>` for the graph's target of each language, only the files that changed |
| CodeGraph: Rebuild Graph | The same with `--full` for C# |
| CodeGraph: Start / Stop / Restart Live Daemon | `codegraph-index --watch`, `codegraph-index stop` |
| CodeGraph: Show Daemon Log | Opens `.codegraph/daemon.log` at its end |
| CodeGraph: Show Usage | `codegraph-index usage` for the last 7 and 30 days |
| CodeGraph: Refresh | Searches the workspace again and reads each graph's status |
| CodeGraph: Show Output | Every command the extension ran, with its output |

## Settings

| Setting | Default | Meaning |
|---|---|---|
| `codegraph.installDir` | empty | The folder with `codegraph-index` and `codegraph-mcp`. When it is empty, the extension uses `codegraph-index` on `PATH`, else the installers' folder. |
| `codegraph.daemon.autoStart` | `true` | Start a daemon for each graph that exists when the workspace opens. |
| `codegraph.daemon.idleExitMinutes` | `120` | A daemon the extension starts stops after this long unused. |
| `codegraph.reindexOnSave` | `true` | Re-index the targets no daemon watches when you save one of their files. |
| `codegraph.registerMcpServer` | `true` | Offer `codegraph-mcp` to VS Code's chat clients. |
| `codegraph.checkForUpdates` | `true` | Check GitHub for a newer release once a day. |
| `codegraph.clients` | `["claude"]` | The assistants selected at first in **Set Up Repository**. |

## What it sends over the network

The extension makes these requests and no others:

- the update check: one request a day to `api.github.com/repos/nerdcules/CodeGraph/releases/latest` (a pre-release
  extension installs its own release while that one is older);
- an install or update: the archive and `SHA256SUMS` from `github.com/nerdcules/CodeGraph/releases`.

The indexer and the MCP server run on your machine, and the graph stays in `.codegraph/`.

## Tested

- **Unit tests** (`npm test`): 13 tests of the parts without VS Code. They cover target discovery, the projects a
  `.sln` or `.slnx` lists, the one target of each language a graph takes, which folders an update may replace,
  release asset names, checksums, version order, status parsing and formatting.
- **Integration test** (`npm run test:integration`): VS Code 1.146.0 starts with the extension on a copy of a small
  C# solution and runs 8 steps against a real `codegraph-index` built from this repository. The extension finds the
  solution and indexes it. It offers one MCP server for the graph: the test starts it with the offered arguments,
  calls `graph_stats` over stdio, and gets the graph's own counts. A `codegraph` entry in `.vscode/mcp.json`
  withdraws the offer and removing it brings it back. Then the extension sees an edit make the graph stale, starts
  the daemon and waits for it to index the edit, stops it, and rebuilds the graph.
  - CI run 43972862634 (commit `3ae5ace`, 16 November 2026) passed that commit's 9 unit tests and the 7 steps it had,
    on Linux, macOS and Windows.
  - CI run 44033732682 (commit `e3de08c`, 17 November 2026) passed all 13 unit tests and the same 7 steps on all three.
  - CI run 44035151104 (commit `9b623cf`, 17 November 2026) passed all 13 unit tests and all 8 steps, including
    the MCP step, on all three.
- **Install test** (`CODEGRAPH_TEST_INSTALL_RELEASE=1 npm run test:install`): installs the 1.2.0 release with the
  extension's own code (download, checksum, unpack, swap), then updates it while `codegraph-mcp` runs from the
  folder. On macOS and Linux the update must replace the folder under the running program. On Windows it must
  point the junction at a new release folder and keep the old one whole while the program runs. With the program
  stopped, a second update must go through everywhere, and on Windows delete the old release folder. On Windows a
  real folder from before the junction layout must be refused while a program runs from it, unchanged, and become a
  junction when nothing runs. It downloads the release archive (89 to 115 MB), so it runs only when asked. CI run
  44035151104 (commit `9b623cf`) passed the earlier version of this test on all three systems. On 19 November 2026
  the junction version passed on Windows 11 in 36.2 s. A folder holding another program is refused, and the
  junction layout installs, updates and deletes old releases on every system (unit tests).
- **Not tested yet:**
  - An update from a published release that carries the extension: the first release with it will be the first.
  - GitHub Copilot listing and calling the server; CI cannot sign in to Copilot. To check it by hand, open a
    repository that is set up in VS Code with Copilot signed in. Run **MCP: List Servers**, check that **CodeGraph**
    is listed and starts, then ask agent mode a question about the code (for example, who calls a method).

## Build it from source

```bash
cd codegraph/vscode-extension
npm ci
npm test                      # compile and run the unit tests
CODEGRAPH_TEST_INSTALL_DIR=../src/CodeGraph.Indexer/bin/Release/net10.0 \
CODEGRAPH_TEST_MCP=../src/CodeGraph.Mcp/bin/Release/net10.0/codegraph-mcp npm run test:integration
CODEGRAPH_TEST_INSTALL_RELEASE=1 npm run test:install   # downloads the 1.2.0 release archive
npm run package               # writes ../dist/codegraph-vscode.vsix
```

The integration test downloads VS Code into `.vscode-test/`, or uses the program named by `CODEGRAPH_TEST_VSCODE`.
On Linux without a display, run it under `xvfb-run -a`. Run it from a terminal outside VS Code, or unset
`ELECTRON_RUN_AS_NODE` first.

---

[CodeGraph](../README.md) · [Docs](../docs/README.md) · [MCP server](../MCP-SERVER.md) · [Extractors](../EXTRACTORS.md) · [Benchmarks](../BENCHMARKS.md) · [Prompts](../PROMPTS.md) · [Changelog](../CHANGELOG.md) · [Claude Code plugin](../plugin/README.md) · **VS Code extension**
