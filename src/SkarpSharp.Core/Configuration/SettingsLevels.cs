using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SkarpSharp.Configuration;

/// <summary>
/// Where an effective setting came from, lowest first. Each level overrides the ones before it.
/// <see cref="Plugin"/> is a reserved, always empty slot; <see cref="Environment"/> is the repository level when its file
/// came from <c>SKARP_CONFIG</c>.
/// </summary>
public enum SettingOrigin { Default, Plugin, System, Personal, Repository, Environment, CommandLine }

/// <summary>One settings level of a run: its file, whether it was found and applied, and why it was skipped.</summary>
/// <param name="Path">The file (absolute), or null for the defaults, the plugin slot and the command line.</param>
/// <param name="Skipped">Why an existing file was not applied (<c>--gate</c>, <c>useInRepositories: false</c>,
/// <c>--no-user-config</c>, <c>SKARP_NO_USER_CONFIG</c>, <c>--no-config</c>), or null.</param>
public sealed record SettingsSource(SettingOrigin Level, string? Path, bool Found, bool Applied, string? Skipped = null);

/// <summary>One value a level set for a key, in the order the levels apply (an origin chain).</summary>
/// <param name="Value">The value as text; null when the level reset the key to skarp's default (JSON <c>null</c>).</param>
/// <param name="Mode">Lists: <c>replace</c> or <c>append</c>.</param>
public sealed record OriginEvent(string Key, SettingOrigin Level, string? Path, string? Value, string? Mode = null);

public static class SettingOrigins
{
    /// <summary><c>default</c>, <c>plugin</c>, <c>system</c>, <c>personal</c>, <c>repository</c>, <c>environment</c> or <c>command line</c>.</summary>
    public static string Name(this SettingOrigin o) => o == SettingOrigin.CommandLine ? "command line" : o.ToString().ToLowerInvariant();

    /// <summary>The system and personal levels: machine state, skipped by the gate.</summary>
    public static bool IsMachine(this SettingOrigin o) => o is SettingOrigin.System or SettingOrigin.Personal;
}

/// <summary>
/// The machine-wide settings files and the environment. Tests replace <see cref="Default"/> with
/// <see cref="None"/> so they never read the developer's real files.
/// </summary>
public sealed class SettingsLocations
{
    /// <summary>The system file; null: none.</summary>
    public string? SystemFile { get; init; }
    /// <summary>The personal file; null: none.</summary>
    public string? PersonalFile { get; init; }
    /// <summary>Reads an environment variable (<c>SKARP_CONFIG</c>, <c>SKARP_NO_USER_CONFIG</c>).</summary>
    public Func<string, string?> Variable { get; init; } = System.Environment.GetEnvironmentVariable;

    /// <summary>This machine's files: <c>~/.skarp/settings.json</c> (<c>SKARP_HOME</c> replaces <c>~/.skarp</c>) and the system file.</summary>
    public static SettingsLocations Machine() => new()
    {
        SystemFile = DefaultSystemFile(), PersonalFile = Path.Combine(Home(), "settings.json"),
    };

    /// <summary>skarp's home folder: <c>SKARP_HOME</c>, else <c>~/.skarp</c> (also the tracking files' home).</summary>
    public static string Home() =>
        System.Environment.GetEnvironmentVariable("SKARP_HOME") is { Length: > 0 } h ? h
            : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".skarp");

    /// <summary>No system or personal file and no environment variables.</summary>
    public static SettingsLocations None => new() { Variable = _ => null };

    /// <summary>The locations <see cref="CommandLine.Parse(IReadOnlyList{string})"/> uses; tests set <see cref="None"/>.</summary>
    public static SettingsLocations Default { get; set; } = Machine();

    /// <summary><c>%ProgramData%\skarp\settings.json</c>, <c>/Library/Application Support/skarp/settings.json</c> (macOS) or <c>/etc/skarp/settings.json</c>.</summary>
    public static string DefaultSystemFile() =>
        OperatingSystem.IsWindows() ? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData), "skarp", "settings.json")
        : OperatingSystem.IsMacOS() ? "/Library/Application Support/skarp/settings.json"
        : "/etc/skarp/settings.json";

    /// <summary>A file whose content is replaced by <see cref="OverlayText"/> (the
    /// candidate being validated before a save), so the levels resolve exactly as they would after writing it.</summary>
    internal string? OverlayPath { get; init; }
    internal string? OverlayText { get; init; }

    /// <summary>These locations with <paramref name="path"/> read as <paramref name="text"/>.</summary>
    internal SettingsLocations WithOverlay(string path, string text) => new()
    {
        SystemFile = SystemFile, PersonalFile = PersonalFile, Variable = Variable, OverlayPath = Path.GetFullPath(path), OverlayText = text,
    };

    bool IsOverlay(string path) => OverlayPath is not null
        && string.Equals(Path.GetFullPath(path), OverlayPath, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    internal bool Exists(string path) => IsOverlay(path) || File.Exists(path);

    internal string ReadText(string path) => IsOverlay(path) ? OverlayText! : File.ReadAllText(path);

    public const string ConfigVariable = "SKARP_CONFIG";
    public const string NoUserConfigVariable = "SKARP_NO_USER_CONFIG";
}

/// <summary>What the level resolver needs from the command line.</summary>
/// <param name="CommandLine">The <c>--setting</c> level, merged above the files; null without one.</param>
internal sealed record LevelRequest(string Directory, string? ConfigFlag, bool NoConfig, bool NoUserConfig, bool? GateFlag,
    bool GateUserConfig, SettingsLocations Locations, CommandLineLevel? CommandLine = null);

/// <summary>The resolved levels: their status, the merged file and the origin events.</summary>
internal sealed class LayeredSettings
{
    public List<SettingsSource> Levels { get; } = [];
    /// <summary>The merged settings, or null when no file applied.</summary>
    public SettingsFile? Merged { get; set; }
    /// <summary>The file the merged settings' paths are relative to and errors name: the repository file, else the highest applied.</summary>
    public string? MergedPath { get; set; }
    public string? RepositoryFile { get; set; }
    public List<OriginEvent> Events { get; } = [];
    /// <summary>The repository file's <c>overrides</c>, in file order; empty when none.</summary>
    public List<RuleOverride> Overrides { get; set; } = [];
    /// <summary>The merged <c>rules</c> and <c>thresholds</c> before any override (the tree the overrides patch).</summary>
    public JsonObject? RulesTree { get; set; }
    /// <summary>The <c>rules</c> and <c>thresholds</c> of the <c>--setting</c> level (applied after the overrides), or null.</summary>
    public JsonObject? CommandLineRules { get; set; }
}

/// <summary>
/// Finds the settings levels of a run (system, personal, repository), applies the skip rules (the
/// gate, <c>useInRepositories</c>, <c>--no-user-config</c>), checks level-specific keys and merges the files, lowest
/// first, per the registry: scalars replace, objects merge by key, lists follow their <c>&lt;name&gt;Mode</c>; a JSON
/// <c>null</c> resets a key to skarp's default. Errors name the level and the file.
/// </summary>
internal static class SettingsLevels
{
    static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = false };
    static readonly JsonDocumentOptions DocOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>A file of one level, read and checked.</summary>
    sealed record LevelFile(SettingOrigin Level, string Path, JsonObject Node, SettingsFile File, List<RuleOverride>? Overrides = null);

    /// <summary>The words an error starts with: <c>settings file</c> (repository), <c>personal settings file</c>, ...</summary>
    internal static string Label(SettingOrigin level) => level switch
    {
        SettingOrigin.System => "system settings file",
        SettingOrigin.Personal => "personal settings file",
        SettingOrigin.Environment => "settings file (SKARP_CONFIG)",
        _ => "settings file",
    };

    /// <exception cref="LevelException">A file is missing, unreadable or invalid (the message names the level and path).</exception>
    public static LayeredSettings Resolve(LevelRequest r)
    {
        var result = new LayeredSettings();
        var env = r.Locations.Variable;
        result.Levels.Add(new(SettingOrigin.Default, null, true, true));
        result.Levels.Add(new(SettingOrigin.Plugin, null, false, false));
        string? systemPath = Full(r.Locations.SystemFile), personalPath = Full(r.Locations.PersonalFile);

        // The repository level: --config, else SKARP_CONFIG, else the nearest .skarp.json up to the git work tree root.
        var (found, gitRoot) = SettingsFileReader.FindRepository(r.Directory);
        var envConfig = env(SettingsLocations.ConfigVariable) is { Length: > 0 } ec ? ec : null;
        var repoLevel = r.ConfigFlag is null && envConfig is not null ? SettingOrigin.Environment : SettingOrigin.Repository;
        var repoPath = r.ConfigFlag ?? envConfig ?? found;
        bool inRepository = found is not null || gitRoot is not null || r.ConfigFlag is not null || envConfig is not null;
        var repositoryRoot = found is not null ? System.IO.Path.GetDirectoryName(found)! : gitRoot ?? System.IO.Path.GetFullPath(r.Directory);

        if (r.NoConfig)
        {
            AddSkipped(result, SettingOrigin.System, systemPath, "--no-config");
            AddSkipped(result, SettingOrigin.Personal, personalPath, "--no-config");
            result.Levels.Add(new(repoLevel, repoPath is null ? null : System.IO.Path.GetFullPath(repoPath), repoPath is not null && File.Exists(repoPath), false, "--no-config"));
            result.Levels.Add(new(SettingOrigin.CommandLine, null, true, true));
            return r.CommandLine is null ? result : MergeFiles(result, [], null, r.CommandLine);
        }

        LevelFile? repo = null;
        if (repoPath is not null)
        {
            if ((r.ConfigFlag is not null || envConfig is not null) && !r.Locations.Exists(repoPath))
                throw new LevelException(repoLevel == SettingOrigin.Environment ? $"Settings file not found: {repoPath} ({SettingsLocations.ConfigVariable})" : $"Settings file not found: {repoPath}");
            repo = Load(r.Locations, repoLevel, repoPath, r.Directory, System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(repoPath))!, validate: false);
        }

        // The gate is the repository's: --gate (or gate.enabled in .skarp.json without --no-gate) skips the machine files.
        bool gate = r.GateFlag ?? repo?.File.Gate?.Enabled == true;
        string? skip = gate && !r.GateUserConfig ? "--gate (--gate-user-config includes it)"
            : r.NoUserConfig ? "--no-user-config"
            : IsSet(env(SettingsLocations.NoUserConfigVariable)) ? SettingsLocations.NoUserConfigVariable
            : null;

        var files = new List<LevelFile>();
        foreach (var (level, path) in new[] { (SettingOrigin.System, systemPath), (SettingOrigin.Personal, personalPath) })
        {
            if (path is null) continue;
            if (!r.Locations.Exists(path)) { result.Levels.Add(new(level, path, false, false)); continue; }
            if (skip is not null) { result.Levels.Add(new(level, path, true, false, skip)); continue; }
            var file = Load(r.Locations, level, path, r.Directory, repositoryRoot, validate: true);
            if (inRepository && file.File.UseInRepositories == false)
            {
                result.Levels.Add(new(level, path, true, false, "useInRepositories: false"));
                continue;
            }
            result.Levels.Add(new(level, path, true, true));
            files.Add(file);
        }
        if (repoPath is not null) result.Levels.Add(new(repoLevel, System.IO.Path.GetFullPath(repoPath), true, true));
        else result.Levels.Add(new(SettingOrigin.Repository, null, false, false));
        result.Levels.Add(new(SettingOrigin.CommandLine, null, true, true));
        if (repo is not null) files.Add(repo);
        if (files.Count == 0 && r.CommandLine is null) return result;

        return MergeFiles(result, files, repo, r.CommandLine);
    }

    /// <summary>Merges the applied files, lowest first, into <paramref name="result"/>; keeps the repository
    /// file's overrides and the merged <c>rules</c> and <c>thresholds</c> they patch.</summary>
    static LayeredSettings MergeFiles(LayeredSettings result, List<LevelFile> files, LevelFile? repo, CommandLineLevel? commandLine)
    {
        var merged = new JsonObject();
        foreach (var f in files) Merge(merged, f.Node, "", f.Level, f.Path, result.Events);
        if (repo?.Overrides is { Count: > 0 } overrides)
        {
            result.Overrides = overrides;
            var tree = new JsonObject();
            foreach (var k in (ReadOnlySpan<string>)["rules", "thresholds"]) if (merged[k] is { } v) tree[k] = v.DeepClone();
            result.RulesTree = tree;
        }
        result.RepositoryFile = repo?.Path;
        // With only --setting values, the current folder anchors their paths.
        result.MergedPath = repo?.Path ?? (files.Count > 0 ? files[^1].Path : System.IO.Path.Combine(System.Environment.CurrentDirectory, "--setting"));
        var label = files.Count > 0 ? Label(repo?.Level ?? files[^1].Level) : "--setting";
        if (commandLine is not null) MergeCommandLine(result, merged, commandLine);
        try { result.Merged = SettingsFileReader.Parse(merged.ToJsonString(), result.MergedPath); }
        catch (SettingsException ex) { throw new LevelException($"skarp: {label} {ex.Message}"); }
        return result;
    }

    /// <summary>
    /// Merges the <c>--setting</c> level above the files. Its origin events carry the flag text in place of a
    /// file; its <c>coverage</c> globs, relative to the current folder, are rebased onto the folder the merged globs are
    /// read from.
    /// </summary>
    static void MergeCommandLine(LayeredSettings result, JsonObject merged, CommandLineLevel commandLine)
    {
        var tree = (JsonObject)commandLine.Tree.DeepClone();
        if (tree["coverage"] is JsonArray coverage && coverage.Count > 0)
        {
            var folder = System.IO.Path.GetDirectoryName(result.MergedPath!)!;
            var rel = System.IO.Path.GetRelativePath(folder, System.Environment.CurrentDirectory).Replace('\\', '/');
            if (rel.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(rel))
                throw new LevelException($"skarp: --setting coverage: the globs are relative to the current folder, which lies outside {folder} (where the coverage globs are read from); run from there or use --coverage <file>");
            if (rel != ".")
                tree["coverage"] = new JsonArray([.. coverage.Select(g => (JsonNode)JsonValue.Create(rel + "/" + g!.GetValue<string>()))]);
        }
        int first = result.Events.Count;
        Merge(merged, tree, "", SettingOrigin.CommandLine, null, result.Events);
        for (int i = first; i < result.Events.Count; i++)
            result.Events[i] = result.Events[i] with { Path = commandLine.FlagOf(result.Events[i].Key) };
        result.CommandLineRules = commandLine.RulesTree();
        merged["version"] ??= SettingsFileReader.CurrentVersion;
    }

    static string? Full(string? path) => path is null ? null : System.IO.Path.GetFullPath(path);

    static bool IsSet(string? value) => value is { Length: > 0 } && value != "0" && !value.Equals("false", StringComparison.OrdinalIgnoreCase);

    static void AddSkipped(LayeredSettings result, SettingOrigin level, string? path, string reason)
    {
        if (path is null) return;
        bool exists = File.Exists(path);
        result.Levels.Add(new(level, path, exists, false, exists ? reason : null));
    }

    /// <summary>Reads, parses and checks one level's file; include and exclude are rebased onto the analyzed directory.</summary>
    /// <param name="globBase">The folder the file's globs are relative to (the repository root for machine files).</param>
    /// <param name="validate">Also check the values (presets, rules, thresholds, ...) here, naming this file.</param>
    static LevelFile Load(SettingsLocations locations, SettingOrigin level, string path, string directory, string globBase, bool validate)
    {
        path = System.IO.Path.GetFullPath(path);
        try
        {
            string text;
            try { text = locations.ReadText(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SettingsException($"{path}: cannot read: {ex.Message}");
            }
            JsonObject? node;
            try { node = JsonNode.Parse(text, NodeOptions, DocOptions) as JsonObject; }
            catch (JsonException ex)
            {
                _ = SettingsFileReader.Parse(text, path); // the reader's message (key path and reason)
                throw new SettingsException($"{path}: {ex.Message}");
            }
            if (node is null)
            {
                _ = SettingsFileReader.Parse(text, path);
                throw new SettingsException($"{path}: expected a JSON object");
            }
            if (node["overrides"] is { } ov and not JsonArray) throw new SettingsException($"{path}: overrides: wrong type (expected a list of objects)");
            var file = SettingsFileReader.Parse(HasNull(node) ? WithoutNulls(node).ToJsonString() : text, path);
            CheckKeys(node, "", level, path);
            // Overrides (repository level only; CheckKeys rejects them elsewhere) are read here and kept out of the merge.
            List<RuleOverride>? overrides = null;
            if (node.TryGetPropertyValue("overrides", out var list) && node.Remove("overrides") && list is not null)
                overrides = RuleOverrides.Read(list, path, CommandLine.GlobPrefix(globBase, directory));
            RebaseGlobs(node, path, directory, globBase);
            if (validate) CommandLine.Validate(file, path, directory);
            return new LevelFile(level, path, node, file, overrides);
        }
        catch (SettingsException ex) { throw new LevelException($"skarp: {Label(level)} {ex.Message}"); }
        catch (ArgumentException ex) { throw new LevelException($"skarp: {Label(level)} {path}: {ex.Message}"); } // duplicate keys
    }

    /// <summary>Reads and checks one level's file on its own (<c>skarp setup</c> before a save); the
    /// message names the level, file and key.</summary>
    /// <exception cref="LevelException">The file is invalid.</exception>
    internal static void Check(SettingsLocations locations, SettingOrigin level, string path, string directory) =>
        _ = Load(locations, level, path, directory, directory, validate: true);

    internal static bool HasNull(JsonNode? node) => node switch
    {
        null => true,
        JsonObject o => o.Any(p => HasNull(p.Value)),
        JsonArray a => a.Any(HasNull),
        _ => false,
    };

    internal static JsonNode WithoutNulls(JsonNode node)
    {
        if (node is not JsonObject o) return node.DeepClone();
        var copy = new JsonObject();
        foreach (var (k, v) in o) if (v is not null) copy[k] = WithoutNulls(v);
        return copy;
    }

    /// <summary>Keys a level may not hold, and the mode values.</summary>
    static void CheckKeys(JsonObject node, string prefix, SettingOrigin level, string path)
    {
        var scope = level.IsMachine() ? LevelScope.Machine : LevelScope.Repository;
        foreach (var (name, value) in node)
        {
            var key = prefix + name;
            if (SettingsRegistry.Find(key) is { } row)
            {
                if ((row.Levels & scope) == 0 && value is not null)
                    throw new SettingsException($"{path}: {key}: {NotHere(row, level)}");
                if (SettingsRegistry.ListOfMode(key) is not null && value?.GetValue<string>() is { } mode)
                {
                    if (mode is not ("replace" or "append"))
                        throw new SettingsException($"{path}: {key}: '{mode}' is not replace or append");
                    var listName = name[..^"Mode".Length];
                    if (!node.ContainsKey(listName))
                        throw new SettingsException($"{path}: {key} needs {prefix}{listName} in the same file; to empty an inherited list, write \"{listName}\": []");
                }
            }
            if (value is JsonObject child && SettingsRegistry.Governing(key)?.Key == key) CheckKeys(child, key + ".", level, path);
        }
    }

    /// <summary>Why <paramref name="key"/> may not be set at <paramref name="level"/>, as the loader says it, or null.</summary>
    internal static string? NotAllowed(string key, SettingOrigin level)
    {
        var scope = level.IsMachine() ? LevelScope.Machine : LevelScope.Repository;
        return SettingsRegistry.Governing(key) is { } row && (row.Levels & scope) == 0 ? NotHere(row, level) : null;
    }

    static string NotHere(SettingsKey row, SettingOrigin level) =>
        row.Key == "useInRepositories" ? "allowed only in the system and personal settings files"
        : row.Key == "gate.enabled" ? "the gate is the repository's: set it in .skarp.json or use --gate"
        : row.IsPath ? $"a path, allowed only in a repository's .skarp.json (not in the {level.Name()} settings file)"
        : $"allowed only in a repository's .skarp.json (not in the {level.Name()} settings file)";

    /// <summary>Globs are relative to the file's folder (machine files: the repository root); rebased onto the analyzed directory.</summary>
    static void RebaseGlobs(JsonObject node, string path, string directory, string globBase)
    {
        var prefix = CommandLine.GlobPrefix(globBase, directory);
        foreach (var key in (ReadOnlySpan<string>)["include", "exclude"])
        {
            if (node[key] is not JsonArray list) continue;
            var globs = list.Select(g => g!.GetValue<string>()).ToList();
            if (globs.Any(string.IsNullOrEmpty)) throw new SettingsException($"{path}: {key}: empty glob");
            if (prefix is null or "") continue;
            node[key] = new JsonArray([.. globs.Select(g => SettingsFileReader.RebaseGlob(g, prefix)).OfType<string>().Select(g => (JsonNode)JsonValue.Create(g))]);
        }
    }

    /// <summary>
    /// Merges one level's file into <paramref name="target"/>: objects key by key, lists by their mode, everything else
    /// replaced; <c>null</c> removes the key (skarp's default). Records one origin event per value set.
    /// </summary>
    internal static void Merge(JsonObject target, JsonObject source, string prefix, SettingOrigin level, string? path, List<OriginEvent> events)
    {
        foreach (var (name, value) in source)
        {
            var key = prefix + name;
            if (SettingsRegistry.ListOfMode(key) is not null) continue; // read with its list
            if (SettingsRegistry.Find(key)?.Merge == MergeKind.Overrides) continue; // one level only, read by RuleOverrides
            if (key == "version") { target[name] = value?.DeepClone(); continue; }
            if (value is null)
            {
                target.Remove(name);
                events.Add(new(key, level, path, null));
                continue;
            }
            switch (value)
            {
                case JsonObject obj:
                    if (target[name] is not JsonObject inner) target[name] = inner = new JsonObject();
                    Merge(inner, obj, key + ".", level, path, events);
                    break;
                case JsonArray list:
                    var mode = source[name + "Mode"]?.GetValue<string>() ?? "replace";
                    if (mode == "append" && target[name] is JsonArray inherited)
                        foreach (var item in list) inherited.Add(item?.DeepClone());
                    else target[name] = list.DeepClone();
                    events.Add(new(key, level, path, ListText(list), mode));
                    break;
                default:
                    target[name] = value.DeepClone();
                    events.Add(new(key, level, path, ScalarText(value)));
                    break;
            }
        }
        // A mode without its list is rejected by CheckKeys.
    }

    static string ListText(JsonArray list) => string.Join(", ", list.Select(i => i is null ? "null" : ScalarText(i)));

    static string ScalarText(JsonNode value) => value.GetValueKind() switch
    {
        JsonValueKind.String => value.GetValue<string>(),
        JsonValueKind.Number => value.ToJsonString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.ToJsonString(),
    };
}

/// <summary>A settings level that cannot be used; the message is complete (level, path, key, reason).</summary>
internal sealed class LevelException(string message) : Exception(message);
