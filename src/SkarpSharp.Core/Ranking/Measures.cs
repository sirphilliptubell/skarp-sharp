using SkarpSharp.Configuration;
using System.Globalization;
using SkarpSharp.Findings;
using SkarpSharp.Model;

namespace SkarpSharp.Ranking;

/// <summary>
/// A rule's own measure for a rule sort, on the records the rule judges (the same members as the
/// rule: top-level members for complex-method and deep-nesting, no lambdas or local functions for long-method), and the
/// limit an entry is read against: the highest level it crosses, else the lowest level (what it would cross first).
/// </summary>
public sealed class Measures(string rule, string metric, PathRules rules, IReadOnlyList<TypeSummary> types)
{
    readonly ILookup<string, TypeSummary> _types = types.Where(t => t.Kind is not ("enum" or "") && t.Path.Length > 0).ToLookup(t => t.Path, StringComparer.Ordinal);

    /// <summary>The metric a rule sorts by: the first condition of its lowest level in <paramref name="settings"/> (so the
    /// preset and <c>--complex-metric</c> or <c>--long-method-lines</c> decide), else the rule's usual metric.</summary>
    public static string MetricOf(RuleSettings settings, string rule)
    {
        if (settings.Rules.TryGetValue(rule, out var a) && (a.Notice ?? a.Warning)?.AnyOf is { Count: > 0 } conditions) return conditions[0].Metric;
        return rule switch
        {
            RuleIds.ComplexMethod => settings.ComplexMetric,
            RuleIds.DeepNesting => "nesting",
            RuleIds.LongMethod => settings.LongMethodLines == "source" ? "sourceLines" : "physicalLines",
            RuleIds.TooManyParameters => "parameters",
            RuleIds.PathExplosion => "npath",
            RuleIds.BumpyRoad => "bumps",
            RuleIds.ComplexCondition => "booleanOperators",
            _ => "sourceLines",
        };
    }

    /// <summary>A member's measure, or null when the rule does not judge that member.</summary>
    public double? Of(FileResult file, MemberResult m)
    {
        bool topLevel = m.Parent is null;
        return rule switch
        {
            RuleIds.ComplexMethod or RuleIds.DeepNesting => topLevel ? Rules.MemberMetric(m, metric) : null,
            RuleIds.LongMethod => m.Kind is "Lambda" or "LocalFunction" ? null : Rules.MemberMetric(m, metric),
            RuleIds.PathExplosion => Rules.MemberMetric(m, metric),
            RuleIds.TooManyParameters => Parameters(m),
            RuleIds.BumpyRoad => m.Kind is "Lambda" ? null : Rules.Bumps(m, BumpDepth(file)).Count,
            RuleIds.ComplexCondition => m.Kind is "Lambda" ? null : m.BooleanOperatorsMax ?? 0,
            _ => null,
        };
    }

    /// <summary>The parameters too-many-parameters judges: not of lambdas, overrides or explicit implementations.</summary>
    static double? Parameters(MemberResult m) => m.Kind == "Lambda" || m.InheritedSignature == true ? null : m.Parameters;

    int BumpDepth(FileResult file) =>
        int.TryParse(Active(file).Parameters.GetValueOrDefault("depth"), NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d : 2;

    /// <summary>A file's worst value: its worst method (method rules; the first on a tie), its largest type (large-type) or
    /// the file itself (large-file); null when it has nothing the rule judges.</summary>
    public (double Value, MemberResult? Member, TypeSummary? Type)? WorstOf(FileResult file) => rule switch
    {
        RuleIds.LargeFile => (metric == "physicalLines" ? file.Lines.Physical : file.Lines.Source, null, null),
        RuleIds.LargeType => Largest(_types[file.Path], t => Rules.TypeMetric(t, metric)) is ({ } t, var v) ? (v, null, t) : null,
        _ => Largest(file.Members, m => Of(file, m)) is ({ } m, var mv) ? (mv, m, null) : null,
    };

    /// <summary>The first item with the largest value, or (null, 0) when none has a value.</summary>
    static (T? Item, double Value) Largest<T>(IEnumerable<T> items, Func<T, double?> value) where T : class
    {
        T? best = null;
        double max = double.MinValue;
        foreach (var item in items)
            if (value(item) is double v && v > max) (max, best) = (v, item);
        return (best, best is null ? 0 : max);
    }

    ActiveRule Active(FileResult file) => rules.For(file.Path, file.TestCode)[rule];

    /// <summary>The limit of a value in words (<c>70</c>, <c>at least 8</c>), from the file's settings: the highest level
    /// it crosses, else the lowest level; null when the rule is off for the file.</summary>
    public string? Limit(FileResult file, double value, MemberResult? member)
    {
        var a = Active(file);
        var key = rule == RuleIds.TooManyParameters && member?.Kind == "Constructor" ? "constructorParameters" : metric;
        Condition? Find(LevelThreshold? level) => level?.Find(key) ?? (key != metric ? level?.Find(metric) : null);
        var c = Find(a.Warning) is { } w && w.Test(value) ? w : Find(a.Notice) ?? Find(a.Warning);
        return c is null ? null : (c.Comparison == Comparison.AtLeast ? "at least " : "") + Condition.Num(c.Value);
    }
}
