'use strict';

// The CodeGraph plugin for Obsidian. It writes the code graph into the vault as generated notes (by running
// `codegraph-index export obsidian`), keeps them fresh when the graph changes, and answers live questions about the
// note in front of you through the same codegraph-mcp server Claude Code uses.

const { Plugin, PluginSettingTab, Setting, ItemView, Notice, SuggestModal, Modal, MarkdownView, FileSystemAdapter } = require('obsidian');
const fs = require('fs');
const path = require('path');
const { McpClient } = require('../lib/mcp.js');
const { parseAnswer } = require('../lib/rows.js');
const { resolveLaunch } = require('../lib/config.js');
const { linkText, wikilink, idAt, membersOf, editorUrl } = require('../lib/links.js');
const { run, exportArgs, summaryOf } = require('../lib/run.js');

const VIEW_TYPE = 'codegraph-live';
const ICON = 'git-fork';

const DEFAULT_SETTINGS = {
  repoRoot: '',
  folder: '_code',
  mcpPath: '',
  indexPath: '',
  editorUrl: 'vscode://file{file}:{line}',
  autoRefresh: true,
  depth: 1,
  // Per repo folder: the fingerprint of the .mcp.json entry the user approved, or chose to ignore.
  approvedRepoConfig: {},
  ignoredRepoConfig: {},
};

const MEMBER_TABS = [
  { key: 'callers', label: 'Callers', tool: 'callers_of', args: (id, s) => ({ id, depth: s.depth }) },
  { key: 'callees', label: 'Callees', tool: 'callees_of', args: (id, s) => ({ id, depth: s.depth }) },
  { key: 'impact', label: 'Impact', tool: 'impact_of', args: (id) => ({ id }) },
  { key: 'tests', label: 'Tests', tool: 'tests_for', args: (id) => ({ id }) },
];

const TYPE_TABS = [
  { key: 'references', label: 'References', tool: 'references_to', args: (id) => ({ id }) },
  { key: 'impact', label: 'Impact', tool: 'impact_of', args: (id) => ({ id }) },
  { key: 'tests', label: 'Tests', tool: 'tests_for', args: (id) => ({ id }) },
  { key: 'members', label: 'Members', tool: 'members_of', args: (id) => ({ id }) },
];

class CodeGraphPlugin extends Plugin {
  async onload() {
    this.settings = Object.assign({}, DEFAULT_SETTINGS, await this.loadData());
    this.client = null;
    this.launch = null;
    this.watcher = null;
    this.exportManifest = null;
    this.exportManifestStamp = 0;
    this.lastGraphStamp = 0;
    this.busy = false;

    this.statusEl = this.addStatusBarItem();
    this.setStatus('CodeGraph');

    this.registerView(VIEW_TYPE, (leaf) => new LiveView(leaf, this));
    this.addRibbonIcon(ICON, 'Open the CodeGraph panel', () => this.openPanel());

    this.addCommand({ id: 'rebuild-notes', name: 'Rebuild code notes', callback: () => this.rebuild(false) });
    this.addCommand({ id: 'reindex-and-rebuild', name: 'Re-index repo and rebuild code notes', callback: () => this.reindex() });
    this.addCommand({ id: 'open-panel', name: 'Open live panel', callback: () => this.openPanel() });
    this.addCommand({
      id: 'find-symbol',
      name: 'Find symbol',
      callback: () => new SymbolModal(this.app, this, (row) => this.openNode(row.id, row.fullName)).open(),
    });
    this.addCommand({
      id: 'insert-code-link',
      name: 'Insert code link',
      editorCallback: (editor) => new SymbolModal(this.app, this, (row) => this.insertLink(editor, row)).open(),
    });

    this.addSettingTab(new CodeGraphSettingTab(this.app, this));
    this.registerInterval(window.setInterval(() => {
      if (this.app.workspace.getLeavesOfType(VIEW_TYPE).length > 0) {
        this.refreshStatus();
      }
    }, 60000));
    this.app.workspace.onLayoutReady(() => this.configure());
  }

  onunload() {
    this.teardown();
  }

  teardown() {
    if (this.client) {
      this.client.stop();
      this.client = null;
    }

    if (this.watcher) {
      this.watcher.close();
      this.watcher = null;
    }

    window.clearTimeout(this.refreshTimer);
  }

  async saveSettings() {
    await this.saveData(this.settings);
    window.clearTimeout(this.configureTimer);
    this.configureTimer = window.setTimeout(() => this.configure(), 800);
  }

  /** (Re)starts everything that depends on the settings: the server launch, the graph watcher, the status. */
  configure() {
    this.teardown();
    this.exportManifest = null;
    this.exportManifestStamp = 0;
    if (!this.settings.repoRoot) {
      this.setStatus('CodeGraph: set the repo in settings');
      return;
    }

    this.launch = resolveLaunch(this.settings);
    // The repo's .mcp.json is repo content: it runs only once the user has seen and approved this exact entry.
    const repoConfig = this.launch.repoConfig;
    if (repoConfig && repoConfig.decision === 'pending' && this.promptedFingerprint !== repoConfig.fingerprint) {
      this.promptedFingerprint = repoConfig.fingerprint;
      new RepoConfigModal(this.app, this, repoConfig).open();
    }

    this.client = new McpClient(this.launch.mcp, {
      log: (line) => console.debug('[codegraph-mcp]', line),
      clientVersion: this.manifest.version,
    });
    this.lastGraphStamp = this.graphStamp();
    if (this.settings.autoRefresh) {
      this.watchGraph();
    }

    this.refreshStatus();
    this.app.workspace.getLeavesOfType(VIEW_TYPE).forEach((leaf) => leaf.view.follow(true));
  }

  setStatus(text) {
    this.statusEl.setText(text);
  }

  vaultBase() {
    const adapter = this.app.vault.adapter;
    return adapter instanceof FileSystemAdapter ? adapter.getBasePath() : null;
  }

  folder() {
    return (this.settings.folder || '_code').replace(/\\/g, '/').replace(/^\/+|\/+$/g, '');
  }

  /** The exporter's id → note map, re-read when the file changes. */
  loadExportManifest() {
    const base = this.vaultBase();
    if (!base) {
      return null;
    }

    const file = path.join(base, ...this.folder().split('/'), '.codegraph-export.json');
    try {
      const stamp = fs.statSync(file).mtimeMs;
      if (!this.exportManifest || stamp !== this.exportManifestStamp) {
        this.exportManifest = JSON.parse(fs.readFileSync(file, 'utf8'));
        this.exportManifestStamp = stamp;
      }
    } catch {
      this.exportManifest = null;
    }

    return this.exportManifest;
  }

  graphStamp() {
    try {
      return this.launch ? fs.statSync(this.launch.dbPath).mtimeMs : 0;
    } catch {
      return 0;
    }
  }

  watchGraph() {
    const dir = path.dirname(this.launch.dbPath);
    try {
      this.watcher = fs.watch(dir, (event, name) => {
        if (name && !String(name).startsWith(path.basename(this.launch.dbPath))) {
          return;
        }

        window.clearTimeout(this.refreshTimer);
        this.refreshTimer = window.setTimeout(() => {
          const stamp = this.graphStamp();
          if (stamp !== 0 && stamp !== this.lastGraphStamp) {
            this.rebuild(true);
          }
        }, 2000);
      });
    } catch (error) {
      console.warn('[codegraph] cannot watch', dir, error);
    }
  }

  async rebuild(quiet) {
    if (!this.ready()) {
      return;
    }

    if (this.busy) {
      if (!quiet) {
        new Notice('CodeGraph is already working; try again in a moment.');
      }

      return;
    }

    const base = this.vaultBase();
    if (!base) {
      new Notice('CodeGraph needs a vault on the local file system.');
      return;
    }

    this.busy = true;
    const notice = quiet ? null : new Notice('CodeGraph: writing code notes…', 0);
    const stamp = this.graphStamp();
    try {
      const outDir = path.join(base, ...this.folder().split('/'));
      const result = await run(this.launch.index.command, exportArgs(this.launch.dbPath, outDir, this.folder()), this.launch.index);
      if (result.code !== 0) {
        throw new Error(lastLines(result.stderr) || `exit code ${result.code}`);
      }

      this.lastGraphStamp = stamp;
      const summary = summaryOf(result.stdout) || 'notes written';
      if (!quiet) {
        new Notice(`CodeGraph: ${summary.replace(/^obsidian: /, '')}`);
      }

      this.loadExportManifest();
      await this.refreshStatus();
      this.app.workspace.getLeavesOfType(VIEW_TYPE).forEach((leaf) => leaf.view.follow(true));
    } catch (error) {
      new Notice(`CodeGraph could not write the notes: ${messageOf(error)}`, 10000);
    } finally {
      notice?.hide();
      this.busy = false;
    }
  }

  async reindex() {
    if (!this.ready() || this.busy) {
      return;
    }

    this.busy = true;
    const notice = new Notice('CodeGraph: indexing the repo…', 0);
    try {
      const result = await run(this.launch.index.command, [this.settings.repoRoot], this.launch.index, undefined);
      if (result.code !== 0) {
        throw new Error(lastLines(result.stderr) || `exit code ${result.code}`);
      }
    } catch (error) {
      new Notice(`CodeGraph could not index the repo: ${messageOf(error)}`, 10000);
      return;
    } finally {
      notice.hide();
      this.busy = false;
    }

    await this.rebuild(false);
  }

  async refreshStatus() {
    if (!this.client) {
      return;
    }

    const manifest = this.loadExportManifest();
    const commit = manifest && manifest.commit ? manifest.commit.slice(0, 7) : null;
    try {
      const text = await this.client.callTool('index_status', {});
      const stale = /STALE/.test(text);
      const pending = this.launch && this.launch.repoConfig && this.launch.repoConfig.decision === 'pending' ? ' · .mcp.json not approved' : '';
      this.setStatus(`CodeGraph${commit ? ' ' + commit : ''} · ${stale ? 'stale' : 'fresh'}${pending}`);
    } catch (error) {
      this.setStatus('CodeGraph: server unavailable');
      console.warn('[codegraph] index_status failed', error);
    }
  }

  /** Records the user's decision on the repo's .mcp.json entry and reconfigures. */
  async decideRepoConfig(repoConfig, decision) {
    const mine = decision === 'approved' ? 'approvedRepoConfig' : 'ignoredRepoConfig';
    const other = decision === 'approved' ? 'ignoredRepoConfig' : 'approvedRepoConfig';
    this.settings[mine] = { ...(this.settings[mine] || {}), [this.settings.repoRoot]: repoConfig.fingerprint };
    this.settings[other] = { ...(this.settings[other] || {}) };
    delete this.settings[other][this.settings.repoRoot];
    await this.saveSettings();
  }

  async forgetRepoConfig() {
    for (const key of ['approvedRepoConfig', 'ignoredRepoConfig']) {
      this.settings[key] = { ...(this.settings[key] || {}) };
      delete this.settings[key][this.settings.repoRoot];
    }

    this.promptedFingerprint = null;
    await this.saveSettings();
  }

  ready() {
    if (!this.settings.repoRoot || !this.launch) {
      new Notice('Set the repo folder in the CodeGraph settings first.');
      return false;
    }

    return true;
  }

  async openPanel() {
    let leaf = this.app.workspace.getLeavesOfType(VIEW_TYPE)[0];
    if (!leaf) {
      leaf = this.app.workspace.getRightLeaf(false);
      await leaf.setViewState({ type: VIEW_TYPE, active: true });
    }

    this.app.workspace.revealLeaf(leaf);
  }

  async openNode(id, label) {
    const target = linkText(this.loadExportManifest(), id);
    if (!target) {
      new Notice(`${label || id} has no note yet. Run "Rebuild code notes".`);
      return;
    }

    await this.app.workspace.openLinkText(target, '', false);
  }

  insertLink(editor, row) {
    const label = shortLabel(row);
    const link = wikilink(this.loadExportManifest(), row.id, label);
    if (!link) {
      new Notice(`${row.fullName} has no note yet; inserted its name. Run "Rebuild code notes" to link it.`);
    }

    editor.replaceSelection(link || '`' + row.fullName + '`');
  }

  openSource(file, line) {
    const url = editorUrl(this.settings.editorUrl, file, line);
    try {
      require('electron').shell.openExternal(url);
    } catch {
      window.open(url);
    }
  }
}

class LiveView extends ItemView {
  constructor(leaf, plugin) {
    super(leaf);
    this.plugin = plugin;
    this.target = null; // { noteId, notePath, title, isType }
    this.memberId = null;
    this.tab = null;
    this.sequence = 0;
  }

  getViewType() {
    return VIEW_TYPE;
  }

  getDisplayText() {
    return 'CodeGraph';
  }

  getIcon() {
    return ICON;
  }

  async onOpen() {
    this.registerEvent(this.app.workspace.on('active-leaf-change', () => this.follow(false)));
    this.registerEvent(this.app.workspace.on('file-open', () => this.follow(false)));
    this.registerEvent(this.app.metadataCache.on('changed', () => this.follow(false)));
    this.follow(true);
  }

  /** Points the panel at the active code note, and at the member heading under the cursor when there is one. */
  follow(force) {
    const file = this.app.workspace.getActiveFile();
    const frontmatter = file ? this.app.metadataCache.getFileCache(file)?.frontmatter : null;
    const noteId = frontmatter && frontmatter.codegraph_id;
    if (!noteId) {
      if (force || !this.target) {
        this.render();
      }

      return;
    }

    const manifest = this.plugin.loadExportManifest();
    let memberId = null;
    const view = this.app.workspace.getActiveViewOfType(MarkdownView);
    if (manifest && view && view.file === file && view.getMode() === 'source') {
      const line = view.editor.getCursor().line;
      const headings = (this.app.metadataCache.getFileCache(file)?.headings || []).filter((h) => h.level === 3 && h.position.start.line <= line);
      if (headings.length > 0) {
        const id = idAt(manifest, file.path, headings[headings.length - 1].heading);
        memberId = id && id !== noteId ? id : null;
      }
    }

    const changed = !this.target || this.target.noteId !== noteId || (memberId && memberId !== this.memberId);
    if (!changed && !force) {
      return;
    }

    const previous = this.target ? this.target.noteId : null;
    this.target = { noteId, notePath: file.path, title: file.basename, isType: frontmatter.kind === 'Type' };
    if (previous !== noteId || memberId) {
      this.memberId = memberId;
    }

    this.render();
  }

  currentId() {
    return this.memberId || (this.target && this.target.noteId);
  }

  tabs() {
    return this.memberId || !(this.target && this.target.isType) ? MEMBER_TABS : TYPE_TABS;
  }

  render() {
    const root = this.containerEl.children[1];
    root.empty();
    root.addClass('codegraph-panel');
    if (!this.plugin.settings.repoRoot) {
      root.createEl('p', { text: 'Set the repo folder in the CodeGraph settings to start.', cls: 'codegraph-muted' });
      return;
    }

    if (!this.target) {
      root.createEl('p', { text: `Open a note under ${this.plugin.folder()}/ to see who uses it, what it calls and which tests guard it.`, cls: 'codegraph-muted' });
      const button = root.createEl('button', { text: 'Rebuild code notes' });
      button.onclick = () => this.plugin.rebuild(false);
      return;
    }

    const header = root.createDiv({ cls: 'codegraph-header' });
    header.createEl('div', { text: this.target.title, cls: 'codegraph-title' });
    const members = membersOf(this.plugin.loadExportManifest(), this.target.notePath);
    if (members.length > 0) {
      const select = header.createEl('select', { cls: 'dropdown' });
      select.setAttr('aria-label', 'Member');
      select.createEl('option', { text: this.target.isType ? 'The whole type' : 'The whole file', value: '' });
      for (const m of members) {
        const option = select.createEl('option', { text: m.heading, value: m.id });
        if (m.id === this.memberId) {
          option.selected = true;
        }
      }

      select.onchange = () => {
        this.memberId = select.value || null;
        this.render();
      };
    }

    const tabs = this.tabs();
    if (!tabs.some((t) => t.key === this.tab)) {
      this.tab = tabs[0].key;
    }

    const bar = root.createDiv({ cls: 'codegraph-tabs' });
    bar.setAttr('role', 'tablist');
    for (const t of tabs) {
      const button = bar.createEl('button', { text: t.label, cls: t.key === this.tab ? 'is-active' : '' });
      button.setAttr('role', 'tab');
      button.setAttr('aria-selected', String(t.key === this.tab));
      button.onclick = () => {
        this.tab = t.key;
        this.render();
      };
    }

    const body = root.createDiv({ cls: 'codegraph-body' });
    this.ask(tabs.find((t) => t.key === this.tab), body);
  }

  async ask(tab, body) {
    const sequence = ++this.sequence;
    const id = this.currentId();
    body.createEl('p', { text: 'Asking the graph…', cls: 'codegraph-muted' });
    let text;
    try {
      if (!this.plugin.client) {
        throw new Error('the server is not configured');
      }

      text = await this.plugin.client.callTool(tab.tool, tab.args(id, this.plugin.settings));
    } catch (error) {
      if (sequence === this.sequence) {
        body.empty();
        body.createEl('p', { text: `CodeGraph could not answer: ${messageOf(error)}`, cls: 'codegraph-error' });
      }

      return;
    }

    if (sequence !== this.sequence) {
      return;
    }

    body.empty();
    const manifest = this.plugin.loadExportManifest();
    const answer = parseAnswer(text);
    if (answer.stale) {
      const banner = body.createDiv({ cls: 'codegraph-stale' });
      banner.createEl('strong', { text: 'Index is out of date. ' });
      banner.createSpan({ text: answer.stale.replace(/^⚠ STALE INDEX:?\s*/, '') });
      const button = banner.createEl('button', { text: 'Re-index' });
      button.onclick = () => this.plugin.reindex();
    }

    // Headers and summaries sit between groups of rows, in the order the tool wrote them.
    let list = null;
    for (const item of answer.items) {
      if (item.text !== undefined) {
        body.createEl('p', { text: item.text, cls: 'codegraph-muted' });
        list = null;
        continue;
      }

      const row = item.row;
      list = list || body.createEl('ul', { cls: 'codegraph-rows' });
      const li = list.createEl('li');
      li.createSpan({ text: row.kind, cls: 'codegraph-kind' });
      const target = linkText(manifest, row.id);
      if (target) {
        const a = li.createEl('a', { text: shortLabel(row), cls: 'internal-link', href: target });
        a.setAttr('title', row.fullName);
        a.onclick = (event) => {
          event.preventDefault();
          this.app.workspace.openLinkText(target, '', event.ctrlKey || event.metaKey);
        };
      } else {
        li.createSpan({ text: shortLabel(row), attr: { title: row.fullName } });
      }

      if (row.extra) {
        li.createSpan({ text: row.extra, cls: 'codegraph-extra' });
      }

      if (row.stale) {
        li.createSpan({ text: 'stale', cls: 'codegraph-badge' });
      }

      if (row.file) {
        const source = li.createEl('button', { text: `${path.basename(row.file)}:${row.startLine}`, cls: 'codegraph-source' });
        source.setAttr('aria-label', `Open ${row.file} at line ${row.startLine} in your editor`);
        source.onclick = () => this.plugin.openSource(row.file, row.startLine);
      }

    }
  }
}

/** Shows the codegraph entry of the repo's .mcp.json and asks whether the plugin may start it. */
class RepoConfigModal extends Modal {
  constructor(app, plugin, repoConfig) {
    super(app);
    this.plugin = plugin;
    this.repoConfig = repoConfig;
  }

  onOpen() {
    const { contentEl } = this;
    contentEl.empty();
    contentEl.addClass('codegraph-modal');
    contentEl.createEl('h2', { text: 'Use the server from the repo\'s .mcp.json?' });
    contentEl.createEl('p', {
      text: `${this.plugin.settings.repoRoot} has a .mcp.json that names a codegraph server. The file is part of the repo, so a clone or a pull can change it, and the plugin runs what it names with your rights. Use it only if you trust the repo.`,
    });
    const pre = contentEl.createEl('pre', { cls: 'codegraph-config' });
    pre.createEl('code', { text: JSON.stringify({ command: this.repoConfig.command, args: this.repoConfig.args, env: this.repoConfig.env }, null, 2) });
    contentEl.createEl('p', {
      text: 'Until you decide, the plugin starts the installed codegraph-mcp and codegraph-index with your own environment. You can change this under Settings → CodeGraph.',
      cls: 'codegraph-muted',
    });
    const buttons = contentEl.createDiv({ cls: 'codegraph-buttons' });
    const use = buttons.createEl('button', { text: 'Use it', cls: 'mod-cta' });
    use.onclick = async () => {
      this.close();
      await this.plugin.decideRepoConfig(this.repoConfig, 'approved');
    };
    const ignore = buttons.createEl('button', { text: 'Ignore it' });
    ignore.onclick = async () => {
      this.close();
      await this.plugin.decideRepoConfig(this.repoConfig, 'ignored');
    };
    const later = buttons.createEl('button', { text: 'Ask me later' });
    later.onclick = () => this.close();
  }

  onClose() {
    this.contentEl.empty();
  }
}

class SymbolModal extends SuggestModal {
  constructor(app, plugin, onChoose) {
    super(app);
    this.plugin = plugin;
    this.onChoose = onChoose;
    this.setPlaceholder('Type, member or file name');
  }

  async getSuggestions(query) {
    if (query.trim().length < 2 || !this.plugin.client) {
      return [];
    }

    try {
      const text = await this.plugin.client.callTool('find_symbol', { query: query.trim(), limit: 30 });
      return parseAnswer(text).items.filter((i) => i.row).map((i) => i.row);
    } catch (error) {
      console.warn('[codegraph] find_symbol failed', error);
      return [];
    }
  }

  renderSuggestion(row, el) {
    el.createDiv({ text: shortLabel(row) });
    el.createEl('small', {
      text: `${row.kind} · ${row.fullName}${row.file ? ` · ${path.basename(row.file)}:${row.startLine}` : ''}`,
      cls: 'codegraph-muted',
    });
  }

  onChooseSuggestion(row) {
    this.onChoose(row);
  }
}

class CodeGraphSettingTab extends PluginSettingTab {
  constructor(app, plugin) {
    super(app, plugin);
    this.plugin = plugin;
  }

  display() {
    const { containerEl } = this;
    containerEl.empty();
    const s = this.plugin.settings;
    const text = (name, desc, key, placeholder) =>
      new Setting(containerEl).setName(name).setDesc(desc).addText((t) => t
        .setPlaceholder(placeholder)
        .setValue(String(s[key] || ''))
        .onChange(async (value) => {
          s[key] = value.trim();
          await this.plugin.saveSettings();
        }));

    text('Repo folder', 'The git worktree whose graph to show: the folder that holds .codegraph/ (set up with codegraph-index init).', 'repoRoot', '/home/me/dev/App');
    text('Notes folder', 'Vault folder for the generated notes. Everything in it is rewritten; keep your own notes elsewhere.', 'folder', '_code');
    text('Editor link', 'Opens a source location. {file} is the absolute path, {line} the line.', 'editorUrl', DEFAULT_SETTINGS.editorUrl);
    text('codegraph-mcp', 'Leave empty to use the repo\'s approved .mcp.json entry, then ~/.codegraph/bin, then PATH.', 'mcpPath', '');
    text('codegraph-index', 'Leave empty to use the one beside codegraph-mcp, then ~/.codegraph/bin, then PATH.', 'indexPath', '');
    const repoConfig = this.plugin.launch ? this.plugin.launch.repoConfig : null;
    const decision = !repoConfig ? 'The repo has no codegraph entry in .mcp.json; the installed binaries are used.'
      : repoConfig.decision === 'approved' ? 'Approved: the plugin starts the server named there, with its environment.'
        : repoConfig.decision === 'ignored' ? 'Ignored: the plugin starts the installed binaries with your own environment.'
          : 'Not decided yet: the installed binaries are used until you approve the entry.';
    const trust = new Setting(containerEl).setName('Repo .mcp.json').setDesc(decision);
    if (repoConfig) {
      trust.addButton((button) => button.setButtonText('Review').onClick(() => new RepoConfigModal(this.app, this.plugin, repoConfig).open()));
      if (repoConfig.decision !== 'pending') {
        trust.addButton((button) => button.setButtonText('Forget').onClick(async () => {
          await this.plugin.forgetRepoConfig();
          this.display();
        }));
      }
    }
    new Setting(containerEl)
      .setName('Call depth')
      .setDesc('How many hops the Callers and Callees tabs follow.')
      .addSlider((slider) => slider.setLimits(1, 5, 1).setValue(s.depth).setDynamicTooltip().onChange(async (value) => {
        s.depth = value;
        await this.plugin.saveSettings();
      }));
    new Setting(containerEl)
      .setName('Rebuild when the graph changes')
      .setDesc('Rewrites the notes a couple of seconds after codegraph-index (or its watch mode) updates the graph.')
      .addToggle((toggle) => toggle.setValue(s.autoRefresh).onChange(async (value) => {
        s.autoRefresh = value;
        await this.plugin.saveSettings();
      }));
  }
}

/** Type for a type, Type.Member for a member: the last segments of the full name, without parameter lists. */
function shortLabel(row) {
  const name = row.fullName.replace(/\(.*$/, '');
  const parts = name.split('.');
  if (row.kind === 'type' || row.kind === 'project' || row.kind === 'file' || parts.length < 2) {
    return parts[parts.length - 1] || row.fullName;
  }

  return parts.slice(-2).join('.');
}

function lastLines(text) {
  return String(text || '').trim().split(/\r?\n/).slice(-3).join(' ');
}

function messageOf(error) {
  return error && error.message ? error.message : String(error);
}

module.exports = CodeGraphPlugin;
