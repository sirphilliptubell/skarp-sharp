# Skarp

Skarp is a free, open-source command-line tool that measures the health of C# code with Roslyn and points at the places worth a look.

Skarp is for C# developers, teams and AI coding agents who want to know which files and methods to clean up or review first, and who want a CI check that stops new problems from creeping in. It analyzes C# and .NET code only. It is not a security scanner.

## What it does

- Gives every C# file a health score from 1 to 10 (10 is healthy), lowered by its findings. See [docs/health-score.md](docs/health-score.md).
- Reports findings: places worth a look, such as complex, long or deeply nested methods, large types, long parameter lists and duplicated code.
- Reads git history to find hotspots: files that are both problematic and often changed.
- Records a baseline, so later runs mark findings as new or worse, and can fail a CI job (the gate).
- Runs a semantic tier on a solution (coupling, god classes, unused code, namespace cycles).
- Writes text, JSON, Markdown, GitHub annotations and SARIF.
- Offers hooks and an MCP server for AI coding agents (experimental).

Findings are places worth a look, not violations. Without `--gate`, the exit code never depends on them.

## Install

Installing it as a .NET tool needs the .NET 10 SDK.

```
dotnet tool install -g Skarp
skarp --version
skarp --help
```

Archives for Windows x64 and Linux x64 are also attached to each GitHub release: <https://github.com/sirphilliptubell/skarp-sharp/releases>. Details are in [docs/installation.md](docs/installation.md).

## First run

```
skarp analyze path/to/your/repository
```

Sample output on a one-file project:

```
Skarp: analyze ., 1 file, health 8.8 average (10 = healthy)
No warnings. 4 notices at 1 place in 1 file (magic numbers counted, not listed).

A.cs health 8.8
  notice line 1 A.M complex-method: cognitive complexity 28 (limit 15) [f89c5b33]
  notice line 1 A.M deep-nesting: nesting depth 7 (limit 3) [fb0e9baf]
  notice line 1 A.M too-many-parameters: parameters 6 (limit 4) [6f0ef961]
```

Useful next steps:

```
skarp explain complex-method       # what a rule measures and its thresholds
skarp baseline .                   # record the current findings in .skarp-baseline.json
skarp analyze . --new-only         # show only new or worsened findings
skarp analyze . --gate             # exit code 3 when something new or worse is found
skarp hotspots .                   # activity hotspot table from git history
```

[docs/getting-started.md](docs/getting-started.md) walks through this step by step.

## What is checked

Run `skarp explain` for the full list. The rules fall into these families:

- Size and complexity: `complex-method`, `long-method`, `deep-nesting`, `path-explosion`, `bumpy-road`, `complex-condition`, `brain-method`, `large-type`, `large-file`, `too-many-parameters`.
- Code smells: `empty-catch`, `magic-number`.
- Suppressions: `suppression-without-justification`.
- Duplication: `duplicate-code`, `duplicate-code-near`.
- History: `active-complex-file`.
- Design, with a solution (semantic tier): `high-coupling`, `god-class`, `namespace-cycle`, and `brain-class` (experimental, off by default).
- Unused code, with a solution: `unused-member`, `unused-type`, `unread-member`, `used-only-by-tests`.
- Coverage, with a Cobertura report: `untested-complex-method`.

Thresholds come from presets, including ones modeled on published values of other tools. See [docs/rules.md](docs/rules.md) and [docs/presets.md](docs/presets.md).

## Documentation

Start here:

- [docs/getting-started.md](docs/getting-started.md): install, first run, reading the output.
- [docs/installation.md](docs/installation.md): .NET tool, release archives, requirements.

Concepts and reference:

- [docs/rules.md](docs/rules.md): every rule, what it measures and how to improve the code.
- [docs/presets.md](docs/presets.md): threshold presets and where their values come from.
- [docs/explain.md](docs/explain.md): the `skarp explain` command.
- [docs/settings.md](docs/settings.md): settings levels, `.skarp.json`, per-path overrides.
- [docs/health-score.md](docs/health-score.md): how the 1 to 10 score is computed.
- [docs/baselines-and-gate.md](docs/baselines-and-gate.md): baselines, `--new-only` and the CI gate.
- [docs/history-and-hotspots.md](docs/history-and-hotspots.md): git history, windows, bots and hotspots.
- [docs/semantic-tier.md](docs/semantic-tier.md): the solution-level analysis.
- [docs/coverage.md](docs/coverage.md): reading Cobertura coverage reports.
- [docs/conditional-compilation.md](docs/conditional-compilation.md): how `#if` code is handled.
- [docs/reports-and-sarif.md](docs/reports-and-sarif.md): text, JSON, Markdown, GitHub and SARIF output.
- [docs/contracts.md](docs/contracts.md): exact output formats, exit codes, settings keys and finding ids.

AI coding agents:

- [docs/check-and-agents.md](docs/check-and-agents.md): `skarp check`, `skarp show` and the MCP server (experimental).
- [docs/agents/claude-code.md](docs/agents/claude-code.md): hooks and MCP for Claude Code (experimental).
- [docs/git-hooks.md](docs/git-hooks.md): git hooks that check each commit (experimental).

Worked examples:

- [docs/examples/first-run-on-an-existing-repository.md](docs/examples/first-run-on-an-existing-repository.md)
- [docs/examples/adding-a-ci-gate.md](docs/examples/adding-a-ci-gate.md)
- [docs/examples/team-and-personal-settings.md](docs/examples/team-and-personal-settings.md)
- [docs/examples/using-skarp-with-an-ai-coding-agent.md](docs/examples/using-skarp-with-an-ai-coding-agent.md)
- [docs/examples/tracking-a-hotspot-over-time.md](docs/examples/tracking-a-hotspot-over-time.md)
- [docs/examples/tuning-thresholds-to-a-vendor-preset.md](docs/examples/tuning-thresholds-to-a-vendor-preset.md)

About the project:

- [docs/comparison.md](docs/comparison.md): how Skarp relates to other code health tools.
- [docs/design-decisions.md](docs/design-decisions.md): why Skarp works the way it does.
- [ARCHITECTURE.md](ARCHITECTURE.md): how the code is organized.
- [docs/packaging.md](docs/packaging.md): how releases are built and shipped.
- [CHANGELOG.md](CHANGELOG.md): what changed in each release.
- [CONTRIBUTING.md](CONTRIBUTING.md): how to build, test and contribute.
- [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) and [SECURITY.md](SECURITY.md): conduct and reporting problems.
- [CREDITS.md](CREDITS.md) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md): sources of ideas and third-party licenses.
- [llms.txt](llms.txt): an index of these pages for AI assistants.

## Contracts

The exact contracts live in [docs/contracts.md](docs/contracts.md): the JSON document schemas, exit codes, settings keys and finding ids. Exit codes in short: 0 done, 1 tool error, 2 usage error, 3 gate tripped.

## Status

Skarp is at version 0.x. Output formats and settings are versioned and documented, but may still change before 1.0. The default text format is for people and AI agents to read and may change in any release; use JSON or SARIF for tools. Commands marked experimental (`check`, `show`, `install`, `mcp`, and the `setup` forms `command`, `claude` and `git`) and the code health score may change more freely.

## License

MIT. See [LICENSE](LICENSE).

## Credits

Skarp builds on published research and the work of other tools. See [CREDITS.md](CREDITS.md) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Contributing

Issues and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## See also

- [docs/getting-started.md](docs/getting-started.md)
- [docs/contracts.md](docs/contracts.md)
- [CHANGELOG.md](CHANGELOG.md)
