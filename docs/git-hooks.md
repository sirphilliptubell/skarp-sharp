# Git hooks: a Skarp check before each commit (and push)

Skarp can check the staged C# files before each commit, and the whole project before each push; this is experimental and needs git 2.54 or newer.

Skarp can warn you before a commit makes code health worse. The hook is a **fast local warning, not the gate**: anyone can skip it with `git commit --no-verify`, and a clone without it is not checked at all. The real gate stays in CI (`skarp analyze --gate`, with branch protection; section 7, and [Baselines and the gate](baselines-and-gate.md)).

## 1. Quick start

```sh
skarp baseline .                     # once: record today's findings (the hook blocks only new or worse ones)
git add .skarp-baseline.json && git commit -m "Skarp baseline"
skarp install git-hooks              # the commit hook, in this clone's git config
skarp install git-hooks --pre-push   # or: the commit hook and a push hook
```

`skarp install git-hooks` shows a preview and asks before writing (`--yes` skips the question, `--dry-run` only previews, `--print` prints the git commands). Running it twice changes nothing. `skarp install git-hooks --check` tests the setup, `--remove` takes it out. `skarp setup git` (or the "Git hooks" row of `skarp setup`) shows the same options in a menu.

Then commit as usual. A commit whose staged C# files have findings that are new or worse than in the baseline is stopped:

```text
Skarp stopped this commit: 1 new problem in 1 staged file.

src/Orders.cs health 10.0 -> 7.2 (10 = healthy)
  new line 12 Orders.Total brain-method: long (84 physical lines), complex (cognitive complexity 61), deeply nested (nesting depth 4) [42b76a88]

What you can do:
  1. Fix it, then stage and commit again.
  2. Accept it as known:   `skarp baseline . && git add .skarp-baseline.json`   then commit
  3. Skip the check once:  `git commit --no-verify`
About a rule: `skarp explain brain-method`
```

The report starts with the verdict and its counts, groups the findings by file under each file's code health (10 = healthy; the file with the largest drop first), lists every finding that blocks on one line each (Skarp's text format, see [Reports and SARIF](reports-and-sarif.md): what changed, the line, the place, the rule, what it measured and the limit, the finding's id last; others are counted as `+N more in this file`), and ends with the ways out.

**When Claude Code runs the commit.** With `CLAUDECODE=1` in the environment (Claude Code sets it for the commands it runs, and git passes it to its hooks), the commit and push reports offer Claude two ways out, which leave the decision about a problem to you, and name neither `skarp baseline` nor `--no-verify`:

```text
What you can do:
  1. Fix it, then stage and commit again.
  2. Or stop and tell the user which problems should stay and why; the user decides.
```

People (no `CLAUDECODE`) keep the three above. A commit that passes is silent, or prints one line when a staged file's health changed (`Skarp: commit ok. src/Orders.cs health 7.2 -> 8.1 (better)`). Notes are single lines starting `Skarp:`.

## 2. What the commit hook does

The hook runs `skarp check --staged --gate`:

- It reads **what you are committing**, not your working files: the staged C# files (added, copied, modified, renamed), through git, so `git add -p`, `git commit -a` and `git commit <file>` are all judged right.
- It compares them with the **staged** `.skarp-baseline.json` and reads `.skarp.json` from the index too. A commit that changes the baseline is allowed and says so in one line.
- It stops the commit (exit 3) on findings in the gate set (`gate.on`, default every warning; see [Settings](settings.md)) that are new or worse than in the baseline, with the report above.
- It also stops the commit when a staged file is **past a health limit** against the staged baseline, whatever the gate set: its syntax score fell by 1.0 or more (`gate.health.maxDrop`), it fell at all and ends below 6.5 (`gate.health.floor`), or it is a new file below 6.5 (`gate.health.newFileMin`; `0` turns a limit off). A score falls mostly through notices, so the file's heading names the limit and every new or worse finding of the file is listed under it, notices too:

  ```text
  Skarp stopped this commit: health past its limit in 1 staged file.

  src/Orders/Shipping.cs health 8.3 -> 7.1 (down 1.2, limit under 1.0) (10 = healthy)
    new line 40 Shipping.Plan complex-method (notice): cognitive complexity 17 (limit 15) [5b1e02aa]
    new line 77 Shipping.Route deep-nesting (notice): nesting depth 4 (limit 3) [0c2a77e1]
  ```

  With findings too: `Skarp stopped this commit: 2 new problems in 1 staged file; health past its limit in 1 staged file.` Because the comparison is the baseline, the heading can show a drop that earlier commits began; `skarp baseline .` accepts it as it accepts findings. A file the staged baseline does not list is new (a renamed file too).
- Without a baseline in the commit it lets the commit through with one line naming `skarp baseline`. A commit without C# files (a message reword, a docs change) is silent.
- Output goes to standard error; nothing is saved in `.skarp/`; it never reads standard input.
- Measured on Windows 11 with git 2.56: it added about 0.2 s to a commit of 10 files.

**What it does not cover.** Say this to your team plainly:

- **Whole-project rules:** duplicate code across files, the semantic tier (unused code, coupling, namespace cycles) and history (active complex files). A file-by-file check cannot decide them. The push hook and CI run them.
- **`git commit --amend`** checks only what is staged now, not the files the amended commit already changed.
- **Merges, rebases and cherry-picks** do not run a pre-commit hook at all (git runs `pre-merge-commit` for a clean merge, nothing for replayed commits), so merged or replayed code is not checked here. The push hook and CI cover it.

**Skipping it once:** `git commit --no-verify`, or `git -c hook.skarp-pre-commit.enabled=false commit`. (`git -c core.hooksPath=/dev/null` no longer turns off hooks that live in git config.)

## 3. The push hook (`--pre-push`)

`skarp install git-hooks --pre-push` adds a second hook that runs before `git push`: `skarp analyze --gate --no-history --no-semantic` on the checked-out tree, which adds what the commit hook leaves out (duplicate code, every file; the health limits judge every file against the baseline too). It runs only when the pushed commit is the current checkout (`HEAD`), because it reads the working tree; pushing another branch prints one line saying it was not checked. Deleting a remote branch is not checked. When it blocks the push (exit 3) it prints the same kind of report on standard error, with the project's health under the verdict and push wording in the ways out:

```text
Skarp stopped this push: 1 new problem in 1 file.
Project health 7.0 over 2 files (was 10.0)

src/Orders.cs health 10.0 -> 6.9 (10 = healthy)
  new line 12 Orders.Total brain-method: long (84 physical lines), complex (cognitive complexity 61), deeply nested (nesting depth 4) [42b76a88]

What you can do:
  1. Fix it, commit the fix, and push again.
  2. Accept it as known:   `skarp baseline . && git add .skarp-baseline.json`   then commit and push
  3. Skip the check once:  `git push --no-verify`
About a problem: `skarp show <id>` eg: `skarp show 42b76a88`
About a rule: `skarp explain brain-method`
```

The push check saves its run in `.skarp/last-run/`, so `skarp show <id>` with the id in square brackets at the end of a finding prints everything about it (the commit hook saves nothing, so its report does not name `skarp show`). A push that passes is silent, or prints one line when the project's health moved. With `--format` added to the hook command, the analysis prints in that format instead and only the ways out follow. `--semantic` keeps the semantic tier (a design-time build; slow). Uncommitted changes in your working tree are analyzed too.

## 4. Where it is written (local, the default)

Skarp writes two keys per hook into this repository's own git config (`.git/config`; in a linked worktree the shared one, so every worktree runs it), never a hook file. Git 2.54 added these hooks in git config; they run before any script in `.git/hooks` or `core.hooksPath`, and next to hook managers. Skarp writes `command` before `event` (an `event` without a `command` stops every commit) and removes them in the other order. The same by hand:

<!-- equals: skarp install git-hooks --pre-push --print (GitHooksInstallTests) -->
```sh
git config --local hook.skarp-pre-commit.command "command -v skarp >/dev/null 2>&1 || { echo 'Skarp: not found, commit not checked; install Skarp to check commits' >&2; exit 0; }; exec skarp check --staged --gate"
git config --local hook.skarp-pre-commit.event pre-commit
git config --local hook.skarp-pre-push.command "command -v skarp >/dev/null 2>&1 || { echo 'Skarp: not found, push not checked; install Skarp to check pushes' >&2; exit 0; }; exec skarp analyze --gate --no-history --no-semantic --push-check"
git config --local hook.skarp-pre-push.event pre-push
```

These lines work in sh, bash, PowerShell and cmd. Git runs the command through its own `sh` and appends the hook's arguments, so it ends in a simple command.

- **Skarp not found** (not on the PATH git sees, for example in some GUI clients): the commit goes through with the line "Skarp: not found, commit not checked". With `--strict` the guard is left out and the commit is blocked instead.
- **Another program:** `--command <path>` writes the path with forward slashes inside single quotes (`'C:/Tools/skarp.exe'`); a path with a single quote is refused.

`skarp install git-hooks --check` lists the git version, both keys of each entry and their order, an `event` without a `command`, where the program resolves, and hook managers that also run (two checks per commit when both call Skarp). It exits 1 on a problem.

## 5. Shared with the team (`--shared`)

A local install is yours only. To share the hook through the repository, `skarp install git-hooks --shared` does one of two things.

**No hook manager:** it writes `.githooks/pre-commit` (and `pre-push` with `--pre-push`) as POSIX `sh` scripts with LF line endings and a marked block. An existing shell script gets the block appended; a script in another language is left alone and the block is printed for you to add. Commit the scripts as executable (`git add --chmod=+x .githooks/pre-commit`; Skarp does it for a file git already tracks). Each clone then opts in once with `git config core.hooksPath .githooks`; Skarp never sets `core.hooksPath` itself. `--remove --shared` takes the block out again (a file holding only Skarp's block is deleted).

<!-- equals: skarp install git-hooks --shared --pre-push --print (GitHooksInstallTests) -->
```sh
==> .githooks/pre-commit <==
#!/bin/sh
# >>> skarp >>>
# Skarp's commit check (skarp install git-hooks --shared); --remove --shared takes this block out.
if command -v skarp >/dev/null 2>&1; then
  skarp check --staged --gate || exit $?
else
  echo 'Skarp: not found, commit not checked; install Skarp to check commits' >&2
fi
# <<< skarp <<<
==> .githooks/pre-push <==
#!/bin/sh
# >>> skarp >>>
# Skarp's push check (skarp install git-hooks --shared); --remove --shared takes this block out.
if command -v skarp >/dev/null 2>&1; then
  skarp analyze --gate --no-history --no-semantic --push-check "$@" || exit $?
else
  echo 'Skarp: not found, push not checked; install Skarp to check pushes' >&2
fi
# <<< skarp <<<
```

**A hook manager:** when the repository uses one, Skarp prints that tool's snippet and writes nothing, because the manager owns those files. Add the snippet yourself. These are printed by `skarp install git-hooks --shared --pre-push`; without `--pre-push` only the commit part.

husky (`.husky/`): add to `.husky/pre-commit` and `.husky/pre-push`.

<!-- equals: snippet for husky (GitHooksInstallTests) -->
```sh
# .husky/pre-commit (add these lines)
# >>> skarp >>>
# Skarp's commit check (skarp install git-hooks --shared); --remove --shared takes this block out.
if command -v skarp >/dev/null 2>&1; then
  skarp check --staged --gate || exit $?
else
  echo 'Skarp: not found, commit not checked; install Skarp to check commits' >&2
fi
# <<< skarp <<<

# .husky/pre-push (add these lines)
# >>> skarp >>>
# Skarp's push check (skarp install git-hooks --shared); --remove --shared takes this block out.
if command -v skarp >/dev/null 2>&1; then
  skarp analyze --gate --no-history --no-semantic --push-check "$@" || exit $?
else
  echo 'Skarp: not found, push not checked; install Skarp to check pushes' >&2
fi
# <<< skarp <<<
```

Husky.Net (`.husky/task-runner.json`): add to `"tasks"`; the hooks run them with `dotnet husky run --group pre-commit` (and `pre-push`).

<!-- equals: snippet for Husky.Net (GitHooksInstallTests) -->
```json
// .husky/task-runner.json: add to "tasks"; the hook runs them with dotnet husky run --group <group>
    {
      "name": "skarp-pre-commit",
      "group": "pre-commit",
      "command": "skarp",
      "args": ["check", "--staged", "--gate"]
    },
    {
      "name": "skarp-pre-push",
      "group": "pre-push",
      "command": "skarp",
      "args": ["analyze", "--gate", "--no-history", "--no-semantic", "--push-check"]
    }
```

lefthook (`lefthook.yml` and its variants): merge into the file (in YAML form; translate for a JSON or TOML file).

<!-- equals: snippet for lefthook (GitHooksInstallTests) -->
```yaml
pre-commit:
  commands:
    skarp:
      glob: "*.cs"
      run: skarp check --staged --gate
pre-push:
  commands:
    skarp:
      use_stdin: true
      run: skarp analyze --gate --no-history --no-semantic --push-check
```

pre-commit (`.pre-commit-config.yaml`): add under `repos:`.

<!-- equals: snippet for pre-commit (GitHooksInstallTests) -->
```yaml
- repo: local
  hooks:
    - id: skarp
      name: Skarp
      entry: skarp check --staged --gate
      language: system
      pass_filenames: false
      types: [c#]
    - id: skarp-push
      name: Skarp (push)
      entry: skarp analyze --gate --no-history --no-semantic --push-check
      language: system
      pass_filenames: false
      always_run: true
      stages: [pre-push]
```

Under Husky.Net and pre-commit the push check gets no ref lines from git, so it analyzes the current checkout whatever is pushed.

A local install next to a manager works too: git runs both, the config hook first. If the manager also runs Skarp, each commit is checked twice; the install preview and `--check` say so.

## 6. Removing

- `skarp install git-hooks --remove`: Skarp's entries from the local git config, `event` first, then `command`. Other hooks and keys are not touched; the config file is as before the install.
- `skarp install git-hooks --remove --shared`: Skarp's block from `.githooks/`.
- An installed push hook stays when you run `skarp install git-hooks` again without `--pre-push`; `--no-pre-push` removes it.

## 7. CI stays the gate

The hook makes problems visible early. The gate that cannot be skipped runs in CI on every pull request, on the whole project, with history and the semantic tier:

```sh
skarp analyze . --gate --format markdown >> "$GITHUB_STEP_SUMMARY"
```

[Adding a CI gate](examples/adding-a-ci-gate.md) has the full workflow; make that job a required check with branch protection.

## See also

- [Checks and agents](check-and-agents.md): `skarp check`, including `--staged`
- [Baselines and the gate](baselines-and-gate.md): the baseline the hook compares with, and the CI gate
- [Health score](health-score.md): the limits the hook judges
- [Using Skarp with Claude Code](agents/claude-code.md)
- [Adding a CI gate](examples/adding-a-ci-gate.md)
