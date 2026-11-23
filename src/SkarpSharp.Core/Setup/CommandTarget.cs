using System.Text.Json.Nodes;
using SkarpSharp.Configuration;

namespace SkarpSharp.Setup;

/// <summary>
/// The "command line" target of <c>skarp setup</c>: a command, its run options and an
/// in-memory settings level above every file. The levels below resolve as the command would read them from its
/// directory, or as the gate does when the gate run option is on; nothing is ever written.
/// </summary>
public sealed class CommandTarget
{
    /// <summary>The commands offered, in menu order, with one line each.</summary>
    public static readonly IReadOnlyList<(string Name, string Description)> Commands =
    [
        ("analyze", "findings report and metrics: the usual run"),
        ("baseline", "record the current findings in .skarp-baseline.json"),
        ("hotspots", "the activity hotspot table"),
        ("history", "git history only"),
    ];

    readonly string _cwd;
    readonly string? _config;
    readonly SettingsLocations _locations;

    public string Command { get; private set; }
    /// <summary>Run option values by name; a switch is <c>on</c>.</summary>
    public Dictionary<string, string> Options { get; } = new(StringComparer.Ordinal);
    public LevelModel Model { get; private set; }

    CommandTarget(string command, string cwd, string? config, SettingsLocations locations, SettingsDocument document)
    {
        Command = command;
        _cwd = cwd;
        _config = config;
        _locations = locations;
        Model = Open(document);
    }

    /// <summary>Opens the target for <paramref name="command"/> with no changes.</summary>
    /// <exception cref="SetupException">A settings file cannot be read.</exception>
    public static CommandTarget Open(string command, string cwd, string? config, SettingsLocations locations) =>
        new(command, cwd, config, locations, NewDocument(cwd));

    static SettingsDocument NewDocument(string cwd) =>
        new() { Path = System.IO.Path.Combine(System.IO.Path.GetFullPath(cwd), "(command line)"), Original = SettingsDocument.Empty(), Current = SettingsDocument.Empty() };

    /// <summary>The folder the command analyzes (absolute).</summary>
    public string Directory => System.IO.Path.GetFullPath(System.IO.Path.Combine(_cwd, Options.GetValueOrDefault("directory") ?? "."));

    LevelModel Open(SettingsDocument document)
    {
        bool gate = Command == "analyze" && Options.GetValueOrDefault("gate") == "on";
        LayeredSettings layered;
        try { layered = SettingsLevels.Resolve(new LevelRequest(Directory, _config, false, false, gate ? true : null, false, _locations)); }
        catch (LevelException ex) { throw new SetupException(ex.Message); }
        var below = new List<LevelTree>();
        foreach (var l in layered.Levels)
            if (l.Applied && l.Path is not null && l.Level != SettingOrigin.CommandLine)
                below.Add(new LevelTree(l.Level, l.Path, SettingsDocument.Open(l.Path).Original));
        return new LevelModel
        {
            Target = SettingOrigin.CommandLine, Document = document, Directory = _cwd, Locations = _locations, ConfigFlag = _config,
            Command = Command, Below = below,
        };
    }

    /// <summary>Sets a run option (null or empty removes it); the directory and the gate re-resolve the levels below.</summary>
    /// <exception cref="SetupException">An unknown option or value, or a directory that does not exist.</exception>
    public void SetOption(string name, string? value)
    {
        var option = RunOptions.Find(name) ?? throw new SetupException($"unknown run option '{name}' ({string.Join(", ", RunOptions.All.Select(o => o.Name))})");
        if ((option.Commands & SettingsRegistry.Command(Command)) == 0) throw new SetupException($"{name}: not a run option of skarp {Command}");
        if (string.IsNullOrEmpty(value)) Options.Remove(name);
        else
        {
            value = option.Kind switch
            {
                RunOptionKind.Switch => value is "on" or "true" ? "on" : value is "off" or "false" ? "off"
                    : throw new SetupException($"{name}: '{value}' is not on or off"),
                RunOptionKind.Choice when !option.AllowedFor(Command).Contains(value) => throw new SetupException($"{name}: '{value}' is not {string.Join(", ", option.AllowedFor(Command))}"),
                _ => value,
            };
            if (name == "directory" && !System.IO.Directory.Exists(System.IO.Path.Combine(_cwd, value))) throw new SetupException($"directory: no folder {value}");
            if (value == "off") Options.Remove(name); else Options[name] = value;
        }
        if (name is "directory" or "gate") Model = Open(Model.Document);
    }

    /// <summary>Removes every run option (the levels below re-resolve for the default directory).</summary>
    public void ClearOptions()
    {
        Options.Clear();
        Model = Open(Model.Document);
    }

    /// <summary>Switches to another command: the run options and changed keys it does not use are dropped.</summary>
    public void SetCommand(string command)
    {
        if (command == Command) return;
        Command = command;
        var cmd = SettingsRegistry.Command(command);
        foreach (var name in Options.Keys.ToList())
            if ((RunOptions.Find(name)!.Commands & cmd) == 0) Options.Remove(name);
        foreach (var key in SettingsDocument.Leaves(Model.Document.Current).ToList())
            if (key != "version" && ((SettingsRegistry.Row(key) ?? SettingsRegistry.Governing(key))?.Commands & cmd) == 0)
                SettingsDocument.Remove(Model.Document.Current, key);
        Model = Open(Model.Document);
    }

    /// <summary>Builds and checks the command line.</summary>
    /// <exception cref="SetupException">The command does not parse, or an internal error.</exception>
    public CommandBuilder Build() => CommandBuilder.Build(new CommandRequest
    {
        Command = Command, Options = Options, Changes = (JsonObject)Model.Document.Current.DeepClone(), Cwd = _cwd, Locations = _locations, Config = _config,
    });
}
