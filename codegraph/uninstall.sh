#!/usr/bin/env bash
# Removes what install.sh put on this machine (macOS / Linux).
#
#   ./uninstall.sh [--install-dir DIR] [--repo DIR]... [--force] [--yes]
#
#   --install-dir  the folder install.sh published into; default ~/.codegraph/bin
#   --repo         a repo whose .mcp.json loses its codegraph entry and whose .codegraph/ is deleted (repeatable)
#   --force        stop running codegraph-mcp / codegraph-index processes first
#   --yes          do not ask before deleting
#
# The PATH lines install.sh --add-to-path appended to ~/.bashrc and ~/.zshrc are removed too.
set -euo pipefail

install_dir="$HOME/.codegraph/bin"
repos=()
force=false
yes=false

while [ $# -gt 0 ]; do
    case "$1" in
        --install-dir) install_dir="$2"; shift 2 ;;
        --repo)        repos+=("$2"); shift 2 ;;
        --force)       force=true; shift ;;
        --yes|-y)      yes=true; shift ;;
        -h|--help)     sed -n '2,11p' "$0"; exit 0 ;;
        *) echo "unknown option: $1" >&2; exit 1 ;;
    esac
done

step() { printf '\n\033[36m==> %s\033[0m\n' "$1"; }

confirm() {
    local answer
    if [ "$yes" = true ]; then return 0; fi
    read -r -p "$1 [y/N] " answer </dev/tty
    answer="$(printf '%s' "$answer" | tr '[:upper:]' '[:lower:]' | xargs)"
    [ "$answer" = y ] || [ "$answer" = yes ]
}

# ---- running processes --------------------------------------------------------------------------------
running=""
if command -v pgrep >/dev/null; then
    running="$(pgrep -f "$install_dir/codegraph-(mcp|index)" || true)"
fi
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

# ---- install folder -----------------------------------------------------------------------------------
if [ -d "$install_dir" ]; then
    if [ ! -f "$install_dir/codegraph-mcp.dll" ] && [ ! -f "$install_dir/codegraph-index.dll" ]; then
        echo "$install_dir does not look like a codegraph install (no codegraph-mcp.dll or codegraph-index.dll); not deleting it" >&2
        exit 1
    fi
    step "Install folder: $install_dir"
    if confirm "Delete $install_dir and everything in it?"; then
        rm -rf "$install_dir"
        echo deleted
    fi
else
    echo "no install folder at $install_dir"
fi

# ---- PATH ---------------------------------------------------------------------------------------------
for rc in "$HOME/.bashrc" "$HOME/.zshrc"; do
    [ -f "$rc" ] || continue
    if grep -qF "export PATH=\"\$PATH:$install_dir\"" "$rc"; then
        step "Removing from $rc"
        tmp="$(mktemp)"
        # Drop the marker comment install.sh wrote and the export line after it.
        # ENVIRON, not -v: -v would interpret backslashes in the path.
        line="export PATH=\"\$PATH:$install_dir\"" awk '
            $0 == "# codegraph"          { held = 1; next }
            held && $0 == ENVIRON["line"] { held = 0; next }
            held                         { print "# codegraph"; held = 0 }
            { print }' "$rc" > "$tmp"
        cat "$tmp" > "$rc"
        rm -f "$tmp"
        echo 'Open a new shell for the PATH change to apply.'
    fi
done

# ---- repos --------------------------------------------------------------------------------------------
for dir in "${repos[@]}"; do
    dir="$(cd "$dir" && pwd)"
    step "Repo: $dir"

    config="$dir/.mcp.json"
    if [ -f "$config" ]; then
        if command -v python3 >/dev/null; then
            python3 - "$config" "$yes" <<'PY'
import json, sys
path, yes = sys.argv[1], sys.argv[2] == "true"
with open(path, encoding="utf-8") as f:
    config = json.load(f) or {}
servers = config.get("mcpServers") or {}
if "codegraph" not in servers:
    print(f"no codegraph entry in {path}")
    sys.exit(0)
if not yes and input(f"Remove the codegraph server entry from {path}? [y/N] ").strip().lower() not in ("y", "yes"):
    sys.exit(0)
del servers["codegraph"]
with open(path, "w", encoding="utf-8") as f:
    json.dump(config, f, indent=2)
    f.write("\n")
print("entry removed")
PY
        else
            echo "python3 is not available; remove the \"codegraph\" entry from $config by hand" >&2
        fi
    fi

    if [ -d "$dir/.codegraph" ] && confirm "Delete the graph and telemetry in $dir/.codegraph?"; then
        rm -rf "$dir/.codegraph"
        echo deleted
    fi
done

printf '\nDone.\n'
