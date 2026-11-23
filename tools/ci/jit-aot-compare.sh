#!/usr/bin/env bash
# JIT = AOT output check: runs the JIT build (src/SkarpSharp.Cli Release, via
# dotnet skarp.dll) and a Native AOT build of skarp on the same inputs and compares the JSON with tools/ci/compare-json.mjs,
# which ignores run-time fields (timings, peak memory, runtime name, absolute paths).
#
# Usage: tools/ci/jit-aot-compare.sh <aot skarp executable> <output dir> <edge-case dir> [<git repository>]
#   <edge-case dir>   analyzed with --no-history (tests/oracle/EdgeCases); also with a settings file, and a baseline
#                     written by both builds (equal files) and compared against; --format markdown and
#                     --format github with the gate (text and exit codes equal)
#   <git repository>  optional; analyzed with history: analyze (default window), hotspots --all-history,
#                     analyze --include-tests --preset codescene --complex-metric cyclomatic,
#                     history --all-history --include-bots -w (a real repository with history)
# The semantic tier on this repository (restored by the build): the semantic JSON files of both builds are
#                     compared too (run.timings ignored). The unused-code fixture, restored in a copy, with
#                     --all-targets (one semantic file per framework), and without it (highest target only).
# --format sarif (both tiers in one file, no run-time fields) must be byte-identical: on the edge cases
#                     with a baseline, on this repository with the semantic tier, on the unused-code fixture and on the
#                     git repository.
# Namespace cycles on a fixture with a two-namespace cycle (JSON block, SARIF kind namespace); this repository
#                     with --include-generated-targets and only the namespace rule walking (unused-code rules off).
# The agent loop on copies of the edge cases and the cycles fixture (so .skarp/ is written under the output dir):
#                     analyze (the text report), show (list, detail, JSON) on the saved run, and check --track (plain and
#                     --hook claude) with SKARP_HOME in the output dir (a fresh one per build), text and exit codes equal.
# The code health score in every output above (JSON, Markdown, SARIF artifacts, agent lines), plus a health line
#                     of check --track after a worse edit, show --file, and the edge cases with score settings.
# Duplicate code on a clone fixture (the mutation fixture of tests/SkarpSharp.Tests/Clones/Mutations plus a
#                     suppressed copy and a suppression without justification) with duplicate-code-near on: JSON (clone
#                     blocks, repository.duplication, suppressed), SARIF (related locations), a baseline file, and the
#                     agent summary with the duplicate-code hint.
# The brain composites (brain-method, brain-class, opt-in) on a generated fixture (see the block below).
# Prerequisite: dotnet build src/SkarpSharp.Cli -c Release. Run from the repository root. Exit code 0 when all equal.
set -euo pipefail

if [ $# -lt 3 ]; then
  echo "usage: $0 <aot skarp executable> <output dir> <edge-case dir> [<git repository>]" >&2
  exit 2
fi
aot="$1"; out="$2"; edge="$3"; repo="${4:-}"
jit_dll="src/SkarpSharp.Cli/bin/Release/net10.0/skarp.dll"
[ -f "$jit_dll" ] || { echo "JIT build not found: $jit_dll (run dotnet build src/SkarpSharp.Cli -c Release)" >&2; exit 2; }
[ -x "$aot" ] || [ -f "$aot" ] || { echo "AOT executable not found: $aot" >&2; exit 2; }
mkdir -p "$out"

failed=0
run_pair() {
  local name="$1"; shift
  # Same fixed degree of parallelism for both builds; output is independent of it, but this keeps the runs comparable.
  # --format json: JSON only to the file, the one-line summary with the runtime name on stderr (the human report
  # is the default on stdout).
  dotnet "$jit_dll" "$@" --format json -o "$out/$name.jit.json" 2> "$out/$name.jit.log"
  "$aot" "$@" --format json -o "$out/$name.aot.json" 2> "$out/$name.aot.log"
  grep -q "(AOT)" "$out/$name.aot.log" || { echo "$name: the AOT executable did not report an AOT runtime" >&2; failed=1; }
  if node tools/ci/compare-json.mjs "$out/$name.jit.json" "$out/$name.aot.json"; then :; else failed=1; fi
}

run_pair edge-analyze analyze "$edge" --no-history --parallelism 4
# Settings file (source-generated JSON), baseline write and compare.
printf '{ "version": 1, "preset": "codescene", "thresholds": { "long-method": { "notice": 20 } }, "rules": { "magic-number": false } }
' > "$out/settings.json"
dotnet "$jit_dll" baseline "$edge" --no-history --config "$out/settings.json" --baseline "$out/edge-baseline.jit.json" > /dev/null
"$aot" baseline "$edge" --no-history --config "$out/settings.json" --baseline "$out/edge-baseline.aot.json" > /dev/null
if cmp -s "$out/edge-baseline.jit.json" "$out/edge-baseline.aot.json"; then echo "equal: baseline files"; else echo "baseline files differ" >&2; failed=1; fi
run_pair edge-analyze-settings analyze "$edge" --no-history --config "$out/settings.json" --baseline "$out/edge-baseline.jit.json" --threshold long-method.notice=10
# The Markdown summary and GitHub annotations with the gate (exit code 3 when it trips); the text must be equal.
for fmt in markdown github; do
  args=(analyze "$edge" --no-history --config "$out/settings.json" --baseline "$out/edge-baseline.jit.json" --threshold long-method.notice=10
        --gate --gate-on '*:*' --format "$fmt")
  jit_code=0; aot_code=0
  dotnet "$jit_dll" "${args[@]}" > "$out/edge-$fmt.jit.txt" || jit_code=$?
  "$aot" "${args[@]}" > "$out/edge-$fmt.aot.txt" || aot_code=$?
  if [ "$jit_code" -ne "$aot_code" ] || [ "$jit_code" -eq 1 ] || [ "$jit_code" -eq 2 ]; then echo "$fmt: exit codes $jit_code (JIT) and $aot_code (AOT)" >&2; failed=1; fi
  if cmp -s "$out/edge-$fmt.jit.txt" "$out/edge-$fmt.aot.txt"; then echo "equal: --format $fmt (exit code $jit_code)"; else echo "--format $fmt output differs" >&2; failed=1; fi
done
# SARIF holds no run-time fields, so the files of both builds must be byte-identical (exit codes too).
run_sarif_pair() {
  local name="$1"; shift
  local jit_code=0 aot_code=0
  dotnet "$jit_dll" "$@" --format sarif -o "$out/$name.jit.sarif" 2> "$out/$name.jit.log" || jit_code=$?
  "$aot" "$@" --format sarif -o "$out/$name.aot.sarif" 2> "$out/$name.aot.log" || aot_code=$?
  if [ "$jit_code" -ne "$aot_code" ] || [ "$jit_code" -eq 1 ] || [ "$jit_code" -eq 2 ]; then echo "$name: exit codes $jit_code (JIT) and $aot_code (AOT)" >&2; failed=1; fi
  if cmp -s "$out/$name.jit.sarif" "$out/$name.aot.sarif"; then echo "equal: $name (SARIF, exit code $jit_code)"; else echo "$name: SARIF differs" >&2; failed=1; fi
}
run_sarif_pair edge-sarif analyze "$edge" --no-history --config "$out/settings.json" --baseline "$out/edge-baseline.jit.json" --threshold long-method.notice=10 --parallelism 4
# Semantic tier (this repository must be restored: dotnet restore SkarpSharp.slnx).
run_semantic_pair() {
  local name="$1"; shift
  dotnet "$jit_dll" "$@" --format json -o "$out/$name.jit.json" --semantic-output "$out/$name.jit.semantic.json" 2> "$out/$name.jit.log"
  "$aot" "$@" --format json -o "$out/$name.aot.json" --semantic-output "$out/$name.aot.semantic.json" 2> "$out/$name.aot.log"
  grep -q "skarp semantic: ran" "$out/$name.aot.log" || { echo "$name: the semantic tier did not run (AOT): $(grep 'skarp semantic' "$out/$name.aot.log")" >&2; failed=1; }
  for kind in "" ".semantic"; do
    if node tools/ci/compare-json.mjs "$out/$name.jit$kind.json" "$out/$name.aot$kind.json"; then :; else failed=1; fi
  done
}
run_semantic_pair self-semantic analyze . --no-history --parallelism 4
run_semantic_pair self-semantic-microsoft analyze . --no-history --parallelism 4 --preset microsoft --no-generators
run_sarif_pair self-sarif analyze . --no-history --parallelism 4
grep -q "skarp semantic: ran" "$out/self-sarif.aot.log" || { echo "self-sarif: the semantic tier did not run (AOT)" >&2; failed=1; }
# The cycles fixture (two namespaces that use each other), restored
# in a copy: the semantic JSON's namespaces block and SARIF namespace results.
cycles="$out/cycles-fixture"
rm -rf "$cycles" && mkdir -p "$cycles"
cp -R tests/SkarpSharp.Tests/Semantic/Fixtures/cycles/. "$cycles/"
for f in "$cycles"/*.fixture; do if [ -e "$f" ]; then mv "$f" "${f%.fixture}"; fi; done
dotnet restore "$cycles/Lib.slnx" -nologo > "$out/cycles-fixture-restore.log"
run_semantic_pair cycles-semantic analyze "$cycles" --no-history --no-config --parallelism 4
grep -q '"shortestCycle"' "$out/cycles-semantic.aot.semantic.json" || { echo "cycles fixture: no namespace cycle component (AOT)" >&2; failed=1; }
run_sarif_pair cycles-sarif analyze "$cycles" --no-history --no-config --parallelism 4
grep -q '"ruleId": "namespace-cycle"' "$out/cycles-sarif.aot.sarif" || { echo "cycles fixture: no namespace-cycle result in SARIF (AOT)" >&2; failed=1; }
grep -q '"kind": "namespace"' "$out/cycles-sarif.aot.sarif" || { echo "cycles fixture: no namespace logical location (AOT)" >&2; failed=1; }
# The agent loop. Each build gets its own fresh tracking home, so both start from the same state; show reads
# the run the JIT build saved (the AOT build's analyze then saves an equal one). Times are normalised (none are expected).
agent_norm() { sed -E 's/[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:]{8}Z/<time>/g; s/\([^)]*ago[^)]*\)/(<age>)/g' "$1"; }
agent_pair() {
  local name="$1" dir="$2" input="$3"; shift 3
  local jit_code=0 aot_code=0 input_abs=""
  [ -z "$input" ] || input_abs="$(cd "$(dirname "$input")" && pwd)/$(basename "$input")"
  if [ -n "$input" ]; then
    (cd "$dir" && SKARP_HOME="$out_abs/agent-home.jit" dotnet "$OLDPWD/$jit_dll" "$@" < "$input_abs") > "$out_abs/$name.jit.txt" 2> "$out_abs/$name.jit.log" || jit_code=$?
    (cd "$dir" && SKARP_HOME="$out_abs/agent-home.aot" "$aot_abs" "$@" < "$input_abs") > "$out_abs/$name.aot.txt" 2> "$out_abs/$name.aot.log" || aot_code=$?
  else
    (cd "$dir" && SKARP_HOME="$out_abs/agent-home.jit" dotnet "$OLDPWD/$jit_dll" "$@") > "$out_abs/$name.jit.txt" 2> "$out_abs/$name.jit.log" || jit_code=$?
    (cd "$dir" && SKARP_HOME="$out_abs/agent-home.aot" "$aot_abs" "$@") > "$out_abs/$name.aot.txt" 2> "$out_abs/$name.aot.log" || aot_code=$?
  fi
  if [ "$jit_code" -ne "$aot_code" ] || [ "$jit_code" -eq 1 ]; then echo "$name: exit codes $jit_code (JIT) and $aot_code (AOT)" >&2; failed=1; fi
  if cmp -s <(agent_norm "$out/$name.jit.txt") <(agent_norm "$out/$name.aot.txt"); then echo "equal: $name (exit code $jit_code, $(wc -l < "$out/$name.jit.txt") lines)"; else echo "$name: output differs" >&2; failed=1; fi
}
aot_abs="$(cd "$(dirname "$aot")" && pwd)/$(basename "$aot")"
out_abs="$(cd "$out" && pwd)"
rm -rf "$out/agent-home.jit" "$out/agent-home.aot"
agent_edge="$out/agent-edge"
rm -rf "$agent_edge" && mkdir -p "$agent_edge" && cp -R "$edge/." "$agent_edge/"
printf '{ "version": 1, "history": { "enabled": false }, "semantic": { "enabled": false } }
' > "$agent_edge/.skarp.json"
dotnet "$jit_dll" baseline "$agent_edge" > /dev/null
# A stricter long-method notice than the baseline's makes new findings for the summary, show and check to print.
printf '{ "version": 1, "history": { "enabled": false }, "semantic": { "enabled": false }, "thresholds": { "long-method": { "notice": 10 } } }
' > "$agent_edge/.skarp.json"
agent_pair agent-analyze "$agent_edge" "" analyze . --format text --min-severity notice
dotnet "$jit_dll" analyze "$agent_edge" > /dev/null
agent_pair agent-show-list "$agent_edge" "" show --all --include-notices
agent_pair agent-show-json "$agent_edge" "" show --format json --new-only
first_handle="$(grep -oE '\[[0-9a-f]{8}\]' "$out/agent-show-list.jit.txt" | head -1 | tr -d '[]')"
[ -n "$first_handle" ] || { echo "agent: no finding listed by show" >&2; failed=1; }
agent_pair agent-show-detail "$agent_edge" "" show "$first_handle"
agent_pair agent-show-baseline "$agent_edge" "" show --from baseline --include-notices
agent_file="$(ls "$agent_edge"/*.cs | head -1)"
agent_pair agent-check "$agent_edge" "" check "$(basename "$agent_file")" --include-notices
agent_pair agent-check-track "$agent_edge" "" check "$(basename "$agent_file")" --track --session jitaot --include-notices
printf '
// edited
' >> "$agent_file"
agent_pair agent-check-track-again "$agent_edge" "" check "$(basename "$agent_file")" --track --session jitaot --include-notices
node -e 'console.log(JSON.stringify({session_id:"hook",hook_event_name:"PostToolUse",tool_name:"Edit",tool_input:{file_path:process.argv[1]}}))' "$(cd "$agent_edge" && pwd -W 2>/dev/null || pwd)/$(basename "$agent_file")" > "$out/agent-hook-input.json"
agent_pair agent-hook "$agent_edge" "$out/agent-hook-input.json" check --hook claude --include-notices
node -e 'console.log(JSON.stringify({session_id:"hook",hook_event_name:"Stop",stop_hook_active:false}))' > "$out/agent-stop-input.json"
agent_pair agent-hook-stop "$agent_edge" "$out/agent-stop-input.json" check --hook claude --include-notices
# The code health score. An edit that lowers the file's score prints a health line first; show --file prints
# the file's score and every contribution; a settings file with score weights changes the JSON scores (both builds equal).
printf '
public class HealthProbe { public void M(int a, int b, int c, int d, int e, int f, int g, int h, int i) { } }
' >> "$agent_file"
agent_pair agent-check-health "$agent_edge" "" check "$(basename "$agent_file")" --track --session jitaot
grep -q ' health [0-9]' "$out/agent-check-health.aot.txt" || { echo "agent: no health line after a worse edit (AOT)" >&2; failed=1; }
agent_pair agent-show-health "$agent_edge" "" show --file "$(basename "$agent_file")"
grep -q ' health [0-9]' "$out/agent-show-health.aot.txt" || { echo "agent: show --file printed no health line (AOT)" >&2; failed=1; }
printf '{ "version": 1, "score": { "decay": 0.5, "weights": { "long-method": { "notice": 0.9, "cap": 4 } }, "worst": { "below": 9.5 } } }
' > "$out/score-settings.json"
run_pair edge-analyze-score analyze "$edge" --no-history --config "$out/score-settings.json" --parallelism 4
# The cycles fixture (restored above): the session-end summary with a namespace cycle, and show's full cycle detail.
agent_pair agent-cycles-analyze "$cycles" "" analyze . --no-history --no-config --format text
dotnet "$jit_dll" analyze "$cycles" --no-history --no-config > /dev/null
cycle_handle="$(grep 'namespace-cycle' "$out/agent-cycles-analyze.jit.txt" | grep -oE '\[[0-9a-f]{8}\]' | head -1 | tr -d '[]')"
[ -n "$cycle_handle" ] || { echo "cycles fixture: no namespace-cycle line in the agent summary" >&2; failed=1; }
agent_pair agent-cycles-show "$cycles" "" show "$cycle_handle"
grep -q "suggested cut" "$out/agent-cycles-show.aot.txt" || { echo "cycles fixture: show printed no cut (AOT)" >&2; failed=1; }
agent_pair agent-cycles-check "$cycles" "" check Lib/Orders.cs --no-config --include-notices
run_semantic_pair self-semantic-generated-targets analyze . --no-history --parallelism 4 --include-generated-targets --rule unused-member=off --rule unused-type=off --rule used-only-by-tests=off --rule unread-member=off
# The unused-code fixture (a library with two targets and a test project), restored in a copy, with all targets:
# unused members and types, the used-only-by-tests label, and one semantic file per framework.
fixture="$out/unused-fixture"
rm -rf "$fixture" && mkdir -p "$fixture"
cp -R tests/SkarpSharp.Tests/Semantic/Fixtures/unused/. "$fixture/"
for f in "$fixture"/*.fixture "$fixture"/.*.fixture; do if [ -e "$f" ]; then mv "$f" "${f%.fixture}"; fi; done
dotnet restore "$fixture/Unused.slnx" -nologo > "$out/unused-fixture-restore.log"
unused_args=(analyze "$fixture" --no-history --no-config --all-targets --parallelism 4 --format json)
dotnet "$jit_dll" "${unused_args[@]}" -o "$out/unused.jit.json" --semantic-output "$out/unused.jit.semantic.json" 2> "$out/unused.jit.log"
"$aot" "${unused_args[@]}" -o "$out/unused.aot.json" --semantic-output "$out/unused.aot.semantic.json" 2> "$out/unused.aot.log"
grep -q "skarp semantic: ran" "$out/unused.aot.log" || { echo "unused fixture: the semantic tier did not run (AOT)" >&2; failed=1; }
grep -q '"rule": "used-only-by-tests"' "$out/unused.aot.semantic.net10.0.json" || { echo "unused fixture: no used-only-by-tests label (AOT)" >&2; failed=1; }
for file in unused unused.semantic.net10.0 unused.semantic.net9.0; do
  jit_file="$out/${file/unused/unused.jit}.json"; aot_file="$out/${file/unused/unused.aot}.json"
  if node tools/ci/compare-json.mjs "$jit_file" "$aot_file"; then :; else failed=1; fi
done
# The same fixture with the default (highest target only): findings carry their scope, one semantic file.
run_semantic_pair unused-highest analyze "$fixture" --no-history --no-config --parallelism 4
grep -q 'net10.0 only; --all-targets for all' "$out/unused-highest.aot.semantic.json" || { echo "unused fixture (highest target): no scoped finding (AOT)" >&2; failed=1; }
run_sarif_pair unused-sarif analyze "$fixture" --no-history --no-config --all-targets --parallelism 4
grep -q '"ruleId": "unused-member"' "$out/unused-sarif.aot.sarif" || { echo "unused fixture: no unused-member result in SARIF (AOT)" >&2; failed=1; }

# The clone fixture (see the header).
clones="$out/clones-fixture"
rm -rf "$clones" && mkdir -p "$clones"
cp tests/SkarpSharp.Tests/Clones/Mutations/*.cs "$clones/"
sed -e 's/class InvoiceTotals$/class InvoiceTotalsKept/' \
    -e 's/^    public decimal Total(/    [System.Diagnostics.CodeAnalysis.SuppressMessage("skarp", "duplicate-code", Justification = "kept apart on purpose")]\n    [System.Diagnostics.CodeAnalysis.SuppressMessage("skarp", "magic-number", Justification = "tariff values of the fixture")]\n    public decimal Total(/' \
    tests/SkarpSharp.Tests/Clones/Mutations/m1.original.cs > "$clones/m1.kept.cs"
printf 'namespace Fixture;\nclass Unjustified\n{\n    [System.Diagnostics.CodeAnalysis.SuppressMessage("skarp", "complex-method")]\n    int M(int x) { if (x > 0) return 1; return 0; }\n}\n' > "$clones/unjustified.cs"
# Low-content follow-up: two constructors that only store 16 injected services (skipped by default, reported with
# duplicate-code.lowContent=report).
for t in Order Product; do
  {
    printf 'namespace Fixture;\npublic class %sController\n{\n' "$t"
    for i in $(seq 1 16); do printf '    private readonly I%sService%d _service%d;\n' "$t" "$i" "$i"; done
    printf '\n    public %sController(\n' "$t"
    for i in $(seq 1 16); do sep=','; [ "$i" = 16 ] && sep=')'; printf '        I%sService%d service%d%s\n' "$t" "$i" "$i" "$sep"; done
    printf '    {\n'
    for i in $(seq 1 16); do printf '        _service%d = service%d;\n' "$i" "$i"; done
    printf '    }\n}\n'
  } > "$clones/${t}Controller.cs"
done
clone_args=(analyze "$clones" --no-history --no-config --rule duplicate-code-near=on --parallelism 4)
run_pair clones-analyze "${clone_args[@]}"
run_pair clones-lowcontent "${clone_args[@]}" --threshold duplicate-code.lowContent=report
groups_of() { node -e 'console.log(JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")).repository.duplication.groups)' "$1"; }
[ "$(groups_of "$out/clones-lowcontent.aot.json")" -eq "$(( $(groups_of "$out/clones-analyze.aot.json") + 1 ))" ] \
  || { echo "clone fixture: lowContent=report did not add exactly the low-content group (AOT)" >&2; failed=1; }
for needle in '"kind": "near-miss"' '"kind": "renamed"' '"duplication"' '"suppressed"' '"rule": "suppression-without-justification"'; do
  grep -q "$needle" "$out/clones-analyze.aot.json" || { echo "clone fixture: no $needle in the JSON (AOT)" >&2; failed=1; }
done
run_sarif_pair clones-sarif "${clone_args[@]}"
grep -q '"relatedLocations"' "$out/clones-sarif.aot.sarif" || { echo "clone fixture: no related locations in SARIF (AOT)" >&2; failed=1; }
dotnet "$jit_dll" baseline "$clones" --no-history --no-config --rule duplicate-code-near=on --baseline "$out/clones-baseline.jit.json" > /dev/null
"$aot" baseline "$clones" --no-history --no-config --rule duplicate-code-near=on --baseline "$out/clones-baseline.aot.json" > /dev/null
if cmp -s "$out/clones-baseline.jit.json" "$out/clones-baseline.aot.json"; then echo "equal: clone baseline files"; else echo "clone baseline files differ" >&2; failed=1; fi
grep -q '"files": \[' "$out/clones-baseline.aot.json" || { echo "clone fixture: no clone files in the baseline (AOT)" >&2; failed=1; }
agent_pair agent-clones "$clones" "" analyze . --no-history --no-config --no-baseline --no-save --rule duplicate-code-near=on --format text --min-severity notice
grep -q ' duplicate-code: ' "$out/agent-clones.aot.txt" || { echo "clone fixture: no duplicate-code finding line in the text report (AOT)" >&2; failed=1; }

# The brain composites (opt-in) on a generated fixture: a worked example (Importer.Run is
# long, complex and deeply nested, Importer.Map long and complex only, ten small methods) plus a type whose brain-method
# composite is suppressed with a justification. JSON (composites block, suppressed parts), SARIF (composite results with
# related locations), Markdown, the agent summary, check, show of a composite, and a baseline recorded with the rules off
# compared with them on (0 new, 0 worsened).
brain="$out/brain-fixture"
rm -rf "$brain" && mkdir -p "$brain"
node -e '
const fs = require("fs"), dir = process.argv[1];
function method(name, lines, depth, branches) {
  const out = [`    public int ${name}(int a, int b)`, "    {", "        int x = 0;"];
  for (let i = 0; i < branches; i++) out.push(`        if (a > ${i}) { if (b > ${i}) x++; }`);
  for (let d = 0; d < depth; d++) { const p = " ".repeat(8 + 4 * d); out.push(`${p}if (a > b + ${d})`, `${p}{`); }
  const fill = Math.max(0, lines - (3 + branches + 2 * depth) - depth - 2);
  for (let i = 0; i < fill; i++) out.push(" ".repeat(8 + 4 * depth) + `x += a * ${i % 7};`);
  for (let d = depth - 1; d >= 0; d--) out.push(" ".repeat(8 + 4 * d) + "}");
  out.push("        return x;", "    }");
  return out.join("\n") + "\n";
}
function type(name, attr) {
  let s = `namespace Shop;\npublic class ${name}\n{\n` + (attr ? `    ${attr}\n` : "") + method("Run", 80, 5, 3) + method("Map", 60, 1, 6);
  for (let i = 0; i < 10; i++) s += `    public int Small${i}(int v) => v + ${i};\n`;
  return s + "}\n";
}
fs.writeFileSync(dir + "/Importer.cs", type("Importer"));
fs.writeFileSync(dir + "/Exporter.cs", type("Exporter", "[System.Diagnostics.CodeAnalysis.SuppressMessage(\"skarp\", \"brain-method\", Justification = \"a state machine on purpose\")]"));
' "$brain"
printf '{ "version": 1, "history": { "enabled": false }, "semantic": { "enabled": false }, "rules": { "brain-method": true, "brain-class": true, "duplicate-code": false } }
' > "$brain/.skarp.json"
run_pair brain-analyze analyze "$brain" --no-history --no-baseline --no-save --parallelism 4
for needle in '"composites"' '"rule": "brain-class"' '"rule": "brain-method"' '"brainMethods"' '"suppressed"'; do
  grep -q "$needle" "$out/brain-analyze.aot.json" || { echo "brain fixture: no $needle in the JSON (AOT)" >&2; failed=1; }
done
run_sarif_pair brain-sarif analyze "$brain" --no-history --no-baseline --no-save --parallelism 4
grep -q '"ruleId": "brain-class"' "$out/brain-sarif.aot.sarif" || { echo "brain fixture: no brain-class result in SARIF (AOT)" >&2; failed=1; }
agent_pair brain-markdown "$brain" "" analyze . --no-baseline --no-save --all --format markdown
agent_pair brain-agent "$brain" "" analyze . --no-baseline --no-save --format text --min-severity notice
grep -q ' brain-class: ' "$out/brain-agent.aot.txt" || { echo "brain fixture: no brain-class line in the text report (AOT)" >&2; failed=1; }
dotnet "$jit_dll" baseline "$brain" --rule brain-method=off --rule brain-class=off > /dev/null
run_pair brain-relabel analyze "$brain" --no-save --parallelism 4
node -e 'const b = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")).baseline; if (b.new !== 0 || b.worsened !== 0) { console.error(`brain fixture: relabelling gave ${b.new} new, ${b.worsened} worsened`); process.exit(1); }' "$out/brain-relabel.aot.json" || failed=1
dotnet "$jit_dll" analyze "$brain" > /dev/null
brain_handle="$(cd "$brain" && dotnet "$OLDPWD/$jit_dll" show --rule brain-method --include-notices | grep 'brain-method' | grep -oE '\[[0-9a-f]{8}\]' | head -1 | tr -d '[]')"
[ -n "$brain_handle" ] || { echo "brain fixture: show listed no brain-method composite" >&2; failed=1; }
agent_pair brain-show "$brain" "" show "$brain_handle"
grep -q 'parts (3):' "$out/brain-show.aot.txt" || { echo "brain fixture: show printed no parts (AOT)" >&2; failed=1; }
printf '
// edited
' >> "$brain/Importer.cs"
agent_pair brain-check "$brain" "" check Importer.cs --include-notices --no-save --no-baseline
grep -q ' brain-class: ' "$out/brain-check.aot.txt" || { echo "brain fixture: check printed no brain-class line (AOT)" >&2; failed=1; }

if [ -n "$repo" ]; then
  run_pair repo-analyze analyze "$repo" --parallelism 4
  run_pair repo-hotspots hotspots "$repo" --all-history --parallelism 4
  run_pair repo-analyze-tests analyze "$repo" --include-tests --preset codescene --complex-metric cyclomatic --parallelism 4
  run_sarif_pair repo-sarif analyze "$repo" --parallelism 4
  # history has no --parallelism option
  dotnet "$jit_dll" history "$repo" --all-history --include-bots -w --format json -o "$out/repo-history.jit.json" 2> "$out/repo-history.jit.log"
  "$aot" history "$repo" --all-history --include-bots -w --format json -o "$out/repo-history.aot.json" 2> "$out/repo-history.aot.log"
  if node tools/ci/compare-json.mjs "$out/repo-history.jit.json" "$out/repo-history.aot.json"; then :; else failed=1; fi
fi

if [ "$failed" -ne 0 ]; then echo "JIT and AOT outputs differ (details above; files in $out)" >&2; exit 1; fi
echo "JIT = AOT on all inputs"
