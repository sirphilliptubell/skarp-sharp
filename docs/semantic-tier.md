# The semantic tier

The semantic tier loads your solution into Roslyn with full type information, so Skarp can answer questions that syntax alone cannot: which types a class uses, which code nothing calls, which namespaces depend on each other.

Skarp analyzes in two tiers:

- The syntax tier parses each file on its own. It is fast, needs no build and always runs. It produces the method, type and file findings.
- The semantic tier compiles the solution in memory and binds every name to its symbol. It adds the findings below. It runs by itself when the inputs allow, and tells you why when they do not.

## What the semantic tier adds

| Rule | What it reports | Level |
| --- | --- | --- |
| `high-coupling` | A type that uses more than 20 other types (class coupling, CBO). | notice |
| `god-class` | A complex class that uses much foreign data and has low cohesion (Lanza and Marinescu's God Class strategy). | notice |
| `unused-member` | A private or internal member nothing in the solution uses. | notice |
| `unused-type` | A private or internal type nothing in the solution uses. | notice |
| `unread-member` | A private or internal field or auto-property that is written but never read. | notice |
| `used-only-by-tests` | Production code that only test projects use. A label, never gated. | notice |
| `namespace-cycle` | Namespaces that depend on each other in a cycle, with the cheapest cut to break it. | warning |

For each rule, `skarp explain <rule>` gives the definition, the roots that keep code from being reported, and the thresholds. See [explain](explain.md) and [rules](rules.md).

Unused-code findings are reported only when certain. Public and protected members are never reported, because code outside the solution may use them. Entry points, overrides, interface implementations, members called by the compiler by pattern, members with attributes, and names that appear in string literals count as used. You can list further code to treat as used with `unusedCode.allow` in `.skarp.json` (see [settings](settings.md)).

The tier also writes per-type measures to its own JSON file: class coupling (several counting variants), afferent coupling, ATFD, tight class cohesion and weighted method complexity. See [Output file](#output-file).

## How it runs

The tier does not build your projects into your output folders. It works in these steps:

1. **Choose a solution.** Skarp looks for `.sln` and `.slnx` files under the analyzed folder. If several have the same project set, it prefers `.slnx`, otherwise the most recently modified. If one solution contains the projects of all the others, it uses that one. Otherwise it uses the solution at the folder's root. If it cannot choose, it says which candidates it found. Use `--solution <file>` to pick one.
2. **Check the restore.** Each C# project needs its restore result (`obj/project.assets.json`). Skarp never restores by default. With `--restore` it runs `dotnet restore` on the solution first.
3. **Record the compiler arguments with a design-time build.** One `dotnet msbuild` run over the solution calls each project's `Compile` target with the compiler call skipped, and records the command line the compiler would have got. This is the same kind of build an IDE does. It can write into the projects' `obj/` folders. Skarp's own working files go to a temporary folder that is removed afterwards.
4. **Run source generators** (unless `--no-generators`). For each project with analyzers, Skarp runs the SDK's compiler reference-only with analyzers skipped, so only source generators run. Generated files join the compilation.
5. **Compile and measure.** Projects are compiled in memory, one at a time in dependency order, using Roslyn's own parsing of the recorded compiler arguments. Test projects are compiled and searched for uses, but their own code is not reported.

The syntax tier runs at the same time, so the semantic tier adds wall time mostly when the solution is large. It needs the .NET SDK (`dotnet`) on the PATH. If your repository has a `global.json`, the SDK it pins must be installed.

### Options

| Option | Effect |
| --- | --- |
| `--no-semantic` | Syntax tier only. `--semantic` turns it back on. |
| `--restore` | Run `dotnet restore` on the solution first. Implies the semantic tier. |
| `--solution <file>` | The `.sln` or `.slnx` to use. |
| `--no-generators` | Skip source generators. Types that need generated code are then not evaluated. `--generators` turns them back on. |
| `--all-targets` | Analyze every target framework of each project. The default is the highest one per project. |
| `--target <tfm>` | Analyze this target framework (repeatable). A project without it is skipped, with a note. |
| `--semantic-output <file>` | Where to write the semantic JSON. |
| `--include-generated-targets` | `namespace-cycle`: keep references to types declared only in generated code (experimental). |

The same choices exist in `.skarp.json`: `semantic.enabled`, `semantic.solution`, `semantic.generators`, `semantic.targets` (`highest`, `all`, or monikers such as `net10.0`), `unusedCode.allow` and `namespaceCycles.includeGeneratedTargets`. See [settings](settings.md).

### All targets or the highest target

A multi-targeted project (for example `net8.0` and `net10.0`) has one compilation per framework. By default Skarp measures the highest framework only, which is faster and enough for most repositories.

With `--all-targets`, every framework is compiled and measured. Code is reported as unused only if it is unused in every analyzed framework, so code that a different framework uses counts as used. When more than one framework is analyzed, Skarp writes one semantic JSON file per framework.

## Requirements and failure modes

The tier does not fail your run. If it cannot run, the rest of the analysis is unchanged and the report says why. Run `skarp analyze --verbose` to see the `semantic:` line.

| Situation | What Skarp says |
| --- | --- |
| No solution found | `no .sln or .slnx file found`, with a hint to pass `--solution <file>` or use `--no-semantic`. |
| Several different solutions | Lists them and asks you to choose with `--solution <file>` (or `semantic.solution`). |
| Projects not restored | `not restored: <project> (project.assets.json missing)`, with the hint to run `dotnet restore <solution>` first or add `--restore`. |
| .NET SDK missing | A hint that the tier needs the .NET SDK on the path. |
| `global.json` pins an SDK that is not installed | A hint naming `global.json` and `dotnet --list-sdks`. |
| Design-time build gave no compiler arguments | `the design-time build gave no compiler arguments`, with the first failure. |
| Some projects have no kept files | `N projects not measured`: all their files were left out as tests or by `--exclude`. |

Types that touch symbols Roslyn cannot resolve (a missing package, code that needs generated code you skipped) are counted as `not evaluated` and get no finding. Unused-code verdicts also depend on a clean compilation, so restore first.

Sample line of `skarp analyze --verbose` on a small solution:

```
semantic:   ran on Demo.slnx; target framework net10.0; 3 types measured, 0 not evaluated; 2 findings
```

Sample findings from the same run:

```
src/Lib/Billing.cs
  notice line 3 Calc.Unused unused-member: private method Unused has no reference in the solution [63e7f525]
  notice line 4 type Orphan unused-type: internal class Orphan has no reference in the solution [b39a278d]
```

When the tier did not run:

```
semantic:   not evaluated: not restored: src/Lib/Lib.csproj (project.assets.json missing) (run `dotnet restore Demo.slnx` first, or add --restore)
```

## Output file

The text report includes the semantic findings next to the syntax findings. The per-type measures go into a separate JSON file, named like this:

- With `--output report.json`, the semantic file is `report.semantic.json` beside it. With more than one target framework it is `report.semantic.<tfm>.json`.
- With `--semantic-output <file>`, that name is used.
- With `--format sarif`, `--output` names the SARIF file, so the semantic JSON is written only when you pass `--semantic-output`.
- Without either option, no semantic file is written.

`skarp baseline` records the semantic findings in `.skarp-baseline.semantic.json` next to the syntax baseline (with a framework suffix when there are several). `analyze` compares against them, and `--gate` judges both tiers. See [baselines and the gate](baselines-and-gate.md). SARIF output holds the findings of both tiers ([reports and SARIF](reports-and-sarif.md)).

The semantic JSON follows the same stability rules as the other documents; see [contracts](contracts.md).

## Notes

- `skarp check` (experimental, for AI-agent hooks) runs the syntax tier only. With `--hook claude`, the end-of-turn summary can add the semantic tier with `--semantic`. See [check and agents](check-and-agents.md).
- Class coupling and ATFD do not count .NET framework types (namespace `System` and below) by default. The semantic JSON also has the counts with framework types included.
- The thresholds for `god-class` and `high-coupling` come from published Java studies and tools (Lanza and Marinescu 2006; Checkstyle and PMD). They are not a calibration for C#. Treat these findings as places worth a look.

## See also

- [Rules](rules.md)
- [Explain a rule](explain.md)
- [Presets](presets.md)
- [Settings](settings.md)
- [Baselines and the gate](baselines-and-gate.md)
- [Conditional compilation](conditional-compilation.md)
- [Installation](installation.md)
