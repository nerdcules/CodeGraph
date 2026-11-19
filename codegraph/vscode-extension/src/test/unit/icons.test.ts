// The extension's icons must show the CodeGraph logo (../assets/logo.svg). scripts/icons.js makes them from the logo;
// these tests fail if the logo changes and nobody runs the script, or if somebody edits an icon by hand.
import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

const extensionDir = path.resolve(__dirname, '..', '..', '..');
const media = path.join(extensionDir, 'media');
const icons = require(path.join(extensionDir, 'scripts', 'icons.js')) as {
  readLogo(): string;
  activitySvg(logo: string): string;
  pngInfo(png: Buffer): { width: number; height: number; colourType: number; source?: string };
  sourceText(logo: string): string;
};
const lf = (file: string) => fs.readFileSync(file, 'utf8').replace(/\r\n/g, '\n');
const again = 'Run `node scripts/icons.js --browser <Chrome or Edge>` to make the icons from assets/logo.svg again.';

test('media/activity.svg is the logo as a one-colour mask', () => {
  assert.equal(lf(path.join(media, 'activity.svg')), icons.activitySvg(icons.readLogo()), again);
});

// The pixels are not checked here: that needs a browser. The script records the logo it rendered.
test('media/icon.png is 256 px and records the current logo', () => {
  const info = icons.pngInfo(fs.readFileSync(path.join(media, 'icon.png')));
  assert.deepEqual([info.width, info.height, info.colourType], [256, 256, 6]);
  assert.equal(info.source, icons.sourceText(icons.readLogo()), again);
});

interface El { tag: string; attrs: Record<string, string>; children: El[] }

/** The ids in the Overview's logo have a per-copy prefix; the logo file uses the bare names. */
const bareIds = (v: string) => v.replace(/cg-logo-\d+-/g, '');

/** The elements of an SVG text, with no comments, title or desc. */
function parseSvg(text: string): El {
  const stack: El[] = [{ tag: '#root', attrs: {}, children: [] }];
  const clean = text.replace(/<!--[\s\S]*?-->/g, '').replace(/<(title|desc)>[\s\S]*?<\/\1>/g, '');
  for (const m of clean.matchAll(/<(\/?)([\w:]+)([^>]*?)(\/?)>/g)) {
    if (m[1]) { stack.pop(); continue; }
    const attrs: Record<string, string> = {};
    for (const a of m[3].matchAll(/([\w:-]+)="([^"]*)"/g)) attrs[a[1]] = bareIds(a[2]);
    const el: El = { tag: m[2], attrs, children: [] };
    stack[stack.length - 1].children.push(el);
    if (!m[4]) stack.push(el);
  }
  return stack[0].children[0];
}

test('the Overview draws the same elements as the logo', () => {
  const source = fs.readFileSync(path.join(media, 'overview.js'), 'utf8');
  const start = source.indexOf('  let logoCount = 0;');
  const end = source.indexOf('\n  }\n', start);
  assert.ok(start >= 0 && end > start, 'media/overview.js has no logo() function');
  const svgEl = (tag: string, attrs: Record<string, string | number>, children: El[] = []): El =>
    ({ tag, attrs: Object.fromEntries(Object.entries(attrs).map(([k, v]) => [k, bareIds(String(v))])), children });
  const logo = new Function('svgEl', `${source.slice(start, end + 4)}\nreturn logo();`)(svgEl) as El;
  const want = parseSvg(icons.readLogo());
  assert.equal(logo.attrs.viewBox, want.attrs.viewBox);
  assert.deepEqual(logo.children, want.children);
});
