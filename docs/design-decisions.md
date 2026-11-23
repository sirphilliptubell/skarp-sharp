# Design decisions

The main choices behind Skarp and the reasons for them.

Each entry has the decision, the reason and what follows from it.

## 1. Roslyn only, C# only

- **Decision.** Skarp analyzes C# with Roslyn syntax trees and semantic models. It has no other language support and no language-agnostic parser.
- **Reason.** Roslyn understands C# fully: partial types, `#if`, records, lambdas, generated code. A generic parser has to guess at these.
- **Consequence.** Numbers can be checked against Microsoft's own metrics engine. Other languages, compiled IL and PDB analysis are out of scope.

## 2. No security scanning

- **Decision.** Skarp measures code health. It does not look for vulnerabilities, secrets or vulnerable dependencies.
- **Reason.** Dedicated tools do that better, and mixing the two blurs what a finding means.
- **Consequence.** Use Skarp next to a security scanner, not instead of one.

## 3. Findings are "worth a look", not verdicts

- **Decision.** A finding says where to look. Skarp never calls it a violation or an error. Without `--gate` the exit code never depends on findings.
- **Reason.** Metrics are heuristics. A long method may be fine; a short one may not be. People should decide.
- **Consequence.** Wording stays neutral, every finding can be explained with `skarp explain`, and inline suppression requires a written justification.

## 4. Two tiers

- **Decision.** The syntax tier runs on any folder with no build. The semantic tier adds class coupling, cohesion, god classes, unused code and namespace cycles, and needs a restorable solution.
- **Reason.** Most value should work on first contact, with no setup. Deeper checks should be available, not required.
- **Consequence.** The semantic tier is skipped with a note when its inputs are missing, and never restores or builds unless asked (`--restore`). Its findings have their own output file and baseline. See [semantic-tier.md](semantic-tier.md).

## 5. Stable finding ids

- **Decision.** A finding is identified by rule, file and member name. Line numbers are not part of it. Overloads and lambdas are numbered in order.
- **Reason.** Edits move lines all the time. An id that changes with them would make every baseline stale.
- **Consequence.** The same id appears in the command line, hooks, the MCP server and SARIF. Renaming or moving a member makes its findings new. This is a known limit.

## 6. Baselines instead of fixing everything

- **Decision.** `skarp baseline` records today's findings in a committed `.skarp-baseline.json`. Later runs mark each finding as new, known or worsened.
- **Reason.** A real codebase has years of findings. Asking a team to fix them all before they can use a tool means it never gets used.
- **Consequence.** The gate (`--gate`) fails only on new or worsened findings. Accepting a change means re-recording the baseline. See [baselines-and-gate.md](baselines-and-gate.md).

## 7. A health score you can trace

- **Decision.** Each file gets a score from 1 to 10, lowered by its findings with documented weights per rule and severity. Bands are 9 or more healthy, 4 up to 9 problematic, below 4 unhealthy.
- **Reason.** A single number is easy to track and gate on, but only if people can see where it came from.
- **Consequence.** Every lost point traces to a finding, and `skarp show --file <path>` lists where a file's points went. The score is experimental. The gate also judges the score with three limits (a file may not drop by a set amount, a new file must start at a minimum, a worsened file may not end below a floor). See [health-score.md](health-score.md).

## 8. Hotspots are activity, not risk

- **Decision.** Hotspots rank files by how often they change in git history and how complex they are. They are called activity hotspots and carry no claim about bugs.
- **Reason.** History shows where people work, which is a useful pointer. It does not prove a defect.
- **Consequence.** History uses the local git repository, with no account and no server. Whitespace, very large commits and bot commits have explicit, documented settings. See [history-and-hotspots.md](history-and-hotspots.md).

## 9. Native AOT and RID-specific tool packages

- **Decision.** Skarp is a .NET 10 tool, built as a Native AOT executable. It ships as a RID-specific tool package for Windows x64 and Linux x64, with a framework-dependent package for other platforms.
- **Reason.** A fast cold start matters for hooks that run after every edit, and for CI.
- **Consequence.** No reflection, source-generated JSON and warnings as errors. A test principle follows: the JIT build and the AOT build must produce equal output. See [packaging.md](packaging.md) and [../ARCHITECTURE.md](../ARCHITECTURE.md).

## 10. Versioned contracts

- **Decision.** The JSON output, the baseline file, the settings file and SARIF carry a schema version and are described in [contracts.md](contracts.md). The human-readable text format is explicitly not a contract.
- **Reason.** Tools and scripts need something stable to read. People need freedom to improve the text.
- **Consequence.** Contract tests guard the shape of the machine-readable output. Tools should read JSON or SARIF, not scrape text.

## 11. Determinism

- **Decision.** The same inputs give the same output: sorted by path, with explicit tie-breaks, independent of thread timing and culture.
- **Reason.** Baselines, golden tests, caches and CI comparisons all break if output wobbles.
- **Consequence.** Parsing is parallel, but results are ordered afterwards. New output must be sorted.

## 12. Thresholds come from published sources, with credit

- **Decision.** Thresholds are grouped in named presets, each credited to its source (for example `codescene`, `microsoft`, `reportgenerator`, `ndepend`, `doomen`). The default preset is the one with the best-supported numbers, and any value can be overridden.
- **Reason.** There is no single right threshold. Showing where each number comes from lets users judge it and pick the one their team already trusts.
- **Consequence.** `skarp explain <rule>` prints the thresholds per preset with their sources. See [presets.md](presets.md) and [explain.md](explain.md). Ideas and formulas are described in our own words and credited in [../CREDITS.md](../CREDITS.md).

## 13. Zero setup, then settings in layers

- **Decision.** A run works with no configuration. Settings come in layers (defaults, system file, personal file, repository `.skarp.json`, flags), and per-path `overrides` handle legacy folders.
- **Reason.** First use should be one command. Teams and individuals still need to tune things, and to see where a value came from.
- **Consequence.** `skarp config` shows the effective settings and their origins. A gate in CI skips personal and system files by default, so results do not depend on who runs it. See [settings.md](settings.md).

## 14. Offline, no telemetry

- **Decision.** Skarp reads your files and your local git repository. It makes no network calls and sends nothing anywhere.
- **Reason.** Source code is private, and a code-health tool should not need an account or a server.
- **Consequence.** Everything runs locally, including the MCP server, which talks over standard input and output. When the semantic tier starts `dotnet`, Skarp sets the .NET CLI's own telemetry opt-out for that child process.

## 15. Deterministic hooks for AI agents, not generated advice

- **Decision.** Skarp gives AI coding agents facts: hooks that report what an edit made worse, and an MCP server with a few tools. It does not edit code or write review comments with an LLM.
- **Reason.** An agent needs fast, repeatable feedback it can act on. The agent already writes the fixes.
- **Consequence.** The same finding ids and thresholds apply for people and agents. See [check-and-agents.md](check-and-agents.md).

## 16. Experimental labelling

- **Decision.** Commands and outputs whose shape may still change are marked experimental in the help and in these docs: `check`, `show`, `install`, `mcp`, the `--setting` flag, `--show-suppressed`, the health score and its gate limits, and the `brain-class` rule.
- **Reason.** Honest labels let people adopt what is stable and try the rest without surprise.
- **Consequence.** Everything not labelled experimental is meant to stay stable, and changes to it are recorded in [../CHANGELOG.md](../CHANGELOG.md).

## 17. Measure, do not run or fix

- **Decision.** Skarp never runs your tests and never rewrites your code. It reads coverage reports you produce, and it reports.
- **Reason.** Running tests is slow, environment-specific and not its job. Auto-fixing needs trust Skarp has not earned.
- **Consequence.** Coverage-aware findings need a Cobertura file from a tool such as Coverlet. See [coverage.md](coverage.md).

## See also

- [../ARCHITECTURE.md](../ARCHITECTURE.md)
- [comparison.md](comparison.md)
- [rules.md](rules.md)
- [contracts.md](contracts.md)
- [packaging.md](packaging.md)
