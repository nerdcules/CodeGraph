// Walks TypeScript programs with the type checker and records the graph: declarations as nodes, and calls,
// heritage, overrides, type references, imports, dependency injection and Angular template use as edges.
// The record shapes are CodeGraph's import contract (EXTRACTORS.md); ids are stable across runs because they
// come from file paths and declaration names, never from positions (tests and templates aside, see below).
import ts from 'typescript';
import fs from 'node:fs';
import path from 'node:path';
import { AngularIndex } from './angular.mjs';

const TEST_FILE = /(\.(spec|test)\.[cm]?[jt]sx?$)|([\\/]__tests__[\\/])/;
const TEST_CALLS = new Set(['it', 'test', 'fit', 'xit', 'ftest', 'xtest']);
const SUITE_CALLS = new Set(['describe', 'fdescribe', 'xdescribe', 'suite', 'context']);
const SETUP_CALLS = new Set(['beforeEach', 'beforeAll', 'afterEach', 'afterAll']);
const ANGULAR_DECORATORS = new Set(['Component', 'Directive', 'Pipe', 'Injectable', 'NgModule']);
const LIFECYCLE = new Set([
  'ngOnInit', 'ngOnChanges', 'ngDoCheck', 'ngAfterContentInit', 'ngAfterContentChecked',
  'ngAfterViewInit', 'ngAfterViewChecked', 'ngOnDestroy',
]);
const MAX_BODY_TERMS = 400;

const posix = p => p.split(path.sep).join('/');

function propertyName(name) {
  if (!name) return undefined;
  if (ts.isIdentifier(name) || ts.isPrivateIdentifier(name) || ts.isStringLiteral(name) || ts.isNumericLiteral(name)) return name.text;
  if (ts.isComputedPropertyName(name)) return `[${name.expression.getText()}]`;
  return undefined;
}

const isFunctionLike = n => n && (ts.isArrowFunction(n) || ts.isFunctionExpression(n));

function isTopLevelVariable(decl) {
  const statement = decl.parent?.parent;
  return ts.isVariableDeclaration(decl) && ts.isIdentifier(decl.name) && statement && ts.isVariableStatement(statement)
    && (ts.isSourceFile(statement.parent) || ts.isModuleBlock(statement.parent));
}

/** The node a declaration becomes, or undefined when it is not one (locals, parameters, object-literal members). */
function shapeOf(n) {
  switch (n.kind) {
    case ts.SyntaxKind.ClassDeclaration: return { kind: 'Type', typeKind: 'Class' };
    case ts.SyntaxKind.ClassExpression: return n.name && !ts.isVariableDeclaration(n.parent) ? { kind: 'Type', typeKind: 'Class' } : undefined;
    case ts.SyntaxKind.InterfaceDeclaration: return { kind: 'Type', typeKind: 'Interface' };
    case ts.SyntaxKind.EnumDeclaration: return { kind: 'Type', typeKind: 'Enum' };
    case ts.SyntaxKind.TypeAliasDeclaration: return { kind: 'Type', typeKind: 'TypeAlias' };
    case ts.SyntaxKind.ModuleDeclaration: return n.body && ts.isModuleBlock(n.body) && ts.isIdentifier(n.name) ? { kind: 'Type', typeKind: 'Module' } : undefined;
    case ts.SyntaxKind.FunctionDeclaration: return { kind: 'Method' };
    case ts.SyntaxKind.MethodDeclaration:
    case ts.SyntaxKind.MethodSignature: return ts.isObjectLiteralExpression(n.parent) ? undefined : { kind: 'Method' };
    case ts.SyntaxKind.Constructor: return { kind: 'Method', memberKind: 'Constructor' };
    case ts.SyntaxKind.GetAccessor:
    case ts.SyntaxKind.SetAccessor: return ts.isObjectLiteralExpression(n.parent) ? undefined : { kind: 'Property' };
    case ts.SyntaxKind.PropertySignature: return { kind: 'Property' };
    case ts.SyntaxKind.PropertyDeclaration: return isFunctionLike(n.initializer) ? { kind: 'Method' } : { kind: 'Field' };
    case ts.SyntaxKind.EnumMember: return { kind: 'Field' };
    case ts.SyntaxKind.VariableDeclaration:
      if (!isTopLevelVariable(n)) return undefined;
      if (isFunctionLike(n.initializer)) return { kind: 'Method' };
      if (n.initializer && ts.isClassExpression(n.initializer)) return { kind: 'Type', typeKind: 'Class' };
      return { kind: 'Field' };
    default: return undefined;
  }
}

/** The name a declaration contributes to its qualified name. */
function segmentOf(n) {
  switch (n.kind) {
    case ts.SyntaxKind.ClassDeclaration:
    case ts.SyntaxKind.FunctionDeclaration: return n.name?.text ?? 'default';
    case ts.SyntaxKind.ClassExpression: return ts.isVariableDeclaration(n.parent) ? undefined : n.name?.text;
    case ts.SyntaxKind.InterfaceDeclaration:
    case ts.SyntaxKind.EnumDeclaration:
    case ts.SyntaxKind.TypeAliasDeclaration:
    case ts.SyntaxKind.ModuleDeclaration: return n.name.text;
    case ts.SyntaxKind.Constructor: return 'constructor';
    case ts.SyntaxKind.MethodDeclaration:
    case ts.SyntaxKind.MethodSignature:
    case ts.SyntaxKind.GetAccessor:
    case ts.SyntaxKind.SetAccessor:
    case ts.SyntaxKind.PropertySignature:
    case ts.SyntaxKind.PropertyDeclaration:
    case ts.SyntaxKind.EnumMember: return ts.isObjectLiteralExpression(n.parent) ? undefined : propertyName(n.name);
    case ts.SyntaxKind.VariableDeclaration: return isTopLevelVariable(n) ? n.name.text : undefined;
    default: return undefined;
  }
}

function qualifiedName(decl) {
  const parts = [];
  for (let n = decl; n && !ts.isSourceFile(n); n = n.parent) {
    const segment = segmentOf(n);
    if (segment !== undefined) parts.unshift(segment);
  }
  return parts.join('.');
}

/** From a resolved declaration to the declaration that is a node: an arrow function to the property holding it. */
function nodeDeclaration(decl) {
  if (!decl) return undefined;
  if (isFunctionLike(decl) && decl.parent && (ts.isPropertyDeclaration(decl.parent) || ts.isVariableDeclaration(decl.parent))) decl = decl.parent;
  if (ts.isClassExpression(decl) && ts.isVariableDeclaration(decl.parent)) decl = decl.parent;
  return shapeOf(decl) ? decl : undefined;
}

function packageOf(fileName) {
  const parts = posix(fileName).split('/');
  const i = parts.lastIndexOf('node_modules');
  if (i < 0) return undefined;
  const first = parts[i + 1];
  return first?.startsWith('@') ? `${first}/${parts[i + 2]}` : first;
}

function languageOf(fileName) {
  return /\.[cm]?jsx?$/.test(fileName) ? 'javascript' : 'typescript';
}

function collapse(text, max = 240) {
  const one = text.replace(/\s+/g, ' ').trim();
  return one.length > max ? `${one.slice(0, max - 1)}…` : one;
}

export class Extractor {
  /** @param {{ root: string, log: (m: string) => void }} options */
  constructor({ root, log }) {
    this.root = root;
    this.log = log;
    this.nodes = new Map();
    this.externals = new Map();
    this.edges = new Map();
    this.files = new Set();
    this.walked = new Set();
    this.terms = new Map();
    this.texts = new Map();
    this.angular = new AngularIndex(this);
  }

  rel(fileName) { return posix(path.relative(this.root, fileName)); }

  inSource(sf) {
    if (sf.isDeclarationFile || /[\\/]node_modules[\\/]/.test(sf.fileName)) return false;
    const rel = path.relative(this.root, sf.fileName);
    return !rel.startsWith('..') && !path.isAbsolute(rel);
  }

  addNode(record) {
    const existing = this.nodes.get(record.id);
    if (existing) {
      // get and set accessors, overloads and merged declarations are one symbol: widen the span, keep the first.
      existing.start = Math.min(existing.start || record.start, record.start || existing.start);
      existing.end = Math.max(existing.end, record.end);
      return existing;
    }
    this.nodes.set(record.id, { type: 'node', ...record });
    return record;
  }

  addEdge(from, to, kind, file) {
    if (!from || !to || from === to) return;
    if (kind === 'CallsExternal' && !this.nodes.has(to) && this.externals.has(to)) this.addNode(this.externals.get(to));
    const key = `${from}\0${to}\0${kind}\0${file}`;
    if (!this.edges.has(key)) this.edges.set(key, { type: 'edge', from, to, kind, file });
  }

  /** Adds words a node's body mentions, for full-text search. */
  addTerms(owner, text) {
    if (!owner || !text) return;
    let list = this.terms.get(owner);
    if (!list) this.terms.set(owner, list = []);
    if (list.length < MAX_BODY_TERMS) list.push(text);
  }

  idOf(decl) {
    const sf = decl.getSourceFile();
    return `ts:${this.rel(sf.fileName)}#${qualifiedName(decl)}`;
  }

  /** The node id a resolved declaration stands for: a source node, an External node (created), or undefined. */
  targetOf(rawDecl, program) {
    const decl = nodeDeclaration(rawDecl);
    if (!decl) return undefined;
    const sf = decl.getSourceFile();
    if (this.inSource(sf)) return { id: this.idOf(decl), external: false, decl };
    // lib.*.d.ts sits inside node_modules/typescript, but it is the language's standard library, not a package.
    const module = program.isSourceFileDefaultLibrary(sf) ? 'lib' : packageOf(sf.fileName) ?? this.rel(sf.fileName);
    const qualified = qualifiedName(decl);
    const id = `ts:ext:${module}#${qualified}`;
    // Created only when a call reaches it (addEdge): a type merely mentioned, like Promise, is not a node.
    if (!this.externals.has(id)) this.externals.set(id, { id, kind: 'External', name: qualified.split('.').pop() || qualified, fullName: `${module}.${qualified}`, language: languageOf(sf.fileName) });
    return { id, external: true, decl };
  }

  symbolAt(checker, node) {
    let symbol = checker.getSymbolAtLocation(node);
    if (symbol && symbol.flags & ts.SymbolFlags.Alias) {
      try { symbol = checker.getAliasedSymbol(symbol); } catch { /* unresolved import */ }
    }
    return symbol;
  }

  /** Runs one tsconfig's program; files another config already walked are skipped. */
  runConfig({ name, config }) {
    const host = { ...ts.sys, onUnRecoverableConfigFileDiagnostic: d => this.log(ts.flattenDiagnosticMessageText(d.messageText, '\n')) };
    const parsed = ts.getParsedCommandLineOfConfigFile(config, { noEmit: true }, host);
    if (!parsed) return;
    const compilerHost = ts.createCompilerHost(parsed.options, true);
    // Referenced projects resolve to their sources, not to (possibly unbuilt) declaration output.
    compilerHost.useSourceOfProjectReferenceRedirect = () => true;
    const program = ts.createProgram({ rootNames: parsed.fileNames, options: parsed.options, projectReferences: parsed.projectReferences, host: compilerHost });
    const checker = program.getTypeChecker();

    const projectId = `ts:project:${this.rel(config)}`;
    const frameworkId = `ts:framework:${this.rel(config)}`;
    const language = parsed.fileNames.every(f => languageOf(f) === 'javascript') ? 'javascript' : 'typescript';
    this.addNode({ id: projectId, kind: 'Project', name, fullName: this.rel(config), file: config, language });
    this.addNode({ id: frameworkId, kind: 'Project', name: 'framework', fullName: `${name}.framework`, file: config, language });
    this.addEdge(projectId, frameworkId, 'Contains', config);
    this.files.add(config);

    // Every workspace file the program holds, not only its roots: an Angular CLI app's tsconfig lists just
    // src/main.ts and reaches the rest through imports.
    let count = 0;
    for (const sf of program.getSourceFiles()) {
      const file = path.resolve(sf.fileName);
      if (this.walked.has(file) || !this.inSource(sf)) continue;
      this.walked.add(file);
      this.walkFile(sf, program, checker, projectId, frameworkId);
      count++;
    }
    this.log(`${name}: ${count} file(s) from ${this.rel(config)}`);
  }

  walkFile(sf, program, checker, projectId, frameworkId) {
    const file = path.resolve(sf.fileName);
    const rel = this.rel(file);
    const language = languageOf(file);
    const fileId = `ts:file:${rel}`;
    this.files.add(file);
    this.addNode({ id: fileId, kind: 'File', name: path.basename(file), fullName: rel, file, start: 1, end: sf.getLineAndCharacterOfPosition(sf.end).line + 1, language });
    this.addEdge(projectId, fileId, 'Contains', file);

    const isTestFile = TEST_FILE.test(file);
    const testIds = new Set();
    const line = pos => sf.getLineAndCharacterOfPosition(pos).line + 1;

    const declare = (decl, shape, container) => {
      const id = this.idOf(decl);
      const qualified = qualifiedName(decl);
      const flags = ts.getCombinedModifierFlags(decl);
      const topLevel = !decl.parent || ts.isSourceFile(decl.parent) || ts.isModuleBlock(decl.parent)
        || (ts.isVariableDeclaration(decl) && isTopLevelVariable(decl));
      const exported = ts.isVariableDeclaration(decl)
        ? !!(ts.getCombinedModifierFlags(decl.parent.parent) & ts.ModifierFlags.Export) || !!(flags & ts.ModifierFlags.Export)
        : !!(flags & ts.ModifierFlags.Export);
      const accessibility = decl.name && ts.isPrivateIdentifier(decl.name) ? 'Private'
        : flags & ts.ModifierFlags.Private ? 'Private'
        : flags & ts.ModifierFlags.Protected ? 'Protected'
        : topLevel ? (exported ? 'Public' : 'Internal')
        : 'Public';
      const bodyStart = decl.body ? decl.body.getStart(sf)
        : decl.members ? decl.members.pos
        : decl.initializer && isFunctionLike(decl.initializer) && decl.initializer.body ? decl.initializer.body.getStart(sf)
        : decl.end;
      const signature = collapse(sf.text.slice(decl.getStart(sf), bodyStart));
      const symbol = decl.name ? checker.getSymbolAtLocation(decl.name) : undefined;
      const doc = symbol ? ts.displayPartsToString(symbol.getDocumentationComment(checker)) : '';
      const isAsync = !!(flags & ts.ModifierFlags.Async) || !!(decl.initializer && isFunctionLike(decl.initializer) && ts.getCombinedModifierFlags(decl.initializer) & ts.ModifierFlags.Async);
      this.addNode({
        id, kind: shape.kind, typeKind: shape.typeKind, memberKind: shape.memberKind,
        name: qualified.split('.').pop() || qualified,
        fullName: `${rel.replace(/\.[cm]?[jt]sx?$/, '')}:${qualified}`,
        signature, file, start: line(decl.getStart(sf)), end: line(decl.end), accessibility,
        static: !!(flags & ts.ModifierFlags.Static), abstract: !!(flags & ts.ModifierFlags.Abstract) || ts.isMethodSignature(decl) || ts.isPropertySignature(decl),
        async: isAsync, override: !!(flags & ts.ModifierFlags.Override), language,
      });
      this.addEdge(container, id, 'Contains', file);
      this.texts.set(id, `${shape.kind} ${qualified}\n${signature}\n${doc}\n${collapse(sf.text.slice(decl.getStart(sf), decl.end), 1500)}`);
      this.addTerms(id, doc);
      return id;
    };

    const heritage = (decl, typeId, isClass) => {
      for (const clause of decl.heritageClauses ?? []) {
        const kind = clause.token === ts.SyntaxKind.ExtendsKeyword ? 'Inherits' : 'Implements';
        for (const type of clause.types) {
          const symbol = this.symbolAt(checker, type.expression);
          const target = symbol?.declarations?.[0] && this.targetOf(symbol.declarations[0], program);
          if (target && !target.external) this.addEdge(typeId, target.id, kind, file);
          if (!isClass) continue;
          // Member level: what each member overrides or implements, by name on the base or interface type.
          const baseType = checker.getTypeAtLocation(type);
          for (const member of decl.members) {
            const memberName = propertyName(member.name);
            if (!memberName || !shapeOf(member) || ts.isConstructorDeclaration(member)) continue;
            const baseMember = checker.getPropertyOfType(baseType, memberName)?.declarations?.[0];
            const baseTarget = baseMember && this.targetOf(baseMember, program);
            if (!baseTarget || baseTarget.external) continue;
            const memberId = this.idOf(member);
            this.addEdge(memberId, baseTarget.id, kind === 'Inherits' ? 'Overrides' : 'Implements', file);
            if (kind === 'Inherits' && this.nodes.get(memberId)) this.nodes.get(memberId).override = true;
          }
        }
      }
    };

    const decoratorName = d => {
      const expression = ts.isCallExpression(d.expression) ? d.expression.expression : d.expression;
      return ts.isIdentifier(expression) ? expression.text : ts.isPropertyAccessExpression(expression) ? expression.name.text : undefined;
    };

    const classAnnotations = (decl, typeId) => {
      const decorators = ts.canHaveDecorators(decl) ? ts.getDecorators(decl) ?? [] : [];
      const angular = decorators.find(d => ANGULAR_DECORATORS.has(decoratorName(d)));
      if (angular) this.angular.register(decl, decoratorName(angular), angular, typeId, sf, file);
      if (decorators.length === 0) return;
      for (const member of decl.members) {
        const memberName = propertyName(member.name);
        const memberDecorators = ts.canHaveDecorators(member) ? ts.getDecorators(member) ?? [] : [];
        const invoked = (angular && memberName && LIFECYCLE.has(memberName)) || (memberDecorators.length > 0 && ts.isMethodDeclaration(member));
        if (invoked && shapeOf(member)) this.addEdge(frameworkId, this.idOf(member), 'InvokedByFramework', file);
        if (ts.isConstructorDeclaration(member)) {
          // Constructor injection: every parameter whose type is a class in the source.
          for (const parameter of member.parameters) {
            if (!parameter.type || !ts.isTypeReferenceNode(parameter.type)) continue;
            const symbol = this.symbolAt(checker, parameter.type.typeName);
            const target = symbol?.declarations?.[0] && this.targetOf(symbol.declarations[0], program);
            if (target && !target.external) this.addEdge(typeId, target.id, 'Injects', file);
          }
        }
      }
    };

    const resolveCall = (node, ctx) => {
      const signature = checker.getResolvedSignature(node);
      let decl = signature?.declaration;
      if (!decl || ts.isJSDocSignature?.(decl)) {
        const callee = ts.isCallExpression(node) ? node.expression : node.expression;
        const symbol = this.symbolAt(checker, ts.isPropertyAccessExpression(callee) ? callee.name : callee);
        decl = symbol?.valueDeclaration ?? symbol?.declarations?.[0];
      }
      if (!decl) return;
      const isSuper = ts.isCallExpression(node) && node.expression.kind === ts.SyntaxKind.SuperKeyword;
      if ((ts.isNewExpression(node) || isSuper) && !ts.isConstructorDeclaration(decl)) {
        // An implicit constructor: the edge goes to a node-less <Class>.constructor, as C#'s does.
        const classSymbol = isSuper ? checker.getTypeAtLocation(node.expression).getSymbol() : this.symbolAt(checker, node.expression);
        const classDecl = classSymbol?.declarations?.find(d => ts.isClassDeclaration(d) || ts.isClassExpression(d) || ts.isVariableDeclaration(d));
        const target = classDecl && this.targetOf(classDecl, program);
        if (!target) return;
        if (target.external) {
          this.addEdge(ctx.owner, target.id, 'CallsExternal', file);
          return;
        }
        this.addEdge(ctx.owner, `${target.id}.constructor`, 'Calls', file);
        this.addEdge(ctx.owner, target.id, 'References', file);
        if (ctx.test) this.addEdge(target.id, ctx.owner, 'TestedBy', file);
        return;
      }
      const target = this.targetOf(decl, program);
      if (!target) return;
      if (target.external) {
        this.addEdge(ctx.owner, target.id, 'CallsExternal', file);
        return;
      }
      this.addEdge(ctx.owner, target.id, 'Calls', file);
      if (ctx.test) this.addEdge(target.id, ctx.owner, 'TestedBy', file);
    };

    /** A use of a symbol that is not a call: a property read is a call, as in C#; a type name is a reference. */
    const resolveUse = (nameNode, ctx) => {
      const symbol = this.symbolAt(checker, nameNode);
      const decl = symbol?.valueDeclaration ?? symbol?.declarations?.[0];
      if (!decl || ts.isEnumMember(decl)) return;
      const target = this.targetOf(decl, program);
      if (!target || target.external) return;
      const kind = shapeOf(target.decl)?.kind;
      if (kind === 'Type') {
        this.addEdge(ctx.owner, target.id, 'References', file);
        if (ctx.test) this.addEdge(target.id, ctx.owner, 'TestedBy', file);
      } else if (kind) {
        this.addEdge(ctx.owner, target.id, 'Calls', file);
        if (ctx.test) this.addEdge(target.id, ctx.owner, 'TestedBy', file);
      }
    };

    const testName = (call) => {
      const callee = call.expression;
      const base = ts.isIdentifier(callee) ? callee.text
        : ts.isPropertyAccessExpression(callee) && ts.isIdentifier(callee.expression) && ['only', 'skip', 'todo', 'concurrent'].includes(callee.name.text) ? callee.expression.text
        : undefined;
      const [title, body] = call.arguments;
      if (!base || !title || !(ts.isStringLiteral(title) || ts.isNoSubstitutionTemplateLiteral(title) || ts.isTemplateExpression(title))) return undefined;
      if (!body || !(isFunctionLike(body))) return undefined;
      const kind = TEST_CALLS.has(base) ? 'test' : SUITE_CALLS.has(base) ? 'suite' : undefined;
      return kind && { kind, title: ts.isTemplateExpression(title) ? title.getText(sf).slice(1, -1) : title.text, body };
    };

    const visit = (node, ctx) => {
      // Declarations that are nodes: their own id owns what their body does.
      const shape = shapeOf(node);
      if (shape) {
        const id = declare(node, shape, ctx.container);
        if (shape.kind === 'Type') {
          if (ts.isClassDeclaration(node) || ts.isClassExpression(node) || ts.isInterfaceDeclaration(node)) heritage(node, id, !ts.isInterfaceDeclaration(node));
          if (ts.isClassDeclaration(node) || ts.isClassExpression(node)) classAnnotations(node, id);
          if (ts.isVariableDeclaration(node) && node.initializer && ts.isClassExpression(node.initializer)) {
            heritage(node.initializer, id, true);
            classAnnotations(node.initializer, id);
            ts.forEachChild(node.initializer, child => visit(child, { ...ctx, owner: id, container: id, classId: id }));
            return;
          }
          ts.forEachChild(node, child => visit(child, { ...ctx, owner: id, container: id, classId: ts.isModuleDeclaration(node) ? ctx.classId : id }));
          return;
        }
        ts.forEachChild(node, child => visit(child, { ...ctx, owner: id, container: ctx.container }));
        return;
      }

      if (ts.isImportDeclaration(node) || ts.isExportDeclaration(node)) {
        const specifier = node.moduleSpecifier;
        const target = specifier && this.symbolAt(checker, specifier)?.valueDeclaration;
        if (target && ts.isSourceFile(target) && this.inSource(target)) this.addEdge(fileId, `ts:file:${this.rel(path.resolve(target.fileName))}`, 'Imports', file);
        return;
      }

      if (ts.isCallExpression(node)) {
        if (node.expression.kind === ts.SyntaxKind.ImportKeyword && node.arguments[0]) {
          const target = this.symbolAt(checker, node.arguments[0])?.valueDeclaration;
          if (target && ts.isSourceFile(target) && this.inSource(target)) this.addEdge(fileId, `ts:file:${this.rel(path.resolve(target.fileName))}`, 'Imports', file);
        }
        const test = isTestFile ? testName(node) : undefined;
        if (test) {
          const suite = [...ctx.suite, test.title];
          let id = `ts:${rel}#${suite.join(' > ')}`;
          if (testIds.has(id)) id += `@${line(node.getStart(sf))}`;
          testIds.add(id);
          this.addNode({
            id, kind: 'Method', name: test.title, fullName: `${rel.replace(/\.[cm]?[jt]sx?$/, '')}:${suite.join(' > ')}`,
            signature: collapse(node.getText(sf).split('\n')[0]), file, start: line(node.getStart(sf)), end: line(node.end),
            isTest: true, accessibility: 'Public', language,
          });
          this.addEdge(ctx.suiteId ?? fileId, id, 'Contains', file);
          this.addTerms(id, test.title);
          const inner = { ...ctx, owner: id, test: true, suite: test.kind === 'suite' ? suite : ctx.suite, suiteId: test.kind === 'suite' ? id : ctx.suiteId };
          for (const argument of node.arguments.slice(1)) visit(argument, inner);
          return;
        }
        if (ctx.test && ts.isIdentifier(node.expression) && SETUP_CALLS.has(node.expression.text)) {
          for (const argument of node.arguments) visit(argument, ctx);
          return;
        }
        if (ts.isIdentifier(node.expression) && node.expression.text === 'inject' && ctx.classId && node.arguments[0]) {
          const symbol = this.symbolAt(checker, node.arguments[0]);
          const target = symbol?.declarations?.[0] && this.targetOf(symbol.declarations[0], program);
          if (target && !target.external) this.addEdge(ctx.classId, target.id, 'Injects', file);
        }
        resolveCall(node, ctx);
      } else if (ts.isNewExpression(node)) {
        resolveCall(node, ctx);
      } else if (ts.isPropertyAccessExpression(node)) {
        const parent = node.parent;
        const isCallee = (ts.isCallExpression(parent) || ts.isNewExpression(parent)) && parent.expression === node;
        if (!isCallee) resolveUse(node.name, ctx);
        this.addTerms(ctx.owner, node.name.text);
      } else if (ts.isIdentifier(node)) {
        const parent = node.parent;
        const isName = (parent && 'name' in parent && parent.name === node && !ts.isPropertyAccessExpression(parent))
          || ts.isPropertyAccessExpression(parent) && parent.name === node
          || ts.isImportSpecifier(parent) || ts.isExportSpecifier(parent) || ts.isImportClause(parent) || ts.isNamespaceImport(parent)
          || ts.isLabeledStatement(parent) || ts.isBreakOrContinueStatement(parent) || ts.isTypeReferenceNode(parent) || ts.isQualifiedName(parent);
        const isCallee = (ts.isCallExpression(parent) || ts.isNewExpression(parent)) && parent.expression === node;
        if (!isName && !isCallee) resolveUse(node, ctx);
        this.addTerms(ctx.owner, node.text);
      } else if (ts.isTypeReferenceNode(node)) {
        const nameNode = ts.isQualifiedName(node.typeName) ? node.typeName.right : node.typeName;
        const symbol = this.symbolAt(checker, nameNode);
        const decl = symbol?.declarations?.[0];
        const target = decl && this.targetOf(decl, program);
        if (target && !target.external) this.addEdge(ctx.owner, target.id, 'References', file);
      } else if (ts.isStringLiteralLike(node)) {
        this.addTerms(ctx.owner, node.text);
      } else if (ts.isObjectLiteralExpression(node)) {
        // Angular routes: { path, component: X } or loadComponent: () => import(...).then(m => m.X).
        this.angular.route(node, ctx.owner, checker, program, file);
      }
      ts.forEachChild(node, child => visit(child, ctx));
    };

    visit(sf, { owner: fileId, container: fileId, test: false, suite: [], suiteId: undefined, classId: undefined, frameworkId });
  }

  /** Everything as records, header first. */
  *records({ target, tool }) {
    this.angular.resolveTemplates();
    // Paths are relative to the root the header names, which keeps a large workspace's records small.
    yield { type: 'header', language: 'typescript', tool, root: this.root, target };
    for (const file of this.files) yield { type: 'file', path: this.rel(file) };
    for (const node of this.nodes.values()) {
      if (!node.language) node.language = 'typescript';
      yield node.file ? { ...node, file: this.rel(node.file) } : node;
    }
    for (const edge of this.edges.values()) yield { ...edge, file: this.rel(edge.file) };
    for (const [id, node] of this.nodes) {
      if (node.kind === 'External' || node.kind === 'Project') continue;
      const parts = [{ text: node.name, weight: 3 }, { text: node.fullName, weight: 1 }];
      if (node.signature) parts.push({ text: node.signature, weight: 2 });
      const body = this.terms.get(id);
      if (body) parts.push({ text: body.join(' '), weight: 1 });
      yield { type: 'search', node: id, parts };
      const text = this.texts.get(id);
      if (text) yield { type: 'embed', node: id, text };
    }
    yield { type: 'meta', key: 'files', value: String(this.walked.size) };
  }
}

export { languageOf, qualifiedName };
export const readText = file => { try { return fs.readFileSync(file, 'utf8'); } catch { return undefined; } };
