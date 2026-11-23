using SkarpSharp.Configuration;
using SkarpSharp.Setup;

namespace SkarpSharp.Install;

/// <summary>The rows of the git hooks screen.</summary>
public enum GitHookRow { Commit, Push, Semantic, Shared, Strict, Program, Remove }

/// <summary>
/// The "Git hooks" screen of <c>skarp setup</c>, built like the Claude Code hooks screen.
/// Rows: commit hook (always on), push hook, semantic for push, local or shared, strict, program, and "Remove Skarp's git
/// hooks". The context line says whether they are installed and whether there is a baseline. Keys: Enter or Space change a
/// row, <c>a</c> applies through <c>skarp install git-hooks</c> after the menu closes, <c>s</c> prints it, Esc goes back.
/// </summary>
public sealed class GitHooksScreen(GitHooksTarget target, HookSettings? settings = null) : IMenuScreen
{
    /// <summary>The rows are the enum's values in order (no array over the enum: Native AOT size).</summary>
    const int NameWidth = 18, MaxEntry = 260, RowCount = (int)GitHookRow.Remove + 1;

    readonly SetupMenu.ListState _rowList = new();
    bool _entering;
    readonly LineEditor _entry = new(MaxEntry);

    public GitHooksTarget Target { get; } = target;

    /// <summary>The screen for <paramref name="cwd"/>; <paramref name="seams"/> null: the machine's.</summary>
    /// <summary>With <paramref name="locations"/>, the commit row's pane names the health limits in force.</summary>
    public static GitHooksScreen Open(string cwd, InstallSeams? seams, SettingsLocations? locations = null) =>
        new(GitHooksTarget.Open(cwd, seams ?? new InstallSeams()), locations is null ? null : new HookSettings(cwd, locations));

    public CommandBuilder Build() => Target.Build();

    public IReadOnlyList<string> Args() => Target.Args();

    GitHookRow Highlighted => (GitHookRow)Math.Clamp(_rowList.Index, 0, RowCount - 1);

    // ---- keys -------------------------------------------------------------------------------------------------------

    public (ScreenAction Action, string? Error) Handle(ConsoleKeyInfo k, int page)
    {
        if (_entering) return (ScreenAction.None, HandleEntry(k));
        if (SetupMenu.Move(_rowList, k, RowCount, page)) return (ScreenAction.None, null);
        return k.Key switch
        {
            ConsoleKey.Escape => (ScreenAction.Back, null),
            ConsoleKey.A => (ScreenAction.Apply, null),
            ConsoleKey.S => (ScreenAction.Show, null),
            ConsoleKey.Enter or ConsoleKey.Spacebar => (ScreenAction.None, Change(Highlighted)),
            ConsoleKey.Delete => (ScreenAction.None, Reset(Highlighted)),
            _ => (ScreenAction.None, null),
        };
    }

    string? Change(GitHookRow row)
    {
        switch (row)
        {
            case GitHookRow.Commit: return "The commit hook is always on: it is what the hooks are for. Remove Skarp's git hooks to turn everything off.";
            case GitHookRow.Program: _entering = true; _entry.Start(Target.Spec.Command); return null;
            case GitHookRow.Push: Target.TogglePush(); break;
            case GitHookRow.Semantic: Target.ToggleSemantic(); break;
            case GitHookRow.Shared: Target.ToggleShared(); break;
            case GitHookRow.Strict: Target.ToggleStrict(); break;
            default: Target.ToggleRemove(); break;
        }
        return null;
    }

    string? Reset(GitHookRow row)
    {
        Target.Reset(row);
        return null;
    }

    string? HandleEntry(ConsoleKeyInfo k)
    {
        switch (k.Key)
        {
            case ConsoleKey.Escape: _entering = false; return null;
            case ConsoleKey.Enter:
                try { Target.SetProgram(_entry.Text); }
                catch (SetupException ex) { return ex.Message; }
                _entering = false;
                return null;
        }
        _entry.Handle(k);
        return null;
    }

    // ---- frame parts ------------------------------------------------------------------------------------------------

    public string Header => $"Skarp setup  Git hooks  {(Target.Spec.Shared ? "shared scripts" : "local git config")}  {Target.Where}{(Target.Changed ? "  changed" : "")}";

    public string Context => $"{Target.Status}; baseline {(Target.HasBaseline ? "present" : "missing")}; a applies through skarp install git-hooks";

    public (IReadOnlyList<string> Items, SetupMenu.ListState State) Body()
    {
        var lines = new List<string>();
        for (int i = 0; i < RowCount; i++) lines.Add(Line((GitHookRow)i));
        return (lines, _rowList);
    }

    string Line(GitHookRow row)
    {
        var (s, l) = (Target.Spec, Target.Loaded);
        var (name, value, changed) = row switch
        {
            GitHookRow.Commit => ("commit hook", "on (always)", false),
            GitHookRow.Push => ("push hook", OnOff(s.PrePush == true), s.PrePush != l.PrePush),
            GitHookRow.Semantic => ("semantic for push", OnOff(s.Semantic), s.Semantic != l.Semantic),
            GitHookRow.Shared => ("install as", s.Shared ? "shared (.githooks/, committed)" : "local (this clone's git config)", s.Shared != l.Shared),
            GitHookRow.Strict => ("strict", OnOff(s.Strict), s.Strict != l.Strict),
            GitHookRow.Program => ("program", s.Command, s.Command != l.Command),
            _ => ("", "", Target.Remove),
        };
        if (row == GitHookRow.Remove) return $"{(changed ? '*' : ' ')}Remove Skarp's git hooks{(Target.Remove ? ": yes" : "")}";
        return $"{(changed ? '*' : ' ')}{name.PadRight(NameWidth)} {value}";
    }

    static string OnOff(bool on) => on ? "on" : "off";

    public string Pane() => _entering ? "The program git runs: skarp from PATH, or a path." : Highlighted switch
    {
        GitHookRow.Commit => "Before each commit: skarp check --staged --gate blocks staged C# files that are worse than the staged baseline" + Limits() + ". --no-verify skips it; CI stays the gate." + Baseline(),
        GitHookRow.Push => "Before each push (--pre-push): skarp analyze --gate on the checkout when HEAD is pushed; covers duplicate code, merges and rebases. Slower.",
        GitHookRow.Semantic => "--semantic: the push hook keeps the semantic tier (a build; slow).",
        GitHookRow.Shared => "Local: this clone's git config, only for you. Shared: Skarp's block in .githooks/ (committed; each clone sets core.hooksPath), or a hook manager's snippet to add by hand.",
        GitHookRow.Strict => "--strict: block when Skarp is not found (off: let it through with one line).",
        GitHookRow.Program => "The program git runs (--command): skarp from PATH, or a path such as C:/Tools/skarp.exe; empty means skarp.",
        _ => "skarp install git-hooks --remove (with shared: the block in .githooks/). Enter switches it; a applies, s prints.",
    };

    /// <summary>The health limits in force; nothing when unknown, or without a baseline (everything passes).</summary>
    string Limits() => Target.HasBaseline && settings?.HealthLimits() is { } h ? $" or past a health limit ({h})" : "";

    string Baseline() => Target.HasBaseline ? "" : " No baseline yet: everything passes until one is committed (skarp baseline .).";

    public Line Footer => _entering ? _entry.Line("program (empty: skarp): ", LineStyle.Dim)
        : new("Up/Down move  Enter change  Del default  a apply  s show command  Esc back", LineStyle.Dim);
}

/// <summary>
/// The rows after the levels on <c>skarp setup</c>'s first choice that open a screen ("Claude Code
/// hooks", <see cref="HooksScreen"/>; "Git hooks", <see cref="GitHooksScreen"/>), their panes, and how to open one.
/// </summary>
public static class MenuScreens
{
    public static readonly string[] Rows =
    [
        "Claude Code hooks  run Skarp after each edit and turn (skarp install claude)",
        "Git hooks          warn before a commit makes code worse (skarp install git-hooks)",
    ];

    public static readonly string[] Panes =
    [
        "Skarp's hooks in Claude Code's settings: a check after each edit and a summary at the end of each turn. Choose the scope and options, then apply them through skarp install claude (with its preview and question) or print the command.",
        "A git hook that checks the staged C# files before each commit, and on request the project before each push. A fast local warning: git commit --no-verify skips it, CI stays the gate. Apply through skarp install git-hooks (preview and question) or print the command.",
    ];

    /// <summary>The row of <c>skarp setup claude</c> or <c>skarp setup git</c>, or -1.</summary>
    public static int Index(string? name) => name switch { "claude" => 0, "git" => 1, _ => -1 };

    public static IMenuScreen Open(int index, string cwd, InstallSeams? seams, SettingsLocations locations) =>
        index == 0 ? HooksScreen.Open(cwd, seams, locations) : GitHooksScreen.Open(cwd, seams, locations);
}
