// The install layout of Windows (issue #9), outside VS Code: the install folder is a link to one folder per release.
// These tests make the link layout on every OS (a junction on Windows, a symbolic link elsewhere) with a small archive
// that only looks like a release. install.test.ts runs a real release with codegraph-mcp running from it.
import * as assert from 'node:assert/strict';
import * as cp from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { test } from 'node:test';
import { installArchive, isLinkedInstall, versionsFolder } from '../../install';
import { followFileLinks, isInstallFolder } from '../../platform';

const token = { isCancellationRequested: false };
const quiet = () => undefined;

/** A .tar.gz with the files isInstallFolder and the installer look for, and a VERSION file. */
function fakeRelease(root: string, version: string): string {
  const folder = path.join(root, `release-${version}`);
  fs.mkdirSync(folder, { recursive: true });
  for (const name of ['codegraph-index.dll', 'codegraph-mcp.dll', 'codegraph-index', 'codegraph-mcp', 'codegraph-index.exe', 'codegraph-mcp.exe']) {
    fs.writeFileSync(path.join(folder, name), version);
  }
  fs.writeFileSync(path.join(folder, 'VERSION'), `${version}\n`);
  const archive = path.join(root, `codegraph-${version}.tar.gz`);
  // Relative names: GNU tar reads "C:\…" as a remote host, and a test can run where it comes first on PATH.
  cp.execFileSync('tar', ['-czf', path.basename(archive), '-C', path.basename(folder), '.'], { cwd: root });
  return archive;
}

const version = (dir: string) => fs.readFileSync(path.join(dir, 'VERSION'), 'utf8').trim();

test('the linked layout: install, update, old releases deleted, the path never changes', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-layout-'));
  try {
    const dir = path.join(root, 'tools', 'codegraph');
    const versions = versionsFolder(dir);
    assert.equal(versions, path.join(root, 'tools', '.codegraph.versions'));

    await installArchive(fakeRelease(root, '1.0.0'), dir, token, quiet, true);
    assert.ok(isLinkedInstall(dir), 'the install folder is a link');
    assert.equal(version(dir), '1.0.0');
    assert.ok(isInstallFolder(dir));
    const first = fs.realpathSync(dir);
    assert.equal(path.dirname(first), fs.realpathSync(versions));
    assert.match(path.basename(first), /^1\.0\.0-/);

    await installArchive(fakeRelease(root, '1.1.0'), dir, token, quiet, true);
    assert.ok(isLinkedInstall(dir));
    assert.equal(version(dir), '1.1.0');
    assert.ok(!fs.existsSync(first), 'the old release is deleted when nothing runs from it');
    assert.deepEqual(fs.readdirSync(versions).length, 1);
    assert.deepEqual(fs.readdirSync(path.dirname(dir)).sort(), ['.codegraph.versions', 'codegraph'], 'no staging or link left beside it');
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('the first update of a real folder moves it among the releases and deletes it', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-layout-'));
  try {
    const dir = path.join(root, 'codegraph');
    await installArchive(fakeRelease(root, '1.0.0'), dir, token, quiet, false);
    assert.ok(!isLinkedInstall(dir), 'the old layout is a real folder');
    fs.writeFileSync(path.join(dir, 'from-the-old-installation'), '');

    await installArchive(fakeRelease(root, '1.1.0'), dir, token, quiet, true);
    assert.ok(isLinkedInstall(dir));
    assert.equal(version(dir), '1.1.0');
    assert.ok(!fs.existsSync(path.join(dir, 'from-the-old-installation')));
    assert.equal(fs.readdirSync(versionsFolder(dir)).length, 1, 'the old folder is deleted');
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('an update refuses a folder that holds other programs, linked or not', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-layout-'));
  try {
    const dir = path.join(root, 'bin');
    fs.mkdirSync(dir);
    fs.writeFileSync(path.join(dir, 'git'), '');
    await assert.rejects(installArchive(fakeRelease(root, '1.0.0'), dir, token, quiet, true), /not a CodeGraph installation/);
    assert.deepEqual(fs.readdirSync(dir), ['git']);
    assert.ok(!fs.existsSync(versionsFolder(dir)));
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('followFileLinks follows a link on the file, not a link on its folder', (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-links-'));
  try {
    const release = path.join(root, 'versions', '1.0.0');
    fs.mkdirSync(release, { recursive: true });
    fs.writeFileSync(path.join(release, 'codegraph-index'), '');
    const dir = path.join(root, 'codegraph');
    fs.symlinkSync(release, dir, 'junction');

    const plain = path.join(dir, 'codegraph-index');
    assert.equal(followFileLinks(plain), plain, 'the junction on the folder stays');
    assert.equal(followFileLinks(path.join(root, 'missing')), path.join(root, 'missing'));

    const bin = path.join(root, 'bin');
    fs.mkdirSync(bin);
    const link = path.join(bin, 'codegraph-index');
    try {
      fs.symlinkSync(plain, link, 'file');
    } catch (error) {
      // A file link on Windows needs Developer Mode or an administrator; a junction does not.
      t.diagnostic(`no file link here (${(error as NodeJS.ErrnoException).code}); only the folder case ran`);
      return;
    }
    assert.equal(followFileLinks(link), plain, 'the link on the file is followed to the install folder, junction kept');
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
