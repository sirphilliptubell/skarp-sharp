# Explain

`skarp explain` tells you what a rule measures, why it matters, how to improve flagged code and which thresholds each preset uses.

## Commands

```
skarp explain                 # list every rule and every preset
skarp explain <rule>          # the long explanation of one rule
skarp explain complex-method
```

`explain` takes at most one rule id. An unknown id or a preset id (such as `pmd`) prints the rule and preset list to standard error and exits with code 2. An extra argument prints a one-line error and exits with code 2. It does not read your settings files: it shows the built-in thresholds of every preset, not the values you have overridden. To see the values in force for your repository, run `skarp config <directory>` (see [settings.md](settings.md)).

The same explanations are available to AI agents through the experimental MCP server (`skarp mcp`, tool `explain`). See [check-and-agents.md](check-and-agents.md).

## The list

`skarp explain` without an argument prints:

1. Each rule id with its one-line explanation.
2. Which rules are on by default. Every rule is on except `brain-class` (experimental). `untested-complex-method` runs only when you give a coverage report.
3. Each preset id with the source it is based on.

Use it to find the id you need for `--rule`, `--threshold` or `--preset`.

## Reading one rule

`skarp explain deep-nesting` prints (shortened):

```
deep-nesting: Deep nesting (judged per member)

Code nested many levels deep forces the reader to keep every enclosing condition in mind.

What it measures: the deepest nesting of control structures in the member (if, switch, loops, try), the outermost
one being level 1. An else-if chain does not deepen; a lambda or local function adds one level for its body.

Why it matters: each level is a condition the reader has to remember to understand the innermost line. ...

How to improve: return early (guard clauses) instead of wrapping the rest of the body in an if, invert conditions,
use continue in loops, and extract the inner block into a method with a name that says what it does.

Thresholds per preset ("over" = greater than; "at least" = from the value on; a blank warning means notice only):
  default
    notice:  nesting over 3  (CodeScene: depth 4)
    warning: blank
  codescene
    notice:  nesting at least 4  (CodeScene defaults: 4)
    ...
  doomen: no value (the default is used)
  ...
Change a value: --threshold deep-nesting.notice=<n>, --threshold deep-nesting.warning=<n> or =off on skarp analyze or baseline, or thresholds.deep-nesting in .skarp.json.
```

The parts:

- Title line: the rule id, its name and the unit it judges (member, type, file, namespace or clone group).
- One-line summary: the same line as in the list.
- What it measures: the exact definition, the unit, what is not counted and, where it matters, the choices you can switch (for example `--complex-metric cyclomatic`).
- Why it matters: the reason the rule exists.
- How to improve: concrete refactorings.
- Extra sections for some rules: a note on counting differences, how to mark a deliberate exception (`duplicate-code`), the rule sets (`god-class`).
- Thresholds per preset: for every [preset](presets.md), the notice and warning level and the source of the value in parentheses. "over 15" means greater than 15. "at least 15" means 15 or more. A blank warning means the rule only reports notices. "structural" means the rule has no number: it fires on a shape (an empty `catch`, an unused member). "no value (the default is used)" means the preset leaves that rule to the default. Some rules list more than one metric (for example `default [cognitive]` and `default [cyclomatic]`), and some list parameters such as `copyLines=20`.
- The last line says how to change a value.

The composite rules (`brain-method`, `brain-class`) and `untested-complex-method` print their definition and parameters in place of the preset table.

## Explaining one finding

`explain` is about rules. To see one finding from your own code in full, with the measured value, the threshold in force, its place and the metrics of the member, use `skarp show` with the handle printed in square brackets in the report (experimental):

```
skarp analyze .
skarp show f704b9d7
```

`show` reads the saved last run, so it needs no new analysis. See [rules.md](rules.md#finding-ids) for what the handle is.

## See also

- [rules.md](rules.md)
- [presets.md](presets.md)
- [settings.md](settings.md)
- [getting-started.md](getting-started.md)
- [check-and-agents.md](check-and-agents.md)
