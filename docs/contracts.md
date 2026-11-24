# Contracts

What scripts, CI pipelines and tools may depend on in Skarp: the JSON documents, baseline and settings files, rule ids, finding ids and exit codes.

Text, Markdown and GitHub-annotation output is written for people and may change in any release. Tools should read `--format json` (or SARIF). Every schema and format version is 1 at the first public release.

## 1. Stable and experimental

| Contract | Status |
|---|---|
| Analysis, history and semantic JSON output | stable (parts marked experimental below) |
| Baseline files `.skarp-baseline.json` and `.skarp-baseline.semantic.json` | stable |
| Settings file `.skarp.json` | stable (keys of experimental features are experimental) |
| SARIF profile | stable |
| Rule ids and the finding id format | stable |
| Exit codes of `analyze`, `hotspots`, `history`, `baseline`, `config` and `explain` | stable |
| Saved run header, `skarp show --format json`, tracking files, `skarp config --format json` | experimental |
| `check`, `show`, `install`, `setup command`, `setup claude`, `setup git`, `mcp`, `--push-check` and everything the help calls experimental | experimental |

Experimental contracts may change in any release; the release notes say so. Inside a stable JSON document, the blocks of experimental features (the health score, `composites`, `coverage`, `run.variants`, `ranking`) are experimental too.

## 2. Versioning rule

Each JSON document carries its own integer version. For every change:

| Change | Version |
|---|---|
| New property, new optional key, new value in a field documented as open (for example a new rule id in `findings[].rule`) | stays the same (additive) |
| Property removed or renamed; kind changed (string to number, object to array); a property that was always present becomes optional or nullable; a value's meaning or unit changes; the finding id format changes | goes up by one |

- Readers of Skarp output must ignore properties they do not know.
- Skarp reads baselines leniently (unknown keys are ignored) and settings strictly (an unknown key is an error, so a file with a key from a newer Skarp fails on an older one).
- A change of the finding id format also bumps the baseline version, because every recorded id would otherwise become new.
- The tool version (`run.toolVersion`, `toolVersion` in baselines) is informational. Contracts are judged by their schema versions.

## 3. JSON documents and their versions

| Document | Written by | Version field | Version | Status |
|---|---|---|---|---|
| Analysis output | `skarp analyze`, `hotspots`, `baseline` (with `--format json` or `--output`) | `schemaVersion` | 1 | stable |
| History output | `skarp history` (`--format json` or `--output`) | `schemaVersion` | 1 (shares the analysis version) | stable |
| Semantic output | `analyze` and `baseline` when the semantic tier runs and a semantic file is asked for (`--semantic-output`, or `--output`) | `schemaVersion` | 1 | stable |
| Syntax baseline `.skarp-baseline.json` | `skarp baseline` | `version` | 1 | stable |
| Semantic baseline `.skarp-baseline.semantic.json` | `skarp baseline` with the semantic tier | `version` | 1 | stable |
| Settings `.skarp.json` | you | `version` (required) | 1 | stable |
| SARIF log | `skarp analyze --format sarif` | `runs[0].properties.profileVersion` (SARIF itself is `2.1.0`) | 1 | stable |
| Saved run header `.skarp/last-run/run.json` | every `skarp analyze` (not with `--no-save`) | `version` | 1 | experimental |
| `skarp show --format json` | `skarp show` | `schemaVersion` | 1 | experimental |
| `skarp config --format json` | `skarp config` | `schemaVersion` | 1 | experimental |
| Tracking file `~/.skarp/tracked/<date>-<session>.json` | `skarp check --track` and the Claude Code hook | `version` | 1 | experimental |

The machine-checked shape of each document is a committed snapshot in [tests/SkarpSharp.Tests/Contracts/shapes](../tests/SkarpSharp.Tests/Contracts/shapes): one line per property path with its JSON kind, and `?` when the property may be absent.

- [analysis.v1.verified.txt](../tests/SkarpSharp.Tests/Contracts/shapes/analysis.v1.verified.txt)
- [history.v1.verified.txt](../tests/SkarpSharp.Tests/Contracts/shapes/history.v1.verified.txt)
- [semantic.v1.verified.txt](../tests/SkarpSharp.Tests/Contracts/shapes/semantic.v1.verified.txt)
- [baseline.v1.verified.txt](../tests/SkarpSharp.Tests/Contracts/shapes/baseline.v1.verified.txt)
- [settings.v1.verified.txt](../tests/SkarpSharp.Tests/Contracts/shapes/settings.v1.verified.txt)
- [sarif.v1.verified.txt](../tests/SkarpSharp.Tests/Contracts/shapes/sarif.v1.verified.txt)
- [last-run.v1.verified.txt](../tests/SkarpSharp.Tests/Contracts/shapes/last-run.v1.verified.txt)
- [show.v1.verified.txt](../tests/SkarpSharp.Tests/Contracts/shapes/show.v1.verified.txt)
- [tracking.v1.verified.txt](../tests/SkarpSharp.Tests/Contracts/shapes/tracking.v1.verified.txt)

`skarp config --format json` has no shape snapshot yet; the golden output `tests/SkarpSharp.Tests/Golden/expected/SettingsLevelsTests.Config_json_and_the_gate_view.verified.json` shows it.

### Analysis output

Top-level properties: `schemaVersion`, `run`, `files`, `types`, `repository`, and when present `findings`, `findingCounts`, `excluded`, `history`, `hotspots`, `baseline`, `gate`, `suppressed`, and the experimental `health`, `composites`, `coverage` and `ranking`. The shape snapshot lists every property.

- `run` holds the run header: root, tool version, settings and their fingerprint, git information, `settingsFiles` (the settings levels that applied, each with `level` and `path`), and for files with `#if` the `variants` block.
- `findings[]` has one entry per finding: `id`, `handle`, `rule`, `severity` (`notice` or `warning`), `path`, `line`, `member`, `message`, `metric`, `value`, `threshold`, `preset`, `thresholdSource`, and with a baseline `baselineState` (`new`, `known` or `worsened`).
- `gate` holds the verdict when the gate ran: `on` (the gate set), `tripped`, the `new` and `worsened` finding counts that count, `outside`, and `health` with the limits in force and the files past a limit.
- `health` (experimental) holds the code health score: per file `score`, `band` (`healthy`, `problematic` or `unhealthy`) and `contributions`; the project averages; the worst files. See [health-score.md](health-score.md).
- `suppressed` lists findings an inline suppression left out (section 5).
- `ranking` (experimental) is present with `--sort`.

### Rules shared by all JSON outputs

- Property names are camelCase. Null properties are left out, so `?` in a snapshot means "may be missing".
- Paths are relative to the analyzed folder with `/` separators (`files[].path`, `findings[].path`). `run.root`, `run.settingsFiles[].path`, `baseline.path` and `run.git.repositoryRoot` are absolute and machine-specific.
- Run-time fields differ between runs: `run.durationSeconds`, `run.peakWorkingSetBytes`, `run.runtime`, `run.git.durationSeconds` and the semantic `run.timings`. Everything else is the same for the same input and settings; the JIT and Native AOT builds give equal output.
- Enumerated values: `findings[].severity` is `notice` or `warning`; `findings[].baselineState` is `new`, `known` or `worsened`; `excluded[].kind` is `test` or `exclude`; semantic `run.status` is `ran`, `off` or `not evaluated`. The set of `findings[].rule` values is open.
- The semantic output goes to the path of `--semantic-output`, else to the `--output` path with `.semantic` before `.json`. With one file per target framework the framework goes before `.json` (`out.semantic.net8.0.json`).

### Saved run (experimental)

Every `skarp analyze` saves its result in `.skarp/last-run/`: `analysis.json` and `semantic.json` (or `semantic.<tfm>.json` per framework), the same documents `--output` writes, and `run.json`, the header with `version`, `writtenAt`, `commit`, `dirty`, `settingsFingerprint`, `toolVersion`, `root`, `documents`, `files` (a SHA-256 per analyzed file) and `solution`. The folder is replaced as a whole. `skarp show` and the MCP tool `findings` read it. `.skarp/checked/` holds the last `skarp check` of each file and is internal.

## 4. Baseline files

`.skarp-baseline.json` (syntax tier) and `.skarp-baseline.semantic.json` (semantic tier) are written by `skarp baseline`. The writer is deterministic: a header, then the findings grouped under the path of their file (files sorted by path, findings by rule and place, one finding per line), no dates, so merges conflict only where findings changed.

- Header keys: `version`, `type` (`baseline` or `semantic-baseline`), `toolVersion`, `preset`, `complexMetric` and `longMethodLines` (syntax), `settingsFingerprint`, `commit` and `csharpCommits` (syntax, when git history ran), `solution`, `targets` and `targetFramework` (semantic). The semantic `commit` may end in `+` when `.cs` files had uncommitted changes.
- `files`: an object from the relative path (with `/`) of a file to the array of its findings. A syntax baseline lists every analyzed file (an empty array means "analyzed, no findings"; excluded and test files are not listed); a semantic baseline lists the files that have findings. `repository`: the findings that belong to no single file (clone groups, namespace cycles); absent when there are none.
- Entries: `{ "rule": "...", "place": "...", "value": ... }`. `place` is the type or member within the file (absent when the finding has none), `value` is absent for structural rules, and clone-group entries also carry `severity` and `files`. The finding id is `<rule>|<path>|<place>`: the path is the key of the group, empty in `repository`.
- A file whose `type` is missing, or is not the type the command reads, is refused with a message naming both.
- A baseline whose `version` is not 1 is refused with a message to run `skarp baseline` again.
- `settingsFingerprint` is a 16-digit hexadecimal hash of the effective rule settings. Its input format is internal; a change only makes the run report "rule settings changed".

See [baselines-and-gate.md](baselines-and-gate.md).

## 5. Rule ids

Rule ids are stable and never reused for a different meaning. Thresholds, presets and default severities are not part of the contract: `skarp explain <rule>` prints the ones in force. See [rules.md](rules.md).

| Id | Tier | Place |
|---|---|---|
| `complex-method` | syntax | member |
| `deep-nesting` | syntax | member |
| `long-method` | syntax | member |
| `large-type` | syntax | type |
| `large-file` | syntax | file |
| `too-many-parameters` | syntax | member |
| `path-explosion` | syntax | member |
| `active-complex-file` | syntax, needs history | file |
| `bumpy-road` | syntax | member |
| `complex-condition` | syntax | member |
| `empty-catch` | syntax | member |
| `magic-number` | syntax | member |
| `duplicate-code` | syntax | clone group |
| `duplicate-code-near` | syntax | clone group |
| `suppression-without-justification` | syntax | member |
| `brain-method` | syntax, composite | member |
| `brain-class` | syntax, composite, experimental, off by default | type |
| `untested-complex-method` | syntax, only with a coverage report | member |
| `high-coupling` | semantic | type |
| `god-class` | semantic | type |
| `unused-member` | semantic | member |
| `unused-type` | semantic | type |
| `used-only-by-tests` | semantic | member |
| `unread-member` | semantic | member |
| `namespace-cycle` | semantic | namespace |

Preset ids (`--preset`, `preset` in settings): `default`, `codescene`, `clean-cadet`, `doomen`, `reportgenerator`, `ndepend`, `pmd`, `checkstyle`, `microsoft`, `jscpd`, `alves-percentile`. The ids are stable; their values may change. See [presets.md](presets.md).

Inline suppression works for every rule of both tiers: `[SuppressMessage("skarp", "<rule-id>", Justification = "<why>")]` on a member or type, or at assembly level with a `Target`. A suppression without a justification does not count and is reported as `suppression-without-justification`. Suppressed findings are left out of findings, counts, gate, baseline and score, and listed in `suppressed`.

## 6. Finding id format

An id is `<rule>|<path>|<place>`, with an optional `~<n>` suffix.

- `<rule>` is a rule id. `<path>` is the file relative to the analyzed folder with `/` separators.
- `<place>` for a member finding is the member key `<type>::<Member>`: no parameters, constructors as `.ctor`, `#<n>` for the n-th member of the same name in the file (n from 2), lambdas `<parent>/lambda<n>`, local functions `<parent>/<name>`, top-level statements `<top-level>`.
- `<place>` for a type finding is the type's full name: nested types joined with `+`, generic arity as `` `n `` (`Namespace.Outer+Inner`, ``Namespace.Cache`2``).
- A file finding has an empty place, so the id ends with `|`.
- A namespace cycle has no path: `namespace-cycle||namespace <node> (<assembly>)`, where `<node>` is the first namespace of the cycle in ordinal order (`<global>` for the global namespace).
- A clone group has no path: `duplicate-code||clone <h>` or `duplicate-code-near||clone <h>`, where `<h>` is the first 16 lowercase hexadecimal characters of a SHA-256 over the normalized tokens of the group's seed window (the first 50 tokens of the first copy in path order). The same code gives the same id on every platform. Two groups with the same seed take `~2`.
- `~<n>` marks the second and later finding with the same id (for example several empty catch clauses in one member), in source order.
- Members are matched across `#if` variants by identifier position, kind and name. A member only a variant shows is numbered after the base view's members.
- Ids hold no line numbers, so edits elsewhere in a file keep them. Renaming or moving a member, type or file changes its ids; a run marks such pairs `possiblyRenamedFrom`.

The handle is the first 8 lowercase hexadecimal characters of the SHA-256 of the id's UTF-8 bytes, written as `findings[].handle` and shown in text output. `skarp show` accepts a handle or any prefix of 4 or more characters, and lists all matches when a prefix is shared. Two ids can share a handle; the JSON keeps the full ids.

The format has no version field of its own. Changing it bumps the baseline version and the schema versions (section 2). The SARIF fingerprint carries it under the versioned key `skarpFindingId/v1` (section 9).

## 7. Exit codes

| Code | Meaning |
|---|---|
| 0 | Done. Also when findings exist: without `--gate`, findings and baselines never change the exit code. Help and `explain` exit 0. |
| 1 | Tool error: an I/O, access or data error during the run, for example an unreadable or unsupported baseline file. |
| 2 | Usage error: unknown command or option, missing or invalid value, invalid settings file, folder not found, unknown rule for `explain`, `--gate` without a baseline file or with `--no-baseline`, a `--baseline` file that does not exist, a `--coverage` file that is missing or not Cobertura, and with `--strict-coverage` a stale report. |
| 3 | Gate tripped: with `--gate` (or `gate.enabled`), findings in the gate set are new or worse than in the syntax or semantic baseline, or a file is past a health limit (`gate.health.*`). |

Experimental commands:

- `skarp check --gate` (without `--hook`) exits 3 when a warning in the gate set is new or worse, or a file is past a health limit.
- `skarp check --hook claude` always exits 0. It answers in JSON and fails open: an internal error prints nothing on standard output.
- `skarp show` exits 2 for an unknown handle, a missing saved run or baseline, an ambiguous `--session` prefix and bad options.
- `skarp install claude` and `skarp install git-hooks` exit 0 when they wrote, had nothing to change or `--check` found everything in order; 1 when `--check` found a problem; 2 for a usage error, a refused file, an answer of no, no terminal without `--yes`, or a failed write.
- `skarp check --staged` exits 0 (also without staged C# files), 3 when `--gate` trips, 1 when git fails, 2 for a usage error. `skarp analyze --push-check` exits as `analyze`, and 0 when the push is not checked.
- `skarp setup` exits 0 when it saved, had nothing to change or was quit; 2 for a usage error, an invalid value or a failed write; 130 when the menu is cancelled with Ctrl+C.

Any other exit code is an unhandled crash and a bug.

## 8. Settings file `.skarp.json`

Version 1. The file is found in the analyzed folder or the nearest parent up to the git work tree root. `--config <file>` and `--no-config` override. `version` is required and must be 1. Keys are camelCase and case-sensitive. Comments and trailing commas are allowed. Unknown keys and wrong types are errors naming the key path (exit code 2). Precedence: default, then file, then flag. Globs are relative to the file's folder.

Keys: `preset`, `complexMetric`, `longMethodLines`, `recordParameters`, `magicNumbers`, `rules`, `thresholds`, `includeTests`, `include`, `exclude`, `top`, `history`, `report`, `gate`, `semantic`, `unusedCode`, `namespaceCycles`, `saveLastRun`, `score`, `coverage`, `symbols`, `variants`, `output`, `tracking`, `overrides`, and in machine files `useInRepositories` and `root`. The shape snapshot [settings.v1.verified.txt](../tests/SkarpSharp.Tests/Contracts/shapes/settings.v1.verified.txt) lists every key and sub-key. The meaning of each is in [settings.md](settings.md).

Settings levels, lowest first: Skarp's defaults, the system file, the personal file (`~/.skarp/settings.json`; `SKARP_HOME` replaces `~/.skarp`), the repository file (`.skarp.json`, else `--config`, else `SKARP_CONFIG`) and the command line. All files use this schema and version.

- Scalars replace. Objects merge key by key. Lists replace unless the file that sets the list also sets `<name>Mode` to `append`. A missing key inherits, `null` resets the key to its default, `[]` empties a list.
- `useInRepositories` is allowed only in machine files. `root`, `coverage`, `semantic.solution`, `gate.enabled` and `overrides` are allowed only in the repository file. Elsewhere they are errors that name the level.
- `overrides` is an ordered list of per-path entries with `files`, `excludeFiles`, `tests`, `rules` and `thresholds`. Later entries win key by key. `duplicate-code`, `duplicate-code-near` and `namespace-cycle` cannot vary per path.
- `skarp config` shows each effective value and where it came from; `--setting <key>=<value>` (experimental) sets any run setting for one run.

## 9. SARIF profile

`skarp analyze --format sarif` writes SARIF 2.1.0 (`$schema` `https://json.schemastore.org/sarif-2.1.0.json`). The file is valid against the official schema. See [reports-and-sarif.md](reports-and-sarif.md).

- What Skarp writes inside SARIF has its own version in `runs[0].properties.profileVersion` (1), with the rules of section 2.
- One run with `tool.driver.name` `skarp`. `rules` lists the rule ids of section 5 in catalog order; `ruleIndex` is the position in it. Rule texts, default levels and tags are not part of the contract.
- One result per finding of both tiers. `level` is `warning` for warnings and `note` for notices. `properties.tier` is `syntax` or `semantic`.
- Locations: `artifactLocation.uri` is the path relative to `SRCROOT`, percent-encoded per segment, with `uriBaseId` `SRCROOT`. `originalUriBaseIds.SRCROOT.uri` is the absolute `file:` URI of the git work tree root (or of the analyzed folder outside a work tree). `region.startLine` is the finding line (line 1 for file findings); no columns. A clone group points at its first copy and lists the others in `relatedLocations`.
- Fingerprint: `partialFingerprints` holds `skarpFindingId/v1`, the finding id of section 6. `primaryLocationLineHash` is not written; GitHub's upload action computes it.
- `baselineState` (`new`, `unchanged` or `updated`) is written only when every result was compared with a baseline; otherwise it is absent on every result, with a note in `runs[0].properties.notes`. Fixed baseline entries are not written.
- At most 25,000 results (GitHub code scanning rejects more). Above it, warnings are kept first, then notices, and `runs[0].properties.resultsOmitted` gives the number left out.
- The output has no times or runtime names, so JIT and Native AOT builds write identical files.
- `runs[0].artifacts[]` carries each file's health score in `properties.health` (experimental).

## 10. Text format and hook output (experimental)

`--format text` is the default output. Its layout and wording may change in any release; tools read JSON. What stays: the exit codes (section 7), the Claude Code hook's JSON envelope below, and that every command the text prints can be run.

The Claude Code hook (`skarp check --hook claude`, see [check-and-agents.md](check-and-agents.md)) reads the hook JSON on standard input (`session_id`, `agent_id`, `agent_type`, `hook_event_name`, `tool_input.file_path`, `stop_hook_active`; other fields are ignored). Standard output is either empty or one JSON object: `{"hookSpecificOutput":{"hookEventName":"PostToolUse"|"Stop","additionalContext":"<text>"}}`, or with `--gate`, when a warning in the gate set is new or worse, `{"decision":"block","reason":"<text>"}`. A Stop never blocks twice in a row. The commit and push hooks print their reports on standard error ([git-hooks.md](git-hooks.md)).

## 11. MCP server (experimental)

`skarp mcp` serves the Model Context Protocol over standard input and output, with the tools `find_worst_code`, `check`, `findings` and `explain`. Each call returns one text block in the text format; there is no output schema. Errors are tool errors (`isError: true`) whose text names the cap or the valid values. Tool names and parameters may change in any release. See [check-and-agents.md](check-and-agents.md).

## 12. Guard: shape snapshots

`tests/SkarpSharp.Tests/Contracts/ContractShapeTests.cs` reads the shape of each document from the source-generated System.Text.Json metadata the tool serializes with, so optional properties count even when no sample contains them. It compares the shape with the snapshots in [tests/SkarpSharp.Tests/Contracts/shapes](../tests/SkarpSharp.Tests/Contracts/shapes):

- A removed, renamed, retyped or newly optional property fails until the version constant is bumped and a new snapshot is recorded.
- An added property fails until the snapshot of the same version is updated (`SKARP_UPDATE_SHAPES=1` writes it, and never drops a line).
- Committed samples (the golden outputs and this repository's own baselines and `.skarp.json`) must conform, and both baseline writers must produce files that read back.

The test does not guard value meanings and units, enumerated value sets, or the human and CI text.

## See also

- [settings.md](settings.md)
- [rules.md](rules.md)
- [baselines-and-gate.md](baselines-and-gate.md)
- [reports-and-sarif.md](reports-and-sarif.md)
- [check-and-agents.md](check-and-agents.md)
- [health-score.md](health-score.md)
- [CONTRIBUTING.md](../CONTRIBUTING.md)
