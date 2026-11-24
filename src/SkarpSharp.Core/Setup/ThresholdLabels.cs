using System.Text.Json.Nodes;
using SkarpSharp.Configuration;
using SkarpSharp.Findings;

namespace SkarpSharp.Setup;

/// <summary>One condition in the "N or more" form; <see cref="OrMore"/> is false for a fractional "over".</summary>
public sealed class NormalCondition(string metric, double value, bool orMore)
{
    public string Metric { get; } = metric;
    public double Value { get; } = value;
    public bool OrMore { get; } = orMore;
}

/// <summary>A distinct vendor condition set of one rule level, with the presets that publish it (preset-list order).</summary>
public sealed class VendorSet(IReadOnlyList<NormalCondition> conditions, bool allOf, IReadOnlyList<Preset> presets, IReadOnlyList<LevelThreshold> levels)
{
    public IReadOnlyList<NormalCondition> Conditions { get; } = conditions;
    public bool AllOf { get; } = allOf;
    public IReadOnlyList<Preset> Presets { get; } = presets;
    public IReadOnlyList<LevelThreshold> Levels { get; } = levels;

    public List<string> Metrics
    {
        get
        {
            var list = new List<string>();
            foreach (var c in Conditions) list.Add(c.Metric);
            return list;
        }
    }
}

/// <summary>
/// The words of thresholds in <c>skarp setup</c>. Every condition reads
/// "N or more &lt;metric&gt;" with a plain metric name: a whole-number "over N" becomes "N+1 or more", so vendors with the
/// same effective limit merge onto one line; fractional values keep their value. Any-of sets read "…, or …", all-of
/// sets "… and … together". A choice is written in the comparison of the level it overrides (an override keeps the
/// level's comparison, <see cref="RuleSettings"/>): under "over", "10 or more" writes 9.
/// </summary>
public static class ThresholdLabels
{
    /// <summary>The plain name of a metric.</summary>
    public static string MetricName(string metric) => metric switch
    {
        "sourceLines" => "source lines",
        "physicalLines" => "all lines",
        "statements" => "statements",
        "methods" => "methods",
        "fields" => "fields",
        "parameters" => "parameters",
        "constructorParameters" => "constructor parameters",
        "nesting" => "nesting depth",
        "cbo" => "coupled classes",
        "cboAll" => "coupled classes (framework types too)",
        "cboMicrosoft" => "coupled classes (Microsoft counting)",
        "cyclomaticMicrosoft" => "cyclomatic (Microsoft counting)",
        "wmc" => "WMC",
        "npath" => "NPath",
        "booleanOperators" => "boolean operators",
        "activityShare" => "% of recent changes",
        _ => metric,
    };

    /// <summary>A vendor's short name; Java sources are marked <c>[Java]</c>.</summary>
    public static string VendorName(string presetId) => presetId switch
    {
        "default" => "Skarp default",
        "codescene" => "CodeScene",
        "clean-cadet" => "Clean-CaDET",
        "doomen" => "Doomen",
        "reportgenerator" => "ReportGenerator",
        "ndepend" => "NDepend",
        "pmd" => "PMD [Java]",
        "checkstyle" => "Checkstyle [Java]",
        "microsoft" => "Microsoft",
        "jscpd" => "jscpd",
        "alves-percentile" => "Alves [Java]",
        _ => presetId,
    };

    static bool Whole(double v) => v == Math.Floor(v);

    public static NormalCondition Normalize(Condition c) =>
        c.Comparison == Comparison.AtLeast ? new(c.Metric, c.Value, true)
        : Whole(c.Value) ? new(c.Metric, c.Value + 1, true)
        : new(c.Metric, c.Value, false);

    public static string Label(NormalCondition c) =>
        c.OrMore ? $"{SettingsDocument.N(c.Value)} or more {MetricName(c.Metric)}" : $"over {SettingsDocument.N(c.Value)} {MetricName(c.Metric)}";

    public static string Label(IReadOnlyList<NormalCondition> conditions, bool allOf)
    {
        var parts = new List<string>();
        foreach (var c in conditions) parts.Add(Label(c));
        return allOf ? string.Join(" and ", parts) + (conditions.Count > 1 ? " together" : "") : string.Join(", or ", parts);
    }

    /// <summary>A level's conditions in the "N or more" form.</summary>
    public static List<NormalCondition> Normalized(LevelThreshold level)
    {
        var list = new List<NormalCondition>();
        foreach (var c in level.AnyOf) list.Add(Normalize(c));
        return list;
    }

    /// <summary>The vendors of a set, comma-separated.</summary>
    public static string Vendors(VendorSet set)
    {
        var names = new List<string>();
        foreach (var p in set.Presets) names.Add(VendorName(p.Id));
        return string.Join(", ", names);
    }

    /// <summary>A level's metric keys, comma-separated.</summary>
    public static string MetricKeys(LevelThreshold level)
    {
        var names = new List<string>();
        foreach (var c in level.AnyOf) names.Add(c.Metric);
        return string.Join(", ", names);
    }

    public static NormalCondition? FindMetric(IReadOnlyList<NormalCondition> list, string metric)
    {
        foreach (var c in list) if (c.Metric == metric) return c;
        return null;
    }

    /// <summary>A level in words: <c>off</c>, <c>on</c> for a level without a number, else its conditions.</summary>
    public static string Label(LevelThreshold? level) =>
        level is null ? "off" : level.AnyOf.Count == 0 ? "on" : Label(Normalized(level), level.AllOf);

    /// <summary>The number of the first condition only ("16 or more"), for the preview of single-metric levels.</summary>
    public static string ShortLabel(LevelThreshold? level) =>
        level is { AnyOf.Count: 1 } && Normalize(level.AnyOf[0]) is { OrMore: true } c ? $"{SettingsDocument.N(c.Value)} or more" : Label(level);

    /// <summary>The preset table key of a rule: <c>complex-method/&lt;metric&gt;</c> or the rule id.</summary>
    public static string PresetKey(string rule, ThresholdContext ctx) => rule == RuleIds.ComplexMethod ? $"{rule}/{ctx.ComplexMetric}" : rule;

    /// <summary>
    /// The distinct vendor condition sets of a rule level, strict to loose (by the first condition's "or more" value),
    /// each with its presets: Skarp default first, then preset-list order. Line-based long-method values are in the
    /// context's line unit.
    /// </summary>
    public static IReadOnlyList<VendorSet> VendorSets(string rule, Severity severity, ThresholdContext ctx)
    {
        var key = PresetKey(rule, ctx);
        var sets = new List<(string Id, VendorSet Set)>();
        foreach (var preset in Presets.All)
        {
            if (!preset.Rules.TryGetValue(key, out var t)) continue;
            var level = severity == Severity.Warning ? t.Warning : t.Notice;
            if (level is not { AnyOf.Count: > 0 }) continue;
            var conditions = new List<NormalCondition>();
            foreach (var c in level.AnyOf)
                conditions.Add(Normalize(rule == RuleIds.LongMethod && c.Metric == RuleSettings.LinesMetric ? c with { Metric = ctx.LineMetric } : c));
            var id = level.AllOf ? "all:" : "any:";
            foreach (var c in conditions) id += $"{c.Metric}={c.Value}{(c.OrMore ? "+" : ">")}|";
            int existing = -1;
            for (int i = 0; i < sets.Count; i++) if (sets[i].Id == id) existing = i;
            if (existing >= 0) { ((List<Preset>)sets[existing].Set.Presets).Add(preset); ((List<LevelThreshold>)sets[existing].Set.Levels).Add(level); }
            else sets.Add((id, new VendorSet(conditions, level.AllOf, new List<Preset> { preset }, new List<LevelThreshold> { level })));
        }
        // Strict to loose; a stable sort keeps preset-list order between equal values.
        var result = new List<VendorSet>();
        foreach (var s in sets) result.Add(s.Set);
        for (int i = 1; i < result.Count; i++)
            for (int j = i; j > 0 && result[j - 1].Conditions[0].Value > result[j].Conditions[0].Value; j--)
                (result[j - 1], result[j]) = (result[j], result[j - 1]);
        return result;
    }

    /// <summary>The level a choice overrides: the preset's level, or (blank) the other level, whose metric and
    /// comparison an override of a blank level borrows.</summary>
    public static LevelThreshold? Basis(string rule, Severity severity, ThresholdContext ctx)
    {
        var active = ctx.Base.Rules[rule];
        return active.Level(severity) ?? active.Level(severity == Severity.Warning ? Severity.Notice : Severity.Warning);
    }

    /// <summary>
    /// True when the vendor set is offered: it measures exactly the basis level's metrics, or (any-of levels only) a part
    /// of them, in which case choosing it changes only those and the basis keeps the others (for
    /// example a single-number parameters limit over Skarp's parameters-or-constructor-parameters level).
    /// </summary>
    public static bool Offered(VendorSet set, LevelThreshold basis) =>
        basis.AnyOf.Count > 0 && AllIn(set, basis)
        && (set.Conditions.Count == basis.AnyOf.Count || (set.Conditions.Count < basis.AnyOf.Count && !set.AllOf && !basis.AllOf));

    /// <summary>True when an offered set covers only part of the basis level's metrics.</summary>
    public static bool Partial(VendorSet set, LevelThreshold basis) => set.Conditions.Count < basis.AnyOf.Count;

    /// <summary>The conditions a choice of <paramref name="set"/> means over <paramref name="basis"/>, in basis order:
    /// the set's value per metric, the basis's own for metrics the set does not have.</summary>
    public static List<NormalCondition> Completed(VendorSet set, LevelThreshold basis)
    {
        var list = new List<NormalCondition>();
        foreach (var c in basis.AnyOf) list.Add(FindMetric(set.Conditions, c.Metric) ?? Normalize(c));
        return list;
    }

    /// <summary>"; keeps 6 or more constructor parameters" for the basis conditions a partial set leaves alone.</summary>
    public static string KeepsNote(VendorSet set, LevelThreshold basis)
    {
        var kept = new List<NormalCondition>();
        foreach (var c in basis.AnyOf) if (FindMetric(set.Conditions, c.Metric) is null) kept.Add(Normalize(c));
        return kept.Count == 0 ? "" : "; keeps " + Label(kept, false);
    }

    static bool AllIn(VendorSet set, LevelThreshold basis)
    {
        foreach (var c in set.Conditions) if (basis.Find(c.Metric) is null) return false;
        return true;
    }

    /// <summary>The stored number for "N or more" under a comparison: one less under "over" for whole numbers.</summary>
    public static double Stored(NormalCondition wanted, Comparison comparison) =>
        comparison == Comparison.Over && wanted.OrMore && Whole(wanted.Value) ? wanted.Value - 1 : wanted.Value;

    /// <summary>
    /// The value a set of wanted conditions writes over <paramref name="basis"/>: a number for a single-metric level,
    /// else an object of metric values; each in the basis condition's comparison.
    /// </summary>
    public static JsonNode Write(IReadOnlyList<NormalCondition> wanted, LevelThreshold basis)
    {
        if (basis.AnyOf.Count == 1) return SettingsDocument.Number(Stored(wanted[0], basis.AnyOf[0].Comparison));
        var o = new JsonObject();
        foreach (var c in basis.AnyOf)
            if (FindMetric(wanted, c.Metric) is { } w) o[c.Metric] = SettingsDocument.Number(Stored(w, c.Comparison));
        return o;
    }

    /// <summary>The "(writes 9)" note when the stored numbers differ from the label's.</summary>
    public static string WritesNote(IReadOnlyList<NormalCondition> wanted, JsonNode written)
    {
        if (written is JsonValue v) return wanted.Count == 1 && SettingsDocument.AsNumber(v) != wanted[0].Value ? $" (writes {SettingsDocument.Text(v)})" : "";
        if (written is JsonObject o)
            foreach (var w in wanted)
                if (SettingsDocument.AsNumber(o[w.Metric]) is { } x && x != w.Value) return $" (writes {SettingsDocument.Text(o)})";
        return "";
    }

    /// <summary>The conditions a stored threshold value means over <paramref name="basis"/>, in the "N or more" form.</summary>
    public static IReadOnlyList<NormalCondition>? Meaning(JsonNode? stored, LevelThreshold? basis)
    {
        if (basis is not { AnyOf.Count: > 0 } || stored is null) return null;
        var list = new List<NormalCondition>();
        if (stored is JsonValue)
        {
            for (int i = 0; i < basis.AnyOf.Count; i++) list.Add(Normalize(i == 0 ? With(basis.AnyOf[0], stored) : basis.AnyOf[i]));
            return list;
        }
        if (stored is not JsonObject o) return null;
        foreach (var c in basis.AnyOf)
            list.Add(Normalize(With(c, o[c.Metric] ?? (o[RuleSettings.LinesMetric] is { } l && c.Metric.EndsWith("Lines", StringComparison.Ordinal) ? l : null))));
        // A metric the level does not have is added with "over" (as the override does).
        foreach (var (name, value) in o)
            if (name != RuleSettings.LinesMetric && basis.Find(name) is null && SettingsDocument.AsNumber(value) is { } d)
                list.Add(Normalize(new Condition(name, Comparison.Over, d)));
        return list;
    }

    static Condition With(Condition c, JsonNode? v) => SettingsDocument.AsNumber(v) is { } d ? c with { Value = d } : c;

    /// <summary>True when two condition lists are the same, in order.</summary>
    public static bool Same(IReadOnlyList<NormalCondition> a, IReadOnlyList<NormalCondition> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].Metric != b[i].Metric || a[i].Value != b[i].Value || a[i].OrMore != b[i].OrMore) return false;
        return true;
    }

    /// <summary>The pane text of a vendor set: each vendor's own wording (the source sentence) and the condition in
    /// internal keys, then the settings key.</summary>
    public static string Pane(VendorSet set, string key)
    {
        var parts = new List<string>();
        for (int i = 0; i < set.Presets.Count; i++)
            parts.Add($"{VendorName(set.Presets[i].Id)}: \"{set.Levels[i].Source}\" ({set.Levels[i].Describe()}).");
        return string.Join(" ", parts) + $" Key: {key}.";
    }
}
