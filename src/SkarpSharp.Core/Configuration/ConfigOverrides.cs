using System.Text.Json.Nodes;
using SkarpSharp.Findings;

namespace SkarpSharp.Configuration;

/// <summary>
/// The overrides part of <c>skarp config</c>: each entry with the number of
/// files it matches among the files a full run analyzes, and with <c>--file</c> one file's effective rules and
/// thresholds with their origins.
/// </summary>
public sealed class ConfigOverrides
{
    public required IReadOnlyList<RuleOverride> Entries { get; init; }
    /// <summary>Per entry, the files it matches; null when the run was not counted.</summary>
    public int[]? Matched { get; init; }
    /// <summary>The files a full run analyzes (the denominator of <see cref="Matched"/>).</summary>
    public int AnalyzedFiles { get; init; }
    /// <summary>The files any entry matches.</summary>
    public int MatchedFiles { get; init; }
    /// <summary><c>--file</c>: one file's view, or null.</summary>
    public ConfigFileView? File { get; init; }
    /// <summary>Rules the entries switch on that the repository settings have off and that need the unused-code walk.</summary>
    public IReadOnlyList<(RuleOverride Entry, string Rule)> WalkRules { get; init; } = [];
}

/// <summary><c>skarp config --file &lt;path&gt;</c>: the file, its test status and the settings it is judged by.</summary>
public sealed class ConfigFileView
{
    /// <summary>Relative to the analyzed directory, '/' separators.</summary>
    public required string Path { get; init; }
    public bool TestCode { get; init; }
    /// <summary>Why a full run leaves the file out, or null.</summary>
    public string? Excluded { get; init; }
    /// <summary>The entries that match it, in file order (0-based).</summary>
    public required IReadOnlyList<int> Matched { get; init; }
    public required RuleSettings Settings { get; init; }
}

/// <summary>One row of the <c>--file</c> view: a rule's switch, level or parameter, its value and its origin.</summary>
public sealed record ConfigRuleRow(string Rule, string Part, string Value, string Origin);

/// <summary>The rows and origins of <c>skarp config --file</c> (the human and the JSON form share them).</summary>
public static class ConfigOverrideRows
{
    /// <summary>
    /// Every rule of the file's settings: off (with the origin of the switch), else its notice and warning levels and its
    /// parameters, each with the level or override that set it (the command line wins over every override; an override
    /// reset is named as such), else the preset.
    /// </summary>
    public static List<ConfigRuleRow> Rows(SkarpOptions o, ConfigFileView f)
    {
        var rows = new List<ConfigRuleRow>();
        var s = f.Settings;
        foreach (var info in RuleCatalog.Every.Concat(RuleCatalog.Composite.Where(c => s[c.Id].Enabled)))
        {
            var a = s[info.Id];
            if (!a.Enabled) { rows.Add(new(info.Id, "on/off", "off", RuleOrigin(o, f.Matched, info.Id))); continue; }
            if (RuleCatalog.IsComposite(info.Id)) rows.Add(new(info.Id, "on/off", "on (composite)", RuleOrigin(o, f.Matched, info.Id)));
            else
                foreach (var sev in (ReadOnlySpan<Severity>)[Severity.Notice, Severity.Warning])
                {
                    var part = sev == Severity.Warning ? "warning" : "notice";
                    rows.Add(new(info.Id, part, LevelText(info.Id, a, a.Level(sev)), Origin(o, f.Matched, info.Id, part) ?? $"preset {a.LevelPreset(sev)}"));
                }
            foreach (var (k, v) in a.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
                rows.Add(new(info.Id, k, v, Origin(o, f.Matched, info.Id, k) ?? $"preset {a.Preset}"));
        }
        return rows;
    }

    static string LevelText(string rule, ActiveRule active, LevelThreshold? level) =>
        level is null ? "blank"
        : rule == RuleIds.GodClass ? (GodClassPresets.Find(active.Parameters.GetValueOrDefault("preset", "")) ?? GodClassPresets.Sets[0]).Describe()
        : level.Describe();

    /// <summary>Who set <c>thresholds.&lt;rule&gt;.&lt;part&gt;</c>: the command line, else the last matching override
    /// that names it (or resets the rule), else the level that set it; null: the preset.</summary>
    static string? Origin(SkarpOptions o, IReadOnlyList<int> matched, string rule, string part)
    {
        var prefix = $"{rule}.{part}";
        if (o._cliThresholds.Any(t => t.IndexOf('=') is int eq and > 0 && Under(t[..eq].Trim(), prefix))) return SettingOrigin.CommandLine.Name();
        for (int i = matched.Count - 1; i >= 0; i--)
        {
            var entry = o.RuleOverrides[matched[i]];
            if (entry.Thresholds is not { } th || !th.TryGetPropertyValue(rule, out var value)) continue;
            if (value is null) return $"{entry.Name} (reset to the preset)";
            if (value is JsonObject levels && levels.TryGetPropertyValue(part, out var v)) return v is null ? $"{entry.Name} (reset to the preset)" : entry.Name;
        }
        var keys = new List<string>();
        foreach (var t in o.Thresholds)
            if (t.IndexOf('=') is int eq and > 0 && Under(t[..eq].Trim(), prefix)) keys.Add(o.OriginText("thresholds." + t[..eq].Trim()));
        return keys.Count == 0 ? null : string.Join(", ", keys.Distinct());
    }

    static bool Under(string key, string prefix) => key == prefix || key.StartsWith(prefix + ".", StringComparison.Ordinal);

    /// <summary>Who switched the rule on or off: the command line, else the last matching override, else its level.</summary>
    static string RuleOrigin(SkarpOptions o, IReadOnlyList<int> matched, string rule)
    {
        if (o._cliRules.ContainsKey(rule)) return SettingOrigin.CommandLine.Name();
        for (int i = matched.Count - 1; i >= 0; i--)
        {
            var entry = o.RuleOverrides[matched[i]];
            if (entry.Rules is { } r && r.TryGetPropertyValue(rule, out var v)) return v is null ? $"{entry.Name} (reset to the default)" : entry.Name;
        }
        return o.OriginText($"rules.{rule}");
    }

    /// <summary>What an entry changes, in words: <c>rules magic-number off; thresholds long-method {"notice":100}</c>.</summary>
    public static string Changes(RuleOverride r)
    {
        var parts = new List<string>();
        if (r.Rules is { Count: > 0 } rules)
            parts.Add("rules " + string.Join(", ", rules.Select(p => $"{p.Key} {(p.Value is null ? "reset" : p.Value.ToJsonString() == "true" ? "on" : "off")}")));
        if (r.Thresholds is { Count: > 0 } th)
            parts.Add("thresholds " + string.Join(", ", th.Select(p => $"{p.Key} {(p.Value is null ? "reset" : p.Value.ToJsonString())}")));
        return parts.Count == 0 ? "nothing" : string.Join("; ", parts);
    }
}
