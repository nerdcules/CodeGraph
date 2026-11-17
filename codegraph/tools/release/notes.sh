#!/usr/bin/env bash
# Prints the CHANGELOG.md section for a tag (v1.2.0 → "## 1.2.0"), with its relative links pointed at the tag,
# followed by the install one-liners.
#   tools/release/notes.sh v1.2.0 CHANGELOG.md
set -euo pipefail
tag="$1"
changelog="$2"
version="${tag#v}"

awk -v version="$version" '
    /^## / { on = ($2 == version) ; next }
    /^---$/ { on = 0 }  # the navigation bar at the end of the file
    on { print }
' "$changelog" | sed -e :a -e '/^\n*$/{$d;N;ba' -e '}' |
    # A release page resolves a relative link against the repository root at the tag, but CHANGELOG.md's links are
    # relative to codegraph/: make each one absolute.
    sed -E -e "s#\]\((\./)?([^)#:][^):]*)\)#](https://github.com/nerdcules/CodeGraph/blob/$tag/codegraph/\2)#g" \
           -e 's#/codegraph/\.\./#/#g'

# A pre-release (a tag with a "-suffix") is not GitHub's latest release, and the installers install the latest one
# unless told which: name the tag in the one-liners.
sh_version="" ps_version="" prerelease=""
if [[ "$tag" == *-* ]]; then
    sh_version=" --version $tag" ps_version=" -Version $tag"
    prerelease="This is a pre-release. The installers install the latest full release unless given a tag, so the commands
below name this one. The Claude Code plugin installs the latest full release."
fi

cat <<MD

## Install
${prerelease:+
$prerelease
}
macOS / Linux:

\`\`\`bash
curl -fsSL https://raw.githubusercontent.com/nerdcules/CodeGraph/main/codegraph/install.sh | bash -s -- ~/dev/YourApp --add-to-path$sh_version
\`\`\`

Windows (PowerShell):

\`\`\`powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/nerdcules/CodeGraph/main/codegraph/install.ps1))) C:\dev\YourApp -AddToPath$ps_version
\`\`\`

Or download the archive for your platform below, unpack it anywhere, and run \`codegraph-index init\` in a repository.
\`SHA256SUMS\` holds the checksum of each file. The executables are not code-signed; on macOS, run
\`xattr -dr com.apple.quarantine <folder>\` on a folder unpacked from a browser download.
MD
