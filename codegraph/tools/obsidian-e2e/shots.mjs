// Takes the documentation screenshots of the CodeGraph plugin in a real Obsidian, driven over the DevTools protocol.
//
//   node shots.mjs --port 9222 --out /results --type <type note basename> --member <member heading prefix>
//
// Run by screenshots.py inside the e2e image; the vault is already set up. Each shot is <out>/<name>.png.

import { writeFileSync } from 'node:fs';
import { connect, sleep } from './cdp.mjs';

const args = {};
for (let i = 2; i < process.argv.length; i += 2) {
  args[process.argv[i].replace(/^--/, '')] = process.argv[i + 1];
}

const page = await connect(Number(args.port), { timeoutMs: 90000 });
const shot = async (name) => {
  await sleep(800);
  await page.evaluate("document.querySelectorAll('.notice').forEach((n) => n.remove()); return true;");
  // Park the pointer in a corner so no hover tooltip shows.
  await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 2, y: size.height - 40 }).catch(() => {});
  writeFileSync(`${args.out}/${name}.png`, await page.screenshot());
  console.log(`  ${name}.png`);
};
const idle = () => page.waitFor(`
  const b = document.querySelector('.codegraph-panel .codegraph-body');
  return b && !/Asking the graph/.test(b.textContent);
`, { timeoutMs: 60000, label: 'the panel to answer' });
const open = (path, mode = 'preview') => page.evaluate(`
  const file = app.vault.getAbstractFileByPath(${JSON.stringify(path)});
  if (!file) throw new Error('no note ' + ${JSON.stringify(path)});
  const leaf = app.workspace.getLeaf(false);
  await leaf.setViewState({ type: 'markdown', state: { file: file.path, mode: ${JSON.stringify(mode)} }, active: true });
  return true;
`);
const tab = (label) => page.evaluate(`
  const b = [...document.querySelectorAll('.codegraph-tabs button')].find((x) => x.textContent === ${JSON.stringify(label)});
  if (!b) throw new Error('no tab ${label}');
  b.click();
  return true;
`);

await page.waitFor('return window.app && app.workspace && app.workspace.layoutReady', { timeoutMs: 90000, label: 'the workspace' });
// Fill the virtual screen: Electron opens a smaller window.
const size = { width: Number(args.width || 1600), height: Number(args.height || 1000) };
try {
  const { windowId } = await page.send('Browser.getWindowForTarget');
  await page.send('Browser.setWindowBounds', { windowId, bounds: { left: 0, top: 0, ...size, windowState: 'normal' } });
} catch {
  await page.evaluate(`window.resizeTo(${size.width}, ${size.height}); return true;`);
}
await sleep(1000);
await page.evaluate(`
  const p = app.plugins;
  if (typeof p.isEnabled === 'function' && !p.isEnabled()) await p.setEnable(true);
  await p.loadManifests();
  if (!p.plugins.codegraph) await p.enablePluginAndSave('codegraph');
  document.querySelectorAll('.modal-container .modal-close-button').forEach((b) => b.click());
  for (let i = 0; i < 40 && !(p.plugins.codegraph && p.plugins.codegraph.launch); i++) await new Promise((r) => setTimeout(r, 250));
  app.commands.executeCommandById('codegraph:rebuild-notes');
  return true;
`);
await page.waitFor(`
  const plugin = app.plugins.plugins.codegraph;
  if (!app.vault.getAbstractFileByPath('_code/Index.md') || plugin.busy) return null;
  const manifest = JSON.parse(await app.vault.adapter.read('_code/.codegraph-export.json'));
  const files = app.vault.getMarkdownFiles().filter((f) => f.path.startsWith('_code/'));
  return files.length >= manifest.files.length && files.every((f) => app.metadataCache.getFileCache(f));
`, { timeoutMs: 180000, label: 'the notes' });
const typePath = await page.evaluate(`
  const f = app.vault.getMarkdownFiles().find((x) => x.path.startsWith('_code/Types/') && x.basename === ${JSON.stringify(args.type)});
  if (!f) throw new Error('no type note ${args.type}');
  return f.path;
`);

// 1. The start page, with the file explorer showing the generated folders.
await page.evaluate(`
  const explorer = app.workspace.getLeavesOfType('file-explorer')[0];
  if (explorer) {
    app.workspace.revealLeaf(explorer);
    const view = explorer.view;
    for (const dir of ['_code', '_code/Types']) {
      const item = view.fileItems && view.fileItems[dir];
      if (item && item.setCollapsed) item.setCollapsed(false);
    }
  }
  return true;
`);
await open('_code/Index.md');
await page.evaluate('app.workspace.rightSplit.collapse(); return true;');
await shot('index');
await page.evaluate('app.workspace.rightSplit.expand(); return true;');

// 2. A type note with the panel on one of its members' callers, then the same member's impact.
await open(typePath);
await page.evaluate(`app.commands.executeCommandById('codegraph:open-panel'); return true;`);
await page.waitFor(`const t = document.querySelector('.codegraph-panel .codegraph-title'); return t && t.textContent === ${JSON.stringify(args.type)};`, { timeoutMs: 30000, label: 'the panel title' });
await idle();
await shot('type-references');
await page.evaluate(`
  const select = document.querySelector('.codegraph-panel select');
  const option = [...select.options].find((o) => o.value && o.textContent.startsWith(${JSON.stringify(args.member)}));
  if (!option) throw new Error('no member ${args.member}: ' + [...select.options].map((o) => o.textContent).join(', '));
  select.value = option.value;
  select.dispatchEvent(new Event('change'));
  return true;
`);
await idle();
await tab('Callers');
await idle();
await shot('member-callers');
await tab('Impact');
await idle();
await shot('member-impact');
await tab('Tests');
await idle();
await shot('member-tests');

// 3. Find symbol.
await page.evaluate(`app.commands.executeCommandById('codegraph:find-symbol'); return true;`);
await page.waitFor('return document.querySelector(".prompt-input");', { timeoutMs: 10000, label: 'the search box' });
await page.evaluate(`const i = document.querySelector('.prompt-input'); i.value = 'Freshness'; i.dispatchEvent(new Event('input')); return true;`);
await page.waitFor('return document.querySelectorAll(".suggestion-item").length > 0;', { timeoutMs: 30000, label: 'suggestions' });
await shot('find-symbol');
await page.evaluate(`
  for (let i = 0; i < 3 && document.querySelector('.modal-container'); i++) {
    (document.activeElement || document.body).dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', code: 'Escape', bubbles: true }));
    await new Promise((r) => setTimeout(r, 200));
  }
  return true;
`);

// 4. Reports, at full width: their diagram and tables are wider than a readable line.
await page.evaluate("app.vault.setConfig('readableLineLength', false); app.workspace.rightSplit.collapse(); return true;");
await open('_code/Reports/Project map.md');
await page.waitFor('return document.querySelector(".markdown-reading-view .mermaid svg");', { timeoutMs: 30000, label: 'the Mermaid diagram' }).catch(() => null);
await shot('project-map');
await open('_code/Reports/Test coverage.md');
await shot('test-coverage');
await page.evaluate("app.vault.setConfig('readableLineLength', true); app.workspace.rightSplit.expand(); return true;");

// 5. The settings tab, over a type note.
await open(typePath);
await page.evaluate(`
  app.setting.open();
  app.setting.openTabById('codegraph');
  // Shown only, never saved: a placeholder in place of this machine's home folder.
  const input = [...document.querySelectorAll('.modal-container input[type=text]')].find((i) => i.value === app.plugins.plugins.codegraph.settings.repoRoot);
  if (input) input.value = '/home/you/src/my-repo';
  return true;
`);
await shot('settings');
await page.evaluate('app.setting.close(); return true;');

// 6. Obsidian's graph view over the code notes.
await page.evaluate(`app.workspace.rightSplit.collapse(); app.workspace.leftSplit.collapse(); app.commands.executeCommandById('graph:open'); return true;`);
await sleep(8000);
await shot('graph-view');
page.close();
