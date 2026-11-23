# Rules

Every Skarp rule: what it measures, its default thresholds, which tier judges it, whether it is on by default, how to suppress a finding and how findings get stable ids.

The numbers below are the defaults of the `default` preset. Other [presets](presets.md) change them. `skarp explain <rule>` always prints the current text and thresholds of your installed version, so trust it over this page if they differ. See [explain.md](explain.md).

Findings are places worth a look, not violations. Every finding is a `notice` or a `warning`. A rule whose warning column says "none" only reports notices.

## Tiers

- Syntax tier: judged from the syntax tree of one file at a time. It needs no build and no solution. Always available.
- Semantic tier: needs the Roslyn semantic model of a solution (types, symbol references). It runs when Skarp finds a `.sln` or `.slnx` file and can load it, and is skipped otherwise. Use `--no-semantic` to turn it off. See [semantic-tier.md](semantic-tier.md).

## Syntax rules

| Rule | Unit | What it measures | Notice | Warning |
|---|---|---|---|---|
| `complex-method` | member | Cognitive complexity (G. Ann Campbell, SonarSource): one point per break in linear flow, more for nesting. `--complex-metric cyclomatic` switches to cyclomatic complexity. | cognitive over 15 | none (cyclomatic: over 30) |
| `deep-nesting` | member | Deepest nesting of control structures (the outermost is level 1). | over 3 | none |
| `long-method` | member | Physical lines of the member (`--long-method-lines source` counts only lines with code). | over 50 | over 70 |
| `large-type` | type | Source lines or WMC (sum of the members' cyclomatic complexity) of a class, struct, record or interface, partial parts merged. | over 100 lines or WMC over 20 | none |
| `large-file` | file | Source lines of the whole file. | over 1000 | over 5000 |
| `too-many-parameters` | member | Declared parameters; constructors are allowed one more. | over 4 (constructors over 5) | 8 or more |
| `path-explosion` | member | NPath (Nejmeh, 1988), the number of acyclic execution paths. | over 200 | none |
| `bumpy-road` | member | Separate chunks of nested logic (nesting depth 2) in one member. | 2 or more bumps | none |
| `complex-condition` | member | Boolean operators (`&&`, `\|\|`) in one expression. | 2 or more | none |
| `empty-catch` | member | A `catch` block without statements. | with a comment, a specific exception type or a `when` filter | an empty bare `catch` or `catch (Exception)` without a comment |
| `magic-number` | member | Count of unexplained numeric literals in a member body (ignores -1, 0, 1, 2, constants, attribute arguments and a few other forms). One finding per member. Counted in the summary, not listed unless `--magic-numbers list` or `--all`. | any | none |
| `duplicate-code` | clone group | Copied code across files: exact copies and copies with renamed identifiers or changed literals. | 100 tokens and 10 lines per copy | a copy of 50 or more lines, or 3 or more copies of 20 or more lines |
| `duplicate-code-near` | clone group | Whole methods that are copies with small edits. | similarity 0.75 and 10 lines | none |
| `suppression-without-justification` | member | A `[SuppressMessage("skarp", ...)]` without a justification. The suppression does not count. | always | none |
| `brain-method` | member | A member that has `long-method`, `complex-method` and `deep-nesting` findings at once, reported as one finding. | all three parts | none |
| `brain-class` | type | A large non-static type with a brain method and at least 10 methods, reported as one finding. Experimental. | see `skarp explain brain-class` | none |
| `active-complex-file` | file | A file in the top 4 percent of changed files by commits in the history window that also has a complexity-type finding. Needs git history. | top 4 percent | none |
| `untested-complex-method` | member | A member with cyclomatic complexity 6 or more that no test covers, read from a Cobertura report. Not evaluated without `--coverage`. See [coverage.md](coverage.md). | cyclomatic 6 or more | none |

Notes:

- `complex-method`: the default is cognitive complexity, whatever the preset. A preset without a cognitive threshold uses the default's cognitive threshold. With `--complex-metric cyclomatic` the default thresholds are over 15 (notice) and over 30 (warning).
- `duplicate-code` and `duplicate-code-near` are left out of `skarp check` (one file cannot decide them). `analyze` reports them. Test files follow the test setting, and clones between test and production code are never reported.
- `duplicate-code-near` has score weight 0 and `brain-method` adds no score of its own: they group or label findings and do not lower the [health score](health-score.md) further.
- `active-complex-file` and `untested-complex-method` need history and a coverage report. See [history-and-hotspots.md](history-and-hotspots.md) and [coverage.md](coverage.md).
- Sources: the default thresholds name their sources in `skarp explain`. `bumpy-road`, `complex-condition` and `brain-method` follow CodeScene's published descriptions of those code smells, `path-explosion` follows Nejmeh (1988), and `duplicate-code` takes its minimum size from SonarQube and PMD CPD. `brain-class`, `unused-*`, `unread-member`, `used-only-by-tests` and `namespace-cycle` are Skarp's own designs; `skarp explain` names the tools they are modeled on. [../CREDITS.md](../CREDITS.md) lists the sources in full.

## Semantic rules

| Rule | Unit | What it measures | Notice | Warning |
|---|---|---|---|---|
| `high-coupling` | type | Class coupling (CBO): distinct other types a type uses. .NET framework types in `System` are not counted. | over 20 | none |
| `god-class` | type | Lanza and Marinescu's God Class (2006): WMC at least 47, ATFD (foreign data accessed) over 5 and TCC (tight class cohesion) below one third, all at once. | all three hold | none |
| `unused-member` | member | A private or internal member that nothing in the solution uses. | when certain | none |
| `unused-type` | type | A private or internal type that nothing in the solution uses. | when certain | none |
| `unread-member` | member | A private or internal field or auto-property that is written but never read. | when certain | none |
| `used-only-by-tests` | member | Production code that only test projects use. A label, never gated. | always | none |
| `namespace-cycle` | namespace | Namespaces that depend on each other in a cycle, in a solution's non-test projects. | none | every cycle |

The god-class values are Java statistics from the original book, not a C# calibration. The rule parameter `god-class.preset` chooses another rule set (`iplasma`, `atfd-2`).

## On by default

Every rule is on by default except `brain-class`, which is experimental. `untested-complex-method` is on whenever you give a coverage report and not evaluated without one. Semantic rules run only when the semantic tier runs.

Switch a rule on or off:

```
skarp analyze . --rule brain-class=on
skarp analyze . --rule magic-number=off
```

or in `.skarp.json`:

```json
{ "version": 1, "rules": { "magic-number": false, "brain-class": true } }
```

Change a threshold:

```
skarp analyze . --threshold complex-method.notice=12
skarp analyze . --threshold long-method.warning=off
skarp analyze . --threshold duplicate-code.notice.tokens=60
```

or in `.skarp.json`:

```json
{ "version": 1, "thresholds": { "complex-method": { "notice": 12 }, "long-method": { "warning": "off" } } }
```

`<rule>.<level>=<value>` sets the main threshold, `<rule>.<level>.<metric>=<value>` one metric of a rule with several, and `<rule>.<parameter>=<value>` a rule parameter such as `duplicate-code.copyLines`. A value is a number or `off`. Settings can also differ per path (the `overrides` list), per user and per system. See [settings.md](settings.md).

## Suppress a finding

For a deliberate exception, mark the member or type in code and say why:

```csharp
using System.Diagnostics.CodeAnalysis;

[SuppressMessage("skarp", "deep-nesting", Justification = "Mirrors the legacy pricing table.")]
public decimal Price(int kind, bool member, bool coupon, int qty, string region)
{
    // ...
}
```

Rules for suppression:

- The category is `skarp` (or `skarp.<anything>`). The check id is the rule id.
- `UnconditionalSuppressMessage` works too.
- On a member the suppression covers that member. On a type it covers the type with its members and nested types. An assembly-level `[assembly: SuppressMessage(...)]` with a `Target` covers the named types, members or namespaces in any analyzed file.
- A `Justification` is required. A suppression without one does not count, and Skarp reports a `suppression-without-justification` notice at the attribute.
- A clone group is suppressed when every copy lies in suppressed code. If only some copies are suppressed, the rest still form a group when two or more remain.
- Suppressed findings are left out of the report, the counts, the gate, the baseline and the score. `--show-suppressed` (experimental) lists them, and the JSON output holds them under `suppressed`.
- File findings (`large-file`, `active-complex-file`) and path-less findings other than clone groups (`namespace-cycle`) are not suppressed with attributes. Turn the rule off or raise its threshold in settings, for example with an `overrides` entry for the files concerned.

To leave out whole files or folders instead, use `--exclude <glob>` or `exclude` in `.skarp.json`.

## Finding ids

Every finding has a stable id, so that baselines, the gate and SARIF can recognize the same finding in a later run even when lines move.

The id has the form `<rule>|<path>|<place>`:

```
complex-method|src/Order.cs|Demo.OrderService::Price
```

- `<rule>` is the rule id.
- `<path>` is the file relative to the analyzed folder, with `/` separators.
- `<place>` is the member as `Namespace.Type::Member` (no parameters, `.ctor` for constructors), the full type name for type findings (nested types joined with `+`, generic arity as `` `n ``), or empty for file findings.
- A second member with the same name in a file gets `#2`, then `#3` (`Demo.C::Run#2`). Lambdas are `<parent>/lambda1`, local functions `<parent>/<name>`.
- A second finding with the same id (for example several empty `catch` clauses in one member) gets a suffix `~2`, `~3`, in source order.
- Findings that span files have no path: a namespace cycle is `namespace-cycle||namespace <name> (<assembly>)`, and a clone group is `duplicate-code||clone <hash>`, where the hash comes from the normalized tokens at the start of the first copy.

The id contains no line numbers, so edits elsewhere in a file keep it. Renaming or moving a member, type or file changes its ids. The run then marks the old and new finding as a probable rename where it can tell.

The short code in square brackets in the text report (`[f704b9d7]`) is the handle: the first 8 hex characters of the SHA-256 of the id. `skarp show <handle>` accepts the handle or any prefix of 4 or more characters. Two different ids can share a handle (it is 32 bits), and `show` then lists both. The full id is in the JSON output and in SARIF as `partialFingerprints`.

Rule ids and finding ids are stable from the first release. Thresholds, presets and default severities are not, and may change.

## See also

- [explain.md](explain.md)
- [presets.md](presets.md)
- [settings.md](settings.md)
- [baselines-and-gate.md](baselines-and-gate.md)
- [health-score.md](health-score.md)
- [semantic-tier.md](semantic-tier.md)
- [reports-and-sarif.md](reports-and-sarif.md)
