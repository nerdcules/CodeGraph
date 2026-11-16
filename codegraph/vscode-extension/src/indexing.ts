// One index run at a time per graph (DuckDB has one writer), with progress, and re-index on save for the targets
// no daemon serves.
import * as path from 'path';
import * as vscode from 'vscode';
import { Cli } from './cli';
import { Graph, Graphs, samePath, Target } from './graphs';
import { isInside, sourceExtensions } from './targets';

export class Indexing implements vscode.Disposable {
  private readonly queues = new Map<string, Promise<unknown>>();
  private readonly pending = new Map<string, NodeJS.Timeout>();
  private readonly subscriptions: vscode.Disposable[] = [];

  constructor(
    private readonly cli: Cli,
    private readonly graphs: Graphs,
    private readonly output: vscode.LogOutputChannel,
  ) {
    this.subscriptions.push(vscode.workspace.onDidSaveTextDocument((document) => this.onSave(document)));
  }

  /** Indexes targets of a graph (all of them when none is named), incrementally or in full, behind a progress notification. */
  index(graph: Graph, options: { targets?: Target[]; full?: boolean; quiet?: boolean } = {}): Promise<boolean> {
    const targets = options.targets ?? graph.targets;
    if (targets.length === 0) {
      void vscode.window.showWarningMessage(`CodeGraph: nothing to index in ${graph.name}; no solution, project or build file was found.`);
      return Promise.resolve(false);
    }
    const title = `${options.full ? 'Rebuilding' : 'Indexing'} ${graph.name}`;
    return this.enqueue(graph, async () => {
      graph.busy = options.full ? 'rebuilding' : 'indexing';
      this.graphs.fire();
      try {
        const run = async (progress?: vscode.Progress<{ message?: string }>, token?: vscode.CancellationToken) => {
          let ok = true;
          for (const target of targets) {
            const args = [target.file, '--db', graph.db];
            // --full rebuilds the C# rows from scratch; the other routes always rewrite their own language.
            if (options.full && target.kind === 'csharp') {
              args.push('--full');
            }
            progress?.report({ message: path.basename(target.file) });
            const result = await this.cli.run(args, path.dirname(target.file), {
              token,
              onLine: (line) => progress?.report({ message: `${path.basename(target.file)}: ${line.slice(0, 120)}` }),
            });
            if (token?.isCancellationRequested) {
              return false;
            }
            if (result.code !== 0) {
              ok = false;
              const last = result.stderr.trim().split(/\r?\n/).pop() ?? `exit ${result.code}`;
              void vscode.window
                .showErrorMessage(`CodeGraph: indexing ${path.basename(target.file)} failed: ${last}`, 'Show Output')
                .then((choice) => choice && this.output.show());
            }
          }
          return ok;
        };
        return options.quiet
          ? await vscode.window.withProgress({ location: vscode.ProgressLocation.Window, title }, (progress) => run(progress))
          : await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title, cancellable: true }, run);
      } finally {
        graph.busy = undefined;
        await this.graphs.refresh(graph);
      }
    });
  }

  /** `codegraph-index init`: writes each chosen assistant's MCP config and .gitignore entry, then indexes. */
  init(graph: Graph, target: Target, clients: string[]): Promise<boolean> {
    return this.enqueue(graph, async () => {
      graph.busy = 'setting up';
      this.graphs.fire();
      try {
        return await vscode.window.withProgress(
          { location: vscode.ProgressLocation.Notification, title: `Setting up ${graph.name}`, cancellable: true },
          async (progress, token) => {
            const result = await this.cli.run(['init', target.file, '--client', clients.join(',')], path.dirname(target.file), {
              token,
              onLine: (line) => progress.report({ message: line.slice(0, 140) }),
            });
            if (result.code !== 0 && !token.isCancellationRequested) {
              const last = result.stderr.trim().split(/\r?\n/).pop() ?? `exit ${result.code}`;
              void vscode.window.showErrorMessage(`CodeGraph: set-up failed: ${last}`, 'Show Output').then((choice) => choice && this.output.show());
            }
            return result.code === 0;
          },
        );
      } finally {
        graph.busy = undefined;
        await this.graphs.refresh(graph);
      }
    });
  }

  private enqueue<T>(graph: Graph, work: () => Promise<T>): Promise<T> {
    const previous = this.queues.get(graph.db) ?? Promise.resolve();
    const next = previous.catch(() => undefined).then(work);
    this.queues.set(graph.db, next);
    void next.finally(() => {
      if (this.queues.get(graph.db) === next) {
        this.queues.delete(graph.db);
      }
    });
    return next;
  }

  /**
   * A save re-indexes the target that holds the file, unless the graph's daemon is watching it. One daemon serves
   * a graph, so the Angular front end beside a C# solution is refreshed here. The daemon's own target is never
   * re-indexed on save: without a daemon that is a full MSBuild load per save, and the graph shows as stale instead.
   */
  private onSave(document: vscode.TextDocument): void {
    if (document.uri.scheme !== 'file' || !vscode.workspace.getConfiguration('codegraph').get<boolean>('reindexOnSave', true)) {
      return;
    }
    const file = document.uri.fsPath;
    const graph = this.graphs.forFile(file);
    if (!graph || !graph.status?.exists || !this.cli.installed()) {
      return;
    }
    const daemonTarget = this.graphs.daemonTarget(graph);
    const extension = path.extname(file).toLowerCase();
    const target = graph.targets
      .filter((t) => !(daemonTarget && samePath(t.file, daemonTarget.file)) && t.kind !== daemonTarget?.kind)
      .filter((t) => !t.missing && sourceExtensions[t.kind].includes(extension) && isInside(file, path.dirname(t.file)))
      .sort((a, b) => b.file.length - a.file.length)[0];
    if (!target) {
      return;
    }
    // Saves come in bursts (save all, format on save); one run after the last.
    const key = target.file;
    clearTimeout(this.pending.get(key));
    this.pending.set(
      key,
      setTimeout(() => {
        this.pending.delete(key);
        this.output.info(`saved ${path.basename(file)}: re-indexing ${path.basename(target.file)}, which no daemon watches`);
        void this.index(graph, { targets: [target], quiet: true });
      }, 1500),
    );
  }

  dispose(): void {
    for (const timer of this.pending.values()) {
      clearTimeout(timer);
    }
    for (const subscription of this.subscriptions) {
      subscription.dispose();
    }
  }
}
