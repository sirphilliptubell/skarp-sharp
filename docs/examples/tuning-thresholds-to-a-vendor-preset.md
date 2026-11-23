# Tuning thresholds to a vendor preset

Start from a preset that matches the limits you already know, adjust single values, and give old or special folders their own rules.

## Goal

Make Skarp judge code by limits your team accepts. Examples: you used CodeScene, NDepend, PMD or Checkstyle before and want similar thresholds, or one legacy folder should be held to a looser standard than the new code.

## Prerequisites

- Skarp installed ([installation](../installation.md)).
- A repository to try it on. All commands below also work on a copy of it.

## Steps

1. See which rules and presets exist:

   ```sh
   skarp explain
   ```

   The preset list ends with these ids: `default` (the composite, used when you pick nothing), `codescene`, `clean-cadet`, `doomen`, `reportgenerator`, `ndepend`, `pmd`, `checkstyle`, `microsoft`, `jscpd`, `alves-percentile`. Each preset is a set of published thresholds of that tool or study. Where a preset has no value for a rule, the default is used. See [presets](../presets.md).

2. Read how one rule is judged under each preset:

   ```sh
   skarp explain complex-method
   ```

   ```text
   default [cognitive]
     notice:  cognitive over 15  (Sonar S3776, PMD, detekt and Lizard: 15)
     warning: blank
   ...
   ndepend [cyclomatic]
     notice:  cyclomatic over 15  (NDepend: 15, hard to maintain)
     warning: cyclomatic over 30  (NDepend: 30, extremely complex)
   ...
   ```

   "over" means greater than, "at least" means from the value on, and a blank warning means the rule only produces notices. The text also says what the rule measures, why it matters and how to improve flagged code. See [explain](../explain.md).

   Note that the complex-method rule reads cognitive complexity whatever the preset, because most presets only have a cyclomatic value. To use a preset's cyclomatic limit, add `--complex-metric cyclomatic`.

3. Try a preset for one run and compare:

   ```sh
   skarp analyze . --no-baseline --preset codescene
   skarp analyze . --no-baseline --preset pmd
   ```

   The summary line of each run shows how many findings that preset gives on your code, and the counts differ because the limits differ. Add `--no-semantic` to compare the syntax tier only. A preset changes the limits, not the way Skarp counts; some presets come from other languages (PMD, Checkstyle) and their numbers are applied to C# counts, which `skarp explain` says.

4. Adjust single values. Use `--threshold <rule>.<level>=<value>` where the level is `notice` or `warning` and the value is a number or `off`:

   ```sh
   skarp analyze . --no-baseline --preset codescene --threshold complex-method.notice=20
   ```

   ```text
   src/Shop/Orders.cs health 8.5
     notice line 5 Orders.Price complex-method: cognitive complexity 28 (limit 20) [c52db149]
   ```

   Rules with several metrics use `<rule>.<level>.<metric>=<value>`, and rule parameters use `<rule>.<parameter>=<value>`. `--rule <rule>=off` switches a rule off.

5. Save the choice for the team in `.skarp.json`:

   ```sh
   skarp setup --level repository --set preset=codescene --set thresholds.complex-method.notice=20
   ```

   ```json
   {
     "version": 1,
     "preset": "codescene",
     "thresholds": { "complex-method": { "notice": 20 } }
   }
   ```

   Then check what is in effect, with the origin of every value:

   ```sh
   skarp config .
   ```

   ```text
   Thresholds in effect (origin: the preset, or the level that changed the value):
     rule                 level   value                                                origin
     complex-method       notice  cognitive over 20                                    repository
     deep-nesting         notice  nesting at least 4                                   preset codescene
   ```

   Changing the preset or a threshold changes the findings, so re-record the baseline afterwards: `skarp baseline .` ([baselines and the gate](../baselines-and-gate.md)).

6. Give a folder its own rules with per-path overrides. In `.skarp.json`, `overrides` is an ordered list. Each entry selects files with `files` (globs relative to the file; `src/Legacy/**` selects a folder), optionally `excludeFiles`, and optionally `"tests": true` for test code as Skarp detects it. Its `rules` and `thresholds` use the same grammar as at the top level:

   ```json
   {
     "version": 1,
     "preset": "codescene",
     "overrides": [
       {
         "files": ["src/Legacy/**"],
         "rules": { "deep-nesting": false },
         "thresholds": { "complex-method": { "notice": "off" } }
       }
     ]
   }
   ```

   Matching entries apply in file order after the repository's own values, later entries win key by key, `null` resets a key to the default, and flags win over everything. Limits: duplicate-code, duplicate-code-near and namespace-cycle cannot vary per path, and active-complex-file can only be switched off per path.

7. Check that the override hits the files you meant:

   ```sh
   skarp config . --file src/Legacy/Old.cs
   ```

   ```text
   Overrides (...; 1 of 3 analyzed files matched):
     override 1: files src/Legacy/**
       changes: rules deep-nesting off; thresholds complex-method {"notice":"off"}
       matches: 1 file

   File src/Legacy/Old.cs: production code; override 1 matches
     rule                 level     value                                                origin
     complex-method       notice    blank                                                override 1
     deep-nesting         on/off    off                                                  override 1
   ```

   The file's table shows each effective value and the entry that set it, and whether Skarp treats the file as test code.

## Common variations

- Count lines of code, not physical lines, in long-method: `--long-method-lines source` (experimental).
- Read cyclomatic complexity: `--complex-metric cyclomatic`.
- Positional record parameters: `--record-parameters check` (default `exempt`).
- Set a rule for the whole team but not for the gate: `gate.on` in `.skarp.json` decides which rules and severities fail CI ([adding a CI gate](adding-a-ci-gate.md)).
- Suppress a single place in code, with a reason: `[SuppressMessage("skarp", "complex-method", Justification = "generated state machine")]`. A suppression without a justification does not count (rule `suppression-without-justification`).
- Treat tests differently: `--include-tests` brings test code in, or an override with `"tests": true` gives it looser limits.
- A research-based reference: `alves-percentile` uses benchmark percentiles from published studies, not tool defaults.

## See also

- [Presets](../presets.md)
- [Rules](../rules.md)
- [Explain](../explain.md)
- [Settings](../settings.md)
- [Team and personal settings](team-and-personal-settings.md)
- [Adding a CI gate](adding-a-ci-gate.md)
