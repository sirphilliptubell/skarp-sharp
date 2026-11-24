using System.Text.Json.Nodes;
using SkarpSharp.Configuration;
using SkarpSharp.Findings;

namespace SkarpSharp.Setup;

/// <summary>A value of a key together with the level it comes from.</summary>
public sealed class LevelValue(JsonNode? value, SettingOrigin level, string? path)
{
    public JsonNode? Value { get; } = value;
    public SettingOrigin Level { get; } = level;
    public string? Path { get; } = path;
}

/// <summary>A settings level's raw tree, as read for <c>skarp setup</c>.</summary>
public sealed class LevelTree(SettingOrigin level, string path, JsonObject node)
{
    public SettingOrigin Level { get; } = level;
    public string Path { get; } = path;
    public JsonObject Node { get; } = node;
}

/// <summary>
/// What the editor knows about the target level: its raw file (never the merged tree), the
/// value every key inherits from the levels below with its level, the values file levels above set (environment and
/// command line are not shown) and the effective value after the edit. Levels resolve as for <c>analyze</c>, not as the
/// gate: a repository's <c>gate.enabled</c> does not hide the machine files here.
/// </summary>
public sealed class LevelModel
{
    public required SettingOrigin Target { get; init; }
    public required SettingsDocument Document { get; init; }
    /// <summary>The applied file levels below the target, lowest first.</summary>
    public IReadOnlyList<LevelTree> Below { get; init; } = [];
    /// <summary>The applied file levels above the target, lowest first.</summary>
    public IReadOnlyList<LevelTree> Above { get; init; } = [];
    /// <summary>A new repository file: where it will be created.</summary>
    public bool NewRepositoryFile { get; init; }
    /// <summary>The folder relative paths and validation start from.</summary>
    public required string Directory { get; init; }
    public required SettingsLocations Locations { get; init; }
    /// <summary>The <c>--config</c> file, when given.</summary>
    public string? ConfigFlag { get; init; }
    /// <summary>The command a command-line level is built for (<see cref="SettingOrigin.CommandLine"/> target).</summary>
    public string? Command { get; init; }

    /// <summary>The target is the in-memory command-line level of <c>skarp setup command</c>.</summary>
    public bool IsCommandLine => Target == SettingOrigin.CommandLine;

    JsonObject? _inherited;
    List<OriginEvent> _inheritedEvents = [];

    public string Path => Document.Path;

    /// <summary>The path as the menu shows it: relative under the current folder, <c>~</c> under the home folder.</summary>
    public string ShownPath => Shorten(Path, Directory);

    /// <summary><paramref name="path"/> relative to <paramref name="cwd"/> when below it, with <c>~</c> for the user's
    /// home folder when below that, else in full.</summary>
    public static string Shorten(string path, string cwd)
    {
        static bool Under(string p, string folder, out string rest)
        {
            var f = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder)) + System.IO.Path.DirectorySeparatorChar;
            bool under = p.StartsWith(f, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
            rest = under ? p[f.Length..] : "";
            return under;
        }
        var full = System.IO.Path.GetFullPath(path);
        if (Under(full, cwd, out var below)) return below;
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (home.Length > 0 && Under(full, home, out var rest)) return "~" + System.IO.Path.DirectorySeparatorChar + rest;
        return full;
    }
    public bool IsMachine => Target.IsMachine();

    /// <summary>The levels below merged (what this level inherits).</summary>
    public JsonObject Inherited
    {
        get
        {
            if (_inherited is null)
            {
                _inherited = [];
                _inheritedEvents = [];
                foreach (var t in Below) SettingsLevels.Merge(_inherited, t.Node, "", t.Level, t.Path, _inheritedEvents);
            }
            return _inherited;
        }
    }

    /// <summary>The inherited value of <paramref name="key"/> with its level; null when nothing below sets it (skarp's default).</summary>
    public LevelValue? InheritedValue(string key)
    {
        var value = SettingsDocument.Get(Inherited, key, out bool found);
        if (!found) return null;
        OriginEvent? e = null;
        foreach (var ev in _inheritedEvents)
            if (ev.Key == key || key.StartsWith(ev.Key + ".", StringComparison.Ordinal) || ev.Key.StartsWith(key + ".", StringComparison.Ordinal)) e = ev;
        return new LevelValue(value, e?.Level ?? SettingOrigin.Default, e?.Path);
    }

    /// <summary>The highest file level above the target that sets <paramref name="key"/> (or a parent of it to null), or null.</summary>
    public LevelValue? AboveValue(string key)
    {
        for (int i = Above.Count - 1; i >= 0; i--)
        {
            var value = SettingsDocument.Get(Above[i].Node, key, out bool found);
            if (found) return new LevelValue(value, Above[i].Level, Above[i].Path);
        }
        return null;
    }

    /// <summary>The value set at this level now: found false when the key is absent here.</summary>
    public JsonNode? Here(string key, out bool found) => SettingsDocument.Get(Document.Current, key, out found);

    /// <summary>The tree this level produces after the edit: the levels below, then this one.</summary>
    public JsonObject Stack() => MergeOf(Below, Document.Current, []);

    /// <summary>The levels below, then <paramref name="current"/> as this level (the preview's "before").</summary>
    public JsonObject StackWith(JsonObject current) => MergeOf(Below, current, []);

    /// <summary>Everything that applies after the edit: below, this level, then the file levels above.</summary>
    public JsonObject Everything() => MergeOf(Below, Document.Current, Above);

    /// <summary>Everything that applies with <paramref name="current"/> as this level (the preview's "before").</summary>
    public JsonObject EverythingWith(JsonObject current) => MergeOf(Below, current, Above);

    JsonObject MergeOf(IEnumerable<LevelTree> below, JsonObject current, IEnumerable<LevelTree> above)
    {
        var merged = new JsonObject();
        var events = new List<OriginEvent>();
        foreach (var t in below) SettingsLevels.Merge(merged, t.Node, "", t.Level, t.Path, events);
        SettingsLevels.Merge(merged, current, "", Target, Path, events);
        foreach (var t in above) SettingsLevels.Merge(merged, t.Node, "", t.Level, t.Path, events);
        return merged;
    }

    /// <summary>The leaf keys changed since the editor opened, in the order of the new file, then the removed ones.</summary>
    public IReadOnlyList<string> Changed()
    {
        var before = SettingsDocument.Leaves(Document.Original).ToList();
        var after = SettingsDocument.Leaves(Document.Current).ToList();
        var keys = after.Concat(before.Where(k => !after.Contains(k))).Where(k => k != "version");
        return [.. keys.Where(k =>
        {
            var a = SettingsDocument.Get(Document.Original, k, out bool fa);
            var b = SettingsDocument.Get(Document.Current, k, out bool fb);
            return fa != fb || !JsonNode.DeepEquals(a, b);
        })];
    }

    /// <summary>Reverts <paramref name="key"/> to its value when the editor opened.</summary>
    public void Revert(string key)
    {
        var original = SettingsDocument.Get(Document.Original, key, out bool found);
        if (found) SettingsDocument.Set(Document.Current, key, original?.DeepClone());
        else SettingsDocument.Remove(Document.Current, key);
    }

    /// <summary>The thresholds a tree resolves to (preset, metric, line unit and overrides), or the default's when it
    /// does not resolve (an invalid value is reported by the save, not here).</summary>
    public static ThresholdContext Thresholds(JsonObject tree)
    {
        string preset = "default", lines = "physical";
        string? metric = null;
        var overrides = new List<string>();
        try
        {
            var copy = (JsonObject)tree.DeepClone();
            copy["version"] = SettingsFileReader.CurrentVersion;
            var file = SettingsFileReader.Parse(copy.ToJsonString());
            if (file.Preset is { } p && Presets.Find(p) is not null) preset = p;
            if (file.ComplexMetric is "cognitive" or "cyclomatic") metric = file.ComplexMetric;
            if (file.LongMethodLines is "physical" or "source") lines = file.LongMethodLines;
            foreach (var (text, _) in SettingsFileReader.ThresholdOverrides(file, "settings"))
                if (Resolves(preset, metric, [.. overrides, text], lines)) overrides.Add(text);
        }
        catch (Exception ex) when (ex is SettingsException or System.Text.Json.JsonException or InvalidOperationException)
        {
            overrides.Clear(); // an invalid tree: the save reports it; the editor shows the preset's values
        }
        return new ThresholdContext(preset, metric ?? "cognitive", lines,
            RuleSettings.Resolve(preset, metric, [], lines), RuleSettings.Resolve(preset, metric, overrides, lines));
    }

    static bool Resolves(string preset, string? metric, IReadOnlyList<string> overrides, string lines)
    {
        try { _ = RuleSettings.Resolve(preset, metric, overrides, lines); return true; }
        catch (ArgumentException) { return false; }
    }
}

/// <summary>A tree's thresholds: the preset's values (<see cref="Base"/>) and the values after overrides (<see cref="Active"/>).</summary>
public sealed class ThresholdContext(string preset, string complexMetric, string longMethodLines, RuleSettings @base, RuleSettings active)
{
    public string Preset { get; } = preset;
    public string ComplexMetric { get; } = complexMetric;
    public string LongMethodLines { get; } = longMethodLines;
    public RuleSettings Base { get; } = @base;
    public RuleSettings Active { get; } = active;

    /// <summary>The metric line-based long-method values are measured in.</summary>
    public string LineMetric => LongMethodLines == "source" ? "sourceLines" : "physicalLines";
}
