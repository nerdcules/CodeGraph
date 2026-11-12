// Installs the plugin into a vault: node install-plugin.mjs <vault folder>
// Builds main.js, then copies it with manifest.json and styles.css to <vault>/.obsidian/plugins/codegraph/.
// Settings already there (data.json) are kept. Enable it in Obsidian under Settings → Community plugins.

import { copyFileSync, existsSync, mkdirSync, readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = dirname(fileURLToPath(import.meta.url));
const vault = process.argv[2];
if (!vault) {
  console.error('usage: node install-plugin.mjs <vault folder>');
  process.exit(1);
}

const vaultDir = resolve(vault);
if (!existsSync(join(vaultDir, '.obsidian'))) {
  console.error(`${vaultDir} has no .obsidian folder: open it as a vault in Obsidian once first`);
  process.exit(1);
}

execFileSync(process.execPath, [join(root, 'build.mjs')], { stdio: 'inherit' });
const { id } = JSON.parse(readFileSync(join(root, 'manifest.json'), 'utf8'));
const target = join(vaultDir, '.obsidian', 'plugins', id);
mkdirSync(target, { recursive: true });
for (const file of ['main.js', 'manifest.json', 'styles.css']) {
  copyFileSync(join(root, file), join(target, file));
}

console.log(`installed to ${target}; enable "CodeGraph" under Settings → Community plugins`);
