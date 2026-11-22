// The Overview view: a dashboard of the selected graph, drawn in the webview with the editor's own theme colours.
import * as crypto from 'crypto';
import * as vscode from 'vscode';
import { Daemons } from './daemons';
import { Graph, Graphs } from './graphs';

/** What the webview draws; plain data, so the page script needs no VS Code API beyond postMessage. */
export interface OverviewState {
  installed: boolean;
  version?: string;
  latest?: string;
  selected?: string;
  graphs: {
    db: string;
    name: string;
    root: string;
    busy?: string;
    statusError?: string;
    managed: boolean;
    hasTarget: boolean;
    targets: { file: string; kind: string; missing?: string }[];
    status?: Graph['status'];
  }[];
}

const allowed = new Set([
  'codegraph.install',
  'codegraph.init',
  'codegraph.chooseTarget',
  'codegraph.index',
  'codegraph.rebuild',
  'codegraph.startDaemon',
  'codegraph.stopDaemon',
  'codegraph.restartDaemon',
  'codegraph.showDaemonLog',
  'codegraph.showUsage',
  'codegraph.refresh',
  'codegraph.showOutput',
]);

export class OverviewView implements vscode.WebviewViewProvider, vscode.Disposable {
  private view: vscode.WebviewView | undefined;
  private selected: string | undefined;
  private readonly subscription: vscode.Disposable;

  constructor(
    private readonly extensionUri: vscode.Uri,
    private readonly graphs: Graphs,
    private readonly daemons: Daemons,
    private readonly info: () => { installed: boolean; version?: string; latest?: string },
  ) {
    this.subscription = graphs.onDidChange(() => this.post());
  }

  resolveWebviewView(view: vscode.WebviewView): void {
    this.view = view;
    const media = vscode.Uri.joinPath(this.extensionUri, 'media');
    view.webview.options = { enableScripts: true, localResourceRoots: [media] };
    view.webview.html = this.html(view.webview, media);
    view.webview.onDidReceiveMessage((message: { type: string; command?: string; db?: string }) => {
      if (message.type === 'ready') {
        this.post();
      } else if (message.type === 'select' && message.db) {
        this.selected = message.db;
        this.post();
      } else if (message.type === 'command' && message.command && allowed.has(message.command)) {
        const graph = message.db ? this.graphs.get(message.db) : undefined;
        void vscode.commands.executeCommand(message.command, graph);
      } else if (message.type === 'open' && message.db) {
        void vscode.commands.executeCommand('revealInExplorer', vscode.Uri.file(message.db));
      }
    });
    view.onDidChangeVisibility(() => view.visible && this.post());
  }

  /** Pushes the current state to the page; a hidden view catches up when it is shown. */
  post(): void {
    if (!this.view?.visible) {
      return;
    }
    const all = this.graphs.all();
    if (!all.some((g) => g.db === this.selected)) {
      this.selected = all[0]?.db;
    }
    const state: OverviewState = {
      ...this.info(),
      selected: this.selected,
      graphs: all.map((g) => ({
        db: g.db,
        name: g.name,
        root: g.root,
        busy: g.busy,
        statusError: g.statusError,
        managed: this.daemons.isManaged(g),
        hasTarget: !!this.graphs.daemonTarget(g),
        targets: g.targets.map((t) => ({ file: t.file, kind: t.kind, missing: t.missing?.tool })),
        status: g.status,
      })),
    };
    void this.view.webview.postMessage({ type: 'state', state });
  }

  private html(webview: vscode.Webview, media: vscode.Uri): string {
    const nonce = crypto.randomBytes(16).toString('base64');
    const css = webview.asWebviewUri(vscode.Uri.joinPath(media, 'overview.css'));
    const js = webview.asWebviewUri(vscode.Uri.joinPath(media, 'overview.js'));
    return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource}; img-src ${webview.cspSource} data:; script-src 'nonce-${nonce}';">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<link rel="stylesheet" href="${css}">
<title>CodeGraph</title>
</head>
<body>
<main id="app" aria-live="polite"><div class="skeleton"></div><div class="skeleton short"></div></main>
<script nonce="${nonce}" src="${js}"></script>
</body>
</html>`;
  }

  dispose(): void {
    this.subscription.dispose();
  }
}
