// The Graphs view: each graph with its health, daemon, languages, targets, stale files and usage.
import * as path from 'path';
import * as vscode from 'vscode';
import { Daemons } from './daemons';
import { ago, bytes, count, languageName, plural, shortCommit } from './format';
import { Graph, Graphs, Target } from './graphs';
import { daemonStateOf, healthOf } from './status';

type Node =
  | { kind: 'graph'; graph: Graph }
  | { kind: 'daemon'; graph: Graph }
  | { kind: 'languages'; graph: Graph }
  | { kind: 'language'; graph: Graph; name: string }
  | { kind: 'targets'; graph: Graph }
  | { kind: 'target'; graph: Graph; target: Target }
  | { kind: 'stale'; graph: Graph }
  | { kind: 'staleFile'; graph: Graph; file: string; deleted: boolean }
  | { kind: 'usage'; graph: Graph }
  | { kind: 'message'; text: string; icon: string; color?: string; tooltip?: string; command?: vscode.Command };

const green = new vscode.ThemeColor('testing.iconPassed');
const yellow = new vscode.ThemeColor('list.warningForeground');
const red = new vscode.ThemeColor('list.errorForeground');
const blue = new vscode.ThemeColor('charts.blue');
const purple = new vscode.ThemeColor('charts.purple');

export class GraphTree implements vscode.TreeDataProvider<Node>, vscode.Disposable {
  private readonly changed = new vscode.EventEmitter<Node | undefined>();
  readonly onDidChangeTreeData = this.changed.event;
  private readonly subscription: vscode.Disposable;

  constructor(
    private readonly graphs: Graphs,
    private readonly daemons: Daemons,
  ) {
    this.subscription = graphs.onDidChange(() => this.changed.fire(undefined));
  }

  getChildren(node?: Node): Node[] {
    if (!node) {
      return this.graphs.all().map((graph) => ({ kind: 'graph', graph }));
    }
    const { graph } = node as { graph: Graph };
    switch (node.kind) {
      case 'graph': {
        const status = graph.status;
        if (graph.statusError) {
          return [{ kind: 'message', text: 'status failed', icon: 'error', color: 'red', tooltip: graph.statusError }];
        }
        if (!status) {
          return [{ kind: 'targets', graph }];
        }
        if (!status.exists) {
          return [
            {
              kind: 'message',
              text: 'Not indexed yet: set up the repository',
              icon: 'rocket',
              command: { command: 'codegraph.init', title: 'Set Up', arguments: [graph] },
            },
            { kind: 'targets', graph },
          ];
        }
        const children: Node[] = [];
        if (status.error) {
          children.push({ kind: 'message', text: status.error, icon: 'error', color: 'red', tooltip: status.error });
        }
        children.push({ kind: 'daemon', graph });
        if (status.graph && status.graph.changedCount + status.graph.missingCount > 0) {
          children.push({ kind: 'stale', graph });
        }
        if (status.graph?.languages.length) {
          children.push({ kind: 'languages', graph });
        }
        children.push({ kind: 'targets', graph });
        children.push({ kind: 'usage', graph });
        return children;
      }
      case 'languages':
        return (graph.status?.graph?.languages ?? []).map((l) => ({ kind: 'language', graph, name: l.name }));
      case 'targets':
        return graph.targets.map((target) => ({ kind: 'target', graph, target }));
      case 'stale': {
        const g = graph.status?.graph;
        if (!g) {
          return [];
        }
        const nodes: Node[] = [
          ...g.changedFiles.map((file) => ({ kind: 'staleFile' as const, graph, file, deleted: false })),
          ...g.missingFiles.map((file) => ({ kind: 'staleFile' as const, graph, file, deleted: true })),
        ];
        const more = g.changedCount + g.missingCount - nodes.length;
        if (more > 0) {
          nodes.push({ kind: 'message', text: `and ${plural(more, 'more file')}`, icon: 'ellipsis' });
        }
        return nodes;
      }
      default:
        return [];
    }
  }

  getTreeItem(node: Node): vscode.TreeItem {
    switch (node.kind) {
      case 'graph':
        return this.graphItem(node.graph);
      case 'daemon':
        return this.daemonItem(node.graph);
      case 'languages': {
        const languages = node.graph.status?.graph?.languages ?? [];
        const item = new vscode.TreeItem('Languages', vscode.TreeItemCollapsibleState.Collapsed);
        item.description = languages.map((l) => languageName(l.name)).join(' · ');
        item.iconPath = new vscode.ThemeIcon('symbol-namespace', purple);
        return item;
      }
      case 'language': {
        const language = node.graph.status?.graph?.languages.find((l) => l.name === node.name);
        const item = new vscode.TreeItem(languageName(node.name));
        const errors = (language?.compileErrors ?? 0) + (language?.languageVersionErrors ?? 0);
        item.description = language ? `${plural(language.nodes, 'node')} · ${plural(language.files, 'file')}` : '';
        item.iconPath = new vscode.ThemeIcon(errors > 0 ? 'warning' : 'symbol-class', errors > 0 ? yellow : blue);
        const tip = new vscode.MarkdownString();
        tip.appendMarkdown(`**${languageName(node.name)}**\n\n`);
        if (language?.compiler) {
          tip.appendMarkdown(`Compiler: ${language.compiler}${language.newestLanguageVersion ? ` (up to ${language.newestLanguageVersion})` : ''}\n\n`);
        }
        if (language?.languageVersions) {
          tip.appendMarkdown(`Code uses: ${language.languageVersions}\n\n`);
        }
        if (language?.compileErrors) {
          tip.appendMarkdown(`⚠ ${language.compileErrors} compile error(s)${language.firstCompileError ? `, first: \`${language.firstCompileError}\`` : ''}\n\n`);
        }
        if (language?.languageVersionErrors) {
          tip.appendMarkdown(`⚠ ${language.languageVersionErrors} error(s) need a newer language version than the indexer knows; declarations under them are missing.\n\n`);
        }
        for (const note of language?.notes ?? []) {
          tip.appendMarkdown(`Note: ${note}\n\n`);
        }
        item.tooltip = tip;
        return item;
      }
      case 'targets': {
        const item = new vscode.TreeItem('Targets', vscode.TreeItemCollapsibleState.Collapsed);
        item.description = plural(node.graph.targets.length, 'target');
        item.iconPath = new vscode.ThemeIcon('target');
        return item;
      }
      case 'target': {
        const { target, graph } = node;
        const item = new vscode.TreeItem(vscode.Uri.file(target.file));
        item.label = path.basename(target.file);
        const relative = path.relative(graph.root, path.dirname(target.file));
        const daemon = this.daemonTargetFile(graph) === target.file && graph.status?.daemon ? ' · watched' : '';
        item.description = `${languageName(target.kind)}${relative ? ` · ${relative}` : ''}${daemon}`;
        item.contextValue = 'target';
        item.resourceUri = vscode.Uri.file(target.file);
        item.command = { command: 'vscode.open', title: 'Open', arguments: [vscode.Uri.file(target.file)] };
        const replaced = this.graphs.replaces(graph, target);
        if (target.missing) {
          item.iconPath = new vscode.ThemeIcon('warning', yellow);
          item.description += ` · needs ${target.missing.tool}`;
          item.tooltip = new vscode.MarkdownString(
            `\`${target.missing.tool}\` is not on PATH, so this target cannot be indexed.\n\n${target.missing.install}: [${target.missing.url}](${target.missing.url})`,
          );
        } else if (replaced) {
          item.iconPath = new vscode.ThemeIcon('circle-slash');
          item.description += ' · not indexed';
          item.tooltip = new vscode.MarkdownString(
            `${target.file}\n\nNot in this graph: its ${languageName(target.kind)} rows come from \`${path.relative(graph.root, replaced.file) || replaced.file}\`, ` +
              `and an index replaces all of a language's rows, so one ${languageName(target.kind)} target feeds a graph. Index this one to replace them.`,
          );
        } else {
          item.tooltip = target.file;
        }
        return item;
      }
      case 'stale': {
        const g = node.graph.status!.graph!;
        const item = new vscode.TreeItem('Changed since indexing', vscode.TreeItemCollapsibleState.Collapsed);
        const parts = [g.changedCount ? `${count(g.changedCount)} edited` : '', g.missingCount ? `${count(g.missingCount)} deleted` : ''].filter(Boolean);
        item.description = parts.join(' · ');
        item.iconPath = new vscode.ThemeIcon('diff', yellow);
        return item;
      }
      case 'staleFile': {
        const item = new vscode.TreeItem(vscode.Uri.file(node.file));
        item.label = path.basename(node.file);
        item.description = `${path.relative(node.graph.root, path.dirname(node.file)) || '.'}${node.deleted ? ' · deleted' : ''}`;
        item.resourceUri = vscode.Uri.file(node.file);
        if (!node.deleted) {
          item.command = { command: 'vscode.open', title: 'Open', arguments: [vscode.Uri.file(node.file)] };
        }
        return item;
      }
      case 'usage': {
        const usage = node.graph.status?.usage;
        const item = new vscode.TreeItem('Usage');
        item.contextValue = 'usage';
        item.iconPath = new vscode.ThemeIcon('graph-line', green);
        item.description = usage ? `${plural(usage.calls, 'tool call')} this week${usage.errors ? ` · ${count(usage.errors)} failed` : ''}` : 'no tool calls yet';
        item.tooltip = usage?.line ?? 'The MCP server records one line per tool call beside the graph; none yet.';
        item.command = { command: 'codegraph.showUsage', title: 'Show Usage', arguments: [node.graph] };
        return item;
      }
      case 'message': {
        const item = new vscode.TreeItem(node.text);
        item.iconPath = new vscode.ThemeIcon(node.icon, node.color === 'red' ? red : node.color === 'yellow' ? yellow : undefined);
        item.tooltip = node.tooltip;
        item.command = node.command;
        return item;
      }
    }
  }

  private graphItem(graph: Graph): vscode.TreeItem {
    const status = graph.status;
    const health = graph.statusError ? 'error' : healthOf(status);
    const item = new vscode.TreeItem(graph.name, vscode.TreeItemCollapsibleState.Expanded);
    item.contextValue = `graph.${health}`;
    item.id = graph.db;
    const g = status?.graph;
    if (graph.busy) {
      item.iconPath = new vscode.ThemeIcon('sync~spin', blue);
      item.description = `${graph.busy}…`;
    } else if (health === 'fresh') {
      item.iconPath = new vscode.ThemeIcon('pass-filled', green);
      item.description = `${count(g!.nodes, true)} nodes · up to date`;
    } else if (health === 'stale') {
      item.iconPath = new vscode.ThemeIcon('warning', yellow);
      const changed = g!.changedCount + g!.missingCount;
      item.description = `${count(g!.nodes, true)} nodes · ${g!.headMoved && changed === 0 ? 'HEAD moved' : `${count(changed)} changed`}`;
    } else if (health === 'error') {
      item.iconPath = new vscode.ThemeIcon('error', red);
      item.description = 'cannot read the graph';
    } else {
      item.iconPath = new vscode.ThemeIcon('circle-large-outline');
      item.description = status ? 'not indexed' : '';
    }
    const tip = new vscode.MarkdownString(undefined, true);
    tip.appendMarkdown(`**$(type-hierarchy) ${graph.name}**\n\n`);
    tip.appendMarkdown(`Graph: \`${graph.db}\`\n\n`);
    if (g) {
      tip.appendMarkdown(`Built ${ago(g.indexedAt)} (${g.mode ?? '?'}) at \`${shortCommit(g.commit)}\`; HEAD is \`${shortCommit(g.head)}\`\n\n`);
      tip.appendMarkdown(`${count(g.nodes)} nodes · ${count(g.edges)} edges\n\n`);
    }
    if (status?.version) {
      tip.appendMarkdown(`codegraph-index ${status.version}`);
    }
    item.tooltip = tip;
    return item;
  }

  private daemonItem(graph: Graph): vscode.TreeItem {
    const daemon = graph.status?.daemon;
    const state = daemonStateOf(daemon);
    const item = new vscode.TreeItem('Live daemon');
    item.contextValue = daemon ? 'daemon.running' : 'daemon.stopped';
    if (!daemon) {
      item.iconPath = new vscode.ThemeIcon('circle-slash');
      item.description = this.graphs.daemonTarget(graph) ? 'stopped' : 'no target to watch';
      item.tooltip = 'Not running. Start it to keep the graph fresh on every save and answer the live tools (symbol_at, live_references, diagnostics, check_change).';
      return item;
    }
    const icon = state === 'watching' ? new vscode.ThemeIcon('pulse', green) : new vscode.ThemeIcon('loading~spin', blue);
    item.iconPath = icon;
    const managed = this.daemons.isManaged(graph) ? '' : ' · started elsewhere';
    item.description =
      state === 'loading'
        ? `loading the solution…${managed}`
        : `${state} · ${plural(daemon.projects, 'project')} · ${daemon.workingSetBytes ? bytes(daemon.workingSetBytes) : `pid ${daemon.pid}`}${managed}`;
    const tip = new vscode.MarkdownString();
    tip.appendMarkdown(`**Live daemon** — pid ${daemon.pid}\n\n`);
    tip.appendMarkdown(`Watching \`${path.basename(daemon.solution)}\`: ${plural(daemon.projects, 'project')}, ${plural(daemon.documents, 'document')}\n\n`);
    tip.appendMarkdown(`Started ${ago(daemon.startedAt)}; last index ${ago(daemon.lastIndexedAt)}${daemon.lastIndexSeconds !== undefined ? ` in ${daemon.lastIndexSeconds.toFixed(1)} s` : ''}\n\n`);
    if (daemon.workingSetBytes) {
      tip.appendMarkdown(`Memory: ${bytes(daemon.workingSetBytes)}\n\n`);
    }
    if (daemon.idleExitMinutes) {
      tip.appendMarkdown(`Stops after ${daemon.idleExitMinutes} min unused`);
    }
    item.tooltip = tip;
    return item;
  }

  private daemonTargetFile(graph: Graph): string | undefined {
    return this.graphs.daemonTarget(graph)?.file;
  }

  dispose(): void {
    this.subscription.dispose();
    this.changed.dispose();
  }
}
