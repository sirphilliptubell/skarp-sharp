# Adding a CI gate

Make a pull request fail when it adds findings or lowers a file's health score, using a committed baseline and GitHub Actions.

## Goal

Old findings stay accepted. New code must not make things worse. The gate fails the build with exit code 3, annotates the pull request and optionally uploads SARIF to GitHub code scanning.

## Prerequisites

- A repository with C# code on GitHub.
- A baseline file committed to the repository. Record it first:

  ```sh
  skarp baseline . --no-semantic
  git add .skarp-baseline.json
  git commit -m "Add Skarp baseline"
  ```

  See [first run on an existing repository](first-run-on-an-existing-repository.md).
- A local run that passes, so the first CI run does not surprise you.

## Steps

1. Try the gate locally. It needs the baseline file; without one it stops with exit code 2.

   ```sh
   skarp analyze . --gate --no-semantic
   echo $?
   ```

   Exit codes: 0 done (gate passed or off), 1 tool error, 2 usage error (including `--gate` without a baseline), 3 gate tripped. A tripped gate looks like this (a new method with many findings was added):

   ```text
   Skarp: gate failed: health past its limit in 1 file (health: a drop under 1.0, new files at least 6.5, no drop below 6.5)
   ...
   src/Shop/Clean.cs health 10.0 -> 8.5 (down 1.5, limit under 1.0)
     new line 7 Clean.Pick complex-method (notice): cognitive complexity 18 (limit 15) [e21b2183]
     new line 7 Clean.Pick deep-nesting (notice): nesting depth 5 (limit 3) [af6940c5]
   ```

   What the gate judges:
   - Findings new or worse than the baseline whose rule and severity are in the gate set. The default set is every rule at warning level (`*:warning`). Change it with `--gate-on <rule>:<severity>` (repeatable) or `gate.on` in `.skarp.json`.
   - The health score of each file, whatever the gate set: a file may not lose 1.0 or more, a new file must score at least 6.5, and a file whose score fell may not end below 6.5. The flags `--gate-health-drop`, `--gate-health-new-min` and `--gate-health-floor` change the limits; 0 turns one off. These health limits are experimental. The project average never gates.

   See [baselines and the gate](../baselines-and-gate.md) for the full rules.

2. Add a workflow, `.github/workflows/skarp.yml`:

   ```yaml
   name: skarp
   on:
     pull_request:
     push:
       branches: [main]

   permissions:
     contents: read
     security-events: write   # only for the SARIF upload step

   jobs:
     skarp:
       runs-on: ubuntu-latest
       steps:
         - uses: actions/checkout@v7
           with:
             fetch-depth: 0          # full history, for hotspot data

         - uses: actions/setup-dotnet@v6
           with:
             dotnet-version: 10.0.x

         - name: Install Skarp
           run: dotnet tool install -g Skarp

         - name: Gate (annotations on the pull request)
           run: skarp analyze . --no-semantic --gate --format github

         - name: Summary
           if: always()
           run: skarp analyze . --no-semantic --format markdown >> "$GITHUB_STEP_SUMMARY"

         - name: SARIF
           if: always()
           run: skarp analyze . --no-semantic --format sarif -o skarp.sarif

         - uses: github/codeql-action/upload-sarif@v4
           if: always()
           with:
             sarif_file: skarp.sarif
   ```

   Notes on the snippet:
   - `dotnet tool install -g Skarp` installs the package from NuGet. On platforms with a native build the package carries a Native AOT executable; elsewhere it runs on the .NET 10 runtime, which `setup-dotnet` provides. Pin a version with `--version <x.y.z>` if you want reproducible runs. See [installation](../installation.md).
   - `--format github` prints GitHub workflow commands: annotations for findings that are new or worse than the baseline. When the gate trips it also prints an error line with the reason.
   - `--format markdown` is a summary meant for `$GITHUB_STEP_SUMMARY`.
   - `--format sarif` writes SARIF 2.1.0 with every finding of both tiers, so code scanning shows all findings, not only new ones. With `--format sarif`, `-o` writes only the SARIF document. See [reports and SARIF](../reports-and-sarif.md). Code scanning needs to be available for the repository; leave this step out otherwise.
   - The gate skips the system and personal settings files by default, so a CI run depends only on the repository's `.skarp.json` and the flags. `--gate-user-config` turns that off.

3. Commit the workflow and open a pull request. A pull request that adds a finding in the gate set, or drops a file's score too far, fails the `Gate` step with exit code 3.

4. When a finding is accepted, record it again and commit the new baseline in the same pull request:

   ```sh
   skarp baseline . --no-semantic
   ```

   The reviewer sees the baseline diff. After you change thresholds, a preset or rules, re-record the baseline too: `skarp setup` warns that findings change.

## Common variations

- Only some rules gate: `--gate-on complex-method:warning --gate-on duplicate-code:*`.
- Turn the gate on in the repository file so the flag is not needed: `{ "version": 1, "gate": { "enabled": true } }` in `.skarp.json` (`--no-gate` turns it off for one run).
- The semantic tier in CI: Skarp never restores or builds by default. Run `dotnet restore` before it, or pass `--restore`, and drop `--no-semantic`. Record the baseline the same way ([semantic tier](../semantic-tier.md)).
- Faster, no history: add `--no-history`. Hotspot data is not needed for the gate.
- Only what is new: `--new-only`.
- Coverage from your test step: `--coverage coverage.cobertura.xml` adds the `untested-complex-method` rule ([coverage](../coverage.md)).
- Windows runners work the same; use `runs-on: windows-latest`.
- Checks before the push: [git hooks](../git-hooks.md) give a local warning, but CI stays the gate, since `git commit --no-verify` skips hooks.

## See also

- [Baselines and the gate](../baselines-and-gate.md)
- [Reports and SARIF](../reports-and-sarif.md)
- [Installation](../installation.md)
- [Settings](../settings.md)
- [Git hooks](../git-hooks.md)
- [Team and personal settings](team-and-personal-settings.md)
