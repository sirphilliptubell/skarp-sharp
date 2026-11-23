using System.Text.Json.Nodes;

namespace SkarpSharp.Configuration;

/// <summary>The <c>--setting</c> values of one command line as one settings level: the tree (dotted keys set in
/// order, later wins) and the flag text of each key (<c>skarp config --origins</c> shows it).</summary>
internal sealed class CommandLineLevel(JsonObject tree, IReadOnlyList<(string Key, string Flag)> flags)
{
    public JsonObject Tree { get; } = tree;
    public IReadOnlyList<(string Key, string Flag)> Flags { get; } = flags;

    /// <summary>The flag text of the last <c>--setting</c> that set <paramref name="key"/>, a parent of it or an entry below it.</summary>
    public string? FlagOf(string key)
    {
        for (int i = Flags.Count - 1; i >= 0; i--)
        {
            var k = Flags[i].Key;
            if (k == key || key.StartsWith(k + ".", StringComparison.Ordinal) || k.StartsWith(key + ".", StringComparison.Ordinal)) return Flags[i].Flag;
        }
        return null;
    }

    /// <summary>The <c>rules</c> and <c>thresholds</c> it sets (applied after the per-path overrides), or null.</summary>
    public JsonObject? RulesTree()
    {
        JsonObject? tree = null;
        foreach (var k in (ReadOnlySpan<string>)["rules", "thresholds"])
            if (Tree.TryGetPropertyValue(k, out var v)) (tree ??= [])[k] = v?.DeepClone();
        return tree;
    }
}

/// <summary>
/// <c>--setting &lt;key&gt;=&lt;value&gt;</c>. Each value is split at the first
/// <c>=</c> and parsed with the <c>skarp setup --set</c> grammar (<see cref="SettingText"/>); together they form one
/// settings level above the files and below the dedicated flags. Errors name the flag and the key.
/// </summary>
internal static class CommandLineSettings
{
    /// <summary>Builds the level of <paramref name="values"/> for <paramref name="command"/>; null with an error message
    /// when a value is rejected (a key that describes a file, an unknown key, a key the command does not use, a value out
    /// of range, a mode without its list).</summary>
    public static CommandLineLevel? Build(string command, IReadOnlyList<string> values, string directory, out string? error)
    {
        error = null;
        var tree = new JsonObject();
        var flags = new List<(string, string)>();
        var cmd = SettingsRegistry.Command(command);
        foreach (var raw in values)
        {
            int eq = raw.IndexOf('=');
            if (eq <= 0) { error = $"--setting {raw}: expected <key>=<value>"; return null; }
            var key = raw[..eq].Trim();
            var text = raw[(eq + 1)..].Trim();
            if (KeyError(key, cmd, command, directory) is { } why) { error = $"--setting {key}: {why}"; return null; }
            JsonNode? value;
            try { value = SettingText.Parse(key, text); }
            catch (SettingTextException ex) { error = $"--setting {ex.Message}"; return null; }
            if (key == "semantic.solution" && value is JsonValue v && v.TryGetValue<string>(out var sln) && sln.Length > 0)
                value = JsonValue.Create(Path.GetFullPath(sln)); // relative to the current folder, as --solution
            SettingText.SetPath(tree, key, value);
            flags.Add((key, $"--setting {key}={text}"));
        }
        error = ModeError(tree, "") ?? Validate(tree, directory);
        return error is null ? new CommandLineLevel(tree, flags) : null;
    }

    internal static string? KeyError(string key, SettingCommands cmd, string command, string directory)
    {
        if (!SettingText.WellFormed(key)) return "not a settings key";
        if (SettingText.DescribesFile(key))
            return key == "useInRepositories" ? "describes a file; set it in the system or personal settings file"
                : $"describes a file; set it in {SettingsFileReader.Find(directory) ?? SettingsFileReader.FileName}";
        var row = SettingsRegistry.Row(key);
        if (row is null && key.StartsWith("rules.", StringComparison.Ordinal))
            return $"unknown rule (known: {string.Join(", ", SettingsRegistry.Rules.Select(r => r.Id))})";
        if (row is null && SettingsRegistry.Governing(key) is not { Key: "rules" or "thresholds" or "score.weights" })
            return "unknown key (skarp config lists the keys)";
        var governing = row ?? SettingsRegistry.Governing(key)!;
        return (governing.Commands & cmd) == 0 ? $"not used by skarp {command}" : null;
    }

    /// <summary>A mode key needs its list in the same command line (the rule of the files).</summary>
    static string? ModeError(JsonObject node, string prefix)
    {
        foreach (var (name, value) in node)
        {
            var key = prefix + name;
            if (SettingsRegistry.ListOfMode(key) is { } list && !node.ContainsKey(list.Key[(list.Key.LastIndexOf('.') + 1)..]))
                return $"--setting {key} needs --setting {list.Key}=<list> in the same command line; to empty an inherited list, write --setting {list.Key}=[]";
            if (value is JsonObject child && ModeError(child, key + ".") is { } e) return e;
        }
        return null;
    }

    /// <summary>The level on its own through the settings reader and the value checks of the files.</summary>
    static string? Validate(JsonObject tree, string directory)
    {
        var copy = (JsonObject)(SettingsLevels.HasNull(tree) ? SettingsLevels.WithoutNulls(tree) : tree.DeepClone());
        copy["version"] = SettingsFileReader.CurrentVersion;
        try
        {
            var file = SettingsFileReader.Parse(copy.ToJsonString(), "--setting");
            CommandLine.Validate(file, "--setting", directory);
            return null;
        }
        catch (SettingsException ex) { return ex.Message; }
        catch (ArgumentException ex) { return $"--setting: {ex.Message}"; }
    }
}
