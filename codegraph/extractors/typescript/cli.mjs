#!/usr/bin/env node
// codegraph-ts: TypeScript and Angular extractor for CodeGraph.
//
//   codegraph-ts <tsconfig.json | angular.json | nx.json | folder> [--out records.ndjson]
//
// Prints the graph as newline-delimited JSON records (CodeGraph's EXTRACTORS.md contract) on stdout, or into
// --out; progress goes to stderr. codegraph-index runs it and pipes the records into `codegraph-index import`.
import fs from 'node:fs';
import { createRequire } from 'node:module';
import { discover } from './lib/project.mjs';
import { Extractor } from './lib/extract.mjs';

const version = createRequire(import.meta.url)('./package.json').version;
const args = process.argv.slice(2);
if (args[0] === '--version') {
  console.log(version);
  process.exit(0);
}

let target;
let out;
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--out' && args[i + 1]) out = args[++i];
  else if (!args[i].startsWith('-') && !target) target = args[i];
  else {
    console.error(`unexpected argument: ${args[i]}`);
    process.exit(1);
  }
}

if (!target) {
  console.error('usage: codegraph-ts <tsconfig.json | angular.json | nx.json | folder> [--out records.ndjson]');
  process.exit(1);
}

const log = message => process.stderr.write(`codegraph-ts: ${message}\n`);
let workspace;
try {
  workspace = discover(target);
} catch (e) {
  log(e.message);
  process.exit(1);
}

const started = Date.now();
const extractor = new Extractor({ root: workspace.root, log });
for (const config of workspace.configs) extractor.runConfig(config);

// Written in chunks with writeSync: a large workspace is hundreds of MB of records, and process.stdout.write
// would buffer all of it in memory when the reader is slower.
const fd = out ? fs.openSync(out, 'w') : 1;
let chunk = [];
let size = 0;
const flush = () => {
  if (chunk.length === 0) return;
  const buffer = Buffer.from(chunk.join(''));
  for (let offset = 0; offset < buffer.length;) {
    try {
      offset += fs.writeSync(fd, buffer, offset);
    } catch (e) {
      if (e.code !== 'EAGAIN') throw e; // a non-blocking pipe that is full: try again
    }
  }
  chunk = [];
  size = 0;
};
let records = 0;
for (const record of extractor.records({ target: workspace.target, tool: `codegraph-ts ${version}` })) {
  const line = JSON.stringify(record) + '\n';
  chunk.push(line);
  size += line.length;
  records++;
  if (size > 1 << 20) flush();
}
flush();
if (out) fs.closeSync(fd);
log(`${extractor.walked.size} file(s), ${extractor.nodes.size} nodes, ${extractor.edges.size} edges, ${records} records in ${((Date.now() - started) / 1000).toFixed(1)}s`);
