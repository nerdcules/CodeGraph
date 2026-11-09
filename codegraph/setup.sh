#!/usr/bin/env bash
# Source checkout setup (macOS / Linux): pull the latest CodeGraph, wire a repo's .mcp.json to `dotnet run`,
# build the graph. The no-publish alternative to install.sh: the MCP server runs from this checkout with
# `dotnet run --no-build`, so a `git pull` plus a build is an upgrade.
#
#   ./setup.sh [--solution PATH] [--config-dir DIR] [--yes] [--no-pull] [--full]
#
#   --solution    the .sln / .slnx / .csproj to index (prompted for when omitted)
#   --config-dir  where .mcp.json goes; default: the git worktree root of the solution
#   --yes         answer every question with its default: pull if behind, write the config, index
#   --no-pull     skip the fetch and pull
#   --full        rebuild the graph from scratch
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
solution=""
config_dir=""
yes=false
no_pull=false
full=false

while [ $# -gt 0 ]; do
    case "$1" in
        --solution)   solution="$2"; shift 2 ;;
        --config-dir) config_dir="$2"; shift 2 ;;
        --yes|-y)     yes=true; shift ;;
        --no-pull)    no_pull=true; shift ;;
        --full)       full=true; shift ;;
        -h|--help)    sed -n '2,12p' "$0"; exit 0 ;;
        *) echo "unknown option: $1" >&2; exit 1 ;;
    esac
done

step() { printf '\n\033[36m==> %s\033[0m\n' "$1"; }

confirm() { # confirm "question" default(true|false)
    local question="$1" default="${2:-true}" hint answer
    if [ "$yes" = true ]; then [ "$default" = true ]; return; fi
    if [ "$default" = true ]; then hint='[Y/n]'; else hint='[y/N]'; fi
    read -r -p "$question $hint " answer </dev/tty
    answer="$(printf '%s' "$answer" | tr '[:upper:]' '[:lower:]' | xargs)"
    if [ -z "$answer" ]; then [ "$default" = true ]; return; fi
    [ "$answer" = y ] || [ "$answer" = yes ]
}

for tool in dotnet git; do
    command -v "$tool" >/dev/null || { echo "$tool not found on PATH" >&2; exit 1; }
done

# ---- 1. pull -------------------------------------------------------------------------------------------
if [ "$no_pull" = false ]; then
    step 'Checking origin for a newer CodeGraph'
    git -C "$root" fetch origin --quiet
    branch="$(git -C "$root" rev-parse --abbrev-ref HEAD)"
    upstream="$(git -C "$root" rev-parse --abbrev-ref --symbolic-full-name '@{upstream}' 2>/dev/null || echo origin/HEAD)"
    behind="$(git -C "$root" rev-list --count "HEAD..$upstream")"
    dirty="$(git -C "$root" status --porcelain --untracked-files=no)"

    if [ "$behind" -eq 0 ]; then
        echo "$branch is up to date with $upstream"
    elif [ -n "$dirty" ]; then
        echo "warning: $branch is $behind commit(s) behind $upstream but has local changes; not pulling. Commit or stash, then re-run." >&2
    elif confirm "$branch is $behind commit(s) behind $upstream. Pull?"; then
        git -C "$root" pull --ff-only --quiet || { echo 'git pull --ff-only failed; the branch has diverged from origin' >&2; exit 1; }
        echo "now at $(git -C "$root" rev-parse --short HEAD)"
    fi
fi

# ---- 2. build ------------------------------------------------------------------------------------------
step 'Building (warnings are errors)'
dotnet build "$root/CodeGraph.slnx" -c Release -warnaserror --nologo

# ---- 3. .mcp.json --------------------------------------------------------------------------------------
if [ -z "$solution" ]; then
    if [ "$yes" = true ]; then echo 'pass --solution PATH when running with --yes' >&2; exit 1; fi
    read -r -p 'Solution or project to index (.sln / .slnx / .csproj): ' solution </dev/tty
fi
solution="$(cd "$(dirname "$solution")" && pwd)/$(basename "$solution")"
[ -f "$solution" ] || { echo "not found: $solution" >&2; exit 1; }

solution_dir="$(dirname "$solution")"
if [ -z "$config_dir" ]; then
    config_dir="$(git -C "$solution_dir" rev-parse --show-toplevel 2>/dev/null || echo "$solution_dir")"
fi
config_path="$config_dir/.mcp.json"
mcp_project="$root/src/CodeGraph.Mcp"

step "MCP config: $config_path"
entry_json="$(printf '{"command":"dotnet","args":["run","--project","%s","-c","Release","--no-build"]}' "$mcp_project")"
if command -v python3 >/dev/null; then
    # Merge into whatever is there; other servers are kept.
    python3 - "$config_path" "$entry_json" "$yes" <<'PY'
import json, os, sys
path, entry, yes = sys.argv[1], json.loads(sys.argv[2]), sys.argv[3] == "true"
config = {}
if os.path.exists(path):
    with open(path, encoding="utf-8") as f:
        config = json.load(f) or {}
servers = config.setdefault("mcpServers", {})
current = servers.get("codegraph")
if current == entry:
    print("codegraph server entry already up to date")
    sys.exit(0)
if not yes:
    verb = "Replace" if current else "Add"
    answer = input(f"{verb} the codegraph server entry in {path}? [Y/n] ").strip().lower()
    if answer not in ("", "y", "yes"):
        sys.exit(0)
servers["codegraph"] = entry
with open(path, "w", encoding="utf-8") as f:
    json.dump(config, f, indent=2)
    f.write("\n")
print(json.dumps(config, indent=2))
PY
elif [ -f "$config_path" ]; then
    echo "python3 is not available to merge into the existing $config_path; add this entry under \"mcpServers\" by hand:" >&2
    echo "  \"codegraph\": $entry_json"
elif confirm "Write $config_path?"; then
    printf '{\n  "mcpServers": {\n    "codegraph": %s\n  }\n}\n' "$entry_json" > "$config_path"
    cat "$config_path"
fi

# ---- 4. index ------------------------------------------------------------------------------------------
step "Indexing $solution"
index_args=("$solution")
if [ "$full" = true ]; then index_args+=(--full); fi
dotnet run --project "$root/src/CodeGraph.Indexer" -c Release --no-build -- "${index_args[@]}"

cat <<EOF

Done. Claude Code picks up $config_path on its next start in that repo.
Keep the graph fresh while you work:
  dotnet run --project "$root/src/CodeGraph.Indexer" -c Release --no-build -- "$solution" --watch
Upgrade later by re-running this script.
EOF
