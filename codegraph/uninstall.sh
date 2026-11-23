#!/usr/bin/env bash
# Removes what install.sh put on this machine (macOS / Linux).
#
#   ./uninstall.sh [--install-dir DIR] [--repo DIR]... [--force] [--yes]
#
#   --install-dir  the folder install.sh published into; default ~/.codegraph/bin
#   --repo         a repo whose MCP configs (.mcp.json and those of the other assistants init sets up) lose their
#                  codegraph entry and whose .codegraph/ is deleted (repeatable)
#   --force        stop running codegraph-mcp / codegraph-index processes first
#   --yes          do not ask before deleting
#
# The PATH lines install.sh --add-to-path appended to ~/.bashrc and ~/.zshrc are removed too, and so is the
# CodeGraph VS Code extension when `code` or `code-insiders` lists it.
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
        -h|--help)     sed -n '2,13p' "$0"; exit 0 ;;
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
        echo "deleted $install_dir"
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

    # Each assistant's project config that init can write, with the key its servers sit under; Visual Studio's
    # .vs/mcp.json sits beside the solution, so look a few folders down for it.
    pairs=(".mcp.json:mcpServers" ".cursor/mcp.json:mcpServers" ".vscode/mcp.json:servers" ".github/mcp.json:servers"
           ".gemini/settings.json:mcpServers" ".roo/mcp.json:mcpServers" ".kilo/kilo.json:mcp"
           ".continue/mcpServers/codegraph.json:mcpServers")
    while IFS= read -r vs; do pairs+=("${vs#"$dir"/}:servers"); done < <(find "$dir" -maxdepth 4 -path '*/.vs/mcp.json' -not -path '*/node_modules/*' 2>/dev/null)
    for pair in "${pairs[@]}"; do
        config="$dir/${pair%%:*}"
        [ -f "$config" ] || continue
        if command -v python3 >/dev/null; then
            python3 - "$config" "${pair##*:}" "$yes" <<'PY' || echo "could not read $config; remove the \"codegraph\" entry by hand" >&2
import json, sys
path, key, yes = sys.argv[1], sys.argv[2], sys.argv[3] == "true"
with open(path, encoding="utf-8") as f:
    config = json.load(f) or {}
servers = config.get(key) or {}
if "codegraph" not in servers:
    print(f"no codegraph entry in {path}")
    sys.exit(0)
if not yes:
    # stdin is this script (a heredoc), so ask on the terminal, as confirm() does.
    with open("/dev/tty", "w") as out, open("/dev/tty") as tty:
        out.write(f"Remove the codegraph server entry from {path}? [y/N] ")
        out.flush()
        if tty.readline().strip().lower() not in ("y", "yes"):
            sys.exit(0)
del servers["codegraph"]
with open(path, "w", encoding="utf-8") as f:
    json.dump(config, f, indent=2)
    f.write("\n")
print(f"removed the codegraph entry from {path}")
PY
        else
            echo "python3 is not available; remove the \"codegraph\" entry from $config by hand" >&2
        fi
    done

    if [ -d "$dir/.codegraph" ] && confirm "Delete the graph, telemetry and question log in $dir/.codegraph?"; then
        rm -rf "$dir/.codegraph"
        echo "deleted $dir/.codegraph"
    fi
done

# ---- Codex ------------------------------------------------------------------------------------------------
# init --client codex puts one entry in Codex's global config rather than in a repo.
if command -v codex >/dev/null && codex mcp get codegraph >/dev/null 2>&1; then
    step "Codex"
    if confirm "Remove the codegraph server from Codex's global config?"; then
        codex mcp remove codegraph
    fi
fi

# ---- VS Code extension ------------------------------------------------------------------------------------
# install.sh --vscode installs it with each VS Code command line it finds.
for editor in code code-insiders; do
    command -v "$editor" >/dev/null || continue
    if "$editor" --list-extensions 2>/dev/null | grep -qix 'nerdcules.codegraph'; then
        step "VS Code extension ($editor)"
        if confirm "Uninstall the CodeGraph extension from $editor?"; then
            "$editor" --uninstall-extension nerdcules.codegraph
        fi
    fi
done

printf '\nDone.\n'
