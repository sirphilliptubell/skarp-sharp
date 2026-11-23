using SkarpSharp.Model;

namespace SkarpSharp.Findings;

/// <summary>
/// The opt-in gate: trips when the run has findings that are new or worsened against the baseline and
/// whose rule and severity are in the gate set. The set is a list of <c>rule:severity</c> pairs; <c>*</c> stands for any
/// rule or any severity. Default: <c>*:warning</c> (every rule, warnings only).
/// </summary>
public static class Gate
{
    public const string DefaultPair = "*:warning";

    /// <summary>Exit code of a run whose gate tripped (0 done, 1 tool error, 2 usage error).</summary>
    public const int TrippedExitCode = 3;

    /// <summary>Checks one pair; returns an error message or null.</summary>
    public static string? Validate(string pair)
    {
        int colon = pair.IndexOf(':');
        if (colon <= 0 || colon == pair.Length - 1)
            return $"'{pair}' is not <rule>:<severity> (for example *:warning or long-method:notice)";
        var rule = pair[..colon];
        var sev = pair[(colon + 1)..];
        if (rule != "*" && RuleCatalog.Find(rule) is null)
            return $"'{pair}': unknown rule '{rule}' (known: *, {string.Join(", ", RuleCatalog.Every.Where(r => r.Id != RuleIds.UsedOnlyByTests).Select(r => r.Id))})";
        if (RuleCatalog.IsComposite(rule))
            return $"'{pair}': {rule} is a composite: the gate judges its parts (long-method, complex-method, deep-nesting, large-type), not the composite";
        if (rule == RuleIds.UsedOnlyByTests)
            return $"'{pair}': {RuleIds.UsedOnlyByTests} is a label and is never gated";
        if (sev is not ("notice" or "warning" or "*"))
            return $"'{pair}': severity '{sev}' is not notice, warning or *";
        return null;
    }

    /// <summary>True when the finding's rule and severity are in the set.</summary>
    public static bool Matches(IReadOnlyList<string> pairs, Finding f) => Matches(pairs, f.Rule, f.Severity);

    /// <summary>True when the rule and severity are in the set (an empty set: the default, <see cref="DefaultPair"/>).</summary>
    public static bool Matches(IReadOnlyList<string> pairs, string rule, string severity)
    {
        if (pairs.Count == 0) return rule != RuleIds.UsedOnlyByTests && severity == "warning";
        foreach (var p in pairs)
        {
            int colon = p.IndexOf(':');
            var r = p[..colon];
            var sev = p[(colon + 1)..];
            if ((r == "*" || r == rule) && (sev == "*" || sev == severity)) return true;
        }
        return false;
    }

    /// <summary>A warning in the gate set, labelled <c>severe</c> or <c>blocked</c> in the agent format.</summary>
    public static bool Severe(IReadOnlyList<string> pairs, string rule, string severity) =>
        severity == "warning" && rule != RuleIds.UsedOnlyByTests && Matches(pairs, rule, severity);

    /// <summary>Evaluates the gate over findings already marked against a baseline.</summary>
    public static GateResult Evaluate(IReadOnlyList<Finding> findings, IReadOnlyList<string> pairs)
    {
        var set = pairs.Count == 0 ? [DefaultPair] : pairs;
        var r = new GateResult { On = [.. set] };
        foreach (var f in findings)
        {
            // The used-only-by-tests label is never gated, not even counted as outside the gate set.
            if (f.BaselineState is not ("new" or "worsened") || f.Rule == RuleIds.UsedOnlyByTests) continue;
            if (!Matches(set, f)) { r.Outside++; continue; }
            if (f.BaselineState == "new") r.New++; else r.Worsened++;
        }
        r.Tripped = r.New + r.Worsened > 0;
        return r;
    }

    /// <summary>One line for the report: the result, the counts and how to accept the changes.</summary>
    /// <param name="health">The health part (<c>Reporting.LimitText.Describe</c>), or null.</param>
    public static string Describe(GateResult g, string? health = null)
    {
        var set = string.Join(", ", g.On);
        var outside = g.Outside > 0 ? $"; {g.Outside} other new or worsened finding{(g.Outside == 1 ? "" : "s")} outside the gate set" : "";

        var findings = g.New + g.Worsened == 0 && health is not null ? health
            : $"{g.New} new and {g.Worsened} worsened finding{(g.New + g.Worsened == 1 ? "" : "s")} in the gate set ({set}){outside}{(health is null ? "" : "; " + health)}";
        return g.Tripped
            ? $"gate tripped: {findings}. Fix them, or accept them with `skarp baseline` and commit .skarp-baseline.json (exit code 3)."
            : $"gate passed: no new or worsened findings in the gate set ({set}){outside}.";
    }
}
