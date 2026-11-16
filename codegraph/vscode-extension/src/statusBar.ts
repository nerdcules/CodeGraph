// One status bar item: the health of the graph the active editor belongs to, and whether its daemon is up.
import * as vscode from 'vscode';
import { ago, count, shortCommit } from './format';
import { Graph, Graphs } from './graphs';
import { daemonStateOf, healthOf } from './status';

export class StatusBar implements vscode.Disposable {
  private readonly item = vscode.window.createStatusBarItem('codegraph.status', vscode.StatusBarAlignment.Left, 20);
  private readonly subscriptions: vscode.Disposable[];

  constructor(private readonly graphs: Graphs) {
    this.item.name = 'CodeGraph';
    this.item.command = 'workbench.view.extension.codegraph';
    this.subscriptions = [graphs.onDidChange(() => this.update()), vscode.window.onDidChangeActiveTextEditor(() => this.update())];
    this.update();
  }

  private current(): Graph | undefined {
    const file = vscode.window.activeTextEditor?.document.uri;
    return (file?.scheme === 'file' ? this.graphs.forFile(file.fsPath) : undefined) ?? this.graphs.all()[0];
  }

  update(): void {
    const graph = this.current();
    if (!graph) {
      this.item.hide();
      return;
    }
    const health = graph.statusError ? 'error' : healthOf(graph.status);
    const daemon = daemonStateOf(graph.status?.daemon);
    const g = graph.status?.graph;
    let icon = '$(type-hierarchy)';
    let text = 'CodeGraph';
    this.item.backgroundColor = undefined;
    if (graph.busy || daemon === 'indexing' || daemon === 'loading') {
      icon = '$(sync~spin)';
    }
    if (health === 'stale' && g) {
      text = `CodeGraph $(warning) ${count(g.changedCount + g.missingCount)}`;
    } else if (health === 'error') {
      text = 'CodeGraph $(error)';
      this.item.backgroundColor = new vscode.ThemeColor('statusBarItem.errorBackground');
    } else if (health === 'missing') {
      text = 'CodeGraph $(circle-large-outline)';
    } else if (health === 'fresh') {
      text = 'CodeGraph $(check)';
    }
    if (daemon === 'watching') {
      text += ' $(pulse)';
    }
    this.item.text = `${icon} ${text}`;

    const tip = new vscode.MarkdownString(undefined, true);
    tip.isTrusted = { enabledCommands: ['codegraph.index', 'codegraph.startDaemon', 'codegraph.init'] };
    tip.appendMarkdown(`**CodeGraph · ${graph.name}**\n\n`);
    if (g) {
      tip.appendMarkdown(`${count(g.nodes)} nodes · ${count(g.edges)} edges · built ${ago(g.indexedAt)} at \`${shortCommit(g.commit)}\`\n\n`);
      tip.appendMarkdown(
        health === 'stale'
          ? `$(warning) ${count(g.changedCount)} edited, ${count(g.missingCount)} deleted since indexing${g.headMoved ? '; HEAD moved' : ''} · [Index now](command:codegraph.index)\n\n`
          : '$(check) Up to date with the working tree\n\n',
      );
    } else if (health === 'missing') {
      tip.appendMarkdown('Not indexed yet · [Set up](command:codegraph.init)\n\n');
    } else if (graph.statusError || graph.status?.error) {
      tip.appendMarkdown(`$(error) ${graph.statusError ?? graph.status?.error}\n\n`);
    }
    tip.appendMarkdown(
      daemon === 'stopped'
        ? 'Live daemon stopped · [Start](command:codegraph.startDaemon)'
        : `Live daemon ${daemon} (pid ${graph.status?.daemon?.pid})`,
    );
    this.item.tooltip = tip;
    this.item.show();
  }

  dispose(): void {
    this.item.dispose();
    for (const subscription of this.subscriptions) {
      subscription.dispose();
    }
  }
}
