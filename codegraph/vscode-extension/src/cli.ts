// Runs codegraph-index and codegraph-mcp: finds them, logs every run to the output channel, and turns exits into results.
import * as cp from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
import { defaultInstallDir, executable, isInstallFolder } from './platform';
import { parseStatus, StatusReport } from './status';

export interface RunResult {
  code: number;
  stdout: string;
  stderr: string;
}

export class Cli {
  constructor(private readonly output: vscode.LogOutputChannel) {}

  /** The folder the binaries are in: the setting, else the folder of codegraph-index on PATH, else the installers' default. */
  installDir(): string {
    const configured = vscode.workspace.getConfiguration('codegraph').get<string>('installDir')?.trim();
    if (configured) {
      return expandHome(configured);
    }
    const onPath = findOnPath(executable('codegraph-index'));
    // A link on PATH (~/.local/bin/codegraph-index → ~/.codegraph/bin/codegraph-index) means the folder it points into.
    return onPath ? path.dirname(realPath(onPath)) : defaultInstallDir();
  }

  /**
   * The folder an install or update writes to: the setting; else the folder the indexer on PATH runs from, when that
   * is a CodeGraph installation of its own; else the installers' default. `adopt` is set when the default is chosen
   * over an indexer found on PATH (a wrapper script in a shared folder), so the caller points the setting at it.
   */
  updateDir(): { dir: string; adopt: boolean } {
    const configured = vscode.workspace.getConfiguration('codegraph').get<string>('installDir')?.trim();
    if (configured) {
      return { dir: expandHome(configured), adopt: false };
    }
    const onPath = findOnPath(executable('codegraph-index'));
    if (!onPath) {
      return { dir: defaultInstallDir(), adopt: false };
    }
    const dir = path.dirname(realPath(onPath));
    return isInstallFolder(dir) ? { dir, adopt: false } : { dir: defaultInstallDir(), adopt: true };
  }

  indexer(): string {
    return path.join(this.installDir(), executable('codegraph-index'));
  }

  server(): string {
    return path.join(this.installDir(), executable('codegraph-mcp'));
  }

  installed(): boolean {
    return fs.existsSync(this.indexer());
  }

  /** `codegraph-index --version`, or undefined when it does not run. */
  async version(): Promise<string | undefined> {
    if (!this.installed()) {
      return undefined;
    }
    const result = await this.run(['--version'], undefined, { quiet: true });
    return result.code === 0 ? result.stdout.trim() : undefined;
  }

  async status(db: string, cwd: string): Promise<StatusReport> {
    const result = await this.run(['status', '--json', '--db', db], cwd, { quiet: true });
    if (result.code !== 0) {
      throw new Error(result.stderr.trim() || `codegraph-index status exited with ${result.code}`);
    }
    return parseStatus(result.stdout);
  }

  /**
   * Runs the indexer to completion. Its log lines (stderr) stream into the output channel as they come, so a long
   * index shows its progress; `onLine` sees each one as well, for a progress notification.
   */
  run(
    args: string[],
    cwd: string | undefined,
    options: { quiet?: boolean; token?: vscode.CancellationToken; onLine?: (line: string) => void } = {},
  ): Promise<RunResult> {
    const command = this.indexer();
    if (!options.quiet) {
      this.output.info(`$ ${quote(command)} ${args.map(quote).join(' ')}${cwd ? `   (in ${cwd})` : ''}`);
    }
    return new Promise((resolve) => {
      let stdout = '';
      let stderr = '';
      let child: cp.ChildProcess;
      try {
        child = cp.spawn(command, args, { cwd, env: childEnv(), windowsHide: true });
      } catch (error) {
        resolve({ code: -1, stdout, stderr: String(error) });
        return;
      }
      const cancel = options.token?.onCancellationRequested(() => child.kill());
      const lines = (chunk: string, sink: (line: string) => void) => {
        for (const line of chunk.split(/\r?\n/)) {
          // The indexer's progress spinner redraws with \r and escape codes; keep the text only.
          const text = line.replace(/\x1b\[[0-9;?]*[A-Za-z]/g, '').trim();
          if (text) {
            sink(text);
          }
        }
      };
      child.stdout?.setEncoding('utf8').on('data', (chunk: string) => {
        stdout += chunk;
        if (!options.quiet) {
          lines(chunk, (line) => this.output.info(line));
        }
      });
      child.stderr?.setEncoding('utf8').on('data', (chunk: string) => {
        stderr += chunk;
        lines(chunk, (line) => {
          if (!options.quiet) {
            this.output.info(line);
          }
          options.onLine?.(line);
        });
      });
      child.on('error', (error) => {
        cancel?.dispose();
        if (!options.quiet) {
          this.output.error(`could not run ${command}: ${error.message}`);
        }
        resolve({ code: -1, stdout, stderr: stderr + error.message });
      });
      child.on('close', (code) => {
        cancel?.dispose();
        if (!options.quiet) {
          this.output.info(`exit ${code ?? 'signal'}`);
        }
        resolve({ code: code ?? -1, stdout, stderr });
      });
    });
  }

  /** Starts a long-running indexer (the daemon), detached from this process's console, with its log in a file. */
  spawnDetached(args: string[], cwd: string): cp.ChildProcess {
    this.output.info(`$ ${quote(this.indexer())} ${args.map(quote).join(' ')}   (in ${cwd})`);
    return cp.spawn(this.indexer(), args, { cwd, env: childEnv(), windowsHide: true, stdio: 'ignore', detached: process.platform !== 'win32' });
  }
}

/** The extension host's environment, minus what would confuse a child .NET or Node process started from it. */
function childEnv(): NodeJS.ProcessEnv {
  const env = { ...process.env };
  delete env.ELECTRON_RUN_AS_NODE;
  delete env.NODE_OPTIONS;
  return env;
}

export function findOnPath(name: string): string | undefined {
  const dirs = (process.env.PATH ?? '').split(path.delimiter).filter(Boolean);
  for (const dir of dirs) {
    const candidate = path.join(dir, name);
    try {
      if (fs.statSync(candidate).isFile()) {
        return candidate;
      }
    } catch {
      // not here
    }
  }
  return undefined;
}

function realPath(p: string): string {
  try {
    return fs.realpathSync(p);
  } catch {
    return p;
  }
}

function expandHome(p: string): string {
  return p.startsWith('~') ? path.join(os.homedir(), p.slice(1)) : p;
}

function quote(arg: string): string {
  return /^[\w@%+=:,./\\-]+$/.test(arg) ? arg : `"${arg.replace(/"/g, '\\"')}"`;
}
