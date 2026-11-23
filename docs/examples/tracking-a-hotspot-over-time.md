# Tracking a hotspot over time

Pick the file that is both changed often and unhealthy, refactor it, and use the baseline and the history to see whether it got better.

## Goal

A hotspot is a file that changes often and also has findings, so its problems cost you every week. This walkthrough finds one, records its state, and shows how to follow it as you clean it up.

## Prerequisites

- Skarp installed ([installation](../installation.md)).
- A git repository with some history (the `git` command line must be on the PATH).
- A baseline recorded now, on the commit where you start: `skarp baseline .`.

## Steps

1. Find the hotspot. The table ranks files by commits in the history window (12 months before the last commit by default), then by complexity:

   ```sh
   skarp hotspots . --top 5
   ```

   ```text
   Activity hotspots (top 3: commits in the window, then cognitive complexity sum):
     rank commits    added  deleted  cogSum cogMax srcLines health  path
        1       3       57        0      28     28       54    8.5  src/Shop/Orders.cs
        2       1       10        0      15     15        9    9.5  src/Legacy/Old.cs
        3       1        6        0       0      0        5   10.0  src/Shop/Clean.cs
     code health: average 8.7 over 3 files (baseline 8.7, unchanged); hotspots 8.7 (baseline 8.7, unchanged)
   ```

   `commits` counts the commits that touched the file, `added` and `deleted` count lines, `cogSum` and `cogMax` are the sum and maximum of cognitive complexity over its members, and `health` is its score (10 is healthy). The last line compares the average with the baseline; "hotspots" averages the files that are changed most. See [history and hotspots](../history-and-hotspots.md).

   Other views of the same data:

   ```sh
   skarp history . --top 10                          # most active files, authors, last change
   skarp analyze . --sort hotspot --top 5            # hotspots with their findings
   skarp show --file src/Shop/Orders.cs --include-notices   # where a file's points went (experimental)
   ```

   The `active-complex-file` rule marks a file that changed often and contains code other rules flag.

2. Look at what is wrong in the top file:

   ```sh
   skarp analyze . --sort hotspot --top 1 --no-semantic
   ```

   ```text
   src/Shop/Orders.cs health 8.5 (problematic; 10 = healthy), 3 commits in the history window
     notice line 5 Orders.Price complex-method: cognitive complexity 28 (limit 15) [c52db149]
     notice line 5 Orders.Price deep-nesting: nesting depth 4 (limit 3) [99828636]
     notice line 36 Orders.Price complex-condition: boolean operators 3 (limit at least 2) [f4471140]
     +3 more in this file
   ```

   Ask how to fix a rule with `skarp explain complex-method`.

3. Refactor, with the agent or by hand. While you work, `skarp check` compares one file with its earlier state in your session (experimental). `--track` keeps a tracking file in `~/.skarp/tracked/`; the first check of a file compares it with the last commit:

   ```sh
   skarp check src/Shop/Orders.cs --track
   ```

   ```text
   Skarp: src/Shop/Orders.cs health 8.5 -> 10.0 (10 = healthy)
   ```

   Findings that went away are reported as `fixed`, once. A second identical check prints nothing, since unchanged findings are silent. `--session <id>` names the session, otherwise one file per day is shared.

4. Compare with the baseline. `analyze` and `hotspots` show the change in the health average, and `--new-only` shows only findings that are new or worse:

   ```sh
   skarp analyze . --no-semantic
   ```

   ```text
   Skarp: analyze ., 3 files, health 9.4 average, was 9.2 (10 = healthy)
   ```

   After the refactor the average rose from 9.2 (the baseline) to 9.4, and in `skarp hotspots` the top file shows health 10.0 with `cogSum` 2 instead of 28. Findings that are gone from the code are counted as fixed against the baseline.

5. Record the new state, so the improvement becomes the new floor that a CI gate protects ([baselines and the gate](../baselines-and-gate.md)):

   ```sh
   skarp baseline .
   git add .skarp-baseline.json
   git commit -m "Refactor Orders.Price; re-record Skarp baseline"
   ```

6. Follow the history of the baseline. The baseline file holds each finding's id and value. Every commit that changes it is a data point, and any old version can be read back:

   ```sh
   git log --oneline -- .skarp-baseline.json
   skarp show --from baseline --file src/Shop/Orders.cs --include-notices
   ```

   ```text
   src/Shop/Orders.cs
     recorded (file) complex-method: value 28 [c52db149]
     recorded (file) deep-nesting: value 4 [99828636]
     ...
   ```

   Skarp itself keeps no time series, only the baseline and the saved last run. If you want a chart, store `skarp analyze . --format json -o` output per release in your own build artifacts.

## Common variations

- A different time window: `--window 6` (months back from the last commit), `--since 2026-01-01`, or `--all-history`.
- Ignore pure whitespace edits in line counts: `-w`. Reformatting commits that touch many files: `--bulk-flag <n>` lists them, `--bulk-drop <n>` drops them.
- Bots: commits by bots are excluded; `--include-bots` counts them, `--bot-pattern <regex>` adds patterns.
- Member-level numbers for a tool: `skarp hotspots . --detail members --format json`.
- A hotspot is less urgent if no one touches it. Rank by findings instead with `--sort health`.
- Add coverage to see complex code that no test runs: [coverage](../coverage.md).

## See also

- [History and hotspots](../history-and-hotspots.md)
- [Health score](../health-score.md)
- [Baselines and the gate](../baselines-and-gate.md)
- [Check and agents](../check-and-agents.md)
- [First run on an existing repository](first-run-on-an-existing-repository.md)
- [Adding a CI gate](adding-a-ci-gate.md)
