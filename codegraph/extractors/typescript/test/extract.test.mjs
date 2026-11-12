// Runs the extractor on the Angular fixture and checks the edges each feature is responsible for.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { discover } from '../lib/project.mjs';
import { Extractor } from '../lib/extract.mjs';

const fixture = path.join(path.dirname(fileURLToPath(import.meta.url)), 'fixtures', 'angular-app');
const workspace = discover(fixture);
const extractor = new Extractor({ root: workspace.root, log: () => {} });
for (const config of workspace.configs) extractor.runConfig(config);
const records = [...extractor.records({ target: workspace.target, tool: 'test' })];
const nodes = new Map(records.filter(r => r.type === 'node').map(r => [r.id, r]));
const edges = records.filter(r => r.type === 'edge');
const id = s => s.startsWith('ts:') ? s : `ts:src/app/${s}`;
const has = (from, kind, to) => edges.some(e => e.from === id(from) && e.kind === kind && e.to === id(to));
const edge = (from, kind, to) => assert.ok(has(from, kind, to), `missing ${kind} ${from} -> ${to}`);

test('angular.json yields the app and spec configs, spec files walked once', () => {
  assert.deepEqual(workspace.configs.map(c => path.basename(c.config)), ['tsconfig.app.json', 'tsconfig.spec.json']);
  assert.equal(records[0].type, 'header');
  assert.equal(records[0].language, 'typescript');
  assert.ok(records.some(r => r.type === 'file' && r.path === 'src/app/users.component.html'), 'templateUrl files are stamped');
});

test('declarations become nodes with stable ids and modifiers', () => {
  const load = nodes.get(id('user.service.ts#UserService.load'));
  assert.equal(load.kind, 'Method');
  assert.equal(load.async, true);
  assert.equal(load.override, true);
  assert.equal(load.file, 'src/app/user.service.ts');
  assert.equal(nodes.get(id('user.service.ts#UserService.#secret')).accessibility, 'Private');
  assert.equal(nodes.get(id('repository.ts#Repository.log')).accessibility, 'Protected');
  assert.equal(nodes.get(id('user.ts#User')).typeKind, 'Interface');
  assert.equal(nodes.get(id('user-card.component.ts#UserCardComponent.pick')).kind, 'Method', 'an arrow-function property is a method');
  assert.equal(nodes.get(id('app.routes.ts#routes')).kind, 'Field');
});

test('calls, property reads, heritage and imports', () => {
  edge('user.service.ts#UserService.load', 'Calls', 'http.service.ts#HttpService.get');
  edge('user.service.ts#UserService.load', 'Calls', 'repository.ts#Repository.log');
  edge('user.service.ts#UserService.constructor', 'Calls', 'repository.ts#Repository.constructor');
  edge('user.service.ts#UserService.formatName', 'Calls', 'user.ts#User.name');
  edge('user.service.ts#UserService', 'Inherits', 'repository.ts#Repository');
  edge('user.service.ts#UserService', 'Implements', 'loader.ts#Loader');
  edge('user.service.ts#UserService.load', 'Overrides', 'repository.ts#Repository.load');
  edge('user.service.ts#UserService.load', 'Implements', 'loader.ts#Loader.load');
  edge('ts:file:src/app/user.service.ts', 'Imports', 'ts:file:src/app/http.service.ts');
  edge('user.service.ts#UserService.formatName', 'CallsExternal', 'ts:ext:lib#String.trim');
});

test('dependency injection through the constructor and inject()', () => {
  edge('user.service.ts#UserService', 'Injects', 'http.service.ts#HttpService');
  edge('user-card.component.ts#UserCardComponent', 'Injects', 'user.service.ts#UserService');
  // inject<T>() types the field, so calls through it resolve.
  edge('user-card.component.ts#UserCardComponent.display', 'Calls', 'user.service.ts#UserService.formatName');
});

test('templates: members used, components and pipes rendered, lifecycle hooks invoked', () => {
  edge('user-card.component.ts#UserCardComponent.<template>', 'Calls', 'user-card.component.ts#UserCardComponent.display');
  edge('user-card.component.ts#UserCardComponent.<template>', 'Calls', 'user-card.component.ts#UserCardComponent.pick');
  edge('users.component.ts#UsersComponent.<template>', 'Calls', 'users.component.ts#UsersComponent.select');
  edge('users.component.ts#UsersComponent', 'Renders', 'user-card.component.ts#UserCardComponent');
  edge('user-card.component.ts#UserCardComponent', 'Renders', 'shout.pipe.ts#ShoutPipe');
  edge('ts:framework:tsconfig.app.json', 'InvokedByFramework', 'user-card.component.ts#UserCardComponent.ngOnInit');
  edge('ts:framework:tsconfig.app.json', 'InvokedByFramework', 'users.component.ts#UsersComponent.<template>');
  assert.ok(!edges.some(e => e.kind !== 'Contains' && e.to === id('user-card.component.ts#UserCardComponent.unusedHelper')), 'nothing uses unusedHelper');
});

test('routes render their components, lazy ones too', () => {
  edge('app.routes.ts#routes', 'Renders', 'users.component.ts#UsersComponent');
  edge('app.routes.ts#routes', 'Renders', 'user-card.component.ts#UserCardComponent');
});

test('specs: suites and tests are test nodes, what they touch is TestedBy them', () => {
  const formats = id('user.service.spec.ts#UserService > formats names');
  assert.equal(nodes.get(formats).isTest, true);
  assert.equal(nodes.get(id('user.service.spec.ts#UserService > loads')).isTest, true, 'it.skip is still a test');
  edge('user.service.ts#UserService.formatName', 'TestedBy', formats);
  edge('user.service.ts#UserService.constructor', 'TestedBy', 'user.service.spec.ts#UserService');
});

test('every edge carries the file that produced it', () => {
  assert.ok(edges.every(e => typeof e.file === 'string' && e.file.length > 0));
});
