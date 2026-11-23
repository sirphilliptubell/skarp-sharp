using SkarpSharp.Findings;
using SkarpSharp.Model;

namespace SkarpSharp.Health;

/// <summary>
/// One finding as a score model sees it: identity, rule, severity, and the measure against its threshold (the ratio
/// orders a rule's findings worst first). Built from a full finding, a tracking entry or a baseline entry.
/// </summary>
public sealed record ScoredFinding(string Id, string Rule, string Severity, double Ratio)
{
    public static ScoredFinding From(Finding f) => new(f.Id, f.Rule, f.Severity, FindingEngine.Ratio(f));

    public static ScoredFinding From(string id, string rule, string severity, string? metric, double? value, double? threshold) =>
        new(id, rule, severity, FindingEngine.Ratio(new Finding { Metric = metric, Value = value, Threshold = threshold }));
}

/// <summary>What a score model is given for one file: its findings (one tier or both) and its source line count.</summary>
public sealed record ScoreInput(IReadOnlyList<ScoredFinding> Findings, int SourceLines);

/// <summary>A model's verdict on one file.</summary>
public sealed record ScoreResult(double Score, double Penalty, IReadOnlyList<HealthContribution> Contributions)
{
    public string Band => Bands.Of(Score);
}

/// <summary>
/// A code health score model: per file, findings in, a score with its contributions out. The seam for
/// later models (debt ratio, Maintainability Index) and scoring plugins; only <see cref="GradeModel"/> exists.
/// </summary>
public interface IScoreModel
{
    /// <summary>The value of <c>--score-model</c> / <c>score.model</c>.</summary>
    string Name { get; }
    ScoreResult Score(ScoreInput input);
}

/// <summary>The models <c>--score-model</c> can choose.</summary>
public static class ScoreModels
{
    public static readonly IReadOnlyList<string> Names = [GradeModel.ModelName];

    public static bool IsKnown(string name) => Names.Contains(name, StringComparer.Ordinal);

    public static IScoreModel Create(ScoreSettings s) => s.Model switch
    {
        GradeModel.ModelName => new GradeModel(HealthWeights.Resolve(s.Weights), s.Decay),
        _ => throw new ArgumentException($"unknown score model '{s.Model}' (known: {string.Join(", ", Names)})"),
    };
}

/// <summary>Bands of a 1 to 10 score (display only; the Code Red paper's bands with CodeScene's public labels).</summary>
public static class Bands
{
    public const string Healthy = "healthy", Problematic = "problematic", Unhealthy = "unhealthy";

    public static string Of(double score) => score >= 9 ? Healthy : score >= 4 ? Problematic : Unhealthy;
}

/// <summary>
/// The <c>grade</c> model: <c>score = max(1, 10 - sum over rules r of min(cap_r, sum over the
/// rule's findings k = 1..n of base(r, severity_k) x decay_r^(k-1)))</c>, decay_r the rule's own decay (1 for the
/// clone rules) or <c>score.decay</c>. The findings of a rule are taken heaviest
/// first (base weight, then value relative to threshold, then id), which keeps the score monotone: adding a finding
/// never raises it, removing one never lowers it.
/// </summary>
public sealed class GradeModel(IReadOnlyDictionary<string, RuleWeight> weights, double decay) : IScoreModel
{
    public const string ModelName = "grade";
    public string Name => ModelName;

    public ScoreResult Score(ScoreInput input)
    {
        var contributions = new List<HealthContribution>();
        double penalty = 0;
        // Rules in a fixed order, so the double sum (and the score) is the same whatever order the findings come in.
        foreach (var group in input.Findings.GroupBy(f => f.Rule, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (!weights.TryGetValue(group.Key, out var w)) continue;
            penalty += Rule(group.Key, w, [.. group], contributions);
        }
        var ordered = contributions.OrderByDescending(c => c.Points).ThenBy(c => RuleOrder(c.Rule)).ThenBy(c => FindingEngine.SeverityRank(c.Severity)).ToList();
        return new ScoreResult(Math.Max(1, 10 - penalty), penalty, ordered);
    }

    static int RuleOrder(string rule) => RuleCatalog.Find(rule)?.Order ?? int.MaxValue;

    /// <summary>The points one rule takes from the file; adds its contributions (one per severity).</summary>
    double Rule(string rule, RuleWeight w, List<ScoredFinding> findings, List<HealthContribution> contributions)
    {
        var sorted = findings.OrderByDescending(f => w.Base(f.Severity)).ThenByDescending(f => f.Ratio).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
        var bySeverity = new SortedDictionary<string, (int Count, double Raw)>(StringComparer.Ordinal);
        double raw = 0, factor = 1, ruleDecay = w.Decay ?? decay;
        foreach (var f in sorted)
        {
            double cost = w.Base(f.Severity) * factor;
            factor *= ruleDecay;
            raw += cost;
            var (n, r) = bySeverity.GetValueOrDefault(f.Severity);
            bySeverity[f.Severity] = (n + 1, r + cost);
        }
        if (raw <= 0) return 0;
        bool capped = raw > w.Cap;
        double scale = capped ? w.Cap / raw : 1;
        foreach (var (severity, (count, part)) in bySeverity)
            contributions.Add(new HealthContribution { Rule = rule, Severity = severity, Count = count, Points = part * scale, Capped = capped ? true : null });
        return capped ? w.Cap : raw;
    }
}
