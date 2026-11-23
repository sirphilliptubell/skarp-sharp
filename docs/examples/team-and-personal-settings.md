# Team and personal settings

Put the settings the whole team shares in `.skarp.json` in the repository, and keep your own preferences in a personal file.

## Goal

Everyone on the team, and CI, gets the same findings and the same gate. Each developer can still choose things that do not change findings, such as how many places are listed.

## Prerequisites

- Skarp installed ([installation](../installation.md)).
- A repository to put `.skarp.json` in.

## How the levels fit together

Settings come from these levels, lowest first. Each level overrides the ones before it:

1. Skarp's defaults.
2. The system file: `%ProgramData%\skarp\settings.json` on Windows, `/etc/skarp/settings.json` on Linux, `/Library/Application Support/skarp/settings.json` on macOS.
3. The personal file: `~/.skarp/settings.json` (the environment variable `SKARP_HOME` replaces `~/.skarp`).
4. The repository file: `.skarp.json` in the analyzed folder or a parent, up to the git work tree root.
5. Flags on the command line.

Objects merge key by key. Lists replace, unless the file also sets `"<name>Mode": "append"`. `null` resets a key to Skarp's default. Details: [settings](../settings.md).

## Steps

1. Create the team file. The flag form of `skarp setup` edits one level without a menu and prints a preview:

   ```sh
   skarp setup --level repository --set "exclude=**/Generated/**" --set thresholds.too-many-parameters.notice=6 --set gate.enabled=true
   ```

   ```text
   creates .skarp.json (repository): 3 changes
     exclude: (inherit) -> **/Generated/**
     thresholds.too-many-parameters.notice: ... -> 7 or more parameters, ...
     gate.enabled: (inherit) -> true
   changes findings: the next run's settings fingerprint will differ from the baseline (re-record the baseline when you are done)
   Saved .skarp.json
   ```

   The file is plain JSON:

   ```json
   {
     "version": 1,
     "exclude": [ "**/Generated/**" ],
     "thresholds": { "too-many-parameters": { "notice": 6 } },
     "gate": { "enabled": true }
   }
   ```

   Without flags, `skarp setup repository` opens an interactive menu with an explanation pane and a preview before saving (it needs a terminal). The tool keeps a `.bak` copy of an existing file and removes comments when it saves, so edit by hand if you want to keep comments. Use `--dry-run` to preview and `--print` to write the result to standard output.

2. Re-record the baseline, because findings changed, and commit both files:

   ```sh
   skarp baseline .
   git add .skarp.json .skarp-baseline.json
   git commit -m "Share Skarp settings and baseline"
   ```

3. Set your own preferences in the personal file. Choose settings that change what you see, not what counts as a finding:

   ```sh
   skarp setup --level personal --set top=10 --set history.window=6
   ```

   This writes `~/.skarp/settings.json`:

   ```json
   {
     "version": 1,
     "top": 10,
     "history": { "window": 6 }
   }
   ```

4. Check what is in effect and where each value comes from:

   ```sh
   skarp config .
   skarp config . --origins
   ```

   ```text
   settings levels (lowest first; each overrides the ones before):
     default       skarp's built-in defaults
     system        ... (not found)
     personal      ~/.skarp/settings.json (applied)
     repository    .skarp.json (applied)
     command line  flags

     setting                                  value                    origin
     exclude                                  **/Generated/**          repository
     top                                      10                       personal
     gate.enabled                             true                     repository
   ```

   `--origins` adds the file of each value and the values it overrode. `skarp config . --format json` gives the same for tools.

5. Remember that the gate ignores the system and personal files. With `--gate` the personal level shows as skipped:

   ```text
   personal      ~/.skarp/settings.json (skipped: --gate (--gate-user-config includes it))
   ```

   This keeps CI and local gate runs identical. Anything that decides the gate belongs in `.skarp.json`. For other runs, `--no-user-config` or the environment variable `SKARP_NO_USER_CONFIG=1` skips the system and personal files, and `--no-config` reads no file at all.

## Common variations

- One run only: `--setting history.window=3` (experimental; any settings key, repeatable) or the dedicated flag such as `--window 3`. Flags win over every file.
- Build a command line from your settings instead of saving them: `skarp setup command` (experimental), or `skarp setup --command analyze --set preset=pmd --option format=sarif --option output=skarp.sarif --shell sh`.
- A different repository file: `--config <file>` or `SKARP_CONFIG=<file>`.
- A machine-wide default for every user: the system file, edited with `skarp setup system`.
- Remove a key from one level so it inherits: `skarp setup --level repository --unset gate.enabled`. Empty a level: `--reset`.
- Different rules for different folders: [per-path overrides](tuning-thresholds-to-a-vendor-preset.md).
- Hooks have their own menus: `skarp setup claude` and `skarp setup git` (experimental), see [using Skarp with an AI coding agent](using-skarp-with-an-ai-coding-agent.md).

## See also

- [Settings](../settings.md)
- [Presets](../presets.md)
- [Baselines and the gate](../baselines-and-gate.md)
- [Adding a CI gate](adding-a-ci-gate.md)
- [Tuning thresholds to a vendor preset](tuning-thresholds-to-a-vendor-preset.md)
