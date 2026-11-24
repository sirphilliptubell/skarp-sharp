using System.Text;
using System.Text.Json.Nodes;
using SkarpSharp.Configuration;

namespace SkarpSharp.Setup;

/// <summary>What a built command needs: the command, its run options, the changed settings and where it runs.</summary>
public sealed class CommandRequest
{
    public required string Command { get; init; }
    /// <summary>Run option values by name (<see cref="RunOptions"/>); a switch is <c>on</c>.</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();
    /// <summary>The command-line level: only the changed keys, as the editor holds them.</summary>
    public required JsonObject Changes { get; init; }
    /// <summary>The current folder: paths are printed relative to it.</summary>
    public required string Cwd { get; init; }
    public required SettingsLocations Locations { get; init; }
    /// <summary>The <c>--config</c> file given to <c>skarp setup</c>, passed on.</summary>
    public string? Config { get; init; }
}

/// <summary>One argument of a built command: its text, the part that is a path (from <see cref="PathFrom"/>; -1 none)
/// and the setting or run option it belongs to (a group is printed or left out as a whole).</summary>
public sealed class CommandArg(string text, string group, int pathFrom = -1)
{
    public string Text { get; } = text;
    public string Group { get; } = group;
    public int PathFrom { get; } = pathFrom;
}

/// <summary>
/// Builds <c>skarp &lt;command&gt; &lt;directory&gt; &lt;run options&gt;
/// &lt;dedicated flags&gt; &lt;--setting ...&gt;</c> from the changed settings, in registry order. A changed key uses its
/// dedicated flag when that flag expresses the value exactly, else <c>--setting key=value</c>. Before printing, the
/// arguments are parsed back by <see cref="CommandLine.Parse(IReadOnlyList{string}, SettingsLocations?)"/> and must give
/// the same effective settings as the same changes all written as <c>--setting</c>.
/// </summary>
public sealed class CommandBuilder
{
    /// <summary>A line longer than this gets the "put these settings in .skarp.json" note.</summary>
    public const int LongLine = 400;
    const string FileAdvice = "set it in .skarp.json (skarp setup repository)";

    readonly CommandRequest _r;
    readonly List<CommandArg> _args = [];
    readonly List<CommandArg> _reference = [];
    readonly List<string> _notes = [];

    /// <summary>The arguments after <c>skarp</c>, unquoted, paths relative to the current folder.</summary>
    public IReadOnlyList<CommandArg> Args => _args;
    /// <summary>Notes for every shell (settings no command line can carry).</summary>
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Built by <see cref="FromArgs"/>: every argument is needed, so a shell that cannot carry one gets no line.</summary>
    bool _whole;

    CommandBuilder(CommandRequest r) => _r = r;

    /// <summary>
    /// A ready argument list (after <c>skarp</c>), printed as it is with the same shells, header and Markdown.
    /// No settings notes and no long-line hint; a shell that cannot carry an argument gets a note instead of the line.
    /// The caller checks the arguments.
    /// </summary>
    public static CommandBuilder FromArgs(IReadOnlyList<string> args, string cwd)
    {
        var b = new CommandBuilder(new CommandRequest { Command = args[0], Changes = [], Cwd = cwd, Locations = SettingsLocations.None }) { _whole = true };
        var group = "command";
        foreach (var a in args)
        {
            if (a.StartsWith("--", StringComparison.Ordinal)) group = a;
            b.Add(new CommandArg(a, group));
        }
        return b;
    }

    /// <summary>Builds and checks the command.</summary>
    /// <exception cref="SetupException">The command does not parse (the message is Skarp's), or the printed form gives
    /// other effective settings than the changes (an internal error).</exception>
    public static CommandBuilder Build(CommandRequest r)
    {
        var b = new CommandBuilder(r);
        b.AddRunOptions();
        b.AddSettings();
        b.Check();
        return b;
    }

    // ---- arguments ----------------------------------------------------------------------------------------------------

    void Add(CommandArg a, bool both = true)
    {
        _args.Add(a);
        if (both) _reference.Add(a);
    }

    void AddRunOptions()
    {
        Add(new CommandArg(_r.Command, "command"));
        var dir = _r.Options.GetValueOrDefault("directory") is { Length: > 0 } d ? Relative(d) : ".";
        Add(new CommandArg(dir, "directory", 0));
        if (_r.Config is not null) { Add(new CommandArg("--config", "config")); Add(new CommandArg(Relative(_r.Config), "config", 0)); }
        foreach (var o in RunOptions.All)
        {
            if (o.Flag.Length == 0 || !_r.Options.TryGetValue(o.Name, out var v) || (o.Commands & SettingsRegistry.Command(_r.Command)) == 0) continue;
            switch (o.Kind)
            {
                case RunOptionKind.Switch: if (v == "on") Add(new CommandArg(o.Flag, o.Name)); break;
                case RunOptionKind.Path: Add(new CommandArg(o.Flag, o.Name)); Add(new CommandArg(Relative(v), o.Name, 0)); break;
                default: Add(new CommandArg(o.Flag, o.Name)); Add(new CommandArg(v, o.Name)); break;
            }
        }
    }

    /// <summary>A path relative to the current folder (another drive stays absolute).</summary>
    string Relative(string path)
    {
        if (!Path.IsPathRooted(path)) return path;
        return Path.GetRelativePath(_r.Cwd, path);
    }

    void AddSettings()
    {
        var keys = new List<string>(SettingsDocument.Leaves(_r.Changes));
        keys.RemoveAll(k => k == "version");
        keys.Sort(CompareKeys);
        var dedicated = new List<CommandArg>();
        var generic = new List<CommandArg>();
        foreach (var key in keys)
        {
            var value = SettingsDocument.Get(_r.Changes, key, out _);
            var settings = SettingArgs(key, value);
            if (settings is null) { _notes.Add($"{key} is left out: its value cannot be written on a command line; {FileAdvice}"); continue; }
            foreach (var a in settings) _reference.Add(a);
            if (Dedicated(key, value) is { } flags) dedicated.AddRange(flags);
            else generic.AddRange(settings);
        }
        _args.AddRange(dedicated);
        _args.AddRange(generic);
    }

    /// <summary>Registry order: the row's place in <see cref="SettingsRegistry.Editable"/>, then the key.</summary>
    static int CompareKeys(string a, string b)
    {
        int c = Index(a).CompareTo(Index(b));
        return c != 0 ? c : string.CompareOrdinal(a, b);
    }

    static int Index(string key)
    {
        if (SettingsRegistry.ListOfMode(key) is { } list) key = list.Key;
        var row = SettingsRegistry.Row(key) ?? SettingsRegistry.Governing(key);
        for (int i = 0; i < SettingsRegistry.Editable.Count; i++) if (ReferenceEquals(SettingsRegistry.Editable[i], row)) return i;
        return int.MaxValue;
    }

    /// <summary><c>--setting key=value</c> (a threshold level with metric values: one per metric); null when the value
    /// has no text form.</summary>
    List<CommandArg>? SettingArgs(string key, JsonNode? value)
    {
        var list = new List<CommandArg>();
        if (value is JsonObject metrics)
        {
            foreach (var (metric, v) in metrics)
                if (Text(v) is { } t) { list.Add(new("--setting", key)); list.Add(new($"{key}.{metric}={t}", key)); }
                else return null;
            return list.Count > 0 ? list : null;
        }
        if (Text(value) is not { } text) return null;
        list.Add(new("--setting", key));
        list.Add(key == "semantic.solution" && value is not null ? new($"{key}={Relative(text)}", key, key.Length + 1) : new($"{key}={text}", key));
        return list;
    }

    /// <summary>The grammar's text of a value, or null when parsing it back would not give the same value.</summary>
    static string? Text(JsonNode? value)
    {
        var t = SettingText.Format(value);
        return t is not null && t.Trim().Length == t.Length && (t.Length > 0 || value is JsonArray) ? t : null;
    }

    static List<CommandArg> Flag(string key, params string[] tokens)
    {
        var list = new List<CommandArg>(tokens.Length);
        foreach (var t in tokens) list.Add(new CommandArg(t, key));
        return list;
    }

    static bool? Bool(JsonNode? v) => v is JsonValue j && j.TryGetValue<bool>(out var b) ? b : null;

    /// <summary>
    /// The dedicated flag that expresses <paramref name="value"/> exactly, or null: not for <c>null</c> or <c>[]</c>, not
    /// for flags that append (<c>include</c>, <c>exclude</c>, <c>history.botPatterns</c>) or go one way only when the value
    /// is the other way, and not for keys without a flag.
    /// </summary>
    List<CommandArg>? Dedicated(string key, JsonNode? value)
    {
        if (value is null or JsonArray { Count: 0 }) return null;
        var text = Text(value);
        var b = Bool(value);
        if (key.StartsWith("rules.", StringComparison.Ordinal))
            return b is { } on ? Flag(key, "--rule", $"{key["rules.".Length..]}={(on ? "on" : "off")}") : null;
        if (key.StartsWith("thresholds.", StringComparison.Ordinal))
        {
            if (value is JsonObject metrics)
            {
                var list = new List<CommandArg>();
                foreach (var (metric, v) in metrics) list.AddRange(Flag(key, "--threshold", $"{key["thresholds.".Length..]}.{metric}={Text(v)}"));
                return list;
            }
            return text is null ? null : Flag(key, "--threshold", $"{key["thresholds.".Length..]}={text}");
        }
        return key switch
        {
            "preset" or "complexMetric" or "longMethodLines" or "recordParameters" or "magicNumbers" or "score.model"
                => Flag(key, "--" + FlagName(key), text!),
            "top" => Flag(key, "--top", text!),
            "report.perFile" => Flag(key, "--per-file", text!),
            "history.window" => Flag(key, "--window", text!),
            "history.since" => Flag(key, "--since", text!),
            "history.bulkFlag" => Flag(key, "--bulk-flag", text!),
            "history.bulkDrop" => Flag(key, "--bulk-drop", text!),
            "includeTests" => Both(key, b, "--include-tests", "--no-include-tests"),
            "history.enabled" => Both(key, b, "--history", "--no-history"),
            "history.ignoreWhitespace" => Both(key, b, "--ignore-whitespace", "--no-ignore-whitespace"),
            "history.includeBots" => Both(key, b, "--include-bots", "--no-include-bots"),
            "gate.enabled" => Both(key, b, "--gate", "--no-gate"),
            "semantic.enabled" => Both(key, b, "--semantic", "--no-semantic"),
            "semantic.generators" => Both(key, b, "--generators", "--no-generators"),
            "history.allHistory" => b == true ? Flag(key, "--all-history") : null,
            "namespaceCycles.includeGeneratedTargets" => b == true ? Flag(key, "--include-generated-targets") : null,
            "saveLastRun" => b == false ? Flag(key, "--no-save") : null,
            "semantic.solution" => text is null ? null : [new CommandArg("--solution", key), new CommandArg(Relative(text), key, 0)],
            "gate.on" => Replacing(key, value, "--gate-on"),
            "gate.health.maxDrop" => Flag(key, "--gate-health-drop", text!),
            "gate.health.newFileMin" => Flag(key, "--gate-health-new-min", text!),
            "gate.health.floor" => Flag(key, "--gate-health-floor", text!),
            "semantic.targets" => Targets(key, value),
            _ => null,
        };
    }

    static string FlagName(string key) => key switch
    {
        "complexMetric" => "complex-metric",
        "longMethodLines" => "long-method-lines",
        "recordParameters" => "record-parameters",
        "magicNumbers" => "magic-numbers",
        "score.model" => "score-model",
        _ => key,
    };

    static List<CommandArg>? Both(string key, bool? b, string on, string off) => b is { } v ? Flag(key, v ? on : off) : null;

    /// <summary>A list whose flag replaces the inherited list: one flag per item, only when this level replaces.</summary>
    List<CommandArg>? Replacing(string key, JsonNode? value, string flag)
    {
        if (value is not JsonArray items || Appends(key)) return null;
        var list = new List<CommandArg>();
        foreach (var item in items) if (Text(item) is { } t) list.AddRange(Flag(key, flag, t)); else return null;
        return list;
    }

    List<CommandArg>? Targets(string key, JsonNode? value)
    {
        if (value is not JsonArray items || Appends(key)) return null;
        if (items.Count == 1 && Text(items[0]) == "all") return Flag(key, "--all-targets");
        foreach (var item in items) if (Text(item) == "all") return null;
        return Replacing(key, value, "--target");
    }

    bool Appends(string key) => Text(SettingsDocument.Get(_r.Changes, key + "Mode", out _)) == "append";

    // ---- the check ----------------------------------------------------------------------------------------------------

    /// <summary>Parses the printed arguments and the all-<c>--setting</c> form back; their effective settings must agree.</summary>
    void Check()
    {
        var reference = CommandLine.Parse(Absolute(_reference), _r.Locations);
        if (reference.Options is not { } expected) throw new SetupException(reference.Error ?? "the command does not parse");
        var printed = CommandLine.Parse(Absolute(_args), _r.Locations);
        if (printed.Options is not { } actual)
            throw new SetupException($"internal error: the printed command does not parse ({printed.Error}); please report it");
        if (Snapshot(expected) != Snapshot(actual))
            throw new SetupException("internal error: the printed command gives other settings than the changes; please report it");
    }

    List<string> Absolute(List<CommandArg> args)
    {
        var list = new List<string>(args.Count);
        foreach (var a in args)
            list.Add(a.PathFrom < 0 ? a.Text : a.Text[..a.PathFrom] + Path.GetFullPath(Path.Combine(_r.Cwd, a.Text[a.PathFrom..])));
        return list;
    }

    /// <summary>The effective settings of a run in words: every setting's value and every rule's resolved line.</summary>
    internal static string Snapshot(SkarpOptions o)
    {
        var sb = new StringBuilder();
        foreach (var s in o.Settings()) sb.Append(s.Key).Append('=').Append(s.Value).Append('\n');
        var rules = o.RuleSettings();
        foreach (var r in rules.ToInfo(coverage: true).Rules) sb.Append(Baselines.Baseline.RuleLine(r)).Append('\n');
        foreach (var r in rules.ToSemanticInfo().Rules) sb.Append(Baselines.Baseline.RuleLine(r)).Append('\n');
        return sb.ToString();
    }

    // ---- printing -------------------------------------------------------------------------------------------------------

    /// <summary>The command line for one shell, with the notes for what it leaves out; <paramref name="lossy"/> is true
    /// when a setting could not be written for this shell.</summary>
    public (string Line, List<string> Notes) Line(Shell shell, out bool lossy)
    {
        var notes = new List<string>(_notes);
        var (args, dropped) = Arguments(shell);
        foreach (var g in dropped)
        {
            var text = _args.First(a => a.Group == g && ShellQuote.Quote(shell, ShellPath(shell, a)) is null).Text;
            if (Essential(g))
            {
                lossy = true;
                return ("", [Unwritable(shell, g, text)]);
            }
            notes.Add($"{g} is left out: {Why(shell, text)}; {FileAdvice}{(shell == Shell.Sh ? "" : " or use bash/sh")}");
        }
        var sb = new StringBuilder("skarp");
        var bang = new List<string>();
        bool msys = false;
        foreach (var a in _args)
        {
            if (Dropped(dropped, a.Group)) continue;
            var text = ShellPath(shell, a);
            sb.Append(' ').Append(ShellQuote.Quote(shell, text));
            if (shell == Shell.Cmd && text.Contains('!') && !bang.Contains(a.Group)) bang.Add(a.Group);
            if (shell == Shell.Sh && ShellQuote.MsysMayConvert(text)) msys = true;
        }
        foreach (var g in bang) notes.Add($"if delayed expansion is on, ! in {g} is lost");
        if (msys) notes.Add("Git Bash on Windows: put MSYS_NO_PATHCONV=1 before skarp, or arguments starting with / are rewritten as Windows paths");
        if (TooLong(sb.Length)) notes.Add("long command: consider putting these settings in .skarp.json (skarp setup repository)");
        lossy = dropped.Count > 0;
        return (sb.ToString(), notes);
    }

    /// <summary>A group the command cannot do without: the command and directory, or every group of <see cref="FromArgs"/>.</summary>
    bool Essential(string group) => _whole || group is "command" or "directory";

    string Unwritable(Shell shell, string group, string text) =>
        $"the command cannot be written for {Name(shell)}: {(_whole ? text + ":" : "the " + group)} {Why(shell, text)}";

    /// <summary>The "put these settings in .skarp.json" hint is for built commands only.</summary>
    bool TooLong(int length) => !_whole && length > LongLine;

    static bool Dropped(List<string> dropped, string group) =>
        dropped.Contains(group) || (group.EndsWith("Mode", StringComparison.Ordinal) && dropped.Contains(group[..^"Mode".Length]));

    /// <summary>The arguments Skarp receives from the line printed for <paramref name="shell"/> (paths with that shell's
    /// separators), and the groups left out because the shell cannot carry them (a list takes its mode with it).</summary>
    public (List<string> Args, List<string> Dropped) Arguments(Shell shell)
    {
        var dropped = new List<string>();
        foreach (var a in _args)
            if (!dropped.Contains(a.Group) && ShellQuote.Quote(shell, ShellPath(shell, a)) is null) dropped.Add(a.Group);
        var args = new List<string>();
        foreach (var a in _args) if (!Dropped(dropped, a.Group)) args.Add(ShellPath(shell, a));
        return (args, dropped);
    }

    static string Why(Shell shell, string text) => shell switch
    {
        Shell.Cmd when text.Contains('%') => "cmd expands % even inside quotes",
        Shell.PowerShell when text.Contains('"') => "Windows PowerShell 5.1 and PowerShell 7 pass a \" to programs differently",
        Shell.PowerShell when !text.Contains('\n') && !text.Contains('\r') => "Windows PowerShell 5.1 and PowerShell 7 pass a value with spaces and a trailing backslash differently",
        _ => "a line break cannot be written on one command line",
    };

    /// <summary>A path part with the shell's separators: <c>/</c> for sh, <c>\</c> for cmd and PowerShell.</summary>
    static string ShellPath(Shell shell, CommandArg a)
    {
        if (a.PathFrom < 0) return a.Text;
        var path = a.Text[a.PathFrom..];
        path = shell == Shell.Sh ? path.Replace('\\', '/') : path.Replace('/', '\\');
        return a.Text[..a.PathFrom] + path;
    }

    static string Comment(Shell shell) => shell == Shell.Cmd ? "REM" : "#";

    static string Name(Shell shell) => shell switch { Shell.PowerShell => "PowerShell", Shell.Cmd => "cmd", _ => "sh" };

    /// <summary>The output for one shell: a header line naming the shell and the folder, the line, then the notes as comments.</summary>
    public string Render(Shell shell, out bool lossy)
    {
        var (line, notes) = Line(shell, out lossy);
        var sb = new StringBuilder();
        sb.Append(Comment(shell)).Append(' ').Append(Name(shell)).Append(", run from ").Append(Folder()).Append('\n');
        if (line.Length > 0) sb.Append(line).Append('\n');
        foreach (var n in notes) sb.Append(Comment(shell)).Append(" note: ").Append(n).Append('\n');
        return sb.ToString();
    }

    /// <summary>All shells as Markdown: a heading line, one fenced block per shell, then the notes outside the blocks.</summary>
    public string RenderMarkdown(out bool cmdLossy)
    {
        cmdLossy = false;
        var sb = new StringBuilder();
        sb.Append("**Skarp command** (run from ").Append(Folder()).Append(")\n");
        var notes = new List<string>();
        foreach (var (shell, fence) in new[] { (Shell.PowerShell, "powershell"), (Shell.Cmd, "bat"), (Shell.Sh, "sh") })
        {
            var (line, shellNotes) = Line(shell, out bool lossy);
            if (shell == Shell.Cmd) cmdLossy = lossy;
            sb.Append("\n```").Append(fence).Append('\n').Append(line).Append("\n```\n");
            foreach (var n in shellNotes)
            {
                var text = _notes.Contains(n) || n.StartsWith("long command", StringComparison.Ordinal) ? n : $"{Name(shell)}: {n}";
                if (!notes.Contains(text)) notes.Add(text);
            }
        }
        if (notes.Count > 0) sb.Append('\n');
        foreach (var n in notes) sb.Append("Note: ").Append(n).Append('\n');
        return sb.ToString();
    }

    /// <summary>The current folder as printed (in full).</summary>
    string Folder() => Path.TrimEndingDirectorySeparator(Path.GetFullPath(_r.Cwd));
}
