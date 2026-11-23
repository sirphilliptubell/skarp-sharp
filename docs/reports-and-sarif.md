# Reports and SARIF

Skarp can print its findings as text, JSON, a Markdown summary, GitHub annotations or a SARIF log. This page says what each format is for and how to use it in CI.

## Formats

Pick one with `--format` on `skarp analyze`:

| Format | For | Notes |
|---|---|---|
| `text` (default) | people and AI agents reading a terminal | Not a contract: the wording may change in any release. |
| `json` | tools | The stable analysis document ([contracts](contracts.md)). `--output <file>` (`-o`) also writes it to a file. |
| `markdown` | a CI job summary (`$GITHUB_STEP_SUMMARY`) | `analyze` only. |
| `github` | annotations on a pull request | `analyze` only. Workflow commands for new and worsened findings. |
| `sarif` | GitHub code scanning and SARIF viewers | `analyze` only. SARIF 2.1.0 with every finding of both tiers. |

`skarp hotspots`, `skarp history` and the other commands print text or JSON; see their `--help`.

Text, Markdown and annotation output is written for people and may change. Tools should read `--format json` or SARIF.

## Text report

The default report leads with the health summary, then the places most worth a look (most active files first when history is available), then tables. `--verbose` adds the run details, the health block and each rule's one-line explanation. `--top <n>` and `--per-file <n>` limit how many places are shown. `--no-save` skips saving the run under `.skarp/last-run/` (which `skarp show` reads).

## Markdown summary

```sh
skarp analyze . --gate --format markdown >> "$GITHUB_STEP_SUMMARY"
```

The summary has the number of findings by severity, the code health overview, the top places and, when a baseline exists, which findings are new or worse. A line states when no baseline file was found and suggests `skarp baseline`.

## GitHub annotations

```sh
skarp analyze . --gate --format github
```

Prints GitHub workflow commands. With a baseline, only new and worsened findings are annotated. Without one, only warnings are annotated, and Skarp says so on standard error. GitHub shows at most 10 annotations of each kind per step, so Skarp prints at most 10 warnings and 10 notices. When the gate trips, an `::error` line states why. See [baselines and the gate](baselines-and-gate.md).

## SARIF

```sh
skarp analyze . --format sarif -o skarp.sarif
```

The log is SARIF 2.1.0 and validates against the official schema. It has one run that holds:

- every rule of the catalog with its full explanation (the same text as `skarp explain`);
- every finding of the syntax tier and, when it ran, the semantic tier (warnings as `warning`, notices as `note`);
- file locations as relative URIs under the base id `SRCROOT`, plus logical locations (type and member);
- `partialFingerprints` with the key `skarpFindingId/v1`, the stable finding id ([contracts](contracts.md));
- `baselineState` (`new`, `unchanged` or `updated`) from the baselines when they exist;
- `runs[0].properties.profileVersion`, the version of what Skarp writes inside SARIF.

SARIF holds no timings or absolute paths, so the same input gives a byte-identical file. A run is capped at 25,000 results.

### Upload to GitHub code scanning

```yaml
name: skarp
on:
  push:
    branches: [main]
  pull_request:

permissions:
  contents: read
  security-events: write

jobs:
  skarp:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7
        with:
          fetch-depth: 0
      - uses: actions/setup-dotnet@v6
        with:
          dotnet-version: 10.0.x
      - run: dotnet tool install -g Skarp
      - run: skarp analyze . --format sarif -o skarp.sarif
      - uses: github/codeql-action/upload-sarif@v4
        with:
          sarif_file: skarp.sarif
          category: skarp
```

Pin the actions to a commit SHA if your policy requires it. Code scanning must be available for the repository.

Findings are places worth a look, not violations. If you do not want them shown as code scanning alerts, upload the SARIF file as a build artifact for a SARIF viewer instead.

## See also

- [Baselines and the gate](baselines-and-gate.md)
- [Adding a CI gate](examples/adding-a-ci-gate.md)
- [Contracts](contracts.md): the JSON, SARIF profile and exit codes
- [Check, show and agents](check-and-agents.md): the agent-facing text output
