// Draws the Overview from the state the extension posts. Builds DOM nodes (no innerHTML with data), so paths and
// messages from the graph are always text, and styles go through the CSSOM, which the page's CSP allows.
// @ts-check
(function () {
  // @ts-ignore acquireVsCodeApi is injected by VS Code
  const vscode = acquireVsCodeApi();
  const app = /** @type {HTMLElement} */ (document.getElementById('app'));
  const svgNs = 'http://www.w3.org/2000/svg';
  const palette = ['--cg-blue', '--cg-purple', '--cg-green', '--cg-orange', '--cg-yellow', '--cg-red'];
  const languageNames = { csharp: 'C#', typescript: 'TypeScript', javascript: 'JavaScript', java: 'Java', kotlin: 'Kotlin', scala: 'Scala', python: 'Python', go: 'Go', rust: 'Rust' };

  /** @param {string} tag @param {Record<string, any>} [props] @param {...any} children */
  function h(tag, props, ...children) {
    const el = document.createElement(tag);
    for (const [key, value] of Object.entries(props || {})) {
      if (value === undefined || value === null || value === false) continue;
      if (key === 'class') el.className = value;
      else if (key === 'style') Object.assign(el.style, value);
      else if (key.startsWith('on')) el.addEventListener(key.slice(2), value);
      else if (key.startsWith('--')) el.style.setProperty(key, value);
      else el.setAttribute(key, value === true ? '' : String(value));
    }
    for (const child of children.flat(Infinity)) {
      if (child === undefined || child === null || child === false) continue;
      el.append(child instanceof Node ? child : document.createTextNode(String(child)));
    }
    return el;
  }

  /** Icons drawn as SVG paths (16×16, codicon-like), so the page needs no icon font. */
  const icons = {
    play: 'M4 2.5v11l9-5.5z',
    stop: 'M4 4h8v8H4z',
    restart: 'M8 2.5a5.5 5.5 0 1 0 5.2 3.7l-1.4.5A4 4 0 1 1 8 4v2l3-2.75L8 .5z',
    log: 'M3 3h10v1.5H3zm0 3h10v1.5H3zm0 3h7v1.5H3zm0 3h10v1.5H3z',
    refresh: 'M13.5 8a5.5 5.5 0 1 1-1.6-3.9L10 6h4.5V1.5l-1.6 1.6A7 7 0 1 0 15 8z',
    chart: 'M2 13.5h12V15H2zM3 8h2.2v4.5H3zm3.9-4h2.2v8.5H6.9zm3.9 2h2.2v6.5h-2.2z',
  };

  /** @param {keyof typeof icons} name @param {string} title @param {() => void} onclick */
  function iconButton(name, title, onclick) {
    const svg = document.createElementNS(svgNs, 'svg');
    svg.setAttribute('viewBox', '0 0 16 16');
    svg.setAttribute('aria-hidden', 'true');
    const path = document.createElementNS(svgNs, 'path');
    path.setAttribute('d', icons[name]);
    path.setAttribute('fill', 'currentColor');
    svg.append(path);
    return h('button', { class: 'icon', title, 'aria-label': title, onclick }, svg);
  }

  /** @param {string} tag @param {Record<string, string | number>} attrs @param {Element[]} [children] */
  function svgEl(tag, attrs, children = []) {
    const el = document.createElementNS(svgNs, tag);
    for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, String(v));
    el.append(...children);
    return el;
  }

  let logoCount = 0;
  function logo() {
    // The CodeGraph logo (assets/logo.svg, also the extension's icon): a ring of five linked nodes open on the right,
    // around the lit node under change. It keeps its own colours and tile, so it reads the same in every theme.
    const ring = [[84, 29.4], [44, 29.4], [24, 64], [44, 98.6], [84, 98.6]];
    const id = `cg-logo-${++logoCount}`;
    return svgEl('svg', { viewBox: '0 0 128 128', class: 'logo', 'aria-hidden': 'true' }, [
      svgEl('defs', {}, [
        svgEl('linearGradient', { id: `${id}-bg`, x1: 0, y1: 0, x2: 1, y2: 1 }, [
          svgEl('stop', { offset: 0, 'stop-color': '#14263B' }),
          svgEl('stop', { offset: 1, 'stop-color': '#0D1B2A' }),
        ]),
        svgEl('filter', { id: `${id}-glow`, x: '-50%', y: '-50%', width: '200%', height: '200%' }, [
          svgEl('feGaussianBlur', { stdDeviation: 2.5, result: 'b' }),
          svgEl('feMerge', {}, [svgEl('feMergeNode', { in: 'b' }), svgEl('feMergeNode', { in: 'SourceGraphic' })]),
        ]),
      ]),
      svgEl('rect', { width: 128, height: 128, rx: 28, fill: `url(#${id}-bg)` }),
      svgEl('g', { stroke: '#00C9B1', 'stroke-width': 3.5, 'stroke-linecap': 'round', fill: 'none' }, [
        svgEl('path', { d: 'M84 29.4 L44 29.4 L24 64 L44 98.6 L84 98.6', opacity: 0.9 }),
        svgEl('g', { opacity: 0.55 }, ring.map(([x, y]) => svgEl('line', { x1: 64, y1: 64, x2: x, y2: y }))),
      ]),
      svgEl('g', { fill: '#00C9B1', stroke: '#0D1B2A', 'stroke-width': 2.5 },
        ring.map(([cx, cy]) => svgEl('circle', { cx, cy, r: 7 }))),
      svgEl('circle', { cx: 64, cy: 64, r: 11, fill: '#FFD166', stroke: '#0D1B2A', 'stroke-width': 2.5, filter: `url(#${id}-glow)` }),
      svgEl('g', { stroke: '#FFD166', 'stroke-width': 3.5, 'stroke-linecap': 'round', opacity: 0.9 }, [
        svgEl('line', { x1: 96, y1: 64, x2: 108, y2: 64 }),
        svgEl('polyline', { points: '102,58 108,64 102,70', fill: 'none' }),
      ]),
    ]);
  }
  /** @param {string} command @param {string} [db] */
  const run = (command, db) => vscode.postMessage({ type: 'command', command, db });

  /** @param {number} n @param {boolean} [compact] */
  function count(n, compact) {
    if (!compact || Math.abs(n) < 10000) return n.toLocaleString('en-US');
    for (const [size, unit] of [[1e9, 'B'], [1e6, 'M'], [1e3, 'k']]) {
      if (Math.abs(n) >= size) {
        const v = n / size;
        return (v >= 100 ? Math.round(v) : Number(v.toFixed(1))) + unit;
      }
    }
    return String(n);
  }

  /** @param {number} n */
  function bytes(n) {
    const units = ['B', 'KB', 'MB', 'GB'];
    let v = n;
    let u = 0;
    while (v >= 1024 && u < units.length - 1) { v /= 1024; u++; }
    return (u === 0 ? v : v.toFixed(v >= 100 ? 0 : 1)) + ' ' + units[u];
  }

  /** @param {string | undefined} iso */
  function ago(iso) {
    if (!iso) return 'never';
    const t = new Date(iso).getTime();
    if (Number.isNaN(t)) return iso;
    const s = Math.max(0, (Date.now() - t) / 1000);
    if (s < 45) return 'just now';
    if (s < 3600) return Math.round(s / 60) + ' min ago';
    if (s < 86400) return Math.round(s / 3600) + ' h ago';
    if (s < 7 * 86400) { const d = Math.round(s / 86400); return d + (d === 1 ? ' day ago' : ' days ago'); }
    return new Date(t).toISOString().slice(0, 10);
  }

  /** @param {string} p */
  const base = (p) => p.split(/[\\/]/).pop() || p;

  /**
   * A control's identity across redraws: its label, else its text (a graph tab's text is the graph's name). The page
   * is redrawn whenever the extension posts state (every 15 s while the window has focus), so focus moves back to
   * the control with the same identity, and keyboard users keep their place.
   * @param {Element} el
   */
  const identity = (el) => el.tagName + '|' + (el.getAttribute('aria-label') || el.textContent || '');

  /** @param {any[]} children */
  function commit(children) {
    const focused = document.activeElement && app.contains(document.activeElement) && document.activeElement !== app
      ? identity(document.activeElement)
      : undefined;
    app.replaceChildren(...children);
    if (focused) {
      const again = Array.from(app.querySelectorAll('button, a, [tabindex]')).find((el) => identity(el) === focused);
      if (again instanceof HTMLElement) again.focus({ preventScroll: true });
    }
  }

  function render(state) {
    const children = [];
    if (!state.installed) {
      children.push(
        h('section', { class: 'card empty' },
          logo(),
          h('h1', {}, 'Install CodeGraph'),
          h('p', {}, 'Downloads the latest release for this machine from GitHub and checks it against the release’s SHA256SUMS. Nothing else is needed for C#.'),
          h('div', { class: 'actions', style: { justifyContent: 'center' } },
            h('button', { class: 'btn primary', onclick: () => run('codegraph.install') }, 'Install CodeGraph'))),
      );
      commit(children);
      return;
    }

    if (state.graphs.length === 0) {
      children.push(
        h('section', { class: 'card empty' },
          logo(),
          h('h1', {}, 'Nothing to index here'),
          h('p', {}, 'No .sln, .slnx, .csproj, tsconfig.json, angular.json, pom.xml, build.gradle, pyproject.toml, go.mod or Cargo.toml was found in this workspace.'),
          h('div', { class: 'actions', style: { justifyContent: 'center' } },
            h('button', { class: 'btn', onclick: () => run('codegraph.refresh') }, 'Search again'))),
      );
      children.push(footer(state));
      commit(children);
      return;
    }

    if (state.graphs.length > 1) {
      children.push(h('nav', { class: 'tabs', role: 'tablist' },
        state.graphs.map((g) => h('button', {
          class: 'tab',
          role: 'tab',
          'aria-selected': String(g.db === state.selected),
          onclick: () => vscode.postMessage({ type: 'select', db: g.db }),
        }, healthDot(g), g.name))));
    }

    const graph = state.graphs.find((g) => g.db === state.selected) || state.graphs[0];
    children.push(hero(graph));
    const status = graph.status;
    if (status && status.exists && status.graph) {
      children.push(daemonCard(graph));
      if (status.graph.changedCount + status.graph.missingCount > 0) children.push(staleCard(graph));
      if (status.graph.languages.length) children.push(languagesCard(status.graph));
      children.push(usageCard(graph));
    }
    children.push(footer(state));
    commit(children);
  }

  function health(g) {
    if (g.busy) return { tone: 'blue', text: g.busy.charAt(0).toUpperCase() + g.busy.slice(1) + '…', spin: true };
    if (g.statusError) return { tone: 'red', text: 'Status failed' };
    const s = g.status;
    if (!s || !s.exists) return { tone: 'muted', text: 'Not indexed' };
    if (s.error || !s.graph) return { tone: 'red', text: 'Unreadable' };
    if (s.graph.stale) {
      const changed = s.graph.changedCount + s.graph.missingCount;
      return { tone: 'yellow', text: changed > 0 ? count(changed) + ' changed' : 'HEAD moved' };
    }
    return { tone: 'green', text: 'Up to date' };
  }

  function healthDot(g) {
    const s = health(g);
    return h('span', { class: 'dot tone-' + s.tone + (s.spin ? ' spin' : '') });
  }

  function hero(g) {
    const s = g.status;
    const state = health(g);
    const tint = { green: '--cg-green', yellow: '--cg-yellow', red: '--cg-red', blue: '--cg-blue', muted: '--cg-accent' }[state.tone];
    const card = h('section', { class: 'card hero', '--hero-tint': 'var(' + tint + ')' });
    const top = h('div', { class: 'hero-top' },
      h('div', { style: { minWidth: '0' } },
        h('h1', { title: g.root }, g.name),
        h('div', { class: 'sub' }, subline(g))),
      h('span', { class: 'pill tone-' + state.tone }, h('span', { class: 'dot' + (state.spin ? ' spin' : '') }), state.text));
    card.append(top);

    if (g.statusError) {
      card.append(h('div', { class: 'warn-line' }, g.statusError));
    } else if (s && s.error) {
      card.append(h('div', { class: 'warn-line' }, s.error));
    }

    if (s && s.graph) {
      const files = s.graph.languages.reduce((n, l) => n + l.files, 0);
      card.append(h('div', { class: 'stats' },
        stat(count(s.graph.nodes, true), 'nodes', count(s.graph.nodes)),
        stat(count(s.graph.edges, true), 'edges', count(s.graph.edges)),
        stat(count(files, true), 'files', count(files))));
      card.append(h('div', { class: 'actions', style: { marginTop: '12px', position: 'relative' } },
        h('button', { class: 'btn primary', disabled: !!g.busy, onclick: () => run('codegraph.index', g.db) }, 'Index now'),
        h('button', { class: 'btn', disabled: !!g.busy, onclick: () => run('codegraph.rebuild', g.db) }, 'Rebuild'),
        h('button', { class: 'btn', onclick: () => run('codegraph.init', g.db) }, 'Assistants…')));
    } else if (!g.statusError && (!s || !s.exists)) {
      const missing = g.targets.filter((t) => t.missing);
      card.append(h('p', { class: 'sub' },
        g.targets.length
          ? 'Found ' + g.targets.map((t) => base(t.file)).join(', ') + '. Set the repository up to build its graph and connect your assistants.'
          : 'No target found for this graph.'));
      for (const t of missing) {
        card.append(h('div', { class: 'warn-line' }, base(t.file) + ' needs ' + t.missing + ', which is not on PATH.'));
      }
      card.append(h('div', { class: 'actions', style: { marginTop: '10px', position: 'relative' } },
        h('button', { class: 'btn primary', disabled: !!g.busy || !g.targets.length, onclick: () => run('codegraph.init', g.db) }, 'Set up repository…')));
    }
    return card;
  }

  function subline(g) {
    const s = g.status;
    if (!s || !s.graph) return g.root;
    const parts = ['Built ' + ago(s.graph.indexedAt)];
    if (s.graph.commit) parts.push('at ' + s.graph.commit.slice(0, 7));
    if (s.graph.headMoved && s.graph.head) parts.push('HEAD ' + s.graph.head.slice(0, 7));
    return parts.join(' · ');
  }

  function stat(value, label, title) {
    return h('div', { class: 'stat', title }, h('div', { class: 'value' }, value), h('div', { class: 'label' }, label));
  }

  function daemonCard(g) {
    const d = g.status.daemon;
    let tone = 'muted';
    let title = 'Stopped';
    let meta = g.hasTarget ? 'Start it to re-index on every save and answer the live tools.' : 'No target to watch.';
    let dotClass = 'dot';
    if (d) {
      if (d.loading) { tone = 'blue'; title = 'Loading the solution'; dotClass = 'dot spin'; }
      else if (d.indexing || d.pending > 0) { tone = 'blue'; title = 'Indexing'; dotClass = 'dot spin'; }
      else { tone = 'green'; title = 'Watching'; dotClass = 'dot pulse'; }
      const parts = [count(d.projects) + (d.projects === 1 ? ' project' : ' projects'), count(d.documents) + ' docs'];
      if (d.workingSetBytes) parts.push(bytes(d.workingSetBytes));
      if (d.lastIndexedAt) parts.push('indexed ' + ago(d.lastIndexedAt));
      meta = parts.join(' · ') + (g.managed ? '' : ' · started elsewhere');
    }
    const buttons = d
      ? [iconButton('restart', 'Restart the daemon', () => run('codegraph.restartDaemon', g.db)),
         iconButton('stop', 'Stop the daemon', () => run('codegraph.stopDaemon', g.db))]
      : [iconButton('play', 'Start the daemon', () => run('codegraph.startDaemon', g.db))];
    buttons.push(iconButton('log', 'Show the daemon log', () => run('codegraph.showDaemonLog', g.db)));
    return h('section', { class: 'card' },
      h('h2', {}, 'Live daemon'),
      h('div', { class: 'daemon tone-' + tone },
        h('span', { class: dotClass }),
        h('div', { class: 'grow' },
          h('div', { class: 'state' }, title),
          h('div', { class: 'meta', title: d ? 'pid ' + d.pid + ' · ' + d.solution : '' }, meta)),
        h('div', { class: 'actions', style: { gap: '2px', flexWrap: 'nowrap' } }, buttons)));
  }

  function staleCard(g) {
    const s = g.status.graph;
    const shown = [...s.changedFiles.map((f) => [f, 'edited']), ...s.missingFiles.map((f) => [f, 'deleted'])].slice(0, 6);
    const rest = s.changedCount + s.missingCount - shown.length;
    const relative = (f) => f.startsWith(g.root) ? f.slice(g.root.length + 1) : f;
    return h('section', { class: 'card' },
      h('h2', {}, 'Changed since indexing', h('span', { class: 'pill tone-yellow' }, count(s.changedCount + s.missingCount))),
      h('ul', { class: 'files' },
        shown.map(([f, how]) => h('li', { title: f },
          h('span', {}, base(f)),
          h('span', { class: 'path' }, relative(f).slice(0, -base(f).length).replace(/[\\/]$/, '') + (how === 'deleted' ? ' · deleted' : '')))),
        rest > 0 ? h('li', { class: 'muted' }, '+' + count(rest) + ' more') : null),
      g.status.daemon ? null : h('div', { class: 'actions', style: { marginTop: '8px' } },
        h('button', { class: 'btn primary', disabled: !!g.busy, onclick: () => run('codegraph.index', g.db) }, 'Index now')));
  }

  function languagesCard(graph) {
    const total = graph.languages.reduce((n, l) => n + l.nodes, 0) || 1;
    const color = (i) => 'var(' + palette[i % palette.length] + ')';
    const warnings = graph.languages.filter((l) => (l.compileErrors || 0) + (l.languageVersionErrors || 0) > 0);
    return h('section', { class: 'card' },
      h('h2', {}, 'Languages'),
      h('div', { class: 'bar', role: 'img', 'aria-label': graph.languages.map((l) => (languageNames[l.name] || l.name) + ' ' + l.nodes + ' nodes').join(', ') },
        graph.languages.map((l, i) => h('span', { title: (languageNames[l.name] || l.name) + ': ' + count(l.nodes) + ' nodes', style: { flexGrow: String(l.nodes / total), background: color(i) } }))),
      h('div', { class: 'legend' },
        graph.languages.map((l, i) => h('span', { class: 'item', title: [l.compiler, l.languageVersions && 'code uses ' + l.languageVersions].filter(Boolean).join(' · ') },
          h('span', { class: 'dot', style: { background: color(i) } }),
          languageNames[l.name] || l.name,
          h('span', { class: 'count' }, count(l.nodes, true) + ' · ' + count(l.files) + ' files')))),
      warnings.map((l) => h('div', { class: 'warn-line', title: l.firstLanguageVersionError || l.firstCompileError || '' },
        '⚠ ' + (languageNames[l.name] || l.name) + ': ' +
        [l.compileErrors ? l.compileErrors + ' compile error(s)' : '', l.languageVersionErrors ? l.languageVersionErrors + ' need a newer language version' : ''].filter(Boolean).join(', '))));
  }

  function usageCard(g) {
    const u = g.status.usage;
    if (!u) {
      return h('section', { class: 'card' },
        h('h2', {}, 'This week'),
        h('div', { class: 'muted' }, 'No tool calls yet. Ask your assistant a question about the code; each call it makes to codegraph is counted here.'));
    }
    const max = Math.max(1, ...u.topTools.map((t) => t.calls));
    const biggest = Math.max(1, u.sourceTokens, u.resultTokens);
    const compare = (label, value, cls) => [
      h('span', { class: 'name plain' }, label),
      h('div', { class: 'track' }, h('div', { class: 'fill ' + cls, style: { width: Math.max(2, (value / biggest) * 100) + '%' } })),
      h('span', { class: 'n' }, count(value, true)),
    ];
    return h('section', { class: 'card', title: u.line },
      h('h2', {}, 'This week', iconButton('chart', 'Show usage', () => run('codegraph.showUsage', g.db))),
      h('div', { class: 'big' }, count(u.calls), h('small', {}, u.calls === 1 ? 'tool call answered' : 'tool calls answered')),
      h('div', { class: 'muted', style: { fontSize: '12px', marginTop: '3px' } },
        [u.errors ? count(u.errors) + ' failed' : '', u.allTimeCalls > u.calls ? count(u.allTimeCalls) + ' since ' + u.firstCallAt.slice(0, 10) : 'first call ' + u.firstCallAt.slice(0, 10)].filter(Boolean).join(' \u00b7 ')),
      h('h2', { style: { marginTop: '12px', marginBottom: '6px' } }, 'Size in tokens'),
      h('div', { class: 'tools', title: 'The files the answers pointed at, and the answers themselves, measured in tokens' },
        compare('files pointed at', u.sourceTokens, 'source'),
        compare('answers', u.resultTokens, 'result')),
      h('h2', { style: { marginTop: '12px', marginBottom: '6px' } }, 'Most used'),
      h('div', { class: 'tools' },
        u.topTools.map((t) => [
          h('span', { class: 'name', title: t.tool }, t.tool),
          h('div', { class: 'track' }, h('div', { class: 'fill', style: { width: Math.max(4, (t.calls / max) * 100) + '%' } })),
          h('span', { class: 'n' }, count(t.calls)),
        ])));
  }

  function footer(state) {
    const update = state.latest && state.version && newer(state.latest, state.version);
    return h('div', { class: 'footer' },
      h('span', {}, state.version ? 'codegraph-index ' + state.version : ''),
      update
        ? h('a', { onclick: () => run('codegraph.install'), title: 'Install ' + state.latest }, state.latest.replace(/^v/, '') + ' available → update')
        : h('span', { style: { display: 'inline-flex' } }, iconButton('refresh', 'Refresh', () => run('codegraph.refresh'))));
  }

  /** @param {string} a @param {string} b */
  function newer(a, b) {
    const p = (v) => v.replace(/^v/, '').split(/[-+]/)[0].split('.').map((x) => parseInt(x, 10) || 0);
    const [x, y] = [p(a), p(b)];
    for (let i = 0; i < Math.max(x.length, y.length); i++) {
      if ((x[i] || 0) !== (y[i] || 0)) return (x[i] || 0) > (y[i] || 0);
    }
    return false;
  }

  window.addEventListener('message', (event) => {
    if (event.data && event.data.type === 'state') {
      vscode.setState(event.data.state);
      render(event.data.state);
    }
  });
  const saved = vscode.getState();
  if (saved) render(saved);
  vscode.postMessage({ type: 'ready' });
})();
