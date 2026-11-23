using SkarpSharp.Install;

namespace SkarpSharp.Commands;

/// <summary>
/// <c>skarp install &lt;target&gt;</c>, with <c>claude</c> the only target. It writes Skarp's hook
/// entries (PostToolUse after each edit, Stop at the end of a turn, PostCompact after a compaction) into a
/// Claude Code settings file, removes them
/// (<c>--remove</c>) or checks them (<c>--check</c>). Exit codes: 0 written, nothing to change, or all good; 1
/// <c>--check</c> found a problem; 2 usage error, a refused file, the user said no, or a failed write. Experimental.
/// </summary>
public static class InstallCommand
{
    public const string Usage = """
        skarp install: put Skarp's hooks into an AI agent's settings or into git. Experimental. Targets: claude (Claude
        Code), git-hooks (a check before each commit, and on request before each push).

        Usage:
          skarp install claude [--hooks|--mcp] [--scope local|project|user] [--no-stop] [--no-compact] [--gate]
                               [--semantic] [--max-lines <n>] [--include-notices|--no-include-notices]
                               [--command <program>] [--force] [--dry-run] [--print] [--yes]
              writes Skarp's hooks and its MCP server entry (--hooks: the hooks only; --mcp: the MCP entry only).
              The MCP entry {"type":"stdio","command":<program>,"args":["mcp"]} is named skarp, in .mcp.json at the
              git root (project scope) or in ~/.claude.json (local: under this project; user: for every project;
              CLAUDE_CONFIG_DIR replaces ~); other servers stay as they are, an existing skarp entry is updated in
              place. The hooks are three entries (exec form) that run `skarp check --hook claude`: PostToolUse with
              the matcher Edit|Write (timeout 30 s), Stop (timeout 60 s) and PostCompact (timeout 30 s; it only notes
              that Claude Code compacted the context, so the next message repeats the About line); an existing Skarp
              entry is updated in place: options added to it by hand are kept after the ones Skarp writes, and
              notices stay on unless --no-include-notices (an old `--min-severity notice` becomes --include-notices).
              The hook options below apply to the hooks only.
          skarp install claude --remove [--hooks|--mcp] [--scope local|project|user] [--command <program>] [--dry-run]
                               [--yes]
              removes Skarp's entries, and the hook groups and events they leave empty; the files and their other
              keys stay
          skarp install claude --check [--hooks|--mcp]
              lists Skarp's hook entries in all three settings files, finds each program and runs it once with a
              PostToolUse input for a non-C# file (which Skarp ignores), then Skarp's MCP entry at each scope and its
              program; exit 1 on any problem

        Scopes (Claude Code's own):
          local     .claude/settings.local.json at the git root (the current folder outside git); the default; only
                    for you (Skarp warns when git does not ignore it)
          project   .claude/settings.json there; committed, so it runs for everyone who clones the repository
          user      ~/.claude/settings.json (CLAUDE_CONFIG_DIR replaces ~/.claude); every project on this machine

        Options:
          --no-stop              leave out the Stop entry (an existing Skarp Stop entry is removed)
          --no-compact           leave out the PostCompact entry (an existing Skarp PostCompact entry is removed)
          --gate, --semantic (Stop only), --max-lines <n>, --include-notices
                                 passed to `skarp check` in the entries' args (skarp check --help); default none
          --no-include-notices   remove --include-notices from an existing entry
          --command <program>    run this program instead of plain `skarp` found on PATH, for example
                                 ${CLAUDE_PROJECT_DIR}/.skarp/bin/skarp.exe (Claude Code expands ${CLAUDE_PROJECT_DIR});
                                 at project scope an absolute path or a .exe is refused unless --force, because it
                                 breaks for teammates on other machines
          --force                write although a different Skarp entry is installed at another scope (both run), or
                                 a --command that is not portable at project scope
          --dry-run              print the preview and write nothing
          --print                write the complete new file to standard output (the preview goes to standard error)
                                 and write nothing
          --yes                  write without asking (needed without a terminal)

        The file must be strict JSON (no comments, no trailing commas), as Claude Code itself requires; another file is
        refused with the snippet to add by hand. An existing file is copied to <file>.bak before an atomic write; its
        indentation and line endings are kept. A Skarp entry is a "command" handler whose program is skarp (any folder,
        any extension, or the --command given) with args starting `check --hook claude`; other hooks are never touched.

        skarp setup claude shows Skarp's entry at each scope in a menu, then runs this command or prints it.

        Git hooks (git 2.54 or newer; docs/git-hooks.md):
          skarp install git-hooks [--pre-push [--semantic]] [--no-pre-push] [--strict] [--command <program>] [--shared]
                                  [--dry-run] [--print] [--yes]
              a commit hook in this repository's git config (hook.skarp-pre-commit): skarp check --staged --gate
              blocks a commit whose staged C# files are new or worse than the staged baseline (exit 3)
          skarp install git-hooks --remove [--shared] [--dry-run] [--yes]
          skarp install git-hooks --check   git version, both keys and their order, the program, hook managers

          --pre-push             also a push hook: skarp analyze --gate --no-history --no-semantic, when HEAD is
                                 pushed (--semantic keeps the semantic tier); --no-pre-push removes it
          --strict               when Skarp is not found, block (default: let the commit through with one line)
          --command <program>    instead of skarp from PATH; written with / in single quotes
          --shared               for the team: a hook manager's snippet (nothing written), else Skarp's marked block
                                 in .githooks/ (each clone runs git config core.hooksPath .githooks)
          --print                the git config lines (shared: the scripts); nothing written

        A warning, not the gate: git commit --no-verify skips it, CI stays the gate. It leaves out whole-project rules,
        sees only what is staged now with --amend, and does not run for merges, rebases and cherry-picks.

        Exit codes: 0 written, nothing to change, a snippet printed, or --check all good; 1 --check found a problem;
        2 usage error, a refused file, git older than 2.54 or no repository, the answer no, or a failed write.
        """;

    public static int Run(IReadOnlyList<string> args, CommandIO io)
    {
        if (args.Count > 1 && args[1] == GitHooksCall.Target) return RunGitHooks(args, io);
        var (r, error) = InstallArgs.Parse(args);
        if (r.Help) { io.Out.WriteLine(Usage); return 0; }
        if (error is not null)
        {
            io.Error.WriteLine($"skarp install: {error}");
            io.Error.WriteLine("Usage: skarp install claude [options] | --remove | --check (skarp install --help)");
            return 2;
        }
        var seams = io.Install ?? new InstallSeams();
        var projectDir = InstallArgs.ProjectDir(io.Cwd);
        if (r.Check) return new ClaudeCheck(io.Out, seams, projectDir) { Hooks = r.Hooks, Mcp = r.Mcp }.Run();
        var installer = new ClaudeInstaller(new InstallRequest { Scope = r.Scope, Spec = r.Spec(), Force = r.Force, DryRun = r.DryRun, Print = r.Print, Yes = r.Yes, Hooks = r.Hooks, Mcp = r.Mcp },
            io.Out, io.Error, seams, projectDir);
        return r.Remove ? installer.Remove() : installer.Install();
    }

    /// <summary><c>skarp install git-hooks</c>.</summary>
    static int RunGitHooks(IReadOnlyList<string> args, CommandIO io)
    {
        var (r, error) = GitHooksCall.ParseRequest(args);
        if (r.Help) { io.Out.WriteLine(Usage); return 0; }
        if (error is not null)
        {
            io.Error.WriteLine($"skarp install: {error}");
            io.Error.WriteLine("Usage: skarp install git-hooks [options] | --remove | --check (skarp install --help)");
            return 2;
        }
        var seams = io.Install ?? new InstallSeams();
        if (r.Check) return new GitHooksCheck(io.Out, seams, io.Cwd).Run();
        var installer = new GitHooksInstaller(new GitHooksRun { Spec = r.Spec, DryRun = r.DryRun, Print = r.Print, Yes = r.Yes }, io.Out, io.Error, seams, io.Cwd);
        return r.Remove ? installer.Remove() : installer.Install();
    }

    /// <summary>The hooks screen's "apply" after <c>skarp setup</c> closed: the line, then the normal installer
    /// on the plain console, with its preview and question.</summary>
    public static int RunFromMenu(IReadOnlyList<string> args, CommandIO io)
    {
        io.Out.WriteLine("Running: " + Words(args));
        return Run(args, io);
    }

    /// <summary>The command in words (an argument with whitespace in double quotes), for the console only.</summary>
    public static string Words(IReadOnlyList<string> args) =>
        "skarp " + string.Join(" ", args.Select(a => a.Any(char.IsWhiteSpace) ? "\"" + a + "\"" : a));
}
