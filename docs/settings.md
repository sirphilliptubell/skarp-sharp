# Settings

Skarp reads settings from up to four files and the command line, merges them in a fixed order, and `skarp config` shows what came out and where each value came from.

Settings change which findings appear and how the report looks. For the meaning of the rules and presets, see [Rules](rules.md) and [Presets](presets.md).

## Settings levels and precedence

Levels are listed from lowest to highest. A higher level overrides the ones below it.

| Level | Where | Meant for |
| --- | --- | --- |
| defaults | built into Skarp | |
| system | Windows: `%ProgramData%\skarp\settings.json`. Linux: `/etc/skarp/settings.json`. macOS: `/Library/Application Support/skarp/settings.json` | every user of a machine |
| personal | `~/.skarp/settings.json` (the folder can be changed with `SKARP_HOME`) | you, in every folder |
| repository | the nearest `.skarp.json`, in the analyzed directory or a parent, up to the root of the git work tree | the team; commit it |
| flags | the command line, including `--setting key=value` | one run |

Rules for the merge:

- A scalar (number, text, true/false) in a higher level replaces the lower one.
- Objects (`rules`, `thresholds`, `history`, `score.weights` and so on) merge key by key.
- A list (`exclude`, `include`, `gate.on` and others) replaces the lower list. If the file also sets `"<name>Mode": "append"` (for example `"excludeMode": "append"`), the list is added to the lower one instead.
- A JSON `null` resets a key to Skarp's built-in default.
- Comments and trailing commas are allowed in settings files.

There is also a reserved "plugin" slot between defaults and system. It is always empty.

Settings files are checked on load. An unknown key or a bad value stops the run with exit code 2 and a message that names the level and the file.

### The repository file

Skarp looks for `.skarp.json` in the analyzed directory, then in each parent, and stops at the nearest one or at the root of the git work tree. Paths inside the file (`semantic.solution`, `coverage`) are relative to the file.

You can point at another file for the repository level:

```
skarp analyze . --config ci/skarp.json
SKARP_CONFIG=ci/skarp.json skarp analyze .
```

The flag wins over the variable.

### System and personal files

These files are machine state. Some keys exist only for machine files or only for the repository file:

- `useInRepositories` (system and personal only): with `false`, the file is applied to folders outside any repository and ignored inside one.
- `root`, `gate.enabled`, `semantic.solution`, `coverage` and `overrides` (repository only): a machine file that contains them is an error.

When the gate is on (`--gate`, or `gate.enabled` in `.skarp.json`), Skarp skips the system and personal files, so a CI run judges the same way as a local run on a clean checkout. `--gate-user-config` applies them anyway.

### Skipping levels

| Flag or variable | Effect |
| --- | --- |
| `--no-config` | read no settings file at all |
| `--no-user-config` or `SKARP_NO_USER_CONFIG=1` | skip the system and personal files (any value except empty, `0` or `false` turns it on) |
| `--gate-user-config` | with `--gate`, apply the system and personal files anyway |

## Looking at the effective settings: skarp config

```
skarp config <directory> [--origins] [--file <path>] [--format text|json]
```

`skarp config` prints, in this order:

1. the levels, lowest first, with the path of each file and whether it was found and applied (or why it was skipped);
2. the baseline file in use;
3. a table of every setting with its value and its origin (`default`, `system`, `personal`, `repository`, `command line`);
4. the thresholds in effect for each rule and level, with the preset or level that set them;
5. notes, for example when a preset has no value for a rule and the default is used instead;
6. the entries of `overrides` and how many analyzed files each one matches.

A short sample on a folder with a `.skarp.json` that sets `preset`:

```
settings levels (lowest first; each overrides the ones before):
  default       skarp's built-in defaults
  plugin        (reserved; empty)
  system        C:\ProgramData\skarp\settings.json (not found)
  personal      ...\home\settings.json (not found)
  repository    ...\proj\.skarp.json (applied)
  command line  flags

  setting                                  value                    origin
  preset                                   pmd                      repository
  complexMetric                            cognitive                default
```

- `--origins` adds, for each value, the file it came from and the values it overrode.
- `--format json` gives the same information for tools.
- `--file <path>` shows one file's effective rules and thresholds, each with its origin (a level, or `override N`), and whether Skarp treats the file as test code:

```
File src/Order.cs: production code; no override matches
  rule                 level     value                                                origin
  complex-method       notice    cognitive at least 15                                preset pmd
```

`skarp config` takes any run option, so `skarp config . --preset pmd --window 6` shows what that command line would use.

## Editing settings: skarp setup

`skarp setup` edits one settings level. It writes JSON files for you, checks the result the way `analyze` would read it, and shows a preview before it saves.

| Command | What it does |
| --- | --- |
| `skarp setup` | an interactive menu that starts by asking which level to edit |
| `skarp setup <level>` | the menu for `system`, `personal` or `repository` |
| `skarp setup --level <level> --set key=value ...` | the flag form for scripts and agents: no questions, the preview is printed and the file is written |
| `skarp setup command` | experimental: a menu that builds a command line for one run (`analyze`, `baseline`, `hotspots` or `history`) and prints it for PowerShell, cmd, bash or all three |
| `skarp setup --command <command> ...` | experimental: the flag form of `setup command` |
| `skarp setup claude` | experimental: Skarp's hooks for Claude Code; see [Check and agents](check-and-agents.md) and [Claude Code](agents/claude-code.md) |
| `skarp setup git` | experimental: Skarp's git hooks; see [Git hooks](git-hooks.md) |

The menu needs an interactive terminal. `NO_COLOR` or `TERM=dumb` draws it without colour. The repository level is the nearest `.skarp.json`; a new one is created at the git root, or in the current folder outside git. `--config <file>` picks another file.

### The flag form

```
skarp setup --level repository --set preset=pmd --set history.window=6 \
    --set "exclude=gen/**,obj/**" --set rules.magic-number=false \
    --set thresholds.long-method.warning=80
```

Output:

```
creates .../proj/.skarp.json (repository): 5 changes
  preset: (inherit) -> pmd
  history.window: (inherit) -> 6
  exclude: (inherit) -> gen/**, obj/**
  rules.magic-number: (inherit) -> false
  thresholds.long-method.warning: (preset: 71 or more) -> 80 or more
changes findings: the next run's settings fingerprint will differ from the baseline (re-record the baseline when you are done)
Saved .../proj/.skarp.json
```

Options of the flag form:

| Option | Meaning |
| --- | --- |
| `--set <key>=<value>` | set a key. Lists are comma-separated (`exclude=a/**,b/**`); `excludeMode=append` appends instead of replacing. Repeatable; edits apply in order |
| `--unset <key>` | remove the key from this level so it inherits again. Repeatable |
| `--reset` | empty the level first, then apply the other edits |
| `--dry-run` | print the preview and write nothing |
| `--print` | print the new file content to standard output instead of writing it (the preview goes to standard error) |
| `--config <file>` | the repository-level file, as for `analyze` |

Saving a file removes its comments; the original is kept as `<file>.bak`. Exit codes: 0 saved, nothing to change, or quit; 2 usage error, invalid value, failed write or no terminal; 130 Ctrl+C.

Threshold keys use the same grammar as the `--threshold` flag: `thresholds.<rule>.<level>[.<metric>]=<number|off>` and `thresholds.<rule>.<parameter>=<value>`. Score weights are edited as `score.weights.<rule>.<notice|warning|cap|decay>=<n>`.

### Building a command line

```
skarp setup --command analyze --set history.window=6 \
    --option format=sarif --option output=skarp.sarif --option gate=on --shell sh
```

prints

```
# sh, run from .../proj
skarp analyze . --format sarif --output skarp.sarif --gate --window 6
```

The built command carries only what differs from your settings files. It uses a dedicated flag when one expresses the value exactly, and otherwise `--setting <key>=<value>`. `--option` takes `directory`, `format`, `output`, `baseline`, `no-baseline`, `new-only`, `gate`, `min-severity` and `all`. `--shell` is `powershell`, `cmd`, `sh` or `markdown` (all three).

## Environment variables

| Variable | Meaning |
| --- | --- |
| `SKARP_HOME` | replaces `~/.skarp`: the folder of the personal settings file and of Skarp's tracking files |
| `SKARP_CONFIG` | a file to read as the repository level (the `--config` flag wins) |
| `SKARP_NO_USER_CONFIG` | skip the system and personal files (`1` or any value except empty, `0`, `false`) |
| `SKARP_DOTNET` | the `dotnet` executable the [semantic tier](semantic-tier.md) starts; otherwise `DOTNET_HOST_PATH`, otherwise `dotnet` on the path |
| `NO_COLOR`, `TERM=dumb` | draw the `skarp setup` menu without colour |

## Settings keys

Every key below can be written in a settings file. Types are JSON types. Many keys also have a flag; `skarp --help` lists them. The `--setting key=value` flag (experimental) sets any run setting by its file key, at the command-line level.

### General and rules

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `version` | number | 1 | format version of the file; always 1 |
| `useInRepositories` | boolean | true | machine files only: apply the file inside repositories too |
| `root` | boolean | false | repository file only: marks the repository root |
| `preset` | string | `default` | threshold preset; see [Presets](presets.md) |
| `complexMetric` | `cognitive` or `cyclomatic` | `cognitive` | metric of the complex-method rule |
| `longMethodLines` | `physical` or `source` | `physical` | line unit of long-method: every line of the declaration, or only lines that are not blank or comments |
| `recordParameters` | `exempt` or `check` | `exempt` | whether positional record parameters count for too-many-parameters |
| `magicNumbers` | `count` or `list` | `count` | magic numbers in the report: counted only, or listed |
| `rules` | object | rule defaults | switch rules on or off by id: `{ "long-method": false }` |
| `thresholds` | object | the preset | threshold overrides per rule, level and metric (see below) |
| `top` | number | 20 | places shown in the report and rows in the hotspot tables |
| `report.perFile` | number | 3 | places shown per file before "+N more" (0: no cap) |
| `saveLastRun` | boolean | true | save each `analyze` run under `.skarp/last-run/` for `skarp show` |

Rule ids are listed by `skarp explain`; see also [Rules](rules.md) and [Explain](explain.md).

`thresholds` has the shape `{ "<rule>": { "notice": 40, "warning": 100 } }`. A level can be a number, `off`, or an object of metric values for rules with several metrics (for example `{ "notice": { "wmc": 50 } }`). Rule parameters are keys of the same object (for example `"bumpy-road": { "depth": 3 }`). The flag form is `--threshold <rule>.<level>=<value>`.

### Files

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `includeTests` | boolean | false | analyze test files too |
| `include` | list of globs | none | globs that bring back files the test detection left out |
| `exclude` | list of globs | none | files and folders to leave out; wins over `include` |
| `symbols` | list | automatic | preprocessor symbols to analyze; see [Conditional compilation](conditional-compilation.md) |
| `variants.cap` | number | 32 | variants per file besides the base parse for `analyze` (0: the base view only) |
| `variants.checkCap` | number | 4 | variants per file for `skarp check`, the base parse counted in |

### History

See [History and hotspots](history-and-hotspots.md).

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `history.enabled` | boolean | true | read git history; off is faster |
| `history.window` | number | 12 | months of history read, ending at the last commit (1 to 1200) |
| `history.since` | date | none | start date of the window, such as `2026-01-01`; replaces the window length |
| `history.allHistory` | boolean | false | read the whole history |
| `history.ignoreWhitespace` | boolean | false | ignore whitespace changes in changed-line counts |
| `history.bulkFlag` | number | 300 | list commits that touch more than this many C# files |
| `history.bulkDrop` | number | none | drop commits that touch more than this many C# files |
| `history.includeBots` | boolean | false | count commits by bots |
| `history.botPatterns` | list of regular expressions | none | extra bot patterns, matched on author name or email |

### Gate

See [Baselines and gate](baselines-and-gate.md).

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `gate.enabled` | boolean | false | repository file only: turn the gate on (exit code 3 on new or worsened findings and on files past a health limit) |
| `gate.on` | list of `rule:severity` | `*:warning` | the pairs that trip the gate; `*` stands for any rule or severity |
| `gate.health.maxDrop` | number | 1.0 | the gate stops a file whose health fell by this much or more; 0 is off |
| `gate.health.newFileMin` | number | 6.5 | the gate stops a new file whose health is below this; 0 is off |
| `gate.health.floor` | number | 6.5 | the gate stops a file whose health fell and ends below this; 0 is off |

### Score

See [Health score](health-score.md).

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `score.model` | string | `grade` | the score model (the only one) |
| `score.decay` | number | 0.7 | each further finding of a rule in a file costs this share of the one before (more than 0, at most 1) |
| `score.weights` | object | built-in table | weight overrides per rule: `score.weights.<rule>.<notice\|warning\|cap\|decay>` |
| `score.worst.count` | number | 10 | files in the worst-files list |
| `score.worst.below` | number | 7 | only files scoring below this are listed as worst |

### Semantic tier and coverage

See [Semantic tier](semantic-tier.md) and [Coverage](coverage.md).

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `semantic.enabled` | boolean | true | run the semantic tier when the inputs allow |
| `semantic.solution` | path | automatic | repository file only: the solution to load, relative to the file |
| `semantic.generators` | boolean | true | run source generators in the semantic tier |
| `semantic.targets` | list | `highest` | target frameworks: `highest`, `all`, or names such as `net10.0` |
| `unusedCode.allow` | list of patterns | none | code that counts as used: documentation ids or names, with `*` and `?` |
| `namespaceCycles.includeGeneratedTargets` | boolean | false | count references to types declared only in generated code |
| `coverage` | list of globs | none | repository file only: Cobertura reports, globs relative to the file |

### Agents (experimental)

These keys belong to `skarp check` and the hooks; see [Check and agents](check-and-agents.md).

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `output.checkMaxLines` | number | 5 | finding lines of `skarp check` (the after-edit hook) |
| `output.gateMaxLines` | number | 50 | finding lines of the end-of-turn summary with `--gate` |
| `tracking.retentionDays` | number | 30 | days tracking files are kept |
| `tracking.lookbackDays` | number | 14 | days back an earlier record of the same file and session is looked for |

### Mode keys

Every list key has a generated `<name>Mode` key (`excludeMode`, `includeMode`, `gate.onMode`, `semantic.targetsMode`, `history.botPatternsMode`, `unusedCode.allowMode`, `symbolsMode`, `coverageMode`) with the values `replace` (default) and `append`.

## Per-path overrides

The repository's `.skarp.json` may hold `overrides`, an ordered list of entries. Each entry selects files and changes rules and thresholds for them:

```json
{
  "version": 1,
  "preset": "pmd",
  "overrides": [
    { "files": ["src/Legacy/**"], "thresholds": { "long-method": { "warning": 200 } } },
    { "tests": true, "rules": { "magic-number": false } }
  ]
}
```

- `files`: globs relative to the settings file. `src/Legacy/**` selects a folder.
- `excludeFiles`: globs that take files out again.
- `tests`: `true` selects test code as Skarp detects it. When several selectors are given, all must hold.
- `rules` and `thresholds`: the same grammar as at the top level. They apply after the repository's own values, in file order; a later entry wins key by key, `null` resets to the default, and flags win over everything.

Everything else stays one setting per run. The rules duplicate-code, duplicate-code-near and namespace-cycle cannot vary by path, and active-complex-file can only be switched off. `overrides` exists only in the repository file. `skarp config` lists the entries and the files each one matches:

```
Overrides (repository file; ...; 0 of 2 analyzed files matched):
  override 1: files src/Legacy/**
    changes: thresholds long-method {"warning":200}
    matches: 0 files (matches nothing)
```

## Include and exclude globs

`include` and `exclude` (and the `files` selectors of overrides) use one glob syntax:

- `*` matches within one path segment, `?` one character, `**` any number of segments.
- Paths are relative to the analyzed directory and use `/`.
- A pattern matches a file when it matches the path or one of its folders.
- A pattern without `/` can match at any depth: `Migrations` or `*.g.cs`.
- Matching is case-insensitive on Windows and macOS.

The folders `bin`, `obj`, `.git`, `.vs`, `.idea`, `node_modules`, `packages` and `artifacts` are always skipped, and generated files (by file name such as `*.g.cs` and `*.generated.cs`, or an auto-generated header comment) are left out unless you pass `--include-generated`.

Examples:

```
skarp analyze . --exclude "src/Legacy/**" --exclude "**/*.Designer.cs"
skarp analyze . --include "tests/Shared/**"
```

## Test code

Skarp leaves test code out by default, so test files do not lower the score of a project. A file is test code when:

- its nearest project is a test project: it references a common test framework package (xunit, NUnit, MSTest, TUnit, `Microsoft.NET.Test.Sdk`), sets `IsTestProject`, uses the MSTest SDK, or has a global using of a test framework namespace; or
- the file itself has a using directive for one of these namespaces: `Xunit`, `NUnit.Framework`, `Microsoft.VisualStudio.TestTools.UnitTesting`, `TUnit`.

Detection reads project files and syntax only; nothing is built.

| Want | Use |
| --- | --- |
| analyze tests too | `--include-tests` or `"includeTests": true` |
| bring back some files that were detected as tests | `--include <glob>` or `include` |
| see which files were left out and why | `--exclude-tests-why` |
| treat tests differently from production code | an override with `"tests": true` |
| check whether one file counts as test code | `skarp config . --file <path>` |

## See also

- [Baselines and gate](baselines-and-gate.md)
- [Presets](presets.md)
- [Rules](rules.md)
- [Health score](health-score.md)
- [Semantic tier](semantic-tier.md)
- [Coverage](coverage.md)
- [Check and agents](check-and-agents.md)
- [Git hooks](git-hooks.md)
- [Examples: team and personal settings](examples/team-and-personal-settings.md)
