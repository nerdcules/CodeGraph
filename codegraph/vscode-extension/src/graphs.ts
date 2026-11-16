// The graphs in this workspace: which targets feed each one, and the last status the indexer reported for it.
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { Cli, findOnPath } from './cli';
import { StatusReport } from './status';
import { chooseTargets, excludeGlob, isInside, Requirement, requirements, TargetKind, targetGlob } from './targets';

export interface Target {
  file: string;
  kind: TargetKind;
  /** Set when the tool this kind needs is not on PATH. */
  missing?: Requirement;
}

export interface Graph {
  /** The graph file; also the key, since one graph has one daemon (its pipe is named from this path). */
  db: string;
  /** The git worktree root, or the target's folder outside a repository. */
  root: string;
  name: string;
  targets: Target[];
  status?: StatusReport;
  /** Why the last status call failed, when it did. */
  statusError?: string;
  /** What the extension is doing to this graph right now, for a spinner. */
  busy?: string;
}

/** The nearest folder at or above `start` holding a `.git` entry, like Worktree.RootOf. */
export function rootOf(start: string): string | undefined {
  for (let dir = path.resolve(start); ; dir = path.dirname(dir)) {
    if (fs.existsSync(path.join(dir, '.git'))) {
      return dir;
    }
    if (path.dirname(dir) === dir) {
      return undefined;
    }
  }
}

/** Where the indexer writes a target's graph by default, like Worktree.DefaultDbPath. */
export function defaultDb(targetFile: string): string {
  const dir = path.dirname(targetFile);
  return path.join(rootOf(dir) ?? dir, '.codegraph', 'graph.db');
}

export class Graphs implements vscode.Disposable {
  private readonly changed = new vscode.EventEmitter<void>();
  readonly onDidChange = this.changed.event;
  private graphs: Graph[] = [];
  private discovering: Promise<void> | undefined;

  constructor(private readonly cli: Cli) {}

  all(): readonly Graph[] {
    return this.graphs;
  }

  get(db: string): Graph | undefined {
    return this.graphs.find((g) => g.db === db);
  }

  /** The graph a file belongs to: the one whose root holds it, the deepest when roots nest. */
  forFile(file: string): Graph | undefined {
    return this.graphs
      .filter((g) => isInside(file, g.root))
      .sort((a, b) => b.root.length - a.root.length)[0];
  }

  fire(): void {
    this.changed.fire();
  }

  /** Finds targets and graphs in every workspace folder, then reads each graph's status. */
  async discover(): Promise<void> {
    this.discovering ??= this.discoverNow().finally(() => (this.discovering = undefined));
    return this.discovering;
  }

  private async discoverNow(): Promise<void> {
    const found = new Map<string, Graph>();
    const add = (db: string): Graph => {
      let graph = found.get(db);
      if (!graph) {
        const previous = this.get(db);
        const root = path.dirname(path.dirname(db));
        graph = { db, root, name: path.basename(root), targets: [], status: previous?.status, busy: previous?.busy };
        found.set(db, graph);
      }
      return graph;
    };

    for (const folder of vscode.workspace.workspaceFolders ?? []) {
      if (folder.uri.scheme !== 'file') {
        continue;
      }
      // An existing graph for the folder shows even when no target is found (a layout the search misses).
      const existing = path.join(rootOf(folder.uri.fsPath) ?? folder.uri.fsPath, '.codegraph', 'graph.db');
      if (fs.existsSync(existing)) {
        add(existing);
      }
      const uris = await vscode.workspace.findFiles(new vscode.RelativePattern(folder, targetGlob), excludeGlob, 500);
      for (const target of chooseTargets(uris.map((u) => u.fsPath))) {
        add(defaultDb(target.file)).targets.push({ ...target, missing: missingTool(target.kind) });
      }
    }

    this.graphs = [...found.values()].sort((a, b) => a.name.localeCompare(b.name));
    this.changed.fire();
    await this.refreshAll();
  }

  /** Re-reads every graph's status. */
  async refreshAll(): Promise<void> {
    await Promise.all(this.graphs.map((g) => this.refresh(g, false)));
    this.changed.fire();
  }

  async refresh(graph: Graph, fire = true): Promise<void> {
    if (!this.cli.installed()) {
      graph.status = undefined;
      graph.statusError = undefined;
    } else {
      try {
        graph.status = await this.cli.status(graph.db, graph.root);
        graph.statusError = undefined;
      } catch (error) {
        graph.statusError = error instanceof Error ? error.message : String(error);
      }
    }
    if (fire) {
      this.changed.fire();
    }
  }

  /**
   * The target the live daemon is started on: the one the graph was built from when it is still here, else a C#
   * solution, else the first target. One daemon serves each graph.
   */
  daemonTarget(graph: Graph): Target | undefined {
    const built = graph.status?.graph?.solution;
    return (
      graph.targets.find((t) => built && samePath(t.file, built)) ??
      graph.targets.find((t) => t.kind === 'csharp' && /\.slnx?$/i.test(t.file)) ??
      graph.targets.find((t) => t.kind === 'csharp') ??
      graph.targets[0] ??
      (built && fs.existsSync(built) ? { file: built, kind: 'csharp' as TargetKind } : undefined)
    );
  }

  dispose(): void {
    this.changed.dispose();
  }
}

/** The requirement for a kind when its tool is not on PATH. Checked once per discovery, by file lookup only. */
function missingTool(kind: TargetKind): Requirement | undefined {
  const requirement = requirements[kind];
  const names = process.platform === 'win32' ? [`${requirement.tool}.exe`, `${requirement.tool}.cmd`, `${requirement.tool}.bat`] : [requirement.tool];
  return names.some((n) => findOnPath(n)) ? undefined : requirement;
}

export function samePath(a: string, b: string): boolean {
  const norm = (p: string) => path.resolve(p).replace(/\\/g, '/');
  return process.platform === 'linux' ? norm(a) === norm(b) : norm(a).toLowerCase() === norm(b).toLowerCase();
}
