# Changelog

All notable changes to Skarp are recorded here, in the [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) format.

## [Unreleased]

## [0.1.0] - 2026-11-24

First public release. Formats are versioned and documented but may still change before 1.0. Experimental commands are marked as such.

### Analysis

- `skarp analyze` reads C# with Roslyn and reports findings per file and method, with all metrics in a JSON document.
- Test code and generated code are detected and left out by default (`--include-tests`, `--include-generated`, `--include`, `--exclude`).
- Threshold presets modeled on published values (`default`, `codescene`, `clean-cadet`, `doomen`, `reportgenerator`, `ndepend`, `pmd`, `checkstyle`, `microsoft`, `jscpd`, `alves-percentile`); `skarp explain` describes each rule and preset.
- Inline suppression with `[SuppressMessage("skarp", "<rule>", Justification = "...")]`.

### Findings and score

- Rules for complexity, length, nesting, type and file size, parameters, path explosion, bumpy roads, complex conditions, brain methods, empty catch and magic numbers.
- Every file gets a health score from 1 to 10 (experimental); reports show the size-weighted average, the hotspot average and the worst files.
- `--sort`, `--top`, `--per-file`, `--group` and `--min-severity` shape the report.

### Baselines and gate

- `skarp baseline` records `.skarp-baseline.json`; later runs mark findings as new or worse; `--new-only` shows only those.
- `--gate` fails with exit code 3 on new or worsened findings and on files whose health score drops too far, with limits you can set.

### History

- Git history reading with a window, bot filtering and whitespace handling; `skarp history` and `skarp hotspots`.
- The `active-complex-file` rule marks files that change often and also hold flagged code.

### Semantic tier

- With a solution, design-time MSBuild adds `high-coupling`, `god-class`, `namespace-cycle`, `unused-member`, `unused-type`, `unread-member` and `used-only-by-tests`; `brain-class` is experimental and off by default.
- Skarp does not restore or build by default (`--restore` restores first).

### Duplicate code

- `duplicate-code` reports exact or renamed copies once per clone group, across files; `duplicate-code-near` reports near-copy methods as notices.

### Reports

- Text, JSON, Markdown (for `$GITHUB_STEP_SUMMARY`), GitHub workflow commands and SARIF 2.1.0.
- `skarp show` reads the saved last run or the baseline without analyzing again (experimental).
- Optional Cobertura coverage reports add `untested-complex-method`.

### Agent tools

- `skarp check` for an AI agent's hook, `skarp install claude` for Claude Code hooks and the MCP server, `skarp install git-hooks` for git, and `skarp mcp` (all experimental).

### Install and settings

- Installed as the .NET tool `Skarp` (command `skarp`, .NET 10); release archives for Windows x64 and Linux x64.
- `skarp --version` (or `-V`) prints the installed version.
- On Windows, when `skarp` is the `skarp.cmd` shim of the Native AOT tool package, `skarp install claude` writes the hook and MCP entries as `cmd.exe /c skarp ...` at local and user scope, so Claude Code can start them.
- Settings levels: defaults, system file, personal file, repository `.skarp.json`, then flags; per-path `overrides`; `skarp config` shows where each value came from; `skarp setup` edits settings in a menu.

[Unreleased]: https://github.com/sirphilliptubell/skarp-sharp/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/sirphilliptubell/skarp-sharp/releases/tag/v0.1.0
