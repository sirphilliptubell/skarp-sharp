using SkarpSharp.Model;

namespace SkarpSharp.Health;

/// <summary>
/// <c>skarp analyze --gate</c>'s health limits over the scored files, against the baseline. The score
/// compared is the one the run shows: <c>full</c> with a semantic baseline, else <c>syntax</c> (with the semantic tier on
/// but no semantic baseline, the syntax score against the syntax baseline, with a note). A file the baseline holds is
/// judged by the drop and floor rules, a file it does not hold by the new-file rule. The project average never
/// gates.
/// </summary>
public static class AnalysisLimits
{
    public const string NoSemanticBaselineNote = "no semantic baseline, so the health limits compare the syntax scores with the baseline";

    public static GateHealth Judge(AnalysisResult result, HealthInputs inputs, HealthLimits limits)
    {
        var gate = new GateHealth { MaxDrop = limits.MaxDrop, NewFileMin = limits.NewFileMin, Floor = limits.Floor };
        if (!limits.Any || result.Health is null || inputs.SyntaxBaseline is null) return gate;
        var notes = new List<string>();
        Dictionary<string, double>? syntax = null;
        if (inputs.Semantic is not null && inputs.SemanticBaseline is null)
        {
            syntax = HealthEngine.BaselineScores(ScoreModels.Create(inputs.Settings), inputs, result, full: false) ?? [];
            notes.Add(NoSemanticBaselineNote);
        }
        var held = HealthEngine.HeldFiles(inputs);
        var breaches = new List<HealthBreach>();
        foreach (var file in HealthEngine.Scored(result, inputs.IncludeGenerated))
        {
            if (file.Health is not { } h) continue;
            var (before, isNew, now) = syntax is null ? (h.BaselineScore, h.InBaseline == false, h.Score) : Syntax(file.Path, h, syntax, held);
            if (!isNew && before is null) continue;
            if (HealthLimit.Of(file.Path, before, now, isNew, limits) is { } b) breaches.Add(b);
        }
        breaches.Sort((a, b) => Drop(b).CompareTo(Drop(a)) is var c && c != 0 ? c : string.CompareOrdinal(a.Path, b.Path));
        foreach (var b in breaches) gate.Files.Add(new GateHealthFile { Path = b.Path, Before = b.Before, Now = b.Now, Rule = b.Rule });
        if (notes.Count > 0) gate.Note = string.Join("; ", notes);
        return gate;
    }

    /// <summary>The file's syntax score and its syntax baseline score (the fallback without a semantic baseline).</summary>
    static (double? Before, bool IsNew, double Now) Syntax(string path, FileHealth h, Dictionary<string, double> baseline, IReadOnlySet<string> held)
    {
        double now = h.SyntaxScore ?? h.Score;
        return held.Contains(path) ? (baseline.GetValueOrDefault(path, HealthEngine.BaselineDefault), false, now) : (null, true, now);
    }

    /// <summary>The drop of a breach (a new file: from 10), for the order.</summary>
    static double Drop(HealthBreach b) => (b.Before ?? HealthEngine.BaselineDefault) - b.Now;

    /// <summary>The breaches of a gate result as <see cref="HealthBreach"/> (reports), with the result's limits.</summary>
    public static List<HealthBreach> Breaches(GateHealth? gate)
    {
        var list = new List<HealthBreach>();
        if (gate is null) return list;
        var limits = new HealthLimits(gate.MaxDrop, gate.NewFileMin, gate.Floor);
        foreach (var f in gate.Files) list.Add(new HealthBreach(f.Path, f.Before, f.Now, f.Rule, limits));
        return list;
    }
}
