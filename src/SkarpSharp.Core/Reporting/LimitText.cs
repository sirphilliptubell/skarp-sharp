using SkarpSharp.Findings;
using SkarpSharp.Health;
using SkarpSharp.Model;

namespace SkarpSharp.Reporting;

/// <summary>
/// <c>skarp analyze --gate</c>'s health limits in the text format: the verdict's health part and the
/// files past a limit, each under a heading that says which limit (<c>src/A.cs health 8.3 -&gt; 7.1 (down 1.2, limit under
/// 1.0)</c>) with its new or worse findings, notices included, by line, within <c>output.gateMaxLines</c>.
/// </summary>
public static class LimitText
{
    /// <summary>The gate verdict: <c>Skarp: gate failed: 1 finding new or worse; health past its limit in 2 files (gate
    /// set *:warning; health: a drop under 1.0, new files at least 6.5, no drop below 6.5)</c>.</summary>
    public static string GateVerdict(GateResult g)
    {
        if (!g.Tripped) return "Skarp: gate passed";
        int findings = g.New + g.Worsened, files = g.Health?.Files.Count ?? 0;
        var set = $"gate set {string.Join(", ", g.On)}";
        if (files == 0) return $"Skarp: gate failed: {TextFormat.Count(findings, "finding")} new or worse ({set})";
        var limits = $"health: {Limits(g.Health!).Describe()}";
        return findings == 0
            ? $"Skarp: gate failed: {HealthLimit.Verdict(files)} ({limits})"
            : $"Skarp: gate failed: {TextFormat.Count(findings, "finding")} new or worse; {HealthLimit.Verdict(files)} ({set}; {limits})";
    }

    /// <summary>The limits of a gate result.</summary>
    public static HealthLimits Limits(GateHealth h) => new(h.MaxDrop, h.NewFileMin, h.Floor);

    /// <summary>The health part of <see cref="Gate.Describe"/>: <c>health past its limit in 1 file: src/A.cs health 8.3 -&gt;
    /// 7.1 (down 1.2, limit under 1.0)</c>; null without a file past a limit.</summary>
    public static string? Describe(GateHealth? h)
    {
        if (h is not { Files.Count: > 0 }) return null;
        const int Named = 5;
        var named = new List<string>();
        foreach (var b in AnalysisLimits.Breaches(h))
            if (named.Count < Named) named.Add(HealthLimit.Heading(b.Path, b));
        var more = h.Files.Count > Named ? $", +{h.Files.Count - Named} more" : "";
        return $"{HealthLimit.Verdict(h.Files.Count)}: {string.Join(", ", named)}{more}";
    }

    /// <summary>
    /// The files past a limit, each with its heading and its new or worse findings (both tiers' when the score counts
    /// both), by line; at most <paramref name="budget"/> finding lines in all, the rest folded per file. Returns the paths
    /// written (the file list after them leaves them out).
    /// </summary>
    public static HashSet<string> WriteFiles(TextWriter w, AnalysisResult r, int budget, ref bool scale, Action<Finding> shown)
    {
        var written = new HashSet<string>(StringComparer.Ordinal);
        if (r.Gate?.Health is not { Files.Count: > 0 } h) return written;
        var findings = new List<Finding>(BrainRules.Collapse(r.Findings ?? [], r.Composites));
        if (r.Health?.Kind == HealthEngine.Full && Highest(r.SemanticTier?.Results ?? []) is { } semantic) findings.AddRange(semantic.Findings);
        int left = Math.Max(1, budget);
        foreach (var b in AnalysisLimits.Breaches(h))
        {
            written.Add(b.Path);
            w.WriteLine();
            var heading = HealthLimit.Heading(b.Path, b);
            w.WriteLine(scale ? heading : heading + TextFormat.Scale);
            scale = true;
            var own = findings.FindAll(f => f.Path == b.Path && f.BaselineState is "new" or "worsened");
            own.Sort((x, y) => x.Line.CompareTo(y.Line));
            int n = Math.Min(left, own.Count);
            for (int i = 0; i < n; i++)
            {
                w.WriteLine(TextFormat.FindingLine(own[i], true));
                shown(own[i]);
            }
            left -= n;
            if (own.Count > n) w.WriteLine(TextFormat.MoreInFile(own.Count - n));
        }
        return written;
    }

    /// <summary>The semantic document of the highest target framework (the one a <c>full</c> score counts), or null.</summary>
    static SemanticResult? Highest(IReadOnlyList<SemanticResult> results)
    {
        SemanticResult? best = null;
        foreach (var r in results)
            if (best is null || (r.Run.TargetFramework is { } t && best.Run.TargetFramework is { } bt && Semantic.TargetFrameworks.Key(t).CompareTo(Semantic.TargetFrameworks.Key(bt)) > 0)) best = r;
        return best;
    }
}
