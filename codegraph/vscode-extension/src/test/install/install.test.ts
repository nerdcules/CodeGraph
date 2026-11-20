// The extension's install and update code against a real release, outside VS Code (install.ts has no VS Code
// runtime import). It downloads the release archive for this machine once, so it runs only when asked:
//   CODEGRAPH_TEST_INSTALL_RELEASE=1 npm run test:install       (CODEGRAPH_TEST_RELEASE picks the tag, default v1.2.0)
// It then updates from an archive of that installation, with codegraph-mcp running from the folder. macOS and Linux
// replace the folder under the running program. On Windows the install folder is a junction to a folder per release:
// the update points it at the new release, and the old release stays until nothing runs from it. A real folder from
// before that layout is refused once while a program runs from it, and becomes a junction when nothing does. Then it
// updates again with the program stopped.
import * as assert from 'node:assert/strict';
import * as cp from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { test } from 'node:test';
import { installArchive, installRelease, isLinkedInstall, setRecentMs, versionsFolder } from '../../install';
import { executable, isInstallFolder } from '../../platform';

const enabled = process.env.CODEGRAPH_TEST_INSTALL_RELEASE === '1';
// The releases this test makes are seconds old; the cleanup keeps a folder younger than an hour unless told not to.
setRecentMs(0);
const tag = process.env.CODEGRAPH_TEST_RELEASE || 'v1.2.0';
const windows = process.platform === 'win32';
const token = { isCancellationRequested: false, onCancellationRequested: () => ({ dispose() {} }) };
const progress = { report() {} };
const lines: string[] = [];
const log = (line: string) => {
  lines.push(line);
  console.log(`    ${line}`);
};

/** Entries beside the install folder left by a staging, a swap or a link ("bin.new-…", ".bin.old-…", ".bin.next-…"). */
const leftovers = (dir: string) =>
  fs.readdirSync(path.dirname(dir)).filter((name) => name.startsWith(`.${path.basename(dir)}.`) && name !== path.basename(versionsFolder(dir)));

/** The release folders the junction layout keeps, on Windows. */
const releases = (dir: string) => (fs.existsSync(versionsFolder(dir)) ? fs.readdirSync(versionsFolder(dir)) : []);

const exited = (child: cp.ChildProcess) =>
  child.exitCode !== null || child.signalCode !== null ? Promise.resolve() : new Promise<void>((resolve) => child.once('exit', () => resolve()));

/** codegraph-mcp running from `dir` (its working folder elsewhere), as an assistant would run it. */
async function serve(dir: string, repo: string): Promise<cp.ChildProcess> {
  const stderr: string[] = [];
  const server = cp.spawn(path.join(dir, executable('codegraph-mcp')), ['--root', repo], { cwd: repo, stdio: ['pipe', 'ignore', 'pipe'], windowsHide: true });
  server.stderr!.setEncoding('utf8').on('data', (chunk: string) => stderr.push(chunk));
  await new Promise((resolve) => setTimeout(resolve, 3000));
  assert.equal(server.exitCode, null, `codegraph-mcp keeps running (stderr: ${stderr.join('').trim()})`);
  return server;
}

async function stop(server: cp.ChildProcess): Promise<void> {
  server.stdin!.end();
  server.kill();
  await exited(server);
  await new Promise((resolve) => setTimeout(resolve, 500));
}

test(`installs ${tag}, and an update with codegraph-mcp running ${windows ? 'points the junction at the new release' : 'replaces the folder'}`, { skip: !enabled && 'set CODEGRAPH_TEST_INSTALL_RELEASE=1 (downloads a release archive)', timeout: 600_000 }, async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-install-test-'));
  const dir = path.join(root, 'inst', 'bin');
  const repo = path.join(root, 'repo');
  fs.mkdirSync(repo);
  const servers: cp.ChildProcess[] = [];
  try {
    // 1. A fresh install through the whole path: download, checksum, unpack, swap.
    await installRelease(tag, dir, progress, token, log);
    assert.equal(fs.readFileSync(path.join(dir, 'VERSION'), 'utf8').trim(), tag.replace(/^v/, ''));
    assert.ok(isInstallFolder(dir), 'the result is a CodeGraph installation');
    assert.equal(isLinkedInstall(dir), windows, 'a junction on Windows, a real folder elsewhere');
    assert.deepEqual(leftovers(dir), []);

    // The update's archive: this installation packed again, so the test downloads once.
    const archive = path.join(root, 'update.tar.gz');
    cp.execFileSync('tar', ['-czf', path.basename(archive), '-C', path.relative(root, dir), '.'], { cwd: root });
    const marker = path.join(dir, 'from-the-old-installation');
    fs.writeFileSync(marker, '');

    // 2. An update with codegraph-mcp running from the folder.
    const server = await serve(dir, repo);
    servers.push(server);
    const before = windows ? fs.realpathSync(dir) : dir;
    lines.length = 0;
    await installArchive(archive, dir, token, log);
    assert.ok(!fs.existsSync(marker), 'the install folder holds the new release');
    assert.equal(server.exitCode, null, 'the running program is unaffected');
    assert.deepEqual(leftovers(dir), []);
    if (windows) {
      assert.ok(isLinkedInstall(dir));
      assert.notEqual(fs.realpathSync(dir), before, 'the junction points at a new folder');
      assert.ok(fs.existsSync(path.join(before, 'from-the-old-installation')), 'the old release stays whole while it runs');
      assert.equal(releases(dir).length, 2);
      assert.ok(lines.some((line) => line.startsWith(`kept ${before}`)), `the log names the kept folder (${lines.join(' | ')})`);
    }

    // 3. With the program stopped, the update goes through on every OS, and on Windows deletes the old release.
    await stop(server);
    fs.writeFileSync(marker, '');
    await installArchive(archive, dir, token, log);
    assert.ok(!fs.existsSync(marker), 'the folder was replaced');
    assert.equal(fs.readFileSync(path.join(dir, 'VERSION'), 'utf8').trim(), tag.replace(/^v/, ''));
    assert.deepEqual(leftovers(dir), []);
    if (windows) {
      assert.equal(releases(dir).length, 1, 'only the release the junction points at is left');
      assert.ok(!fs.existsSync(before));
    }

    if (windows) {
      // 4. A real folder from before the junction layout: refused once while a program runs from it, nothing changed.
      const legacy = path.join(root, 'legacy', 'bin');
      await installArchive(archive, legacy, token, log, false);
      assert.ok(!isLinkedInstall(legacy));
      const held = await serve(legacy, repo);
      servers.push(held);
      const entries = fs.readdirSync(legacy).sort();
      await assert.rejects(installArchive(archive, legacy, token, log), /could not move .* aside .*nothing was changed\. This occurs only once/s);
      assert.ok(!isLinkedInstall(legacy));
      assert.deepEqual(fs.readdirSync(legacy).sort(), entries, 'the installation is unchanged');
      assert.deepEqual(releases(legacy), [], 'the unpacked release is removed');
      assert.deepEqual(leftovers(legacy), []);

      // With the program stopped, the same update makes the junction and deletes the old folder.
      await stop(held);
      await installArchive(archive, legacy, token, log);
      assert.ok(isLinkedInstall(legacy));
      assert.equal(releases(legacy).length, 1);
      assert.deepEqual(leftovers(legacy), []);
    }
  } finally {
    for (const server of servers) {
      if (server.exitCode === null && server.signalCode === null) {
        server.kill();
        await exited(server);
      }
    }
    fs.rmSync(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 500 });
  }
});
