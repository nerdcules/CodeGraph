// Runs inside VS Code (see runTest.ts): the extension finds the fixture, indexes it with the real codegraph-index,
// starts and stops a real daemon, and sees an edit make the graph stale. No test framework: each step throws on failure.
import * as assert from 'assert';
import * as cp from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import type { Cli } from '../../cli';
import type { Graph, Graphs } from '../../graphs';
import type { McpOffer } from '../../mcp';

async function until<T>(what: string, probe: () => Promise<T | undefined> | T | undefined, seconds: number): Promise<T> {
  const deadline = Date.now() + seconds * 1000;
  for (;;) {
    const value = await probe();
    if (value) {
      return value;
    }
    if (Date.now() > deadline) {
      throw new Error(`timed out after ${seconds} s waiting for ${what}`);
    }
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
}

/**
 * Starts an MCP server the way a chat client does with a definition (command, args, working folder), and asks it to
 * initialize and call one tool over stdio (newline-delimited JSON-RPC). Returns the tool's text.
 */
async function callTool(command: string, args: string[], cwd: string, tool: string): Promise<string> {
  const child = cp.spawn(command, args, { cwd, stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
  const stderr: string[] = [];
  child.stderr.setEncoding('utf8').on('data', (chunk: string) => stderr.push(chunk));
  const answers = new Map<number, (message: { result?: { content?: { text?: string }[] }; error?: unknown }) => void>();
  let buffer = '';
  child.stdout.setEncoding('utf8').on('data', (chunk: string) => {
    buffer += chunk;
    for (let end = buffer.indexOf('\n'); end >= 0; end = buffer.indexOf('\n')) {
      const line = buffer.slice(0, end).trim();
      buffer = buffer.slice(end + 1);
      if (line) {
        const message = JSON.parse(line) as { id?: number; result?: { content?: { text?: string }[] }; error?: unknown };
        if (typeof message.id === 'number') {
          answers.get(message.id)?.(message);
        }
      }
    }
  });
  const request = (id: number, method: string, params: unknown) =>
    new Promise<{ result?: { content?: { text?: string }[] }; error?: unknown }>((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(`${method}: no answer in 60 s (stderr: ${stderr.join('').trim()})`)), 60_000);
      answers.set(id, (message) => {
        clearTimeout(timer);
        resolve(message);
      });
      child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
    });
  try {
    const init = await request(1, 'initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'codegraph-vscode-test', version: '0' } });
    assert.ok(init.result, `initialize answered: ${JSON.stringify(init)}`);
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
    const call = await request(2, 'tools/call', { name: tool, arguments: {} });
    assert.ok(call.result, `${tool} answered: ${JSON.stringify(call)}`);
    return (call.result.content ?? []).map((c) => c.text ?? '').join('\n');
  } finally {
    child.stdin.end();
    child.kill();
  }
}

const steps: [string, (ctx: { graphs: Graphs; cli: Cli; mcp: McpOffer; graph: () => Graph }) => Promise<void>][] = [
  [
    'registers its commands',
    async () => {
      const commands = await vscode.commands.getCommands(true);
      for (const name of ['codegraph.init', 'codegraph.index', 'codegraph.rebuild', 'codegraph.startDaemon', 'codegraph.stopDaemon', 'codegraph.install']) {
        assert.ok(commands.includes(name), `${name} is registered`);
      }
    },
  ],
  [
    'finds the solution and reports the graph as not built',
    async ({ graphs, graph }) => {
      await until('discovery', () => graphs.all().length > 0 && graphs.all()[0].status, 60);
      assert.equal(graphs.all().length, 1);
      assert.deepEqual(graph().targets.map((t) => [path.basename(t.file), t.kind]), [['Hello.slnx', 'csharp']]);
      assert.equal(graph().status?.exists, false);
      assert.ok(graph().status?.version, 'status reports the indexer version');
    },
  ],
  [
    'indexes the solution',
    async ({ graphs, graph }) => {
      await vscode.commands.executeCommand('codegraph.index', graph());
      await graphs.refresh(graph());
      const status = graph().status!;
      assert.equal(status.exists, true, `the graph exists (status: ${JSON.stringify(status)})`);
      assert.ok(status.graph, `the graph was read (${status.error ?? ''})`);
      assert.equal(status.graph!.stale, false);
      const csharp = status.graph!.languages.find((l) => l.name === 'csharp');
      assert.ok(csharp && csharp.nodes > 0 && csharp.files > 0 && csharp.compileErrors === 0, `C# rows: ${JSON.stringify(csharp)}`);
    },
  ],
  [
    'offers the MCP server for the graph, which answers from it',
    async ({ graphs, cli, mcp, graph }) => {
      const root = graph().root;
      const [definition, ...more] = mcp.definitions();
      assert.equal(more.length, 0, 'one definition for one graph');
      assert.equal(definition.label, 'CodeGraph');
      assert.equal(definition.command, cli.server());
      assert.deepEqual(definition.args, ['--root', root]);
      assert.equal(definition.cwd?.fsPath, root);

      // The definition started as a chat client starts it. The test's install folder may be the indexer's build
      // output, which has no codegraph-mcp: CODEGRAPH_TEST_MCP names the server's own build then.
      const command = process.env.CODEGRAPH_TEST_MCP || definition.command;
      const stats = await callTool(command, definition.args, definition.cwd!.fsPath, 'graph_stats');
      assert.equal(stats, `${graph().status!.graph!.nodes} nodes, ${graph().status!.graph!.edges} edges`);

      // A workspace that names its own codegraph server in .vscode/mcp.json gets no second one, and gets ours back
      // when the entry goes; each change re-announces the list.
      let announced = 0;
      const subscription = mcp.onDidChange(() => announced++);
      const config = path.join(root, '.vscode', 'mcp.json');
      try {
        fs.writeFileSync(config, JSON.stringify({ servers: { codegraph: { type: 'stdio', command: definition.command, args: definition.args } } }));
        await graphs.refreshAll();
        assert.equal(announced, 1, 'adding the workspace entry re-announces');
        assert.deepEqual(mcp.definitions(), []);
        fs.rmSync(config);
        await graphs.refreshAll();
        assert.equal(announced, 2, 'removing it re-announces');
        assert.equal(mcp.definitions().length, 1);
        await graphs.refreshAll();
        assert.equal(announced, 2, 'a status refresh with nothing changed does not');
      } finally {
        subscription.dispose();
        fs.rmSync(config, { force: true });
      }
    },
  ],
  [
    'sees an edit make the graph stale',
    async ({ graphs, graph }) => {
      const program = path.join(process.env.CODEGRAPH_TEST_WORKSPACE!, 'Hello', 'Program.cs');
      fs.appendFileSync(program, '\npublic sealed class Added { }\n');
      await graphs.refresh(graph());
      assert.equal(graph().status?.graph?.stale, true);
      assert.equal(graph().status?.graph?.changedCount, 1);
    },
  ],
  [
    'starts the live daemon, which indexes the edit',
    async ({ graphs, graph }) => {
      await vscode.commands.executeCommand('codegraph.startDaemon', graph());
      const daemon = await until(
        'the daemon to watch',
        async () => {
          await graphs.refresh(graph());
          const d = graph().status?.daemon;
          return d && !d.loading && !d.indexing && d.lastIndexedAt ? d : undefined;
        },
        180,
      );
      assert.equal(path.basename(daemon.solution), 'Hello.slnx');
      await until('the edit to be indexed', async () => {
        await graphs.refresh(graph());
        return graph().status?.graph?.stale === false;
      }, 60);
    },
  ],
  [
    'stops the daemon',
    async ({ graphs, graph }) => {
      await vscode.commands.executeCommand('codegraph.stopDaemon', graph());
      await graphs.refresh(graph());
      assert.equal(graph().status?.daemon, undefined);
    },
  ],
  [
    'rebuilds the graph in full',
    async ({ graphs, graph }) => {
      await vscode.commands.executeCommand('codegraph.rebuild', graph());
      await graphs.refresh(graph());
      // On a miss, the paths: a recorded path spelled unlike the listed target once made the rebuild ask (Windows 8.3).
      assert.equal(graph().status?.graph?.mode, 'full', `graph records ${graph().status?.graph?.solution}; targets ${graph().targets.map((t) => t.file).join(', ')}`);
      assert.equal(graph().status?.graph?.stale, false);
    },
  ],
];

export async function run(): Promise<void> {
  const extension = vscode.extensions.getExtension<{ graphs: Graphs; cli: Cli; mcp: McpOffer }>('nerdcules.codegraph');
  assert.ok(extension, 'the extension is loaded');
  const api = await extension.activate();
  const ctx = { ...api, graph: () => api.graphs.all()[0] };
  let failed = 0;
  let passed = 0;
  for (const [name, step] of steps) {
    const started = Date.now();
    try {
      await step(ctx);
      passed++;
      console.log(`  ✔ ${name} (${((Date.now() - started) / 1000).toFixed(1)} s)`);
    } catch (error) {
      failed++;
      console.log(`  ✘ ${name}\n    ${error instanceof Error ? error.stack ?? error.message : String(error)}`);
      break; // later steps build on earlier ones
    }
  }
  console.log(`integration: ${steps.length} steps, ${passed} passed, ${failed} failed, ${steps.length - passed - failed} not run`);
  if (failed > 0) {
    throw new Error(`${failed} integration step(s) failed`);
  }
}
