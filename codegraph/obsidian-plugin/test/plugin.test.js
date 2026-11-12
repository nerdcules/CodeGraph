'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const os = require('os');
const path = require('path');
const Module = require('module');
const { execFileSync } = require('child_process');

const { parseRow, parseAnswer } = require('../lib/rows.js');
const { linkText, notePath, wikilink, idAt, membersOf, editorUrl } = require('../lib/links.js');
const { readMcpEntry, fingerprint, resolveLaunch } = require('../lib/config.js');
const { McpClient } = require('../lib/mcp.js');
const { exportArgs, summaryOf, run } = require('../lib/run.js');

// Rows exactly as CodeGraphTools.Format writes them.
const CALLERS = [
  'callers of GymX.Core.WorkoutService.SaveAsync:',
  'method  GymX.Core.WorkoutService.SaveAsync(Workout, CancellationToken)  depth 1  C:\\dev\\GymX\\src\\GymX.Core\\WorkoutService.cs:41-67  [M:GymX.Core.WorkoutService.SaveAsync(GymX.Core.Workout,System.Threading.CancellationToken)]',
  'method  App.Sort(Int32[])  /repo/My Folder/Sort.cs:3-9  [M:App.Sort(System.Int32[])]  STALE',
  'external  System.Console.WriteLine  [ext:System.Console.WriteLine]',
  '',
].join('\n');

test('rows: kind, name, extra, location and id, Windows paths and ids with brackets included', () => {
  const answer = parseAnswer(CALLERS);
  assert.equal(answer.stale, null);
  assert.equal(answer.items.length, 4);
  assert.deepEqual(answer.items[0], { text: 'callers of GymX.Core.WorkoutService.SaveAsync:' });

  const first = answer.items[1].row;
  assert.equal(first.kind, 'method');
  assert.equal(first.fullName, 'GymX.Core.WorkoutService.SaveAsync(Workout, CancellationToken)');
  assert.equal(first.extra, 'depth 1');
  assert.equal(first.file, 'C:\\dev\\GymX\\src\\GymX.Core\\WorkoutService.cs');
  assert.equal(first.startLine, 41);
  assert.equal(first.endLine, 67);
  assert.equal(first.id, 'M:GymX.Core.WorkoutService.SaveAsync(GymX.Core.Workout,System.Threading.CancellationToken)');
  assert.equal(first.stale, false);

  const second = answer.items[2].row;
  assert.equal(second.file, '/repo/My Folder/Sort.cs');
  assert.equal(second.extra, null);
  assert.equal(second.id, 'M:App.Sort(System.Int32[])');
  assert.equal(second.stale, true);

  const external = answer.items[3].row;
  assert.equal(external.file, null);
  assert.equal(external.id, 'ext:System.Console.WriteLine');
});

test('rows: the staleness warning is split off, prose lines stay text', () => {
  const answer = parseAnswer('⚠ STALE INDEX: HEAD moved\nService.Save: no callers in the indexed source');
  assert.equal(answer.stale, '⚠ STALE INDEX: HEAD moved');
  assert.deepEqual(answer.items, [{ text: 'Service.Save: no callers in the indexed source' }]);
  assert.equal(parseRow('status: STALE'), null);
});

// The shape ObsidianExport.Manifest serialises.
const MANIFEST = {
  version: 1,
  linkRoot: '_code',
  commit: 'd17bc4dabc',
  indexedAt: '2026-11-12T10:00:00Z',
  files: ['Index.md', 'Types/App/App.Store.md'],
  nodes: {
    'T:App.Store': { path: 'Types/App/App.Store.md', heading: null },
    'M:App.Store.Save(System.Int32)': { path: 'Types/App/App.Store.md', heading: 'Save' },
    'M:App.Store.Save(System.String)': { path: 'Types/App/App.Store.md', heading: 'Save 2' },
    'P:App': { path: 'Projects/App.md', heading: null },
  },
};

test('links: ids map to link text, wikilinks and back from a note and heading', () => {
  assert.equal(linkText(MANIFEST, 'M:App.Store.Save(System.String)'), '_code/Types/App/App.Store#Save 2');
  assert.equal(linkText(MANIFEST, 'T:App.Store'), '_code/Types/App/App.Store');
  assert.equal(linkText(MANIFEST, 'T:Missing'), null);
  assert.equal(notePath(MANIFEST, 'P:App'), '_code/Projects/App.md');
  assert.equal(wikilink(MANIFEST, 'T:App.Store', 'Store|x'), '[[_code/Types/App/App.Store|Store∣x]]');
  assert.equal(idAt(MANIFEST, '_code/Types/App/App.Store.md', 'Save 2'), 'M:App.Store.Save(System.String)');
  assert.equal(idAt(MANIFEST, '_code/Types/App/App.Store.md', 'Unknown heading'), 'T:App.Store');
  assert.equal(idAt(MANIFEST, '_code/Other.md', null), null);
  assert.deepEqual(membersOf(MANIFEST, '_code/Types/App/App.Store.md').map((m) => m.heading), ['Save', 'Save 2']);
});

test('links: editor URLs get a leading slash and forward slashes on every OS', () => {
  assert.equal(editorUrl('vscode://file{file}:{line}', '/repo/src/A.cs', 12), 'vscode://file/repo/src/A.cs:12');
  assert.equal(editorUrl('vscode://file{file}:{line}', 'C:\\dev\\My App\\A.cs', 3), 'vscode://file/C:/dev/My%20App/A.cs:3');
});

test('config: the repo\'s .mcp.json decides the server, the indexer sits beside it', () => {
  const files = {
    [path.join('/repo', '.mcp.json')]: JSON.stringify({
      mcpServers: {
        other: { command: 'x' },
        codegraph: { command: '/home/me/.codegraph/bin/codegraph-mcp', env: { DOTNET_ROOT: '/opt/dotnet' } },
      },
    }),
  };
  const host = {
    platform: 'linux',
    home: '/home/me',
    env: { PATH: '/usr/bin' },
    exists: (p) => p === '/home/me/.codegraph/bin/codegraph-index',
    readFile: (p) => {
      if (!(p in files)) throw new Error('ENOENT');
      return files[p];
    },
  };

  const entry = readMcpEntry('/repo', host.readFile);
  assert.equal(entry.env.DOTNET_ROOT, '/opt/dotnet');

  // The entry is repo content: until the user approves it, the installed binaries run with the plain environment.
  const pending = resolveLaunch({ repoRoot: '/repo' }, host);
  assert.equal(pending.repoConfig.decision, 'pending');
  assert.equal(pending.repoConfig.command, '/home/me/.codegraph/bin/codegraph-mcp');
  assert.equal(pending.mcp.command, 'codegraph-mcp');
  assert.equal(pending.mcp.env.DOTNET_ROOT, undefined);
  assert.equal(pending.index.env.DOTNET_ROOT, undefined);
  const ignored = resolveLaunch({ repoRoot: '/repo', ignoredRepoConfig: { '/repo': fingerprint(entry) } }, host);
  assert.equal(ignored.repoConfig.decision, 'ignored');
  assert.equal(ignored.mcp.command, 'codegraph-mcp');
  // An approval covers one exact entry: a changed command, argument or variable asks again.
  const changed = resolveLaunch({ repoRoot: '/repo', approvedRepoConfig: { '/repo': fingerprint({ ...entry, env: { DOTNET_ROOT: '/evil' } }) } }, host);
  assert.equal(changed.repoConfig.decision, 'pending');
  assert.equal(changed.mcp.env.DOTNET_ROOT, undefined);
  assert.equal(fingerprint(entry), fingerprint(readMcpEntry('/repo', host.readFile)));

  const launch = resolveLaunch({ repoRoot: '/repo', approvedRepoConfig: { '/repo': fingerprint(entry) } }, host);
  assert.equal(launch.repoConfig.decision, 'approved');
  assert.equal(launch.mcp.command, '/home/me/.codegraph/bin/codegraph-mcp');
  assert.equal(launch.mcp.cwd, '/repo');
  assert.equal(launch.mcp.env.DOTNET_ROOT, '/opt/dotnet');
  assert.equal(launch.mcp.env.PATH, '/usr/bin');
  assert.equal(launch.index.command, '/home/me/.codegraph/bin/codegraph-index');
  assert.equal(launch.dbPath, path.join('/repo', '.codegraph', 'graph.db'));

  // No .mcp.json: settings, then the install folder, then PATH.
  const bare = resolveLaunch({ repoRoot: '/elsewhere', indexPath: '/opt/ci' }, { ...host, exists: () => false });
  assert.equal(bare.repoConfig, null);
  assert.equal(bare.mcp.command, 'codegraph-mcp');
  assert.equal(bare.index.command, '/opt/ci');
});

test('config: a database path in the server arguments is the graph the notes come from', () => {
  const readFile = () => JSON.stringify({ mcpServers: { codegraph: { command: 'codegraph-mcp', args: ['other/graph.db'] } } });
  const approved = { '/repo': fingerprint(readMcpEntry('/repo', readFile)) };
  const launch = resolveLaunch({ repoRoot: '/repo', approvedRepoConfig: approved }, {
    platform: 'win32',
    home: 'C:\\Users\\me',
    env: {},
    exists: () => false,
    readFile,
  });
  assert.equal(launch.dbPath, path.resolve('/repo', 'other/graph.db'));
  assert.equal(launch.index.command, 'codegraph-index');
});

test('mcp: initializes once, calls tools, surfaces tool errors and restarts after the server exits', async () => {
  const client = new McpClient({ command: process.execPath, args: [path.join(__dirname, 'fixtures', 'fake-mcp.js')] }, { timeoutMs: 10000 });
  try {
    const text = await client.callTool('callers_of', { id: 'M:App.Store.Save', depth: 2 });
    const answer = parseAnswer(text);
    assert.match(answer.stale, /^⚠ STALE INDEX/);
    assert.equal(answer.items[1].row.id, 'M:App.Client.Run');
    assert.equal(answer.items[1].row.extra, 'depth 2');

    await assert.rejects(client.callTool('broken', {}), /no node with id X/);
    await assert.rejects(client.callTool('crash', {}), /exited/);

    const again = await client.callTool('callers_of', { id: 'M:Other', depth: 1 });
    assert.match(again, /callers of M:Other/);
  } finally {
    client.stop();
  }
});

test('run: exporter arguments and its summary line', async () => {
  assert.deepEqual(exportArgs('/repo/.codegraph/graph.db', '/vault/_code', '_code'),
    ['export', 'obsidian', '--db', '/repo/.codegraph/graph.db', '--out', '/vault/_code', '--link-root', '_code']);
  assert.equal(summaryOf('noise\nobsidian: 3 written, 10 unchanged, 1 deleted in /vault/_code\n'), 'obsidian: 3 written, 10 unchanged, 1 deleted');
  const result = await run(process.execPath, ['-e', 'console.log("out"); console.error("err"); process.exit(2)']);
  assert.deepEqual(result, { code: 2, stdout: 'out\n', stderr: 'err\n' });
});

test('bundle: main.js is current and loads as one module with Obsidian stubbed', () => {
  execFileSync(process.execPath, [path.join(__dirname, '..', 'build.mjs'), '--check']);
  const stub = {};
  for (const name of ['Plugin', 'PluginSettingTab', 'ItemView', 'SuggestModal', 'Modal', 'MarkdownView', 'FileSystemAdapter', 'Setting', 'Notice']) {
    stub[name] = class {};
  }

  const original = Module._load;
  Module._load = function (request, ...rest) {
    return request === 'obsidian' ? stub : original.call(this, request, ...rest);
  };
  try {
    const file = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-obsidian-')), 'main.js');
    fs.copyFileSync(path.join(__dirname, '..', 'main.js'), file);
    const Plugin = require(file);
    assert.equal(typeof Plugin, 'function');
    assert.ok(Plugin.prototype instanceof stub.Plugin);
    assert.equal(typeof Plugin.prototype.rebuild, 'function');
  } finally {
    Module._load = original;
  }
});
