// Unit tests for the modules with no VS Code import. Run with `node --test out/test/unit/`.
import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { test } from 'node:test';
import { ago, bytes, count, languageName, plural, shortCommit } from '../../format';
import { assetName, checksumFor, compareVersions, defaultInstallDir, executable, isInstallFolder, runtimeId } from '../../platform';
import { daemonStateOf, healthOf, parseStatus, StatusReport } from '../../status';
import { chooseTargets, classify, indexedTargets, isInside, solutionProjects, sourceExtensions } from '../../targets';

test('count writes thousands and compacts large numbers', () => {
  assert.equal(count(3276), '3,276');
  assert.equal(count(3276, true), '3,276');
  assert.equal(count(20245, true), '20.2k');
  assert.equal(count(2533862, true), '2.5M');
  assert.equal(count(175547, true), '176k');
  assert.equal(count(1_000_000, true), '1M');
});

test('bytes, ago, plural, shortCommit, languageName', () => {
  assert.equal(bytes(512), '512 B');
  assert.equal(bytes(1536), '1.5 KB');
  assert.equal(bytes(412 * 1024 * 1024), '412 MB');
  const now = new Date('2026-11-16T12:00:00Z');
  assert.equal(ago('2026-11-16T11:59:30Z', now), 'just now');
  assert.equal(ago('2026-11-16T11:55:00Z', now), '5 min ago');
  assert.equal(ago('2026-11-16T09:00:00Z', now), '3 h ago');
  assert.equal(ago('2026-11-15T12:00:00Z', now), '1 day ago');
  assert.equal(ago('2026-11-01T12:00:00Z', now), '2026-11-01');
  assert.equal(ago(undefined, now), 'never');
  assert.equal(plural(1, 'file'), '1 file');
  assert.equal(plural(1200, 'file'), '1,200 files');
  assert.equal(shortCommit('63f502c42abb492abd5d537a0dc79822e5acc229'), '63f502c');
  assert.equal(shortCommit(undefined), '?');
  assert.equal(languageName('csharp'), 'C#');
  assert.equal(languageName('cobol'), 'cobol');
});

test('runtime ids and asset names match tools/release/package.sh', () => {
  assert.equal(runtimeId('linux', 'x64'), 'linux-x64');
  assert.equal(runtimeId('darwin', 'arm64'), 'osx-arm64');
  assert.equal(runtimeId('win32', 'x64'), 'win-x64');
  assert.equal(runtimeId('freebsd', 'x64'), undefined);
  assert.equal(runtimeId('linux', 'ia32'), undefined);
  assert.equal(assetName('linux-arm64'), 'codegraph-linux-arm64.tar.gz');
  assert.equal(assetName('win-arm64'), 'codegraph-win-arm64.zip');
  assert.equal(defaultInstallDir('linux', '/home/me'), path.join('/home/me', '.codegraph', 'bin'));
  assert.equal(defaultInstallDir('win32', 'C:\\Users\\me'), 'C:\\tools\\codegraph');
  assert.equal(executable('codegraph-mcp', 'win32'), 'codegraph-mcp.exe');
  assert.equal(executable('codegraph-mcp', 'linux'), 'codegraph-mcp');
});

test('checksumFor reads a SHA256SUMS line by file name', () => {
  const a = 'a'.repeat(64);
  const b = 'B'.repeat(64);
  const sums = `${a}  codegraph-linux-x64.tar.gz\r\n${b} *codegraph-win-x64.zip\n`;
  assert.equal(checksumFor(sums, 'codegraph-linux-x64.tar.gz'), a);
  assert.equal(checksumFor(sums, 'codegraph-win-x64.zip'), 'b'.repeat(64));
  assert.equal(checksumFor(sums, 'codegraph-osx-x64.tar.gz'), undefined);
});

test('compareVersions orders dotted versions numerically', () => {
  assert.equal(compareVersions('v1.10.0', '1.9.2'), 1);
  assert.equal(compareVersions('1.2.0', 'v1.2.0'), 0);
  assert.equal(compareVersions('1.2', '1.2.1'), -1);
  assert.equal(compareVersions('1.3.0-rc1', '1.3.0'), 0);
});

test('classify knows every marker the indexer accepts', () => {
  assert.equal(classify('/r/App.slnx'), 'csharp');
  assert.equal(classify('/r/App.SLN'), 'csharp');
  assert.equal(classify('/r/src/Lib.csproj'), 'csharp');
  assert.equal(classify('/r/web/tsconfig.json'), 'typescript');
  assert.equal(classify('/r/angular.json'), 'typescript');
  assert.equal(classify('/r/build.gradle.kts'), 'java');
  assert.equal(classify('/r/pyproject.toml'), 'python');
  assert.equal(classify('/r/go.work'), 'go');
  assert.equal(classify('/r/Cargo.toml'), 'rust');
  assert.equal(classify('/r/package.json'), undefined);
});

test('chooseTargets keeps solutions over the projects they list and the outermost marker of each language', () => {
  const r = path.resolve('/repo');
  const chosen = chooseTargets([
    path.join(r, 'src', 'Lib', 'Lib.csproj'),
    path.join(r, 'App.slnx'),
    path.join(r, 'web', 'tsconfig.json'),
    path.join(r, 'web', 'projects', 'lib', 'tsconfig.json'),
    path.join(r, 'tools', 'tsconfig.json'),
    path.join(r, 'java', 'pom.xml'),
    path.join(r, 'java', 'module', 'pom.xml'),
  ], () => [path.join(r, 'src', 'Lib', 'Lib.csproj')]);
  assert.deepEqual(
    chosen.map((t) => [t.kind, path.relative(r, t.file)]),
    [
      ['csharp', 'App.slnx'],
      ['java', path.join('java', 'pom.xml')],
      ['typescript', path.join('tools', 'tsconfig.json')],
      ['typescript', path.join('web', 'tsconfig.json')],
    ],
  );
  // Without a solution, each project is its own target.
  assert.equal(chooseTargets([path.join(r, 'A.csproj'), path.join(r, 'b', 'B.csproj')]).length, 2);
  // A project the solution does not list stays: another repository in the same folder, or one left out of it.
  const other = path.resolve('/folder/other/Tool/Tool.csproj');
  assert.deepEqual(
    chooseTargets([path.join(r, 'App.sln'), path.join(r, 'Lib', 'Lib.csproj'), other], () => [path.join(r, 'Lib', 'Lib.csproj')]).map((t) => t.file),
    [other, path.join(r, 'App.sln')].sort((a, b) => a.localeCompare(b)),
  );
  assert.ok(isInside(path.join(r, 'a', 'b'), r));
  assert.ok(isInside(r, r));
  assert.ok(!isInside(path.resolve('/repository'), r));
});

test('solutionProjects reads the projects of a .sln and a .slnx', () => {
  const r = path.resolve('/repo');
  const sln = [
    'Microsoft Visual Studio Solution File, Format Version 12.00',
    'Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "src\\App\\App.csproj", "{11111111-1111-1111-1111-111111111111}"',
    'EndProject',
    'Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "docs", "docs", "{22222222-2222-2222-2222-222222222222}"',
    'EndProject',
  ].join('\r\n');
  assert.deepEqual(solutionProjects(path.join(r, 'App.sln'), sln), [path.join(r, 'src', 'App', 'App.csproj')]);
  const slnx = '<Solution>\n  <Folder Name="/src/">\n    <Project Path="src/Lib/Lib.csproj" />\n  </Folder>\n  <Project Path="Hello/Hello.csproj" Type="Classic C#" />\n</Solution>';
  assert.deepEqual(solutionProjects(path.join(r, 'App.slnx'), slnx), [path.join(r, 'src', 'Lib', 'Lib.csproj'), path.join(r, 'Hello', 'Hello.csproj')]);
});

test('indexedTargets feeds a graph one target per kind: the one it holds, else the daemon\'s, else a solution', () => {
  const r = path.resolve('/repo');
  const web = { file: path.join(r, 'web', 'tsconfig.json'), kind: 'typescript' as const };
  const tools = { file: path.join(r, 'tools', 'tsconfig.json'), kind: 'typescript' as const };
  const sln = { file: path.join(r, 'App.sln'), kind: 'csharp' as const };
  const project = { file: path.join(r, 'Tool', 'Tool.csproj'), kind: 'csharp' as const };
  const targets = [project, sln, tools, web];
  assert.deepEqual(indexedTargets(targets, () => undefined), [sln, tools]);
  assert.deepEqual(indexedTargets(targets, (kind) => (kind === 'typescript' ? web.file : undefined)), [sln, web]);
  assert.deepEqual(indexedTargets(targets, () => undefined, project), [project, tools]);
  // A held target that is no longer found does not stop the kind from being indexed.
  assert.deepEqual(indexedTargets([web], () => path.join(r, 'gone', 'tsconfig.json')), [web]);
});

test('isInstallFolder accepts only a missing, empty or CodeGraph folder', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-unit-'));
  try {
    assert.ok(isInstallFolder(path.join(dir, 'missing')));
    assert.ok(isInstallFolder(dir));
    fs.writeFileSync(path.join(dir, 'codegraph-index'), '');
    fs.writeFileSync(path.join(dir, 'other-tool'), '');
    assert.ok(!isInstallFolder(dir), 'a shared bin folder');
    fs.writeFileSync(path.join(dir, 'codegraph-index.dll'), '');
    fs.writeFileSync(path.join(dir, 'codegraph-mcp.dll'), '');
    assert.ok(isInstallFolder(dir));
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('a save of an ES or CommonJS module re-indexes TypeScript, like TypeScriptExtractor.SourceExtensions', () => {
  for (const extension of ['.ts', '.tsx', '.mts', '.cts', '.js', '.jsx', '.mjs', '.cjs', '.html', '.json']) {
    assert.ok(sourceExtensions.typescript.includes(extension), extension);
  }
});

test('parseStatus accepts the indexer JSON and rejects anything else', () => {
  const report = parseStatus(JSON.stringify({ version: '1.2.0', db: '/r/.codegraph/graph.db', exists: false }));
  assert.equal(healthOf(report), 'missing');
  assert.throws(() => parseStatus('{"hello": 1}'));
  assert.throws(() => parseStatus('usage: codegraph-index ...'));
});

test('healthOf and daemonStateOf name the states the views draw', () => {
  const graph = {
    schemaCurrent: true, headMoved: false, stale: false, changedCount: 0, changedFiles: [], missingCount: 0, missingFiles: [], nodes: 1, edges: 0, languages: [],
  };
  const base: StatusReport = { version: '1', db: 'g.db', exists: true, graph };
  assert.equal(healthOf(undefined), 'missing');
  assert.equal(healthOf(base), 'fresh');
  assert.equal(healthOf({ ...base, graph: { ...graph, stale: true } }), 'stale');
  assert.equal(healthOf({ ...base, graph: undefined, error: 'damaged' }), 'error');
  const daemon = { pid: 1, solution: 's', dbPath: 'g', projects: 1, documents: 1, startedAt: '', pending: 0, indexing: false, loading: false };
  assert.equal(daemonStateOf(undefined), 'stopped');
  assert.equal(daemonStateOf({ ...daemon, loading: true }), 'loading');
  assert.equal(daemonStateOf({ ...daemon, pending: 2 }), 'indexing');
  assert.equal(daemonStateOf(daemon), 'watching');
});
