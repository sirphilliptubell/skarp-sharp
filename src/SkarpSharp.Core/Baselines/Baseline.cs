using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkarpSharp.Findings;
using SkarpSharp.Model;

namespace SkarpSharp.Baselines;

/// <summary>One recorded finding: its identity and measured value.</summary>
public sealed class BaselineEntry
{
    public string Id { get; set; } = "";
    public double? Value { get; set; }
    /// <summary>Clone groups only, the severity and the files of the instances (the group's points
    /// are taken from each of them in baseline health scores).</summary>
    public string? Severity { get; set; }
    public List<string>? Files { get; set; }
}

/// <summary>
/// A baseline file as the program works with it: every recorded finding by its id (<c>rule|path|place</c>). On disk
/// (<c>.skarp-baseline.json</c>, committed to the repository, written deliberately with <c>skarp baseline</c>) the
/// findings are grouped under the path of their file, see <see cref="BaselineDisk"/>.
/// </summary>
public sealed class BaselineFile
{
    public int Version { get; set; }
    /// <summary>The kind of file: <see cref="Baseline.TypeName"/> or <see cref="SemanticBaseline.TypeName"/>.</summary>
    public string Type { get; set; } = "";
    public string ToolVersion { get; set; } = "";
    public string Preset { get; set; } = "";
    public string ComplexMetric { get; set; } = "";
    public string LongMethodLines { get; set; } = "";
    /// <summary>Short hash of the effective rule settings (<see cref="Baseline.SettingsFingerprint"/>); empty in older files.</summary>
    public string SettingsFingerprint { get; set; } = "";
    /// <summary>HEAD (full hash) when the baseline was written; absent without git, with history off or in a shallow clone.</summary>
    public string? Commit { get; set; }
    /// <summary>Commits reachable from <see cref="Commit"/> that change C# files (the base of the C# commit total).</summary>
    public int? CsharpCommits { get; set; }
    /// <summary>The solution and target framework(s) of a semantic baseline.</summary>
    public string? Solution { get; set; }
    public string? Targets { get; set; }
    public string? TargetFramework { get; set; }
    /// <summary>Every file the run analyzed (relative, '/' separators, sorted; excluded and test files
    /// not listed), so files created later are told apart from files without findings. Syntax baselines only;
    /// empty in a semantic baseline.</summary>
    public List<string> Files { get; set; } = [];
    public List<BaselineEntry> Findings { get; set; } = [];
}

public static class Baseline
{
    public const string FileName = ".skarp-baseline.json";
    /// <summary>The <c>type</c> property of a syntax baseline file.</summary>
    public const string TypeName = "baseline";
    public const int CurrentVersion = 1;

    /// <summary>Rules whose value measures activity, not the code: a higher value is not "worsened".</summary>
    static readonly HashSet<string> ActivityRules = new(StringComparer.Ordinal) { RuleIds.ActiveComplexFile };

    /// <summary>The baseline text for these findings and settings (deterministic: no dates, sorted by file path, then rule and place).</summary>
    public static string Write(IEnumerable<Finding> findings, string toolVersion, FindingSettings settings, string? settingsFingerprint = null,
        History.CommitAnchor? anchor = null, IEnumerable<string>? files = null)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append($"  \"version\": {CurrentVersion},\n");
        sb.Append($"  \"type\": \"{TypeName}\",\n");
        sb.Append($"  \"toolVersion\": {BaselineLayout.Str(toolVersion)},\n");
        sb.Append($"  \"preset\": {BaselineLayout.Str(settings.Preset)},\n");
        sb.Append($"  \"complexMetric\": {BaselineLayout.Str(settings.ComplexMetric)},\n");
        sb.Append($"  \"longMethodLines\": {BaselineLayout.Str(settings.LongMethodLines)},\n");
        if (settingsFingerprint is not null) sb.Append($"  \"settingsFingerprint\": {BaselineLayout.Str(settingsFingerprint)},\n");
        if (anchor is not null)
        {
            sb.Append($"  \"commit\": {BaselineLayout.Str(anchor.Commit)},\n");
            sb.Append($"  \"csharpCommits\": {anchor.CSharpCommits.ToString(CultureInfo.InvariantCulture)},\n");
        }
        BaselineLayout.AppendBody(sb, findings, files);
        return sb.ToString();
    }

    /// <summary>Reads the text of a syntax baseline (<c>type</c> "baseline"); the file must be of that type.</summary>
    public static BaselineFile Parse(string json, string path) => BaselineLayout.Parse(json, path, TypeName);

    public static BaselineFile Read(string path) => Parse(File.ReadAllText(path), path);

    /// <summary>Report note when the rule settings differ from the baseline's.</summary>
    public const string SettingsChangedNote =
        "rule settings changed since the baseline; new and fixed counts include settings effects; re-run `skarp baseline` to reset";

    /// <summary>Report note for a baseline written before settings fingerprints.</summary>
    public const string SettingsUnknownNote =
        "the baseline records no rule-settings fingerprint (older skarp), so whether rule settings changed since it is unknown; re-run `skarp baseline` to record one";

    /// <summary>One rule's line of the settings fingerprint: <c>rule long-method notice=... warning=... parameters</c> or <c>rule x off</c>.</summary>
    public static string RuleLine(RuleSettingInfo r)
    {
        var sb = new StringBuilder("rule ").Append(r.Rule);
        if (r.Off == true) return sb.Append(" off").ToString();
        sb.Append(" notice=").Append(r.Notice ?? "-").Append(" warning=").Append(r.Warning ?? "-");
        if (r.Parameters is { } ps)
            foreach (var (k, v) in ps.OrderBy(p => p.Key, StringComparer.Ordinal)) sb.Append(' ').Append(k).Append('=').Append(v);
        return sb.ToString();
    }

    /// <summary>
    /// A short fingerprint (first 16 hex digits of SHA-256) of the effective rule settings that change the finding
    /// set or its values: preset, complex-method metric, long-method line unit, record parameters, the thresholds and
    /// parameters in effect per rule (so overrides count by their effect, not their spelling), rules switched off, and
    /// the file selection (tests included, generated included, include and exclude globs). The magic-numbers mode is not
    /// included: it only changes which findings are listed, not the finding set.
    /// </summary>
    /// <param name="variants">The variant settings in words (mode, caps, <c>symbols</c>,
    /// <c>VariantSettings.FingerprintText</c>); they change the finding set of files with <c>#if</c>.</param>
    /// <param name="overrides">One text per <c>overrides</c> entry, in file order
    /// (RuleOverrides.FingerprintLines); appended last, so no overrides gives the same text.</param>
    public static string SettingsFingerprint(FindingSettings settings, bool includeTests, bool includeGenerated,
        IEnumerable<string> include, IEnumerable<string> exclude, string? variants = null, IEnumerable<string>? overrides = null)
    {
        var sb = new StringBuilder();
        sb.Append("v1\n");
        sb.Append("preset=").Append(settings.Preset).Append('\n');
        sb.Append("complexMetric=").Append(settings.ComplexMetric).Append('\n');
        sb.Append("longMethodLines=").Append(settings.LongMethodLines).Append('\n');
        sb.Append("recordParameters=").Append(settings.RecordParameters).Append('\n');
        sb.Append("includeTests=").Append(includeTests ? "true" : "false").Append('\n');
        sb.Append("includeGenerated=").Append(includeGenerated ? "true" : "false").Append('\n');
        sb.Append("include=").AppendJoin('|', include.Order(StringComparer.Ordinal)).Append('\n');
        sb.Append("exclude=").AppendJoin('|', exclude.Order(StringComparer.Ordinal)).Append('\n');
        if (variants is not null) sb.Append("variants=").Append(variants).Append('\n');
        // The coverage rules are listed only in runs with a report; leaving them out keeps the fingerprint the same
        // with and without --coverage (their findings are "not evaluated" without one, never a settings effect).
        foreach (var r in settings.Rules.Where(r => !RuleCatalog.IsCoverage(r.Rule)).OrderBy(r => r.Rule, StringComparer.Ordinal))
            sb.Append(RuleLine(r)).Append('\n');
        foreach (var o in overrides ?? []) sb.Append(o).Append('\n');
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>
    /// Marks every finding <c>new</c>, <c>known</c> or <c>worsened</c> (value higher than recorded) and counts the
    /// baseline entries no longer found (<c>fixed</c>). Findings without a value, and activity rules, are never worsened.
    /// With <paramref name="settingsFingerprint"/> it also says whether the rule settings changed since the baseline.
    /// </summary>
    /// <summary>
    /// A new finding and a fixed baseline entry with the same rule, the same
    /// file and the same value (both without a value for structural rules) are paired in order, each used once; the new
    /// finding names the fixed id in <see cref="Finding.PossiblyRenamedFrom"/>. It still counts as new.
    /// </summary>
    static int MarkPossiblyRenamed(IReadOnlyList<Finding> findings, List<string> fixedIds, Dictionary<string, double?> recorded)
    {
        if (fixedIds.Count == 0) return 0;
        var pool = new Dictionary<(string Rule, string Path), List<string>>();
        foreach (var id in fixedIds.Order(StringComparer.Ordinal))
        {
            var parts = id.Split('|');
            if (parts.Length < 3) continue;
            var key = (parts[0], parts[1]);
            if (!pool.TryGetValue(key, out var list)) pool[key] = list = [];
            list.Add(id);
        }
        int n = 0;
        foreach (var f in findings)
        {
            if (f.BaselineState != "new" || !pool.TryGetValue((f.Rule, f.Path), out var candidates)) continue;
            int i = candidates.FindIndex(id => recorded[id] == f.Value);
            if (i < 0) continue;
            f.PossiblyRenamedFrom = candidates[i];
            candidates.RemoveAt(i);
            n++;
        }
        return n;
    }

    /// <param name="notEvaluated">Recorded ids this run could not judge (untested-complex-method without a coverage
    /// report, or in a file the reports say nothing about): neither fixed nor new, counted in <c>notEvaluated</c>.</param>
    public static BaselineSummary Compare(IReadOnlyList<Finding> findings, BaselineFile baseline, string path, FindingSettings? settings,
        string? settingsFingerprint = null, Func<string, bool>? notEvaluated = null)
    {
        var recorded = new Dictionary<string, double?>(StringComparer.Ordinal);
        foreach (var e in baseline.Findings) recorded[e.Id] = e.Value;
        var summary = new BaselineSummary
        {
            Path = path, ToolVersion = baseline.ToolVersion, Preset = baseline.Preset, Entries = recorded.Count,
        };
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in findings)
        {
            present.Add(f.Id);
            if (!recorded.TryGetValue(f.Id, out var old))
            {
                f.BaselineState = "new";
                summary.New++;
                continue;
            }
            f.BaselineValue = old;
            if (old is double o && f.Value is double v && v > o && !ActivityRules.Contains(f.Rule))
            {
                f.BaselineState = "worsened";
                summary.Worsened++;
            }
            else
            {
                f.BaselineState = "known";
                summary.Known++;
            }
        }
        var absent = recorded.Keys.Where(id => !present.Contains(id)).ToList();
        var fixedIds = notEvaluated is null ? absent : absent.Where(id => !notEvaluated(id)).ToList();
        summary.Fixed = fixedIds.Count;
        if (absent.Count > fixedIds.Count)
        {
            summary.NotEvaluated = absent.Count - fixedIds.Count;
            summary.Notes.Add($"{summary.NotEvaluated} recorded {RuleIds.UntestedComplexMethod} finding{(summary.NotEvaluated == 1 ? " was" : "s were")} not evaluated (no coverage report, or no coverage data for the file) and stay{(summary.NotEvaluated == 1 ? "s" : "")} as recorded");
        }
        summary.PossiblyRenamed = MarkPossiblyRenamed(findings, fixedIds, recorded);
        if (settingsFingerprint is not null)
        {
            if (string.IsNullOrEmpty(baseline.SettingsFingerprint)) summary.Notes.Add(SettingsUnknownNote);
            else
            {
                summary.SettingsChanged = baseline.SettingsFingerprint != settingsFingerprint;
                if (summary.SettingsChanged == true) summary.Notes.Add(SettingsChangedNote);
            }
        }
        if (settings is not null)
        {
            if (baseline.Preset != settings.Preset) summary.Notes.Add($"baseline recorded with preset {baseline.Preset}, this run uses {settings.Preset}");
            if (baseline.ComplexMetric.Length > 0 && baseline.ComplexMetric != settings.ComplexMetric)
                summary.Notes.Add($"baseline recorded with complex-method on {baseline.ComplexMetric}, this run uses {settings.ComplexMetric}");
            if (baseline.LongMethodLines.Length > 0 && baseline.LongMethodLines != settings.LongMethodLines)
                summary.Notes.Add($"baseline recorded with long-method on {baseline.LongMethodLines} lines, this run uses {settings.LongMethodLines}");
        }
        return summary;
    }
}
