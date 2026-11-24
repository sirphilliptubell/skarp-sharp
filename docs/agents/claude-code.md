# Using Skarp with Claude Code

Skarp tells Claude Code what an edit made worse, right after the edit, in a few lines, and gives a short summary at the end of a turn. This page covers the hooks, the MCP server entry and the setup; everything here is experimental.

The hooks inform; they do not block unless you ask them to (`--gate`, section 5). For the commands behind them see [Checks and agents](../check-and-agents.md).

## 1. Get a Skarp executable

Install the tool (`dotnet tool install -g Skarp`; the command is then `skarp`, and it must be on the `PATH` that Claude Code inherits). See [Installation](../installation.md). Inside a clone of this repository you can instead publish the Native AOT build into the git-ignored `.skarp/` folder (a cold check is then about 40 ms on a small file, section 4):

```sh
dotnet publish src/SkarpSharp.Cli -c Release -r win-x64 -o .skarp/bin     # linux-x64 on Linux; the result is .skarp/bin/skarp(.exe)
```

On Windows the link step needs `vswhere.exe` on `PATH` (`C:\Program Files (x86)\Microsoft Visual Studio\Installer`). Publish again after pulling Skarp changes; a running hook keeps the file open, so publish between sessions.

## 2. The hook settings

**The easy way:** `skarp install claude`. It writes the hooks and Skarp's MCP server entry (section 8; `--hooks` writes only the hooks). It shows a preview, asks before writing (`--yes` skips the question, `--dry-run` only previews, `--print` prints the new file), keeps the old file as `<file>.bak`, and is safe to run twice. `skarp install claude --remove` takes the entries out again; `skarp install claude --check` tests them (below). `skarp install --help` lists the options: `--no-stop`, `--no-compact`, `--gate`, `--semantic`, `--max-lines <n>`, `--include-notices`, `--no-include-notices`, `--command <program>`. Updating an entry that shows notices keeps them: an old `--min-severity notice` becomes `--include-notices`, and an entry with `--include-notices` keeps it unless you add `--no-include-notices`. Options you added to an entry by hand, such as `--no-save`, are kept after the ones Skarp writes.

**With a menu:** `skarp setup claude` (or the "Claude Code hooks" row of `skarp setup`) shows Skarp's entry at each scope and its options as rows: scope, program, per-edit check (always on), end-of-turn summary, after compaction, gate, semantic check, include notices, one line limit, the options kept, and "Remove Skarp's hooks at this scope". The pane shows the `gate.on` and line limits the hook will use (from `.skarp.json` and your settings files; with the gate on, from the repository level only) and where to edit them. `a` closes the menu and runs `skarp install claude` with those options (its preview and question as usual); `s` prints that command for your shell instead. The menu never forces and never runs `--check`.

**By hand**, the same entries. Exec form (`command` plus `args`) runs the program without a shell, so paths with spaces need no quoting. Plain `skarp` is found on the `PATH` Claude Code inherits:

<!-- equals: skarp install claude --hooks --print on an empty folder (InstallCommandTests) -->
```json
{
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "Edit|Write",
        "hooks": [
          {
            "type": "command",
            "command": "skarp",
            "args": [
              "check",
              "--hook",
              "claude"
            ],
            "timeout": 30
          }
        ]
      }
    ],
    "Stop": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "skarp",
            "args": [
              "check",
              "--hook",
              "claude"
            ],
            "timeout": 60
          }
        ]
      }
    ],
    "PostCompact": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "skarp",
            "args": [
              "check",
              "--hook",
              "claude"
            ],
            "timeout": 30
          }
        ]
      }
    ]
  }
}
```

- **The matcher** is `Edit|Write`: Claude Code's `Edit` covers every file-editing tool (`MultiEdit` is legacy).
- **The `Stop` entry is optional.** Leave it out (`--no-stop`) if you want per-edit messages only.
- **The `PostCompact` entry** prints nothing: it only counts, in the session's tracking file, that Claude Code compacted the context, so Skarp's next message carries its About line again (section 3). Leave it out with `--no-compact`; an older install gets it on the next `skarp install claude`.
- **Options** go at the end of `args`: `--gate`, `--semantic` (Stop only), `--max-lines <n>`, `--include-notices`.
- **Another program:** `"command": "${CLAUDE_PROJECT_DIR}/.skarp/bin/skarp.exe"` runs the build of section 1 (`skarp install claude --command '${CLAUDE_PROJECT_DIR}/.skarp/bin/skarp.exe'`); Claude Code expands `${CLAUDE_PROJECT_DIR}` in `command` and `args`. On Windows, exec form needs a real `.exe`: a `.cmd` or `.bat` shim cannot be started.
- **Windows and the `skarp.cmd` shim.** `dotnet tool install -g Skarp` (the Native AOT package) puts a `skarp.cmd` shim in `%USERPROFILE%\.dotnet\tools`, and exec form cannot start it. When plain `skarp` on PATH resolves to such a shim, `skarp install claude` writes the entries (hooks and the MCP server) as `"command": "cmd.exe", "args": ["/c", "skarp", "check", "--hook", "claude", ...]` and says so; `cmd.exe` starts the shim, which starts the real program. That costs about 25 ms per start, and nothing in the entry depends on the tool's version, so it keeps working after `dotnet tool update`. It does this at local and user scope only: the project file is committed and teammates may use another system, so there Skarp writes plain `skarp` and warns. A real `skarp.exe` on PATH (the framework-dependent `Skarp.any` package, or the release archive) is started directly, with no `cmd.exe`; `--command` is always written as given. `skarp install claude --check` runs a wrapped entry through `cmd.exe` as Claude Code will.

**Which file (Claude Code's scopes):**

| Scope | File | For |
|---|---|---|
| `local` (Skarp's default) | `.claude/settings.local.json` at the git root (the current folder outside git) | you, in this repository. Claude Code gitignores it only once it saves a setting there itself; Skarp warns when git does not ignore it. |
| `project` | `.claude/settings.json` there | everyone who clones the repository: it is committed, so every teammate needs `skarp` on `PATH`. Skarp refuses an absolute path or a `.exe` as `--command` here unless `--force`. |
| `user` | `~/.claude/settings.json` (`%USERPROFILE%\.claude` on Windows; `CLAUDE_CONFIG_DIR` replaces `~/.claude`) | you, in every project on this machine. |

Hook entries from all files add up. An identical entry in two files runs once, so `skarp install` only notes it; two different Skarp entries (one with `--gate`, say) both run, so `skarp install` stops unless `--force`.

**Strict JSON.** Claude Code settings files allow no comments and no trailing commas, and Claude Code skips a file that has them (it reports a settings error in `/status`). `skarp install` refuses such a file and prints the entries to add by hand once it is fixed. Claude Code reads hook changes without a restart; `/hooks` lists what it loaded and from which file.

**When it doesn't work: "hook failed".** When the program is missing, Claude Code shows a line such as `PostToolUse:Edit hook failed` / `Executable not found in $PATH: "skarp"` after each edit; you see it, Claude does not, and the edit goes ahead. `skarp install claude --check` lists Skarp's entries in all three files, finds each program, runs it once with a PostToolUse input for a non-C# file (which Skarp ignores) and expects exit 0; exit 1 means a problem (not found, a plain entry that resolves to a `.cmd` shim on Windows, a `cmd.exe` entry on another system, a file that isn't strict JSON, different entries at two scopes). It cannot test Claude Code itself: Claude Code runs hooks only after the workspace-trust question.

**WSL.** Claude Code inside WSL reads the distribution's own `~/.claude` and runs Linux programs. Run `skarp install claude` from inside WSL with a Linux Skarp (`dotnet tool install -g Skarp` there); a Windows install does not configure a WSL session, nor the other way round.

## 3. What Claude sees

**After each edit of a `.cs` file (PostToolUse).** Skarp reads the hook input on standard input (`session_id`, `agent_id`, `agent_type`, `tool_input.file_path`), checks that one file (syntax tier only) and remembers the result per session and agent in `~/.skarp/tracked/<date>-<session>.json`. The first edit of a file in a session is compared with the last commit (git `HEAD`); later edits with the previous check. Only changes are reported, with old and new values (worse, new, and better when a value drops but is still over its limit, in that order in the budget), at most 5 finding lines, through `hookSpecificOutput.additionalContext` (a system reminder next to the tool result). The text is Skarp's text format (see [Reports and SARIF](../reports-and-sarif.md) and [Contracts](../contracts.md)): the file's heading with its health, then one line per finding:

```text
Skarp: src/Parser.cs health 7.9 -> 7.4
  fixed line 12 Parser.Scan deep-nesting (notice): nesting depth 4, now within the limit [91c2aa40]
  worse line 42 Parser.Parse complex-method: cognitive complexity 18 -> 23 (limit 15) [3f9a1c02]
  new line 88 Parser.Emit too-many-parameters: parameters 9 (limit at least 8) [0b7d55e1]
  better line 120 Parser.Flush long-method: physical lines 96 -> 81 (limit 70) [5e0c7d19]
About Skarp: code health notes after each C# edit (first against the last commit, then against the previous edit; health 10 = healthy). About a problem: `skarp show <id>`; why a rule matters and how to fix it: `skarp explain <rule>`.
```

**The About line's rhythm.** The last line above, `About Skarp: ...`, says what the notes are and which commands give more. It is printed to an agent only when it may have lost it: on the agent's first printed message in the session (a subagent, by its `agent_id`, is its own agent), on its first printed message after Claude Code compacted the context (the `PostCompact` entry, section 2, counts compactions in the tracking file; one compaction re-arms every agent of the session), and when 20 printed messages went by since its last About line. Silent checks do not count. It is left out when the line limit is 1. The duplicate-code note (below) follows the same rhythm. `skarp check` run by hand keeps its About lines on every run.

Each finding line is `<what changed> line <n> <place> <rule>[ (notice)]: <numbers> (limit <x>) [<id>]`, in line order. The first word is the change: `new`, `worse`, `better` (still over the limit), `fixed` (once, when a finding drops below its limit; `fixed Parser.Old long-method: method removed` when its member was deleted or renamed, with no line number). A notice says `(notice)` after its rule. `skarp show <id>` gives the full finding (measure, limit, baseline state, the member's metrics); `skarp explain <rule>` says why it matters and how to fix it. Nothing is printed when nothing got worse, for non-C# files and for files a full run leaves out (tests, excluded globs). Notices are tracked but shown only with `--include-notices` added to `args`. A new file that is not in git reports its warnings. "Blocks" is said once, in the first line, when `--gate` is in `args` and a warning in the gate set (`gate.on` of the file's `.skarp.json`, default every warning) is new or worse (section 5). The JSON outputs keep each finding's `severity`.

**Paths.** A file under the project of the hook input's `cwd` (its root: the folder of the nearest `.skarp.json`, else the git work tree root) keeps its path relative to that root; a file of any other folder the session edited shows its full path (`D:/other/src/Billing.cs`).

The heading shows the file's code health score (1 to 10, from its findings; see [Health score](../health-score.md)) when it changed at one decimal against a known earlier score (git `HEAD`, or the session's earlier check of the file): `health <old> -> <new>`. A file new to git (in a git repository, not in `HEAD`) shows `health <score> (new file)`; a file outside git that Skarp has not checked before shows its path only. A message whose only news is a file's health (a heading with no finding line) is printed only when the score moved 0.5 or more since the score last shown to that agent for that file (small drifts add up until they reach 0.5; the heading then shows the move from that score), or rose because a finding was fixed; never for a drop that comes only from notices the hook does not list. The Stop summary has one heading per changed file, worst drop first, unless that score was already shown; a file with no known earlier score shows its score without an arrow.

**Line limits and what comes next.** Each message is bounded and names only the next command for more. Only finding lines count; headings, the first line, the About line and the fold line are free:

| Output | Limit | Setting |
|---|---|---|
| after an edit | 5 finding lines | `output.checkMaxLines` |
| end of turn, without `--gate` | 15 finding lines | (fixed) |
| end of turn, with `--gate` | 50 finding lines, every blocking one first | `output.gateMaxLines` (1 to 1,000) |

`--max-lines <n>` in `args` overrides all three. Findings beyond the limit become one line, never longer than one command: `+N more: skarp show --session <id> --new-only`, where `<id>` is the first 8 characters of the Claude Code session id. The ladder from there:

1. the hook's lines (above);
2. `skarp show --session <id> --new-only`: every finding new or worse during the session, in every file the session edited, across all its agents and folders (each folder answered from its own saved run, `.skarp/last-run/`, and later checks; a folder without a saved run gets a line with the `skarp analyze <folder>` that makes one). A prefix of 4 or more characters is accepted; an ambiguous one lists the sessions (exit 2). It shows warnings; when notices were left out, a line says `+M notices in these files: add --include-notices`;
3. the same with `--include-notices`;
4. `skarp show <id>` for one finding in full (`skarp show --file <path>` lists one file's findings).

`skarp check <file>` run by hand (no session) ends with `+N more: skarp show --file <path>` instead, naming at most three files (plain `skarp show` for more).

**At the end of a turn (Stop).** If files checked in this session changed since the last Stop, Skarp compares each with its state at the start of the session (what its first check compared with: git `HEAD`, an earlier day's record, or nothing for a file not in git) and with the file as committed at git `HEAD` now, and reports only findings that are worse or new against all of them. Old debt in a touched file stays quiet, and so does work committed since the session started (a long session, or files a subagent changed without an edit check). A file in git is never `new`. A finding the baseline (`.skarp-baseline.json`) already holds at that value or higher counts as known.

A finding counts as seen when a message of the session (an edit's, or an earlier Stop's) showed it at its current value, and a Stop whose findings and health headings are all seen prints nothing, since each printed Stop costs Claude a turn. So a Stop that would only sum what the edits printed (`9 -> 11` after `9 -> 10` and `10 -> 11`) is silent. It still reports values that moved after they were shown, lines the per-edit budget folded into `+N more`, files changed outside the edit tools, notices the edit checks did not print, and duplicate code, and nothing else. Its first line says what it compares with (`Skarp, end of turn: not shown after the edits (compared with the start of the session)`), and files are separated by a blank line.

With `--gate` a Stop that blocks is never silenced: every blocking finding is listed, even one already shown, first and up to `output.gateMaxLines`; the first line then says `Skarp did not end the turn: N problems are new or worse in files this session edited.` and the message ends with `Fix them, or tell the user why they should stay. Skarp asks once per turn.` Without `--gate` at most 15 finding lines are printed (applied after leaving lines out); nothing when nothing is left. The Stop covers every file the session edited since the last Stop (or the session start), in any folder: a session that gets the hook late reports everything at once. When it prints, it goes back through `additionalContext`, so Claude continues the turn once to act on it; when it prints nothing, the turn ends without an extra reply. It never fires while Claude is already continuing because of a Stop hook (`stop_hook_active`), and prints nothing when nothing changed. `--semantic` in `args` adds the semantic tier (a full analysis of the root; slow). The Stop summary also reports `duplicate-code` groups (copied code, across files) with a copy in a changed file that are new or bigger than in the baseline; it runs the syntax tier over the whole root for this (about 0.5 s on a repository of this tool's size, a few seconds on large ones), and nothing without a baseline. The per-edit check never reports duplicate code.

**Duplicate code and deliberate duplication.** When the output holds a `duplicate-code` (or `duplicate-code-near`) line, Skarp adds one line once (in the hook, on the About line's rhythm). The hook's note names no way around the finding, so the decision stays with the user:

```text
duplicate code: refactor real copies into one shared member; if the copies are deliberate, tell the user why they should stay
```

`skarp check` and `skarp analyze` run by hand print the longer note:

```text
duplicate code: refactor real copies into one shared member; for deliberate duplication add [SuppressMessage("skarp", "duplicate-code", Justification = "<why>")] to the member
```

Real copies should be refactored (extract the shared part, pass in what differs). Duplication that is deliberate (one overload per type an API repeats on purpose, generated-looking data, test fixtures) is marked where it lives with `[SuppressMessage("skarp", "duplicate-code", Justification = "<why>")]` (`System.Diagnostics.CodeAnalysis`) on the member or type; a suppression without a justification does not count. The same attribute works for every rule (`"complex-method"`, `"long-method"`, ...); `skarp explain duplicate-code` and [Rules](../rules.md) have the details.

**What the text is.** Facts about the code, not instructions: the About line names `skarp show` and `skarp explain` for detail, and the one instruction is in a blocking Stop: fix the findings, or tell the user why they should stay. Findings are places worth a look, not violations. Text shown to Claude never names `skarp baseline`, `--no-verify` or `[SuppressMessage]` as a way out; when Claude runs `git commit` or `git push` and Skarp's git hooks stop it, the report offers two: fix, or stop and tell the user which problems should stay and why ([Git hooks](../git-hooks.md)).

**Brain methods (on by default) and brain classes (experimental, off by default).** A member that is long, complex and deeply nested at once is one `brain-method` line listing those parts (`"rules": { "brain-method": false }` in the root's `.skarp.json` switches this off); with `"rules": { "brain-class": true }`, a large non-static type with such a method and at least 10 methods is one `brain-class` line listing its parts and brain methods, in place of the separate lines:

```text
  new line 4 Importer.Run brain-method: long (80 physical lines), complex (cognitive complexity 24), deeply nested (nesting depth 5) [d2e0c0b4]
```

The line is `new` when every part is new and `worse` when any part is new or worse, and `fixed` under `brain-method` when the brain method is gone; `skarp show <id>` lists the parts, and `skarp explain brain-method` (or `brain-class`) says how to fix it: extract the steps into methods, flatten the nesting with guard clauses, split the class. The gate (`check --gate`, the default `*:warning`) still judges the parts, so turning the composites on never blocks an edit that was allowed before. Partial types get no `brain-class` line in the per-edit check (their `large-type` needs every file).

**Coverage.** Skarp never runs tests. When the root's `.skarp.json` names coverage reports (`"coverage": ["TestResults/**/*.cobertura.xml"]`) or `analyze` gets `--coverage <file>`, `skarp analyze .` can list `untested-complex-method` notices: complex members (cyclomatic 6 or more) no test ran. The per-edit check and the `Stop` hook never report them: the report describes the code as it was when the tests ran, not the file being edited. Produce a fresh report (for example `dotnet test --coverage --coverage-output-format cobertura`) before relying on them; Skarp warns on standard error when the report looks older than the code. See [Coverage](../coverage.md).

## 4. Cost

Measured on Windows 11 with the Native AOT build, idle machine, median of 9 cold runs: a small file 40 ms; Newtonsoft.Json's largest file (324 KB, 6,512 source lines) 210 ms for a repeat edit and 269 ms for a first edit (which also reads the file from git `HEAD`). Edits made through Bash (for example `sed`) do not fire an `Edit|Write` hook; the Stop summary and `skarp analyze` cover them only when Skarp checked the file before.

## 5. Stricter: block on worse code

Add `--gate` to `args` (`skarp install claude --gate`): worse or new warnings in the gate set (`gate.on` of the file's `.skarp.json`, default every warning) then come back as `{"decision": "block", "reason": "<text>"}`, whose first line says how many findings block (`Skarp: this edit added 1 problem that blocks the end of the turn.`). What that does depends on the event (see Claude Code's hooks reference):

- **After an edit (PostToolUse):** the edit has already happened. The block only puts Skarp's reason next to the tool result, worded more strongly than without `--gate`. Nothing forces Claude to act on it; that is up to the model.
- **At the end of a turn (Stop):** Claude Code does not end the turn. It gives Claude another go with the reason. It cannot make Claude fix anything, and Claude may simply reply again. Skarp stays silent while Claude is already continuing because of a Stop hook (`stop_hook_active`), so Skarp blocks the end of a turn at most once per turn. Claude Code also has its own cap: 8 continuations in a row, reset whenever Claude calls a tool.

So the gate makes Skarp's message stronger and guarantees one more chance to respond at the end of a turn; it does not guarantee the code gets fixed. Hard stops belong in CI (`skarp analyze --gate`, see [Baselines and the gate](../baselines-and-gate.md)). Use the gate deliberately: refactoring often gets worse before it gets better. The Stop summary lists every line that still blocks, also those an edit check already blocked on, each time it blocks (the end of a turn is the one moment Claude Code enforces), up to `output.gateMaxLines` (default 50) with the blocking findings first.

**The health limits.** With `--gate` the hook also judges each edited file's syntax health score against the file at the start of the session, whatever `gate.on` says: a drop of 1.0 or more (`gate.health.maxDrop`), a drop that ends below 6.5 (`gate.health.floor`), or a file new to git below 6.5 (`gate.health.newFileMin`); `0` turns a limit off, and a file that did not get worse never blocks. A score falls mostly through notices, which the hook does not print by default, so a file past a limit lists its new or worse findings, notices included. The edit that takes a file past a limit says so once, with the limit in the heading:

```text
Skarp: this edit took health past its limit in 1 file, which blocks the end of the turn.
src/Orders/Shipping.cs health 8.3 -> 7.1 (down 1.2, limit under 1.0)
  new line 40 Shipping.Plan complex-method (notice): cognitive complexity 17 (limit 15) [5b1e02aa]
  new line 77 Shipping.Route deep-nesting (notice): nesting depth 4 (limit 3) [0c2a77e1]
```

Then the Stop blocks (`Skarp did not end the turn: health past its limit in 1 file this session edited.`, joined with a semicolon to the findings count when there are blocking findings too), lists the file's lines even when an edit already showed them, and ends with the same one instruction. The ways out stay the same: fix them, or tell the user why they should stay. Without `--gate` nothing changes. MCP `check` states the same limits as one fact line per file (`Health limit: src/Orders/Shipping.cs is down 1.2; the gate stops at 1.0 or more.`), with no verdict. See [Health score](../health-score.md).

## 6. Turn it off

- `skarp install claude --remove [--scope project|user]` (or "Remove Skarp's hooks at this scope" in `skarp setup claude`), or remove the entries by hand (the file watcher picks the change up), or
- set `"disableAllHooks": true` in your `.claude/settings.local.json` (all hooks, this project, only for you), or
- for one run: `claude --settings '{"disableAllHooks": true}'`.

Any internal error makes the hook print nothing and exit 0 (a broken hook must not stop the agent); the error goes to standard error, which Claude Code shows only in its debug log. The tracking files are deleted after 30 days (`tracking.retentionDays` in `.skarp.json`); delete `~/.skarp/tracked/` at any time to start over. `--no-save` in `args` keeps Skarp from writing `.skarp/checked/` in the repository.

## 7. Other agents

Any MCP client can use `skarp mcp` (section 8; [Checks and agents](../check-and-agents.md) has the entries for Cursor, VS Code and Codex). For checks after each edit there is no ready-made setup for other agents yet. Run `skarp check <file> --track --session <id> [--agent <id>]` after each file write: the text format on standard output, exit 0; with `--gate` exit code 3 and the text on standard error (an agent that expects exit code 2 for "blocking feedback" needs a wrapper, because Skarp uses 2 for usage errors).

## 8. The MCP server (experimental)

Hooks push: Skarp speaks after each edit and at the end of a turn. The MCP server is the pull side: Claude asks Skarp when it wants to know something. `skarp mcp` serves four tools over standard input and output, in this order:

- **`find_worst_code`** ranks the least healthy C# code under a folder or in one file, worst first: by health (default), by hotspot (commits in the history window, then complexity) or by one rule's own measure (for example `long-method`, methods by physical lines, under the limit too). Each file comes with its health, its band (healthy 9 or more, problematic, unhealthy below 4) and up to 3 findings with ids; the closing line says how many listed entries are healthy. `count` is 10 by default and at most 25, `offset` pages. `depth=semantic` adds class coupling, god classes, unused code and namespace cycles (a design-time build, tens of seconds, never a restore); a second call reuses the saved run when no file, no setting, no solution and no baseline changed, and a second semantic call (from either tool) while one runs gets "already going, try again". A syntax call does not replace a saved run that still has fresh semantic results. When the tier cannot run (not restored, no solution) the answer is the syntax ranking with the reason. The answer is the same text as `skarp analyze --sort ... --format text`, apart from the fold lines (`+N more: findings file=<path>`) and the closing lines, which name the `findings` and `explain` tools instead of CLI commands.
- **`check`** answers "did my changes make anything worse?" without a session. Without C# files in `files` it takes the C# files that differ from the last commit, staged or not, plus untracked ones (one `git status`; `path` narrows them; more than 2,000 is an error); with C# files in `files` (up to 200) it takes those. Each file is compared with its root's baseline when there is one (new and worse lines only: exactly what `skarp check` and the commit gate say), else with the file at the last commit (new, worse, better and fixed lines, the health before and after); a file not in the last commit is a new file, a file outside git shows all its warnings. At most 15 finding lines, then a fold line naming the files the rest is in. For files it writes nothing: no tracking file, no `.skarp/checked/` record, no saved run. A `.sln` or `.slnx` in `files` also runs the semantic tier on that solution (class coupling, god classes, unused code, namespace cycles) against the semantic baseline: what is new or worse anywhere in the solution, or without a semantic baseline the warnings in the checked files; that run is saved and reused like `find_worst_code`'s, and a tier that cannot run gives the file part plus the reason. Ids of the file part are not in the saved run (unless the hooks recorded the file), so `findings` may not resolve them; the line already carries the measure and the limit, and `explain` the fix text.
- **`findings`** reads the saved run (`.skarp/last-run/` plus newer per-edit records, as `skarp show` reads them) and never analyzes: a list by `file`, `rule` and `new_only` (30 per call, at most 100, `offset` pages; with `file` the heading has the file's health and where its points went), or one finding in full by `id` (as `skarp show <id>`, then the rule's "How to improve" paragraph). Without a saved run it says so; stale data is answered with a line saying how many files changed since (refresh: `find_worst_code`).
- **`explain`** gives a rule's text as `skarp explain <rule>` does, with only the thresholds in force for the repository (`all_presets=true`: every preset); without a rule, the rule list.

**Install.** `skarp install claude --mcp` writes the entry `"skarp": {"type": "stdio", "command": "skarp", "args": ["mcp"]}`; plain `skarp install claude` writes the hooks and the entry. Where it goes follows Claude Code's own scopes: `--scope local` (default) and `--scope user` write `~/.claude.json` (local: under this project's key in `projects`; user: at the top; `CLAUDE_CONFIG_DIR` replaces `~`), `--scope project` writes `.mcp.json` at the git root, to commit. The file is merged: other servers and keys stay, an existing `skarp` entry is updated in place. `--command` works as for the hooks; `${CLAUDE_PROJECT_DIR}` becomes `${CLAUDE_PROJECT_DIR:-.}` in the entry, because Claude Code expands that variable in an MCP entry only with a default. `skarp install claude --check` lists the entry at each scope and finds its program; `--remove --mcp` takes it out. `skarp setup claude` has an "MCP server" row.

**Approval.** Claude Code asks once before it starts a server from a project's `.mcp.json`; local and user entries start without asking. A new or changed entry is loaded when a session starts; `/mcp` shows whether `skarp` is connected and lists its tools. `find_worst_code` is annotated as not read-only (it saves `.skarp/last-run/`, and the semantic tier writes `obj/` as an IDE does), so Claude Code may ask before the first call; so is `check` (its solution part runs and saves the semantic tier). `findings` and `explain` are read-only.

**Four set-ups:**

1. **Hooks and MCP** (Claude Code, `skarp install claude`): the hooks report what each edit made worse; Claude calls `find_worst_code` when you ask where to clean up, `findings` to read a file's findings or one in full, `explain` when a rule is unfamiliar, and `check` for changes made another way (through the shell, for example). The server's instructions say that where Skarp notes appear after each edit, `check` adds nothing per edit.
2. **MCP without hooks** (other agents, or `skarp install claude --mcp`): nothing checks each edit, so the server's instructions ask the agent to call `check` on the C# files it edited before it finishes a task.
3. **Hooks without MCP** (`skarp install claude --hooks`): the hooks alone.
4. **Neither**: a paragraph in the agent's rules file ([Checks and agents](../check-and-agents.md)): after editing C# files, run `skarp check <files>` and read its output.

**Cost.** The tool list as sent (names, descriptions and schemas of the four tools) is about 7,700 characters and the server's instructions 1,101; Claude Code loads tool schemas on demand. A `check` answer is bounded by 15 finding lines per part, a `findings` list by its `limit` (30, at most 100); the largest answers stay under 40,000 characters (tests check them). A ranking of 10 files is about 4,000 to 5,000 characters on a repository of this tool's size (a syntax call takes about 1.2 s with the Native AOT build, most of it the git history read; a semantic call about 20 s, and about 1 s when the saved run is reused).

## See also

- [Checks and agents](../check-and-agents.md): the commands, the line format and MCP clients other than Claude Code
- [Git hooks](../git-hooks.md): a check before each commit and push
- [Using Skarp with an AI coding agent](../examples/using-skarp-with-an-ai-coding-agent.md): a walk-through
- [Baselines and the gate](../baselines-and-gate.md)
- [Health score](../health-score.md)
- [Settings](../settings.md)
