# Using Skarp with an AI coding agent

Install Skarp's hooks and MCP server for Claude Code, see what the agent is told after each edit, and optionally block a turn that makes code worse. Everything on this page is experimental.

## Goal

The agent learns, right after it edits a C# file, which findings that edit introduced, and can ask Skarp which code is worst before it starts. You can make the end of a turn fail when health got worse.

## Prerequisites

- Skarp installed and on the `PATH` that Claude Code inherits: `dotnet tool install -g Skarp` ([installation](../installation.md)).
- Claude Code, in a git repository with C# code.
- Optional: a committed baseline (`skarp baseline .`) so the agent is compared with the accepted state ([baselines and the gate](../baselines-and-gate.md)).

## Steps

1. Preview what will be written. `--dry-run` writes nothing:

   ```sh
   skarp install claude --scope project --dry-run
   ```

   ```text
   Skarp hooks, project scope: .claude/settings.json (new file)
     PostToolUse: add skarp check --hook claude (matcher Edit|Write, timeout 30)
     Stop: add skarp check --hook claude (timeout 60)
     PostCompact: add skarp check --hook claude (timeout 30)
   dry run: nothing written

   Skarp MCP server, project scope: .mcp.json (new file)
     skarp: add stdio: skarp mcp
   dry run: nothing written
   ```

   Scopes follow Claude Code's own:
   - `local` (default): `.claude/settings.local.json`, only for you. Skarp warns when git does not ignore it.
   - `project`: `.claude/settings.json` and `.mcp.json`, committed, so everyone who clones the repository gets them.
   - `user`: `~/.claude/settings.json` and `~/.claude.json`, for every project on the machine.

2. Install them. The command shows a preview and asks before writing; `--yes` skips the question. An existing file is copied to `<file>.bak`, other hooks and servers are left alone, and running it twice is safe.

   ```sh
   skarp install claude --scope project
   ```

   Use `--hooks` or `--mcp` to write only one of the two. Or use the menu: `skarp setup claude`.

3. Check the installation:

   ```sh
   skarp install claude --check
   ```

   It lists the hook entries in all three files, finds the program and runs it once on an input for a non-C# file, then does the same for the MCP entry. Exit code 1 means a problem.

4. Restart Claude Code, or start a new session, and let the agent edit a C# file.

## What the agent sees

Three hook entries run `skarp check --hook claude`:

- After every edit by the Edit or Write tool (`PostToolUse`), the agent gets a few lines about the edited file. The first check of a file compares it with the last commit; later checks compare it with the previous edit. Nothing is printed when nothing got worse or better. Example (with `--include-notices`):

  ```text
  Skarp: src/Legacy/Old.cs health 9.5 -> 9.4
    new line 5 Old.Run too-many-parameters (notice): parameters 5 (limit 4) [f729ead9]
  About Skarp: code health notes after each C# edit (...). About a problem: `skarp show <id>`; why a rule matters and how to fix it: `skarp explain <rule>`.
  ```

  The text goes to Claude as hook JSON (`additionalContext`). Each line starts with what changed (`new`, `worse`, `better`, `fixed`).
- At the end of a turn (`Stop`), a summary of the files changed since the session started.
- After the context is compacted (`PostCompact`), the next message repeats the "About Skarp" line. It prints nothing else.

Only warnings are shown by default. `--include-notices` also shows notices. Whole-project rules (duplicate code, coupling, unused code) are not part of per-edit checks: one file is parsed, with no history and no directory walk. `skarp analyze` covers them.

The MCP server (`skarp mcp`) gives the agent four tools:

- `find_worst_code`: the least healthy files or methods under a folder, worst first, by health, activity or one rule's measure.
- `check`: what the changed files made worse or better, against the baseline or else the last commit.
- `findings`: the findings of the saved run, or one in full with how to improve it. It never analyzes.
- `explain`: what a rule measures, why it matters, how to improve flagged code and the thresholds in force.

You can read the same data yourself: `skarp show --session <id>` lists the findings in the files a Claude Code session edited, and `skarp show <id>` explains one finding.

## Turning on the gate

By default the hooks inform and never block. To make the agent fix what it made worse, add `--gate`:

```sh
skarp install claude --scope project --gate
```

With the gate on, the hook answers with `"decision": "block"` when an edit adds warnings in the gate set (`gate.on`, default `*:warning`) or takes a file's health past a limit. Sample hook answer, shortened:

```json
{"decision":"block","reason":"Skarp: this edit took health past its limit in 1 file, which blocks the end of the turn.\nsrc/Shop/Clean.cs health 10.0 -> 8.5 (down 1.5, limit under 1.0)\n  new line 7 Clean.Pick complex-method (notice): cognitive complexity 18 (limit 15) [e21b2183]\n ..."}
```

The health limits are the same as in CI: a file may not lose 1.0 or more, a file that fell may not end below 6.5, and a new file must score at least 6.5. They are set by `gate.health.maxDrop`, `newFileMin` and `floor` ([baselines and the gate](../baselines-and-gate.md)). With the gate, only the repository's `.skarp.json` applies, not your personal file. The `Stop` summary lists every blocking line first.

Test the same logic by hand, for example in a script:

```sh
skarp check src/Shop/Clean.cs --gate
```

Without `--hook`, this exits with code 3 when the gate trips (it needs a baseline or `--track`).

## Common variations

- Fewer lines per message: `--max-lines <n>` (default 5; the `Stop` summary 15).
- Also check the whole solution at the end of a turn: `--semantic` (slow).
- Run a specific build: `--command '${CLAUDE_PROJECT_DIR}/.skarp/bin/skarp.exe'` (single quotes, so the shell leaves the variable alone). At project scope an absolute path or `.exe` is refused without `--force`, because it breaks on teammates' machines.
- Leave out the end-of-turn summary or the compaction hook: `--no-stop`, `--no-compact`.
- Remove everything: `skarp install claude --remove`.
- Other MCP clients: configure the command `skarp` with the argument `mcp` as a stdio server.
- A commit-time check for humans and agents alike: [git hooks](../git-hooks.md).

## See also

- [Check and agents](../check-and-agents.md)
- [Claude Code](../agents/claude-code.md)
- [Git hooks](../git-hooks.md)
- [Baselines and the gate](../baselines-and-gate.md)
- [Adding a CI gate](adding-a-ci-gate.md)
- [Rules](../rules.md)
