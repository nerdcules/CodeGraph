// Downloads a CodeGraph release into the install folder: the archive for this machine, checked against the
// release's SHA256SUMS like install.sh and install.ps1 do, unpacked beside the old folder and swapped in, so a failed
// or refused update leaves the old installation whole.
import * as cp from 'child_process';
import * as crypto from 'crypto';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { Readable, Transform } from 'stream';
import { pipeline } from 'stream/promises';
import type { ReadableStream as WebReadableStream } from 'stream/web';
import * as vscode from 'vscode';
import { assetName, checksumFor, isInstallFolder, repository, runtimeId } from './platform';

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
 * folder such as ~/.local/bin must never be replaced). The release is unpacked into a new folder beside `dir`, then
 * `dir` is moved aside and the new folder moved in; only then is the old one deleted. On Windows a folder with a
 * running program in it cannot be moved, so a CodeGraph program still running from `dir` (an MCP server, a daemon)
 * makes the update fail before anything changed.
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
  const parent = path.dirname(dir);
  let staging: string | undefined;
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

    progress.report({ message: `unpacking beside ${dir}` });
    // Beside the install folder, on the same disk, so the swap below is two renames.
    await fs.promises.mkdir(parent, { recursive: true });
    staging = await fs.promises.mkdtemp(path.join(parent, `.${path.basename(dir)}.new-`));
    // tar unpacks both: a .tar.gz everywhere, and a .zip with the bsdtar Windows 10+ ships as tar.exe.
    await exec('tar', ['-xf', archive, '-C', staging]);
    if (process.platform !== 'win32') {
      for (const name of ['codegraph-index', 'codegraph-mcp']) {
        await fs.promises.chmod(path.join(staging, name), 0o755);
      }
    }
    if (process.platform === 'darwin') {
      // A browser download is quarantined on macOS; fetch's is not, but clearing the flag costs nothing (install.sh does too).
      await exec('xattr', ['-dr', 'com.apple.quarantine', staging]).catch(() => undefined);
    }
    if (token.isCancellationRequested) {
      throw new Error('the update was cancelled; the installation is unchanged');
    }

    progress.report({ message: `replacing ${dir}` });
    await swapIn(staging, dir, log);
    staging = undefined;
    log(`installed ${tag} into ${dir}`);
  } finally {
    await fs.promises.rm(scratch, { recursive: true, force: true });
    if (staging) {
      await fs.promises.rm(staging, { recursive: true, force: true }).catch(() => undefined);
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
