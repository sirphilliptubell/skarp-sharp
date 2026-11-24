using System.Text.Json.Nodes;
using SkarpSharp.Configuration;

namespace SkarpSharp.Setup;

/// <summary>
/// The "command line" target of the menu: the command choice, the "Run options" section,
/// the settings sections filtered to the command's keys, and the shell choice that prints the command after the menu
/// closes. Nothing is written.
/// </summary>
public sealed partial class SetupMenu
{
    const string CommandLinePane = "Build a Skarp command for one run: choose a command, run options and settings over your settings files, then print it for PowerShell, cmd or sh, or all as Markdown. Nothing is saved.";
    const string CommandsPane = "Run options and settings come next, shown over your settings files; the command is printed after the menu closes, for the shell you choose.";
    const string RunOptionsSectionText = "The folder, report format, output file, baseline and the other flags of one run that are not settings.";

    static readonly IReadOnlyList<string> Shells = ["PowerShell", "cmd", "bash/sh", "All (Markdown)"];

    /// <summary>The shell chosen for the printed command (null: Markdown), set when the menu printed one.</summary>
    public Shell? PrintedShell { get; private set; }

    static string ShellPane(int index) => index switch
    {
        0 => "Windows PowerShell 5.1 and PowerShell 7: values in single quotes. A value with \" is left out with a note.",
        1 => "cmd.exe at the prompt: values in double quotes. % cannot be written there, so a setting with % is left out with a note.",
        2 => "bash, zsh, sh and Git Bash: values in single quotes.",
        _ => "All three as a Markdown section with one code block per shell, for docs or CI notes.",
    };

    List<string> CommandLines()
    {
        var items = new List<string>();
        foreach (var (name, description) in CommandTarget.Commands) items.Add($"{name,-9} {description}");
        return items;
    }

    int? HandleCommands(ConsoleKeyInfo k)
    {
        if (Move(_commandList, k, CommandTarget.Commands.Count, Page())) return null;
        if (k.Key == ConsoleKey.Escape)
        {
            if (LevelChosenHere) { _target = null; _m = null; _screen = Screen.Levels; return null; }
            Summary = "Nothing printed.";
            return 0;
        }
        if (k.Key != ConsoleKey.Enter) return null;
        var name = CommandTarget.Commands[_commandList.Index].Name;
        try
        {
            if (_target is null) _target = _openCommand!(name);
            else _target.SetCommand(name);
        }
        catch (SetupException ex) { _error = ex.Message; return null; }
        _m = _target.Model;
        _screen = Screen.Sections;
        _sectionList.Index = 0;
        return null;
    }

    List<string> BuilderSections()
    {
        var items = new List<string> { $"{"Run options",-11} {_target!.Options.Count} changed" };
        foreach (var s in MenuSections()) items.Add($"{SetupRows.SectionName(s),-11} {SetupRows.SetHere(_m!, s)} changed");
        items.Add("Clear every change (the bare command)");
        return items;
    }

    // ---- run options ----------------------------------------------------------------------------------------------------

    List<RunOption> RunOptionList()
    {
        var command = SettingsRegistry.Command(_target!.Command);
        var list = new List<RunOption>();
        foreach (var o in RunOptions.All)
            if ((o.Commands & command) != 0 && (!_filter || _target.Options.ContainsKey(o.Name))) list.Add(o);
        return list;
    }

    List<string> RunOptionLines()
    {
        int nameWidth = Math.Clamp(Width * 7 / 20, 12, 30), valueWidth = Math.Clamp(Width * 3 / 10, 8, 30);
        var lines = new List<string>();
        foreach (var o in RunOptionList())
        {
            bool set = _target!.Options.TryGetValue(o.Name, out var v);
            lines.Add($"{(set ? '*' : ' ')}{Cut(o.Name, nameWidth)} {Cut(set ? v! : o.Default, valueWidth)} {(set ? "set here" : "default")}");
        }
        return lines;
    }

    string RunOptionPane()
    {
        var list = RunOptionList();
        if (list.Count == 0) return "No run option is changed (f shows all).";
        var o = list[Math.Min(_rowList.Index, list.Count - 1)];
        var flag = o.Flag.Length == 0 ? "the directory argument" : o.Flag;
        var values = o.Kind == RunOptionKind.Choice ? $" Values: {string.Join(", ", o.AllowedFor(_target!.Command))}." : "";
        return $"{o.Description}{values} Default: {o.Default}; flag: {flag}.";
    }

    int? HandleRunOptions(ConsoleKeyInfo k)
    {
        var list = RunOptionList();
        if (Move(_rowList, k, list.Count, Page())) return null;
        switch (k.Key)
        {
            case ConsoleKey.Escape: _screen = Screen.Sections; return null;
            case ConsoleKey.S: StartShell(Screen.Rows); return null;
            case ConsoleKey.F: _filter = !_filter; _rowList.Index = 0; _rowList.Top = 0; return null;
        }
        if (list.Count == 0) return null;
        var o = list[Math.Min(_rowList.Index, list.Count - 1)];
        switch (k.Key)
        {
            case ConsoleKey.U or ConsoleKey.Delete: SetOption(o, null); break;
            case ConsoleKey.Spacebar when o.Kind == RunOptionKind.Switch: ToggleOption(o); break;
            case ConsoleKey.Enter: EditOption(o); break;
        }
        return null;
    }

    void SetOption(RunOption o, string? value)
    {
        Try(() => _target!.SetOption(o.Name, value));
        _m = _target!.Model;
    }

    void ToggleOption(RunOption o) => SetOption(o, _target!.Options.ContainsKey(o.Name) ? null : "on");

    void EditOption(RunOption o)
    {
        if (o.Kind == RunOptionKind.Switch) { ToggleOption(o); return; }
        _runOption = o;
        _row = null;
        bool set = _target!.Options.TryGetValue(o.Name, out var current);
        if (o.Kind == RunOptionKind.Path) { OpenEntry(Screen.Rows, custom: false, set ? current! : ""); return; }
        var options = new List<SetupOption>();
        foreach (var v in o.AllowedFor(_target.Command))
            options.Add(new(v, OptionAction.Set, JsonValue.Create(v), o.Description, set && v == current ? "(set here)" : !set && v == o.Default ? "(default)" : null));
        options.Add(new($"default ({o.Default})", OptionAction.Remove, null, $"Leaves {o.Flag} out of the command."));
        _options = options;
        _screen = Screen.Picker;
        _pickList.Index = 0;
        for (int i = 0; i < options.Count; i++) if (options[i].IsCurrent) _pickList.Index = i;
        _pickList.Top = 0;
    }

    // ---- printing ---------------------------------------------------------------------------------------------------------

    void StartShell(Screen from)
    {
        _under = from;
        _error = null;
        _screen = Screen.Shell;
    }

    int? HandleShell(ConsoleKeyInfo k)
    {
        if (Move(_shellList, k, Shells.Count, Page())) return null;
        if (k.Key == ConsoleKey.Escape) { _screen = _under; _error = null; return null; }
        if (k.Key != ConsoleKey.Enter) return null;
        CommandBuilder built;
        try { built = _hooks is not null ? _hooks.Build() : _target!.Build(); }
        catch (SetupException ex) { _error = ex.Message; return null; }
        Shell? shell = _shellList.Index switch { 0 => Shell.PowerShell, 1 => Shell.Cmd, 2 => Shell.Sh, _ => null };
        PrintText = shell is { } s ? built.Render(s, out _) : built.RenderMarkdown(out _);
        PrintedShell = shell;
        Summary = "";
        return 0;
    }
}
