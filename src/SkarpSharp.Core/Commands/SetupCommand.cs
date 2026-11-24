using System.Text;
using SkarpSharp.Configuration;
using SkarpSharp.Setup;

namespace SkarpSharp.Commands;

/// <summary>
/// <c>skarp setup</c>, the settings editor. Without edit flags it opens the inline menu on one level
/// (system, personal or repository); with <c>--set</c>, <c>--unset</c> or <c>--reset</c> it is the flag form on the same
/// writer: the same validation and preview, no terminal needed. Exit codes: 0 saved, nothing to change or quit; 2 usage
/// error, invalid value, a failed write or no terminal; 130 Ctrl+C.
/// </summary>
public static class SetupCommand
{
    public const string Usage = """
        skarp setup: edit one settings level (Skarp's settings editor).

        Usage:
          skarp setup [<level>] [--config <file>]
              the inline menu: sections, rows with the value inherited from below and any override from above, an
              explanation pane and a preview before saving; without <level> it starts with the level choice
          skarp setup --level <level> [--set <key>=<value>]... [--unset <key>]... [--reset] [--dry-run] [--print]
              the flag form (scripts and agents): the edits apply in order after --reset, the preview is printed, then
              the file is written, with no question
          skarp setup command
              experimental: build a command line instead of editing a file: choose analyze, baseline, hotspots or
              history, set run options and settings over your settings files, and print the line for PowerShell, cmd,
              bash/sh or all three as Markdown; nothing is saved or run
          skarp setup --command <command> [--set <key>=<value>]... [--option <name>=<value>]...
                      [--shell powershell|cmd|sh|markdown]
              the flag form of the command line: prints the same output (default shell: powershell on Windows, sh
              elsewhere)
          skarp setup claude
              experimental: Skarp's Claude Code hooks (also a row of the level choice): the entry at each
              scope (local, project, user) and its options (program, end-of-turn summary, gate, semantic check,
              notices, one line limit); a applies them through skarp install claude, with its preview and question,
              after the menu closes; s prints that command for a shell; options added by hand are kept
          skarp setup git
              experimental: Skarp's git hooks (last row of the level choice); a runs skarp install git-hooks, s prints it

        Levels: system (%ProgramData%\skarp\settings.json; /etc/skarp/settings.json; macOS /Library/Application
        Support/skarp/settings.json), personal (~/.skarp/settings.json; SKARP_HOME replaces ~/.skarp), repository (the
        nearest .skarp.json; --config <file> or SKARP_CONFIG picks another; a new one is created at the git root, or in
        the current folder outside git).

          --set <key>=<value>    dotted keys: preset=pmd, history.window=6, rules.long-method=false,
                                 thresholds.<rule>.<level>[.<metric>]=<number|off> (the --threshold grammar: the number
                                 is stored as written), thresholds.<rule>.<parameter>=<value>, lists comma-separated
                                 (exclude=a/**,b/**), excludeMode=append; <key>=null writes null ("use Skarp default")
          --unset <key>          remove the key at this level (inherit from the levels below)
          --reset                empty the level to { "version": 1 } before the other edits
          --dry-run              print the preview and write nothing
          --print                write the new file content to standard output instead of the file (the preview goes
                                 to standard error)
          --config <file>        the repository-level file, as for analyze
          --option <name>=<value>  a run option of the built command: directory=src, format=sarif, output=skarp.sarif,
                                 baseline=<file>, no-baseline=on, new-only=on, gate=on (analyze), min-severity=warning,
                                 all=on
          --shell <shell>        powershell, cmd, sh or markdown (all three); a value cmd cannot carry (%) is left out
                                 with a note, and with --shell cmd the exit code is 2

        The built command carries only what differs from your settings files: a dedicated flag when one expresses the
        value exactly (--preset pmd, --threshold long-method.notice=60, --window 6), else --setting <key>=<value>. It is
        parsed back by Skarp before it is printed.

        Saving a file with comments removes them; the original is kept as <file>.bak. The candidate file is checked as
        analyze would read it before anything is written. The menu needs an interactive terminal; NO_COLOR or TERM=dumb
        draws it without colour.

        Exit codes: 0 saved, nothing to change, or quit; 2 usage error, invalid value, failed write or no terminal;
        130 Ctrl+C.
        """;

    sealed class Request
    {
        public SettingOrigin? Level;
        public readonly List<(bool Set, string Key, string Value)> Edits = [];
        public bool Reset, DryRun, Print, Help;
        public string? Config, Script, Size;
        /// <summary><c>skarp setup command</c> (menu, empty) or <c>--command &lt;name&gt;</c> (flag form).</summary>
        public string? Command, Shell;
        public bool CommandMenu;
        /// <summary><c>skarp setup claude</c> or <c>skarp setup git</c>.</summary>
        public string? Screen;
        public readonly List<string> Options = [];
        public bool FlagForm => Edits.Count > 0 || Reset || DryRun || Print;
    }

    public static int Run(IReadOnlyList<string> args, CommandIO io)
    {
        var (r, error) = Parse(args);
        if (error is not null)
        {
            io.Error.WriteLine($"skarp setup: {error}");
            io.Error.WriteLine("Usage: skarp setup [<level>] | skarp setup --level <level> --set <key>=<value> ... (skarp setup --help)");
            return 2;
        }
        if (r.Help) { io.Out.WriteLine(Usage); return 0; }
        var locations = io.SettingsLocations ?? SettingsLocations.Default;
        if (r.Command is not null) return RunCommandFlags(r, io, locations);
        if (r.FlagForm) return RunFlags(r, io, locations);
        return RunMenu(r, io, locations);
    }

    static (Request, string?) Parse(IReadOnlyList<string> args)
    {
        var r = new Request();
        for (int i = 1; i < args.Count; i++)
        {
            var a = args[i];
            string? Value() => i + 1 < args.Count ? args[++i] : null;
            switch (a)
            {
                case "-h" or "--help": r.Help = true; break;
                case "--reset": r.Reset = true; break;
                case "--dry-run": r.DryRun = true; break;
                case "--print": r.Print = true; break;
                case "--command" or "--option" or "--shell":
                    if (Value() is not { } cv) return (r, $"{a} needs a value");
                    if (a == "--command") r.Command = cv;
                    else if (a == "--shell") r.Shell = cv;
                    else r.Options.Add(cv);
                    break;
                case "--level" or "--set" or "--unset" or "--config" or "--script" or "--script-size":
                    if (Value() is not { } v) return (r, $"{a} needs a value");
                    if (a == "--level") { if (LevelOf(v) is not { } l) return (r, $"unknown level '{v}' (system, personal or repository)"); if (r.Level is not null && r.Level != l) return (r, "two different levels given"); r.Level = l; }
                    else if (a == "--set")
                    {
                        int eq = v.IndexOf('=');
                        if (eq <= 0) return (r, $"--set {v}: expected <key>=<value>");
                        r.Edits.Add((true, v[..eq].Trim(), v[(eq + 1)..]));
                    }
                    else if (a == "--unset") r.Edits.Add((false, v.Trim(), ""));
                    else if (a == "--config") r.Config = v;
                    else if (a == "--script") r.Script = v;
                    else r.Size = v;
                    break;
                case "command":
                    r.CommandMenu = true;
                    break;
                case "claude" or "git":
                    r.Screen = a;
                    break;
                default:
                    if (a.StartsWith('-')) return (r, $"unknown option {a}");
                    if (LevelOf(a) is not { } level) return (r, $"unknown level '{a}' (system, personal or repository)");
                    if (r.Level is not null && r.Level != level) return (r, "two different levels given");
                    r.Level = level;
                    break;
            }
        }
        return (r, CheckCommandForm(r));
    }

    /// <summary>The combinations of the command-line target.</summary>
    static string? CheckCommandForm(Request r)
    {
        if (r.Command is not null)
        {
            if (r.Level is not null || r.CommandMenu) return "--command builds a command line; it takes no level";
            if (r.Edits.Any(e => !e.Set) || r.Reset || r.DryRun || r.Print) return "--command takes --set, --option and --shell only (nothing is written)";
            if (r.Shell is not null && ShellOf(r.Shell) is null && r.Shell != "markdown") return $"unknown shell '{r.Shell}' (powershell, cmd, sh or markdown)";
            return null;
        }
        if (r.Options.Count > 0 || r.Shell is not null) return "--option and --shell need --command <command>";
        if (r.CommandMenu && (r.Level is not null || r.FlagForm)) return "skarp setup command builds a command line; it takes no level (the flag form is skarp setup --command <command>)";
        if (r.FlagForm && r.Level is null) return "the flag form needs --level <level> (system, personal or repository)";
        return HooksForm(r);
    }

    /// <summary><c>skarp setup claude</c> takes no level and is not <c>skarp setup command</c>.</summary>
    static string? HooksForm(Request r)
    {
        if (r.Screen is null) return null;
        return r.Level is not null || r.CommandMenu ? $"skarp setup {r.Screen} edits {(r.Screen == "git" ? "Skarp's git hooks" : "Claude Code's hooks")} and takes no level (the flag form is skarp install {(r.Screen == "git" ? "git-hooks" : "claude")})" : null;
    }

    static SettingOrigin? LevelOf(string name) => name switch
    {
        "system" => SettingOrigin.System,
        "personal" => SettingOrigin.Personal,
        "repository" => SettingOrigin.Repository,
        _ => null,
    };

    // ---- opening a level --------------------------------------------------------------------------------------------

    /// <summary>The file of a level: the system or personal file, or the repository file (<c>--config</c>, else
    /// <c>SKARP_CONFIG</c>, else the nearest <c>.skarp.json</c>, else a new one at the git root or here).</summary>
    static (string? Path, bool New) FileOf(SettingOrigin level, string cwd, string? config, SettingsLocations locations)
    {
        switch (level)
        {
            case SettingOrigin.System: return (locations.SystemFile is { } s ? Path.GetFullPath(s) : null, false);
            case SettingOrigin.Personal: return (locations.PersonalFile is { } p ? Path.GetFullPath(p) : null, false);
        }
        var given = config ?? (locations.Variable(SettingsLocations.ConfigVariable) is { Length: > 0 } e ? e : null);
        if (given is not null) return (Path.GetFullPath(Path.Combine(cwd, given)), false);
        var (found, gitRoot) = SettingsFileReader.FindRepository(cwd);
        return found is not null ? (found, false) : (Path.Combine(gitRoot ?? cwd, SettingsFileReader.FileName), true);
    }

    /// <summary>
    /// Opens a level for editing: its raw file and the applied file levels below and above it, resolved as for
    /// <c>analyze</c> (not as the gate). The target's own content is left out of the resolve (an empty file stands in),
    /// so a broken target can still be repaired.
    /// </summary>
    /// <exception cref="SetupException">No location for the level, the file is not JSON, or another level is invalid.</exception>
    internal static LevelModel Open(SettingOrigin level, string cwd, string? config, SettingsLocations locations)
    {
        var (path, isNew) = FileOf(level, cwd, config, locations);
        if (path is null) throw new SetupException($"no {level.Name()} settings file location on this machine");
        var document = SettingsDocument.Open(path);
        var overlay = locations.WithOverlay(path, "{ \"version\": 1 }");
        bool repository = !level.IsMachine();
        LayeredSettings layered;
        try { layered = SettingsLevels.Resolve(new LevelRequest(cwd, repository ? path : config, false, false, false, false, overlay)); }
        catch (LevelException ex) { throw new SetupException(ex.Message); }
        int rank = Rank(level);
        var below = new List<LevelTree>();
        var above = new List<LevelTree>();
        foreach (var l in layered.Levels)
        {
            if (!l.Applied || l.Path is null || l.Level == SettingOrigin.CommandLine || string.Equals(Path.GetFullPath(l.Path), path, StringComparison.OrdinalIgnoreCase)) continue;
            if (Rank(l.Level) < rank) below.Add(Tree(l));
            else if (Rank(l.Level) > rank) above.Add(Tree(l));
        }
        return new LevelModel
        {
            Target = level, Document = document, Directory = cwd, Locations = locations, ConfigFlag = config,
            NewRepositoryFile = repository && isNew,
            Below = below,
            Above = above,
        };
    }

    static int Rank(SettingOrigin level) => level == SettingOrigin.Environment ? (int)SettingOrigin.Repository : (int)level;

    static LevelTree Tree(SettingsSource level)
    {
        var node = SettingsDocument.Open(level.Path!).Original;
        return new LevelTree(level.Level, level.Path!, node);
    }

    // ---- flag form --------------------------------------------------------------------------------------------------

    static int RunFlags(Request r, CommandIO io, SettingsLocations locations)
    {
        LevelModel m;
        try
        {
            m = Open(r.Level!.Value, io.Cwd, r.Config, locations);
            if (r.Reset) SetupWriter.Reset(m);
            foreach (var (set, key, value) in r.Edits)
                if (set) SetupWriter.Set(m, key, value); else SetupWriter.Unset(m, key);
        }
        catch (SetupException ex)
        {
            io.Error.WriteLine($"skarp setup: {ex.Message}; nothing written");
            return 2;
        }
        var text = m.Document.Render();
        var preview = r.Print ? io.Error : io.Out;
        foreach (var line in SetupWriter.Preview(m)) preview.WriteLine(line);
        bool changed = m.Changed().Count > 0;
        if (changed && SetupWriter.Validate(m, text) is { } invalid)
        {
            io.Error.WriteLine($"{invalid}; nothing written");
            return 2;
        }
        if (r.Print) { io.Out.Write(text); return 0; }
        if (r.DryRun) { if (changed) io.Out.WriteLine("dry run: nothing written"); return 0; }
        if (!changed) return 0;
        try { SetupWriter.Write(m, text); }
        catch (SetupException ex)
        {
            io.Error.WriteLine($"skarp setup: {ex.Message}; --print writes the new content to standard output instead");
            return 2;
        }
        io.Out.WriteLine($"Saved {m.Path}" + (m.Document.Existed && m.Document.Comments > 0 ? $"; the original is kept as {m.Path}.bak" : ""));
        return 0;
    }

    // ---- command line ------------------------------------------------------------------------------------

    static Shell? ShellOf(string name) => name switch
    {
        "powershell" => Setup.Shell.PowerShell,
        "cmd" => Setup.Shell.Cmd,
        "sh" or "bash" => Setup.Shell.Sh,
        _ => null,
    };

    /// <summary>
    /// <c>skarp setup --command &lt;command&gt;</c>: the run options and <c>--set</c> edits over the settings files,
    /// built, checked and printed for one shell or as Markdown. Exit 2 on an invalid value, and with an explicit
    /// <c>--shell cmd</c> when cmd cannot carry a value.
    /// </summary>
    static int RunCommandFlags(Request r, CommandIO io, SettingsLocations locations)
    {
        if (!CommandTarget.Commands.Any(c => c.Name == r.Command))
        {
            io.Error.WriteLine($"skarp setup: unknown command '{r.Command}' (analyze, baseline, hotspots or history)");
            return 2;
        }
        string text;
        bool lossy;
        try
        {
            var target = CommandTarget.Open(r.Command!, io.Cwd, r.Config, locations);
            foreach (var option in r.Options)
            {
                int eq = option.IndexOf('=');
                if (eq <= 0) throw new SetupException($"--option {option}: expected <name>=<value>");
                target.SetOption(option[..eq].Trim(), option[(eq + 1)..].Trim());
            }
            foreach (var (_, key, value) in r.Edits) SetupWriter.Set(target.Model, key, value);
            var built = target.Build();
            var shell = r.Shell is null ? (OperatingSystem.IsWindows() ? Setup.Shell.PowerShell : Setup.Shell.Sh) : ShellOf(r.Shell);
            lossy = false;
            text = shell is { } s ? built.Render(s, out lossy) : built.RenderMarkdown(out _);
        }
        catch (SetupException ex)
        {
            io.Error.WriteLine($"skarp setup: {ex.Message}");
            return 2;
        }
        io.Out.Write(text);
        return lossy && r.Shell == "cmd" ? 2 : 0;
    }

    // ---- menu -------------------------------------------------------------------------------------------------------

    static int RunMenu(Request r, CommandIO io, SettingsLocations locations)
    {
        bool console = io.Keys is null && io.Terminal is null && r.Script is null;
        if (console && (Console.IsInputRedirected || Console.IsOutputRedirected))
        {
            io.Error.WriteLine("skarp setup: the menu needs an interactive terminal (input or output is redirected). " +
                "Use the flag form: skarp setup --level <level> --set <key>=<value> (skarp setup --help); for Claude Code's hooks, skarp install claude; for git hooks, skarp install git-hooks.");
            return 2;
        }
        LevelModel? start = null;
        if (r.Level is { } level)
        {
            try { start = Open(level, io.Cwd, r.Config, locations); }
            catch (SetupException ex) { io.Error.WriteLine($"skarp setup: {ex.Message}"); return 2; }
        }
        var choices = start is not null || r.CommandMenu || r.Screen is not null ? [] : Choices(io.Cwd, r.Config, locations);
        var (_, gitRoot) = SettingsFileReader.FindRepository(io.Cwd);
        bool inRepository = gitRoot is not null || choices is [_, _, { Found: true }, ..];
        var menuStart = new SetupMenuStart(choices, inRepository ? 2 : 1, start, l => Open(l, io.Cwd, r.Config, locations))
        {
            OpenCommand = c => CommandTarget.Open(c, io.Cwd, r.Config, locations), StartWithCommands = r.CommandMenu,
            ScreenRows = Install.MenuScreens.Rows, ScreenPanes = Install.MenuScreens.Panes, StartScreen = Install.MenuScreens.Index(r.Screen),
            OpenScreen = i => Install.MenuScreens.Open(i, io.Cwd, io.Install, locations),
        };
        bool color = FrameWriter.ColorAllowed(locations.Variable);

        if (r.Script is not null) return RunScript(r, io, menuStart, color);
        SetupMenu menu;
        int exit;
        if (console)
        {
            using var session = ConsoleSession.Begin();
            menu = new SetupMenu(menuStart, new MenuConsole(new ConsoleKeySource(), new ConsoleTerminal(), color));
            exit = menu.Run();
        }
        else
        {
            menu = new SetupMenu(menuStart, new MenuConsole(io.Keys!, io.Terminal!, color));
            exit = menu.Run();
        }
        return Finish(menu, exit, io);
    }

    /// <summary>After the menu closed: what it printed, its summary, then the hooks screen's install.</summary>
    static int Finish(SetupMenu menu, int exit, CommandIO io)
    {
        if (menu.PrintText is { } print) io.Out.Write(print);
        if (menu.Summary.Length > 0) io.Out.WriteLine(menu.Summary);
        return menu.InstallArgs is { } install ? InstallCommand.RunFromMenu(install, io) : exit;
    }

    static List<LevelChoice> Choices(string cwd, string? config, SettingsLocations locations)
    {
        var list = new List<LevelChoice>();
        foreach (var level in (ReadOnlySpan<SettingOrigin>)[SettingOrigin.System, SettingOrigin.Personal, SettingOrigin.Repository])
        {
            var (path, _) = FileOf(level, cwd, config, locations);
            list.Add(new LevelChoice(level, path is null ? null : LevelModel.Shorten(path, cwd), path is not null && File.Exists(path)));
        }
        list.Add(new LevelChoice(SettingOrigin.CommandLine, null, false)); // build a command line
        return list;
    }

    /// <summary>
    /// Hidden test seam (not contract): <c>--script &lt;keys file&gt; [--script-size WxH]</c> runs the menu on a fake
    /// terminal (default 80x24) and writes each frame's snapshot, then the raw bytes, to standard output, so golden frames
    /// can be compared between a JIT and a Native AOT build.
    /// </summary>
    static int RunScript(Request r, CommandIO io, SetupMenuStart start, bool color)
    {
        var size = (r.Size ?? "80x24").Split('x');
        var terminal = new FakeTerminal(int.Parse(size[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(size[1], System.Globalization.CultureInfo.InvariantCulture));
        var keys = new ScriptKeySource(File.ReadAllLines(r.Script!), terminal);
        var frames = new StringBuilder();
        int n = 0;
        var menu = new SetupMenu(start, new MenuConsole(keys, terminal, color,
            f => frames.Append("=== frame ").Append(n++).Append(" after ").Append(keys.Last ?? "(start)").Append('\n').Append(f.ToSnapshot()),
            Shell.PowerShell));
        int exit = menu.Run();
        io.Out.Write(frames.ToString());
        io.Out.Write("=== raw\n");
        io.Out.Write(terminal.Output.ToString());
        io.Out.Write("\n=== summary\n" + menu.Summary + "\n");
        if (menu.PrintText is { } print) io.Out.Write("=== print\n" + print);
        if (menu.InstallArgs is { } install) io.Out.Write("=== apply\n" + InstallCommand.Words(install) + "\n");
        return exit;
    }
}
