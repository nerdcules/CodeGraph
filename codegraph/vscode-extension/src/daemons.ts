// Starts and stops the live daemon (`codegraph-index --watch`) of each graph. The extension stops the daemons it
// started when the window closes; a daemon someone else started (a terminal, the MCP server) is left running.
import * as cp from 'child_process';
import * as path from 'path';
import * as vscode from 'vscode';
import { Cli } from './cli';
import { Graph, Graphs, Target } from './graphs';

export class Daemons implements vscode.Disposable {
  /**
   * The daemon process this window started for each graph file, from the moment it is spawned (the status that
   * would show it is only read later). A second Start while one is starting or running does nothing.
   */
  private readonly started = new Map<string, cp.ChildProcess>();
  private readonly timers = new Set<NodeJS.Timeout>();

  constructor(
    private readonly cli: Cli,
    private readonly graphs: Graphs,
    private readonly output: vscode.LogOutputChannel,
  ) {}

  /** Where a daemon this extension starts writes its log: the same file the MCP server's launcher uses. */
  logPath(graph: Graph): string {
    return path.join(path.dirname(graph.db), 'daemon.log');
  }

  isManaged(graph: Graph): boolean {
    return this.started.has(graph.db);
  }

  async start(graph: Graph, chosen?: Target): Promise<void> {
    if (graph.status?.daemon || this.started.has(graph.db)) {
      return;
    }
    // A target just set up is the one to watch; else the graph's (Graphs.daemonTarget).
    const target = chosen ?? this.graphs.daemonTarget(graph);
    if (!target) {
      throw new Error(`no target to watch for ${graph.name}`);
    }
    const minutes = vscode.workspace.getConfiguration('codegraph').get<number>('daemon.idleExitMinutes', 120);
    const child = this.cli.spawnDetached(
      [target.file, '--db', graph.db, '--watch', '--log', this.logPath(graph), '--idle-exit', String(minutes)],
      path.dirname(target.file),
    );
    child.on('error', (error) => this.output.error(`daemon for ${graph.name} did not start: ${error.message}`));
    child.on('exit', (code) => {
      // Only this child's own entry: a later start may have replaced it.
      if (this.started.get(graph.db) === child) {
        this.started.delete(graph.db);
      }
      this.output.info(`daemon for ${graph.name} exited (${code ?? 'signal'})`);
      void this.graphs.refresh(graph);
    });
    child.unref();
    this.started.set(graph.db, child);

    // The pipe answers within a second or two, "loading" until the solution is in; watch it come up.
    for (const delay of [800, 2500, 6000, 12000, 25000, 45000, 90000]) {
      const timer = setTimeout(() => {
        this.timers.delete(timer);
        void this.graphs.refresh(graph);
      }, delay);
      this.timers.add(timer);
    }
  }

  async stop(graph: Graph): Promise<void> {
    const result = await this.cli.run(['stop', '--db', graph.db], graph.root);
    this.started.delete(graph.db);
    if (result.code !== 0) {
      throw new Error(result.stderr.trim() || `codegraph-index stop exited with ${result.code}`);
    }
    await this.graphs.refresh(graph);
  }

  async restart(graph: Graph): Promise<void> {
    if (graph.status?.daemon || this.started.has(graph.db)) {
      await this.stop(graph);
    }
    await this.start(graph);
  }

  /** On activation: a daemon for every graph that already exists, when the setting asks for it. */
  async autoStart(): Promise<void> {
    if (!vscode.workspace.getConfiguration('codegraph').get<boolean>('daemon.autoStart', true) || !this.cli.installed()) {
      return;
    }
    for (const graph of this.graphs.all()) {
      if (graph.status?.exists && graph.status.graph && !graph.status.daemon && this.graphs.daemonTarget(graph)) {
        try {
          await this.start(graph);
        } catch (error) {
          this.output.warn(`auto-start for ${graph.name}: ${error instanceof Error ? error.message : String(error)}`);
        }
      }
    }
  }

  /** Stops every daemon this window started, for deactivation and before an update replaces the binaries. */
  async stopManaged(): Promise<void> {
    const graphs = [...this.started.keys()].map((db) => this.graphs.get(db) ?? ({ db, root: path.dirname(path.dirname(db)), name: db, targets: [] } as Graph));
    await Promise.all(graphs.map((g) => this.stop(g).catch(() => undefined)));
  }

  dispose(): void {
    for (const timer of this.timers) {
      clearTimeout(timer);
    }
  }
}
