# Comparison with other code-quality tools

A short table of what Skarp supports next to other code-quality and code-health tools.

As of November 2026.

The table is a good-faith summary of public documentation and may be out of date or wrong in places. Check each product's own documentation before you decide. "Not known" means the documentation we read did not say, not that the feature is missing. Pricing and licenses are not compared here.

Legend: **yes**, **partial**, **no**, **not known**. Short notes follow in brackets.

| Feature | Skarp | CodeScene | NDepend | SonarQube | Teamscale | Qodana |
|---|---|---|---|---|---|---|
| Health score per file | yes (1 to 10, experimental) | yes (1 to 10) | partial (ratings, debt) | partial (A to E ratings) | partial (quality bands) | not known |
| Cyclomatic complexity | yes | yes | yes | yes | yes | not known |
| Cognitive complexity | yes | not known | not known | yes | not known | not known |
| Nesting depth, parameter count, size limits | yes | yes | yes | yes | yes | not known |
| Duplicate code detection | yes (exact, renamed, near) | yes | not known | yes | yes | yes |
| Class coupling and cohesion | yes (coupling and god class; needs a restorable build) | not known | yes | not known | not known | not known |
| Unused code | yes (needs a restorable build) | not known | yes | yes | yes | not known |
| Namespace cycles | yes (needs a restorable build) | not known | yes | not known | not known | not known |
| Layering and dependency rules | no | not known | yes | not known | yes | not known |
| Analysis of `#if` variants | yes | not known | not known | not known | not known | not known |
| Git-history hotspots | yes (local, no account) | yes | not known | not known | not known | not known |
| Trends over time | no | yes | yes | yes | yes | not known |
| Baseline and PR gate | yes | yes | yes | yes | yes | yes |
| Gate on score decline | yes | yes | not known | not known | not known | not known |
| Coverage-aware findings | partial (Cobertura only) | yes (higher tier) | yes | yes | yes | yes |
| SARIF output | yes | not known | yes | not known | not known | yes |
| AI-agent hooks and MCP server | yes (experimental) | yes (MCP) | yes (MCP) | yes (MCP) | yes (MCP, agent plugin) | not known |
| Runs offline, no server | yes | not known | yes | no (server) | no (server) | not known |
| No build needed for the basic analysis | yes | not known | no (build and PDBs) | no (build) | not known | no (build) |
| Native AOT single binary | yes | not known | not known | not known | not known | not known |
| IDE extension | no | yes | yes | yes (SonarLint) | yes | yes |
| Custom rules or query language | no | not known | yes | yes | yes | not known |
| Other languages than C# | no | yes | no (.NET) | yes | yes | yes |
| Security scanning | no (out of scope) | not known | yes | yes | not known | yes |

Where Skarp is behind: layering rules, trends over time, IDE extensions, custom rules, pull-request comments and reports with charts. See [design-decisions.md](design-decisions.md) for what Skarp deliberately leaves out.

## What Skarp does that others in this table do not

As far as the public documentation we read shows:

- It analyzes code under each `#if` symbol variant, so code that is inactive in one build is still measured. See [conditional-compilation.md](conditional-compilation.md).
- The base analysis needs no build, no restore and no PDB files. Only the optional [semantic tier](semantic-tier.md) needs a restorable build.
- Every point a file loses in its health score traces back to a finding. See [health-score.md](health-score.md).
- Threshold presets are named after their published source, so you can pick, for example, `microsoft` or `reportgenerator` and see why each number is what it is. See [presets.md](presets.md).
- One finding id is the same in the command line, the agent hook, the MCP server and SARIF.
- Hooks that run after each edit (deterministic, not LLM-written) sit beside an MCP server. Both are experimental. See [check-and-agents.md](check-and-agents.md).
- Git-history hotspots run from the local repository with no account and no server. See [history-and-hotspots.md](history-and-hotspots.md).
- An interactive menu (`skarp setup`) edits settings at system, personal and repository level.

## See also

- [README.md](../README.md)
- [rules.md](rules.md)
- [health-score.md](health-score.md)
- [baselines-and-gate.md](baselines-and-gate.md)
- [design-decisions.md](design-decisions.md)
