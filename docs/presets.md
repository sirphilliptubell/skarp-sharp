# Presets

A preset is a named set of thresholds taken from a published tool, guideline or study, so you can judge your code against a yardstick you already know.

Choose one with `--preset <id>` on `skarp analyze` or `skarp baseline`, or `"preset": "<id>"` in `.skarp.json`. A preset only changes thresholds. A rule that a preset has no value for uses the `default` preset's value. The list below matches `skarp explain`, which prints every preset's value for every rule (see [explain.md](explain.md)). Values may change in later releases.

```
skarp analyze . --preset codescene
skarp explain long-method
```

## The presets

| Id | Based on | Notes |
|---|---|---|
| `default` | A composite: for each rule, the best supported published value. Values measured against labelled C# code come first, then C#-specific sources, then authority and fit with how Skarp counts. | Each value names its own source in `skarp explain`. Used when you pick nothing. |
| `codescene` | CodeScene's published C# default thresholds. | The vendor does not publish its counting rules and units, so Skarp's counting is used. Sets values for complex-method, deep-nesting, long-method, large-file, too-many-parameters, bumpy-road and complex-condition. |
| `clean-cadet` | Clean-CaDET (University of Novi Sad): rules measured against human labels on C# code. | Sets complex-method, long-method and large-type. The data sets are published on Zenodo under CC BY 4.0. |
| `doomen` | Dennis Doomen's C# coding guidelines (the AV rules). | Sets long-method (15 statements) and too-many-parameters (over 3). |
| `reportgenerator` | The risk-hotspot thresholds of ReportGenerator, a .NET coverage report tool (Apache-2.0). | Sets complex-method (cyclomatic over 15) and path-explosion. |
| `ndepend` | NDepend's default rule and metric thresholds. | NDepend counts on IL (nesting, logical lines). Skarp's syntax counts approximate that, with logical lines taken as statements. Sets complex-method, deep-nesting, long-method, large-type, too-many-parameters and high-coupling. |
| `pmd` | The default properties of PMD's design rules. These are Java rules. | Java counting differs in places, and the values are applied to Skarp's C# counts. |
| `checkstyle` | The default properties of Checkstyle's checks. These are Java checks. | Line counts follow Checkstyle (physical lines). |
| `microsoft` | Microsoft's CA1502 "avoid excessive complexity" (an opt-in rule) and CA1506 class coupling. | Uses Microsoft's own counting (Visual Studio code metrics). Needs the semantic tier for coupling. See [semantic-tier.md](semantic-tier.md). |
| `jscpd` | The default minimum clone size of jscpd: 50 tokens and 5 lines. | Stricter than the default for duplicate-code. All other rules use the default. |
| `alves-percentile` | Benchmark percentiles from Yamashita et al. (2016), using the method of Alves et al., derived from Java open-source systems. | The 80th percentile is the notice and the 90th the warning. Research values, not tool defaults. |

## How thresholds differ

A sample of notice thresholds. "-" means the preset has no value and the default applies. See `skarp explain <rule>` for every value, the warning levels and the exact comparison ("over" means greater than, "at least" means from the value on).

| Rule | default | codescene | clean-cadet | doomen | ndepend | pmd | checkstyle | alves-percentile |
|---|---|---|---|---|---|---|---|---|
| complex-method | cognitive over 15 | cyclomatic at least 9 | cyclomatic over 10 | - | cyclomatic over 15 | cognitive at least 15 | cyclomatic over 10 | cyclomatic over 10 |
| deep-nesting | over 3 | at least 4 | - | - | over 4 | at least 3 (if statements only) | over 2 (if statements only) | - |
| long-method | lines over 50 | lines over 70 | lines over 50 | statements over 15 | statements over 20 | statements at least 60 | lines over 150 | lines over 36 |
| too-many-parameters | over 4 (constructors 5) | over 4 (constructors 5) | - | over 3 | over 5 | at least 10 | over 7 | - |
| large-file | 1000 source lines | 1000 | - | - | - | - | 2000 physical lines | 553 |

The `complex-method` row shows each preset's own metric. Skarp judges cognitive complexity whatever the preset (`default` and `pmd` have cognitive values). For a preset that only has cyclomatic values (`codescene`, `clean-cadet`, `reportgenerator`, `ndepend`, `checkstyle`, `microsoft`, `alves-percentile`), those values apply with `--complex-metric cyclomatic`; otherwise the default cognitive threshold is used.

Two other presets mostly change one area:

- `microsoft`: with `--complex-metric cyclomatic`, complex-method uses Microsoft's cyclomatic count and notices over 25. High-coupling uses Microsoft's count and notices over 95.
- `jscpd`: duplicate-code notices at 50 tokens and 5 lines instead of 100 tokens and 10 lines.

The god-class rule has its own rule sets, chosen with the rule parameter `god-class.preset` (`lanza-marinescu-2006`, `iplasma` or `atfd-2`), not with a global preset. See [rules.md](rules.md).

## How to choose

- Starting out: stay on `default`. It mixes sources so that each value rests on the strongest evidence available.
- You already use CodeScene, NDepend, PMD, Checkstyle or the Doomen guidelines, and want the same yardstick: choose that preset. Mind the counting notes: Skarp's counts are not identical to those tools.
- Strict duplicate detection: `jscpd`.
- Looking at a codebase as a statistical outlier: `alves-percentile` marks the code that is worse than most open-source systems in the study. It is Java data, so treat it as a research reference.
- One or two thresholds differ from what you want: keep `default` and override them with `--threshold`. A preset and overrides combine. See [settings.md](settings.md) and [examples/tuning-thresholds-to-a-vendor-preset.md](examples/tuning-thresholds-to-a-vendor-preset.md).

Changing the preset changes which findings exist, so a [baseline](baselines-and-gate.md) recorded with one preset is not comparable with a run on another. Record the baseline again after you change it.

On the same sample file, `--preset codescene` reports a nesting depth of 5 against "limit at least 4" instead of "limit 3", and `--preset ndepend` reports no too-many-parameters finding for 5 parameters (its limit is over 5) and a nesting limit of 4.

## See also

- [explain.md](explain.md)
- [rules.md](rules.md)
- [settings.md](settings.md)
- [comparison.md](comparison.md)
- [examples/tuning-thresholds-to-a-vendor-preset.md](examples/tuning-thresholds-to-a-vendor-preset.md)
