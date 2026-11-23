using System.Globalization;
using SkarpSharp.Configuration;
using SkarpSharp.Setup;

namespace SkarpSharp.Install;

/// <summary>The rows of the hooks screen.</summary>
public enum HookRow { Scope, Program, PerEdit, Stop, Compact, Gate, Semantic, Notices, MaxLines, Mcp, Kept, Remove }

/// <summary>
/// The "Claude Code hooks" screen of <c>skarp setup</c>, drawn by <see cref="SetupMenu"/>.
/// Rows: scope (with a scope list), program, per-edit check (always on), end-of-turn summary, gate, semantic check,
/// include notices, the one line limit, the options kept, and "Remove Skarp's hooks at this scope". Keys: Enter or Space
/// change a row, <c>a</c> applies, <c>s</c> shows the command, Esc goes back. The words are <see cref="HooksText"/>.
/// </summary>
public sealed class HooksScreen : IMenuScreen
{
    enum Mode { Rows, Scopes, Entry }

    const int MaxEntry = 260;

    readonly HooksText _text;
    readonly SetupMenu.ListState _rowList = new(), _scopeList = new();
    Mode _mode;
    HookRow _editing;
    readonly LineEditor _entry = new(MaxEntry);

    public HooksScreen(HooksTarget target)
    {
        Target = target;
        _text = new HooksText(target);
    }

    public HooksTarget Target { get; }

    /// <summary>The screen on the target for <paramref name="cwd"/>; <paramref name="seams"/> null: the machine's.</summary>
    public static HooksScreen Open(string cwd, InstallSeams? seams, SettingsLocations locations) =>
        new(HooksTarget.Open(cwd, seams ?? new InstallSeams(), locations));

    public CommandBuilder Build() => Target.Build();

    public IReadOnlyList<string> Args() => Target.Args();

    static readonly HookRow[] WithKept =
        [HookRow.Scope, HookRow.Program, HookRow.PerEdit, HookRow.Stop, HookRow.Compact, HookRow.Gate, HookRow.Semantic, HookRow.Notices, HookRow.MaxLines, HookRow.Mcp, HookRow.Kept, HookRow.Remove];
    static readonly HookRow[] WithoutKept =
        [HookRow.Scope, HookRow.Program, HookRow.PerEdit, HookRow.Stop, HookRow.Compact, HookRow.Gate, HookRow.Semantic, HookRow.Notices, HookRow.MaxLines, HookRow.Mcp, HookRow.Remove];

    /// <summary>The rows shown, "other options (kept)" only when there are some (arrays, not a list over the enum: binary size).</summary>
    HookRow[] Rows() => Target.Kept.Count > 0 ? WithKept : WithoutKept;

    List<string> Lines()
    {
        var lines = new List<string>();
        foreach (var row in Rows()) lines.Add(_text.Line(row));
        return lines;
    }

    HookRow Highlighted
    {
        get
        {
            var rows = Rows();
            return rows[Math.Clamp(_rowList.Index, 0, rows.Length - 1)];
        }
    }

    // ---- keys -------------------------------------------------------------------------------------------------------

    public (ScreenAction Action, string? Error) Handle(ConsoleKeyInfo k, int page) => _mode switch
    {
        Mode.Entry => (ScreenAction.None, HandleEntry(k)),
        Mode.Scopes => (ScreenAction.None, HandleScopes(k, page)),
        _ => HandleRows(k, page),
    };

    (ScreenAction, string?) HandleRows(ConsoleKeyInfo k, int page)
    {
        if (SetupMenu.Move(_rowList, k, Rows().Length, page)) return (ScreenAction.None, null);
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

    string? Change(HookRow row)
    {
        switch (row)
        {
            case HookRow.Scope: _mode = Mode.Scopes; _scopeList.Index = (int)Target.Scope; return null;
            case HookRow.Program: return Edit(row, Target.Spec.Command);
            case HookRow.MaxLines: return Edit(row, Target.Spec.MaxLines?.ToString(CultureInfo.InvariantCulture) ?? "");
            case HookRow.PerEdit: return "The per-edit check is always on: it is what the hook is for. Remove Skarp's hooks to turn everything off.";
            case HookRow.Kept: return $"These options are kept as they are; edit them in {Target.ShownPath(Target.Scope)}.";
            case HookRow.Stop: Target.ToggleStop(); break;
            case HookRow.Compact: Target.ToggleCompact(); break;
            case HookRow.Gate: Target.ToggleGate(); break;
            case HookRow.Semantic: Target.ToggleSemantic(); break;
            case HookRow.Notices: Target.ToggleNotices(); break;
            case HookRow.Mcp: Target.ToggleMcp(); break;
            default: Target.ToggleRemove(); break;
        }
        return null;
    }

    string? Reset(HookRow row)
    {
        Target.Reset(row);
        return null;
    }

    string? Edit(HookRow row, string text)
    {
        _editing = row;
        _entry.Start(text);
        _mode = Mode.Entry;
        return null;
    }

    string? HandleEntry(ConsoleKeyInfo k)
    {
        switch (k.Key)
        {
            case ConsoleKey.Escape: _mode = Mode.Rows; return null;
            case ConsoleKey.Enter: return Commit();
        }
        _entry.Handle(k);
        return null;
    }

    string? Commit()
    {
        try
        {
            if (_editing == HookRow.Program) Target.SetProgram(_entry.Text);
            else Target.SetMaxLines(_entry.Text);
        }
        catch (SetupException ex) { return ex.Message; }
        _mode = Mode.Rows;
        return null;
    }

    string? HandleScopes(ConsoleKeyInfo k, int page)
    {
        if (SetupMenu.Move(_scopeList, k, ClaudeScopes.All.Length, page)) return null;
        if (k.Key == ConsoleKey.Enter) Target.Load(ClaudeScopes.All[_scopeList.Index]);
        if (k.Key is ConsoleKey.Enter or ConsoleKey.Escape) _mode = Mode.Rows;
        return null;
    }

    // ---- frame parts ------------------------------------------------------------------------------------------------

    public string Header => $"Skarp setup  Claude Code hooks  {Target.Scope.Name()} scope  {Target.ShownPath(Target.Scope)}{(Target.Changed ? "  changed" : "")}";

    public string Context => _mode == Mode.Scopes ? "Choose the scope: Claude Code's settings file for the entry"
        : "Skarp's hook entry; a applies it through skarp install claude";

    public (IReadOnlyList<string> Items, SetupMenu.ListState State) Body() => _mode == Mode.Scopes
        ? (_text.ScopeLines(), _scopeList)
        : (Lines(), _rowList);

    public string Pane() => _mode switch
    {
        Mode.Scopes => _text.ScopePane(ClaudeScopes.All[_scopeList.Index]),
        Mode.Entry => _text.Pane(_editing),
        _ => _text.Pane(Highlighted),
    };

    public Line Footer => _mode switch
    {
        Mode.Entry when _editing == HookRow.Program => _entry.Line("program (empty: skarp): ", LineStyle.Dim),
        Mode.Entry => _entry.Line("one line limit (empty: off): ", LineStyle.Dim),
        Mode.Scopes => new("Up/Down move  Enter choose  Esc back", LineStyle.Dim),
        _ => new("Up/Down move  Enter change  Del default  a apply  s show command  Esc back", LineStyle.Dim),
    };
}
