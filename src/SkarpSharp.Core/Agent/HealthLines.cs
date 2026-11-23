using SkarpSharp.Health;
using SkarpSharp.Model;

namespace SkarpSharp.Agent;

/// <summary>
/// The health changes of <c>skarp check</c> and the <c>Stop</c> summary. A file's
/// <c>syntax</c> score is recomputed from its findings now and from its earlier findings (the tracking entry, git HEAD,
/// the session-start state, the baseline); a change is recorded only when the score at one decimal changed.
/// </summary>
public static class HealthLines
{
    /// <summary>The syntax score of full findings (a check's current or HEAD findings).</summary>
    public static ScoreResult Of(IScoreModel model, IEnumerable<Finding> findings, int sourceLines) =>
        HealthEngine.SyntaxScore(model, findings.Select(ScoredFinding.From), sourceLines);

    /// <summary>The syntax score of a tracking entry's open findings.</summary>
    public static ScoreResult Of(IScoreModel model, IEnumerable<KeyValuePair<string, TrackedFinding>> findings, int sourceLines) =>
        HealthEngine.SyntaxScore(model, findings.Where(f => f.Value.State == "open")
            .Select(f => ScoredFinding.From(f.Key, f.Value.Rule, f.Value.Severity, f.Value.Metric, f.Value.Value, f.Value.Threshold)), sourceLines);

    /// <summary>A file's health change (the text format prints it as the file's heading, <c>src/Orders.cs health 9.0 -&gt;
    /// 8.8</c>); null when the shown score is unchanged.</summary>
    public static (AgentLine Line, double Delta)? Change(string path, ScoreResult before, ScoreResult after)
    {
        double o = HealthText.Shown(before.Score), n = HealthText.Shown(after.Score);
        if (o == n) return null;
        return (AgentLine.Health(path, o, n), n - o);
    }

    /// <summary>The health of a file not in git (heading <c>src/Invoices.cs health 7.4 (new file)</c>); null when it scores 10.</summary>
    public static (AgentLine Line, double Delta)? New(string path, ScoreResult after)
    {
        double n = HealthText.Shown(after.Score);
        if (n >= 10) return null;
        return (AgentLine.Health(path, null, n), n - 10);
    }

    /// <summary>
    /// <c>skarp show --file</c>: each analyzed file's score recomputed from the findings show holds for
    /// it (the saved run joined with later checks); files the saved run did not analyze are left out.
    /// </summary>
    public static List<ShowHealth> ForShow(ShowData data, IEnumerable<string> paths, ScoreSettings settings)
    {
        var model = ScoreModels.Create(settings);
        var list = new List<ShowHealth>();
        foreach (var path in paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!data.SourceLines.TryGetValue(path, out var src) && !data.Hashes.ContainsKey(path)) continue;
            var findings = data.Entries
                // The files a finding's points are taken from, as analyze scores them: a clone group
                // counts in every file holding a copy, not only in the file its id names.
                .Where(e => e.Finding.Severity != "-" && e.Change is not ("resolved" or "removed") && HealthEngine.ScoredPaths(e.Finding).Contains(path, StringComparer.Ordinal))
                .GroupBy(e => e.Finding.Id, StringComparer.Ordinal).Select(g => ScoredFinding.From(g.First().Finding)).ToList();
            var syntax = HealthEngine.SyntaxScore(model, findings, src);
            var score = data.SemanticRan ? model.Score(new ScoreInput(findings, src)) : syntax;
            list.Add(new ShowHealth { Path = path, Health = HealthEngine.Dto(score, data.SemanticRan ? HealthEngine.Full : HealthEngine.Syntax, data.SemanticRan ? syntax.Score : null) });
        }
        return list;
    }
}
