using System.Text.Json.Serialization;

namespace SkarpSharp.Configuration;

/// <summary>
/// <c>skarp config --format json</c>, for <c>skarp setup</c>: the settings levels and every setting
/// with its value, origin level and file, and the values it overrode. Experimental.
/// </summary>
public sealed class ConfigReport
{
    public const int CurrentVersion = 1;
    public int SchemaVersion { get; set; } = CurrentVersion;
    public string ToolVersion { get; set; } = "";
    /// <summary>The analyzed directory (absolute).</summary>
    public string Directory { get; set; } = "";
    public string BaselineFile { get; set; } = "";
    /// <summary>Every level, lowest first: <c>default</c>, <c>plugin</c> (reserved), <c>system</c>, <c>personal</c>,
    /// <c>repository</c> or <c>environment</c>, <c>command line</c>.</summary>
    public List<ConfigLevel> Levels { get; set; } = [];
    public List<ConfigSetting> Settings { get; set; } = [];
    /// <summary>Threshold overrides in <c>--threshold</c> grammar with their origin, in the order applied.</summary>
    public List<ConfigSetting> Thresholds { get; set; } = [];
    /// <summary>The repository file's <c>overrides</c> entries as written, with the files each matches; absent when none.</summary>
    public List<Model.PathOverrideInfo>? Overrides { get; set; }
    /// <summary><c>--file</c>: one file's effective rules and thresholds with their origins; absent without the flag.</summary>
    public ConfigFile? File { get; set; }

    public static ConfigReport Of(SkarpOptions o, string toolVersion) => Of(o, toolVersion, null);

    /// <param name="overrides">The overrides part (entries, files matched, <c>--file</c>), or null.</param>
    public static ConfigReport Of(SkarpOptions o, string toolVersion, ConfigOverrides? overrides)
    {
        var report = new ConfigReport
        {
            ToolVersion = toolVersion, Directory = Path.GetFullPath(o.Directory), BaselineFile = o.BaselineFile(),
            Levels = [.. o.Levels.Select(l => new ConfigLevel { Level = l.Level.Name(), Path = l.Path, Found = l.Found, Applied = l.Applied, Skipped = l.Skipped })],
        };
        foreach (var s in o.Settings())
        {
            var row = SettingsRegistry.Find(s.Key);
            report.Settings.Add(new ConfigSetting
            {
                Key = s.Key, Value = s.Value, Origin = s.Origin, Path = s.Path, Default = row?.Default,
                AffectsFindings = SettingsRegistry.Governing(s.Key)?.AffectsFindings ?? false,
                Overrides = s.Overridden is { Count: > 0 } chain ? [.. chain.Select(ConfigOrigin.Of)] : null,
            });
        }
        foreach (var t in o.Thresholds)
        {
            int eq = t.IndexOf('=');
            var key = "thresholds." + (eq > 0 ? t[..eq].Trim() : t);
            var chain = o.OriginChain(key);
            report.Thresholds.Add(new ConfigSetting
            {
                Key = key, Value = eq > 0 ? t[(eq + 1)..].Trim() : "", Origin = o.OriginText(key), Path = chain.Count > 0 ? chain[^1].Path : null,
                AffectsFindings = true, Overrides = chain.Count > 1 ? [.. chain.Take(chain.Count - 1).Select(ConfigOrigin.Of)] : null,
            });
        }
        if (overrides is null) return report;
        if (overrides.Entries.Count > 0)
            report.Overrides = [.. overrides.Entries.Select(r => new Model.PathOverrideInfo
            {
                Index = r.Index, Files = r.Files is null ? null : [.. r.Files], ExcludeFiles = r.ExcludeFiles is null ? null : [.. r.ExcludeFiles],
                Tests = r.Tests ? true : null, Rules = RuleOverrides.Element(r.Rules), Thresholds = RuleOverrides.Element(r.Thresholds),
                Matched = overrides.Matched?[r.Index] ?? 0,
            })];
        if (overrides.File is { } f)
            report.File = new ConfigFile
            {
                Path = f.Path, TestCode = f.TestCode, Excluded = f.Excluded, Overrides = [.. f.Matched],
                Rules = [.. ConfigOverrideRows.Rows(o, f).Select(r => new ConfigFileRule { Rule = r.Rule, Part = r.Part, Value = r.Value, Origin = r.Origin })],
            };
        return report;
    }
}

/// <summary><c>skarp config --file</c> in JSON.</summary>
public sealed class ConfigFile
{
    /// <summary>Relative to the analyzed directory.</summary>
    public string Path { get; set; } = "";
    /// <summary>Skarp's test detection marks the file as test code.</summary>
    public bool TestCode { get; set; }
    /// <summary>Why a full run leaves the file out; absent when it is analyzed.</summary>
    public string? Excluded { get; set; }
    /// <summary>The <c>overrides</c> entries that match the file (0-based, file order).</summary>
    public List<int> Overrides { get; set; } = [];
    public List<ConfigFileRule> Rules { get; set; } = [];
}

/// <summary>One rule's switch (<c>on/off</c>), level (<c>notice</c>, <c>warning</c>) or parameter for one file.</summary>
public sealed class ConfigFileRule
{
    public string Rule { get; set; } = "";
    public string Part { get; set; } = "";
    public string Value { get; set; } = "";
    /// <summary>The level or entry that set it: <c>repository</c>, <c>command line</c>, <c>override 2</c>, <c>preset default</c>, ...</summary>
    public string Origin { get; set; } = "";
}

public sealed class ConfigLevel
{
    public string Level { get; set; } = "";
    public string? Path { get; set; }
    public bool Found { get; set; }
    public bool Applied { get; set; }
    /// <summary>Why a found file did not apply (<c>--gate ...</c>, <c>useInRepositories: false</c>, <c>--no-user-config</c>, ...).</summary>
    public string? Skipped { get; set; }
}

public sealed class ConfigSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    /// <summary>The level that set the value (<c>default</c> when none did).</summary>
    public string Origin { get; set; } = "";
    /// <summary>The origin level's file; absent for defaults and the command line.</summary>
    public string? Path { get; set; }
    /// <summary>skarp's built-in default (from the settings registry); absent for entries of <c>rules</c> and the like.</summary>
    public string? Default { get; set; }
    public bool AffectsFindings { get; set; }
    /// <summary>The values lower levels set, lowest first; absent when no other level set the key.</summary>
    public List<ConfigOrigin>? Overrides { get; set; }
}

public sealed class ConfigOrigin
{
    public string Level { get; set; } = "";
    public string? Path { get; set; }
    /// <summary>The value set; absent when the level reset the key to the default (JSON <c>null</c>).</summary>
    public string? Value { get; set; }
    /// <summary>Lists: <c>replace</c> or <c>append</c>.</summary>
    public string? Mode { get; set; }

    internal static ConfigOrigin Of(OriginEvent e) => new() { Level = e.Level.Name(), Path = e.Path, Value = e.Value, Mode = e.Mode };
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ConfigReport))]
public sealed partial class ConfigJsonContext : JsonSerializerContext;
