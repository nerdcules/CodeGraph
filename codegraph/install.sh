#!/usr/bin/env bash
# Installs codegraph-index and codegraph-mcp (and, from 1.6.0-beta.1, the codegraph CLI) from the latest GitHub release, sets up repos to use them, and can add
# the VS Code extension (macOS / Linux). No build tools needed: the archive carries its own .NET runtime.
#
#   curl -fsSL https://raw.githubusercontent.com/nerdcules/CodeGraph/main/codegraph/install.sh | bash
#   ./install.sh [REPO_OR_SOLUTION]... [--version vX.Y.Z] [--install-dir DIR] [--add-to-path] [--live]
#                [--client NAMES] [--vscode | --no-vscode] [--yes]
#                [--from-source] [--skip-tests] [--force]
#
# On a terminal the script asks for what the command line leaves out: the install folder, the repos, the
# assistants, the live daemon, PATH and the VS Code extension, then shows the plan before it changes anything.
# Use the arrow keys and space in the lists, Enter to accept. --yes, or no terminal, asks nothing.
#
#   REPO_OR_SOLUTION  a repo folder or a .sln / .slnx / .csproj to set up: writes .mcp.json, adds .codegraph/ to
#                     .gitignore and builds the graph (codegraph-index init)
#   --version         a release tag to install (default: the latest release)
#   --install-dir     default ~/.codegraph/bin
#   --add-to-path     append the install dir to PATH in ~/.bashrc and ~/.zshrc
#   --live            the MCP server starts the live daemon itself on its first call (CODEGRAPH_DAEMON=auto)
#   --client          the assistants to set each repo up for, comma-separated: claude (the default; also the
#                     Copilot CLI), copilot (CLI, VS Code, Visual Studio, JetBrains), vscode, visualstudio,
#                     jetbrains, cursor, gemini, codex, roo, kilo, continue, or all
#   --vscode          install the VS Code extension (codegraph-vscode.vsix from the same release) with `code`
#   --no-vscode       do not install it, and do not ask
#   --yes, -y         ask nothing: take the options given and the defaults for the rest
#   --from-source     build from this checkout instead of downloading (needs the .NET 10 SDK; Node.js for TypeScript
#                     and for the VS Code extension)
#   --skip-tests      with --from-source, publish without running the test suite
#   --force           stop running codegraph processes before overwriting them
#
# CODEGRAPH_NONINTERACTIVE=1 is the same as --yes. CODEGRAPH_RELEASE_URL replaces
# https://github.com/nerdcules/CodeGraph/releases, for a mirror that keeps the same layout.
# Re-run to upgrade. Set up another repo later with: codegraph-index init [repo-or-solution]
set -euo pipefail

repo_slug="nerdcules/CodeGraph"
release_base="${CODEGRAPH_RELEASE_URL:-https://github.com/$repo_slug/releases}"
release_base="${release_base%/}"
extension_id="nerdcules.codegraph"
default_install_dir="$HOME/.codegraph/bin"
install_dir=""
version="latest"
from_source=false
skip_tests=false
add_to_path=""   # "", true or false: empty means not given, so the wizard may ask
force=false
live=""
vscode=""
assume_yes=false
clients=""
repos=()

usage() {
    if [ -f "$0" ]; then
        awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "$0"
    else
        echo "See https://github.com/$repo_slug/blob/main/codegraph/install.sh for the options."
    fi
}

while [ $# -gt 0 ]; do
    case "$1" in
        --version)     version="$2"; shift 2 ;;
        --install-dir) install_dir="$2"; shift 2 ;;
        --from-source) from_source=true; shift ;;
        --skip-tests)  skip_tests=true; shift ;;
        --add-to-path) add_to_path=true; shift ;;
        --force)       force=true; shift ;;
        --live)        live=true; shift ;;
        --client)      clients="${clients:+$clients,}$2"; shift 2 ;;
        --vscode)      vscode=true; shift ;;
        --no-vscode)   vscode=false; shift ;;
        --yes|-y)      assume_yes=true; shift ;;
        -h|--help)     usage; exit 0 ;;
        -*) echo "unknown option: $1" >&2; exit 1 ;;
        *)  repos+=("$1"); shift ;;
    esac
done

# ---- terminal --------------------------------------------------------------------------------------------
# Piped from curl, stdin is the script; questions are read from the terminal instead. Opening /dev/tty is the test:
# [ -r /dev/tty ] is true even in a process with no controlling terminal.
interactive=false
if [ "$assume_yes" = false ] && [ -z "${CODEGRAPH_NONINTERACTIVE:-}" ] && [ -t 1 ] && { : </dev/tty; } 2>/dev/null; then
    interactive=true
fi
# Arrow-key lists need cursor movement; a dumb terminal gets typed answers instead.
fancy=false
[ "$interactive" = true ] && [ "${TERM:-dumb}" != dumb ] && fancy=true

c_reset="" c_bold="" c_dim="" c_cyan="" c_green="" c_yellow="" c_red=""
if [ -t 1 ] && [ -z "${NO_COLOR:-}" ] && [ "${TERM:-dumb}" != dumb ]; then
    c_reset=$'\033[0m' c_bold=$'\033[1m' c_dim=$'\033[2m' c_cyan=$'\033[36m' c_green=$'\033[32m'
    c_yellow=$'\033[33m' c_red=$'\033[31m'
fi
case "${LC_ALL:-${LC_CTYPE:-${LANG:-}}}" in
    *UTF-8*|*utf-8*|*UTF8*|*utf8*) g_on='◉' g_off='○' g_ptr='❯' g_ok='✓' g_fail='✗' g_skip='–' ;;
    *) g_on='[x]' g_off='[ ]' g_ptr='>' g_ok='ok' g_fail='!!' g_skip='--' ;;
esac

step() { printf '\n%s==>%s %s%s%s\n' "$c_cyan" "$c_reset" "$c_bold" "$1" "$c_reset"; }
note() { printf '  %s%s%s\n' "$c_dim" "$1" "$c_reset"; }
warn() { printf '%swarning:%s %s\n' "$c_yellow" "$c_reset" "$1" >&2; }
die()  { printf '%serror:%s %s\n' "$c_red" "$c_reset" "$1" >&2; exit 1; }

# Shown in the last lines: one row per part of the install, with a tick or a cross.
results=()
result() { results+=("$1|$2"); } # result ok|fail|skip TEXT

tmp="$(mktemp -d)"
cleanup() {
    rm -rf "$tmp"
    if [ "$fancy" = true ]; then printf '\033[?25h'; fi # the lists hide the cursor
}
trap cleanup EXIT
trap 'exit 130' INT

# ---- questions -------------------------------------------------------------------------------------------
# All answers come from /dev/tty. The answer to the last question is in $answer.
answer=""

# ask_yes_no QUESTION DEFAULT : DEFAULT is y or n; returns 0 for yes. One key, Enter for the default.
ask_yes_no() {
    local hint key
    if [ "$2" = y ]; then hint='Y/n'; else hint='y/N'; fi
    printf '%s?%s %s %s(%s)%s ' "$c_cyan" "$c_reset" "$1" "$c_dim" "$hint" "$c_reset"
    while true; do
        if [ "$fancy" = true ]; then
            IFS= read -rsn1 key </dev/tty || key=""
        else
            IFS= read -r key </dev/tty || key=""
        fi
        case "$key" in
            y|Y|yes|Yes) answer=y; break ;;
            n|N|no|No)   answer=n; break ;;
            "")          answer="$2"; break ;;
        esac
    done
    if [ "$answer" = y ]; then printf '%sYes%s\n' "$c_green" "$c_reset"; return 0; fi
    printf '%sNo%s\n' "$c_dim" "$c_reset"
    return 1
}

# ask_text QUESTION [DEFAULT] : a line with readline editing and Tab completion of paths; Enter for the default.
ask_text() {
    local prompt line
    # \001 and \002 tell readline the colour codes take no room, so editing the line keeps the cursor right.
    prompt=$'\001'"$c_cyan"$'\002?\001'"$c_reset"$'\002'" $1"
    if [ -n "${2:-}" ]; then prompt="$prompt "$'\001'"$c_dim"$'\002'"($2)"$'\001'"$c_reset"$'\002'; fi
    if [ "$fancy" = true ]; then
        IFS= read -r -e -p "$prompt " line </dev/tty || line=""
    else
        printf '? %s%s ' "$1" "${2:+ ($2)}"
        IFS= read -r line </dev/tty || line=""
    fi
    # Trim the spaces and quotes a paste or a drag from a file manager brings along.
    line="${line#"${line%%[![:space:]]*}"}"
    line="${line%"${line##*[![:space:]]}"}"
    case "$line" in \"*\"|\'*\') line="${line#?}"; line="${line%?}" ;; esac
    answer="${line:-${2:-}}"
    answer="${answer/#\~/$HOME}"
}

# The list ask_checklist shows: a key, a label and a dim hint per row, and 1 or 0 for whether the row is on.
list_keys=() list_labels=() list_hints=() list_on=()
list_add() { list_keys+=("$1"); list_labels+=("$2"); list_hints+=("$3"); list_on+=("$4"); }

# ask_checklist QUESTION : arrow keys (or j/k) move, space toggles, a toggles all, Enter accepts. Sets list_on and
# $answer to the comma-separated keys that are on. Asks again while nothing is on.
ask_checklist() {
    local n=${#list_keys[@]} cursor=0 key rest i width=0 mark ptr chosen
    for i in "${!list_labels[@]}"; do [ ${#list_labels[$i]} -le $width ] || width=${#list_labels[$i]}; done
    if [ "$fancy" = false ]; then
        printf '? %s\n' "$1"
        for i in "${!list_keys[@]}"; do
            printf '  %2d. %s %-*s  %s\n' $((i + 1)) "$([ "${list_on[$i]}" = 1 ] && echo "$g_on" || echo "$g_off")" "$width" "${list_labels[$i]}" "${list_hints[$i]}"
        done
        while true; do
            printf '  Numbers, separated by spaces or commas (Enter keeps the ones marked): '
            IFS= read -r rest </dev/tty || rest=""
            if [ -n "$rest" ]; then
                for i in "${!list_on[@]}"; do list_on[$i]=0; done
                for key in $(printf '%s' "$rest" | tr ',' ' '); do
                    case "$key" in *[!0-9]*) continue ;; esac
                    [ "$key" -ge 1 ] && [ "$key" -le "$n" ] && list_on[$((key - 1))]=1
                done
            fi
            chosen=""
            for i in "${!list_keys[@]}"; do [ "${list_on[$i]}" = 1 ] && chosen="${chosen:+$chosen,}${list_keys[$i]}"; done
            [ -n "$chosen" ] && break
            echo '  Pick at least one.'
        done
        answer="$chosen"
        return
    fi

    printf '%s?%s %s %s(↑↓ move, space select, a all, enter accept)%s\n' "$c_cyan" "$c_reset" "$1" "$c_dim" "$c_reset"
    printf '\033[?25l'
    while true; do
        for i in "${!list_keys[@]}"; do
            if [ "${list_on[$i]}" = 1 ]; then mark="$c_green$g_on$c_reset"; else mark="$g_off"; fi
            if [ "$i" -eq "$cursor" ]; then ptr="$c_cyan$g_ptr$c_reset"; else ptr=" "; fi
            printf '\033[2K  %s %s %-*s  %s%s%s\n' "$ptr" "$mark" "$width" "${list_labels[$i]}" "$c_dim" "${list_hints[$i]}" "$c_reset"
        done
        IFS= read -rsn1 key </dev/tty || key=""
        if [ "$key" = $'\033' ]; then
            IFS= read -rsn2 -t 1 rest </dev/tty || rest=""
            key="$key$rest"
        fi
        case "$key" in
            $'\033[A'|$'\033OA'|k) cursor=$(( (cursor + n - 1) % n )) ;;
            $'\033[B'|$'\033OB'|j) cursor=$(( (cursor + 1) % n )) ;;
            " "|x) list_on[$cursor]=$(( 1 - list_on[cursor] )) ;;
            a|A)
                rest=0
                for i in "${!list_on[@]}"; do [ "${list_on[$i]}" = 1 ] || rest=1; done
                for i in "${!list_on[@]}"; do list_on[$i]=$rest; done
                ;;
            "")
                chosen=""
                for i in "${!list_keys[@]}"; do [ "${list_on[$i]}" = 1 ] && chosen="${chosen:+$chosen,}${list_keys[$i]}"; done
                [ -n "$chosen" ] && break
                ;;
        esac
        printf '\033[%dA' "$n"
    done
    printf '\033[?25h'
    # Fold the list into one line that names what was picked.
    printf '\033[%dA\033[J' $((n + 1))
    rest=""
    for i in "${!list_keys[@]}"; do [ "${list_on[$i]}" = 1 ] && rest="${rest:+$rest, }${list_labels[$i]}"; done
    printf '%s?%s %s %s%s%s\n' "$c_cyan" "$c_reset" "$1" "$c_green" "$rest" "$c_reset"
    answer="$chosen"
}

# ---- where things are ------------------------------------------------------------------------------------
# The checkout this script lives in, when it is run from one rather than piped from curl. The physical path
# (pwd -P): through a symlink, such as /tmp on macOS, dotnet would see each project under two spellings.
root=""
if [ -n "${BASH_SOURCE[0]:-}" ] && [ -f "${BASH_SOURCE[0]}" ]; then
    root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
    [ -f "$root/CodeGraph.slnx" ] || root=""
fi
[ "$from_source" = false ] || [ -n "$root" ] || die '--from-source needs this script run from a CodeGraph checkout'

# The rc files a PATH line goes in: those that exist, and the login shell's even when it does not yet (a new Mac
# has no ~/.zshrc). uninstall.sh removes the same two lines from both.
path_rcs() {
    local rc
    for rc in "$HOME/.bashrc" "$HOME/.zshrc"; do
        if [ -f "$rc" ] || [ "$rc" = "$HOME/.$(basename "${SHELL:-sh}")rc" ]; then printf '%s\n' "$rc"; fi
    done
}

pretty() { case "$1" in "$HOME"/*) printf '~/%s' "${1#"$HOME"/}" ;; *) printf '%s' "$1" ;; esac; }
on_path() { case ":$PATH:" in *":$1:"*) return 0 ;; *) return 1 ;; esac; }

download() { # download URL FILE
    if command -v curl >/dev/null; then
        if [ "$interactive" = true ] && [ "${3:-}" = big ]; then
            curl -fL --retry 3 --progress-bar -o "$2" "$1"
        else
            curl -fsSL --retry 3 -o "$2" "$1"
        fi
    elif command -v wget >/dev/null; then
        wget -q -O "$2" "$1"
    else
        die 'curl or wget is needed to download the release'
    fi
}

release_url() { # release_url ASSET
    if [ "$version" = latest ]; then
        echo "$release_base/latest/download/$1"
    else
        echo "$release_base/download/$version/$1"
    fi
}

verify() { # verify FILE SUMS-FILE : checks the SHA256 when a tool for it is on PATH
    local expected actual
    expected="$(grep " $(basename "$1")\$" "$2" | cut -d' ' -f1 || true)"
    [ -n "$expected" ] || die "no checksum for $(basename "$1") in SHA256SUMS"
    if command -v sha256sum >/dev/null; then
        actual="$(sha256sum "$1" | cut -d' ' -f1)"
    elif command -v shasum >/dev/null; then
        actual="$(shasum -a 256 "$1" | cut -d' ' -f1)"
    else
        note '(no sha256sum or shasum on PATH; checksum not verified)'
        return
    fi
    [ "$expected" = "$actual" ] || die "checksum mismatch for $(basename "$1")"
    note 'checksum ok'
}

rid=""
if [ "$from_source" = false ]; then
    case "$(uname -s)" in
        Linux)  os=linux ;;
        Darwin) os=osx ;;
        *) die "unsupported OS: $(uname -s). Use --from-source, or install.ps1 on Windows." ;;
    esac
    case "$(uname -m)" in
        x86_64|amd64) arch=x64 ;;
        aarch64|arm64) arch=arm64 ;;
        *) die "unsupported CPU: $(uname -m). Use --from-source." ;;
    esac
    if [ "$os" = linux ] && [ -f /etc/alpine-release ]; then
        die 'the release is built for glibc; on Alpine (musl) use --from-source'
    fi
    rid="$os-$arch"

    # The checksums first: they are small, they show that the release exists before any question is asked, and
    # they say whether it carries the VS Code extension.
    if ! download "$(release_url SHA256SUMS)" "$tmp/SHA256SUMS"; then
        die "could not download $(release_url SHA256SUMS). Check https://github.com/$repo_slug/releases, or build from a checkout with --from-source."
    fi
fi

# VS Code's command line: on PATH, or inside the app on macOS, where putting `code` on PATH is a step of its own.
editors=()
for cmd in code code-insiders; do
    if command -v "$cmd" >/dev/null; then editors+=("$cmd"); fi
done
if [ ${#editors[@]} -eq 0 ] && [ -x "/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code" ]; then
    editors+=("/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code")
fi

# Whether this install can offer the extension, and why not when it cannot.
vsix_problem=""
if [ "$from_source" = true ]; then
    { command -v node >/dev/null && command -v npm >/dev/null; } || vsix_problem='building the VS Code extension needs node and npm on PATH'
elif ! grep -q ' codegraph-vscode.vsix$' "$tmp/SHA256SUMS"; then
    vsix_problem="release $version has no VS Code extension (releases after 1.2.0 carry codegraph-vscode.vsix)"
fi
[ ${#editors[@]} -gt 0 ] || vsix_problem="${vsix_problem:-the VS Code command line (code) is not on PATH}"

extension_version() { # extension_version EDITOR : the installed version of the extension, or nothing
    "$1" --list-extensions --show-versions 2>/dev/null | awk -F@ -v id="$extension_id" 'tolower($1) == id { print $2 }' || true
}

if [ "$vscode" = true ] && [ -n "$vsix_problem" ]; then
    die "--vscode: $vsix_problem"
fi

# ---- the wizard ------------------------------------------------------------------------------------------
if [ "$interactive" = true ]; then
    printf '\n%sCodeGraph installer%s  %s%s%s\n' "$c_bold" "$c_reset" "$c_dim" \
        "$([ "$from_source" = true ] && echo "build of $root" || echo "release $version${rid:+, $rid}")" "$c_reset"
    note 'The compiler'"'"'s answers for your AI coding assistant: who calls this, what breaks if I change it, which tests guard it.'
    echo

    if [ -z "$install_dir" ]; then
        ask_text 'Install folder' "$(pretty "$default_install_dir")"
        install_dir="$answer"
    fi

    if [ ${#repos[@]} -eq 0 ]; then
        here="$(git rev-parse --show-toplevel 2>/dev/null || true)"
        if [ -n "$here" ] && ask_yes_no "Set up $(pretty "$here")?" y; then
            repos+=("$here")
        fi
        while true; do
            if [ ${#repos[@]} -eq 0 ]; then
                ask_text 'Repository to set up: a folder, or a .sln / .slnx / .csproj (Enter to skip)'
            else
                ask_text 'Another repository? (Enter when done)'
            fi
            [ -n "$answer" ] || break
            if [ -e "$answer" ]; then repos+=("$answer"); else echo "  not found: $answer"; fi
        done
    fi

    if [ ${#repos[@]} -gt 0 ] && [ -z "$clients" ]; then
        # Claude Code is on by default, as in `init`; the others are on when their program is found.
        found() { if command -v "$1" >/dev/null || { [ -n "${2:-}" ] && [ -d "$2" ]; }; then echo 1; else echo 0; fi; }
        list_add claude   'Claude Code, Copilot CLI' '.mcp.json' 1
        list_add vscode   'VS Code chat'             '.vscode/mcp.json (the extension offers the server without it)' 0
        list_add jetbrains 'Copilot for JetBrains'   '.github/mcp.json' 0
        list_add cursor   'Cursor'                   '.cursor/mcp.json' "$(found cursor "$HOME/.cursor")"
        list_add gemini   'Gemini CLI'               '.gemini/settings.json' "$(found gemini)"
        list_add codex    'Codex'                    'codex mcp add, in your Codex config' "$(found codex)"
        list_add roo      'Roo Code'                 '.roo/mcp.json' 0
        list_add kilo     'Kilo Code'                '.kilo/kilo.json' 0
        list_add continue 'Continue'                 '.continue/mcpServers/codegraph.json' 0
        ask_checklist 'Assistants to set each repository up for'
        clients="$answer"
    fi

    if [ ${#repos[@]} -gt 0 ] && [ -z "$live" ]; then
        note 'The live daemon answers from the compiler between indexes; it holds the loaded solution in memory.'
        if ask_yes_no 'Let the MCP server start the live daemon on its first call?' n; then live=true; else live=false; fi
    fi
fi

install_dir="${install_dir:-$default_install_dir}"
install_dir="${install_dir/#\~/$HOME}"
# Absolute, without creating it yet: answering no to the plan leaves nothing behind.
case "$install_dir" in /*) ;; *) install_dir="$PWD/$install_dir" ;; esac
[ ! -d "$install_dir" ] || install_dir="$(cd "$install_dir" && pwd)"

if [ "$interactive" = true ]; then
    if [ -z "$add_to_path" ]; then
        if on_path "$install_dir"; then
            add_to_path=false
        elif ask_yes_no "Add $(pretty "$install_dir") to PATH, so codegraph-index works in any shell?" y; then
            add_to_path=true
        else
            add_to_path=false
        fi
    fi

    if [ -z "$vscode" ]; then
        if [ -n "$vsix_problem" ]; then
            note "VS Code extension: not offered; $vsix_problem."
            vscode=false
        else
            installed="$(extension_version "${editors[0]}")"
            question='Install the CodeGraph extension for VS Code?'
            [ -z "$installed" ] || question="Update the CodeGraph extension for VS Code (installed: $installed)?"
            note 'The extension shows each graph'"'"'s state, runs the live daemons, and offers the MCP server to VS Code'"'"'s chat.'
            if ask_yes_no "$question" y; then vscode=true; else vscode=false; fi
        fi
    fi

    # The plan, before anything changes.
    echo
    printf '%sPlan%s\n' "$c_bold" "$c_reset"
    if [ "$from_source" = true ]; then
        printf '  %-12s build, %s, and publish %s to %s\n' 'Install' "$([ "$skip_tests" = true ] && echo 'no tests' || echo 'test')" "$root" "$(pretty "$install_dir")"
    else
        printf '  %-12s CodeGraph %s (%s) to %s\n' 'Install' "$version" "$rid" "$(pretty "$install_dir")"
    fi
    for repo in ${repos[@]+"${repos[@]}"}; do
        printf '  %-12s %s  %s%s%s%s\n' 'Set up' "$(pretty "$repo")" "$c_dim" "${clients:-claude}" "$([ "$live" = true ] && echo ', live daemon')" "$c_reset"
    done
    [ ${#repos[@]} -gt 0 ] || printf '  %-12s %s\n' 'Set up' 'no repository now'
    if [ "$add_to_path" = true ]; then
        rcs="$(path_rcs | while IFS= read -r rc; do printf ' %s' "$(pretty "$rc")"; done)"
        if [ -n "$rcs" ]; then rcs="add to$rcs"; else rcs='no ~/.bashrc or ~/.zshrc to add it to'; fi
        printf '  %-12s %s\n' 'PATH' "$rcs"
    fi
    [ "$vscode" != true ] || printf '  %-12s %s\n' 'VS Code' "install the extension with ${editors[*]##*/}"
    echo
    ask_yes_no 'Go ahead?' y || { echo 'Nothing was changed.'; exit 0; }
fi

mkdir -p "$install_dir"
install_dir="$(cd "$install_dir" && pwd)"

# ---- running processes -----------------------------------------------------------------------------------
# A running server keeps its files open; overwriting them under it is undefined.
running=$(pgrep -f "$install_dir/codegraph-(mcp|index)" || true)
if [ -n "$running" ]; then
    if [ "$force" = false ] && [ "$interactive" = true ]; then
        echo "codegraph is running from $(pretty "$install_dir") (pids: $(echo $running))."
        if ask_yes_no 'Stop it? Assistant sessions using it lose the codegraph tools until they restart it.' n; then force=true; fi
    fi
    if [ "$force" = true ]; then
        step 'Stopping running codegraph processes'
        kill $running || true
        sleep 1
    else
        die "codegraph is running from $install_dir (pids: $(echo $running)). Close the assistant sessions using it, or re-run with --force."
    fi
fi

# ---- the executables: a release archive, or a build of this checkout ---------------------------------
install_from_release() {
    local asset="codegraph-$rid.tar.gz"
    step "Downloading $asset ($version)"
    if ! download "$(release_url "$asset")" "$tmp/$asset" big; then
        die "could not download $(release_url "$asset"). Check https://github.com/$repo_slug/releases, or build from a checkout with --from-source."
    fi
    verify "$tmp/$asset" "$tmp/SHA256SUMS"

    step "Installing to $install_dir"
    find "$install_dir" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
    tar -xzf "$tmp/$asset" -C "$install_dir"
    chmod +x "$install_dir/codegraph-index" "$install_dir/codegraph-mcp"
    [ ! -f "$install_dir/codegraph" ] || chmod +x "$install_dir/codegraph"   # the CLI, in releases from 1.6.0-beta.1
    # A browser download would be quarantined on macOS; curl's is not, but clearing the flag costs nothing.
    if [ "$os" = osx ] && command -v xattr >/dev/null; then
        xattr -dr com.apple.quarantine "$install_dir" 2>/dev/null || true
    fi
    note "codegraph $(cat "$install_dir/VERSION")"
    if ! command -v node >/dev/null; then
        note 'node is not on PATH; C# and the SCIP languages work, TypeScript and Angular need Node.js 22.22.3+ or 24.15+'
    fi
}

install_from_source() {
    command -v dotnet >/dev/null || die 'dotnet SDK not found on PATH. Install .NET 10 from https://dotnet.microsoft.com/download'

    # The TypeScript and Angular extractor is a Node program; without Node, C# and the SCIP languages still work.
    local extractor="$root/extractors/typescript" has_node=false
    if command -v node >/dev/null && command -v npm >/dev/null; then
        has_node=true
        step "Installing the TypeScript extractor's packages (Node $(node --version))"
        (cd "$extractor" && npm ci --omit=dev --no-audit --no-fund --loglevel=error)
    else
        note 'node and npm are not on PATH; TypeScript and Angular indexing is skipped (install Node.js 22.22.3+ or 24.15+ to add it)'
    fi

    step 'Building (warnings are errors)'
    dotnet build "$root/CodeGraph.slnx" -c Release -warnaserror --nologo

    if [ "$skip_tests" = false ]; then
        step 'Running tests'
        dotnet test "$root/CodeGraph.slnx" -c Release --no-build
    fi

    # ReadyToRun precompiles the assemblies for this platform: indexing runs about a quarter faster and the server
    # starts sooner (BENCHMARKS.md, round 8), and only this platform's DuckDB library is copied.
    local source_rid project
    source_rid="$(dotnet --info | awk '/^ *RID:/ { print $2; exit }')"
    [ -n "$source_rid" ] || die 'could not read the runtime identifier from dotnet --info'
    step "Publishing to $install_dir ($source_rid, ReadyToRun)"
    for project in src/CodeGraph.Indexer src/CodeGraph.Mcp; do
        dotnet publish "$root/$project" -c Release -r "$source_rid" --self-contained false -p:PublishReadyToRun=true -o "$install_dir" --nologo
    done
    # The codegraph CLI is a Native AOT project; here it is published ReadyToRun like the release archives carry it,
    # so a source install needs no C toolchain.
    dotnet publish "$root/src/CodeGraph.Cli" -c Release -r "$source_rid" --self-contained false -p:PublishAot=false \
        -p:PublishReadyToRun=true -o "$install_dir" --nologo
    chmod +x "$install_dir/codegraph-index" "$install_dir/codegraph-mcp" "$install_dir/codegraph"
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
    installed_version="$("$install_dir/codegraph-mcp" --version)"
    note "ok: codegraph $installed_version"
    result ok "CodeGraph $installed_version in $(pretty "$install_dir")"
elif [ -n "$dotnet_root" ] && DOTNET_ROOT="$dotnet_root" "$install_dir/codegraph-mcp" --version >/dev/null 2>&1; then
    needs_root=true
    note "ok with DOTNET_ROOT=$dotnet_root (the default .NET location lacks a runtime codegraph needs)"
    result ok "CodeGraph in $(pretty "$install_dir") (needs DOTNET_ROOT=$dotnet_root by hand)"
else
    warn "codegraph-mcp does not start. Run $install_dir/codegraph-mcp --version by hand to see why."
    result fail "CodeGraph in $(pretty "$install_dir") does not start"
fi
index() { if [ "$needs_root" = true ]; then DOTNET_ROOT="$dotnet_root" "$install_dir/codegraph-index" "$@"; else "$install_dir/codegraph-index" "$@"; fi; }

if [ "$add_to_path" = true ]; then
    if on_path "$install_dir"; then
        result skip "PATH already has $(pretty "$install_dir")"
    else
        step 'Adding to PATH'
        rcs=()
        while IFS= read -r rc; do rcs+=("$rc"); done < <(path_rcs)
        for rc in ${rcs[@]+"${rcs[@]}"}; do
            printf '\n# codegraph\nexport PATH="$PATH:%s"\n' "$install_dir" >> "$rc"
            note "added to $(pretty "$rc")"
        done
        if [ ${#rcs[@]} -gt 0 ]; then
            result ok "PATH: added to $(for rc in "${rcs[@]}"; do printf '%s ' "$(pretty "$rc")"; done)(open a new shell)"
        else
            result fail "PATH: no ~/.bashrc or ~/.zshrc; add $install_dir to PATH yourself"
        fi
    fi
fi

init_args=()
[ "$live" = true ] && init_args+=(--live)
[ -n "$clients" ] && init_args+=(--client "$clients")
failed=0
# The ${a[@]+...} form keeps macOS bash 3.2 from calling an empty array unbound under set -u.
for repo in ${repos[@]+"${repos[@]}"}; do
    step "Setting up $repo"
    if index init "$repo" ${init_args[@]+"${init_args[@]}"}; then
        result ok "Set up $(pretty "$repo") for ${clients:-claude}"
    else
        failed=$((failed + 1))
        result fail "Set up $(pretty "$repo") failed; see above"
    fi
done

# ---- the VS Code extension ---------------------------------------------------------------------------
install_extension() {
    local vsix="$tmp/codegraph-vscode.vsix" editor
    if [ "$from_source" = true ]; then
        step 'Building the VS Code extension'
        (cd "$root/vscode-extension" && npm ci --no-audit --no-fund --loglevel=error && npm run compile && npm run package) || return 1
        vsix="$root/dist/codegraph-vscode.vsix"
    else
        step "Downloading codegraph-vscode.vsix ($version)"
        download "$(release_url codegraph-vscode.vsix)" "$vsix" || return 1
        verify "$vsix" "$tmp/SHA256SUMS"
    fi
    for editor in "${editors[@]}"; do
        step "Installing the extension with ${editor##*/}"
        "$editor" --install-extension "$vsix" --force || return 1
    done
}
if [ "$vscode" = true ]; then
    if install_extension; then
        result ok "VS Code extension $(extension_version "${editors[0]}") (reload open VS Code windows)"
        # The extension looks on its own PATH, then in the default folder. A VS Code started from the desktop does not
        # read ~/.bashrc, so anywhere but the default folder needs the setting.
        if [ "$install_dir" != "$default_install_dir" ]; then
            result skip "VS Code: set codegraph.installDir to $install_dir, or the extension installs its own copy"
        fi
    else
        failed=$((failed + 1))
        result fail 'VS Code extension: not installed; see above'
    fi
fi

# ---- summary -----------------------------------------------------------------------------------------
if [ "$needs_root" = true ]; then
    root_hint="DOTNET_ROOT=\"$dotnet_root\" "
else
    root_hint=""
fi
if on_path "$install_dir" || [ "$add_to_path" = true ]; then cmd=codegraph-index; else cmd="$install_dir/codegraph-index"; fi

step 'Done'
for row in ${results[@]+"${results[@]}"}; do
    case "${row%%|*}" in
        ok)   printf '  %s%s%s %s\n' "$c_green" "$g_ok" "$c_reset" "${row#*|}" ;;
        fail) printf '  %s%s%s %s\n' "$c_red" "$g_fail" "$c_reset" "${row#*|}" ;;
        *)    printf '  %s%s%s %s\n' "$c_yellow" "$g_skip" "$c_reset" "${row#*|}" ;;
    esac
done
cat <<EOT

Set up another repo (writes .mcp.json and .gitignore at its root, then builds the graph):
  ${root_hint}$cmd init /path/to/repo-or-solution [--live] [--client copilot,cursor,gemini,codex,roo,kilo,continue]
Keep a graph fresh while you work:
  ${root_hint}$cmd /path/to/App.sln --watch
EOT
[ "$failed" -eq 0 ] || die "$failed step(s) failed; see above"
