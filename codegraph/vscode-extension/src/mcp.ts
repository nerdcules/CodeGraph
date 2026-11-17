// Offers codegraph-mcp to VS Code's chat clients (GitHub Copilot's agent mode and others) for each graph, so no
// .vscode/mcp.json is needed. A workspace that already names a codegraph server there keeps its own entry.
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { Cli } from './cli';
import { Graphs } from './graphs';

export function registerMcp(context: vscode.ExtensionContext, cli: Cli, graphs: Graphs, version: () => string | undefined): void {
  // registerMcpServerDefinitionProvider arrived in VS Code 1.101; an editor without it (an older fork) just skips this.
  const lm = vscode.lm as typeof vscode.lm | undefined;
  if (!lm || typeof lm.registerMcpServerDefinitionProvider !== 'function') {
    return;
  }
  const changed = new vscode.EventEmitter<void>();
  let last = '';
  context.subscriptions.push(
    changed,
    graphs.onDidChange(() => {
      // Only a change in what would be offered re-announces the list; status refreshes do not. Whether
      // .vscode/mcp.json names a codegraph server is part of it: set-up adds one, and removing it brings ours back.
      const key = JSON.stringify([enabled(), cli.installed(), version(), graphs.all().map((g) => [g.root, g.status?.exists, namedInWorkspaceConfig(g.root)])]);
      if (key !== last) {
        last = key;
        changed.fire();
      }
    }),
    vscode.workspace.onDidChangeConfiguration((e) => e.affectsConfiguration('codegraph') && changed.fire()),
    lm.registerMcpServerDefinitionProvider('codegraph.mcp', {
      onDidChangeMcpServerDefinitions: changed.event,
      provideMcpServerDefinitions: () => {
        if (!enabled() || !cli.installed()) {
          return [];
        }
        const server = cli.server();
        return graphs
          .all()
          .filter((g) => g.status?.exists && !namedInWorkspaceConfig(g.root))
          .map((g) => {
            const label = graphs.all().length > 1 ? `CodeGraph (${g.name})` : 'CodeGraph';
            const definition = new vscode.McpStdioServerDefinition(label, server, ['--root', g.root], {}, version());
            definition.cwd = vscode.Uri.file(g.root);
            return definition;
          });
      },
    }),
  );
}

function enabled(): boolean {
  return vscode.workspace.getConfiguration('codegraph').get<boolean>('registerMcpServer', true);
}

/** True when .vscode/mcp.json (written by `codegraph-index init --client vscode`) already has a codegraph server. */
export function namedInWorkspaceConfig(root: string): boolean {
  try {
    const text = fs.readFileSync(path.join(root, '.vscode', 'mcp.json'), 'utf8');
    return /"codegraph"\s*:/.test(text);
  } catch {
    return false;
  }
}
