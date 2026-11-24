#!/usr/bin/env node
// Compares two skarp JSON documents (analyze, hotspots or history output) after removing run-time fields, so a JIT
// Build and a Native AOT build can be checked for identical results.
// Usage: node tools/ci/compare-json.mjs <a.json> <b.json> [--show <n>]
// Exit codes: 0 equal, 1 different, 2 usage or read error.
//
// Ignored (they describe the run, not the code): run.durationSeconds, run.peakWorkingSetBytes, run.runtime ("... (JIT)" or
// "... (AOT)"), run.root, run.git.repositoryRoot and baseline.path (absolute paths), run.git.durationSeconds; run.timings (semantic file) and
// run.semantic.outputs (output file paths). Everything else, including
// the tool version, parse options, parallelism, git version, window and notices, must be equal.
import fs from 'node:fs';

const args = process.argv.slice(2);
if (args.length < 2) {
  console.error('usage: node compare-json.mjs <a.json> <b.json> [--show <n>]');
  process.exit(2);
}
const showAt = args.indexOf('--show');
const show = showAt >= 0 ? Number(args[showAt + 1]) : 20;

const IGNORED = [
  ['run', 'durationSeconds'],
  ['run', 'peakWorkingSetBytes'],
  ['run', 'runtime'],
  ['run', 'root'],
  ['run', 'git', 'repositoryRoot'],
  ['run', 'git', 'durationSeconds'],
  ['baseline', 'path'],
  // The semantic file's stage times, and the syntax header's list of semantic output files (paths differ per build).
  ['run', 'timings'],
  ['run', 'semantic', 'outputs'],
];

function load(file) {
  try {
    const doc = JSON.parse(fs.readFileSync(file, 'utf8'));
    for (const p of IGNORED) {
      let o = doc;
      for (const k of p.slice(0, -1)) o = o?.[k];
      if (o && typeof o === 'object') delete o[p[p.length - 1]];
    }
    return doc;
  } catch (e) {
    console.error(`cannot read ${file}: ${e.message}`);
    process.exit(2);
  }
}

const diffs = [];
function compare(a, b, path) {
  if (diffs.length > 10000) return;
  if (a === b) return;
  if (typeof a !== typeof b || a === null || b === null || typeof a !== 'object' || Array.isArray(a) !== Array.isArray(b)) {
    diffs.push(`${path}: ${JSON.stringify(a)} vs ${JSON.stringify(b)}`);
    return;
  }
  if (Array.isArray(a)) {
    if (a.length !== b.length) diffs.push(`${path}: array length ${a.length} vs ${b.length}`);
    for (let i = 0; i < Math.min(a.length, b.length); i++) compare(a[i], b[i], `${path}[${i}]`);
    return;
  }
  const keys = new Set([...Object.keys(a), ...Object.keys(b)]);
  for (const k of keys) {
    if (!(k in a)) diffs.push(`${path}.${k}: missing in first`);
    else if (!(k in b)) diffs.push(`${path}.${k}: missing in second`);
    else compare(a[k], b[k], `${path}.${k}`);
  }
}

const [fa, fb] = args;
compare(load(fa), load(fb), '$');
if (diffs.length === 0) {
  console.log(`equal (run-time fields ignored): ${fa} = ${fb}`);
  process.exit(0);
}
console.log(`${diffs.length}${diffs.length > 10000 ? '+' : ''} differences between ${fa} and ${fb}; first ${Math.min(show, diffs.length)}:`);
for (const d of diffs.slice(0, show)) console.log(`  ${d}`);
process.exit(1);
