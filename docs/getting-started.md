# Getting started

This page takes you from nothing to your first Skarp report on a C# repository, and says what to read next.

## 1. Install

Skarp is a .NET tool. With the .NET 10 SDK installed:

```
dotnet tool install -g Skarp
skarp --version
skarp --help
```

Other ways to install (release archives, building from source, pinning a version) are in [installation.md](installation.md).

## 2. Run it on a folder

Point `skarp analyze` at the root of a repository, or at any folder that holds C# files:

```
skarp analyze path/to/your/repo
```

Skarp parses every `.cs` file with Roslyn. It leaves out generated code and test code by default. If the folder is inside a git work tree, it also reads the last 12 months of history. If it finds a `.sln` or `.slnx` file, it also runs the semantic tier (coupling, god classes, unused code). See [semantic-tier.md](semantic-tier.md).

To skip history or the semantic tier while you try it out:

```
skarp analyze path/to/your/repo --no-history --no-semantic
```

## 3. Read the report

A small sample (a single file with one tangled method), run with `--no-history`:

```
Skarp: analyze ., 1 file, health 8.5 average (10 = healthy)
No warnings. 5 notices at 1 place in 1 file (magic numbers counted, not listed).

src/Order.cs health 8.5
  notice line 5 OrderService.Price complex-method: cognitive complexity 20 (limit 15) [f704b9d7]
  notice line 5 OrderService.Price deep-nesting: nesting depth 5 (limit 3) [ee86c2a0]
  notice line 5 OrderService.Price too-many-parameters: parameters 5 (limit 4) [e84a8bc3]
  notice line 16 OrderService.Price complex-condition: boolean operators 3 (limit at least 2) [67c3d9e7]
```

How to read it:

- The first line is the run summary with the average health score. Every file gets a score from 1 to 10, where 10 is healthy. See [health-score.md](health-score.md).
- Each finding line has a severity (`notice` or `warning`), the line, the member, the [rule](rules.md) that fired, what was measured and the limit in force.
- The value in square brackets is the finding's handle. It identifies the finding in later runs. See [rules.md](rules.md#finding-ids).
- Findings are places worth a look, not violations. Without `--gate`, the exit code never depends on them.

The text format is meant for people and AI agents and may change between releases. For tools, use `--format json` (see [reports-and-sarif.md](reports-and-sarif.md)).

## 4. Ask why

Every finding and every rule can be explained:

```
skarp explain complex-method
skarp show f704b9d7
```

`skarp explain` prints what a rule measures, why it matters, how to improve the code and the thresholds in every [preset](presets.md). See [explain.md](explain.md). `skarp show` reads the saved last run and prints one finding in full. `show` is experimental.

## 5. Record a baseline

A first run on an existing repository usually finds a lot. Record what is there today, so later runs only point at new or worse findings:

```
skarp baseline path/to/your/repo
```

This writes `.skarp-baseline.json` in that folder. Commit it with the code. Later `skarp analyze` runs compare against it, and `--new-only` shows only what changed for the worse. See [baselines-and-gate.md](baselines-and-gate.md).

## 6. Where to go next

- Tune what counts as a finding: [settings.md](settings.md) and [presets.md](presets.md).
- Fail a CI job on new problems: [baselines-and-gate.md](baselines-and-gate.md) and [examples/adding-a-ci-gate.md](examples/adding-a-ci-gate.md).
- See where the trouble is in files that change often: [history-and-hotspots.md](history-and-hotspots.md).
- Give an AI coding agent feedback while it writes code (experimental): [check-and-agents.md](check-and-agents.md).
- A full walk-through on an existing repository: [examples/first-run-on-an-existing-repository.md](examples/first-run-on-an-existing-repository.md).

## See also

- [installation.md](installation.md)
- [rules.md](rules.md)
- [explain.md](explain.md)
- [settings.md](settings.md)
- [health-score.md](health-score.md)
