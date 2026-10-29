#!/usr/bin/env bash
# Builds, tests, publishes and installs codegraph-index and codegraph-mcp (macOS / Linux).
#
#   ./install.sh [--install-dir DIR] [--skip-tests] [--add-to-path] [--force]
#
# Default install dir: ~/.codegraph/bin. Re-run to upgrade; a running codegraph-mcp is stopped only with --force.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
install_dir="$HOME/.codegraph/bin"
skip_tests=false
add_to_path=false
force=false

while [ $# -gt 0 ]; do
    case "$1" in
        --install-dir) install_dir="$2"; shift 2 ;;
        --skip-tests)  skip_tests=true; shift ;;
        --add-to-path) add_to_path=true; shift ;;
        --force)       force=true; shift ;;
        -h|--help)     sed -n '2,6p' "$0"; exit 0 ;;
        *) echo "unknown option: $1" >&2; exit 1 ;;
    esac
done

step() { printf '\n\033[36m==> %s\033[0m\n' "$1"; }

command -v dotnet >/dev/null || { echo 'dotnet SDK not found on PATH. Install .NET 10 from https://dotnet.microsoft.com/download' >&2; exit 1; }

step 'Building (warnings are errors)'
dotnet build "$root/CodeGraph.slnx" -c Release -warnaserror --nologo

if [ "$skip_tests" = false ]; then
    step 'Running tests'
    dotnet test "$root/CodeGraph.slnx" -c Release --no-build --nologo
fi

# A running server keeps its files open; overwriting them under it is undefined.
running=$(pgrep -f "$install_dir/codegraph-(mcp|index)" || true)
if [ -n "$running" ]; then
    if [ "$force" = true ]; then
        step 'Stopping running codegraph processes'
        kill $running || true
        sleep 1
    else
        echo "codegraph is running from $install_dir (pids: $running). Close the Claude Code sessions using it, or re-run with --force." >&2
        exit 1
    fi
fi

step "Publishing to $install_dir"
mkdir -p "$install_dir"
for project in src/CodeGraph.Indexer src/CodeGraph.Mcp; do
    dotnet publish "$root/$project" -c Release --no-build -o "$install_dir" --nologo
done
chmod +x "$install_dir/codegraph-index" "$install_dir/codegraph-mcp"

if [ "$add_to_path" = true ]; then
    case ":$PATH:" in
        *":$install_dir:"*) ;;
        *)
            step 'Adding to PATH'
            for rc in "$HOME/.bashrc" "$HOME/.zshrc"; do
                [ -f "$rc" ] && printf '\n# codegraph\nexport PATH="$PATH:%s"\n' "$install_dir" >> "$rc"
            done
            echo 'Open a new shell for the PATH change to apply.'
            ;;
    esac
fi

step 'Installed'
echo "  $install_dir/codegraph-index"
echo "  $install_dir/codegraph-mcp"
echo
echo 'Index a solution (writes <worktree>/.codegraph/graph.db; add .codegraph/ to .gitignore):'
echo "  $install_dir/codegraph-index /path/to/App.sln          # once, then incremental"
echo "  $install_dir/codegraph-index /path/to/App.sln --watch  # keep it fresh while you work"
echo
echo 'Put this .mcp.json in the repo root; the server finds the graph from its working directory:'
cat <<EOF
{
  "mcpServers": {
    "codegraph": {
      "command": "$install_dir/codegraph-mcp"
    }
  }
}
EOF
