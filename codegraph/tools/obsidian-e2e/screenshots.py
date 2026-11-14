#!/usr/bin/env python3
"""
Take the documentation screenshots of the Obsidian plugin, against this repo's own graph, in real Obsidian.

    python3 tools/obsidian-e2e/screenshots.py                       # writes docs/images/obsidian-*.png
    python3 tools/obsidian-e2e/screenshots.py --version 1.8.9 --type CodeGraph.Core.GraphStore --member Replace

Needs the e2e image (run.py builds it), a graph of this repo (codegraph-index init) and an install of
codegraph-index and codegraph-mcp (install.sh). Copies the repo's sources and graph into a scratch folder, mounts
that copy at the repo's own path so the graph's file paths hold. The settings shot shows a
placeholder repo folder, so no home folder lands in the docs, and runs Obsidian with --network none.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))

CONTAINER = r"""
set -eu
vault="$HOME/CodeGraph"
mkdir -p "$vault/.obsidian/plugins" "$vault/docs" "$HOME/.config/obsidian" "$HOME/bin"
printf '#!/bin/sh\nexec dotnet /opt/codegraph/bin/codegraph-index.dll "$@"\n' > "$HOME/bin/codegraph-index"
chmod +x "$HOME/bin/codegraph-index"
export PATH="$HOME/bin:$PATH"
cp -r /opt/codegraph/obsidian-plugin "$HOME/plugin-src"
node "$HOME/plugin-src/install-plugin.mjs" "$vault" > /dev/null
# Approve the repo's .mcp.json entry up front, as a user does in the plugin's dialog. Without that the plugin
# falls back to ~/.codegraph/bin and PATH, which the container does not have, and every panel says ENOENT.
approved=$(node -e 'const c = require(process.argv[1]); console.log(c.fingerprint(c.readMcpEntry(process.argv[2])))' \
  "$HOME/plugin-src/lib/config.js" "$ROOT")
cat > "$vault/.obsidian/plugins/codegraph/data.json" <<EOF
{ "repoRoot": "$ROOT", "folder": "_code", "autoRefresh": true, "depth": 1, "approvedRepoConfig": { "$ROOT": "$approved" } }
EOF
echo '["codegraph"]' > "$vault/.obsidian/community-plugins.json"
echo '{ "theme": "obsidian", "baseFontSize": 15 }' > "$vault/.obsidian/appearance.json"
# The graph view: settings closed, labels only when zoomed in, a colour per kind of note.
cat > "$vault/.obsidian/graph.json" <<'EOF'
{ "close": true, "showTags": false, "showOrphans": true, "textFadeMultiplier": -3, "nodeSizeMultiplier": 1.1,
  "lineSizeMultiplier": 0.6, "scale": 0.4, "search": "path:_code",
  "colorGroups": [
    { "query": "path:_code/Reports", "color": { "a": 1, "rgb": 15105570 } },
    { "query": "path:_code/Projects", "color": { "a": 1, "rgb": 5025616 } },
    { "query": "path:_code/Types/CodeGraph.Tests", "color": { "a": 1, "rgb": 9807270 } },
    { "query": "path:_code/Types/CodeGraph.Core", "color": { "a": 1, "rgb": 4359668 } },
    { "query": "path:_code/Types", "color": { "a": 1, "rgb": 10181046 } } ] }
EOF
printf '# Checkout redesign\n\nThe basket total comes from the graph store.\n' > "$vault/docs/Checkout redesign.md"
cat > "$HOME/.config/obsidian/obsidian.json" <<EOF
{ "vaults": { "c0de0e2e00000002": { "path": "$vault", "ts": 1700000000000, "open": true } }, "updateDisabled": true }
EOF
xvfb-run -a -s "-screen 0 1600x1000x24" /opt/obsidian/obsidian --no-sandbox --disable-gpu --disable-dev-shm-usage \
  --force-device-scale-factor=1 --remote-debugging-port=9222 --remote-allow-origins='*' > /results/obsidian.log 2>&1 &
node /opt/e2e/shots.mjs --port 9222 --out /results --type "$TYPE" --member "$MEMBER"
"""


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--version", default="1.8.9", help="Obsidian version: an image codegraph-obsidian:<version>")
    ap.add_argument("--bin", default=os.path.expanduser("~/.codegraph/bin"), help="installed codegraph-index and codegraph-mcp")
    ap.add_argument("--type", default="CodeGraph.Core.GraphStore", help="the type note to show in the live panel")
    ap.add_argument("--member", default="Replace", help="the member to show callers, impact and tests for")
    ap.add_argument("--out", default=os.path.join(REPO, "docs", "images"))
    a = ap.parse_args()
    root = subprocess.run(["git", "-C", REPO, "rev-parse", "--show-toplevel"], text=True, capture_output=True, check=True).stdout.strip()
    if not os.path.exists(os.path.join(root, ".codegraph", "graph.db")):
        sys.exit(f"no graph at {root}/.codegraph: run codegraph-index init first")
    with tempfile.TemporaryDirectory(prefix="codegraph-shots-") as work:
        copy = os.path.join(work, "repo")
        results = os.path.join(work, "results")
        os.makedirs(results)
        # obj/ keeps only its generated sources: the graph indexed them, and a missing one reads as a stale index.
        subprocess.run(["rsync", "-am", "--exclude", "bin/", "--exclude", "node_modules/", "--exclude", "artifacts/",
                        "--include", "obj/**/", "--include", "obj/**.cs", "--exclude", "obj/**",
                        root + "/", copy + "/"], check=True)
        # The server as `codegraph-index init` would write it, but for the container's paths.
        json.dump({"mcpServers": {"codegraph": {"command": "dotnet", "args": ["/opt/codegraph/bin/codegraph-mcp.dll"]}}},
                  open(os.path.join(copy, ".mcp.json"), "w"))
        # Test projects compile source files shipped in NuGet packages; the graph holds them, so mount them too.
        packages = os.environ.get("NUGET_PACKAGES") or os.path.expanduser("~/.nuget/packages")
        nuget = ["-v", f"{packages}:{packages}:ro"] if os.path.isdir(packages) else []
        cmd = ["docker", "run", "--rm", "--network=none", "--shm-size=1g", "--cpus=2", "--memory=4g",
               "--user", f"{os.getuid()}:{os.getgid()}", "-e", f"ROOT={root}", "-e", f"TYPE={a.type}", "-e", f"MEMBER={a.member}",
               "-v", f"{copy}:{root}", "-v", f"{a.bin}:/opt/codegraph/bin:ro", *nuget,
               "-v", f"{os.path.join(REPO, 'obsidian-plugin')}:/opt/codegraph/obsidian-plugin:ro",
               "-v", f"{HERE}:/opt/e2e:ro", "-v", f"{results}:/results",
               "--entrypoint", "sh", f"codegraph-obsidian:{a.version}", "-c", CONTAINER]
        p = subprocess.run(cmd, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        print(p.stdout[-4000:])
        if p.returncode != 0:
            log = os.path.join(results, "obsidian.log")
            if os.path.exists(log):
                print(open(log).read()[-3000:])
            return p.returncode
        os.makedirs(a.out, exist_ok=True)
        for f in sorted(os.listdir(results)):
            if f.endswith(".png"):
                shutil.copy(os.path.join(results, f), os.path.join(a.out, "obsidian-" + f))
                print("wrote", os.path.join(a.out, "obsidian-" + f))
    return 0


if __name__ == "__main__":
    sys.exit(main())
