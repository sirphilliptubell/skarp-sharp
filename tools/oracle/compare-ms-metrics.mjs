#!/usr/bin/env node
// Compares skarp output (JSON) with Microsoft.CodeAnalysis.Metrics output (XML) member by member.
// Usage: node compare-ms-metrics.mjs <ms.xml> <skarp.json> [--diffs <out.json>] [--show <n>] [--expected <out.json>]
// See tools/oracle/README.md for the matching rule and how to produce the inputs.
import fs from 'node:fs';
import path from 'node:path';

const args = process.argv.slice(2);
if (args.length < 2) {
  console.error('usage: node compare-ms-metrics.mjs <ms.xml> <skarp.json> [--diffs <out.json>] [--show <n>] [--expected <out.json>]');
  process.exit(2);
}
const [msPath, oursPath] = args;
const opt = (name, def) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : def; };
const diffsPath = opt('--diffs', null);
const show = Number(opt('--show', '15'));
const expectedPath = opt('--expected', null);

const norm = p => p.replace(/\//g, '\\').toLowerCase();
const decode = s => s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&amp;/g, '&');

// ---- Microsoft XML -------------------------------------------------------------------------
function readMs(xml) {
  const types = [];
  for (const m of xml.matchAll(/<NamedType Name="([^"]*)">/g)) types.push({ pos: m.index, name: decode(m[1]) });
  const items = [];
  const re = /<(Method|Property|Event|Field) Name="([^"]*)" File="([^"]*)" Line="(\d+)">\s*<Metrics>([\s\S]*?)<\/Metrics>/g;
  let t = 0;
  for (const m of xml.matchAll(re)) {
    while (t + 1 < types.length && types[t + 1].pos < m.index) t++;
    const metrics = {};
    for (const mm of m[5].matchAll(/<Metric Name="(\w+)" Value="(-?\d+)" \/>/g)) metrics[mm[1]] = Number(mm[2]);
    items.push({ element: m[1], name: decode(m[2]), file: norm(decode(m[3])), line: Number(m[4]), type: types[t]?.name ?? '', ...metrics });
  }
  return items;
}

const ACCESSOR = /\.(get|set|init|add|remove)$/;

// Simple member name from a Microsoft display name, e.g. "int C.M<T>(int a)" -> "M", "C.C()" -> ".ctor".
function msSimpleName(item) {
  let s = item.name;
  const acc = s.match(ACCESSOR);
  if (acc) return { accessor: acc[1], simple: null };
  const paren = s.indexOf('(');
  if (paren >= 0) s = s.slice(0, paren);
  // last '.' outside angle brackets
  let depth = 0, cut = -1;
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (c === '<') depth++; else if (c === '>') depth--; else if (c === '.' && depth === 0) cut = i;
  }
  let simple = s.slice(cut + 1).trim();
  if (/^(implicit|explicit) operator /.test(simple) || simple.startsWith('operator ')) return { accessor: null, simple };
  simple = simple.replace(/<.*>$/, '');
  const typeSimple = item.type.split('.').pop().replace(/<.*>$/, '');
  if (simple === typeSimple) simple = '.ctor';
  else if (simple === '~' + typeSimple) simple = '~';
  return { accessor: null, simple };
}

// ---- skarp JSON ------------------------------------------------------------------------------
function readOurs(json) {
  const root = json.run.root;
  const byFile = new Map();
  const all = [];
  for (const f of json.files) {
    const abs = norm(path.win32.join(root, f.path));
    const list = [];
    for (const m of f.members) {
      const rec = { ...m, file: abs, relPath: f.path };
      all.push(rec);
      list.push(rec);
    }
    byFile.set(abs, list);
  }
  // fold: own statements plus all nested descendants (lambdas, local functions)
  const byId = new Map(all.map(r => [r.id, r]));
  for (const r of all) r.statementsFolded = r.statements;
  for (const r of all) {
    let p = r.parent ? byId.get(r.parent) : null;
    while (p) { p.statementsFolded += r.statements; p = p.parent ? byId.get(p.parent) : null; }
  }
  return { byFile, all };
}

// Estimated Microsoft SourceLines from our span: Microsoft counts the declaration's full span, i.e. it adds the
// non-blank part of the leading trivia (doc comments, comments, directives, disabled text) above the declaration.
function leadingTriviaLines(fileText, startLine) {
  if (!fileText) return 0;
  let i = startLine - 2; // 0-based index of the line above the declaration
  let count = 0, pendingBlank = 0;
  while (i >= 0) {
    const l = fileText[i].trim();
    if (l === '') { pendingBlank++; i--; continue; }
    const trivia = l.startsWith('//') || l.startsWith('/*') || l.startsWith('*') || l.endsWith('*/') || l.startsWith('#');
    if (!trivia) break;
    count += pendingBlank + 1; pendingBlank = 0; i--;
  }
  return count;
}

const ms = readMs(fs.readFileSync(msPath, 'utf8'));
const ours = readOurs(JSON.parse(fs.readFileSync(oursPath, 'utf8')));
const fileCache = new Map();
const lines = f => { if (!fileCache.has(f)) { try { fileCache.set(f, fs.readFileSync(f, 'utf8').split(/\r?\n/)); } catch { fileCache.set(f, null); } } return fileCache.get(f); };

const msMethods = ms.filter(x => x.element === 'Method');
const matched = [];
const unmatchedMs = [];
const used = new Set();
let nameMismatch = 0;
for (const x of msMethods) {
  const list = ours.byFile.get(x.file) ?? [];
  const { accessor, simple } = msSimpleName(x);
  let cands = list.filter(r => !r.parent && r.startLine <= x.line && x.line <= r.endLine);
  cands = accessor ? cands.filter(r => r.name.endsWith('.' + accessor)) : cands.filter(r => !ACCESSOR.test(r.name));
  if (cands.length > 1 && simple) {
    const named = cands.filter(r => r.name === simple);
    if (named.length) cands = named;
  }
  cands.sort((a, b) => (a.endLine - a.startLine) - (b.endLine - b.startLine));
  const r = cands.find(c => !used.has(c.id));
  if (!r) { unmatchedMs.push(x); continue; }
  used.add(r.id);
  if (simple && r.name !== simple) nameMismatch++;
  matched.push({ ms: x, ours: r });
}
const unmatchedOurs = ours.all.filter(r => !r.parent && !used.has(r.id));

// ---- compare -------------------------------------------------------------------------------
const pre = p => (p.ours.lines.directive + p.ours.lines.inactive) > 0;
const clean = matched.filter(p => !pre(p));
const metrics = [
  ['CyclomaticComplexity', 'cyclomaticMicrosoft', p => p.ours.cyclomaticMicrosoft],
  ['ExecutableLines', 'statements (own)', p => p.ours.statements],
  ['ExecutableLines', 'statements folded (own + nested bodies)', p => p.ours.statementsFolded],
  ['SourceLines', 'lines.physical', p => p.ours.lines.physical],
  ['SourceLines', 'lines.physical + leading trivia (estimate)', p => p.ours.lines.physical + (/Expression$/.test(p.ours.kind) ? 0 : leadingTriviaLines(lines(p.ours.file), p.ours.startLine))],
];

const out = { summary: {}, metrics: {} };
console.log(`Microsoft: ${ms.length} elements (${msMethods.length} methods/accessors, ${ms.filter(x => x.element === 'Property').length} properties, ${ms.filter(x => x.element === 'Event').length} events, ${ms.filter(x => x.element === 'Field').length} fields)`);
console.log(`skarp: ${ours.all.length} records (${ours.all.filter(r => !r.parent).length} members, ${ours.all.filter(r => r.parent).length} nested bodies)`);
console.log(`matched ${matched.length}; unmatched Microsoft methods ${unmatchedMs.length}; unmatched skarp members ${unmatchedOurs.length}; name check mismatches ${nameMismatch}`);
console.log(`matched members touching #if/inactive lines (reported separately): ${matched.length - clean.length}`);
out.summary = { msElements: ms.length, msMethods: msMethods.length, oursMembers: ours.all.filter(r => !r.parent).length, matched: matched.length, unmatchedMs: unmatchedMs.length, unmatchedOurs: unmatchedOurs.length, nameMismatch, preprocessorAffected: matched.length - clean.length };

for (const [msKey, label, get] of metrics) {
  const rows = clean.map(p => ({ p, a: p.ms[msKey], b: get(p) }));
  const eq = rows.filter(r => r.a === r.b).length;
  const rowsPre = matched.filter(pre).map(p => ({ p, a: p.ms[msKey], b: get(p) }));
  const eqPre = rowsPre.filter(r => r.a === r.b).length;
  const pct = (n, d) => d ? (100 * n / d).toFixed(1) + '%' : 'n/a';
  console.log(`\n${msKey} vs ${label}: ${eq}/${rows.length} equal (${pct(eq, rows.length)}); with #if: ${eqPre}/${rowsPre.length}`);
  const diffs = rows.filter(r => r.a !== r.b);
  const delta = new Map();
  for (const d of diffs) delta.set(d.a - d.b, (delta.get(d.a - d.b) ?? 0) + 1);
  if (diffs.length) console.log('  ms - ours: ' + [...delta].sort((x, y) => y[1] - x[1]).map(([k, v]) => `${k > 0 ? '+' : ''}${k}:${v}`).join(' '));
  for (const d of diffs.slice(0, show)) console.log(`  ${d.p.ours.relPath}:${d.p.ms.line} ${d.p.ours.id}  ms=${d.a} ours=${d.b}`);
  out.metrics[`${msKey} vs ${label}`] = { equal: eq, total: rows.length, equalWithIf: eqPre, totalWithIf: rowsPre.length,
    diffs: diffs.map(d => ({ file: d.p.ours.relPath, line: d.p.ms.line, id: d.p.ours.id, msName: d.p.ms.name, ms: d.a, ours: d.b })),
    diffsWithIf: rowsPre.filter(r => r.a !== r.b).map(d => ({ file: d.p.ours.relPath, line: d.p.ms.line, id: d.p.ours.id, ms: d.a, ours: d.b })) };
}

// Unmatched members: classify by cause. A line inside any #if ... #endif region (simple text scan) points at the
// preprocessor (skarp parses with no symbols, Microsoft with the project's symbols); CC 1 and 0 executable lines on the
// Microsoft side points at a member without body (auto-accessor, abstract, interface, extern, partial definition).
function ifDepthByLine(text) {
  if (!text) return null;
  const depth = []; let d = 0;
  for (const raw of text) {
    const l = raw.trim();
    if (/^#\s*if\b/.test(l)) { d++; depth.push(d); continue; }
    if (/^#\s*endif\b/.test(l)) { depth.push(d); d = Math.max(0, d - 1); continue; }
    depth.push(d);
  }
  return depth;
}
const depthCache = new Map();
const inIf = (file, line) => { if (!depthCache.has(file)) depthCache.set(file, ifDepthByLine(lines(file))); const d = depthCache.get(file); return !!d && (d[line - 1] ?? 0) > 0; };
const classifyMs = x => inIf(x.file, x.line) ? 'inside #if region' : (x.CyclomaticComplexity === 1 && x.ExecutableLines === 0 ? 'no body (CC 1, 0 executable lines)' : 'other');
const classifyOurs = r => inIf(r.file, r.startLine) ? 'inside #if region' : 'other';
const tally = (xs, f) => { const m = new Map(); for (const x of xs) m.set(f(x), (m.get(f(x)) ?? 0) + 1); return Object.fromEntries(m); };
console.log('\nUnmatched Microsoft methods by cause: ' + JSON.stringify(tally(unmatchedMs, classifyMs)));
console.log('Unmatched skarp members by cause: ' + JSON.stringify(tally(unmatchedOurs, classifyOurs)));
out.summary.unmatchedMsByCause = tally(unmatchedMs, classifyMs);
out.summary.unmatchedOursByCause = tally(unmatchedOurs, classifyOurs);
for (const x of unmatchedMs.filter(x => classifyMs(x) === 'other').slice(0, show)) console.log(`  ms other: ${x.name} @${x.line} CC=${x.CyclomaticComplexity} EL=${x.ExecutableLines}`);
for (const r of unmatchedOurs.filter(r => classifyOurs(r) === 'other').slice(0, show)) console.log(`  skarp other: ${r.id} @${r.startLine} kind=${r.kind}`);

console.log(`\nUnmatched Microsoft methods (first ${show}):`);
for (const x of unmatchedMs.slice(0, show)) console.log(`  ${x.name} @${x.line} CC=${x.CyclomaticComplexity} EL=${x.ExecutableLines}`);
console.log(`Unmatched skarp members (first ${show}):`);
for (const r of unmatchedOurs.slice(0, show)) console.log(`  ${r.id} @${r.startLine} kind=${r.kind}`);
out.unmatchedMs = unmatchedMs.map(x => ({ name: x.name, file: x.file, line: x.line, cc: x.CyclomaticComplexity, el: x.ExecutableLines }));
out.unmatchedOurs = unmatchedOurs.map(r => ({ id: r.id, line: r.startLine, kind: r.kind }));
if (diffsPath) fs.writeFileSync(diffsPath, JSON.stringify(out, null, 1));

// --expected <file>: writes Microsoft's cyclomatic value per matched skarp member, the data of the regression test
// tests/SkarpSharp.Tests/MicrosoftOracleTests.cs (README section 5). Members with a different skarp value are written with
// "knownDifference": { "skarp": <value>, "reason": "TODO" }; replace TODO with the documented reason, or fix the code.
if (expectedPath) {
  const members = matched
    .map(p => ({ file: p.ours.relPath.replace(/\\/g, '/'), id: p.ours.id, microsoftName: p.ms.name, microsoftLine: p.ms.line,
      cyclomatic: p.ms.CyclomaticComplexity,
      ...(p.ms.CyclomaticComplexity !== p.ours.cyclomaticMicrosoft ? { knownDifference: { skarp: p.ours.cyclomaticMicrosoft, reason: 'TODO' } } : {}) }))
    .sort((a, b) => a.file < b.file ? -1 : a.file > b.file ? 1 : a.microsoftLine - b.microsoftLine || (a.id < b.id ? -1 : 1));
  const doc = {
    description: 'Expected Microsoft.CodeAnalysis.Metrics CyclomaticComplexity per skarp member (cyclomaticMicrosoft). Generated by tools/oracle/compare-ms-metrics.mjs --expected; see tools/oracle/README.md section 5.',
    engine: 'Microsoft.CodeAnalysis.Metrics 5.6.0 (Metrics.exe 5.6.0-2.26263.10)',
    microsoftOnly: unmatchedMs.map(x => ({ name: x.name, line: x.line, cyclomatic: x.CyclomaticComplexity })),
    skarpOnly: unmatchedOurs.map(r => r.id),
    members,
  };
  fs.writeFileSync(expectedPath, JSON.stringify(doc, null, 2) + '\n');
  console.log(`\nwrote ${members.length} expected values (${members.filter(m => m.knownDifference).length} differences) to ${expectedPath}`);
}
