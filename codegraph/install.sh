#!/usr/bin/env bash
# Builds, tests and installs codegraph-index and codegraph-mcp, then sets up repos to use them (macOS / Linux).
#
#   ./install.sh [REPO_OR_SOLUTION]... [--install-dir DIR] [--add-to-path] [--live] [--skip-tests] [--force]
#
#   REPO_OR_SOLUTION  a repo folder or a .sln / .slnx / .csproj to set up: writes .mcp.json, adds .codegraph/ to
#                     .gitignore and builds the graph (codegraph-index init). Asked for when none is given.
#   --install-dir     default ~/.codegraph/bin
#   --add-to-path     append the install dir to PATH in ~/.bashrc and ~/.zshrc
#   --live            the MCP server starts the live daemon itself on its first call (CODEGRAPH_DAEMON=auto)
#   --skip-tests      publish without running the test suite
#   --force           stop running codegraph processes before overwriting them
#
# Re-run to upgrade. Set up another repo later with: codegraph-index init [repo-or-solution]
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
install_dir="$HOME/.codegraph/bin"
skip_tests=false
add_to_path=false
force=false
live=false
repos=()

while [ $# -gt 0 ]; do
    case "$1" in
        --install-dir) install_dir="$2"; shift 2 ;;
        --skip-tests)  skip_tests=true; shift ;;
        --add-to-path) add_to_path=true; shift ;;
        --force)       force=true; shift ;;
        --live)        live=true; shift ;;
        -h|--help)     sed -n '2,15p' "$0"; exit 0 ;;
        -*) echo "unknown option: $1" >&2; exit 1 ;;
        *)  repos+=("$1"); shift ;;
    esac
done

step() { printf '\n\033[36m==> %s\033[0m\n' "$1"; }

command -v dotnet >/dev/null || { echo 'dotnet SDK not found on PATH. Install .NET 10 from https://dotnet.microsoft.com/download' >&2; exit 1; }
install_dir="$(mkdir -p "$install_dir" && cd "$install_dir" && pwd)"

# The TypeScript and Angular extractor is a Node program; without Node, C# and the SCIP languages still work.
extractor="$root/extractors/typescript"
has_node=false
if command -v node >/dev/null && command -v npm >/dev/null; then
    has_node=true
    step "Installing the TypeScript extractor's packages (Node $(node --version))"
    (cd "$extractor" && npm ci --omit=dev --no-audit --no-fund --loglevel=error)
else
    echo 'note: node and npm are not on PATH; TypeScript and Angular indexing is skipped (install Node.js 22.22 or later to add it)'
fi

step 'Building (warnings are errors)'
dotnet build "$root/CodeGraph.slnx" -c Release -warnaserror --nologo

if [ "$skip_tests" = false ]; then
    step 'Running tests'
    dotnet test "$root/CodeGraph.slnx" -c Release --no-build
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

# ReadyToRun precompiles the assemblies for this platform: indexing runs about a quarter faster and the server
# starts sooner (BENCHMARKS.md, round 8), and only this platform's DuckDB library is copied.
rid="$(dotnet --info | awk '/^ *RID:/ { print $2; exit }')"
[ -n "$rid" ] || { echo 'could not read the runtime identifier from dotnet --info' >&2; exit 1; }
step "Publishing to $install_dir ($rid, ReadyToRun)"
for project in src/CodeGraph.Indexer src/CodeGraph.Mcp; do
    dotnet publish "$root/$project" -c Release -r "$rid" --self-contained false -p:PublishReadyToRun=true -o "$install_dir" --nologo
done
chmod +x "$install_dir/codegraph-index" "$install_dir/codegraph-mcp"
if [ "$has_node" = true ]; then
    # codegraph-index looks for extractors/typescript/cli.mjs beside itself.
    rm -rf "$install_dir/extractors/typescript"
    mkdir -p "$install_dir/extractors/typescript"
    cp -R "$extractor/cli.mjs" "$extractor/lib" "$extractor/package.json" "$extractor/package-lock.json" "$install_dir/extractors/typescript/"
    (cd "$install_dir/extractors/typescript" && npm ci --omit=dev --no-audit --no-fund --loglevel=error)
fi

# The executables find .NET through DOTNET_ROOT or the machine's default install location, not through PATH.
# When dotnet comes from a version manager (mise, asdf) or a user-local install, the default location can be
# missing or hold a runtime without ASP.NET Core. init writes DOTNET_ROOT into .mcp.json when the server needs
# it; here the indexer gets it for this script's own runs.
step 'Checking the executables start'
dotnet_root="$(cd "$(dirname "$(readlink -f "$(command -v dotnet)")")" && pwd)"
needs_root=false
if env -u DOTNET_ROOT "$install_dir/codegraph-index" --version >/dev/null 2>&1 && env -u DOTNET_ROOT "$install_dir/codegraph-mcp" --version >/dev/null 2>&1; then
    echo "ok: codegraph $("$install_dir/codegraph-mcp" --version)"
elif DOTNET_ROOT="$dotnet_root" "$install_dir/codegraph-mcp" --version >/dev/null 2>&1; then
    needs_root=true
    echo "ok with DOTNET_ROOT=$dotnet_root (the default .NET location lacks a runtime codegraph needs)"
else
    echo "warning: codegraph-mcp does not start, even with DOTNET_ROOT=$dotnet_root. Run it by hand to see why." >&2
fi
index() { if [ "$needs_root" = true ]; then DOTNET_ROOT="$dotnet_root" "$install_dir/codegraph-index" "$@"; else "$install_dir/codegraph-index" "$@"; fi; }

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

if [ ${#repos[@]} -eq 0 ] && [ -t 0 ]; then
    printf '\nSet up a repo now? Path to a repo folder or solution (Enter to skip): '
    read -r answer || answer=""
    [ -n "$answer" ] && repos+=("${answer/#\~/$HOME}")
fi

init_args=()
[ "$live" = true ] && init_args+=(--live)
failed=0
# The ${a[@]+...} form keeps macOS bash 3.2 from calling an empty array unbound under set -u.
for repo in ${repos[@]+"${repos[@]}"}; do
    step "Setting up $repo"
    index init "$repo" ${init_args[@]+"${init_args[@]}"} || failed=$((failed + 1))
done

if [ "$needs_root" = true ]; then
    root_hint="DOTNET_ROOT=\"$dotnet_root\" "
else
    root_hint=""
fi
step 'Installed'
echo "  $install_dir/codegraph-index"
echo "  $install_dir/codegraph-mcp"
cat <<EOF

Set up a repo (writes .mcp.json and .gitignore at its root, then builds the graph):
  ${root_hint}$install_dir/codegraph-index init /path/to/repo-or-solution [--live]
Keep a graph fresh while you work:
  ${root_hint}$install_dir/codegraph-index /path/to/App.sln --watch
EOF
[ "$failed" -eq 0 ] || { echo "$failed repo setup(s) failed; see above" >&2; exit 1; }
