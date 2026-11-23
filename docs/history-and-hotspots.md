# History and hotspots

Skarp reads your git history to show which C# files change most, then ranks them by how often they change and how complex they are, to find the hotspots.

A hotspot is a file that changes often. Code that is complex and also changes often costs effort on every change, so that is where cleaning up pays back first. Skarp measures this from git alone. It does not need an issue tracker or any server.

## The commands

| Command | What it does |
| --- | --- |
| `skarp history <directory>` | Reads git history only. No code analysis. Prints the most active files. |
| `skarp hotspots <directory>` | Analyzes the code and joins it with the history. Prints the hotspot table. |
| `skarp analyze <directory>` | The full run. It also reads history, flags active complex files and can sort by hotspot (`--sort hotspot`). |

`<directory>` can be the repository root or a folder inside it. Only files under that folder are reported. Skarp needs `git` 2.15 or later on the PATH.

```
skarp history .
skarp hotspots . --top 10
skarp analyze . --sort hotspot
```

Sample output of `skarp history .` on a small repository (some header lines left out):

```
history:    git 2.56.1.windows.1, HEAD ce72ce9edd76, window 2025-11-23 to 2026-11-23; 6 commits read, 4 used, 2 files, +20 -0 lines; 7 commits in repository
dropped:    0 merges, 0 outside window, 1 bot, 0 bulk, 0 shallow-boundary commits; 0 generated, 0 non-C#, 1 pure-move, 0 no-change, 0 not-at-HEAD rows; 0 files excluded (tests and --exclude)

Most active files (top 2 by commits):
  commits    added  deleted authors  last change  path
        4       18        0       1  2026-11-23  src/Orders.cs
        1        2        0       1  2026-11-23  src/Basic.cs
```

Sample output of `skarp hotspots . --top 5` on the same repository:

```
Activity hotspots (top 2: commits in the window, then cognitive complexity sum):
  rank commits    added  deleted  cogSum cogMax srcLines health  path
     1       4       18        0      23     23       15    9.0  src/Orders.cs
     2       1        2        0       0      0        2   10.0  src/Basic.cs
```

The `dropped:` line tells you what Skarp left out and why. Use it when a number looks lower than you expect.

## What is measured per file

For each C# file that exists at `HEAD`, inside the history window:

| Measure | Meaning |
| --- | --- |
| `commits` | Number of commits that changed the file. This is the change frequency. A commit counts once per file. |
| `added`, `deleted` | Lines added and deleted, summed over those commits. This is churn. |
| `authors` | Number of distinct authors, by lowercase e-mail address after git's mailmap is applied. A count only, no names are stored. |
| `firstChange`, `lastChange` | Dates (author date) of the oldest and newest counted change. |

The hotspot table adds the complexity side of each file:

| Column | Meaning |
| --- | --- |
| `cogSum` | Sum of the cognitive complexity of the file's methods. |
| `cogMax` | Cognitive complexity of the worst method. |
| `srcLines` | Source lines of code in the file. |
| `health` | The file's [health score](health-score.md), 1 to 10. |

## How Skarp ranks a hotspot

The table has two axes. It is never one combined score.

1. Files are ordered by `commits` in the window, highest first.
2. Ties are broken by `cogSum`, highest first.
3. Remaining ties are broken by path, so the order is the same on every run.

Only files with at least one counted commit appear. Generated files and files that could not be read are not candidates. Test files are left out unless you ask for them (`--include-tests`).

Change frequency is the main axis because it is the more reliable signal. Complexity alone is a poor predictor of where work will happen. Frequent change alone is not a problem if the code is simple. Reading both axes together is up to you. The idea of weighing complexity by change frequency comes from Adam Tornhill's work on code hotspots (for example the book "Your Code as a Crime Scene", 2015) and from CodeScene.

If a [coverage](coverage.md) report is given, a hotspot row is also marked `(untested: a complex member no test runs)` when its file has an untested complex method.

### The active-complex-file finding

The same idea is also a finding. The rule `active-complex-file` flags a file when both hold:

- it is in the top share of changed files by commits in the window (4 percent by default), and
- it has at least one complexity-type finding (complex method, deep nesting, long method, large type, large file, too many parameters, path explosion, bumpy road or complex condition).

It is a notice, and it needs git history. It does not say the file is buggy. It says the file changed often and contains code other rules flag, so effort spent there pays back first. Run `skarp explain active-complex-file` for the thresholds, and see [rules](rules.md).

## The history window

By default Skarp reads the last 12 months. The window ends at the committer date of `HEAD`, not at today's date, so results do not change when you look at an old checkout.

| Option | Effect |
| --- | --- |
| `--window <months>` | Window length in months, ending at `HEAD`'s committer date (default 12). |
| `--since <date>` | Window start as a date, for example `2026-01-01`. Replaces `--window`. |
| `--all-history` | The whole history. |
| `--no-history` | Skip history (`analyze`, `hotspots`, `baseline`). `--history` turns it back on. |

The window is judged by committer date. The dates shown in the tables are author dates.

## What is counted and what is not

Skarp makes one pass over `git log` and applies these rules. The rules that change numbers are written into the run (`run.git.policy` in JSON) so two runs can be compared only when the rules are equal.

- Only `*.cs` files are read, in any case (`.CS` too).
- Merge commits are skipped. Their changes are already counted in the commits they merged.
- Renames are followed. Skarp asks git to detect renames at 50 percent similarity (limit 1000 files), so a file keeps its history across a move. A rename that changes no lines does not count as a change.
- Files that no longer exist at `HEAD` are not reported.
- Generated files and build output folders (for example `obj/` and `bin/`) are left out. A file renamed to `.cs` from another extension keeps its earlier history.
- Rows with no changed lines (for example a mode change) and binary files count zero lines and are not counted as changes.
- Files left out of the analysis (tests, `--exclude`) are subtracted from the totals, so the history describes the same files as the metrics.
- A shallow clone is read as far as it goes. Skarp prints a notice, and boundary commits are dropped because they look like commits that add every file. Run `git fetch --unshallow` for full history.

If `git` is missing, too old, or the folder is not a repository, Skarp prints a note and continues without history.

### Bots

Commits by bots are left out by default, so automated dependency or formatting commits do not make a file look active. A commit is a bot commit when the author name or e-mail matches one of these case-insensitive patterns: `\[bot\]`, `dependabot`, `renovate`, `github-actions`, `snyk-bot`.

| Option | Effect |
| --- | --- |
| `--bot-pattern <regex>` | Add a pattern (repeatable). It adds to the defaults. |
| `--include-bots` | Count bot commits as well. `--no-include-bots` turns it off. |

### Large commits

Mass edits (a reformat, a namespace change, a vendored library) touch many files and make all of them look active.

| Option | Effect |
| --- | --- |
| `--bulk-flag <n>` | List commits that touch more than `n` C# files (default 300). They are only listed, still counted. |
| `--bulk-drop <n>` | Drop commits that touch more than `n` C# files. Off by default. |

The commit size counts all C# files the commit touched, before the generated-file filter. Flagged commits appear in the JSON as `history.flaggedBulk`.

### Whitespace

`--ignore-whitespace` (or `-w`) ignores whitespace-only changes when counting added and deleted lines. `--no-ignore-whitespace` turns it off.

## Other options

| Option | Effect |
| --- | --- |
| `--top <n>` | Rows in the tables (default 20). |
| `--detail files\|members` | JSON detail. `hotspots` defaults to `files`, `analyze` to `members`. |
| `--format json` | The full document for tools. See [reports and SARIF](reports-and-sarif.md) and [contracts](contracts.md). |
| `--output <file>` | Also write the JSON to a file. |
| `--exclude <glob>`, `--include-tests` | Which files are analyzed. |

All history options also exist as settings in `.skarp.json` under `history`: `enabled`, `window`, `since`, `allHistory`, `ignoreWhitespace`, `bulkFlag`, `bulkDrop`, `includeBots` and `botPatterns`. See [settings](settings.md).

## Sorting by hotspot

`skarp analyze --sort hotspot` lists files with findings by number of commits in the window, then by complexity. Without git history it falls back to ordering by health. The default `analyze` report also lists the most active files first.

## Limits to keep in mind

- History says where work happened, not why. A busy file may be busy because it is central, not because it is bad.
- Squash merges and rebases change how many commits a file seems to have. Compare runs over the same window and the same rules.
- Results depend on the window. A file changed 30 times two years ago is not a hotspot now.
- On a very large repository, `--no-history` skips the history read and a shorter `--window` reads less.

## See also

- [Rules](rules.md), including `active-complex-file`
- [Health score](health-score.md)
- [Coverage](coverage.md)
- [Baselines and the gate](baselines-and-gate.md)
- [Settings](settings.md)
- [Tracking a hotspot over time](examples/tracking-a-hotspot-over-time.md)
