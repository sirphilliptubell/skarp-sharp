# Baselines and gate

A baseline records the findings a codebase has today, so later runs can show what is new or worse, and an opt-in gate can fail a CI job or a commit when new problems appear.

Skarp does not ask you to fix old code first. You record the current state once, commit it, and from then on only changes are judged.

## Record a baseline

```
skarp baseline <directory>
```

```
skarp baseline: recorded 3 findings (0 warning, 3 notice) in .../proj/.skarp-baseline.json
Later runs of skarp analyze mark findings new, known or worsened against it; commit it with the code.
```

The file `.skarp-baseline.json` is written in the analyzed directory. Commit it with the code. `skarp baseline` takes the same options as `skarp analyze` (preset, thresholds, include and exclude, history, semantic tier), because the baseline must be recorded with the settings you will compare with. `--baseline <file>` writes to, or compares with, another file.

The file is plain JSON and deterministic: no dates, the findings grouped under the path of their file (files sorted by path, findings sorted by rule and place), one finding per line, so merges conflict only where the same findings changed.

```json
{
  "version": 1,
  "type": "baseline",
  "toolVersion": "0.1.0",
  "preset": "default",
  "complexMetric": "cognitive",
  "longMethodLines": "physical",
  "settingsFingerprint": "730290a631800de4",
  "files": {
    "src/Customer.cs": [],
    "src/Order.cs": [
      { "rule": "complex-method", "place": "Shop.Order::Price", "value": 17 },
      { "rule": "deep-nesting", "place": "Shop.Order::Price", "value": 5 }
    ]
  }
}
```

What it holds:

- the settings that change values (`preset`, `complexMetric`, `longMethodLines`) and a short fingerprint of every setting that changes the finding set;
- the commit it was written at, when git history is read (absent without git, with history off, or in a shallow clone);
- every analyzed file as a key of `files` (an empty array means "analyzed, no findings"), so a file created later is told apart from a file that has no findings;
- findings that belong to no single file (clone groups, namespace cycles) in a `repository` list;
- every finding: its id and its measured value.

### The semantic baseline

When the [semantic tier](semantic-tier.md) runs (a solution that can be loaded), `skarp baseline` also writes `.skarp-baseline.semantic.json` next to the first file. It has the same format plus the solution and target frameworks, and holds the findings of the semantic rules (coupling, god classes, unused code, namespace cycles). With several target frameworks, one file per framework is written, with the framework in the name (`.skarp-baseline.semantic.net10.0.json`).

If the semantic tier did not run (`--no-semantic`, or the inputs did not allow it), an existing semantic baseline is kept as it is. Semantic entries that a later run could not judge are neither fixed nor new. Without a semantic baseline, a gated run says so and does not gate semantic findings.

## What new, worsened and improved mean

`skarp analyze` compares every finding with the baseline by id:

| State | Meaning |
| --- | --- |
| new | the id is not in the baseline |
| worsened | the id is in the baseline and the measured value is now higher (for example cognitive complexity 17 now 30) |
| known | the id is in the baseline and the value is the same or lower |
| fixed | a baseline entry no longer found in the code |

A finding that got better (a lower value) stays known; a finding whose entry is gone is counted as fixed. Findings without a measured value (for example an empty catch block) can be new or known but never worsened. Findings that measure activity rather than code (active-complex-file) are never worsened.

Sample after a method was made more complex and another method added:

```
src/Order.cs health 8.9 -> 8.7
  worse line 5 Order.Price complex-method (notice): cognitive complexity 17 -> 30 (limit 15) [4b777f49]
  worse line 5 Order.Price deep-nesting (notice): nesting depth 5 -> 7 (limit 3) [a701d5fa]
  notice line 5 Order.Price too-many-parameters: parameters 6 (limit 4) [244833b6]
  new line 15 Extra.M too-many-parameters (notice): parameters 5 (limit 4) [5a3ee763]
```

The heading line shows the file's health score against the baseline (`8.9 -> 8.7`); see [Health score](health-score.md).

Useful options:

- `--new-only` shows only findings that are new or worse than in the baseline.
- `--no-baseline` does not compare with the baseline file.
- `--format github` prints GitHub workflow commands (annotations) for new and worsened findings; `--format markdown` prints a summary for `$GITHUB_STEP_SUMMARY`. See [Reports and SARIF](reports-and-sarif.md).

### How finding ids survive edits

A finding id has three parts, `rule|path|place`:

```
complex-method|src/Order.cs|Shop.Order::Price
```

- the rule id;
- the file path relative to the analyzed directory;
- the place: the type and member name without parameter text (`Shop.Order::Price`), the type name for type rules, or nothing for file rules. Overloads get an ordinal in declaration order (`Price`, `Price#2`). Lambdas and local functions are listed under their parent member.

There are no line numbers in an id. Adding code above a method, reformatting, or editing the method body does not change its id. The finding stays known, or becomes worsened when its value went up.

Renaming or moving a member, or moving it to another file, changes its id. The old entry counts as fixed and the new finding as new. To make this easier to spot, Skarp pairs a new finding with a vanished entry of the same rule, the same file and the same value, and notes "possibly renamed from ..." (the finding is still counted as new):

```
new line 5 Order.Cost too-many-parameters (notice): parameters 6 (possibly renamed from Order.Price) (limit 4) [49364c02]
```

Each finding also has a short handle (the number in brackets, eight hex digits of a hash of the id). `skarp show <handle>` accepts it, or a prefix of four or more digits. `skarp show` is experimental.

### Settings changes

If the rule settings in effect differ from those recorded (another preset, a changed threshold, a rule switched off, a changed include or exclude list), the report says so:

```
Baseline: rule settings changed since the baseline; new and fixed counts include settings effects; re-run `skarp baseline` to reset.
```

In that case the new and fixed counts mix real changes with effects of the settings. Record the baseline again when you change settings on purpose.

## The gate

The gate turns the comparison into an exit code. It is off by default; without it the exit code never depends on findings.

```
skarp analyze . --gate
```

The gate needs a baseline file. With `--gate` and no baseline file, or with `--no-baseline`, Skarp exits with code 2.

The gate trips (exit code 3) when either of these holds:

1. a finding in the gate set is new or worsened against the baseline, in the syntax or the semantic tier;
2. a file is past a health limit (below).

Otherwise the run ends with code 0. Exit codes: 0 done (gate passed or off), 1 tool error, 2 usage error, 3 gate tripped.

### The gate set: --gate-on and gate.on

The gate set is a list of `rule:severity` pairs. A rule id or `*` for any rule, and `notice`, `warning` or `*` for the severity. The default is `*:warning`: every rule, warnings only. New notices do not trip the default gate.

```
skarp analyze . --gate --gate-on complex-method:warning --gate-on duplicate-code:notice
skarp analyze . --gate --gate-on "*:*"
```

`--gate-on` is repeatable and replaces the set from the settings file. In `.skarp.json`:

```json
{
  "version": 1,
  "gate": {
    "enabled": true,
    "on": ["*:warning", "too-many-parameters:notice"]
  }
}
```

`gate.enabled` turns the gate on from the repository file, so `skarp analyze` alone then gates; `--no-gate` turns it off for one run. Machine-level files cannot switch the gate on. When the gate is on, the system and personal settings files are skipped (see [Settings](settings.md)).

Composite rules cannot be named in the gate set; name their parts. The `used-only-by-tests` label is never gated. Findings that are new or worse but outside the gate set are counted and mentioned, not hidden:

```
skarp: gate passed: no new or worsened findings in the gate set (*:warning); 4 other new or worsened findings outside the gate set.
```

A tripped gate prints the findings and a verdict line at the top:

```
Skarp: gate failed: 4 findings new or worse (gate set *:*)
```

### Health limits

The gate also judges each file's [health score](health-score.md) against the baseline, whatever the gate set. This catches files that get worse through notices. There are three limits; `0` turns a limit off.

| Setting | Flag | Default | The gate stops |
| --- | --- | --- | --- |
| `gate.health.maxDrop` | `--gate-health-drop <n>` | 1.0 | a file whose score fell by this much or more |
| `gate.health.newFileMin` | `--gate-health-new-min <n>` | 6.5 | a new file whose score is below this |
| `gate.health.floor` | `--gate-health-floor <n>` | 6.5 | a file whose score fell at all and ends below this |

A file that did not get worse never blocks, and the project average never gates. A file is new when the baseline does not list it, so a renamed file is new too. Without a semantic baseline, the limits compare the syntax scores with the baseline.

```
Skarp: gate failed: health past its limit in 1 file (health: a drop under 1.0, new files at least 6.5, no drop below 6.5)
```

The health limits are experimental, like the score itself.

## Use in CI

A minimal job: check out the code (with full history if you want hotspots), install Skarp, and run the gate.

```yaml
- uses: actions/checkout@v7
  with:
    fetch-depth: 0
- uses: actions/setup-dotnet@v6
  with:
    dotnet-version: 10.0.x
- run: dotnet tool install -g Skarp
- run: skarp analyze . --gate --format github
```

Exit code 3 fails the job. `--format github` adds annotations for new and worsened findings; to write a summary on the run page, run `skarp analyze . --gate --format markdown >> "$GITHUB_STEP_SUMMARY"`. See [Adding a CI gate](examples/adding-a-ci-gate.md) for the full walk-through, and [Reports and SARIF](reports-and-sarif.md) for code scanning output.

Notes:

- Commit `.skarp-baseline.json` (and `.skarp-baseline.semantic.json` if you use the semantic tier). The CI run reads them from the checkout.
- Use the same Skarp version and settings in CI as when the baseline was written; the report notes when they differ.
- The gate skips the system and personal settings files, so a local setting cannot change the verdict.
- `skarp install git-hooks` adds a local pre-commit check that uses the same baseline and gate, and optionally a pre-push check. It is a fast local warning: it can be skipped with `git commit --no-verify`, so keep the gate in CI as well. See [Git hooks](git-hooks.md). The hooks are experimental.

## When to re-baseline

Run `skarp baseline .` again and commit the new file when:

- you accept the new or worsened findings of a change on purpose (the gate message says so: fix them, or accept them with `skarp baseline` and commit the file);
- you changed settings that change findings (preset, thresholds, rules, include or exclude, test handling) and want to start from the new rules;
- you cleaned up a lot and want the baseline to forget the findings that are fixed, so they cannot come back unnoticed;
- the report says the baseline was recorded with another preset or metric than this run uses.

Do not re-baseline just to make a red build green without looking: the diff of `.skarp-baseline.json` shows in review which findings were accepted. Re-baselining on a branch is a normal part of review, and the file is small and sorted so the diff is readable.

## See also

- [Settings](settings.md)
- [Health score](health-score.md)
- [Rules](rules.md)
- [Reports and SARIF](reports-and-sarif.md)
- [Git hooks](git-hooks.md)
- [Check and agents](check-and-agents.md)
- [Adding a CI gate](examples/adding-a-ci-gate.md)
- [Getting started](getting-started.md)
