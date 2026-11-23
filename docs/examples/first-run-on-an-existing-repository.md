# First run on an existing repository

Run Skarp on a repository that already exists, read the report, and record a first baseline so later runs only show what changed.

## Goal

Find out where the code is hard to maintain, without being buried in old findings. At the end you have a ranked list of places worth a look and a committed baseline file.

## Prerequisites

- Skarp installed ([installation](../installation.md)): `dotnet tool install -g Skarp`.
- A C# repository. A git work tree is best, because history adds the "most active files" view. Without git, Skarp prints a note and skips history.

## Steps

1. Run the analysis on the repository root. The example below is a tiny repository with three C# files.

   ```sh
   skarp analyze . --no-semantic
   ```

   `--no-semantic` keeps the first run fast and syntax-only. Leave it out to also run the semantic tier when a solution is found ([semantic tier](../semantic-tier.md)).

2. Read the report. Sample output:

   ```text
   Skarp: analyze ., 3 files, health 8.7 average (10 = healthy)
   No warnings. 7 notices at 3 places in 2 files (magic numbers counted, not listed).

   src/Shop/Orders.cs health 8.5
     notice line 1 (file) active-complex-file: commits 3 (limit at least 3) [f2d85533]
     notice line 5 Orders.Price complex-method: cognitive complexity 28 (limit 15) [c52db149]
     notice line 5 Orders.Price deep-nesting: nesting depth 4 (limit 3) [99828636]
     notice line 5 Orders.Price too-many-parameters: parameters 5 (limit 4) [81455159]
     notice line 36 Orders.Price complex-condition: boolean operators 3 (limit at least 2) [f4471140]

   src/Legacy/Old.cs health 9.5
     notice line 5 Old.Run deep-nesting: nesting depth 5 (limit 3) [ec7af289]
   ```

   - The first line gives the number of files and the average health score. Each file has a score from 1 to 10, where 10 is healthy. See [health score](../health-score.md).
   - Each finding line has a severity (`warning` or `notice`), a line, the member, the rule, the measured value with its limit, and a short id in brackets.
   - Findings are places worth a look, not violations. Without `--gate` the exit code does not depend on them.
   - Files are listed with the most active ones first when history is available.

3. Ask about one finding. Use the id from the report:

   ```sh
   skarp show c52db149
   ```

   This is experimental. It prints the message, the measured value, the threshold and the member's metrics, for example `cognitive 28`, `nesting 4`, `parameters 5`, from the saved last run (`.skarp/last-run/`, written by every `skarp analyze`). To see why a rule exists and how to fix code it flags:

   ```sh
   skarp explain complex-method
   ```

   See [explain](../explain.md).

4. Rank the worst files or methods instead of reading the whole list:

   ```sh
   skarp analyze . --no-semantic --sort health
   skarp analyze . --no-semantic --sort hotspot --top 5
   ```

   `health` ranks files by score. `hotspot` ranks files with findings by commits in the history window, then complexity. See [history and hotspots](../history-and-hotspots.md).

5. Look at the history and hotspot tables (needs git):

   ```sh
   skarp hotspots .
   ```

   ```text
   Activity hotspots (top 3: commits in the window, then cognitive complexity sum):
     rank commits    added  deleted  cogSum cogMax srcLines health  path
        1       3       57        0      28     28       54    8.5  src/Shop/Orders.cs
        2       1       10        0      15     15        9    9.5  src/Legacy/Old.cs
        3       1        6        0       0      0        5   10.0  src/Shop/Clean.cs
   ```

   A file that changes often and also has findings is where cleanup pays back first.

6. Record the baseline. On an old code base you do not want every old finding to block new work, so write them down once:

   ```sh
   skarp baseline . --no-semantic
   ```

   ```text
   skarp baseline: recorded 7 findings (0 warning, 7 notice) in .skarp-baseline.json
   Later runs of skarp analyze mark findings new, known or worsened against it; commit it with the code.
   ```

   Run the baseline with the same options you will use later (for example the same `--no-semantic` choice), then commit `.skarp-baseline.json`. The `.skarp/` folder with the saved runs needs no ignore entry: Skarp writes a `.gitignore` inside it.

7. Work as usual. The next `analyze` compares with the baseline. Here a new method with several findings was added:

   ```text
   Skarp: analyze ., 3 files, health 8.6 average, was 8.8 (10 = healthy)
   5 new, 0 worse than the baseline. ...
   ```

   To see only what is new or worse:

   ```sh
   skarp analyze . --new-only
   ```

## Common variations

- Fewer or more lines: `--top 10`, `--per-file 5`, `--all`, `--min-severity warning`.
- Machine-readable output: `skarp analyze . --format json -o skarp.json`.
- Leave out folders: `--exclude "**/Generated/**"` (repeatable), or an `exclude` list in `.skarp.json` ([settings](../settings.md)).
- Test code is excluded by default; `--include-tests` brings it back, `--exclude-tests-why` lists what was left out.
- Run details (thresholds, history window, timing): `--verbose`.
- A shorter history window: `--window 6` (months) or `--since 2026-01-01`.
- Findings you accept for good reasons: [baselines and the gate](../baselines-and-gate.md), or an inline `[SuppressMessage("skarp", "<rule>", Justification = "<why>")]`.

## See also

- [Getting started](../getting-started.md)
- [Rules](../rules.md)
- [Health score](../health-score.md)
- [Baselines and the gate](../baselines-and-gate.md)
- [Adding a CI gate](adding-a-ci-gate.md)
- [Tracking a hotspot over time](tracking-a-hotspot-over-time.md)
