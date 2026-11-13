#!/bin/sh
# Inside the container: run_one.sh <language> <profile> [seed] [dialect] [level]
# CORPUS_TAG names the toolchain the run is on (a suffix of the result file); CORPUS_NO_LIVE=1 skips the MCP live checks.
# Expects codegraph-index at /opt/codegraph/index (and codegraph-mcp at /opt/codegraph/mcp with the Obsidian
# plugin at /opt/codegraph/obsidian-plugin, for the plugin's live checks), the TypeScript extractor at /opt/codegraph/extractors/typescript
# and, for Java, a JDK at /opt/jdk, Maven at /opt/maven and scip-java at /opt/coursier/bin; for Go, a GOROOT at
# /opt/go and scip-go in /opt/gobin; for Rust, a toolchain (with rust-analyzer) at /opt/rust. Or all on PATH.
set -eu
lang=$1 profile=$2 seed=${3:-1} dialect=${4:-modern} level=${5:-}
name="$lang-$profile-s$seed"
[ "$dialect" = modern ] || name="$name-$dialect"
[ -z "${CORPUS_TAG:-}" ] || name="$name-$CORPUS_TAG"
export PATH="/opt/jdk/bin:/opt/maven/bin:/opt/coursier/bin:/opt/go/bin:/opt/gobin:/opt/rust/bin:$PATH"
# Go and Cargo work offline in a home of their own; the corpora have no third-party dependencies.
export GOTOOLCHAIN=local GOPATH="$HOME/go" GOCACHE="$HOME/go-build" CARGO_HOME="$HOME/.cargo" CARGO_NET_OFFLINE=true
[ -d /opt/go ] && export GOROOT=/opt/go
[ -d /opt/jdk ] && export JAVA_HOME=/opt/jdk
# scip-python is an npm package mounted at /opt/scip-python: give it a launcher on PATH.
if [ -f /opt/scip-python/package.json ] && command -v node >/dev/null; then
  mkdir -p "$HOME/bin"
  entry=$(node -p 'const b = require("/opt/scip-python/package.json").bin; typeof b === "string" ? b : Object.values(b)[0]')
  printf '#!/bin/sh\nexec node "/opt/scip-python/%s" "$@"\n' "$entry" > "$HOME/bin/scip-python"
  chmod +x "$HOME/bin/scip-python"
  export PATH="$HOME/bin:$PATH"
fi
# The JVM takes its home folder from the user database, not $HOME; coursier and Maven must look in $HOME.
export JAVA_TOOL_OPTIONS="-Duser.home=$HOME" COURSIER_CACHE="$HOME/.cache/coursier/v1"
# Maven writes to its local repository, so each container gets its own copy of the seeded one.
if [ -d /seed/m2 ] && [ ! -d "$HOME/.m2" ]; then cp -r /seed/m2 "$HOME/.m2"; fi
work=$(mktemp -d)
python3 /opt/corpus/gen.py --lang "$lang" --profile "$profile" --seed "$seed" --dialect "$dialect" ${level:+--level "$level"} --out "$work/$name"
set -- --corpus "$work/$name" --indexer "dotnet /opt/codegraph/index/codegraph-index.dll" --out "/results/$name.json"
if [ -z "${CORPUS_NO_LIVE:-}" ] && [ -f /opt/codegraph/mcp/codegraph-mcp.dll ] && [ -d /opt/codegraph/obsidian-plugin/lib ]; then
  set -- "$@" --mcp "dotnet /opt/codegraph/mcp/codegraph-mcp.dll" --plugin-lib /opt/codegraph/obsidian-plugin/lib
fi
python3 /opt/corpus/check.py "$@" || status=$?
cp "$work/$name/expected.json" "/results/$name.expected.json" 2>/dev/null || true
exit ${status:-0}
