#!/usr/bin/env bash
# Installs codegraph-index and codegraph-mcp from the latest GitHub release and sets up repos to use them
# (macOS / Linux). No build tools needed: the archive carries its own .NET runtime.
#
#   curl -fsSL https://raw.githubusercontent.com/nerdcules/CodeGraph/main/codegraph/install.sh | bash -s -- [REPO]... [options]
#   ./install.sh [REPO_OR_SOLUTION]... [--version vX.Y.Z] [--install-dir DIR] [--add-to-path] [--live]
#                [--obsidian-vault DIR] [--from-source] [--skip-tests] [--force]
#
#   REPO_OR_SOLUTION  a repo folder or a .sln / .slnx / .csproj to set up: writes .mcp.json, adds .codegraph/ to
#                     .gitignore and builds the graph (codegraph-index init). Asked for when none is given.
#   --version         a release tag to install (default: the latest release)
#   --install-dir     default ~/.codegraph/bin
#   --add-to-path     append the install dir to PATH in ~/.bashrc and ~/.zshrc
#   --live            the MCP server starts the live daemon itself on its first call (CODEGRAPH_DAEMON=auto)
#   --obsidian-vault  also install the Obsidian plugin into that vault
#   --from-source     build from this checkout instead of downloading (needs the .NET 10 SDK; Node.js for TypeScript)
#   --skip-tests      with --from-source, publish without running the test suite
#   --force           stop running codegraph processes before overwriting them
#
# Re-run to upgrade. Set up another repo later with: codegraph-index init [repo-or-solution]
set -euo pipefail

repo_slug="nerdcules/CodeGraph"
install_dir="$HOME/.codegraph/bin"
version="latest"
from_source=false
skip_tests=false
add_to_path=false
force=false
live=false
obsidian_vault=""
repos=()

while [ $# -gt 0 ]; do
    case "$1" in
        --version)     version="$2"; shift 2 ;;
        --install-dir) install_dir="$2"; shift 2 ;;
        --from-source) from_source=true; shift ;;
        --skip-tests)  skip_tests=true; shift ;;
        --add-to-path) add_to_path=true; shift ;;
        --force)       force=true; shift ;;
        --live)        live=true; shift ;;
        --obsidian-vault) obsidian_vault="$2"; shift 2 ;;
        -h|--help)     sed -n '2,20p' "$0"; exit 0 ;;
        -*) echo "unknown option: $1" >&2; exit 1 ;;
        *)  repos+=("$1"); shift ;;
    esac
done

step() { printf '\n\033[36m==> %s\033[0m\n' "$1"; }

# The checkout this script lives in, when it is run from one rather than piped from curl.
root=""
if [ -n "${BASH_SOURCE[0]:-}" ] && [ -f "${BASH_SOURCE[0]}" ]; then
    root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    [ -f "$root/CodeGraph.slnx" ] || root=""
fi

install_dir="$(mkdir -p "$install_dir" && cd "$install_dir" && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

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

# ---- the executables: a release archive, or a build of this checkout ---------------------------------
download() { # download URL FILE
    if command -v curl >/dev/null; then
        curl -fsSL --retry 3 -o "$2" "$1"
    elif command -v wget >/dev/null; then
        wget -q -O "$2" "$1"
    else
        echo 'curl or wget is needed to download the release' >&2
        exit 1
    fi
}

release_url() { # release_url ASSET
    if [ "$version" = latest ]; then
        echo "https://github.com/$repo_slug/releases/latest/download/$1"
    else
        echo "https://github.com/$repo_slug/releases/download/$version/$1"
    fi
}

verify() { # verify FILE SUMS-FILE : checks the SHA256 when a tool for it is on PATH
    local expected actual
    expected="$(grep " $(basename "$1")\$" "$2" | cut -d' ' -f1 || true)"
    [ -n "$expected" ] || { echo "no checksum for $(basename "$1") in SHA256SUMS" >&2; exit 1; }
    if command -v sha256sum >/dev/null; then
        actual="$(sha256sum "$1" | cut -d' ' -f1)"
    elif command -v shasum >/dev/null; then
        actual="$(shasum -a 256 "$1" | cut -d' ' -f1)"
    else
        echo '  (no sha256sum or shasum on PATH; checksum not verified)'
        return
    fi
    [ "$expected" = "$actual" ] || { echo "checksum mismatch for $(basename "$1")" >&2; exit 1; }
    echo '  checksum ok'
}

install_from_release() {
    local os arch rid asset
    case "$(uname -s)" in
        Linux)  os=linux ;;
        Darwin) os=osx ;;
        *) echo "unsupported OS: $(uname -s). Use --from-source, or install.ps1 on Windows." >&2; exit 1 ;;
    esac
    case "$(uname -m)" in
        x86_64|amd64) arch=x64 ;;
        aarch64|arm64) arch=arm64 ;;
        *) echo "unsupported CPU: $(uname -m). Use --from-source." >&2; exit 1 ;;
    esac
    if [ "$os" = linux ] && [ -f /etc/alpine-release ]; then
        echo 'the release is built for glibc; on Alpine (musl) use --from-source' >&2
        exit 1
    fi
    rid="$os-$arch"
    asset="codegraph-$rid.tar.gz"

    step "Downloading $asset ($version)"
    if ! download "$(release_url "$asset")" "$tmp/$asset"; then
        echo "could not download $(release_url "$asset")" >&2
        echo 'Check https://github.com/'"$repo_slug"'/releases, or build from a checkout with --from-source.' >&2
        exit 1
    fi
    download "$(release_url SHA256SUMS)" "$tmp/SHA256SUMS"
    verify "$tmp/$asset" "$tmp/SHA256SUMS"

    step "Installing to $install_dir"
    find "$install_dir" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
    tar -xzf "$tmp/$asset" -C "$install_dir"
    chmod +x "$install_dir/codegraph-index" "$install_dir/codegraph-mcp"
    # A browser download would be quarantined on macOS; curl's is not, but clearing the flag costs nothing.
    if [ "$os" = osx ] && command -v xattr >/dev/null; then
        xattr -dr com.apple.quarantine "$install_dir" 2>/dev/null || true
    fi
    echo "  codegraph $(cat "$install_dir/VERSION")"
    if ! command -v node >/dev/null; then
        echo '  note: node is not on PATH; C# and the SCIP languages work, TypeScript and Angular need Node.js 22.22 or later'
    fi
}

install_from_source() {
    [ -n "$root" ] || { echo '--from-source needs this script run from a CodeGraph checkout' >&2; exit 1; }
    command -v dotnet >/dev/null || { echo 'dotnet SDK not found on PATH. Install .NET 10 from https://dotnet.microsoft.com/download' >&2; exit 1; }

    # The TypeScript and Angular extractor is a Node program; without Node, C# and the SCIP languages still work.
    local extractor="$root/extractors/typescript" has_node=false
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

    # ReadyToRun precompiles the assemblies for this platform: indexing runs about a quarter faster and the server
    # starts sooner (BENCHMARKS.md, round 8), and only this platform's DuckDB library is copied.
    local rid project
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
}

if [ "$from_source" = true ]; then
    install_from_source
else
    install_from_release
fi

# A framework-dependent build finds .NET through DOTNET_ROOT or the machine's default location, not through PATH.
# Under a version manager (mise, asdf) or a user-local install, that location can be missing. init writes
# DOTNET_ROOT into .mcp.json when the server needs it; here the indexer gets it for this script's own runs.
# A release build carries its runtime and never needs it.
step 'Checking the executables start'
dotnet_root=""
if command -v dotnet >/dev/null; then
    dotnet_root="$(cd "$(dirname "$(readlink -f "$(command -v dotnet)")")" && pwd)"
fi
needs_root=false
if env -u DOTNET_ROOT "$install_dir/codegraph-index" --version >/dev/null 2>&1 && env -u DOTNET_ROOT "$install_dir/codegraph-mcp" --version >/dev/null 2>&1; then
    echo "ok: codegraph $("$install_dir/codegraph-mcp" --version)"
elif [ -n "$dotnet_root" ] && DOTNET_ROOT="$dotnet_root" "$install_dir/codegraph-mcp" --version >/dev/null 2>&1; then
    needs_root=true
    echo "ok with DOTNET_ROOT=$dotnet_root (the default .NET location lacks a runtime codegraph needs)"
else
    echo "warning: codegraph-mcp does not start. Run $install_dir/codegraph-mcp --version by hand to see why." >&2
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

# Piped from curl, stdin is the script; the terminal is still there for a question.
if [ ${#repos[@]} -eq 0 ] && [ -z "${CODEGRAPH_NONINTERACTIVE:-}" ] && [ -t 1 ] && [ -r /dev/tty ]; then
    printf '\nSet up a repo now? Path to a repo folder or solution (Enter to skip): '
    read -r answer </dev/tty || answer=""
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

if [ -n "$obsidian_vault" ]; then
    step "Installing the Obsidian plugin into $obsidian_vault"
    if [ ! -d "$obsidian_vault/.obsidian" ]; then
        echo "$obsidian_vault has no .obsidian folder: open it as a vault in Obsidian once first" >&2
        failed=$((failed + 1))
    elif [ "$from_source" = true ] && command -v node >/dev/null 2>&1; then
        node "$root/obsidian-plugin/install-plugin.mjs" "$obsidian_vault" || failed=$((failed + 1))
    else
        plugin_dir="$obsidian_vault/.obsidian/plugins/codegraph"
        download "$(release_url codegraph-obsidian.zip)" "$tmp/codegraph-obsidian.zip"
        mkdir -p "$plugin_dir"
        if command -v unzip >/dev/null; then
            unzip -oq "$tmp/codegraph-obsidian.zip" -d "$plugin_dir"
        elif command -v python3 >/dev/null; then
            python3 -I -c 'import sys, zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])' "$tmp/codegraph-obsidian.zip" "$plugin_dir"
        else
            echo "neither unzip nor python3 is on PATH; unpack $tmp/codegraph-obsidian.zip into $plugin_dir by hand" >&2
            failed=$((failed + 1))
        fi
        echo "installed to $plugin_dir; enable \"CodeGraph\" under Settings → Community plugins"
    fi
fi

if [ "$needs_root" = true ]; then
    root_hint="DOTNET_ROOT=\"$dotnet_root\" "
else
    root_hint=""
fi
step 'Installed'
echo "  $install_dir/codegraph-index"
echo "  $install_dir/codegraph-mcp"
cat <<EOT

Set up a repo (writes .mcp.json and .gitignore at its root, then builds the graph):
  ${root_hint}$install_dir/codegraph-index init /path/to/repo-or-solution [--live]
Keep a graph fresh while you work:
  ${root_hint}$install_dir/codegraph-index /path/to/App.sln --watch
EOT
[ "$failed" -eq 0 ] || { echo "$failed repo setup(s) failed; see above" >&2; exit 1; }
