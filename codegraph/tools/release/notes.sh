#!/usr/bin/env bash
# Prints the CHANGELOG.md section for a tag (v1.2.0 → "## 1.2.0"), followed by the install one-liners.
#   tools/release/notes.sh v1.2.0 CHANGELOG.md
set -euo pipefail
tag="$1"
changelog="$2"
version="${tag#v}"

awk -v version="$version" '
    /^## / { on = ($2 == version) ; next }
    on { print }
' "$changelog" | sed -e :a -e '/^\n*$/{$d;N;ba' -e '}'

cat <<MD

## Install

macOS / Linux:

\`\`\`bash
curl -fsSL https://raw.githubusercontent.com/nerdcules/CodeGraph/main/codegraph/install.sh | bash -s -- ~/dev/YourApp --add-to-path
\`\`\`

Windows (PowerShell):

\`\`\`powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/nerdcules/CodeGraph/main/codegraph/install.ps1))) C:\dev\YourApp -AddToPath
\`\`\`

Or download the archive for your platform below, unpack it anywhere, and run \`codegraph-index init\` in a repository.
\`SHA256SUMS\` holds the checksum of each file. The executables are not code-signed; on macOS, run
\`xattr -dr com.apple.quarantine <folder>\` on a folder unpacked from a browser download.
MD
