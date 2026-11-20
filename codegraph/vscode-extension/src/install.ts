// Downloads a CodeGraph release into the install folder: the archive for this machine, checked against the
// release's SHA256SUMS like install.sh and install.ps1 do, unpacked beside the old folder and swapped in, so a failed
// or refused update leaves the old installation whole.
//
// On Windows a folder cannot be renamed while a program runs from it, and codegraph-mcp runs for as long as an
// assistant session is open (issue #9). There the install folder is a junction to one folder per release, in
// `.<name>.versions` beside it. An update unpacks the new release into a new folder and points the junction at it.
// Programs that run now keep the old folder until they stop; the next update deletes each old folder that nothing
// runs from. The path of the install folder does not change, so PATH and the MCP configs that name it stay correct.
// The executables start themselves again from the release folder (ReleaseFolder.cs), so a process that started
// through the junction does not load files of a later release.
import * as cp from 'child_process';
import * as crypto from 'crypto';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { Readable, Transform } from 'stream';
import { pipeline } from 'stream/promises';
import type { ReadableStream as WebReadableStream } from 'stream/web';
import type * as vscode from 'vscode';
import { assetName, checksumFor, isInstallFolder, repository, runtimeId, samePath } from './platform';

const userAgent = 'codegraph-vscode-extension';

/** The tag of the newest published release, such as "v1.2.0". */
export async function latestTag(): Promise<string> {
  const response = await fetch(`https://api.github.com/repos/${repository}/releases/latest`, {
    headers: { Accept: 'application/vnd.github+json', 'User-Agent': userAgent },
  });
  if (!response.ok) {
    throw new Error(`GitHub answered ${response.status} for the latest release`);
  }
  const body = (await response.json()) as { tag_name?: string };
  if (!body.tag_name) {
    throw new Error('the latest release has no tag');
  }
  return body.tag_name;
}

/**
 * Installs release `tag` into `dir`. Refuses a folder that holds anything but a CodeGraph installation (a shared
 * folder such as ~/.local/bin must never be replaced). On macOS and Linux the release is unpacked into a new folder
 * beside `dir`, then `dir` is moved aside and the new folder moved in; only then is the old one deleted. On Windows
 * `dir` is a junction to a folder per release (see the top of this file), so a program that runs from `dir` does not
 * stop the update. Only the first update of a real folder (an installation made before the junction layout) must
 * move it, and a running program stops that one, before anything changed.
 */
export async function installRelease(
  tag: string,
  dir: string,
  progress: vscode.Progress<{ message?: string; increment?: number }>,
  token: vscode.CancellationToken,
  log: (line: string) => void,
): Promise<void> {
  const rid = runtimeId();
  if (!rid) {
    throw new Error(`no CodeGraph release is built for ${process.platform}-${process.arch}`);
  }
  if (!isInstallFolder(dir)) {
    throw new Error(`${dir} holds files that are not a CodeGraph installation; set codegraph.installDir to a folder of its own`);
  }
  const asset = assetName(rid);
  const base = `https://github.com/${repository}/releases/download/${tag}`;
  const scratch = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'codegraph-install-'));
  try {
    progress.report({ message: `downloading ${asset}` });
    const archive = path.join(scratch, asset);
    const hash = await download(`${base}/${asset}`, archive, progress, token);
    log(`downloaded ${base}/${asset}`);

    const sums = await (await fetchOk(`${base}/SHA256SUMS`)).text();
    const expected = checksumFor(sums, asset);
    if (!expected) {
      throw new Error(`SHA256SUMS of ${tag} lists no ${asset}`);
    }
    if (expected !== hash) {
      throw new Error(`checksum mismatch for ${asset}: expected ${expected}, got ${hash}`);
    }
    log(`checksum ok (${hash})`);

    progress.report({ message: `installing into ${dir}` });
    await installArchive(archive, dir, token, log);
    log(`installed ${tag} into ${dir}`);
  } finally {
    await fs.promises.rm(scratch, { recursive: true, force: true });
  }
}

/**
 * Unpacks a release archive (already checked) and swaps it in (see installRelease). `linked` picks the junction
 * layout, the default on Windows; a test can pass it on any OS, where the link is a symbolic link.
 * Exported for the install test, which updates from an archive of an installation instead of downloading again.
 */
export async function installArchive(
  archive: string,
  dir: string,
  token: { isCancellationRequested: boolean },
  log: (line: string) => void,
  linked: boolean = process.platform === 'win32',
): Promise<void> {
  if (!isInstallFolder(dir)) {
    throw new Error(`${dir} holds files that are not a CodeGraph installation; set codegraph.installDir to a folder of its own`);
  }
  // Beside the install folder, on the same disk, so the swap is renames only.
  const parent = path.dirname(dir);
  await fs.promises.mkdir(parent, { recursive: true });
  const versions = versionsFolder(dir);
  if (linked) {
    await fs.promises.mkdir(versions, { recursive: true });
  }
  let staging: string | undefined = await fs.promises.mkdtemp(linked ? path.join(versions, 'new-') : path.join(parent, `.${path.basename(dir)}.new-`));
  try {
    await unpack(archive, staging);
    if (token.isCancellationRequested) {
      throw new Error('the update was cancelled; the installation is unchanged');
    }
    if (linked) {
      await withInstallLock(dir, async () => {
        try {
          // A name that says which release the folder holds, for a person who looks into the versions folder.
          const version = (await fs.promises.readFile(path.join(staging!, 'VERSION'), 'utf8').catch(() => '')).trim().replace(/[^\w.+-]/g, '_') || 'release';
          const named = path.join(versions, `${version}-${path.basename(staging!).slice('new-'.length)}`);
          await fs.promises.rename(staging!, named);
          staging = named;
          await pointAt(dir, staging, versions);
          staging = undefined;
          await removeOldVersions(named, versions, log);
        } finally {
          if (staging) {
            await fs.promises.rm(staging, { recursive: true, force: true }).catch(() => undefined);
            staging = undefined;
          }
        }
      });
    } else {
      await swapIn(staging, dir, log);
      staging = undefined;
    }
  } finally {
    if (staging) {
      await fs.promises.rm(staging, { recursive: true, force: true }).catch(() => undefined);
    }
  }
}

/** The folder that holds one folder per release when `dir` is a junction: `.<name>.versions` beside `dir`. */
export function versionsFolder(dir: string): string {
  return path.join(path.dirname(dir), `.${path.basename(dir)}.versions`);
}

/** True when `dir` is a junction or a symbolic link (the layout an update on Windows makes), not a real folder. */
export function isLinkedInstall(dir: string): boolean {
  try {
    return fs.lstatSync(dir).isSymbolicLink();
  } catch {
    return false;
  }
}

// tar unpacks both: a .tar.gz everywhere, and a .zip with the bsdtar Windows 10+ ships as tar.exe.
async function unpack(archive: string, folder: string): Promise<void> {
  await exec('tar', ['-xf', archive, '-C', folder]);
  if (process.platform !== 'win32') {
    for (const name of ['codegraph-index', 'codegraph-mcp']) {
      await fs.promises.chmod(path.join(folder, name), 0o755);
    }
  }
  if (process.platform === 'darwin') {
    // A browser download is quarantined on macOS; fetch's is not, but clearing the flag costs nothing (install.sh does too).
    await exec('xattr', ['-dr', 'com.apple.quarantine', folder]).catch(() => undefined);
  }
}

/**
 * Points the junction `dir` at `target`. If `dir` is a junction, a new junction is made beside it and renamed into
 * its place; a running program does not stop that. If `dir` is a real folder (an installation made before the
 * junction layout), it is moved into the versions folder first, and a program that runs from it stops that move.
 * A failure puts `dir` back as it was.
 */
async function pointAt(dir: string, target: string, versions: string): Promise<void> {
  let current: fs.Stats | undefined;
  try {
    current = await fs.promises.lstat(dir);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== 'ENOENT') {
      throw error;
    }
  }
  if (!current) {
    await fs.promises.symlink(target, dir, 'junction');
    return;
  }
  if (current.isSymbolicLink()) {
    await removeStaleLinks(dir);
    const previous = await fs.promises.readlink(dir);
    const next = path.join(path.dirname(dir), `.${path.basename(dir)}.next-${crypto.randomBytes(4).toString('hex')}`);
    await fs.promises.symlink(target, next, 'junction');
    try {
      await fs.promises.unlink(dir);
    } catch (error) {
      await fs.promises.unlink(next).catch(() => undefined);
      throw error;
    }
    try {
      await fs.promises.rename(next, dir);
    } catch (error) {
      await fs.promises.symlink(previous, dir, 'junction').catch(() => undefined);
      await fs.promises.unlink(next).catch(() => undefined);
      throw error;
    }
    return;
  }
  const legacy = path.join(versions, `previous-${crypto.randomBytes(4).toString('hex')}`);
  try {
    await fs.promises.rename(dir, legacy);
  } catch (error) {
    const code = (error as NodeJS.ErrnoException).code ?? String(error);
    throw new Error(
      `could not move ${dir} aside (${code}). A program is still running from it: an MCP server an assistant started ` +
        '(in VS Code: MCP: List Servers → Stop), a daemon started in a terminal, or a terminal whose folder it is. ' +
        `Stop it and update again; nothing was changed. This occurs only once: after this update, ${dir} links to a ` +
        'folder for each release, and an update does not wait for running programs.',
    );
  }
  try {
    await fs.promises.symlink(target, dir, 'junction');
  } catch (error) {
    await fs.promises.rename(legacy, dir);
    throw error;
  }
}

/** Shares the junction publication lock with install.ps1; the helper releases the mutex when stdin closes. */
async function withInstallLock<T>(dir: string, work: () => Promise<T>): Promise<T> {
  if (process.platform !== 'win32') {
    return work();
  }
  const key = path.resolve(dir).replace(/[\\/]+$/, '').toLowerCase();
  const name = `Local\\CodeGraphInstall-${crypto.createHash('sha256').update(key).digest('hex')}`;
  const command =
    `$mutex = [System.Threading.Mutex]::new($false, '${name}'); ` +
    `try { try { $null = $mutex.WaitOne() } catch [System.Threading.AbandonedMutexException] {}; ` +
    `[Console]::Out.WriteLine('LOCKED'); [Console]::Out.Flush(); [Console]::In.ReadLine() | Out-Null } ` +
    `finally { try { $mutex.ReleaseMutex() } catch {}; $mutex.Dispose() }`;
  const child = cp.spawn('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', command], {
    windowsHide: true,
    stdio: ['pipe', 'pipe', 'pipe'],
  });
  let output = '';
  let stderr = '';
  let closed = false;
  let acquired = false;
  let closeResolve!: () => void;
  const close = new Promise<void>((resolve) => {
    closeResolve = resolve;
  });
  child.stdout.setEncoding('utf8');
  child.stderr.setEncoding('utf8');
  child.stderr.on('data', (text: string) => (stderr += text));
  child.stdin.on('error', () => undefined);
  child.on('close', () => {
    closed = true;
    closeResolve();
  });
  try {
    await new Promise<void>((resolve, reject) => {
      child.on('error', reject);
      child.on('close', (code) => {
        if (!acquired) {
          reject(new Error(`could not acquire the install lock: ${stderr || `PowerShell exited ${code}`}`));
        }
      });
      child.stdout.on('data', (text: string) => {
        output += text;
        if (!acquired && output.includes('LOCKED')) {
          acquired = true;
          resolve();
        }
      });
    });
    return await work();
  } finally {
    if (!closed) {
      child.stdin.end();
      await close;
    }
  }
}

/**
 * Deletes each folder in `versions` except `current` (the release the junction now points at) that nothing runs
 * from. On Windows a folder that a running program uses cannot be renamed, so a rename tests each folder first: a
 * delete of a folder in use removes the files that are not open and leaves the program without them. A folder in
 * use stays for the next update. A folder younger than an hour stays too: another update (a second window,
 * install.ps1) can still unpack into it, or can have it ready to link.
 */
async function removeOldVersions(current: string, versions: string, log: (line: string) => void): Promise<void> {
  let names: string[];
  try {
    names = await fs.promises.readdir(versions);
  } catch {
    return;
  }
  for (const name of names) {
    const folder = path.join(versions, name);
    if (samePath(folder, current)) {
      continue;
    }
    const changed = await fs.promises.stat(folder).then((s) => s.mtimeMs).catch(() => 0);
    if (!name.startsWith('.delete-') && Date.now() - changed < recentMs) {
      continue;
    }
    const doomed = path.join(versions, `.delete-${crypto.randomBytes(4).toString('hex')}`);
    try {
      await fs.promises.rename(folder, doomed);
    } catch (error) {
      log(`kept ${folder}: a program still runs from it (${(error as NodeJS.ErrnoException).code ?? String(error)}); the next update deletes it`);
      continue;
    }
    await fs.promises.rm(doomed, { recursive: true, force: true, maxRetries: 3 }).catch((error: unknown) =>
      log(`could not delete ${doomed}: ${error instanceof Error ? error.message : String(error)}`),
    );
  }
}

/** How long an unpacked release folder is safe from the cleanup of another update. A test can set it lower. */
let recentMs = 60 * 60 * 1000;
export function setRecentMs(ms: number): void {
  recentMs = ms;
}

/** Removes `.<name>.next-*` junctions beside `dir` that an update stopped between its two steps left. */
async function removeStaleLinks(dir: string): Promise<void> {
  const prefix = `.${path.basename(dir)}.next-`;
  const names = await fs.promises.readdir(path.dirname(dir)).catch(() => [] as string[]);
  for (const name of names.filter((n) => n.startsWith(prefix))) {
    const link = path.join(path.dirname(dir), name);
    if (isLinkedInstall(link)) {
      await fs.promises.unlink(link).catch(() => undefined); // the link only, never what it points at
    }
  }
}

/** Moves `dir` aside, `staging` into its place, then deletes the old folder; puts `dir` back when the second move fails. */
async function swapIn(staging: string, dir: string, log: (line: string) => void): Promise<void> {
  if (!fs.existsSync(dir)) {
    await fs.promises.rename(staging, dir);
    return;
  }
  const old = path.join(path.dirname(staging), path.basename(staging).replace('.new-', '.old-'));
  try {
    await fs.promises.rename(dir, old);
  } catch (error) {
    const code = (error as NodeJS.ErrnoException).code ?? String(error);
    throw new Error(
      `could not move ${dir} aside (${code}). A program is still running from it: an MCP server an assistant started ` +
        '(in VS Code: MCP: List Servers → Stop), a daemon started in a terminal, or a terminal whose folder it is. ' +
        'Stop it and update again; nothing was changed.',
    );
  }
  try {
    await fs.promises.rename(staging, dir);
  } catch (error) {
    await fs.promises.rename(old, dir);
    throw error;
  }
  await fs.promises.rm(old, { recursive: true, force: true }).catch((error: unknown) =>
    log(`the previous installation could not be deleted from ${old}: ${error instanceof Error ? error.message : String(error)}`),
  );
}

async function fetchOk(url: string): Promise<Response> {
  const response = await fetch(url, { headers: { 'User-Agent': userAgent } });
  if (!response.ok) {
    throw new Error(`${url} answered ${response.status}`);
  }
  return response;
}

/** Streams `url` to `file`, reporting progress, and returns the SHA256 of what it wrote. A write error or a cancel rejects. */
async function download(
  url: string,
  file: string,
  progress: vscode.Progress<{ message?: string; increment?: number }>,
  token: vscode.CancellationToken,
): Promise<string> {
  const abort = new AbortController();
  const cancel = token.onCancellationRequested(() => abort.abort());
  try {
    const response = await fetch(url, { headers: { 'User-Agent': userAgent }, signal: abort.signal });
    if (!response.ok || !response.body) {
      throw new Error(`${url} answered ${response.status}`);
    }
    const total = Number(response.headers.get('content-length')) || 0;
    const hash = crypto.createHash('sha256');
    let received = 0;
    let reported = 0;
    const meter = new Transform({
      transform(chunk: Buffer, _encoding, done) {
        hash.update(chunk);
        received += chunk.length;
        if (total > 0) {
          const percent = (received / total) * 100;
          if (percent - reported >= 1) {
            progress.report({ message: `downloading ${path.basename(file)} (${Math.floor(percent)}%)`, increment: percent - reported });
            reported = percent;
          }
        }
        done(null, chunk);
      },
    });
    // pipeline closes every stream and rejects on the first error from any of them (a full disk, a refused write).
    await pipeline(Readable.fromWeb(response.body as unknown as WebReadableStream<Uint8Array>), meter, fs.createWriteStream(file), { signal: abort.signal });
    return hash.digest('hex');
  } catch (error) {
    if (token.isCancellationRequested) {
      throw new Error('the download was cancelled; the installation is unchanged');
    }
    throw error;
  } finally {
    cancel.dispose();
  }
}

function exec(command: string, args: string[]): Promise<void> {
  return new Promise((resolve, reject) => {
    cp.execFile(command, args, { windowsHide: true }, (error, _stdout, stderr) =>
      error ? reject(new Error(`${command} ${args.join(' ')}: ${stderr || error.message}`)) : resolve(),
    );
  });
}
