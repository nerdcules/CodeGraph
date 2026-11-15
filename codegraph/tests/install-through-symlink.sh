#!/usr/bin/env bash
# Checks that a checkout reached through a symlink restores, builds and installs from source.
#
#   tests/install-through-symlink.sh [--tests] [SOURCE_REPO]
#
#   SOURCE_REPO  the git repository to clone, at its HEAD (default: the repository this script is in)
#   --tests      also run the test suite in the clone
#
# A symlink gives one folder two paths. The OS reports the working directory of a process with symlinks
# resolved (on macOS, /tmp is /private/tmp), and MSBuild tasks resolve a relative ProjectReference against
# that directory. A solution given by the other path then has each referenced project twice: it restores
# twice, builds in Debug next to Release, and fails at random; the test fixtures lose their folder in the
# output (see Directory.Build.targets). This script clones SOURCE_REPO into a folder, makes a symlink to
# that folder, and through the symlink runs restore, a Release build and install.sh --from-source. It
# fails when a project restores twice, when the Release build makes Debug output, or when a command fails.
# The restore and the build check the solution through the symlink. install.sh resolves its own root with
# pwd -P; the install checks that guard, and that the published executables start.
#
# The work folder is made under TMPDIR (default /tmp) and is deleted at the end.
set -euo pipefail
export DOTNET_CLI_UI_LANGUAGE=en # the checks read the English "Restored" lines

with_tests=false
source_repo=""
while [ $# -gt 0 ]; do
    case "$1" in
        --tests)   with_tests=true; shift ;;
        -h|--help) sed -n '2,19p' "$0"; exit 0 ;;
        -*) echo "unknown option: $1" >&2; exit 1 ;;
        *)  source_repo="$1"; shift ;;
    esac
done
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
[ -n "$source_repo" ] || source_repo="$(git -C "$here" rev-parse --show-toplevel)"

step() { printf '\n\033[36m==> %s\033[0m\n' "$1"; }
fail() { echo "FAIL: $1" >&2; exit 1; }

work="$(mktemp -d "${TMPDIR:-/tmp}/codegraph-symlink.XXXXXX")"
work="$(cd "$work" && pwd -P)"
trap 'rm -rf "$work"' EXIT
real="$work/real"
link="$work/link"
mkdir "$real"
ln -s "$real" "$link"
[ "$(cd "$link" && pwd -P)" = "$real" ] || fail "$link does not resolve to $real"

step "Cloning $source_repo into $real/src"
git clone -q "$source_repo" "$real/src"
sln_dir="$link/src/codegraph"
[ -f "$sln_dir/CodeGraph.slnx" ] || fail "no CodeGraph.slnx under $sln_dir"
echo "  $(git -C "$real/src" rev-parse --short HEAD), opened as $sln_dir"

# The symptom is checked before the exit code: the output names the cause when a command fails.
step "Restoring $sln_dir/CodeGraph.slnx"
ok=true
dotnet restore "$sln_dir/CodeGraph.slnx" --nologo 2>&1 | tee "$work/restore.log" || ok=false
restored="$(grep '^ *Restored ' "$work/restore.log" | grep -o '[^/]*\.csproj' || true)"
twice="$(echo "$restored" | sort | uniq -d)"
[ -z "$twice" ] || fail "restored more than once: $(echo "$twice" | tr '\n' ' ')"
[ "$ok" = true ] || fail 'restore failed'
# The check above reads the "Restored" lines; make sure they were there, one per project of the solution.
projects="$(grep -c '<Project Path=' "$sln_dir/CodeGraph.slnx")"
[ "$(echo "$restored" | grep -c .)" = "$projects" ] || fail "expected $projects Restored lines, got: $(echo "$restored" | tr '\n' ' ')"
echo "  each of the $projects projects restored once"

step "Building $sln_dir/CodeGraph.slnx in Release"
ok=true
dotnet build "$sln_dir/CodeGraph.slnx" -c Release -warnaserror --nologo --no-restore 2>&1 | tee "$work/build.log" || ok=false
debug="$(grep 'bin/Debug' "$work/build.log" || true)"
[ -z "$debug" ] || fail "Debug output in a Release build: $(echo "$debug" | tr -s ' ' | tr '\n' ';')"
[ "$ok" = true ] || fail 'build failed'
grep -q 'bin/Release' "$work/build.log" || fail 'the build printed no output paths; the Debug check needs them'
echo "  Release output only"

if [ "$with_tests" = true ]; then
    # Before the .NET tests, as in CI: the TypeScript tests skip themselves when these packages are missing.
    if command -v npm >/dev/null; then
        step "Installing the TypeScript extractor's packages"
        (cd "$sln_dir/extractors/typescript" && npm ci --no-audit --no-fund --loglevel=error)
    fi
    step 'Running the tests'
    # dotnet test reads global.json (the test runner) from the working directory, not from the solution.
    (cd "$sln_dir" && dotnet test "$sln_dir/CodeGraph.slnx" -c Release --no-build)
fi

step "Installing from source into $link/bin"
ok=true
(cd "$sln_dir" && ./install.sh --from-source --skip-tests --install-dir "$link/bin") 2>&1 | tee "$work/install.log" || ok=false
debug="$(grep 'bin/Debug' "$work/install.log" || true)"
[ -z "$debug" ] || fail "Debug output in the install: $(echo "$debug" | tr -s ' ' | tr '\n' ';')"
[ "$ok" = true ] || fail 'install.sh failed'
"$link/bin/codegraph-index" --version

echo
echo "ok: restore, Release build and install through $link"
