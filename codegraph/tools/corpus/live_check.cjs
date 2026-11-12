'use strict';

// Drives the real codegraph-mcp through the Obsidian plugin's own MCP client and parser, the way the live panel
// does, and writes what it saw as JSON for check.py to score. Called from check.py:
//
//   node live_check.cjs --lib <plugin lib/> --mcp '<json argv>' --db graph.db --cwd <corpus> \
//        --manifest <vault>/_code/.codegraph-export.json --mode queries|status|failure --in targets.json --out seen.json
//
// queries: index_status, then for each target {id, name}: find_symbol by name, callers_of, tests_for, impact_of.
// status:  index_status and callers_of on the first target, to see the staleness warning after a source edit.
// failure: a server that cannot start must fail the call with its reason, not hang or crash the client.

const fs = require('fs');
const path = require('path');

const args = {};
for (let i = 2; i < process.argv.length; i += 2) {
  args[process.argv[i].replace(/^--/, '')] = process.argv[i + 1];
}

const { McpClient } = require(path.join(path.resolve(args.lib), 'mcp.js'));
const { parseAnswer } = require(path.join(path.resolve(args.lib), 'rows.js'));
const { linkText } = require(path.join(path.resolve(args.lib), 'links.js'));
const ROW_LIKE = /^(project|file|type|method|property|field|event|external) {2}/;

function digest(text, manifest) {
  const answer = parseAnswer(text);
  const rows = answer.items.filter((i) => i.row).map((i) => i.row);
  const unparsed = answer.items.filter((i) => i.text !== undefined && ROW_LIKE.test(i.text)).map((i) => i.text);
  const unlinked = rows.filter((r) => r.kind !== 'external' && !linkText(manifest, r.id)).map((r) => r.id);
  return {
    stale: answer.stale !== null,
    ids: rows.map((r) => r.id),
    rows: rows.length,
    unparsed: unparsed.length,
    unparsed_sample: unparsed.slice(0, 3),
    unlinked: unlinked.length,
    unlinked_sample: unlinked.slice(0, 3),
  };
}

async function timed(fn) {
  const started = Date.now();
  const value = await fn();
  return [value, Date.now() - started];
}

async function main() {
  const argv = JSON.parse(args.mcp);
  const launch = { command: argv[0], args: [...argv.slice(1), path.resolve(args.db)], cwd: args.cwd, env: process.env };
  const seen = { mode: args.mode };

  if (args.mode === 'failure') {
    for (const [label, bad] of [
      ['missing command', { command: '/nonexistent/codegraph-mcp', args: [] }],
      ['server exits at start', { command: process.execPath, args: ['-e', 'console.error("You must install or update .NET"); process.exit(150)'] }],
    ]) {
      const client = new McpClient({ ...bad, cwd: args.cwd, env: process.env }, { timeoutMs: 15000 });
      try {
        await client.callTool('index_status', {});
        seen[label] = { failed: false };
      } catch (error) {
        seen[label] = { failed: true, message: String(error.message).slice(0, 200) };
      } finally {
        client.stop();
      }
    }

    fs.writeFileSync(args.out, JSON.stringify(seen, null, 1));
    return;
  }

  const manifest = JSON.parse(fs.readFileSync(args.manifest, 'utf8'));
  const targets = JSON.parse(fs.readFileSync(args.in, 'utf8'));
  const client = new McpClient(launch, { timeoutMs: 120000 });
  try {
    const [status, startMs] = await timed(() => client.callTool('index_status', {}));
    seen.status_stale = parseAnswer(status).stale !== null || /status\s*:\s*STALE/.test(status);
    seen.first_call_ms = startMs;
    if (args.mode === 'status') {
      seen.callers = targets.length > 0 ? digest(await client.callTool('callers_of', { id: targets[0].id }), manifest) : null;
      fs.writeFileSync(args.out, JSON.stringify(seen, null, 1));
      return;
    }

    seen.targets = {};
    const latencies = [];
    for (const target of targets) {
      const entry = {};
      const [found, findMs] = await timed(() => client.callTool('find_symbol', { query: target.name, limit: 200 }));
      latencies.push(findMs);
      entry.find = digest(found, manifest);
      entry.found = entry.find.ids.includes(target.id);
      for (const [key, tool, extra] of [['callers', 'callers_of', { depth: 1 }], ['tests', 'tests_for', {}], ['impact', 'impact_of', {}]]) {
        const [text, ms] = await timed(() => client.callTool(tool, { id: target.id, ...extra }));
        latencies.push(ms);
        entry[key] = digest(text, manifest);
      }

      seen.targets[target.id] = entry;
    }

    latencies.sort((a, b) => a - b);
    seen.calls = latencies.length;
    seen.median_ms = latencies.length ? latencies[Math.floor(latencies.length / 2)] : 0;
    seen.max_ms = latencies.length ? latencies[latencies.length - 1] : 0;
  } finally {
    client.stop();
  }

  fs.writeFileSync(args.out, JSON.stringify(seen, null, 1));
}

main().catch((error) => {
  fs.writeFileSync(args.out, JSON.stringify({ mode: args.mode, error: String(error && error.stack || error) }, null, 1));
  process.exit(1);
});
