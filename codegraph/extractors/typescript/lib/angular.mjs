// Angular: components, directives and pipes by their decorators; templates parsed with @angular/compiler,
// matched against every selector in the workspace (Renders edges) and read for the component members they use
// (Calls edges from a synthetic <template> node, which the framework invokes). Routes point at components.
import ts from 'typescript';
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';

let compiler;
function ng() {
  if (compiler === undefined) {
    try {
      compiler = createRequire(import.meta.url)('@angular/compiler');
    } catch {
      compiler = null;
    }
  }
  return compiler;
}

/** The @angular/compiler version templates are parsed with, or undefined when it is not installed. */
export function angularCompilerVersion() {
  try {
    return createRequire(import.meta.url)('@angular/compiler/package.json').version;
  } catch {
    return undefined;
  }
}

const literalText = n => n && (ts.isStringLiteral(n) || ts.isNoSubstitutionTemplateLiteral(n)) ? n.text : undefined;

function objectArgument(decorator) {
  const call = decorator.expression;
  const argument = ts.isCallExpression(call) ? call.arguments[0] : undefined;
  return argument && ts.isObjectLiteralExpression(argument) ? argument : undefined;
}

function property(object, name) {
  const found = object?.properties.find(p => ts.isPropertyAssignment(p) && (p.name.text ?? p.name.getText()) === name);
  return found?.initializer;
}

const typeName = n => n?.constructor?.name ?? '';

export class AngularIndex {
  constructor(extractor) {
    this.x = extractor;
    this.components = [];
    this.selectors = [];   // { classId, selector parsed }
    this.pipes = new Map(); // pipe name -> class id
  }

  register(decl, kind, decorator, classId, sf, file) {
    const options = objectArgument(decorator);
    const selector = literalText(property(options, 'selector'));
    if (selector && (kind === 'Component' || kind === 'Directive') && ng()) {
      try {
        for (const parsed of ng().CssSelector.parse(selector)) this.selectors.push({ classId, parsed });
      } catch { /* an unparsable selector matches nothing */ }
    }
    if (kind === 'Pipe') {
      const name = literalText(property(options, 'name'));
      if (name) this.pipes.set(name, classId);
    }
    if (kind !== 'Component') return;
    const inline = property(options, 'template');
    const url = literalText(property(options, 'templateUrl'));
    const className = decl.name?.text ?? 'default';
    if (inline && literalText(inline) !== undefined) {
      this.components.push({ classId, className, file, text: literalText(inline), templateFile: file, line: sf.getLineAndCharacterOfPosition(inline.getStart(sf)).line + 1 });
    } else if (url) {
      const templateFile = path.resolve(path.dirname(file), url);
      try {
        this.components.push({ classId, className, file, text: fs.readFileSync(templateFile, 'utf8'), templateFile, line: 1 });
        this.x.files.add(templateFile);
      } catch {
        this.x.log(`angular: template not found: ${templateFile}`);
      }
    }
  }

  /** `{ path: 'users', component: UsersComponent }` and `loadComponent: () => import('./x').then(m => m.X)`. */
  route(object, owner, checker, program, file) {
    if (!property(object, 'path') && !property(object, 'component') && !property(object, 'loadComponent')) return;
    const add = expression => {
      const symbol = expression && this.x.symbolAt(checker, expression);
      const decl = symbol?.declarations?.[0];
      const target = decl && this.x.targetOf(decl, program);
      if (target && !target.external) this.x.addEdge(owner, target.id, 'Renders', file);
    };
    const component = property(object, 'component');
    if (component && ts.isIdentifier(component)) add(component);
    const lazy = property(object, 'loadComponent') ?? property(object, 'loadChildren');
    if (lazy) {
      const visit = n => {
        if (ts.isPropertyAccessExpression(n) && ts.isIdentifier(n.expression)) add(n.name);
        ts.forEachChild(n, visit);
      };
      visit(lazy);
    }
  }

  matches(element) {
    const names = new Set(element.attrs);
    return this.selectors.filter(({ parsed }) => {
      if (parsed.element && parsed.element !== '*' && parsed.element !== element.tag) return false;
      if (!parsed.element && parsed.attrs.length === 0) return false;
      for (let i = 0; i < parsed.attrs.length; i += 2) {
        if (!names.has(parsed.attrs[i])) return false;
      }
      for (const not of parsed.notSelectors ?? []) {
        if (not.element === element.tag) return false;
      }
      return true;
    });
  }

  resolveTemplates() {
    if (!ng()) {
      if (this.components.length) this.x.log('angular: @angular/compiler not available; templates not read');
      return;
    }
    for (const component of this.components) {
      let parsed;
      try {
        parsed = ng().parseTemplate(component.text, component.templateFile, { preserveWhitespaces: false });
      } catch (e) {
        this.x.log(`angular: ${component.templateFile}: ${e.message}`);
        continue;
      }
      const templateId = `${component.classId}.<template>`;
      const prefix = component.classId; // members are ts:<rel>#<Class>.<member>
      const origin = component.templateFile;
      this.x.addNode({
        id: templateId, kind: 'Method', name: '<template>', fullName: `${this.x.nodes.get(component.classId)?.fullName ?? component.className}.<template>`,
        file: component.templateFile, start: component.line, end: component.line + component.text.split('\n').length - 1,
        accessibility: 'Private', language: 'typescript',
      });
      this.x.addEdge(component.classId, templateId, 'Contains', origin);
      // The framework renders the template, so what it calls is used even if no TypeScript calls it.
      const framework = this.frameworkFor(component.file);
      if (framework) this.x.addEdge(framework, templateId, 'InvokedByFramework', origin);

      const words = [];
      const seen = new Set();
      const walk = node => {
        if (!node || typeof node !== 'object' || seen.has(node)) return;
        seen.add(node);
        if (Array.isArray(node)) {
          node.forEach(walk);
          return;
        }
        const type = typeName(node);
        // Elements and <ng-template>: match selectors on the tag and every attribute, input, output and template attribute.
        if ((typeof node.name === 'string' || typeof node.tagName === 'string') && Array.isArray(node.children) && Array.isArray(node.attributes)) {
          const attrs = [...node.attributes, ...(node.inputs ?? []), ...(node.outputs ?? []), ...(node.templateAttrs ?? [])].map(a => a.name);
          for (const { classId } of this.matches({ tag: node.tagName ?? node.name, attrs })) {
            if (classId !== component.classId) this.x.addEdge(component.classId, classId, 'Renders', origin);
          }
        }
        if (type === 'BindingPipe' && typeof node.name === 'string') {
          const pipe = this.pipes.get(node.name);
          if (pipe) this.x.addEdge(component.classId, pipe, 'Renders', origin);
        }
        // `name` or `name(...)` on the component itself (an implicit or `this` receiver).
        if ((type === 'PropertyRead' || type === 'SafePropertyRead') && /ImplicitReceiver|ThisReceiver/.test(typeName(node.receiver))) {
          const member = `${prefix}.${node.name}`;
          if (this.x.nodes.has(member)) this.x.addEdge(templateId, member, 'Calls', origin);
          words.push(node.name);
        }
        for (const [key, value] of Object.entries(node)) {
          if (key === 'sourceSpan' || key === 'span' || key === 'nameSpan' || key === 'keySpan' || key === 'valueSpan'
            || key === 'startSourceSpan' || key === 'endSourceSpan' || key === 'handlerSpan' || key === 'i18n') continue;
          if (value && typeof value === 'object') walk(value);
        }
      };
      walk(parsed.nodes);
      this.x.addTerms(templateId, words.join(' '));
    }
  }

  frameworkFor(file) {
    // The framework root of the project whose Contains edge reaches the component's file.
    const fileId = `ts:file:${this.x.rel(file)}`;
    for (const edge of this.x.edges.values()) {
      if (edge.kind === 'Contains' && edge.to === fileId && edge.from.startsWith('ts:project:')) {
        return edge.from.replace('ts:project:', 'ts:framework:');
      }
    }
    return undefined;
  }
}
