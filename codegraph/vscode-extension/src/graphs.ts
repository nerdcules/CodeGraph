// The graphs in this workspace: which targets feed each one, and the last status the indexer reported for it.
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { Cli, findOnPath } from './cli';
import { samePath } from './platform';
import { StatusReport } from './status';
import { chooseTargets, classify, excludeGlob, indexedTargets, isInside, kindLanguages, Requirement, requirements, TargetKind, targetGlob } from './targets';

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

/**
 * The graph a chosen target file goes into when the search did not find it (Choose What to Index…): the graph the
 * indexer writes by default, with that one target. Undefined when the file is not a marker the indexer accepts.
 */
export function graphFor(file: string): Graph | undefined {
  const kind = classify(file);
  if (!kind) {
    return undefined;
  }
  const db = defaultDb(file);
  const root = path.dirname(path.dirname(db));
  return { db, root, name: path.basename(root), targets: [{ file, kind, missing: missingTool(kind) }] };
}

export class Graphs implements vscode.Disposable {
  private readonly changed = new vscode.EventEmitter<void>();
  readonly onDidChange = this.changed.event;
  private graphs: Graph[] = [];
  private discovering: Promise<void> | undefined;
  /** Targets chosen with Choose What to Index…, shown with the ones the search finds until the window closes. */
  private readonly chosen = new Map<string, Target>();

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

  /** Keeps a chosen target in every later discovery, so its graph shows even where the search would not find it. */
  remember(target: Target): void {
    this.chosen.set(path.resolve(target.file), target);
  }

  /** Finds targets and graphs in every workspace folder, then reads each graph's status. */
  async discover(): Promise<void> {
    this.discovering ??= this.discoverNow().finally(() => (this.discovering = undefined));
    return this.discovering;
  }

  private async discoverNow(): Promise<void> {
    // Targets per graph file, collected first; the Graph objects are filled in at the end.
    const found = new Map<string, Target[]>();
    const add = (db: string): Target[] => {
      let targets = found.get(db);
      if (!targets) {
        targets = [];
        found.set(db, targets);
      }
      return targets;
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
        add(defaultDb(target.file)).push({ ...target, missing: missingTool(target.kind) });
      }
    }
    for (const target of [...this.chosen.values()].filter((t) => fs.existsSync(t.file))) {
      const targets = add(defaultDb(target.file));
      if (!targets.some((t) => samePath(t.file, target.file))) {
        targets.push(target);
      }
    }

    // A graph found again keeps its object: an index run in progress holds it, and clears its busy state on it.
    this.graphs = [...found]
      .map(([db, targets]) => {
        const graph = this.get(db) ?? { db, root: path.dirname(path.dirname(db)), name: path.basename(path.dirname(path.dirname(db))), targets };
        graph.targets = targets;
        return graph;
      })
      .sort((a, b) => a.name.localeCompare(b.name));
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

  /**
   * The targets that feed this graph, one per kind (see indexedTargets): an import replaces all of its language's
   * rows, so a second TypeScript workspace or C# solution in the same repository is shown but not indexed with it.
   */
  indexed(graph: Graph): Target[] {
    return indexedTargets(graph.targets, (kind) => this.held(graph, kind), this.daemonTarget(graph));
  }

  /** The target of the same kind whose rows indexing `target` would replace, when it is not `target` itself. */
  replaces(graph: Graph, target: Target): Target | undefined {
    const indexed = this.indexed(graph).find((t) => t.kind === target.kind);
    return indexed && !samePath(indexed.file, target.file) ? indexed : undefined;
  }

  /** The target a kind's rows in the graph were built from, as the graph records it. */
  private held(graph: Graph, kind: TargetKind): string | undefined {
    const report = graph.status?.graph;
    const fromLanguage = report?.languages.find((l) => kindLanguages[kind].includes(l.name) && l.target)?.target;
    // C# records no per-language target; the graph's solution is C#'s when it names a C# file.
    return fromLanguage ?? (report?.solution && classify(report.solution) === kind ? report.solution : undefined);
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

export { samePath };
