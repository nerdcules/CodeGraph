'use strict';

const crypto = require('crypto');
const fs = require('fs');
const os = require('os');
const path = require('path');

// Works out how to start codegraph-mcp and codegraph-index for a repo. `codegraph-index init` already wrote the
// server's command, arguments and environment (DOTNET_ROOT for a version-managed dotnet) into <repo>/.mcp.json,
// so the plugin can start exactly what Claude Code starts. But .mcp.json is part of the repo: a clone or a pull can
// change it, and whatever it names runs with the user's rights. So the entry is used only after the user has
// approved that exact entry (its fingerprint is kept in the plugin settings, per repo), the way Claude Code asks
// before starting a project's servers. Until then, and when the user ignores the entry, the plugin starts the
// installed binaries with the plain environment. Settings override; the install folder and PATH come last.

/** The codegraph entry of a repo's .mcp.json, or null. */
function readMcpEntry(repoRoot, readFile = fs.readFileSync) {
  try {
    const json = JSON.parse(readFile(path.join(repoRoot, '.mcp.json'), 'utf8'));
    const entry = json && json.mcpServers && json.mcpServers.codegraph;
    if (!entry || typeof entry.command !== 'string') {
      return null;
    }

    const env = {};
    if (entry.env && typeof entry.env === 'object') {
      for (const key of Object.keys(entry.env).sort()) {
        env[key] = String(entry.env[key]);
      }
    }

    return {
      command: entry.command,
      args: Array.isArray(entry.args) ? entry.args.map(String) : [],
      env,
    };
  } catch {
    return null;
  }
}

/** A stable fingerprint of an entry: the same command, arguments and environment give the same value. */
function fingerprint(entry) {
  return crypto.createHash('sha256').update(JSON.stringify([entry.command, entry.args, entry.env])).digest('hex');
}

/** What the settings say about a repo's entry: 'approved', 'ignored' or 'pending'. */
function decisionFor(settings, repoRoot, print) {
  const approved = settings.approvedRepoConfig || {};
  const ignored = settings.ignoredRepoConfig || {};
  if (approved[repoRoot] === print) {
    return 'approved';
  }

  return ignored[repoRoot] === print ? 'ignored' : 'pending';
}

/**
 * @param {{ repoRoot: string, mcpPath?: string, indexPath?: string, approvedRepoConfig?: object, ignoredRepoConfig?: object }} settings
 * @param {{ platform?: string, home?: string, exists?: (p: string) => boolean, readFile?: Function, env?: object }} [host]
 * @returns {{ mcp: object, index: object, dbPath: string, repoConfig: null | { fingerprint: string, decision: string, command: string, args: string[], env: object } }}
 */
function resolveLaunch(settings, host = {}) {
  const platform = host.platform || process.platform;
  const home = host.home || os.homedir();
  const exists = host.exists || fs.existsSync;
  const baseEnv = host.env || process.env;
  const exe = platform === 'win32' ? '.exe' : '';
  // Paths follow the host's platform, so a test for one platform runs the same on every other.
  const p = platform === 'win32' ? path.win32 : path.posix;
  const repoRoot = settings.repoRoot;
  const found = readMcpEntry(repoRoot, host.readFile);
  const repoConfig = found ? { ...found, fingerprint: fingerprint(found), decision: decisionFor(settings, repoRoot, fingerprint(found)) } : null;
  const entry = repoConfig && repoConfig.decision === 'approved' ? found : null;
  // install.sh's default folder, and install.ps1's on Windows.
  const folders = [p.join(home, '.codegraph', 'bin'), ...(platform === 'win32' ? ['C:\\tools\\codegraph'] : [])];
  const installed = (name) => folders.map((f) => p.join(f, name + exe)).find((candidate) => exists(candidate)) || null;

  const env = { ...baseEnv, ...(entry ? entry.env : {}) };

  let mcp;
  if (settings.mcpPath) {
    mcp = { command: settings.mcpPath, args: [] };
  } else if (entry) {
    mcp = { command: entry.command, args: entry.args };
  } else {
    mcp = { command: installed('codegraph-mcp') || 'codegraph-mcp', args: [] };
  }

  let index = settings.indexPath || null;
  if (!index && p.isAbsolute(mcp.command) && /codegraph-mcp(\.exe)?$/i.test(p.basename(mcp.command))) {
    const sibling = p.join(p.dirname(mcp.command), p.basename(mcp.command).replace(/codegraph-mcp/i, 'codegraph-index'));
    index = exists(sibling) ? sibling : null;
  }

  index = index || installed('codegraph-index') || 'codegraph-index';
  const dbArg = mcp.args.find((a) => a.toLowerCase().endsWith('.db'));
  const dbPath = dbArg ? p.resolve(repoRoot, dbArg) : p.join(repoRoot, '.codegraph', 'graph.db');
  return {
    mcp: { command: mcp.command, args: mcp.args, env, cwd: repoRoot },
    index: { command: index, env, cwd: repoRoot },
    dbPath,
    repoConfig,
  };
}

module.exports = { readMcpEntry, fingerprint, resolveLaunch };
