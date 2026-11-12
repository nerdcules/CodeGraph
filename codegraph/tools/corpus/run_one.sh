#!/bin/sh
# Inside the container: run_one.sh <language> <profile> [seed]
# Expects codegraph-index at /opt/codegraph/index, the TypeScript extractor at /opt/codegraph/extractors/typescript
# and, for Java, a JDK at /opt/jdk, Maven at /opt/maven and scip-java at /opt/coursier/bin (or on PATH).
set -eu
lang=$1 profile=$2 seed=${3:-1}
name="$lang-$profile-s$seed"
export PATH="/opt/jdk/bin:/opt/maven/bin:/opt/coursier/bin:$PATH"
[ -d /opt/jdk ] && export JAVA_HOME=/opt/jdk
# The JVM takes its home folder from the user database, not $HOME; coursier and Maven must look in $HOME.
export JAVA_TOOL_OPTIONS="-Duser.home=$HOME" COURSIER_CACHE="$HOME/.cache/coursier/v1"
# Maven writes to its local repository, so each container gets its own copy of the seeded one.
if [ -d /seed/m2 ] && [ ! -d "$HOME/.m2" ]; then cp -r /seed/m2 "$HOME/.m2"; fi
work=$(mktemp -d)
python3 /opt/corpus/gen.py --lang "$lang" --profile "$profile" --seed "$seed" --out "$work/$name"
python3 /opt/corpus/check.py --corpus "$work/$name" --indexer "dotnet /opt/codegraph/index/codegraph-index.dll" \
  --out "/results/$name.json" || status=$?
cp "$work/$name/expected.json" "/results/$name.expected.json" 2>/dev/null || true
exit ${status:-0}
