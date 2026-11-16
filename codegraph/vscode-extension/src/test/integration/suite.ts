// Runs inside VS Code (see runTest.ts): the extension finds the fixture, indexes it with the real codegraph-index,
// starts and stops a real daemon, and sees an edit make the graph stale. No test framework: each step throws on failure.
import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import type { Cli } from '../../cli';
import type { Graph, Graphs } from '../../graphs';

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

const steps: [string, (ctx: { graphs: Graphs; cli: Cli; graph: () => Graph }) => Promise<void>][] = [
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
      assert.equal(graph().status?.graph?.mode, 'full');
      assert.equal(graph().status?.graph?.stale, false);
    },
  ],
];

export async function run(): Promise<void> {
  const extension = vscode.extensions.getExtension<{ graphs: Graphs; cli: Cli }>('nerdcules.codegraph');
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
