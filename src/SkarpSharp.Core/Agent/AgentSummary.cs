using SkarpSharp.Findings;
using SkarpSharp.Model;

namespace SkarpSharp.Agent;

/// <summary>
/// The findings of an analysis that are new or worse than in the baseline (without a baseline: the warnings), as check
/// lines (the push hook's report reads them). A composite (brain-method, brain-class) is one line in place of its parts;
/// A warning in the gate set (<c>gate.on</c>) is marked, and a composite blocks only when its parts trip the gate.
/// </summary>
public static class AgentSummary
{
    /// <summary>The lines, unordered and unfolded. <paramref name="gateOn"/>: the gate set (null or empty: the default).</summary>
    public static List<AgentLine> Entries(AnalysisResult result, Severity minSeverity, IReadOnlyList<string>? gateOn)
    {
        var set = gateOn ?? [];
        bool baseline = result.Baseline is not null;
        var lines = new List<AgentLine>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        IEnumerable<Finding> all = BrainRules.Collapse(result.Findings ?? [], result.Composites);
        // Semantic findings: the highest target's document only, the joining rule of skarp show.
        if (Join.Highest(result.SemanticTier?.Results ?? []) is { } r) all = all.Concat(r.Findings);
        foreach (var f in all)
        {
            if (!seen.Add(f.Id)) continue;
            if (minSeverity == Severity.Warning && f.Severity != "warning") continue;
            if (Line(f, baseline) is not { } line) continue;
            lines.Add(Labelled(line, f, set));
        }
        return lines;
    }

    /// <summary>A finding's line: worsened or new against the baseline, or (no baseline) a warning; else null.</summary>
    static AgentLine? Line(Finding f, bool baseline) => (baseline, f.BaselineState) switch
    {
        (true, "worsened") => AgentFormat.For(f, LineKind.Worse, f.BaselineValue),
        (true, "new") => AgentFormat.For(f, LineKind.New),
        (false, _) when f.Severity == "warning" => AgentFormat.For(f, LineKind.Present),
        _ => null,
    };

    /// <summary>The line's gate-set membership; a composite blocks only when its parts trip the gate.</summary>
    static AgentLine Labelled(AgentLine line, Finding f, IReadOnlyList<string> set) => line with
    {
        Gated = Gate.Severe(set, f.Rule, f.Severity),
        PartsBlock = f.Parts?.Exists(p => Gate.Severe(set, p.Rule, p.Severity) && p.BaselineState is "new" or "worsened"),
    };
}
