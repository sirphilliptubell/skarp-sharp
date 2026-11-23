using SkarpSharp.Baselines;
using SkarpSharp.Findings;
using SkarpSharp.Model;

namespace SkarpSharp.Health;

/// <summary>
/// Scores of a baseline, recomputed from its findings without a format change. A baseline entry
/// holds only an id and a value, so its severity is rebuilt: from the current finding with the same id and value when
/// there is one (the same measure gives the same severity), else by judging the value against the rule's warning level
/// in effect; a structural entry (no value) with no current finding takes the rule's highest level.
/// </summary>
public static class HealthBaseline
{
    public static Dictionary<string, List<ScoredFinding>> ByPath(IEnumerable<BaselineEntry> entries, IReadOnlyDictionary<string, Finding> current, RuleSettings rules,
        Func<string, RuleSettings>? rulesOf = null)
    {
        var map = new Dictionary<string, List<ScoredFinding>>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            var parts = e.Id.Split('|', 3);
            // A clone group's entry lists its files and severity; it counts once in each file.
            IEnumerable<string> paths = e.Files is { } files ? files.Distinct(StringComparer.Ordinal) : parts.Length >= 3 && parts[1].Length > 0 ? [parts[1]] : [];
            if (parts.Length < 3) continue;
            var now = current.GetValueOrDefault(e.Id);
            // An entry with a path is judged by that file's settings; clone groups and namespace cycles (pathless) by the repository's.
            var judgeBy = rulesOf is not null && e.Files is null && parts[1].Length > 0 ? rulesOf(parts[1]) : rules;
            var (severity, metric, threshold) = Judge(parts[0], e.Value, now, judgeBy);
            if (e.Severity is { Length: > 0 } recorded && (now is null || now.Value != e.Value)) severity = recorded;
            foreach (var path in paths)
            {
                if (!map.TryGetValue(path, out var list)) map[path] = list = [];
                list.Add(ScoredFinding.From(e.Id, parts[0], severity, metric, e.Value, threshold));
            }
        }
        return map;
    }

    /// <summary>The severity (and the metric and threshold it was judged on) of a recorded entry.</summary>
    public static (string Severity, string? Metric, double? Threshold) Judge(string rule, double? value, Finding? now, RuleSettings rules)
    {
        if (now is not null && now.Value == value) return (now.Severity, now.Metric, now.Threshold);
        if (!rules.Rules.TryGetValue(rule, out var active)) return (now?.Severity ?? "notice", now?.Metric, null);
        if (value is not double v) return (now?.Severity ?? (active.Warning is not null ? "warning" : "notice"), null, null);
        var metric = now?.Metric ?? (active.Notice ?? active.Warning)?.AnyOf.FirstOrDefault()?.Metric;
        if (metric is not null && active.Warning?.Find(metric) is { } w && w.Test(v)) return ("warning", metric, w.Value);
        return ("notice", metric, metric is null ? null : active.Notice?.Find(metric)?.Value);
    }
}
