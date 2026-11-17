// CodeGraph for VS Code: installs the indexer, sets repositories up, runs and watches their daemons, and shows each
// graph's state in a sidebar, a status bar item and an overview.
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { Cli } from './cli';
import { Daemons } from './daemons';
import { Graph, Graphs } from './graphs';
import { Indexing } from './indexing';
import { installRelease, latestTag } from './install';
import { McpOffer, registerMcp } from './mcp';
import { OverviewView } from './overview';
import { compareVersions, isInstallFolder } from './platform';
import { StatusBar } from './statusBar';
import { targetGlob } from './targets';
import { GraphTree } from './tree';

const clientChoices: { id: string; label: string; detail: string }[] = [
  { id: 'claude', label: 'Claude Code', detail: '.mcp.json (also read by the GitHub Copilot CLI)' },
  { id: 'vscode', label: 'GitHub Copilot in VS Code', detail: '.vscode/mcp.json' },
  { id: 'copilot', label: 'Every GitHub Copilot', detail: 'the CLI, VS Code, Visual Studio and JetBrains' },
  { id: 'cursor', label: 'Cursor', detail: '.cursor/mcp.json' },
  { id: 'codex', label: 'Codex', detail: 'its global config, through codex mcp add' },
  { id: 'gemini', label: 'Gemini CLI', detail: '.gemini/settings.json' },
  { id: 'visualstudio', label: 'Visual Studio', detail: '.vs/mcp.json beside the solution' },
  { id: 'jetbrains', label: 'JetBrains IDEs', detail: '.github/mcp.json' },
  { id: 'roo', label: 'Roo Code', detail: '.roo/mcp.json' },
  { id: 'kilo', label: 'Kilo Code', detail: '.kilo/kilo.json' },
  { id: 'continue', label: 'Continue', detail: '.continue/mcpServers/codegraph.json' },
];

let daemons: Daemons | undefined;

export async function activate(context: vscode.ExtensionContext): Promise<{ graphs: Graphs; cli: Cli; mcp: McpOffer }> {
  const output = vscode.window.createOutputChannel('CodeGraph', { log: true });
  const cli = new Cli(output);
  const graphs = new Graphs(cli);
  daemons = new Daemons(cli, graphs, output);
  const indexing = new Indexing(cli, graphs, output);
  const info = { installed: false, version: undefined as string | undefined, latest: context.globalState.get<string>('latestTag') };
  const tree = new GraphTree(graphs, daemons);
  const overview = new OverviewView(context.extensionUri, graphs, daemons, () => info);
  const usageDocs = new UsageDocuments(cli, graphs);

  context.subscriptions.push(
    output,
    graphs,
    daemons,
    indexing,
    tree,
    overview,
    new StatusBar(graphs),
    vscode.window.registerTreeDataProvider('codegraph.graphs', tree),
    vscode.window.registerWebviewViewProvider('codegraph.overview', overview),
    vscode.workspace.registerTextDocumentContentProvider(UsageDocuments.scheme, usageDocs),
  );
  const mcp = registerMcp(context, cli, graphs, () => info.version);

  const refreshInstall = async () => {
    info.installed = cli.installed();
    info.version = await cli.version();
    await vscode.commands.executeCommand('setContext', 'codegraph.installed', info.installed);
  };
  context.subscriptions.push(
    graphs.onDidChange(() => void vscode.commands.executeCommand('setContext', 'codegraph.hasGraphs', graphs.all().length > 0)),
  );

  /** The graph a command acts on: the one passed (from the tree or the overview), else the active file's, else ask. */
  const pick = async (arg?: unknown): Promise<Graph | undefined> => {
    const passed = (arg as { graph?: Graph })?.graph ?? (arg as Graph);
    if (passed && typeof passed.db === 'string') {
      return graphs.get(passed.db) ?? passed;
    }
    const all = graphs.all();
    const active = vscode.window.activeTextEditor?.document.uri;
    const fromEditor = active?.scheme === 'file' ? graphs.forFile(active.fsPath) : undefined;
    if (fromEditor) {
      return fromEditor;
    }
    if (all.length <= 1) {
      if (all.length === 0) {
        void vscode.window.showInformationMessage('CodeGraph: no C#, TypeScript, Java, Python, Go or Rust project was found in this workspace.');
      }
      return all[0];
    }
    const choice = await vscode.window.showQuickPick(
      all.map((g) => ({ label: g.name, description: g.root, graph: g })),
      { placeHolder: 'Which graph?' },
    );
    return choice?.graph;
  };

  const needsInstall = async (): Promise<boolean> => {
    if (cli.installed()) {
      return false;
    }
    const choice = await vscode.window.showWarningMessage('CodeGraph is not installed.', 'Install');
    if (choice === 'Install') {
      await vscode.commands.executeCommand('codegraph.install');
    }
    return !cli.installed();
  };

  const guard = (name: string, body: (arg?: unknown) => Promise<unknown>) =>
    vscode.commands.registerCommand(name, async (arg?: unknown) => {
      try {
        await body(arg);
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        output.error(`${name}: ${message}`);
        void vscode.window.showErrorMessage(`CodeGraph: ${message}`, 'Show Output').then((c) => c && output.show());
      }
    });

  context.subscriptions.push(
    guard('codegraph.refresh', async () => {
      await refreshInstall();
      await graphs.discover();
    }),

    guard('codegraph.showOutput', async () => output.show()),

    guard('codegraph.init', async (arg) => {
      if (await needsInstall()) {
        return;
      }
      const graph = await pick(arg);
      if (!graph) {
        return;
      }
      const target = await pickTarget(graph, graphs);
      if (!target) {
        return;
      }
      const preselected = new Set(vscode.workspace.getConfiguration('codegraph').get<string[]>('clients', ['claude']));
      const chosen = await vscode.window.showQuickPick(
        clientChoices.map((c) => ({ label: c.label, description: c.id, detail: c.detail, picked: preselected.has(c.id), id: c.id })),
        {
          canPickMany: true,
          title: `Set up ${graph.name}`,
          placeHolder: 'Which assistants should get the codegraph server? (VS Code chat clients get it from this extension already)',
        },
      );
      if (!chosen) {
        return;
      }
      const clients = chosen.length ? chosen.map((c) => c.id) : ['claude'];
      if (await indexing.init(graph, target, clients)) {
        const action = graph.status?.daemon ? undefined : await vscode.window.showInformationMessage(
          `CodeGraph: ${graph.name} is indexed and set up for ${chosen.map((c) => c.label).join(', ') || 'Claude Code'}.`,
          'Start Live Daemon',
        );
        if (action) {
          await daemons!.start(graph);
        }
      }
    }),

    guard('codegraph.index', async (arg) => {
      if (await needsInstall()) {
        return;
      }
      const target = (arg as { target?: Graph['targets'][number] })?.target;
      const graph = await pick(arg);
      if (graph) {
        await indexing.index(graph, target ? { targets: [target] } : {});
      }
    }),

    guard('codegraph.rebuild', async (arg) => {
      if (await needsInstall()) {
        return;
      }
      const graph = await pick(arg);
      if (graph) {
        await indexing.index(graph, { full: true });
      }
    }),

    guard('codegraph.startDaemon', async (arg) => {
      if (await needsInstall()) {
        return;
      }
      const graph = await pick(arg);
      if (graph) {
        await daemons!.start(graph);
      }
    }),

    guard('codegraph.stopDaemon', async (arg) => {
      const graph = await pick(arg);
      if (graph) {
        await daemons!.stop(graph);
      }
    }),

    guard('codegraph.restartDaemon', async (arg) => {
      const graph = await pick(arg);
      if (graph) {
        await daemons!.restart(graph);
      }
    }),

    guard('codegraph.showDaemonLog', async (arg) => {
      const graph = await pick(arg);
      if (!graph) {
        return;
      }
      const log = daemons!.logPath(graph);
      if (!fs.existsSync(log)) {
        void vscode.window.showInformationMessage(`CodeGraph: no daemon log yet (${log}). A daemon started from a terminal logs there instead.`);
        return;
      }
      const document = await vscode.workspace.openTextDocument(vscode.Uri.file(log));
      const editor = await vscode.window.showTextDocument(document, { preview: true });
      const end = document.lineAt(document.lineCount - 1).range.end;
      editor.revealRange(new vscode.Range(end, end));
    }),

    guard('codegraph.showUsage', async (arg) => {
      const graph = await pick(arg);
      if (graph) {
        const uri = UsageDocuments.uriFor(graph);
        usageDocs.update(uri);
        await vscode.window.showTextDocument(await vscode.workspace.openTextDocument(uri), { preview: true });
      }
    }),

    guard('codegraph.install', async () => {
      const tag = await vscode.window.withProgress({ location: vscode.ProgressLocation.Window, title: 'CodeGraph: checking GitHub' }, () => latestTag());
      info.latest = tag;
      await context.globalState.update('latestTag', tag);
      const { dir, adopt } = cli.updateDir();
      const current = await cli.version();
      if (current && compareVersions(tag, current) <= 0) {
        void vscode.window.showInformationMessage(`CodeGraph ${current} is the latest release (${cli.installDir()}).`);
        return;
      }
      if (!isInstallFolder(dir)) {
        throw new Error(`${dir} holds files that are not a CodeGraph installation, so it is not replaced; set codegraph.installDir to a folder of its own`);
      }
      const verb = current ? `Update ${current} → ${tag.replace(/^v/, '')}` : `Install ${tag}`;
      const others = graphs.all().filter((g) => g.status?.daemon && !daemons!.isManaged(g));
      const warning = process.platform === 'win32'
        ? ' Windows cannot replace a running program: stop every CodeGraph MCP server and daemon started outside this window first' +
          `${others.length ? ` (${others.length} daemon(s) are running)` : ''}; if one is still running, the update stops before changing anything.`
        : others.length
          ? ` ${others.length} daemon(s) started outside this window keep running the old version until they are restarted.`
          : '';
      const where = adopt ? ` (codegraph-index on PATH, ${cli.indexer()}, is not in a folder of its own; codegraph.installDir will point at the new folder)` : '';
      const confirm = await vscode.window.showInformationMessage(
        `${verb} into ${dir}${where}? The release is unpacked beside it and swapped in, so the old installation stays whole if anything fails.${warning}`,
        { modal: true },
        current ? 'Update' : 'Install',
      );
      if (!confirm) {
        return;
      }
      const restart = graphs.all().filter((g) => daemons!.isManaged(g));
      await daemons!.stopManaged();
      try {
        await vscode.window.withProgress(
          { location: vscode.ProgressLocation.Notification, title: `CodeGraph ${tag}`, cancellable: true },
          (progress, token) => installRelease(tag, dir, progress, token, (line) => output.info(line)),
        );
      } catch (error) {
        // Nothing was replaced, so the daemons this window ran can run the old version again.
        for (const graph of restart) {
          await daemons!.start(graph).catch(() => undefined);
        }
        throw error;
      }
      if (adopt) {
        await vscode.workspace.getConfiguration('codegraph').update('installDir', dir, vscode.ConfigurationTarget.Global);
      }
      await refreshInstall();
      await graphs.discover();
      for (const graph of restart) {
        await daemons!.start(graph).catch(() => undefined);
      }
      void vscode.window.showInformationMessage(`CodeGraph ${info.version ?? tag} is installed in ${dir}.`);
    }),
  );

  // Status: on start, when a graph file changes, and every 15 s while the window has focus (the daemon's state).
  const watcher = vscode.workspace.createFileSystemWatcher('**/.codegraph/graph.db');
  const onGraphFile = debounce((uri: vscode.Uri) => {
    const graph = graphs.all().find((g) => path.resolve(g.db) === path.resolve(uri.fsPath));
    if (graph) {
      void graphs.refresh(graph);
    } else {
      void graphs.discover();
    }
  }, 750);
  const onTargets = debounce(() => void graphs.discover(), 2000);
  // Every marker discovery accepts, so a project added or removed after activation appears or goes without a Refresh.
  const targetWatcher = vscode.workspace.createFileSystemWatcher(targetGlob, false, true, false);
  const poll = setInterval(() => {
    if (vscode.window.state.focused && graphs.all().length > 0) {
      void graphs.refreshAll();
    }
  }, 15_000);
  context.subscriptions.push(
    watcher,
    targetWatcher,
    watcher.onDidChange(onGraphFile),
    watcher.onDidCreate(onGraphFile),
    watcher.onDidDelete(onGraphFile),
    targetWatcher.onDidCreate(onTargets),
    targetWatcher.onDidDelete(onTargets),
    vscode.workspace.onDidChangeWorkspaceFolders(() => void graphs.discover()),
    vscode.workspace.onDidChangeConfiguration(async (e) => {
      if (e.affectsConfiguration('codegraph.installDir')) {
        await refreshInstall();
        await graphs.discover();
      }
    }),
    { dispose: () => clearInterval(poll) },
  );

  await refreshInstall();
  await vscode.commands.executeCommand('setContext', 'codegraph.ready', true);
  await graphs.discover();
  void daemons.autoStart();
  void checkForUpdate(context, info, () => overview.post());

  return { graphs, cli, mcp };
}

export async function deactivate(): Promise<void> {
  // Stop the daemons this window started; deactivation has a few seconds, and `stop` answers well within that.
  await daemons?.stopManaged();
}

/** Which target `init` runs on: the daemon's target when the graph has one kind of target, else the user's pick. */
async function pickTarget(graph: Graph, graphs: Graphs): Promise<Graph['targets'][number] | undefined> {
  const preferred = graphs.daemonTarget(graph);
  if (graph.targets.length <= 1 || (preferred && graph.targets.every((t) => t.kind === preferred.kind || t.kind === 'typescript'))) {
    // A C# solution's init also indexes the TypeScript front ends under the repository.
    return preferred;
  }
  const choice = await vscode.window.showQuickPick(
    graph.targets.map((t) => ({ label: path.basename(t.file), description: path.relative(graph.root, path.dirname(t.file)) || '.', target: t })),
    { placeHolder: 'Set up from which target? The others can be indexed into the same graph afterwards.' },
  );
  return choice?.target;
}

async function checkForUpdate(context: vscode.ExtensionContext, info: { installed: boolean; version?: string; latest?: string }, changed: () => void): Promise<void> {
  if (!vscode.workspace.getConfiguration('codegraph').get<boolean>('checkForUpdates', true)) {
    return;
  }
  const last = context.globalState.get<number>('lastUpdateCheck', 0);
  if (Date.now() - last < 24 * 3600 * 1000 && info.latest) {
    changed();
  } else {
    try {
      info.latest = await latestTag();
      await context.globalState.update('latestTag', info.latest);
      await context.globalState.update('lastUpdateCheck', Date.now());
      changed();
    } catch {
      return; // offline or rate-limited; the next day tries again
    }
  }
  if (!info.installed) {
    const choice = await vscode.window.showInformationMessage(
      `CodeGraph gives your AI coding assistant the compiler's answers. Install ${info.latest ?? 'the latest release'}?`,
      'Install',
    );
    if (choice) {
      await vscode.commands.executeCommand('codegraph.install');
    }
  } else if (info.version && info.latest && compareVersions(info.latest, info.version) > 0 && context.globalState.get('dismissedTag') !== info.latest) {
    const choice = await vscode.window.showInformationMessage(`CodeGraph ${info.latest.replace(/^v/, '')} is available (you have ${info.version}).`, 'Update', 'Not now');
    if (choice === 'Update') {
      await vscode.commands.executeCommand('codegraph.install');
    } else if (choice === 'Not now') {
      await context.globalState.update('dismissedTag', info.latest);
    }
  }
}

/** `codegraph-index usage` for a graph, as a read-only document. */
class UsageDocuments implements vscode.TextDocumentContentProvider {
  static readonly scheme = 'codegraph-usage';
  private readonly changed = new vscode.EventEmitter<vscode.Uri>();
  readonly onDidChange = this.changed.event;

  constructor(
    private readonly cli: Cli,
    private readonly graphs: Graphs,
  ) {}

  static uriFor(graph: Graph): vscode.Uri {
    return vscode.Uri.from({ scheme: UsageDocuments.scheme, path: `/${graph.name} usage.txt`, query: graph.db });
  }

  update(uri: vscode.Uri): void {
    this.changed.fire(uri);
  }

  async provideTextDocumentContent(uri: vscode.Uri): Promise<string> {
    const graph = this.graphs.get(uri.query);
    if (!graph) {
      return 'That graph is no longer in this workspace.';
    }
    const [week, month] = await Promise.all([
      this.cli.run(['usage', '--db', graph.db], graph.root, { quiet: true }),
      this.cli.run(['usage', '--db', graph.db, '--days', '30'], graph.root, { quiet: true }),
    ]);
    return [`CodeGraph usage · ${graph.name}`, `graph: ${graph.db}`, '', week.stdout.trim() || week.stderr.trim(), '', '— last 30 days —', '', month.stdout.trim() || month.stderr.trim(), ''].join('\n');
  }
}

function debounce<A extends unknown[]>(fn: (...args: A) => void, ms: number): (...args: A) => void {
  let timer: NodeJS.Timeout | undefined;
  return (...args: A) => {
    clearTimeout(timer);
    timer = setTimeout(() => fn(...args), ms);
  };
}
