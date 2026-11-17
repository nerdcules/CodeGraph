// The extension's install and update code against a real release, outside VS Code (install.ts has no VS Code
// runtime import). It downloads the release archive for this machine once, so it runs only when asked:
//   CODEGRAPH_TEST_INSTALL_RELEASE=1 npm run test:install       (CODEGRAPH_TEST_RELEASE picks the tag, default v1.2.0)
// It then updates from an archive of that installation, with codegraph-mcp running from the folder: Windows must
// refuse and change nothing, macOS and Linux replace the folder under the running program. Then it updates again
// with the program stopped.
import * as assert from 'node:assert/strict';
import * as cp from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { test } from 'node:test';
import { installArchive, installRelease } from '../../install';
import { executable, isInstallFolder } from '../../platform';

const enabled = process.env.CODEGRAPH_TEST_INSTALL_RELEASE === '1';
const tag = process.env.CODEGRAPH_TEST_RELEASE || 'v1.2.0';
const token = { isCancellationRequested: false, onCancellationRequested: () => ({ dispose() {} }) };
const progress = { report() {} };
const log = (line: string) => console.log(`    ${line}`);

/** Entries beside the install folder left by a staging or swap (".bin.new-…", ".bin.old-…"). */
const leftovers = (dir: string) => fs.readdirSync(path.dirname(dir)).filter((name) => name.startsWith(`.${path.basename(dir)}.`));

const exited = (child: cp.ChildProcess) =>
  child.exitCode !== null || child.signalCode !== null ? Promise.resolve() : new Promise<void>((resolve) => child.once('exit', () => resolve()));

test(`installs ${tag}, and an update with codegraph-mcp running ${process.platform === 'win32' ? 'is refused' : 'replaces the folder'}`, { skip: !enabled && 'set CODEGRAPH_TEST_INSTALL_RELEASE=1 (downloads a release archive)', timeout: 600_000 }, async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-install-test-'));
  const dir = path.join(root, 'inst', 'bin');
  const repo = path.join(root, 'repo');
  fs.mkdirSync(repo);
  let server: cp.ChildProcess | undefined;
  try {
    // 1. A fresh install through the whole path: download, checksum, unpack, swap.
    await installRelease(tag, dir, progress, token, log);
    assert.equal(fs.readFileSync(path.join(dir, 'VERSION'), 'utf8').trim(), tag.replace(/^v/, ''));
    assert.ok(isInstallFolder(dir), 'the result is a CodeGraph installation');
    assert.deepEqual(leftovers(dir), []);

    // The update's archive: this installation packed again, so the test downloads once.
    const archive = path.join(root, 'update.tar.gz');
    cp.execFileSync('tar', ['-czf', archive, '-C', dir, '.']);
    const marker = path.join(dir, 'from-the-old-installation');
    fs.writeFileSync(marker, '');

    // 2. codegraph-mcp running from the folder (its working folder elsewhere), as an assistant would run it.
    const stderr: string[] = [];
    server = cp.spawn(path.join(dir, executable('codegraph-mcp')), ['--root', repo], { cwd: repo, stdio: ['pipe', 'ignore', 'pipe'], windowsHide: true });
    server.stderr!.setEncoding('utf8').on('data', (chunk: string) => stderr.push(chunk));
    await new Promise((resolve) => setTimeout(resolve, 3000));
    assert.equal(server.exitCode, null, `codegraph-mcp keeps running (stderr: ${stderr.join('').trim()})`);
    const before = fs.readdirSync(dir).sort();

    if (process.platform === 'win32') {
      await assert.rejects(installArchive(archive, dir, token, log), /could not move .* aside .*nothing was changed/s);
      assert.deepEqual(fs.readdirSync(dir).sort(), before, 'the installation is unchanged');
      assert.ok(fs.existsSync(marker));
      assert.deepEqual(leftovers(dir), [], 'the staging folder is removed');
    } else {
      await installArchive(archive, dir, token, log);
      assert.ok(!fs.existsSync(marker), 'the folder was replaced');
      assert.equal(server.exitCode, null, 'the running program is unaffected');
      assert.deepEqual(leftovers(dir), []);
      fs.writeFileSync(marker, '');
    }

    // 3. With the program stopped, the update goes through on every OS.
    server.stdin!.end();
    server.kill();
    await exited(server);
    await new Promise((resolve) => setTimeout(resolve, 500));
    await installArchive(archive, dir, token, log);
    assert.ok(!fs.existsSync(marker), 'the folder was replaced');
    assert.equal(fs.readFileSync(path.join(dir, 'VERSION'), 'utf8').trim(), tag.replace(/^v/, ''));
    assert.deepEqual(leftovers(dir), []);
  } finally {
    if (server && server.exitCode === null && server.signalCode === null) {
      server.kill();
      await exited(server);
    }
    fs.rmSync(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 500 });
  }
});
