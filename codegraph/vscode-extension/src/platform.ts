// Where CodeGraph is installed and which release archive fits this machine. No VS Code imports.
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

export const repository = 'nerdcules/CodeGraph';

/** The .NET runtime identifier of the release archive for this OS and CPU, or undefined when none is built for it. */
export function runtimeId(platform: NodeJS.Platform = process.platform, arch: string = process.arch): string | undefined {
  const osName = platform === 'win32' ? 'win' : platform === 'darwin' ? 'osx' : platform === 'linux' ? 'linux' : undefined;
  const cpu = arch === 'x64' ? 'x64' : arch === 'arm64' ? 'arm64' : undefined;
  return osName && cpu ? `${osName}-${cpu}` : undefined;
}

/** The release asset for a runtime id: a zip on Windows, a tar.gz elsewhere (tools/release/package.sh). */
export function assetName(rid: string): string {
  return rid.startsWith('win-') ? `codegraph-${rid}.zip` : `codegraph-${rid}.tar.gz`;
}

/** The folders install.sh and install.ps1 install into by default. */
export function defaultInstallDir(platform: NodeJS.Platform = process.platform, home: string = os.homedir()): string {
  return platform === 'win32' ? 'C:\\tools\\codegraph' : path.join(home, '.codegraph', 'bin');
}

/**
 * True when an update may replace `dir` as a whole: it does not exist, it is empty, or it is a CodeGraph installation
 * (a release archive and a source install both put codegraph-index.dll and codegraph-mcp.dll beside the executables).
 * A folder on PATH such as ~/.local/bin or /usr/local/bin, holding other programs, is none of these.
 */
export function isInstallFolder(dir: string): boolean {
  let entries: string[];
  try {
    entries = fs.readdirSync(dir);
  } catch (error) {
    return (error as NodeJS.ErrnoException).code === 'ENOENT';
  }
  return entries.length === 0 || (entries.includes('codegraph-index.dll') && entries.includes('codegraph-mcp.dll'));
}

export function executable(name: 'codegraph-index' | 'codegraph-mcp', platform: NodeJS.Platform = process.platform): string {
  return platform === 'win32' ? `${name}.exe` : name;
}

/** The SHA256 a SHA256SUMS file lists for one file name, lower case; undefined when it lists none. */
export function checksumFor(sums: string, file: string): string | undefined {
  for (const line of sums.split(/\r?\n/)) {
    const match = /^([0-9a-fA-F]{64})\s+\*?(.+)$/.exec(line.trim());
    if (match && match[2].trim() === file) {
      return match[1].toLowerCase();
    }
  }
  return undefined;
}

/**
 * Compares two dotted versions ("1.10.0" > "1.9.2"). A leading "v" and "+build" metadata are ignored; a pre-release
 * ("1.3.0-beta.1") comes before its release ("1.3.0"), and two pre-releases of one version compare by their suffix.
 */
export function compareVersions(a: string, b: string): number {
  const split = (v: string) => {
    const [core, pre] = v.replace(/^v/, '').split('+')[0].split(/-(.*)/s);
    return { parts: core.split('.').map((p) => Number.parseInt(p, 10) || 0), pre: pre ?? '' };
  };
  const [x, y] = [split(a), split(b)];
  for (let i = 0; i < Math.max(x.parts.length, y.parts.length); i++) {
    const d = (x.parts[i] ?? 0) - (y.parts[i] ?? 0);
    if (d !== 0) {
      return Math.sign(d);
    }
  }
  if (x.pre === y.pre) {
    return 0;
  }
  if (!x.pre || !y.pre) {
    return x.pre ? -1 : 1;
  }
  return Math.sign(x.pre.localeCompare(y.pre, 'en', { numeric: true }));
}

/**
 * The release to install: the newest published one, unless this extension is newer than it. A pre-release of the
 * extension is not "latest" on GitHub, and an older CodeGraph lacks the commands the extension calls, so the
 * extension then installs the release it was built with.
 */
export function releaseToInstall(extensionVersion: string, latest: string): string {
  const own = `v${extensionVersion.replace(/^v/, '')}`;
  return compareVersions(own, latest) > 0 ? own : latest;
}
