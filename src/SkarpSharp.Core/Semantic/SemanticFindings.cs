using System.Globalization;
using SkarpSharp.Findings;
using SkarpSharp.Model;

namespace SkarpSharp.Semantic;

/// <summary>
/// The semantic rules (<c>high-coupling</c>, <c>god-class</c> and the unused-code rules) over the measured types: findings
/// with the same identity scheme as the syntax tier (<c>rule|path|type</c>) and the same report order.
/// </summary>
public static class SemanticFindings
{
    public static double? TypeMetric(SemanticType t, string metric) => metric switch
    {
        "cbo" => t.Cbo,
        "cboAll" => t.CboAll,
        "cboMicrosoft" => t.CboMicrosoft,
        _ => null,
    };

    /// <param name="rulesOf">The settings of a type's or candidate's file (its first declaration by path,
    /// the file the syntax tier reports it in); null: <paramref name="settings"/> everywhere. namespace-cycle and the run
    /// header keep <paramref name="settings"/>, the repository's.</param>
    public static void Apply(SemanticResult result, RuleSettings settings, IReadOnlyDictionary<string, int>? commits,
        IReadOnlyList<UnusedVerdict>? unused = null, Func<string, RuleSettings>? rulesOf = null)
    {
        var findings = new List<Finding>();
        RuleSettings Of(string path) => rulesOf is null ? settings : rulesOf(path);
        foreach (var t in result.Types)
        {
            var rule = Of(t.Path)[RuleIds.HighCoupling];
            if (Rules.Judge(rule, k => TypeMetric(t, k)) is not { } v) continue;
            var f = Rules.Make(RuleIds.HighCoupling, v, rule, t.Path, t.Line, t.Name, Message(t, v.Condition.Metric));
            f.PlaceKey = t.Name;
            f.TypePlace = true;
            findings.Add(f);
        }
        foreach (var t in result.Types)
            if (GodClassRule.Judge(t, Of(t.Path)[RuleIds.GodClass]) is { } g) findings.Add(g);
        findings.AddRange(UnusedCode.Findings(unused ?? [], settings, rulesOf));
        findings.AddRange(NamespaceCycles.Findings(result.Namespaces, settings[RuleIds.NamespaceCycle]));
        if (commits is not null)
            foreach (var f in findings) f.Commits = commits.GetValueOrDefault(f.Path);
        FindingEngine.AssignIds(findings);
        FindingEngine.Sort(findings);
        result.Findings = findings;
        result.FindingCounts = Counts(findings);
    }

    static string Message(SemanticType t, string metric)
    {
        string N(int v) => v.ToString(CultureInfo.InvariantCulture);
        return metric switch
        {
            "cboAll" => $"uses {N(t.CboAll)} other types, framework types counted ({N(t.Cbo)} without them)",
            "cboMicrosoft" => $"class coupling {N(t.CboMicrosoft)} by Microsoft's counting ({N(t.Cbo)} without framework types)",
            _ => $"uses {N(t.Cbo)} other types, framework types not counted ({N(t.CboAll)} with them)",
        };
    }

    public static List<FindingCount> Counts(IEnumerable<Finding> findings) => FindingEngine.Counts(findings, RuleCatalog.Semantic);
}
