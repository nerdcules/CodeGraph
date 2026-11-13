#!/usr/bin/env bash
# Publishes codegraph-index and codegraph-mcp self-contained for each runtime identifier given, bundles the
# TypeScript extractor with them, and writes one archive per platform into dist/:
#
#   tools/release/package.sh linux-x64 linux-arm64        → dist/codegraph-linux-x64.tar.gz, ...
#   CODEGRAPH_VERSION=v1.2.0 tools/release/package.sh win-x64   → dist/codegraph-win-x64.zip
#
# The archive holds the two executables, their runtime (no .NET install needed to run them; C# indexing still
# needs the .NET SDK for MSBuild) and extractors/typescript with its packages (Node.js needed at run time).
# Cross-architecture publishing works on the same OS, so CI runs this once per OS. Release workflow and
# install scripts rely on the names above.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
version="${CODEGRAPH_VERSION:-}"
version="${version#v}"
[ $# -gt 0 ] || { echo "usage: $0 <rid>..." >&2; exit 1; }

dist="$root/dist"
mkdir -p "$dist"

extractor="$root/extractors/typescript"
if command -v npm >/dev/null; then
    echo "==> TypeScript extractor packages"
    (cd "$extractor" && npm ci --omit=dev --no-audit --no-fund --loglevel=error)
else
    echo 'npm is not on PATH; the archives will not hold the TypeScript extractor' >&2
fi

for rid in "$@"; do
    out="$root/artifacts/package/$rid"
    rm -rf "${out:?}"
    mkdir -p "$out"
    echo "==> Publishing $rid${version:+ ($version)}"
    for project in src/CodeGraph.Indexer src/CodeGraph.Mcp; do
        dotnet publish "$root/$project" -c Release -r "$rid" --self-contained true -p:PublishReadyToRun=true \
            ${version:+-p:Version="$version"} -o "$out" --nologo -v quiet
    done

    if [ -d "$extractor/node_modules" ]; then
        mkdir -p "$out/extractors/typescript"
        cp -R "$extractor/cli.mjs" "$extractor/lib" "$extractor/package.json" "$extractor/package-lock.json" "$extractor/node_modules" "$out/extractors/typescript/"
    fi
    cp "$root/../LICENSE" "$out/LICENSE"
    printf '%s\n' "${version:-0.0.0}" > "$out/VERSION"

    case "$rid" in
        win-*)
            archive="$dist/codegraph-$rid.zip"
            rm -f "$archive"
            (cd "$out" && 7z a -tzip -bso0 -bsp0 "$archive" .)
            ;;
        *)
            archive="$dist/codegraph-$rid.tar.gz"
            chmod +x "$out/codegraph-index" "$out/codegraph-mcp"
            tar -czf "$archive" -C "$out" .
            ;;
    esac
    echo "    $(du -h "$archive" | cut -f1)  $archive"
done
