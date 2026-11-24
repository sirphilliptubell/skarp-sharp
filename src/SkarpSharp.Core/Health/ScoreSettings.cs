using SkarpSharp.Findings;

namespace SkarpSharp.Health;

/// <summary>A weight override of one rule (<c>score.weights.&lt;rule&gt;</c>): unset values keep the table's.</summary>
public sealed record WeightOverride(double? Notice, double? Warning, double? Cap, double? Decay = null);

/// <summary>The base weight per finding of each severity and the cap per file of one rule, in points of the 9 available;
/// a rule's own decay (null: <c>score.decay</c>).</summary>
public sealed record RuleWeight(double Notice, double Warning, double Cap, double? Decay = null)
{
    public double Base(string severity) => severity == "warning" ? Warning : Notice;
}

/// <summary>The score settings of a run (<c>score</c> in <c>.skarp.json</c>, <c>--score-model</c>).</summary>
public sealed class ScoreSettings
{
    public const string DefaultModel = "grade";
    public const double DefaultDecay = 0.7;
    public const int DefaultWorstCount = 10;
    public const double DefaultWorstBelow = 7;

    public string Model { get; init; } = DefaultModel;
    /// <summary>The k-th finding of a rule in a file costs base x decay^(k-1), worst first.</summary>
    public double Decay { get; init; } = DefaultDecay;
    /// <summary>Overrides by rule id (validated against the rule catalog when read).</summary>
    public IReadOnlyDictionary<string, WeightOverride> Weights { get; init; } = new Dictionary<string, WeightOverride>(StringComparer.Ordinal);
    public int WorstCount { get; init; } = DefaultWorstCount;
    public double WorstBelow { get; init; } = DefaultWorstBelow;

    public static ScoreSettings Default { get; } = new();
}

/// <summary>
/// The weights table: static data (AOT friendly). A rule without
/// a warning weight (a dash in the table) takes its notice weight for warnings, and the other way round.
/// </summary>
public static class HealthWeights
{
    static readonly Dictionary<string, (double Notice, double? Warning, double Cap)> Table = new(StringComparer.Ordinal)
    {
        // Per clone group a file takes part in, once per file; no decay (the cap limits).
        [RuleIds.DuplicateCode] = (0.3, 0.6, 1.5),
        // On by default, weight 0 (the re-check found 50 to 53% of groups worth attention).
        [RuleIds.DuplicateCodeNear] = (0, 0, 0),
        [RuleIds.SuppressionWithoutJustification] = (0, 0, 0),
        [RuleIds.ComplexMethod] = (0.5, 1.0, 3.0),
        [RuleIds.LongMethod] = (0.4, 1.0, 2.5),
        [RuleIds.DeepNesting] = (0.5, null, 2.0),
        [RuleIds.BumpyRoad] = (0.5, null, 2.0),
        [RuleIds.PathExplosion] = (0.3, null, 1.0),
        [RuleIds.ComplexCondition] = (0.3, null, 1.0),
        [RuleIds.TooManyParameters] = (0.15, 0.3, 0.8),
        [RuleIds.LargeType] = (0.2, null, 1.0),
        [RuleIds.LargeFile] = (0.5, 1.5, 1.5),
        [RuleIds.EmptyCatch] = (0.2, 0.8, 1.5),
        [RuleIds.MagicNumber] = (0.05, null, 0.5),
        [RuleIds.GodClass] = (1.5, null, 1.5),
        [RuleIds.HighCoupling] = (0.6, null, 1.2),
        [RuleIds.UnusedMember] = (0.1, null, 0.5),
        [RuleIds.UnusedType] = (0.1, null, 0.5),
        [RuleIds.UnreadMember] = (0.1, null, 0.5),
        [RuleIds.UsedOnlyByTests] = (0, 0, 0),
        [RuleIds.NamespaceCycle] = (0, 0, 0),
        [RuleIds.ActiveComplexFile] = (0, 0, 0),
    };

    /// <summary>Rules with their own decay (the clone rules sum linearly up to their cap).</summary>
    static readonly Dictionary<string, double> RuleDecay = new(StringComparer.Ordinal)
    {
        [RuleIds.DuplicateCode] = 1.0,
        [RuleIds.DuplicateCodeNear] = 1.0,
    };

    /// <summary>The weights in effect: the table with the overrides applied; rules missing from both weigh nothing.</summary>
    public static IReadOnlyDictionary<string, RuleWeight> Resolve(IReadOnlyDictionary<string, WeightOverride> overrides)
    {
        var result = new Dictionary<string, RuleWeight>(StringComparer.Ordinal);
        foreach (var rule in RuleCatalog.Every)
        {
            var (n, w, cap) = Table.GetValueOrDefault(rule.Id, (0, 0, 0));
            var o = overrides.GetValueOrDefault(rule.Id);
            double notice = o?.Notice ?? n;
            result[rule.Id] = new RuleWeight(notice, o?.Warning ?? w ?? notice, o?.Cap ?? cap, o?.Decay ?? DecayOf(rule.Id));
        }
        return result;
    }

    /// <summary>The table's decay of a rule; null means <c>score.decay</c>.</summary>
    public static double? DecayOf(string rule) => RuleDecay.TryGetValue(rule, out var d) ? d : null;

    /// <summary>The table's weight of a rule as the table prints it (<c>-</c> for a severity it has none for).</summary>
    public static (double Notice, double? Warning, double Cap) Defaults(string rule) => Table.GetValueOrDefault(rule, (0, 0, 0));
}
