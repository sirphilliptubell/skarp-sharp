# Coverage

Skarp reads Cobertura coverage reports that you produce and uses them to find complex methods that no test runs.

Skarp never runs your tests. You run the tests with coverage on, then give Skarp the report. The report adds covered and coverable line counts to the JSON output and turns on one rule, `untested-complex-method`.

## Quick start

1. Produce a Cobertura report with your test run. With Coverlet:

   ```
   dotnet test --collect:"XPlat Code Coverage"
   ```

   This writes a `coverage.cobertura.xml` file under `TestResults/`. With Microsoft code coverage, ask for the Cobertura format with `--coverage-output-format cobertura`.

2. Give the report to Skarp:

   ```
   skarp analyze . --coverage TestResults/<guid>/coverage.cobertura.xml
   ```

3. Read the findings. A complex method with no covered line looks like this (sample from a small test repository):

   ```
   src/Orders.cs health 9.0
     notice line 4 Orders.Price untested-complex-method: cyclomatic complexity 11 (limit at least 6) [4bd5cee8]
   ```

Without a report, `untested-complex-method` is not evaluated and nothing about coverage is shown.

## Supported reports

Skarp reads Cobertura XML only. It detects these producers from the shape of the document and lists the producer for each report in the JSON (`coverage.reports[].producer`):

- Coverlet (`coverlet`)
- Microsoft code coverage (`microsoft`)
- ReportGenerator output (`reportgenerator`)

Other formats are refused with a message that says what the file looks like and how to get Cobertura. This applies to lcov files, Coverlet's JSON format, OpenCover XML, Microsoft's XML and binary `.coverage` files. A refused report ends the run with exit code 2.

Only the file name, the line number and whether the line had hits are used. Class, method and branch data in the report are ignored. Skarp reads the file as a stream, so large reports do not need much memory.

## Giving Skarp the reports

There are two ways. Pass the files on the command line, or list globs in `.skarp.json`.

```
skarp analyze . --coverage unit/coverage.cobertura.xml --coverage integration/coverage.cobertura.xml
```

```json
{
  "coverage": ["TestResults/**/coverage.cobertura.xml"]
}
```

- `--coverage <file>` is repeatable and replaces the `coverage` globs of the settings file.
- The globs in the settings file are relative to the folder of that file. A glob that matches no file produces a warning.
- `--coverage` works on `skarp analyze` and `skarp baseline` (and on `skarp hotspots`, which then marks hotspots whose file has an untested method).
- A file given with `--coverage` that does not exist is an error.

See [settings](settings.md) for the settings file.

### Merging several reports

Give as many reports as you have. Skarp merges them line by line. A line counts as covered when any report covers it. Use this to combine:

- unit and integration test runs,
- one report per test project,
- one report per target framework (code that runs under only one framework would otherwise look untested).

## How reports are matched to files

A report names its files in different ways: absolute paths from the build machine, paths relative to a `<sources>` entry, deterministic `/_/` paths, or URLs. Skarp maps each name to a file it analyzed in this order:

1. A full path inside the analyzed folder.
2. A `<sources>` entry of the report plus the name.
3. A deterministic path starting `/_/`, taken relative to the analyzed folder.
4. A URL, reduced to its path.
5. A unique match on the last path segments (at least two segments, or a file name that occurs only once).

If several files fit equally well, the name is ambiguous and Skarp does not guess. It warns and leaves those lines out. This is how reports made on another machine, such as a CI agent, can still be used locally. Path comparison ignores case on Windows and macOS.

The summary counts matched, unmatched, ambiguous and not-analyzed report files, and warns about the unmatched and ambiguous ones.

## What counts as coverable

- Only lines that can hold code count. Blank lines, comments, `using` lines and declaration headers are not coverable, whatever the report says.
- Lines in `#if` code that no planned symbol set turns on are not counted (see [conditional compilation](conditional-compilation.md)).
- Members and types marked `[ExcludeFromCodeCoverage]` are excluded.
- Generated code and test code are never measured.

The JSON holds covered and coverable lines per member, per file and per project (the nearest `.csproj`), and totals for the run. A file or member with no coverable line has the status `no data`. That is not the same as 0 percent covered.

## The untested-complex-method rule

A member is reported when it has:

- cyclomatic complexity of 6 or more, as Skarp counts it, and
- at least one coverable line in the reports and not one covered line.

Lambdas, local functions, async and iterator bodies are folded into their member. Members with code inactive in every analyzed variant are never reported.

Other facts about the rule:

- It is a notice with score weight 0, so it does not lower the [health score](health-score.md). It is not in the default [gate](baselines-and-gate.md). Gate it with `--gate-on untested-complex-method:notice`.
- The threshold is `--threshold untested-complex-method.notice=<n>` (default 6). Switch it off with `--rule untested-complex-method=off`.
- It says "no test ran this", not "poorly tested". Coverage is a weak measure of test quality, so Skarp uses it only for code nothing runs at all.
- The cut at complexity 6 follows the CRAP metric of Savoia and Evans (2007), where a method with no coverage passes the CRAP limit of 30 from complexity 6 on. Skarp does not compute CRAP itself.
- Deliberately untested code can be suppressed with `[SuppressMessage("skarp", "untested-complex-method", Justification = "<why>")]`.
- It is left out of `skarp check`, because a report is out of date while code is being edited.
- In a baseline, recorded findings of this rule are kept as recorded when a later run has no report, or no data for a file. They are never shown as fixed and never as new.

Run `skarp explain untested-complex-method` for the full text.

## Staleness

A report made from older code gives wrong answers, because the line numbers have moved. Skarp checks for this and warns on standard error:

| Warning | Meaning |
| --- | --- |
| `stale` | Covered lines sit on blank, comment or declaration lines. The lines moved since the report was made. |
| `missing-files` | Files named in the reports are no longer in the analyzed folder. |
| `newer-files` | Files changed after the newest report. A weaker sign, because checking out files also changes their time. |
| `beyond-end` | Report lines past the end of the file (dropped). |
| `inactive-code` | Covered lines in `#if` code that is inactive in every analyzed variant. |
| `unmatched`, `ambiguous` | Report files that match no file, or several files equally. |
| `no-data` | A report with no files at all: the tests did not run, or the code under test was not found. |

Add `--strict-coverage` to fail instead: a stale report (the `stale` and `missing-files` signs) ends `skarp analyze` with exit code 2. Use it in CI so that nobody reads findings from an old report. The weaker signs only warn.

```
skarp analyze . --coverage cov.xml --strict-coverage
```

```
skarp coverage: warning: the reports look older than the code: 1 file has covered lines on blank, comment or declaration lines (the lines moved since the report was made); produce the report again: src/Orders.cs
skarp: --strict-coverage: the coverage report is stale (see the warnings above); produce it again from the current code
```

The best fix is to run the tests and analyze in the same job, in that order.

## Hotspots and coverage

With a report, a row of the [hotspot table](history-and-hotspots.md) is marked `(untested: a complex member no test runs)` (`"untested": true` in JSON) when its file has an `untested-complex-method` finding. A frequently changed, complex file that no test runs is a good place to start.

## See also

- [Rules](rules.md)
- [Explain a rule](explain.md)
- [History and hotspots](history-and-hotspots.md)
- [Conditional compilation](conditional-compilation.md)
- [Baselines and the gate](baselines-and-gate.md)
- [Settings](settings.md)
- [Adding a CI gate](examples/adding-a-ci-gate.md)
