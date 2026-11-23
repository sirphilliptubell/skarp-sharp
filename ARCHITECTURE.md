# Architecture

How Skarp is built, for people who want to read or change the code.

Skarp is a .NET 10 command-line tool that measures C# code health with Roslyn. All logic lives in one library, `SkarpSharp.Core`. The executable, `SkarpSharp.Cli`, is a thin shell around it.

## Repository layout

| Path | What is there |
|---|---|
| `src/SkarpSharp.Core` | All logic: discovery, parsing, metrics, findings, health score, baselines, history, clones, reporting, the semantic tier, hooks, the MCP server. |
| `src/SkarpSharp.Cli` | The `skarp` executable (`Program.cs`) and the packaging settings (Native AOT, tool package). |
| `tests/SkarpSharp.Tests` | Unit, golden-file, contract and end-to-end tests (TUnit, with Verify for snapshots). |
| `tests/oracle` | Small C# projects used as fixed inputs: edge cases for metrics, and a coupling fixture. |
| `tools/ci` | `jit-aot-compare.sh` and `compare-json.mjs`: run a JIT build and a Native AOT build on the same inputs and compare the output. |
| `tools/oracle` | A script and notes to compare Skarp's numbers with Microsoft's code-metrics engine. |
| `docs` | User documentation. |

Namespaces are `SkarpSharp.*`, one per folder of `SkarpSharp.Core` (for example `SkarpSharp.Findings`, `SkarpSharp.Health`).

## Two tiers

**Syntax tier.** Roslyn parses each `.cs` file into a syntax tree. No build, no restore and no installed SDK are needed. All metrics and most rules work here. This tier is fast and always runs.

**Semantic tier.** Some questions need symbols, not just syntax: class coupling, cohesion, god classes, unused code, namespace cycles. For these Skarp loads the solution with design-time MSBuild and builds a Roslyn compilation. It needs a restorable solution and an SDK. It never restores or builds unless you pass `--restore`. If the inputs are missing it is skipped with a note, not an error.

Semantic findings are written to their own JSON file and their own baseline, so a run without the tier never marks them as fixed. The code is in `Semantic/` (`SemanticRunner`, `DesignTimeBuild`, `CompilationPipeline`, `TypeWalker`, `UnusedCode`, `NamespaceCycles`, `GodClassRule`). See [semantic-tier.md](docs/semantic-tier.md).

## Pipeline

One `analyze` run goes through these stages. Each folder is under `src/SkarpSharp.Core`.

1. **Settings.** `Configuration/` merges defaults, the system file, the personal file, the repository `.skarp.json` and the flags into one options object, and remembers where each value came from. See [settings.md](docs/settings.md).
2. **Discovery.** `Discovery/` walks the folder, applies include and exclude globs, and detects generated and test code (`FileDiscovery`, `Glob`, `TestDetection`).
3. **Parsing and metrics.** `Analysis/Analyzer.cs` parses files in parallel. `Syntax/` collects members (methods, accessors, lambdas and so on). `Metrics/` computes cyclomatic, cognitive, NPath, nesting depth, statement counts and line kinds. Each syntax tree is dropped as soon as its metrics are taken, and the per-file results are then aggregated (`Analysis/Aggregation.cs`).
4. **Conditional compilation.** `Conditionals/` plans the `#if` symbol variants of a file, so inactive code is measured too. See [conditional-compilation.md](docs/conditional-compilation.md).
5. **Clones.** `Clones/` turns code into token streams and finds copied code (exact and renamed) and near copies of whole methods.
6. **Findings.** `Findings/FindingEngine.cs` judges the metrics with the rules in `Rules.cs`, using thresholds from `Presets.cs` and the user's overrides. `Suppressions.cs` handles `[SuppressMessage("skarp", ...)]`. Composite findings such as `brain-method` combine other rules.
7. **Health score.** `Health/` turns each file's findings into a score from 1 to 10 with weights per rule and severity. See [health-score.md](docs/health-score.md).
8. **Baselines and the gate.** `Baselines/` writes and reads `.skarp-baseline.json` and marks findings new, known or worsened. `Findings/Gate.cs` and `Health/HealthBaseline.cs` decide the exit code. See [baselines-and-gate.md](docs/baselines-and-gate.md).
9. **History.** `History/` runs the `git` command (through CliWrap), parses `git log` output and joins commit activity with file metrics to give hotspots. See [history-and-hotspots.md](docs/history-and-hotspots.md).
10. **Coverage.** `Coverage/` reads Cobertura reports and maps covered lines to members. Skarp never runs tests.
11. **Ranking.** `Ranking/` sorts files or members worst first for `--sort`.
12. **Reporting.** `Reporting/` writes the text, Markdown, GitHub annotation and SARIF formats. The JSON model lives in `Model/`. See [reports-and-sarif.md](docs/reports-and-sarif.md).

Around the pipeline:

- `Commands/` holds the command classes. `SkarpCommand` dispatches `analyze`, `hotspots`, `history`, `baseline`, `config`, `explain`, `check`, `show`, `install` and `setup`; `Program.cs` hands `mcp` to `Mcp/`.
- `Agent/` and `Install/` hold the hook logic for AI agents and git, and the installers. `Mcp/` holds the MCP server. These are experimental. See [check-and-agents.md](docs/check-and-agents.md).
- `Setup/` is the interactive settings menu.

## Native AOT constraints

`SkarpSharp.Cli` publishes as a Native AOT executable, so the code must be trimmer-safe. The build treats warnings as errors, with a short list of known warnings from inside Roslyn that are kept visible.

- No reflection over your own types.
- JSON uses source generation only: serializer contexts such as `Model/JsonContext.cs` list every type that is written or read. A new JSON type must be added to a context.
- Settings are registered by hand (`Configuration/SettingsRegistry.cs`) instead of being discovered by reflection.
- Invariant globalization is on, so number and date formatting never depends on the machine's culture.

## JIT = AOT equality

The same source must give equal output whether it runs on the JIT or as a Native AOT binary. `tools/ci/jit-aot-compare.sh` runs both on the same inputs (edge cases, a git repository, the semantic tier, SARIF, the hooks) and compares JSON and text, ignoring only timings and paths. If a change makes the two differ, the change is wrong, not the check.

## Determinism

The same input must give the same output. Files and results are ordered by path, not by thread completion. Finding ids do not depend on line numbers, and sorting has explicit tie-breaks (rule order, then path, then member). Apart from labelled timing fields, output does not depend on the clock or the machine's culture. When you add output, sort it.

## Performance and memory

- Files are parsed in parallel (`--parallelism`, default the processor count). Server garbage collection is on, because workstation GC was much slower with many parsing threads.
- Only the facts a later stage needs are kept per file. Syntax trees are not held after metrics are computed.
- Git history is read in one `git log` pass, with paths interned (`History/PathTable.cs`).
- The syntax tier needs no MSBuild. The semantic tier is the slow one, so it is optional and by default uses one target framework per project (the highest).

## Adding a rule

Say you add a syntax-tier rule called `my-rule`. Touch these places:

1. `Findings/RuleCatalog.cs`: add an id to `RuleIds` and a `RuleInfo` entry (title, unit, metrics, one-line summary and the long explanation that `skarp explain` prints).
2. `Findings/Rules.cs`: write the function that judges a member, type or file and returns a `Finding`.
3. `Findings/FindingEngine.cs`: call it for each member, type or file.
4. `Findings/Presets.cs`: add thresholds for it in each preset, with the source named.
5. `Health/ScoreSettings.cs`: give it a default weight in the health score.
6. `Configuration/SettingsRegistry.cs`: register any setting it has beyond the standard thresholds.
7. If a user can sort by it, update `Ranking/Measures.cs` and `Configuration/RankChoice.cs`.
8. If it needs a new metric, add it under `Metrics/` or `Syntax/` and a field on the member model in `Model/`. Then add the field to the JSON model and update the contract tests.
9. Tests: a rule test in `tests/SkarpSharp.Tests/Findings`, a golden or snapshot update if output changes, and a case in `tests/oracle` if it needs a tricky construct.
10. Docs: [rules.md](docs/rules.md) and, if thresholds differ by preset, [presets.md](docs/presets.md).

A rule that needs symbols goes in `Semantic/` and the `Semantic` list of the catalog instead, and its findings use the semantic output and baseline.

## See also

- [CONTRIBUTING.md](CONTRIBUTING.md)
- [docs/design-decisions.md](docs/design-decisions.md)
- [docs/contracts.md](docs/contracts.md)
- [docs/packaging.md](docs/packaging.md)
- [docs/rules.md](docs/rules.md)
