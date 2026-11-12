// Which tsconfig files make up a workspace: an Angular workspace's apps, libraries and spec configs, an Nx
// workspace's project.json files, a solution-style tsconfig's references, or one tsconfig.
import fs from 'node:fs';
import path from 'node:path';

const SKIP = new Set(['node_modules', 'dist', 'out', 'build', 'coverage', '.angular', '.nx', '.git', 'bin', 'obj']);

function readJson(file) {
  try {
    // tsconfig and angular.json allow comments and trailing commas.
    const text = fs.readFileSync(file, 'utf8')
      .replace(/("(?:[^"\\]|\\.)*")|\/\/[^\n]*|\/\*[\s\S]*?\*\//g, (m, str) => str ?? '')
      .replace(/,(\s*[}\]])/g, '$1');
    return JSON.parse(text);
  } catch {
    return null;
  }
}

/** Every tsconfig under a project entry of angular.json: build, test, and anything else that names one. */
function angularConfigs(workspaceFile) {
  const root = path.dirname(workspaceFile);
  const workspace = readJson(workspaceFile);
  const found = [];
  for (const [name, project] of Object.entries(workspace?.projects ?? {})) {
    // Newer test builders (vitest, unit-test) often name no tsConfig: the project's spec config is still the one.
    for (const conventional of ['tsconfig.app.json', 'tsconfig.lib.json', 'tsconfig.spec.json']) {
      const candidate = path.resolve(root, project?.root ?? '', conventional);
      if (fs.existsSync(candidate)) found.push({ name, config: candidate });
    }
    for (const target of Object.values(project?.architect ?? project?.targets ?? {})) {
      for (const options of [target?.options, ...Object.values(target?.configurations ?? {})]) {
        if (typeof options?.tsConfig === 'string') {
          found.push({ name, config: path.resolve(root, options.tsConfig) });
        }
      }
    }
  }
  return found;
}

/** Nx: every project.json beside a tsconfig*.json. */
function nxConfigs(root) {
  const found = [];
  const walk = (dir, depth) => {
    if (depth > 4) return;
    let entries;
    try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
    if (entries.some(e => e.name === 'project.json')) {
      const project = readJson(path.join(dir, 'project.json'));
      for (const e of entries) {
        if (e.isFile() && /^tsconfig\.(app|lib|spec)\.json$/.test(e.name)) {
          found.push({ name: project?.name ?? path.basename(dir), config: path.join(dir, e.name) });
        }
      }
    }
    for (const e of entries) {
      if (e.isDirectory() && !SKIP.has(e.name) && !e.name.startsWith('.')) walk(path.join(dir, e.name), depth + 1);
    }
  };
  walk(root, 0);
  return found;
}

/** A tsconfig and, when it is solution-style (`files: []` plus references), the configs it references. */
function withReferences(config, seen = new Set()) {
  if (seen.has(config) || !fs.existsSync(config)) return [];
  seen.add(config);
  const json = readJson(config);
  const references = (json?.references ?? []).map(r => {
    const target = path.resolve(path.dirname(config), r.path);
    return fs.existsSync(target) && fs.statSync(target).isDirectory() ? path.join(target, 'tsconfig.json') : target;
  });
  const solutionStyle = Array.isArray(json?.files) && json.files.length === 0 && !json.include;
  const own = solutionStyle ? [] : [{ name: path.basename(path.dirname(config)), config }];
  return [...own, ...references.flatMap(r => withReferences(r, seen))];
}

/**
 * The workspace for a target: a tsconfig file, an angular.json, a package.json, or a folder holding one of them.
 * Returns the root (paths in ids are relative to it), the target to record, and the configs, deduplicated.
 */
export function discover(targetArg) {
  let target = path.resolve(targetArg);
  let root = fs.statSync(target).isDirectory() ? target : path.dirname(target);
  let configs;
  if (fs.statSync(target).isDirectory()) {
    if (fs.existsSync(path.join(target, 'angular.json'))) {
      target = path.join(target, 'angular.json');
    } else if (fs.existsSync(path.join(target, 'tsconfig.json'))) {
      target = path.join(target, 'tsconfig.json');
    } else if (fs.existsSync(path.join(target, 'nx.json'))) {
      target = path.join(target, 'nx.json');
    } else {
      throw new Error(`no angular.json, tsconfig.json or nx.json in ${target}`);
    }
  }

  const base = path.basename(target);
  if (base === 'angular.json') {
    configs = angularConfigs(target);
  } else if (base === 'nx.json') {
    configs = nxConfigs(root);
  } else if (base === 'package.json') {
    configs = withReferences(path.join(root, 'tsconfig.json'));
  } else {
    configs = withReferences(target);
  }

  const unique = new Map();
  for (const c of configs) {
    if (fs.existsSync(c.config) && !unique.has(c.config)) unique.set(c.config, c);
  }
  if (unique.size === 0) throw new Error(`no tsconfig found for ${target}`);
  return { root, target, configs: [...unique.values()] };
}
