// End-to-end test of the CodeGraph plugin inside a real Obsidian, driven over the DevTools protocol.
//
//   node e2e.mjs --port 9222 --vault <vault> --repo <corpus repo> --lang csharp --out /results/<name>
//
// Each step is recorded with its outcome; a failed step does not stop the ones that do not depend on it.
// Writes <out>.json and screenshots <out>-<step>.png.

import { writeFileSync, appendFileSync, readdirSync, statSync } from 'node:fs';
import { execFileSync, spawnSync } from 'node:child_process';
import { join } from 'node:path';
import { connect, sleep } from './cdp.mjs';

const args = {};
for (let i = 2; i < process.argv.length; i += 2) {
  args[process.argv[i].replace(/^--/, '')] = process.argv[i + 1];
}

const steps = [];
const result = { obsidian: process.env.OBSIDIAN_VERSION, language: args.lang, steps };
let page;

async function step(name, fn) {
  const started = Date.now();
  try {
    // A step that failed may have left its search box open; it must not take the next step down with it.
    if (page) {
      await page.evaluate(`
        for (let i = 0; i < 3 && document.querySelector('.modal-container'); i++) {
          (document.activeElement || document.body).dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', code: 'Escape', bubbles: true }));
          await new Promise((r) => setTimeout(r, 200));
        }
        return true;
      `).catch(() => {});
    }
    const detail = await fn();
    steps.push({ name, ok: true, ms: Date.now() - started, detail });
    console.log(`  ok    ${name}${detail && detail.summary ? ': ' + detail.summary : ''}`);
    return detail;
  } catch (error) {
    steps.push({ name, ok: false, ms: Date.now() - started, error: String(error.message || error).slice(0, 1500) });
    console.log(`  FAIL  ${name}: ${String(error.message || error).slice(0, 300)}`);
    return null;
  }
}

async function shot(label) {
  try {
    writeFileSync(`${args.out}-${label}.png`, await page.screenshot());
  } catch {
    // a screenshot is evidence, not a test
  }
}

function sourceFiles(dir, ext, found = []) {
  for (const entry of readdirSync(dir).sort()) {
    const full = join(dir, entry);
    if (entry.startsWith('.') || entry === 'node_modules' || entry === 'bin' || entry === 'obj') continue;
    if (statSync(full).isDirectory()) sourceFiles(full, ext, found);
    else if (full.endsWith(ext) && !full.endsWith('.d.ts')) found.push(full); // declaration files are not indexed
  }

  return found;
}

const EXT = { csharp: '.cs', typescript: '.ts', javascript: '.js', angular: '.ts', java: '.java', go: '.go', rust: '.rs', python: '.py' };
const ADDED = {
  csharp: '\nnamespace Corpus.Added { public class AddedByE2e { public void Ping() { } } }\n',
  typescript: '\nexport class AddedByE2e { ping(): void { } }\n',
};

async function main() {
  page = await connect(Number(args.port), { timeoutMs: 90000 });

  await step('Obsidian opens the vault', () => page.waitFor(
    'return window.app && app.workspace && app.workspace.layoutReady && app.vault.getName ? { summary: app.vault.getName() + ", " + (window.require ? "desktop" : "?") } : null',
    { timeoutMs: 90000, label: 'the workspace' }));
  result.userAgent = await page.evaluate('return navigator.userAgent').catch(() => null);

  await step('the plugin loads', () => page.evaluate(`
    const p = app.plugins;
    if (typeof p.isEnabled === 'function' && !p.isEnabled()) await p.setEnable(true);
    await p.loadManifests();
    if (!p.plugins.codegraph) await p.enablePluginAndSave('codegraph');
    document.querySelectorAll('.modal-container .modal-close-button').forEach((b) => b.click());
    const plugin = p.plugins.codegraph;
    if (!plugin) throw new Error('codegraph is not loaded; manifests: ' + Object.keys(p.manifests).join(','));
    for (let i = 0; i < 40 && !plugin.launch; i++) await new Promise((r) => setTimeout(r, 250));
    if (!plugin.launch) throw new Error('the plugin did not configure itself from data.json');
    return { summary: 'mcp: ' + plugin.launch.mcp.command + ' ' + plugin.launch.mcp.args.join(' ') + '; index: ' + plugin.launch.index.command };
  `));

  const notes = await step('Rebuild code notes writes the vault and Obsidian indexes it', async () => {
    await page.evaluate(`app.commands.executeCommandById('codegraph:rebuild-notes'); return true;`);
    return page.waitFor(`
      const plugin = app.plugins.plugins.codegraph;
      if (!app.vault.getAbstractFileByPath('_code/Index.md') || plugin.busy) return null;
      const manifest = JSON.parse(await app.vault.adapter.read('_code/.codegraph-export.json'));
      const files = app.vault.getMarkdownFiles().filter((f) => f.path.startsWith('_code/'));
      if (files.length < manifest.files.length || !files.every((f) => app.metadataCache.getFileCache(f))) return null;
      return { notes: files.length, listed: manifest.files.length, summary: files.length + ' notes indexed' };
    `, { timeoutMs: 180000, label: 'the export and the metadata cache' });
  });

  await step("every link resolves with Obsidian's own resolver", async () => {
    const r = await page.evaluate(`
      const files = app.vault.getMarkdownFiles().filter((f) => f.path.startsWith('_code/'));
      let links = 0; const unresolved = []; const badHeading = [];
      for (const f of files) {
        for (const l of app.metadataCache.getFileCache(f).links || []) {
          links++;
          const hash = l.link.indexOf('#');
          const path = hash < 0 ? l.link : l.link.slice(0, hash);
          const heading = hash < 0 ? null : l.link.slice(hash + 1);
          const dest = app.metadataCache.getFirstLinkpathDest(path, f.path);
          if (!dest) { unresolved.push(f.path + ' → ' + l.link); continue; }
          if (heading) {
            const headings = (app.metadataCache.getFileCache(dest).headings || []).map((h) => h.heading);
            if (!headings.includes(heading)) badHeading.push(f.path + ' → ' + l.link);
          }
        }
      }
      const global = Object.entries(app.metadataCache.unresolvedLinks).filter(([p, v]) => p.startsWith('_code/') && Object.keys(v).length).map(([p, v]) => p + ': ' + Object.keys(v).slice(0, 2).join(', '));
      return { links, unresolved: unresolved.length, unresolved_sample: unresolved.slice(0, 5), bad_heading: badHeading.length, bad_heading_sample: badHeading.slice(0, 5), cache_unresolved: global.length, cache_unresolved_sample: global.slice(0, 5) };
    `, 120000);
    if (r.unresolved || r.bad_heading || r.cache_unresolved) throw new Error(JSON.stringify(r));
    return { ...r, summary: `${r.links} links, all resolved` };
  });

  await step('frontmatter parses into the fields Dataview reads', async () => {
    const r = await page.evaluate(`
      const files = app.vault.getMarkdownFiles().filter((f) => f.path.startsWith('_code/Types/') || f.path.startsWith('_code/Files/'));
      const bad = [];
      for (const f of files) {
        const fm = app.metadataCache.getFileCache(f).frontmatter;
        if (!fm || fm.codegraph !== 'generated' || typeof fm.codegraph_id !== 'string' || !Array.isArray(fm.tested_by)
            || typeof fm.inbound_refs !== 'number' || typeof fm.is_test !== 'boolean' || !Array.isArray(fm.aliases)) bad.push(f.path);
      }
      return { notes: files.length, bad: bad.length, bad_sample: bad.slice(0, 5) };
    `);
    if (r.bad || r.notes === 0) throw new Error(JSON.stringify(r));
    return { ...r, summary: `${r.notes} notes` };
  });

  const target = await step('a type note opens and the live panel answers for it', async () => {
    const t = await page.evaluate(`
      const files = app.vault.getMarkdownFiles().filter((f) => f.path.startsWith('_code/Types/'));
      const ranked = files.map((f) => ({ f, fm: app.metadataCache.getFileCache(f).frontmatter }))
        .filter((x) => x.fm && x.fm.kind === 'Type' && !x.fm.is_test)
        .sort((a, b) => b.fm.inbound_refs - a.fm.inbound_refs);
      if (!ranked.length) throw new Error('no type notes');
      const pick = ranked[0].f;
      await app.workspace.getLeaf(false).openFile(pick);
      app.commands.executeCommandById('codegraph:open-panel');
      return { path: pick.path, basename: pick.basename, alias: (ranked[0].fm.aliases || [])[0] || pick.basename };
    `);
    await page.waitFor(`
      const panel = document.querySelector('.codegraph-panel');
      const title = panel && panel.querySelector('.codegraph-title');
      const body = panel && panel.querySelector('.codegraph-body');
      return title && title.textContent === ${JSON.stringify(t.basename)} && body && !/Asking the graph/.test(body.textContent);
    `, { timeoutMs: 60000, label: 'the panel to answer for the type' });
    return { ...t, summary: t.basename };
  });

  await step('picking a member shows its callers as links that resolve', async () => {
    const r = await page.evaluate(`
      const panel = document.querySelector('.codegraph-panel');
      const options = [...panel.querySelectorAll('select option')].filter((o) => o.value);
      const wait = async () => { for (let i = 0; i < 120; i++) { const b = document.querySelector('.codegraph-panel .codegraph-body'); if (b && !/Asking the graph/.test(b.textContent)) return; await new Promise((r) => setTimeout(r, 250)); } throw new Error('panel never answered'); };
      for (const o of options.slice(0, 15)) {
        const select = document.querySelector('.codegraph-panel select');
        select.value = o.value;
        select.dispatchEvent(new Event('change'));
        await wait();
        const tab = [...document.querySelectorAll('.codegraph-tabs button')].find((b) => b.textContent === 'Callers');
        if (tab && !tab.classList.contains('is-active')) { tab.click(); await wait(); }
        const links = [...document.querySelectorAll('.codegraph-panel .codegraph-rows a.internal-link')];
        if (links.length === 0) continue;
        const unresolved = links.map((a) => a.getAttribute('href')).filter((h) => !app.metadataCache.getFirstLinkpathDest(h.split('#')[0], ''));
        const rows = document.querySelectorAll('.codegraph-panel .codegraph-rows li').length;
        return { member: o.textContent, rows, links: links.length, unresolved, href: links[0].getAttribute('href') };
      }
      throw new Error('no member of the busiest type has callers in the panel');
    `, 120000);
    if (r.unresolved.length) throw new Error(JSON.stringify(r));
    await shot('panel');
    return { ...r, summary: `${r.member}: ${r.rows} rows, ${r.links} links` };
  }).then(async (r) => {
    if (!r) return;
    await step('clicking a row opens that note', async () => {
      const want = r.href.split('#')[0].split('/').pop();
      await page.evaluate(`document.querySelector('.codegraph-panel .codegraph-rows a.internal-link').click(); return true;`);
      await page.waitFor(`return app.workspace.getActiveFile() && app.workspace.getActiveFile().basename === ${JSON.stringify(want)};`, { timeoutMs: 15000, label: want });
      return { summary: want };
    });
  });

  await step('the status bar reports a fresh index', () => page.waitFor(`
    const text = [...document.querySelectorAll('.status-bar-item')].map((e) => e.textContent).find((t) => t.startsWith('CodeGraph'));
    return text && /fresh/.test(text) ? { summary: text } : null;
  `, { timeoutMs: 30000, label: 'CodeGraph … fresh in the status bar' }));

  await step("a changed .mcp.json entry is not used until the dialog approves it", async () => {
    // Drop the approval data.json carried, as if a pull had changed the entry: the plugin must fall back to the
    // installed binaries, show the entry, and go back to the repo's server once "Use it" is clicked.
    await page.evaluate(`
      const plugin = app.plugins.plugins.codegraph;
      plugin.settings.approvedRepoConfig = {};
      await plugin.saveSettings();
      return true;
    `);
    const pending = await page.waitFor(`
      const plugin = app.plugins.plugins.codegraph;
      const modal = document.querySelector('.codegraph-modal');
      return modal && plugin.launch && plugin.launch.repoConfig && plugin.launch.repoConfig.decision === 'pending'
        ? { command: plugin.launch.mcp.command, shown: modal.querySelector('pre code').textContent.includes(plugin.launch.repoConfig.command) } : null;
    `, { timeoutMs: 15000, label: 'the .mcp.json dialog' });
    if (pending.command === 'dotnet') throw new Error('the unapproved entry was used');
    if (!pending.shown) throw new Error('the dialog does not show the entry');
    await shot('mcp-approval');
    await page.evaluate(`[...document.querySelectorAll('.codegraph-modal button')].find((b) => b.textContent === 'Use it').click(); return true;`);
    return page.waitFor(`
      const plugin = app.plugins.plugins.codegraph;
      const ok = !document.querySelector('.codegraph-modal') && plugin.launch && plugin.launch.repoConfig && plugin.launch.repoConfig.decision === 'approved'
        && plugin.launch.mcp.command === 'dotnet' && plugin.settings.approvedRepoConfig[plugin.settings.repoRoot] === plugin.launch.repoConfig.fingerprint;
      return ok ? { summary: 'pending: installed binaries; approved: the repo entry again' } : null;
    `, { timeoutMs: 15000, label: 'the entry approved and in use' });
  });

  await step('Find symbol opens the note it picks', async () => {
    const name = (target && target.alias) || 'Index';
    await page.evaluate(`app.commands.executeCommandById('codegraph:find-symbol'); return true;`);
    await page.waitFor(`return document.querySelector('.prompt-input');`, { timeoutMs: 10000, label: 'the search box' });
    await page.evaluate(`const i = document.querySelector('.prompt-input'); i.value = ${JSON.stringify(name)}; i.dispatchEvent(new Event('input')); return true;`);
    await page.waitFor(`return document.querySelectorAll('.suggestion-item').length > 0;`, { timeoutMs: 30000, label: 'suggestions' });
    await shot('find-symbol');
    await page.evaluate(`document.querySelector('.suggestion-item').click(); return true;`);
    await page.waitFor(`return document.querySelector('.prompt-input') === null && app.workspace.getActiveFile() && app.workspace.getActiveFile().path.startsWith('_code/');`, { timeoutMs: 15000, label: 'a code note to open' });
    return { summary: `${name} → ${await page.evaluate('return app.workspace.getActiveFile().path')}` };
  });

  await step('Insert code link puts a link in a written note, and the type gets a backlink', async () => {
    const name = (target && target.alias) || 'Index';
    await page.evaluate(`
      const file = await app.vault.create('docs/Feature.md', '# A feature\\n\\nThe code: \\n');
      await app.workspace.getLeaf(false).openFile(file);
      for (let i = 0; i < 40 && !app.workspace.activeEditor; i++) await new Promise((r) => setTimeout(r, 250));
      const editor = app.workspace.activeEditor.editor;
      editor.setCursor({ line: 2, ch: editor.getLine(2).length });
      app.commands.executeCommandById('codegraph:insert-code-link');
      return true;
    `);
    await page.waitFor(`return document.querySelector('.prompt-input');`, { timeoutMs: 10000, label: 'the search box' });
    await page.evaluate(`const i = document.querySelector('.prompt-input'); i.value = ${JSON.stringify(name)}; i.dispatchEvent(new Event('input')); return true;`);
    await page.waitFor(`return document.querySelectorAll('.suggestion-item').length > 0;`, { timeoutMs: 30000, label: 'suggestions' });
    await page.evaluate(`document.querySelector('.suggestion-item').click(); return true;`);
    const text = await page.waitFor(`const e = app.workspace.activeEditor; const v = e && e.editor.getValue(); return v && v.includes('[[_code/') ? v : null;`, { timeoutMs: 15000, label: 'the link in the editor' });
    const linked = await page.waitFor(`
      const out = app.metadataCache.resolvedLinks['docs/Feature.md'] || {};
      const hit = Object.keys(out).find((p) => p.startsWith('_code/'));
      return hit || null;
    `, { timeoutMs: 20000, label: 'the link to resolve in the metadata cache' });
    return { link: text.match(/\[\[[^\]]+\]\]/)[0], backlink_from: 'docs/Feature.md', to: linked, summary: linked };
  });

  if (ADDED[args.lang]) {
    await step('a re-index outside Obsidian refreshes the notes on its own', async () => {
      const file = sourceFiles(args.repo, EXT[args.lang]).find((f) => !/spec|test/i.test(f));
      appendFileSync(file, ADDED[args.lang]);
      const target = JSON.parse(execFileSync('python3', ['-c', 'import json,sys; print(json.dumps(json.load(open(sys.argv[1]))["target"]))', join(args.repo, 'expected.json')]).toString());
      const indexRun = spawnSync('codegraph-index', [join(args.repo, target)], { encoding: 'utf8' });
      if (indexRun.status !== 0) throw new Error(`codegraph-index failed (${indexRun.status}): ${(indexRun.stderr || '').slice(-500)}`);
      const indexLog = (indexRun.stderr || '').trim().split('\n').slice(-3).join(' | ');
      const inGraph = () => spawnSync('sh', ['-c', `codegraph-index export --db '${join(args.repo, '.codegraph', 'graph.db')}' 2>/dev/null | grep -c AddedByE2e`], { encoding: 'utf8' }).stdout.trim();
      const state = () => page.evaluate(`
        const p = app.plugins.plugins.codegraph;
        const fs = require('fs');
        const dir = require('path').dirname(p.launch.dbPath);
        const base = app.vault.adapter.getBasePath();
        const walk = (d) => fs.readdirSync(d, { withFileTypes: true }).flatMap((e) => e.isDirectory() ? walk(d + '/' + e.name) : [d + '/' + e.name]);
        const onDisk = walk(base + '/_code').filter((f) => /Added/.test(f)).map((f) => f.slice(base.length + 1));
        const manifest = JSON.parse(fs.readFileSync(base + '/_code/.codegraph-export.json', 'utf8'));
        const inVault = app.vault.getMarkdownFiles().filter((f) => /Added/.test(f.path)).map((f) => f.path);
        return { busy: p.busy, lastGraphStamp: p.lastGraphStamp, graphStamp: p.graphStamp(), watcher: !!p.watcher, files: fs.readdirSync(dir),
          onDisk, inVault, manifestIds: Object.keys(manifest.nodes).filter((k) => /Added/.test(k)), manifestFiles: manifest.files.length };
      `).catch((e) => String(e));
      const note = await page.waitFor(`
        const f = app.vault.getMarkdownFiles().find((x) => x.path.startsWith('_code/') && x.basename.endsWith('AddedByE2e'));
        return f && app.metadataCache.getFileCache(f) ? f.path : null;
      `, { timeoutMs: 60000, label: 'the AddedByE2e note' }).catch(async (e) => { throw new Error(e.message + ' ' + JSON.stringify(await state()) + ' graph has it: ' + inGraph() + '; edited ' + file + '; index: ' + indexLog.slice(-400)); });
      return { edited: file, note, summary: note };
    });
  }

  await step('an edit without a re-index shows the stale banner', async () => {
    const file = sourceFiles(args.repo, EXT[args.lang]).find((f) => !/spec|test/i.test(f)) || sourceFiles(args.repo, EXT[args.lang])[0];
    appendFileSync(file, `\n${args.lang === 'python' ? '#' : '//'} edited, not re-indexed\n`);
    await page.evaluate(`
      const files = app.vault.getMarkdownFiles().filter((f) => f.path.startsWith('_code/Types/') || f.path.startsWith('_code/Files/'));
      await app.workspace.getLeaf(false).openFile(files[0]);
      app.commands.executeCommandById('codegraph:open-panel');
      await new Promise((r) => setTimeout(r, 500));
      const view = app.workspace.getLeavesOfType('codegraph-live')[0].view;
      view.follow(true);
      return true;
    `);
    // The server caches its freshness check for a few seconds, so ask again until the answer catches up.
    await page.waitFor(`
      if (document.querySelector('.codegraph-panel .codegraph-stale')) return true;
      if (Date.now() - (window.__cgAsked || 0) > 2000) {
        window.__cgAsked = Date.now();
        app.workspace.getLeavesOfType('codegraph-live')[0].view.render();
      }
      return false;
    `, { timeoutMs: 30000, label: 'the stale banner' });
    await shot('stale');
    return { edited: file, summary: 'banner shown' };
  });

  const exceptions = page.events.filter((e) => e.method === 'Runtime.exceptionThrown').map((e) => e.params.exceptionDetails.exception?.description || e.params.exceptionDetails.text);
  const errors = page.events.filter((e) => e.method === 'Runtime.consoleAPICalled' && e.params.type === 'error')
    .map((e) => e.params.args.map((a) => a.value ?? a.description ?? '').join(' '));
  const ours = [...exceptions, ...errors].filter((t) => /codegraph/i.test(t));
  steps.push({ name: 'no errors from the plugin in the console', ok: ours.length === 0, detail: { exceptions: exceptions.length, errors: errors.length, ours: ours.slice(0, 5) } });
  console.log(`  ${ours.length ? 'FAIL' : 'ok  '}  no errors from the plugin in the console${ours.length ? ': ' + ours[0].slice(0, 200) : ''}`);
  await shot('end');
  page.close();
}

main().catch((error) => {
  steps.push({ name: 'harness', ok: false, error: String(error.stack || error) });
  console.log(`  FAIL  harness: ${error.message}`);
}).finally(() => {
  result.ok = steps.length > 0 && steps.every((s) => s.ok);
  result.passed = steps.filter((s) => s.ok).length;
  result.total = steps.length;
  writeFileSync(`${args.out}.json`, JSON.stringify(result, null, 1));
  console.log(`${result.ok ? 'PASS' : 'FAIL'} ${result.passed}/${result.total} steps (Obsidian ${result.obsidian}, ${args.lang})`);
  process.exit(result.ok ? 0 : 1);
});
