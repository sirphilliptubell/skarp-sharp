using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkarpSharp.Findings;

namespace SkarpSharp.Configuration;

/// <summary>
/// One entry of the repository file's <c>overrides</c>. Its
/// selector is <see cref="Files"/> (globs), <see cref="ExcludeFiles"/> and <see cref="Tests"/> (Skarp's own test
/// detection), combined with AND; its patch holds <c>rules</c> and <c>thresholds</c> as written, JSON nulls kept as resets
/// to the Skarp default.
/// </summary>
public sealed class RuleOverride
{
    /// <summary>The entry's position in the file (0-based; messages and <c>skarp config</c> say "override N" with N = Index + 1).</summary>
    public required int Index { get; init; }
    /// <summary><c>files</c> as written, or null when the entry has none.</summary>
    public IReadOnlyList<string>? Files { get; init; }
    /// <summary><c>excludeFiles</c> as written, or null.</summary>
    public IReadOnlyList<string>? ExcludeFiles { get; init; }
    /// <summary><c>"tests": true</c>: the entry selects only files Skarp's test detection marks as test code.</summary>
    public bool Tests { get; init; }
    /// <summary><see cref="Files"/> rebased onto the analyzed directory (globs that cannot match there are dropped).</summary>
    public IReadOnlyList<string>? RebasedFiles { get; init; }
    public IReadOnlyList<string>? RebasedExcludeFiles { get; init; }
    /// <summary><c>{ "rules": ..., "thresholds": ... }</c> as written (nulls kept).</summary>
    public required JsonObject Patch { get; init; }
    /// <summary>The rule ids the patch names (in <c>rules</c> or <c>thresholds</c>).</summary>
    public required IReadOnlySet<string> RulesNamed { get; init; }

    /// <summary>The patch's thresholds in <c>--threshold</c> grammar (resets left out).</summary>
    internal IReadOnlyList<string> ThresholdTexts { get; init; } = [];

    /// <summary>Every glob of <see cref="Files"/> was dropped by rebasing: the entry matches no file of this run.</summary>
    public bool MatchesNothing => RebasedFiles is { Count: 0 };

    /// <summary>The <c>rules</c> part as written, or null.</summary>
    public JsonObject? Rules => Patch["rules"] as JsonObject;
    /// <summary>The <c>thresholds</c> part as written, or null.</summary>
    public JsonObject? Thresholds => Patch["thresholds"] as JsonObject;

    /// <summary>"override 2" (1-based, as people count entries).</summary>
    public string Name => $"override {Index + 1}";

    /// <summary>The selectors in words: <c>files src/Legacy/**, excludeFiles src/Legacy/Keep/**, tests</c>.</summary>
    public string SelectorText()
    {
        var parts = new List<string>();
        if (Files is not null) parts.Add("files " + string.Join(", ", Files));
        if (ExcludeFiles is not null) parts.Add("excludeFiles " + string.Join(", ", ExcludeFiles));
        if (Tests) parts.Add("test code");
        return string.Join("; ", parts);
    }

    /// <summary>The selector part of this entry's fingerprint text (globs as applied, after rebasing).</summary>
    internal string SelectorFingerprint()
    {
        var sb = new StringBuilder("override ").Append(Index);
        sb.Append(" files=").Append(RebasedFiles is null ? "-" : string.Join('|', RebasedFiles));
        sb.Append(" excludeFiles=").Append(RebasedExcludeFiles is null ? "-" : string.Join('|', RebasedExcludeFiles));
        return sb.Append(" tests=").Append(Tests ? "true" : "-").ToString();
    }
}

/// <summary>Reads and checks <c>overrides</c>.</summary>
public static class RuleOverrides
{
    static readonly string[] Keys = ["files", "excludeFiles", "tests", "rules", "thresholds"];

    /// <summary>Rules whose findings match across files: one setting per run, an error inside an override.</summary>
    public static readonly IReadOnlyList<string> CrossFile = [Findings.RuleIds.DuplicateCode, Findings.RuleIds.DuplicateCodeNear, Findings.RuleIds.NamespaceCycle];

    /// <summary>
    /// One text per entry for the settings fingerprints (syntax, or <paramref name="semantic"/>): its
    /// selectors, then the fingerprint line of each rule it names, judged by its effect on the repository settings
    /// (the entry alone over them), so a different spelling of the same value keeps the fingerprint.
    /// </summary>
    public static List<string> FingerprintLines(IReadOnlyList<RuleOverride> overrides, PathRules rules, bool semantic)
    {
        var lines = new List<string>(overrides.Count);
        foreach (var r in overrides)
        {
            var settings = rules.Combination([r.Index]);
            var info = semantic ? settings.ToSemanticInfo() : settings.ToInfo();
            var sb = new StringBuilder(r.SelectorFingerprint());
            foreach (var rule in info.Rules.Where(x => r.RulesNamed.Contains(x.Rule)).OrderBy(x => x.Rule, StringComparer.Ordinal))
                sb.Append(" | ").Append(Baselines.Baseline.RuleLine(rule));
            lines.Add(sb.ToString());
        }
        return lines;
    }

    /// <summary><c>run.findings.pathOverrides</c>, each entry as written with the files it matched in
    /// <paramref name="files"/> (path and test status of every file the run kept); null with no overrides.</summary>
    public static List<Model.PathOverrideInfo>? Info(IReadOnlyList<RuleOverride> overrides, PathRules rules, IEnumerable<(string Path, bool TestCode)> files,
        out int matchedFiles)
    {
        matchedFiles = 0;
        if (overrides.Count == 0) return null;
        var (per, any) = rules.MatchCounts(files);
        matchedFiles = any;
        return [.. overrides.Select(r => new Model.PathOverrideInfo
        {
            Index = r.Index, Files = r.Files is null ? null : [.. r.Files], ExcludeFiles = r.ExcludeFiles is null ? null : [.. r.ExcludeFiles],
            Tests = r.Tests ? true : null, Rules = Element(r.Rules), Thresholds = Element(r.Thresholds), Matched = per[r.Index],
        })];
    }

    internal static JsonElement? Element(JsonObject? o)
    {
        if (o is null) return null;
        using var doc = JsonDocument.Parse(o.ToJsonString());
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// The entries of <paramref name="node"/> in file order. Globs are rebased onto the analyzed directory, which lies
    /// <paramref name="prefix"/> below the file's folder (null: elsewhere, globs kept; empty: the same folder).
    /// </summary>
    /// <exception cref="SettingsException">The message names the file, the entry and the key.</exception>
    internal static List<RuleOverride> Read(JsonNode node, string path, string? prefix)
    {
        if (node is not JsonArray list) throw new SettingsException($"{path}: overrides: wrong type (expected a list of objects)");
        var result = new List<RuleOverride>(list.Count);
        for (int i = 0; i < list.Count; i++) result.Add(ReadEntry(list[i], i, path, prefix));
        return result;
    }

    static RuleOverride ReadEntry(JsonNode? node, int i, string path, string? prefix)
    {
        var at = $"overrides[{i}]";
        SettingsException Error(string key, string message) => new($"{path}: {at}{key}: {message}");
        if (node is not JsonObject entry) throw Error("", "wrong type (expected an object)");
        foreach (var (name, _) in entry)
            if (Array.IndexOf(Keys, name) < 0)
                throw Error("." + name, "not allowed in an override (only files, excludeFiles, tests, rules and thresholds; everything else is one setting per run)");
        var files = Globs(entry, "files", Error);
        var excludeFiles = Globs(entry, "excludeFiles", Error);
        bool tests = false;
        if (entry.TryGetPropertyValue("tests", out var t))
        {
            if (t is null || t.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) throw Error(".tests", "wrong type (expected true)");
            if (!t.GetValue<bool>()) throw Error(".tests", "only true is allowed (leave tests out to select production and test code alike)");
            tests = true;
        }
        if (files is null && !tests)
            throw Error("", "needs files or \"tests\": true" + (excludeFiles is not null ? " (excludeFiles only narrows a selection)" : ""));

        var patch = new JsonObject();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in (ReadOnlySpan<string>)["rules", "thresholds"])
        {
            if (!entry.TryGetPropertyValue(part, out var value)) continue;
            if (value is not JsonObject obj)
                throw Error("." + part, value is null ? "null is not allowed here (reset single rules, such as \"long-method\": null)" : "wrong type (expected an object)");
            foreach (var (rule, setting) in obj)
            {
                if (RuleCatalog.Find(rule) is null) continue; // the value check below names it
                CheckRule(rule, part, setting, Error);
                ids.Add(rule);
            }
            patch[part] = obj.DeepClone();
        }
        var texts = CheckValues(patch, path, at);
        return new RuleOverride
        {
            Index = i, Files = files, ExcludeFiles = excludeFiles, Tests = tests, Patch = patch, RulesNamed = ids, ThresholdTexts = texts,
            RebasedFiles = Rebased(files, prefix), RebasedExcludeFiles = Rebased(excludeFiles, prefix),
        };
    }

    /// <summary>The rules that cannot vary per path.</summary>
    static void CheckRule(string rule, string part, JsonNode? setting, Func<string, string, SettingsException> error)
    {
        var key = $".{part}.{rule}";
        if (CrossFile.Contains(rule))
            throw error(key, $"{rule} matches across files and has one setting per run; use exclude to leave files out");
        if (rule == Findings.RuleIds.ActiveComplexFile
            && !(part == "rules" && setting is JsonValue v && v.GetValueKind() == JsonValueKind.False))
            throw error(key, "active-complex-file ranks files repository-wide; an override can only switch it off (\"rules\": { \"active-complex-file\": false })");
    }

    /// <summary>The values are checked as a settings file of their own would be (resets left out), and errors name the entry.</summary>
    /// <returns>The patch's thresholds in <c>--threshold</c> grammar (step 4 of the parse checks them under the run's preset).</returns>
    static List<string> CheckValues(JsonObject patch, string path, string at)
    {
        var check = (JsonObject)SettingsLevels.WithoutNulls(patch);
        check["version"] = SettingsFileReader.CurrentVersion;
        try
        {
            var f = SettingsFileReader.Parse(check.ToJsonString(), path);
            CommandLine.ValidateRules(f, path);
            return [.. SettingsFileReader.ThresholdOverrides(f, path).Select(t => t.Override)];
        }
        catch (SettingsException ex) when (ex.Message.StartsWith(path + ": ", StringComparison.Ordinal))
        {
            throw new SettingsException($"{path}: {at}.{ex.Message[(path.Length + 2)..]}");
        }
    }

    static List<string>? Globs(JsonObject entry, string key, Func<string, string, SettingsException> error)
    {
        if (!entry.TryGetPropertyValue(key, out var node)) return null;
        if (node is not JsonArray a) throw error("." + key, "wrong type (expected a list of globs)");
        if (a.Count == 0) throw error("." + key, key == "files" ? "empty list (name at least one glob, or use \"tests\": true)" : "empty list (leave it out)");
        var globs = new List<string>(a.Count);
        foreach (var g in a)
        {
            if (g is not JsonValue v || v.GetValueKind() != JsonValueKind.String) throw error("." + key, "wrong type (expected a list of globs)");
            var s = v.GetValue<string>();
            if (s.Trim().Length == 0) throw error("." + key, "empty glob");
            globs.Add(s);
        }
        return globs;
    }

    /// <summary>Globs rebased like include and exclude; one that cannot match under the analyzed directory is dropped.</summary>
    static List<string>? Rebased(List<string>? globs, string? prefix)
    {
        if (globs is null || prefix is null or "") return globs;
        var list = new List<string>(globs.Count);
        foreach (var g in globs) if (SettingsFileReader.RebaseGlob(g, prefix) is { } r) list.Add(r);
        return list;
    }
}
