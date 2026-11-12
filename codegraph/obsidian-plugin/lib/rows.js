'use strict';

// Parses the text the codegraph MCP tools return. Node rows share one shape (MCP-SERVER.md, "How answers look"):
//   kind  full name  [extra]  file:start-end  [id]  [STALE]
// Fields are separated by two spaces; the file and the extra are optional. Anything else is a plain text line.

const KINDS = new Set(['project', 'file', 'type', 'method', 'property', 'field', 'event', 'external']);
const LOCATION = /^(.*):(\d+)-(\d+)$/;

/** @typedef {{ kind: string, fullName: string, extra: string | null, file: string | null, startLine: number | null, endLine: number | null, id: string, stale: boolean }} Row */

/** @returns {Row | null} */
function parseRow(line) {
  let rest = line.replace(/\s+$/, '');
  let stale = false;
  if (rest.endsWith('  STALE')) {
    stale = true;
    rest = rest.slice(0, -'  STALE'.length);
  }

  if (!rest.endsWith(']')) {
    return null;
  }

  const open = rest.lastIndexOf('  [');
  if (open < 0) {
    return null;
  }

  const id = rest.slice(open + 3, -1);
  const parts = rest.slice(0, open).split('  ');
  if (parts.length < 2 || !KINDS.has(parts[0]) || id.length === 0) {
    return null;
  }

  let file = null;
  let startLine = null;
  let endLine = null;
  let last = parts.length;
  const location = parts.length > 2 ? LOCATION.exec(parts[parts.length - 1]) : null;
  if (location) {
    file = location[1];
    startLine = Number(location[2]);
    endLine = Number(location[3]);
    last--;
  }

  const extra = last > 2 ? parts.slice(2, last).join('  ') : null;
  return { kind: parts[0], fullName: parts[1], extra, file, startLine, endLine, id, stale };
}

/**
 * Splits a tool answer into its staleness warning (the first block when the index is out of date), node rows and
 * the other lines, kept in order so a panel can show headers and summaries between rows.
 * @returns {{ stale: string | null, items: Array<{ row: Row } | { text: string }> }}
 */
function parseAnswer(text) {
  let stale = null;
  const items = [];
  for (const line of String(text).split(/\r?\n/)) {
    if (line.trim().length === 0) {
      continue;
    }

    if (stale === null && items.length === 0 && line.startsWith('⚠ STALE INDEX')) {
      stale = line;
      continue;
    }

    const row = parseRow(line);
    items.push(row ? { row } : { text: line });
  }

  return { stale, items };
}

module.exports = { parseRow, parseAnswer };
