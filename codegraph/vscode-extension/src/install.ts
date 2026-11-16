// Downloads a CodeGraph release into the install folder, the way install.sh and install.ps1 do: the archive for this
// machine, checked against the release's SHA256SUMS, unpacked over an emptied folder.
import * as cp from 'child_process';
import * as crypto from 'crypto';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
import { assetName, checksumFor, repository, runtimeId } from './platform';

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
 * Installs release `tag` into `dir`. The folder is emptied first, as the installers do, so stop every process
 * running from it before calling this (on Windows a running .exe cannot be replaced).
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

    progress.report({ message: `unpacking into ${dir}` });
    await fs.promises.mkdir(dir, { recursive: true });
    for (const entry of await fs.promises.readdir(dir)) {
      await fs.promises.rm(path.join(dir, entry), { recursive: true, force: true });
    }
    // tar unpacks both: a .tar.gz everywhere, and a .zip with the bsdtar Windows 10+ ships as tar.exe.
    await exec('tar', ['-xf', archive, '-C', dir]);
    if (process.platform !== 'win32') {
      for (const name of ['codegraph-index', 'codegraph-mcp']) {
        await fs.promises.chmod(path.join(dir, name), 0o755);
      }
    }
    if (process.platform === 'darwin') {
      // A browser download is quarantined on macOS; fetch's is not, but clearing the flag costs nothing (install.sh does too).
      await exec('xattr', ['-dr', 'com.apple.quarantine', dir]).catch(() => undefined);
    }
    log(`installed ${tag} into ${dir}`);
  } finally {
    await fs.promises.rm(scratch, { recursive: true, force: true });
  }
}

async function fetchOk(url: string): Promise<Response> {
  const response = await fetch(url, { headers: { 'User-Agent': userAgent } });
  if (!response.ok) {
    throw new Error(`${url} answered ${response.status}`);
  }
  return response;
}

/** Streams `url` to `file`, reporting progress, and returns the SHA256 of what it wrote. */
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
    const out = fs.createWriteStream(file);
    let received = 0;
    let reported = 0;
    const reader = response.body.getReader();
    for (;;) {
      const { done, value } = await reader.read();
      if (done) {
        break;
      }
      hash.update(value);
      if (!out.write(value)) {
        await new Promise<void>((resolve) => out.once('drain', () => resolve()));
      }
      received += value.length;
      if (total > 0) {
        const percent = (received / total) * 100;
        if (percent - reported >= 1) {
          progress.report({ message: `downloading ${path.basename(file)} (${Math.floor(percent)}%)`, increment: percent - reported });
          reported = percent;
        }
      }
    }
    await new Promise<void>((resolve, reject) => out.end((error?: Error | null) => (error ? reject(error) : resolve())));
    return hash.digest('hex');
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
