'use strict';

const { spawn } = require('child_process');

/**
 * Runs a command to completion. Resolves with its exit code and output; rejects only when it cannot start.
 * @returns {Promise<{ code: number, stdout: string, stderr: string }>}
 */
function run(command, args, options = {}, spawnFn = spawn) {
  return new Promise((resolve, reject) => {
    let child;
    try {
      child = spawnFn(command, args, { cwd: options.cwd, env: options.env, windowsHide: true });
    } catch (error) {
      reject(error);
      return;
    }

    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (d) => { stdout += d; });
    child.stderr.on('data', (d) => {
      stderr += d;
      if (options.onStderr) {
        options.onStderr(String(d));
      }
    });
    child.on('error', reject);
    child.on('close', (code) => resolve({ code: code === null ? 1 : code, stdout, stderr }));
  });
}

/** The arguments for `codegraph-index export obsidian` into <vaultBase>/<folder>. */
function exportArgs(dbPath, outDir, folder) {
  return ['export', 'obsidian', '--db', dbPath, '--out', outDir, '--link-root', folder];
}

/** The last line of the exporter's summary, e.g. "obsidian: 3 written, 120 unchanged, 0 deleted in …". */
function summaryOf(stdout) {
  const lines = String(stdout).split(/\r?\n/).filter((l) => l.startsWith('obsidian:'));
  return lines.length > 0 ? lines[lines.length - 1].replace(/ in .*$/, '') : null;
}

module.exports = { run, exportArgs, summaryOf };
