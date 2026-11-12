#!/bin/sh
# Inside the container: run_e2e.sh [language] [profile]
# Generates a corpus codebase, indexes it, sets up a vault with the CodeGraph plugin pointed at it, starts Obsidian
# under Xvfb with the DevTools protocol on a local port, and runs e2e.mjs against it. Results go to /results.
# Expects /opt/codegraph/index (codegraph-index), /opt/codegraph/mcp (codegraph-mcp), /opt/codegraph/obsidian-plugin
# and /opt/corpus (gen.py), mounted read-only by run.py.
set -eu
lang=${1:-csharp} profile=${2:-classic}
name="obsidian-$OBSIDIAN_VERSION-$lang-$profile"
work=$(mktemp -d)
repo="$work/repo"
vault="$work/vault"
mkdir -p "$HOME/bin" "$vault/.obsidian/plugins" "$HOME/.config/obsidian"

# codegraph-index on PATH: the plugin finds it there when .mcp.json names `dotnet` rather than codegraph-mcp.
printf '#!/bin/sh\nexec dotnet /opt/codegraph/index/codegraph-index.dll "$@"\n' > "$HOME/bin/codegraph-index"
chmod +x "$HOME/bin/codegraph-index"
export PATH="$HOME/bin:$PATH"

python3 /opt/corpus/gen.py --lang "$lang" --profile "$profile" --out "$repo" > /dev/null
target=$(python3 -c "import json,sys; print(json.load(open(sys.argv[1]))['target'])" "$repo/expected.json")
codegraph-index "$repo/$target" > "$work/index.log" 2>&1 || { cat "$work/index.log"; exit 1; }

# The repo as `codegraph-index init` leaves it: the server in .mcp.json.
cat > "$repo/.mcp.json" <<EOF
{ "mcpServers": { "codegraph": { "command": "dotnet", "args": ["/opt/codegraph/mcp/codegraph-mcp.dll"] } } }
EOF

# The plugin, installed the way users install it, and its settings. The .mcp.json entry is approved up front, as
# a user does in the plugin's dialog: without that the plugin falls back to the installed binaries.
cp -r /opt/codegraph/obsidian-plugin "$work/plugin-src"
node "$work/plugin-src/install-plugin.mjs" "$vault" > /dev/null
approved=$(node -e 'const c = require(process.argv[1]); console.log(c.fingerprint(c.readMcpEntry(process.argv[2])))' \
  "$work/plugin-src/lib/config.js" "$repo")
cat > "$vault/.obsidian/plugins/codegraph/data.json" <<EOF
{ "repoRoot": "$repo", "folder": "_code", "autoRefresh": true, "depth": 1, "approvedRepoConfig": { "$repo": "$approved" } }
EOF
echo '["codegraph"]' > "$vault/.obsidian/community-plugins.json"
mkdir -p "$vault/docs"
printf '# Feature notes\n\nWritten by people; links into _code/.\n' > "$vault/docs/README.md"

# Obsidian opens the vault listed as open in its own config, with no starter screen.
cat > "$HOME/.config/obsidian/obsidian.json" <<EOF
{ "vaults": { "c0de0e2e00000001": { "path": "$vault", "ts": 1700000000000, "open": true } }, "updateDisabled": true }
EOF

port=9222
xvfb-run -a -s "-screen 0 1440x900x24" /opt/obsidian/obsidian --no-sandbox --disable-gpu --disable-dev-shm-usage \
  --remote-debugging-port=$port --remote-allow-origins='*' > "$work/obsidian.log" 2>&1 &
obsidian=$!
status=0
node /opt/e2e/e2e.mjs --port $port --vault "$vault" --repo "$repo" --lang "$lang" --out "/results/$name" || status=$?
kill "$obsidian" 2>/dev/null || true
cp "$work/obsidian.log" "/results/$name.obsidian.log" 2>/dev/null || true
exit $status
