using System.Text;
using System.Text.Json.Nodes;
using SkarpSharp.Configuration;
using SkarpSharp.Findings;

namespace SkarpSharp.Setup;

/// <summary>A level offered on the level choice screen.</summary>
public sealed class LevelChoice(SettingOrigin level, string? path, bool found)
{
    public SettingOrigin Level { get; } = level;
    public string? Path { get; } = path;
    public bool Found { get; } = found;
}

/// <summary>Where the menu starts: the level choice (empty when a level was given) with its highlight, or the opened
/// level; <see cref="Open"/> opens a chosen level (a <see cref="SetupException"/> is shown in the pane).</summary>
public sealed class SetupMenuStart(IReadOnlyList<LevelChoice> levels, int highlight, LevelModel? model, Func<SettingOrigin, LevelModel> open)
{
    /// <summary>Opens the "command line" target for a command; null when not offered.</summary>
    public Func<string, CommandTarget>? OpenCommand { get; init; }
    /// <summary>Start on the command choice (<c>skarp setup command</c>).</summary>
    public bool StartWithCommands { get; init; }
    /// <summary>The rows after the levels that open a screen ("Claude Code hooks", "Git hooks"), their
    /// panes and how to open one by its index; none when not offered.</summary>
    public IReadOnlyList<string> ScreenRows { get; init; } = [];
    public IReadOnlyList<string> ScreenPanes { get; init; } = [];
    public Func<int, IMenuScreen>? OpenScreen { get; init; }
    /// <summary>The screen to start on (<c>skarp setup claude</c>, <c>skarp setup git</c>), or -1.</summary>
    public int StartScreen { get; init; } = -1;
    public IReadOnlyList<LevelChoice> Levels { get; } = levels;
    public int Highlight { get; } = highlight;
    public LevelModel? Model { get; } = model;
    public Func<SettingOrigin, LevelModel> Open { get; } = open;
}

/// <summary>The menu's keys and terminal; <see cref="Color"/> false draws no SGR codes; <see cref="OnFrame"/> sees each frame.</summary>
public sealed class MenuConsole(IKeySource keys, ITerminal terminal, bool color = true, Action<Frame>? onFrame = null, Shell? defaultShell = null)
{
    public IKeySource Keys { get; } = keys;
    public ITerminal Terminal { get; } = terminal;
    public bool Color { get; } = color;
    public Action<Frame>? OnFrame { get; } = onFrame;
    /// <summary>The shell the command builder starts on; null picks PowerShell on Windows and sh elsewhere.</summary>
    public Shell? DefaultShell { get; } = defaultShell;
}

/// <summary>
/// The inline settings editor. Screens: level choice,
/// sections, rows, picker, entry line, preview and confirm. A frame is a list of logical lines (golden frames read
/// them); <see cref="FrameWriter"/> draws it. Keys: arrows, PgUp, PgDn, Home, End move; Enter edits; Space toggles;
/// Del removes the key here (inherit); <c>f</c> shows only keys set here; <c>u</c> reverts a row; <c>s</c> saves after
/// a preview; Esc goes back and at the top asks before quitting with changes; Ctrl+C quits at once (exit 130).
/// </summary>
public sealed partial class SetupMenu
{
    enum Screen { Levels, Sections, Rows, Picker, Entry, Preview, Confirm, Commands, Shell, Hooks }

    public sealed class ListState { public int Index; public int Top; }

    const string MachineNote = "the gate ignores system and personal settings";
    const string TooSmall = "window too small";
    const int MinWidth = 40, MinHeight = 12;

    readonly IKeySource _keys;
    readonly ITerminal _terminal;
    readonly FrameWriter _writer;
    readonly Action<Frame>? _onFrame;
    readonly Func<SettingOrigin, LevelModel> _open;
    readonly IReadOnlyList<LevelChoice> _levels;

    LevelModel? _m;
    Screen _screen, _under;
    readonly ListState _levelList = new(), _sectionList = new(), _rowList = new(), _pickList = new(), _previewList = new(),
        _commandList = new(), _shellList = new() { Index = OperatingSystem.IsWindows() ? 0 : 2 };
    readonly Func<string, CommandTarget>? _openCommand;
    CommandTarget? _target;
    RunOption? _runOption;
    bool _filter, _writeFailed, _confirmReset;
    IReadOnlyList<SetupOption> _options = [];
    SettingsKey? _row;
    bool _customThreshold;
    readonly LineEditor _entry = new(200);
    string? _error;
    IReadOnlyList<string> _preview = [];

    /// <summary>The file content to print after the menu closed ("print the file instead"), or null.</summary>
    public string? PrintText { get; private set; }
    /// <summary>The line printed after the menu closed.</summary>
    public string Summary { get; private set; } = "";

    public SetupMenu(SetupMenuStart start, MenuConsole console)
    {
        _levels = start.Levels;
        _levelList.Index = start.Highlight;
        _m = start.Model;
        _open = start.Open;
        _keys = console.Keys;
        _terminal = console.Terminal;
        _writer = new FrameWriter(console.Terminal, console.Color);
        _onFrame = console.OnFrame;
        if (console.DefaultShell is { } shell) _shellList.Index = (int)shell;
        _screen = start.Model is null ? Screen.Levels : Screen.Sections;
        _openCommand = start.OpenCommand;
        if (start.StartWithCommands) _screen = Screen.Commands;
        AddScreens(start);
    }

    /// <summary>The "command line" target is open.</summary>
    bool Builder => _target is not null;

    /// <summary>Runs until saved or quit (0) or Ctrl+C (130).</summary>
    public int Run()
    {
        Render(false);
        try
        {
            while (true)
            {
                var ev = _keys.Read();
                if (ev.Resize) { Render(true); continue; }
                var k = ev.Key;
                if (k.Key == ConsoleKey.C && (k.Modifiers & ConsoleModifiers.Control) != 0)
                {
                    Summary = "Cancelled (Ctrl+C); nothing saved.";
                    return 130;
                }
                if (!Small() && Handle(k) is int exit) return exit;
                Render(false);
            }
        }
        finally
        {
            _writer.Close();
        }
    }

    bool Small() => _terminal.Width < MinWidth || _terminal.Height < MinHeight;

    int Changed() => (_m?.Changed().Count ?? 0) + (_target?.Options.Count ?? 0);

    // ---- keys -------------------------------------------------------------------------------------------------------

    int? Handle(ConsoleKeyInfo k)
    {
        if (_screen is not (Screen.Entry or Screen.Preview)) _error = null;
        return _screen switch
        {
            Screen.Levels => HandleLevels(k),
            Screen.Sections => HandleSections(k),
            Screen.Rows => HandleRows(k),
            Screen.Picker => HandlePicker(k),
            Screen.Entry => HandleEntry(k),
            Screen.Preview => HandlePreview(k),
            Screen.Commands => HandleCommands(k),
            Screen.Shell => HandleShell(k),
            Screen.Hooks => HandleHooks(k),
            _ => HandleConfirm(k),
        };
    }

    internal static bool Move(ListState s, ConsoleKeyInfo k, int count, int page)
    {
        int i = k.Key switch
        {
            ConsoleKey.UpArrow => s.Index - 1,
            ConsoleKey.DownArrow => s.Index + 1,
            ConsoleKey.PageUp => s.Index - page,
            ConsoleKey.PageDown => s.Index + page,
            ConsoleKey.Home => 0,
            ConsoleKey.End => count - 1,
            _ => int.MinValue,
        };
        if (i == int.MinValue) return false;
        s.Index = Math.Clamp(i, 0, Math.Max(0, count - 1));
        return true;
    }

    int? HandleLevels(ConsoleKeyInfo k)
    {
        if (Move(_levelList, k, LevelRows, Page())) return null;
        if (k.Key == ConsoleKey.Escape) { Summary = "Nothing changed."; return 0; }
        if (k.Key != ConsoleKey.Enter) return null;
        if (OnHooksRow) { OpenHooks(ScreenRow); return null; }
        if (_levels[_levelList.Index].Level == SettingOrigin.CommandLine) { _screen = Screen.Commands; return null; }
        try
        {
            _m = _open(_levels[_levelList.Index].Level);
            _screen = Screen.Sections;
            _sectionList.Index = 0;
        }
        catch (SetupException ex) { _error = ex.Message; }
        return null;
    }

    /// <summary>The sections shown: every section, or for the command line only those with rows for its command.</summary>
    IReadOnlyList<SettingSection> MenuSections()
    {
        if (!Builder) return SetupRows.Sections;
        var list = new List<SettingSection>();
        foreach (var s in SetupRows.Sections) if (SetupRows.Rows(_m!, s).Count > 0) list.Add(s);
        return list;
    }

    /// <summary>The command line's first section is "Run options".</summary>
    int Offset => Builder ? 1 : 0;

    int SectionCount => Offset + MenuSections().Count + 1; // plus "reset everything"

    bool OnReset => _sectionList.Index == SectionCount - 1;

    bool OnRunOptions => Builder && _sectionList.Index == 0;

    int? HandleSections(ConsoleKeyInfo k)
    {
        if (Move(_sectionList, k, SectionCount, Page())) return null;
        switch (k.Key)
        {
            case ConsoleKey.Enter when OnReset:
                _under = Screen.Sections; _confirmReset = true; _screen = Screen.Confirm; break;
            case ConsoleKey.Enter:
                _screen = Screen.Rows; _rowList.Index = 0; _rowList.Top = 0; break;
            case ConsoleKey.S: if (Builder) StartShell(Screen.Sections); else StartPreview(Screen.Sections); break;
            case ConsoleKey.Escape:
                if (Changed() > 0) { _under = Screen.Sections; _confirmReset = false; _screen = Screen.Confirm; }
                else if (Builder) _screen = Screen.Commands;
                else if (_levels.Count > 0 && _screen == Screen.Sections && LevelChosenHere) { _m = null; _screen = Screen.Levels; }
                else { Summary = "Nothing changed."; return 0; }
                break;
        }
        return null;
    }

    bool LevelChosenHere => _levels.Count > 0;

    SettingSection Section
    {
        get
        {
            var sections = MenuSections();
            return sections[Math.Clamp(_sectionList.Index - Offset, 0, sections.Count - 1)];
        }
    }

    IReadOnlyList<SettingsKey> Rows()
    {
        var rows = SetupRows.Rows(_m!, Section);
        if (!_filter) return rows;
        var leaves = new List<string>(SettingsDocument.Leaves(_m!.Document.Current));
        var shown = new List<SettingsKey>();
        foreach (var r in rows) if (Touches(leaves, r.Key)) shown.Add(r);
        return shown;
    }

    /// <summary>True when one of <paramref name="keys"/> is <paramref name="key"/> or below it.</summary>
    static bool Touches(IReadOnlyList<string> keys, string key)
    {
        foreach (var k in keys) if (k == key || k.StartsWith(key + ".", StringComparison.Ordinal)) return true;
        return false;
    }

    int? HandleRows(ConsoleKeyInfo k)
    {
        if (OnRunOptions) return HandleRunOptions(k);
        var rows = Rows();
        if (Move(_rowList, k, rows.Count, Page())) return null;
        var row = rows.Count > 0 ? rows[Math.Min(_rowList.Index, rows.Count - 1)] : null;
        switch (k.Key)
        {
            case ConsoleKey.Escape: _screen = Screen.Sections; return null;
            case ConsoleKey.S: if (Builder) StartShell(Screen.Rows); else StartPreview(Screen.Rows); return null;
            case ConsoleKey.F: _filter = !_filter; _rowList.Index = 0; _rowList.Top = 0; return null;
        }
        if (row is null) return null;
        switch (k.Key)
        {
            case ConsoleKey.U: _m!.Revert(row.Key); if (row.ModeKey is { } mk) _m.Revert(mk); break;
            case ConsoleKey.Delete: Try(() => SetupWriter.Unset(_m!, row.Key)); break;
            case ConsoleKey.Spacebar when row.Kind == SettingKind.Boolean || SettingsRegistry.ListOfMode(row.Key) is not null: Toggle(row); break;
            case ConsoleKey.Enter: Edit(row); break;
        }
        return null;
    }

    void Try(Action action)
    {
        try { action(); _error = null; }
        catch (SetupException ex) { _error = ex.Message; }
    }

    void Toggle(SettingsKey row)
    {
        var current = SetupRows.Effective(_m!, row.Key);
        if (row.Kind == SettingKind.Boolean)
        {
            bool on = current is JsonValue v && v.TryGetValue<bool>(out var b) ? b : row.Default is "true" or "on";
            Try(() => SetupWriter.Set(_m!, row.Key, on ? "false" : "true"));
        }
        else
        {
            var mode = current is JsonValue v && v.TryGetValue<string>(out var s) ? s : "replace";
            Try(() => SetupWriter.Set(_m!, row.Key, mode == "append" ? "replace" : "append"));
        }
    }

    void Edit(SettingsKey row)
    {
        if (row.Kind == SettingKind.Boolean || SettingsRegistry.ListOfMode(row.Key) is not null) { Toggle(row); return; }
        _row = row;
        _runOption = null;
        _options = SetupRows.Options(_m!, row);
        if (_options.Count > 0)
        {
            _screen = Screen.Picker;
            _pickList.Index = 0;
            for (int i = 0; i < _options.Count; i++) if (_options[i].IsCurrent) _pickList.Index = i;
            _pickList.Top = 0;
            return;
        }
        if (row.Merge == MergeKind.Overrides)
        {
            _error = Builder ? "overrides are edited in the repository's .skarp.json (skarp config lists them)" : $"overrides are edited in the file: {_m!.ShownPath} (skarp config lists them)";
            return;
        }
        if (row.Kind == SettingKind.Object) { _error = row.Description; return; }
        var here = _m!.Here(row.Key, out bool found);
        OpenEntry(Screen.Rows, custom: false, found && here is not null ? SettingsDocument.Text(here) : "");
    }

    void OpenEntry(Screen under, bool custom, string text)
    {
        _under = under;
        _customThreshold = custom;
        _entry.Start(text == "(empty)" ? "" : text);
        _error = null;
        _screen = Screen.Entry;
    }

    int? HandlePicker(ConsoleKeyInfo k)
    {
        if (Move(_pickList, k, _options.Count, Page())) return null;
        if (k.Key == ConsoleKey.Escape) { _screen = Screen.Rows; return null; }
        if (k.Key != ConsoleKey.Enter) return null;
        var option = _options[_pickList.Index];
        if (_runOption is { } run)
        {
            Try(() => _target!.SetOption(run.Name, option.Action == OptionAction.Set ? option.Write!.GetValue<string>() : null));
            _m = _target!.Model;
            _screen = Screen.Rows;
            return null;
        }
        switch (option.Action)
        {
            case OptionAction.Set: SettingsDocument.Set(_m!.Document.Current, _row!.Key, option.Write?.DeepClone()); _screen = Screen.Rows; break;
            case OptionAction.Remove: Try(() => SetupWriter.Unset(_m!, _row!.Key)); _screen = Screen.Rows; break;
            default: OpenEntry(Screen.Picker, custom: _row!.Part is "notice" or "warning", ""); break;
        }
        return null;
    }

    int? HandleEntry(ConsoleKeyInfo k)
    {
        switch (k.Key)
        {
            case ConsoleKey.Escape: _screen = _under; _error = null; return null;
            case ConsoleKey.Enter: CommitEntry(); return null;
        }
        if (_entry.Handle(k)) _error = null;
        return null;
    }

    /// <summary>A key that types a character into an entry line.</summary>
    internal static bool Typed(ConsoleKeyInfo k) => k.KeyChar >= ' ' && k.KeyChar != '\u007f';

    void CommitEntry()
    {
        if (_runOption is { } run)
        {
            try { _target!.SetOption(run.Name, _entry.Text.Trim()); }
            catch (SetupException ex) { _error = ex.Message; return; }
            _m = _target.Model;
            _error = null;
            _screen = Screen.Rows;
            return;
        }
        var row = _row!;
        if (_customThreshold)
        {
            var sev = row.Part == "warning" ? Severity.Warning : Severity.Notice;
            if (SetupRows.CustomThreshold(_m!, row.Rule!, sev, _entry.Text, out var error) is not { } value) { _error = error; return; }
            SettingsDocument.Set(_m!.Document.Current, row.Key, value);
        }
        else
        {
            if (_entry.Text.Trim().Length == 0 && row.Kind != SettingKind.List) { _error = "Type a value, or Esc to go back."; return; }
            try { SetupWriter.Set(_m!, row.Key, _entry.Text); }
            catch (SetupException ex) { _error = ex.Message; return; }
        }
        _error = null;
        _screen = Screen.Rows;
    }

    void StartPreview(Screen from)
    {
        _under = from;
        _preview = SetupWriter.Preview(_m!, shortPaths: true);
        _previewList.Index = 0; _previewList.Top = 0;
        _writeFailed = false;
        _error = null;
        _screen = Screen.Preview;
    }

    int? HandlePreview(ConsoleKeyInfo k)
    {
        if (k.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.PageUp or ConsoleKey.PageDown or ConsoleKey.Home or ConsoleKey.End)
        {
            Move(_previewList, k, Math.Max(1, _preview.Count - Page() + 1), Page());
            _previewList.Top = _previewList.Index;
            return null;
        }
        if (k.Key is ConsoleKey.Escape or ConsoleKey.N) { _screen = _under; _error = null; return null; }
        var text = _m!.Document.Render();
        if (k.Key == ConsoleKey.P && _writeFailed)
        {
            PrintText = text;
            Summary = $"Printed the new content of {_m.Path} instead; nothing saved.";
            return 0;
        }
        if (k.Key != ConsoleKey.Y) return null;
        int changed = Changed();
        if (changed == 0) { _screen = _under; return null; }
        if (SetupWriter.Validate(_m, text) is { } invalid) { _error = invalid + " Nothing written."; return null; }
        try { SetupWriter.Write(_m, text); }
        catch (SetupException ex) { _error = ex.Message; _writeFailed = true; return null; }
        Summary = $"Saved {_m.Path} ({(changed == 1 ? "1 change" : $"{changed} changes")})"
            + (_m.Document.Existed && _m.Document.Comments > 0 ? $"; the original is kept as {_m.Path}.bak." : ".");
        return 0;
    }

    int? HandleConfirm(ConsoleKeyInfo k)
    {
        if (k.Key == ConsoleKey.Y)
        {
            if (!_confirmReset) { Summary = Builder ? "Quit; nothing printed." : "Quit; nothing saved."; return 0; }
            SetupWriter.Reset(_m!);
            if (Builder) { _target!.ClearOptions(); _m = _target.Model; }
            _screen = _under;
            return null;
        }
        if (k.Key is ConsoleKey.N or ConsoleKey.Escape) _screen = _under;
        return null;
    }

    // ---- frames -----------------------------------------------------------------------------------------------------

    int PaneLines() => _terminal.Height >= 30 ? 5 : _terminal.Height >= 20 ? 4 : 3;

    /// <summary>Body rows: the window minus header (2), position, rule, pane, footer and the cursor's line.</summary>
    int Page() => Math.Clamp(_terminal.Height - 6 - PaneLines(), 1, 15);

    int Width => Math.Max(10, _terminal.Width) - 1;

    void Render(bool resized)
    {
        Frame frame;
        if (Small()) frame = new Frame([new Line(TooSmall, LineStyle.Error)]);
        else
        {
            var lines = new List<Line> { new(Header(), LineStyle.Header), new(Context(), LineStyle.Dim) };
            var screen = _screen is Screen.Entry or Screen.Confirm ? _under : _screen;
            var (items, state, highlight) = Body(screen);
            int page = Page();
            if (state.Index < state.Top) state.Top = state.Index;
            if (state.Index >= state.Top + page) state.Top = state.Index - page + 1;
            state.Top = Math.Clamp(state.Top, 0, Math.Max(0, items.Count - page));
            for (int i = state.Top; i < state.Top + page && i < items.Count; i++)
            {
                bool h = highlight && i == state.Index;
                lines.Add(new((h ? "> " : "  ") + items[i], h ? LineStyle.Highlight : LineStyle.Normal));
            }
            lines.Add(new(items.Count == 0 ? "  (nothing here)" : $"  {state.Top + 1}-{Math.Min(items.Count, state.Top + page)} of {items.Count}"
                + (state.Top > 0 ? "  more above" : "") + (state.Top + page < items.Count ? "  more below" : ""), LineStyle.Dim));
            lines.Add(new(new string('-', Math.Min(Width, 78)), LineStyle.Dim));
            var pane = Wrap(_error ?? Pane(), Width);
            int paneLines = PaneLines();
            if (pane.Count > paneLines) pane = [.. pane.Take(paneLines - 1), pane[paneLines - 1].Length < Width ? pane[paneLines - 1] + "…" : pane[paneLines - 1][..^1] + "…"];
            for (int i = 0; i < paneLines; i++)
                lines.Add(new(i < pane.Count ? pane[i] : "", _error is not null && i < pane.Count ? LineStyle.Error : LineStyle.Normal));
            lines.Add(Footer());
            frame = new Frame(lines);
        }
        _onFrame?.Invoke(frame);
        _writer.Draw(frame, resized);
    }

    string Header() => _hooks is not null ? _hooks.Header
        : Builder ? $"Skarp setup  command line  skarp {_target!.Command}  {Changed()} changed"
        : _screen == Screen.Commands ? "Skarp setup  command line"
        : _m is null ? "Skarp setup" : $"Skarp setup  {_m.Target.Name()}  {_m.ShownPath}  {Changed()} changed";

    string Context()
    {
        string note = _m is { IsMachine: true } ? "  (" + MachineNote + ")" : "";
        var screen = _screen is Screen.Entry or Screen.Confirm ? _under : _screen;
        return screen switch
        {
            Screen.Levels => "Choose the settings level to edit",
            Screen.Commands => "Choose the command to build",
            Screen.Shell => "Show the command for which shell?",
            Screen.Hooks => _hooks!.Context,
            Screen.Sections when Builder => "Sections  (changes over your settings files; nothing is saved)",
            Screen.Sections => "Sections" + (_m!.NewRepositoryFile && !_m.Document.Existed ? "  (new file: created on the first save)" : "") + note,
            Screen.Rows when OnRunOptions => "Run options" + (_filter ? "  (changed only)" : ""),
            Screen.Rows => $"{SetupRows.SectionName(Section)}{(_filter ? Builder ? "  (changed only)" : "  (set at this level only)" : "")}{note}",
            Screen.Picker => $"{_runOption?.Name ?? _row!.Key}: choose a value",
            _ => "Preview of the changes",
        };
    }

    (IReadOnlyList<string> Items, ListState State, bool Highlight) Body(Screen screen)
    {
        var items = new List<string>();
        switch (screen)
        {
            case Screen.Levels:
                // The first column is as wide as the longest level name plus two spaces, so "command line" lines up too.
                int width = 0;
                foreach (var l in _levels) width = Math.Max(width, l.Level.Name().Length);
                width += 2;
                foreach (var l in _levels)
                    items.Add(l.Level.Name().PadRight(width) + (l.Level == SettingOrigin.CommandLine ? "build a command, save nothing"
                        : $"{l.Path ?? "(no location)"}  {(l.Found ? "found" : "not found")}"));
                AddHooksRow(items);
                return (items, _levelList, true);
            case Screen.Hooks:
                var (hookItems, hookState) = _hooks!.Body();
                return (hookItems, hookState, true);
            case Screen.Commands:
                return (CommandLines(), _commandList, true);
            case Screen.Shell:
                return (Shells, _shellList, true);
            case Screen.Sections when Builder:
                return (BuilderSections(), _sectionList, true);
            case Screen.Sections:
                foreach (var s in SetupRows.Sections) items.Add($"{SetupRows.SectionName(s),-11} {SetupRows.SetHere(_m!, s)} set here");
                items.Add("Reset everything (empty this level to { \"version\": 1 })");
                return (items, _sectionList, true);
            case Screen.Rows when OnRunOptions:
                return (RunOptionLines(), _rowList, true);
            case Screen.Rows:
                return (RowLines(), _rowList, true);
            case Screen.Picker:
                foreach (var o in _options) items.Add(o.Label + (o.Marker is { } m ? $"  • {m}" : ""));
                return (items, _pickList, true);
            default:
                return (_preview, _previewList, false);
        }
    }

    List<string> RowLines()
    {
        var stack = LevelModel.Thresholds(_m!.Stack());
        var changed = _m.Changed();
        int nameWidth = Math.Clamp(Width * 7 / 20, 12, 30), valueWidth = Math.Clamp(Width * 3 / 10, 8, 30);
        var lines = new List<string>();
        foreach (var r in Rows())
        {
            var v = SetupRows.View(_m, r, stack);
            lines.Add($"{(Touches(changed, r.Key) ? '*' : ' ')}{Cut(v.Name, nameWidth)} {Cut(v.Value, valueWidth)} {v.Origin}{(v.Above is { } a ? "  " + a : "")}");
        }
        return lines;
    }

    static string Cut(string s, int width) => s.Length > width ? s[..(width - 1)] + "~" : s.PadRight(width);

    string Pane()
    {
        var screen = _screen is Screen.Entry or Screen.Confirm ? _under : _screen;
        switch (screen)
        {
            case Screen.Levels when OnHooksRow:
                return _screenPanes[ScreenRow];
            case Screen.Hooks:
                return _hooks!.Pane();
            case Screen.Levels:
                return _levels[_levelList.Index].Level switch
                {
                    SettingOrigin.System => "Settings for every user of this machine, under the personal and repository files. The gate ignores them.",
                    SettingOrigin.Personal => "Your own settings, for every repository you analyze, under the repository's file. The gate ignores them.",
                    SettingOrigin.CommandLine => CommandLinePane,
                    _ => "This repository's .skarp.json, shared with everyone who clones it; it overrides the system and personal files.",
                };
            case Screen.Commands:
                return CommandsPane;
            case Screen.Shell:
                return ShellPane(_shellList.Index);
            case Screen.Sections when Builder && OnRunOptions:
                return RunOptionsSectionText;
            case Screen.Sections:
                return OnReset
                    ? Builder ? "Clears every run option and setting changed here, back to the bare command." : "Removes every key at this level, so every value comes from the levels below or Skarp's defaults (the preview shows each one)."
                    : SectionText(Section);
            case Screen.Rows when OnRunOptions:
                return RunOptionPane();
            case Screen.Rows:
                var rows = Rows();
                if (rows.Count == 0) return "No keys are set at this level in this section (f shows all).";
                var row = rows[Math.Min(_rowList.Index, rows.Count - 1)];
                var above = _m!.AboveValue(row.Key) is { } a ? $" The {a.Level.Name()} file sets it too, so a change here has no effect there." : "";
                return $"{row.Description}{(row.RangeText is { } range ? $" Range: {range}." : "")}{above} Key: {row.Key}.";
            case Screen.Picker:
                return _options.Count == 0 ? "" : _options[_pickList.Index].Pane;
            default:
                return _m!.Changed().Count == 0 ? "Nothing changed: n goes back." : "y saves, n goes back to editing.";
        }
    }

    static string SectionText(SettingSection s) => s switch
    {
        SettingSection.Rules => "Switch rules on or off.",
        SettingSection.Thresholds => "The preset, then each rule's notice and warning thresholds with every vendor's published value, and the rule parameters.",
        SettingSection.History => "How much git history is read and which commits count.",
        SettingSection.Score => "The code health score: model, decay and the worst-files list.",
        SettingSection.Semantic => "The semantic tier (a design-time build of the solution) and its rules.",
        SettingSection.Agent => "The agent loop: saved runs, line budgets and tracking files.",
        SettingSection.Lists => "Globs, patterns and other lists, each edited as one comma-separated line; a list set here gets its replace or append mode.",
        _ => "Everything else: test files, report size, choices of metric and unit, and the variant caps.",
    };

    Line Footer()
    {
        if (_screen == Screen.Entry && _runOption is { } run) return _entry.Line($"{run.Name}: ");
        if (_screen == Screen.Entry)
        {
            var row = _row!;
            var what = _customThreshold ? $"{row.Key} in \"N or more\"" : row.Kind == SettingKind.List ? $"{row.Key} (comma-separated)" : row.Key;
            var range = !_customThreshold && row.RangeText is { } r ? $" ({r})" : "";
            return _entry.Line($"{what}{range}: ");
        }
        if (_screen == Screen.Hooks) return _hooks!.Footer;
        if (_screen == Screen.Confirm)
            return new(_confirmReset ? Builder ? "Clear every change? (y/n)" : "Reset everything at this level? (y/n)" : Builder ? "Quit without printing? (y/n)" : "Quit without saving? (y/n)");
        return new(_screen switch
        {
            Screen.Levels => "Up/Down move  Enter open  Esc quit",
            Screen.Commands => "Up/Down move  Enter choose  Esc " + (LevelChosenHere ? "back" : "quit"),
            Screen.Shell => "Up/Down move  Enter show  Esc back",
            Screen.Sections when Builder => "Up/Down move  Enter open  s show command  Esc " + (Changed() > 0 ? "quit" : "back"),
            Screen.Rows when Builder => "Enter edit  Space toggle  Del undo  f changed only  s show command  Esc back",
            Screen.Sections => "Up/Down move  Enter open  s save  Esc " + (Changed() > 0 || !LevelChosenHere ? "quit" : "back"),
            Screen.Rows => "Enter edit  Space toggle  Del inherit  f set here only  u revert  s save  Esc back",
            Screen.Picker => "Up/Down move  Enter choose  Esc back",
            _ => _writeFailed ? "p print the file instead  n back" : Changed() > 0 ? "Save? y yes  n no" : "n back",
        }, LineStyle.Dim);
    }

    static List<string> Wrap(string text, int width)
    {
        var result = new List<string>();
        var line = new StringBuilder();
        foreach (var word in text.Split(' '))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width) { result.Add(line.ToString()); line.Clear(); }
            if (line.Length > 0) line.Append(' ');
            line.Append(word.Length > width ? word[..width] : word);
        }
        if (line.Length > 0) result.Add(line.ToString());
        return result;
    }
}
