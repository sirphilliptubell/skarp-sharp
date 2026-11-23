# Checks and agents

Skarp has commands that tell an AI coding agent, or you, what a change made worse: `skarp check`, `skarp show`, `skarp install` and `skarp mcp`. All four are experimental: flags and text may still change.

The commands do not block anything by themselves. Findings are places worth a look, not violations. Only `--gate` makes an exit code depend on them.

## skarp check

`skarp check` reports the syntax findings of the files just written. It parses one file at a time, with no history and no directory walk, so it is fast enough to run after every edit.

```sh
skarp check src/Orders.cs
skarp check --staged --gate          # the git commit hook, see Git hooks
```

What the findings are compared with:

- Without `--track`: the baseline (`.skarp-baseline.json` at the root). Only findings new or worse than in it are printed. Without a baseline, every warning is printed.
- With `--track`: the file's earlier state in this session. The first check of a file compares with the last commit; later checks with the previous check. It prints new, worse, better and, once, fixed findings. Unchanged findings print nothing. Tracking files live in `~/.skarp/tracked/` (`SKARP_HOME` replaces `~/.skarp`).

Sample output for a file whose method gained a parameter:

```text
Skarp: src/Orders.cs health 8.4 (10 = healthy)
  worse line 4 Orders.Total too-many-parameters: parameters 9 -> 10 (limit at least 8) [4dea40c2]
About a problem: `skarp show <id>` eg: `skarp show 4dea40c2`
About a rule: `skarp explain too-many-parameters`
```

The root of a check is the folder of the nearest `.skarp.json`, else the git work tree root, else the file's folder. Finding ids equal those of `skarp analyze` on that root.

Main options:

| Option | Meaning |
|---|---|
| `--track` | compare with the file's earlier state in this session |
| `--session <id>`, `--agent <id>` | the session and agent the tracking belongs to |
| `--hook claude` | read a Claude Code hook's JSON on standard input and answer in its JSON; see [Claude Code](agents/claude-code.md) |
| `--gate` | block on worse or new warnings in the gate set (`gate.on`, default `*:warning`) and on [health limits](health-score.md); exit code 3 |
| `--staged` | the staged C# files from git's index; see [Git hooks](git-hooks.md) |
| `--max-lines <n>` | at most n finding lines (default 5; setting `output.checkMaxLines`) |
| `--include-notices` | also print notices (default: warnings only) |
| `--semantic` | Stop hook only: also run the semantic tier (slow) |
| `--no-save` | do not record the result in `.skarp/checked/` |

Some rules need more than one file, so `check` leaves them out: `active-complex-file` (needs history), `duplicate-code` and `duplicate-code-near` (match across files), `untested-complex-method` (needs a coverage report, which is stale while a file is edited), `large-type` of partial types, and every semantic-tier rule. `skarp analyze` covers them.

When a member's findings form a brain method (long, complex and deeply nested at once), `check` prints one `brain-method` line in place of its parts. `brain-class` does the same for types but is experimental and off by default. `--gate` still judges the parts.

Exit codes: 0 done (also when findings are printed), 1 tool error, 2 usage error, 3 `--gate` tripped.

## The line format

Skarp's text format is the same in `analyze`, `check`, `show`, the git hooks and the Claude Code hook. It is for reading, not parsing: it may change in any release. Tools should read JSON.

```text
src/Orders.cs health 9.0 -> 8.8
  worse line 18 Orders.Ship long-method: physical lines 81 -> 97 (limit 70) [cdceba26]
```

- The heading is the file path (relative to the root) with its health score, shown when the score changed at one decimal. A file new to git shows `health 7.4 (new file)`.
- Each finding line is `<what changed> line <n> <place> <rule>[ (notice)]: <measure> (limit <x>) [<id>]`.
- The first word says what changed: `new`, `worse`, `better` (a value dropped but is still over its limit) or `fixed`. Without a comparison it is the level, `warning` or `notice`.
- A notice says `(notice)` after its rule.
- The limit reads `(limit 70)`, or `(limit at least 8)` for rules that trigger from the value on.
- The id is the first eight hex digits of a hash of the finding's identity. `skarp show <id>` takes it, or any prefix of four or more characters.
- Output beyond the line limit folds into one line: `+N more: skarp show --file <path>` by hand, or `+N more: skarp show --session <id> --new-only` in a session.

## skarp show

`skarp show` answers from the saved run (`.skarp/last-run/`, written by every `skarp analyze`, plus later `skarp check` results) or from the baseline, without analyzing again. Run it anywhere inside the analyzed folder.

```sh
skarp show 4dea40c2                    # one finding in full: message, measure and limit, baseline state, the member's metrics
skarp show --file src/Orders.cs        # the findings of one file, with where its health points went
skarp show --rule long-method          # the findings of one rule
skarp show --new-only                  # new or worse than the baseline the run compared with
skarp show --session 3f9a1c02          # every file a Claude Code session edited
skarp show --from baseline             # read the baseline files instead of the saved run
```

Other options: `--include-notices`, `--format text|json`, `--target <tfm>` (the semantic results of one target framework), `--all` (every match; the default is the first 50). If files changed since the run was saved, the first line says how many and how to refresh. Exit codes: 0 done, 1 tool error, 2 usage error (unknown handle, no saved run, unknown session).

Use `skarp explain <rule>` for what a rule measures and how to fix it; see [Explain](explain.md).

## skarp install

`skarp install` writes Skarp's hooks into an agent's settings or into git. It shows a preview, asks before writing (`--yes` skips the question, `--dry-run` only previews, `--print` prints the result without writing), keeps the old file as `<file>.bak` and is safe to run twice.

```sh
skarp install claude                     # hooks and MCP entry, local scope
skarp install claude --scope project     # committed: .claude/settings.json and .mcp.json
skarp install claude --gate              # block on worse code, see below
skarp install claude --check             # find each installed program and run it once
skarp install claude --remove            # take Skarp's entries out
skarp install git-hooks --pre-push       # a check before each commit and push
```

`skarp install claude` has three hook entries, all running `skarp check --hook claude`: PostToolUse on `Edit|Write` (after each edit), Stop (end of turn) and PostCompact (only counts a context compaction). `--hooks` writes only the hooks and `--mcp` only the MCP entry. Scopes, options, the by-hand JSON and troubleshooting are in [Claude Code](agents/claude-code.md). The git hooks are in [Git hooks](git-hooks.md).

`skarp setup claude` and `skarp setup git` show the same options in a menu.

## The gate in hooks

`--gate` makes a hook answer with a block when findings in the gate set are new or worse. It also judges each file's [health score](health-score.md) with three limits (a drop of 1.0 or more, a fall that ends below 6.5, a new file below 6.5), whatever the gate set says.

- In Claude Code the gate makes Skarp's message stronger and gives Claude one more turn at the end of a task. It cannot make Claude fix the code.
- In the git commit hook, exit code 3 stops the commit. `git commit --no-verify` skips it.
- A hard stop that cannot be skipped belongs in CI: see [Baselines and the gate](baselines-and-gate.md).

## skarp mcp

`skarp mcp` is Skarp's [Model Context Protocol](https://modelcontextprotocol.io) server. An MCP client starts it and talks to it over standard input and output. There is no network access and no telemetry; log lines go to standard error. Paths are absolute or relative to the folder the client starts the server in, which is also the default.

Four tools, in this order:

| Tool | What it does |
|---|---|
| `find_worst_code` | the least healthy C# files or methods under a folder, worst first, by health, change activity or one rule's measure. Parameters: `path`, `count` (default 10, at most 25), `depth` (`syntax` or `semantic`), `sort`, `unit`, `offset`. Saves the run in `.skarp/last-run/`. |
| `check` | what the changed C# files (or the files given in `files`) made worse or better, against the baseline or else the last commit. Changes no source file, baseline or setting. A `.sln` or `.slnx` in `files` also runs the semantic tier. |
| `findings` | the findings of the saved run, listed by `file`, `rule` and `new_only` (30 per call, at most 100), or one in full by `id`. Never analyzes. |
| `explain` | what a rule measures, why it matters, how to improve flagged code and the thresholds in force. `all_presets=true` shows every preset. Without a rule: the rule list. |

`find_worst_code` marks files with their band (healthy, problematic, unhealthy) and ends with how many listed entries are healthy, so a ranking of good code is not read as a to-do list. `depth=semantic` runs a design-time build (tens of seconds, never a restore). See [Semantic tier](semantic-tier.md).

The server's instructions ask an agent to call `check` on the C# files it edited before it finishes a task. Where hooks already report after each edit, `check` adds nothing per edit.

`find_worst_code` and `check` save run data (the semantic tier also writes `obj/` as an IDE does), so Skarp marks them as not read-only and a client may ask before the first call. `findings` and `explain` are read-only.

### MCP client entries

Claude Code: `skarp install claude --mcp` writes the entry (`.mcp.json` at the git root for `--scope project`, `~/.claude.json` for `local` and `user`).

Other clients are configured by hand, with `skarp` on the `PATH` the client inherits. These entries use each client's usual format; check the client's own documentation if it has changed.

Cursor (`.cursor/mcp.json` in the project, or `~/.cursor/mcp.json`):

```json
{ "mcpServers": { "skarp": { "type": "stdio", "command": "skarp", "args": ["mcp"] } } }
```

VS Code with GitHub Copilot (`.vscode/mcp.json` in the workspace):

```json
{ "servers": { "skarp": { "type": "stdio", "command": "skarp", "args": ["mcp"] } } }
```

OpenAI Codex (`~/.codex/config.toml`):

```toml
[mcp_servers.skarp]
command = "skarp"
args = ["mcp"]
```

## Agents with neither hooks nor MCP

Add a paragraph like this to the agent's rules file (`AGENTS.md` or similar):

> After editing C# files, run `skarp check <files>` and read its output before finishing. It lists what the edit made worse, one line per finding with an id. `skarp explain <rule>` says how to fix a rule's findings.

For a per-edit check in another agent, run `skarp check <file> --track --session <id> [--agent <id>]` after each file write: the text on standard output and exit code 0. With `--gate` the exit code is 3 and the text goes to standard error. An agent that expects exit code 2 for "blocking feedback" needs a small wrapper, because Skarp uses 2 for usage errors.

## See also

- [Using Skarp with Claude Code](agents/claude-code.md)
- [Git hooks](git-hooks.md)
- [Using Skarp with an AI coding agent](examples/using-skarp-with-an-ai-coding-agent.md)
- [Health score](health-score.md)
- [Reports and SARIF](reports-and-sarif.md)
- [Baselines and the gate](baselines-and-gate.md)
- [Explain](explain.md)
