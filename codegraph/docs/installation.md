# Installation

*Author: Alan Seunarayan*

[Home](README.md) · **Installation** · [Usage](usage.md) · [Obsidian](obsidian.md) · [Use cases](use-cases.md) · [How it works](how-it-works.md) · [Performance](performance.md) · [Architecture](architecture.md)

---

## Contents

- [Before you start](#before-you-start)
- [Install CodeGraph](#install-codegraph)
- [Install the language tools](#install-the-language-tools)
- [Set up more repositories](#set-up-more-repositories)
- [Connect the client](#connect-the-client)
- [Make sure that it works](#make-sure-that-it-works)
- [Install as a Claude Code plugin](#install-as-a-claude-code-plugin)
- [Install by hand](#install-by-hand)
- [Upgrade](#upgrade)
- [Build from source](#build-from-source)
- [Remove CodeGraph](#remove-codegraph)
- [Troubleshooting](#troubleshooting)

## Before you start

CodeGraph is a prebuilt program.
You do not have to build it, and you do not have to install .NET to run it.
The release archive holds its own runtime.

You must have these items for the languages that you index:

| Item | Version | Necessary for |
|---|---|---|
| git | any | All languages. The indexer finds the worktree root with git. |
| .NET SDK | the one that your projects build with | C#. MSBuild loads the projects, so the SDK must be there, as for `dotnet build`. |
| Node.js | 22.22 or later | TypeScript, JavaScript and Angular. |
| A language indexer | see below | Java, Kotlin, Scala, Go, Rust and Python. |

The repositories that you index can be on older versions.
A project on .NET 8, a `tsconfig` from the TypeScript 2 era, or a `go.mod` that says `go 1.18` indexes in full.
[Language versions](language-versions.md) gives the versions that each route reads.

Releases exist for Linux and macOS on x64 and arm64, and for Windows on x64 and arm64.
For Alpine Linux (musl), [build from source](#build-from-source).

## Install CodeGraph

![The install script downloads the release, checks it, and then sets up each repository](images/install-flow.svg)

### On macOS or Linux

1. Open a terminal.
2. Run the install command with the path of your repository:

   ```bash
   curl -fsSL https://raw.githubusercontent.com/nerdcules/CodeGraph/main/codegraph/install.sh | bash -s -- ~/dev/App --add-to-path
   ```

3. Open a new terminal, so that the new PATH is active.

The script installs the executables in `~/.codegraph/bin`.

### On Windows

1. Open PowerShell.
2. Run the install command with the path of your repository:

   ```powershell
   & ([scriptblock]::Create((irm https://raw.githubusercontent.com/nerdcules/CodeGraph/main/codegraph/install.ps1))) C:\dev\App -AddToPath
   ```

3. Open a new PowerShell window, so that the new PATH is active.

The script installs the executables in `C:\tools\codegraph`.
PowerShell 7 can also run `install.ps1` on macOS and Linux.

### What the script does

1. It finds your platform and downloads the archive for it from the [latest release](https://github.com/nerdcules/CodeGraph/releases/latest).
2. It checks the SHA-256 checksum of the archive against `SHA256SUMS` from the same release.
3. It unpacks the archive into the install folder.
4. It makes sure that both executables start.
5. It runs `codegraph-index init` on each repository that you give.
6. With `--obsidian-vault`, it downloads the Obsidian plugin from the same release and installs it into that vault.

### Script options

| bash | PowerShell | Function |
|---|---|---|
| `<repo>...` | `<repo>, ...` | Set up these repositories. With no repository, the script asks for one. |
| `--version vX.Y.Z` | `-Version vX.Y.Z` | Install this release. The default is the latest release. |
| `--install-dir DIR` | `-InstallDir DIR` | Install in a different folder. |
| `--add-to-path` | `-AddToPath` | Add the install folder to the user PATH. |
| `--live` | `-Live` | Let the server start the live daemon itself. |
| `--obsidian-vault DIR` | `-ObsidianVault DIR` | Also install the Obsidian plugin into this vault. See [Obsidian](obsidian.md). |
| `--from-source` | `-FromSource` | Build a checkout instead of downloading. See [Build from source](#build-from-source). |
| `--force` | `-Force` | Stop running `codegraph-mcp` processes before the copy. |

## Install the language tools

Install only the tools for the languages that you use.
C# needs only the .NET SDK that the projects build with.
The TypeScript extractor comes in the archive; it needs Node.js on PATH.

| Language | Indexer | Install command |
|---|---|---|
| Java, Kotlin, Scala | scip-java | `cs install --contrib scip-java` (coursier) |
| Go | scip-go | `go install github.com/scip-code/scip-go/cmd/scip-go@latest` |
| Rust | rust-analyzer | `rustup component add rust-analyzer` |
| Python | scip-python | `npm install -g @sourcegraph/scip-python` |

The indexer finds a tool on PATH.
It also looks in the folders that coursier, `go install` and cargo use.
Thus you do not have to add these folders to PATH.

> **NOTE:** Java also needs a JDK and Maven or Gradle.
> scip-java runs the build of the project to read the code.

The live tools for a language also need a language server.
Install it if you want the live tools:

| Language | Language server |
|---|---|
| Java | jdtls |
| Go | gopls |
| Python | pyright |
| Rust | rust-analyzer (the same tool) |
| TypeScript, JavaScript | typescript-language-server (in the archive) |

## Set up more repositories

You can set up a repository at any time after the installation.

1. Go to the repository:

   ```bash
   cd ~/dev/Other
   ```

2. Run `init`:

   ```bash
   codegraph-index init
   ```

`init` finds the project from the current folder.
It looks for a `.slnx` or `.sln` file first.
It then looks for `angular.json`, `tsconfig.json` or a build file such as `pom.xml`, `go.mod`, `Cargo.toml` or `pyproject.toml`.
If it finds two candidates, it stops and shows both.
Then give the one that you want:

```bash
codegraph-index init App.slnx
```

`init` does these steps:

1. It adds the `codegraph` server to `.mcp.json` at the worktree root. It keeps the other servers.
2. It starts the server as the client does, to make sure that it can.
3. It adds `.codegraph/` to `.gitignore`.
4. It builds the first graph.

In a repository with a C# solution and an Angular folder, `init` indexes both into one graph.

| Option | Function |
|---|---|
| `--live` | Add `CODEGRAPH_DAEMON=auto`. The server then starts the live daemon on its first tool call. |
| `--no-index` | Write the configuration only. Do not build the graph. |
| `--db PATH` | Keep the graph at a different path. |

## Connect the client

`init` writes the configuration for you.
To write it yourself, put this `.mcp.json` file at the root of the repository:

```json
{
  "mcpServers": {
    "codegraph": {
      "command": "/home/you/.codegraph/bin/codegraph-mcp"
    }
  }
}
```

Do not put the path of the graph in the entry.
The server finds `.codegraph/graph.db` from its working folder.
Thus one `.mcp.json` file serves all the worktrees of a repository.

## Make sure that it works

1. Start Claude Code in the repository.
2. Approve the `codegraph` server when Claude Code asks.
3. Type `/mcp` and make sure that `codegraph` shows as connected.
4. Ask Claude Code: "Use codegraph graph_stats."
5. Make sure that the answer shows a node count and an edge count.

If the answer says "no code graph", build the graph:

```bash
codegraph-index init
```

## Install as a Claude Code plugin

On macOS and Linux, the plugin installs CodeGraph for you.
It adds the server to every session, and a skill that tells Claude when to use the tools.

1. In Claude Code, add the marketplace and install the plugin:

   ```
   /plugin marketplace add nerdcules/CodeGraph
   /plugin install codegraph@codegraph
   ```

2. Open a repository and ask a question. On the first call, the plugin downloads the release into `~/.codegraph/bin`.
3. When a tool answers "no code graph", let Claude run `codegraph-index init`, or run it yourself.

On Windows, install with `install.ps1` first.
The plugin then uses that install.
[plugin/README.md](../plugin/README.md) gives the details.

## Install by hand

1. Download the archive for your platform from the [latest release](https://github.com/nerdcules/CodeGraph/releases/latest).
2. Compare its checksum with the line in `SHA256SUMS`.
3. Unpack it into a folder, for example `~/.codegraph/bin`.
4. On macOS, if you downloaded with a browser, clear the quarantine flag:

   ```bash
   xattr -dr com.apple.quarantine ~/.codegraph/bin
   ```

5. Run `codegraph-index init` in a repository.

## Upgrade

1. Close the Claude Code sessions that use CodeGraph.
2. Run the install command again.

> **CAUTION:** A running `codegraph-mcp` holds its files open.
> The script stops if the server runs.
> Close the sessions, or give `--force` to stop the server.

The plugin updates with `/plugin update codegraph@codegraph`.

## Build from source

You need the .NET 10 SDK, git, and a clone of the repository.
Node.js adds the TypeScript extractor.

```bash
cd codegraph
./install.sh --from-source ~/dev/App --add-to-path
```

```powershell
cd codegraph
.\install.ps1 -FromSource C:\dev\App -AddToPath
```

The script builds with warnings as errors, runs the tests, and publishes the executables for your platform.
[Contributing](contributing.md) tells how to run from a checkout without publishing.

## Remove CodeGraph

1. Close the Claude Code sessions that use CodeGraph.
2. Run the uninstall script:

   ```bash
   curl -fsSL https://raw.githubusercontent.com/nerdcules/CodeGraph/main/codegraph/uninstall.sh | bash -s -- --repo ~/dev/App
   ```

   ```powershell
   & ([scriptblock]::Create((irm https://raw.githubusercontent.com/nerdcules/CodeGraph/main/codegraph/uninstall.ps1))) -Repo C:\dev\App
   ```

The script removes the install folder and the PATH entry.
For each repository that you give, it removes the `codegraph` entry from `.mcp.json`.
It also removes the `.codegraph/` folder of that repository.
It asks before each removal.
Give `--yes` or `-Yes` to remove without questions.

## Troubleshooting

| Problem | Cause | Remedy |
|---|---|---|
| The tools say "no code graph at ...". | The repository has no graph yet. | Run `codegraph-index init` in the repository. |
| The indexer says "scip-java is not on PATH". | The language indexer is not installed. | Install the indexer from the table in [Install the language tools](#install-the-language-tools). |
| The indexer reports hundreds of errors on a C# project. | The SDK that the project builds with is not installed, or the project is not restored. | Install that SDK, run `dotnet restore`, and index again. `index_status` shows the first error. |
| The install script stops with "in use". | `codegraph-mcp` runs. | Close the sessions, or give `--force`. |
| macOS says the program is damaged or cannot be opened. | The archive came from a browser and is quarantined. | Run `xattr -dr com.apple.quarantine` on the install folder, or install with the script. |
| The server does not start in the client after a source build. | The client cannot find .NET. | Run `codegraph-index init` again. It adds `DOTNET_ROOT` when necessary. A release build does not need .NET. |
| Answers show `STALE`. | Files changed after the last index. | Run the indexer again, or start the live daemon. See [Usage](usage.md#keep-the-graph-current). |

---

← Previous: [Home](README.md) · Next: [Usage](usage.md) →
