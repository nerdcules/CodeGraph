'use strict';

// The exporter's manifest (.codegraph-export.json beside the notes) maps every node id to its note and heading:
//   { version, linkRoot, commit, indexedAt, files: [...], nodes: { "<id>": { path: "Types/App/App.Store.md", heading: "Save" | null } } }

/** The link text Obsidian resolves for a node, or null when the node has no note. */
function linkText(manifest, id) {
  const ref = manifest && manifest.nodes ? manifest.nodes[id] : undefined;
  if (!ref) {
    return null;
  }

  const note = (manifest.linkRoot ? manifest.linkRoot + '/' : '') + ref.path.replace(/\.md$/, '');
  return ref.heading ? `${note}#${ref.heading}` : note;
}

/** The vault path of a node's note, or null. */
function notePath(manifest, id) {
  const ref = manifest && manifest.nodes ? manifest.nodes[id] : undefined;
  return ref ? (manifest.linkRoot ? manifest.linkRoot + '/' : '') + ref.path : null;
}

/** A wikilink to a node, labelled, or null. */
function wikilink(manifest, id, label) {
  const target = linkText(manifest, id);
  if (!target) {
    return null;
  }

  const safe = String(label || '').replace(/\|/g, '∣').replace(/\[/g, '(').replace(/\]/g, ')');
  return safe ? `[[${target}|${safe}]]` : `[[${target}]]`;
}

/**
 * The node a note (vault path) and heading stand for: the member under that heading, else the note's own node.
 * @returns {string | null}
 */
function idAt(manifest, vaultPath, heading) {
  if (!manifest || !manifest.nodes) {
    return null;
  }

  const relative = stripRoot(manifest, vaultPath);
  let owner = null;
  for (const [id, ref] of Object.entries(manifest.nodes)) {
    if (ref.path !== relative) {
      continue;
    }

    if (heading && ref.heading === heading) {
      return id;
    }

    if (ref.heading === null || ref.heading === undefined) {
      owner = id;
    }
  }

  return owner;
}

/** The members of a note, in heading order as the manifest lists them. */
function membersOf(manifest, vaultPath) {
  if (!manifest || !manifest.nodes) {
    return [];
  }

  const relative = stripRoot(manifest, vaultPath);
  return Object.entries(manifest.nodes)
    .filter(([, ref]) => ref.path === relative && ref.heading)
    .map(([id, ref]) => ({ id, heading: ref.heading }));
}

function stripRoot(manifest, vaultPath) {
  const prefix = manifest.linkRoot ? manifest.linkRoot + '/' : '';
  return prefix && vaultPath.startsWith(prefix) ? vaultPath.slice(prefix.length) : vaultPath;
}

/**
 * An editor URL for a source location. {file} is the path with forward slashes and a leading slash
 * (vscode://file{file}:{line} works on every OS); {line} is the line number.
 */
function editorUrl(template, file, line) {
  let path = String(file).replace(/\\/g, '/');
  if (!path.startsWith('/')) {
    path = '/' + path;
  }

  return template.replace(/\{file\}/g, encodeURI(path)).replace(/\{line\}/g, String(line || 1));
}

module.exports = { linkText, notePath, wikilink, idAt, membersOf, editorUrl };
