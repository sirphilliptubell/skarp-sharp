# Health score

Skarp gives every analyzed C# file a code health score from 1 to 10, where 10 is healthy, built only from the file's findings. This feature is experimental: the weights, the defaults and the output may still change.

Because the score is built only from findings, every point a file loses traces back to a finding you can read with `skarp show`.

## Bands

| Score | Band |
|---|---|
| 9 or more | healthy |
| 4 to below 9 | problematic |
| below 4 | unhealthy |

The 1 to 10 scale and the band limits follow the Code Health scale described by Tornhill and Borg in "Code Red: The Business Impact of Code Quality" (2022), and the band names are the ones CodeScene publishes. The formula and weights below are Skarp's own, not CodeScene's algorithm. A healthy file is not worth reworking just because it appears in a ranking.

## How findings lower the score

The model is called `grade`. For one file:

```text
score = max(1, 10 - sum over rules of min(cap, sum of base(severity) x 0.7^(k-1)))
```

In words:

- Each finding costs points. The cost depends on the rule and on the finding's severity (notice or warning), see the table below.
- Findings of the same rule in the same file are sorted heaviest first. The k-th one costs its base weight times 0.7 to the power k-1. A second finding of a rule costs 70 percent of the first, a third 49 percent.
- Each rule has a cap per file. One kind of smell cannot sink a file on its own.
- The result never goes below 1, and there is no division by file size.

Example: a file with one complex-method notice (0.5), one deep-nesting notice (0.5), one too-many-parameters warning (0.3) and one complex-condition notice (0.3) scores 10 - 1.6 = 8.4. `skarp show --file <path>` lists this arithmetic for a file:

```text
src/Orders.cs health 8.4 (10 = healthy)
  points (problematic, syntax score): complex-method 1 notice -0.5, deep-nesting 1 notice -0.5, too-many-parameters 1 warning -0.3, complex-condition 1 notice -0.3
```

JSON keeps two decimals, plus the unfloored `penalty` that ranks files which all score 1. Text reports show one decimal.

## Weights

Points out of the 9 available (10 minus the lowest score of 1). A dash means the rule has no weight of its own for that severity, and the other severity's weight is used.

| Rule | Notice | Warning | Cap per file |
|---|---|---|---|
| complex-method | 0.5 | 1.0 | 3.0 |
| long-method | 0.4 | 1.0 | 2.5 |
| deep-nesting | 0.5 | - | 2.0 |
| bumpy-road | 0.5 | - | 2.0 |
| path-explosion | 0.3 | - | 1.0 |
| complex-condition | 0.3 | - | 1.0 |
| too-many-parameters | 0.15 | 0.3 | 0.8 |
| large-type | 0.2 | - | 1.0 |
| large-file | 0.5 | 1.5 | 1.5 |
| empty-catch | 0.2 | 0.8 | 1.5 |
| magic-number | 0.05 | - | 0.5 |
| duplicate-code | 0.3 | 0.6 | 1.5 |
| god-class (solution) | 1.5 | - | 1.5 |
| high-coupling (solution) | 0.6 | - | 1.2 |
| unused-member, unused-type, unread-member (solution) | 0.1 | - | 0.5 |

Special cases:

- `duplicate-code` costs its weight once per clone group the file takes part in, and does not decay; only the cap limits it.
- These rules count nothing: `duplicate-code-near`, `suppression-without-justification`, `untested-complex-method`, `used-only-by-tests`, `namespace-cycle` and `active-complex-file`. They are labels, history or notices that inform only.
- Rules marked "solution" belong to the semantic tier, see [Semantic tier](semantic-tier.md).

[Rules](rules.md) describes each rule.

## Syntax score and full score

A `syntax` score counts only the syntax-tier rules. When the semantic tier ran (a solution was found and analyzed), scores are `full`: both tiers count, and a semantic finding counts toward the file its path names. Each file then keeps its `syntaxScore` too. Skarp never compares scores of different kinds.

`skarp check`, the hooks and the MCP server always use the syntax score, since they do not run the semantic tier per edit.

## Roll-ups

- **Average health:** the mean of the file scores, weighted by source lines.
- **Hotspot health:** the same average over the activity hotspots. It needs git history, see [History and hotspots](history-and-hotspots.md).
- **Worst files:** at most `score.worst.count` files (default 10) that score below `score.worst.below` (default 7).
- **Per folder:** JSON also holds a mean per folder.

With a baseline, the averages and each file's `baselineScore` are recomputed from the baseline's findings. Files created since the baseline show as new, and both sides of the average comparison cover only the files the baseline had.

## Where the score shows

- the first line of `skarp analyze`, and each file's heading;
- `skarp analyze --verbose`: a "Code health" block with the worst files;
- the Markdown summary, a `health` column in `skarp hotspots`, and `health` in the JSON (top level and per file);
- a `health` property on each file's SARIF artifact entry;
- `skarp show --file <path>`: the score and every contribution;
- `skarp check` and the hooks: `health <old> -> <new>` in a file's heading when it changed, or `health <score> (new file)` for a file new to git.

Generated files are not scored.

## The gate and the score

A score falls mostly through notices, which the default gate set (`*:warning`) never sees. So wherever the gate runs, it also judges each file's score, whatever `gate.on` says. There are three limits, compared on the one-decimal values shown (a drop equal to the limit blocks):

| Limit | Setting | Flag | Default | Blocks |
|---|---|---|---|---|
| drop | `gate.health.maxDrop` | `--gate-health-drop <n>` | 1.0 | a known file whose score fell by this much or more |
| new file | `gate.health.newFileMin` | `--gate-health-new-min <n>` | 6.5 | a new file scoring below this |
| floor | `gate.health.floor` | `--gate-health-floor <n>` | 6.5 | a file whose score fell at all and ends below this |

`0` turns a limit off. Values run from 0 to 10; a drop limit from 0 to 9. A file that did not get worse never blocks, however low it scores, and the project average never gates.

A file past a limit gets a heading that says which limit, and every new or worse finding of the file is listed under it, notices too, because they cost the points:

```text
src/Orders/Shipping.cs health 8.3 -> 7.1 (down 1.2, limit under 1.0)
  new line 40 Shipping.Plan complex-method (notice): cognitive complexity 17 (limit 15) [5b1e02aa]
  new line 77 Shipping.Route deep-nesting (notice): nesting depth 4 (limit 3) [0c2a77e1]
```

The exit code is 3, as for findings. To accept a drop, run `skarp baseline` and commit the file: the file's reference score becomes today's score. A renamed or moved file counts as new. See [Baselines and the gate](baselines-and-gate.md) and [Git hooks](git-hooks.md).

## Tuning

Settings live in `.skarp.json` (see [Settings](settings.md)); flags and `--setting <key>=<value>` override them.

| Key | Default | Meaning |
|---|---|---|
| `score.model` | `grade` | the score model (the only one) |
| `score.decay` | 0.7 | share of the previous finding's cost that each further finding of a rule costs; above 0 and up to 1 |
| `score.weights.<rule>.notice`, `.warning`, `.cap`, `.decay` | the table above | per-rule overrides; unset values keep the table's; an unknown rule id is an error |
| `score.worst.count` | 10 | files in the worst-files list |
| `score.worst.below` | 7 | only files below this are listed as worst |
| `gate.health.maxDrop`, `newFileMin`, `floor` | 1.0, 6.5, 6.5 | the gate limits |

Example: make complex-method notices cost more, let them cap higher, and list worst files below 8.

```json
{
  "version": 1,
  "score": {
    "weights": { "complex-method": { "notice": 1.0, "cap": 4.0 } },
    "worst": { "count": 5, "below": 8 }
  }
}
```

On the example file above this lowers the score from 8.4 to 7.9. To give a rule no weight, set its `notice`, `warning` and `cap` to 0. Changing weights changes every score, so record a new baseline afterwards if you use the score gate.

The flag `--score-model <name>` exists, but `grade` is its only value. `skarp config <dir>` shows the effective values and where each came from.

## See also

- [Rules](rules.md)
- [Settings](settings.md)
- [Baselines and the gate](baselines-and-gate.md)
- [Semantic tier](semantic-tier.md)
- [History and hotspots](history-and-hotspots.md)
- [Checks and agents](check-and-agents.md)
- [Reports and SARIF](reports-and-sarif.md)
