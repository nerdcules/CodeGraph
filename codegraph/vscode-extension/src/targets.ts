// What codegraph-index can index, and which tool each kind of target needs. No VS Code imports.
import * as fs from 'fs';
import * as path from 'path';

export type TargetKind = 'csharp' | 'typescript' | 'java' | 'python' | 'go' | 'rust';

export interface Requirement {
  /** The program that must be on PATH. */
  tool: string;
  /** What to tell the user when it is missing. */
  install: string;
  /** The page that explains it. */
  url: string;
}

/** The glob the workspace search uses: every file name `classify` accepts. */
export const targetGlob =
  '**/{*.sln,*.slnx,*.csproj,tsconfig.json,angular.json,nx.json,pom.xml,build.gradle,build.gradle.kts,build.sbt,pyproject.toml,setup.py,go.work,go.mod,Cargo.toml}';

/** Folders never searched for targets: dependencies, build output, and the graph itself. */
export const excludeGlob = '**/{node_modules,bin,obj,dist,out,build,target,.git,.codegraph,.venv,venv,vendor,.vscode-test}/**';

/** The kind of target a file is, by name (the same markers as Program.cs, TypeScriptExtractor and ScipIndexers). */
export function classify(file: string): TargetKind | undefined {
  const name = path.basename(file).toLowerCase();
  if (name.endsWith('.sln') || name.endsWith('.slnx') || name.endsWith('.csproj')) {
    return 'csharp';
  }
  if (name === 'tsconfig.json' || name === 'angular.json' || name === 'nx.json') {
    return 'typescript';
  }
  if (['pom.xml', 'build.gradle', 'build.gradle.kts', 'build.sbt'].includes(name)) {
    return 'java';
  }
  if (name === 'pyproject.toml' || name === 'setup.py') {
    return 'python';
  }
  if (name === 'go.mod' || name === 'go.work') {
    return 'go';
  }
  if (name === 'cargo.toml') {
    return 'rust';
  }
  return undefined;
}

/** The tool each kind needs at index time (install.sh, ScipIndexers.All). */
export const requirements: Record<TargetKind, Requirement> = {
  csharp: { tool: 'dotnet', install: 'Install the .NET SDK the solution targets', url: 'https://dotnet.microsoft.com/download' },
  typescript: { tool: 'node', install: 'Install Node.js 22.22.3+ or 24.15+', url: 'https://nodejs.org/' },
  java: { tool: 'scip-java', install: 'cs install --contrib scip-java', url: 'https://sourcegraph.github.io/scip-java/' },
  python: { tool: 'scip-python', install: 'npm install -g @sourcegraph/scip-python', url: 'https://github.com/sourcegraph/scip-python' },
  go: { tool: 'scip-go', install: 'go install github.com/scip-code/scip-go/cmd/scip-go@latest', url: 'https://github.com/scip-code/scip-go' },
  rust: { tool: 'rust-analyzer', install: 'rustup component add rust-analyzer', url: 'https://rust-analyzer.github.io/' },
};

/** File extensions whose save should re-index a target of each kind (ScipIndexers.All, TypeScriptExtractor). */
export const sourceExtensions: Record<TargetKind, string[]> = {
  csharp: ['.cs', '.csproj', '.props', '.targets'],
  typescript: ['.ts', '.tsx', '.js', '.jsx', '.mts', '.cts', '.mjs', '.cjs', '.html', '.json'],
  java: ['.java', '.kt', '.kts', '.scala', '.gradle', '.xml', '.sbt'],
  python: ['.py', '.toml', '.cfg'],
  go: ['.go', '.mod', '.work'],
  rust: ['.rs', '.toml'],
};

/**
 * Which targets to show, from every marker file found. A solution covers the projects it lists, so those .csproj
 * files are dropped (a project no solution lists, or one in another repository of the same folder, stays); a
 * tsconfig.json beside an angular.json or nx.json is the same workspace; nested markers of one SCIP language (a Maven
 * module inside a Maven build) are covered by the outermost one. `projectsOf` reads a solution's project paths.
 */
export function chooseTargets(files: string[], projectsOf: (solution: string) => string[] = readSolutionProjects): { file: string; kind: TargetKind }[] {
  const found = files
    .map((file) => ({ file, kind: classify(file) }))
    .filter((t): t is { file: string; kind: TargetKind } => t.kind !== undefined)
    .sort((a, b) => a.file.length - b.file.length || a.file.localeCompare(b.file));

  const inSolutions = new Set(found.filter((t) => /\.slnx?$/i.test(t.file)).flatMap((t) => projectsOf(t.file)).map(pathKey));
  const chosen: { file: string; kind: TargetKind }[] = [];
  for (const target of found) {
    const dir = path.dirname(target.file);
    if (target.kind === 'csharp' && /\.csproj$/i.test(target.file) && inSolutions.has(pathKey(target.file))) {
      continue;
    }
    if (target.kind === 'csharp') {
      chosen.push(target);
      continue;
    }
    // The outermost marker of a language wins; inner ones are part of it.
    const covered = chosen.some((c) => c.kind === target.kind && isInside(dir, path.dirname(c.file)));
    if (!covered) {
      chosen.push(target);
    }
  }
  return chosen.sort((a, b) => a.kind.localeCompare(b.kind) || a.file.localeCompare(b.file));
}

/** The project paths a .sln or .slnx lists, absolute; none when it cannot be read. */
export function readSolutionProjects(solution: string): string[] {
  try {
    return solutionProjects(solution, fs.readFileSync(solution, 'utf8'));
  } catch {
    return [];
  }
}

/** The .csproj paths in a solution's text, resolved against its folder: `Project(...) = "Name", "a\\B.csproj", ...` lines of a .sln, `<Project Path="a/B.csproj" />` of a .slnx. */
export function solutionProjects(solution: string, text: string): string[] {
  const pattern = /\.slnx$/i.test(solution) ? /<Project\b[^>]*\bPath\s*=\s*"([^"]+\.csproj)"/gi : /^\s*Project\([^)]*\)\s*=\s*"[^"]*"\s*,\s*"([^"]+\.csproj)"/gim;
  const dir = path.dirname(solution);
  return [...text.matchAll(pattern)].map((m) => path.resolve(dir, m[1].replace(/[\\/]/g, path.sep)));
}

/** The languages a kind's import writes rows in (the language column of the graph). */
export const kindLanguages: Record<TargetKind, string[]> = {
  csharp: ['csharp'],
  typescript: ['typescript', 'javascript'],
  java: ['java', 'kotlin', 'scala'],
  python: ['python'],
  go: ['go'],
  rust: ['rust'],
};

/**
 * The targets indexed into one graph: one per kind. Each import replaces every row of its languages, so two
 * TypeScript workspaces (or two C# solutions) indexed into one graph would leave only the last one's rows. Per kind
 * it is the target the graph already holds (`held`), else `preferred` (the daemon's), else a solution before a
 * project, else the first.
 */
export function indexedTargets<T extends { file: string; kind: TargetKind }>(targets: readonly T[], held: (kind: TargetKind) => string | undefined, preferred?: T): T[] {
  const chosen: T[] = [];
  for (const kind of [...new Set(targets.map((t) => t.kind))]) {
    const ofKind = targets.filter((t) => t.kind === kind);
    const heldFile = held(kind);
    const pick =
      (heldFile ? ofKind.find((t) => pathKey(t.file) === pathKey(heldFile)) : undefined) ??
      (preferred?.kind === kind ? ofKind.find((t) => pathKey(t.file) === pathKey(preferred.file)) : undefined) ??
      ofKind.find((t) => /\.slnx?$/i.test(t.file)) ??
      ofKind[0];
    chosen.push(pick);
  }
  return chosen;
}

/** A path as a comparison key: absolute, forward slashes, lower case where the file system ignores case. */
function pathKey(p: string): string {
  const key = path.resolve(p).replace(/\\/g, '/');
  return process.platform === 'linux' ? key : key.toLowerCase();
}

/** True when `child` is `parent` or below it. */
export function isInside(child: string, parent: string): boolean {
  const relative = path.relative(parent, child);
  return relative === '' || (!relative.startsWith('..') && !path.isAbsolute(relative));
}
