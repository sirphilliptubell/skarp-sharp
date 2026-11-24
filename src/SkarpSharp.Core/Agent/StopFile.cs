using SkarpSharp.Configuration;
using SkarpSharp.Findings;
using SkarpSharp.Health;
using SkarpSharp.Model;

namespace SkarpSharp.Agent;

/// <summary>
/// The <c>Stop</c> summary's work on one changed file: the file
/// now against every starting point (each agent's session start and the file at git <c>HEAD</c>), its health
/// heading, and the inputs of the health-only rule (<see cref="HealthNote"/>).
/// </summary>
internal sealed class StopPass(CheckSettings s, CheckOutcome outcome, Dictionary<string, List<TrackedFile>> tracked,
    SortedDictionary<string, List<SortedDictionary<string, double?>>> origins, HeadFiles heads)
{
    /// <summary>By root and path, what <see cref="HealthNote.AtStop"/> needs about each file's chosen start.</summary>
    public Dictionary<string, (bool NoticeOnly, bool Fixed)> Rules { get; } = new(StringComparer.Ordinal);

    /// <summary>The lines of one changed file of <paramref name="root"/>; <paramref name="known"/>: the baseline's values.</summary>
    public void File(string root, string full, CheckRun.RootContext ctx, Dictionary<string, double?> known)
    {
        var rel = FileCheck.Relative(root, full);
        var current = CheckRun.Current(full, root, rel, ctx, out var bytes);
        if (current.ExcludedReason is not null) return;
        if (s.Save && ctx.Options.SaveLastRun) CheckRun.Record(root, current, bytes, [], [], s.Now);
        // The file at HEAD is a starting point too, so a file committed since an agent first
        // checked it (a long session) is compared with what is committed, and a file in git is never new.
        var head = heads.Bytes(full) is { } hb ? FileCheck.Analyze(ctx.Options, ctx.Rules, root, rel, FileCheck.Decode(hb)) : null;
        if (head is { ExcludedReason: not null }) head = null;
        var starts = head is null ? origins[full] : [.. origins[full], HeadValues(head)];
        if (StopHealth(current, tracked[full], head, ctx, s.MinSeverity, outcome, out var breach) is { } rule) Rules[root + "|" + rel] = rule;
        // With the gate, a file past a health limit since the session start lists its notices too.
        if (!s.JudgeHealth) breach = null;
        var limit = new LimitMark(outcome, breach);
        var kinds = new Dictionary<string, LineKind?>(StringComparer.Ordinal);
        foreach (var f in current.Findings)
        {
            var line = CheckRun.AgainstOrigin(f, starts, known);
            kinds[f.Id] = line?.Kind;
            if (line is not null && (CheckRun.Shown(f.Severity, s) || breach is not null)) outcome.Add(line);
        }
        CompositeLines.Apply(outcome, current, f => kinds.GetValueOrDefault(f.Id), limit.MinSeverity(s.MinSeverity), null);
        limit.Apply();
    }

    /// <summary>The open findings of a file at HEAD with their values (a Stop starting point).</summary>
    internal static SortedDictionary<string, double?> HeadValues(FileCheckResult head)
    {
        var values = new SortedDictionary<string, double?>(StringComparer.Ordinal);
        foreach (var f in head.Findings) values[f.Id] = f.Value;
        return values;
    }

    /// <summary>
    /// The Stop health line of a file: its score now against its session-start score (the lowest of the agents
    /// that tracked it, as findings are reported only when worse than every agent's start). The
    /// file at HEAD (<paramref name="head"/>, null when not in git HEAD) is one more start; without any known start the
    /// heading shows the score alone. Returns what the health-only rule needs about the chosen start (null: none).
    /// </summary>
    static (bool NoticeOnly, bool Fixed)? StopHealth(FileCheckResult current, List<TrackedFile> tracked, FileCheckResult? head, CheckRun.RootContext ctx,
        Severity min, CheckOutcome outcome, out HealthBreach? breach)
    {
        int src = current.File?.Lines.Source ?? 0;
        var byId = current.Findings.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var now = HealthLines.Of(ctx.Model, current.Findings, src);
        // Only agents whose start is a known score (git HEAD or an earlier record) count; "new"
        // only for a file new to git; a file outside git with no earlier record gets no health line.
        var rules = ctx.Rules.For(current.Path, current.File?.TestCode ?? false);
        var starts = tracked.FindAll(t => t.ComparedWith is not ("untracked" or "outside-git")).ConvertAll(t => OriginFindings(t, byId, rules).ToList());
        if (head is { ExcludedReason: null }) starts.Add(head.Findings.ConvertAll(ScoredFinding.From));
        if (starts.Count == 0)
        {
            var created = tracked.Exists(t => t.ComparedWith == "untracked") ? HealthLines.New(current.Path, now) : null;
            breach = created is not null ? HealthLimit.Of(current.Path, null, now.Score, true, ctx.Options.HealthLimits) : null;
            if (created is { } c) outcome.AddHealth(c);
            else outcome.AddScore(current.Path, now.Score, now.Score);
            return null;
        }
        var start = starts.MinBy(f => HealthEngine.SyntaxScore(ctx.Model, f, src).Score)!;
        breach = HealthLimit.Of(current.Path, HealthEngine.SyntaxScore(ctx.Model, start, src).Score, now.Score, false, ctx.Options.HealthLimits);
        if (HealthLines.Change(current.Path, HealthEngine.SyntaxScore(ctx.Model, start, src), now) is { } l) outcome.AddHealth(l);
        return HealthNote.Against(ctx.Model, start, current, min);
    }

    /// <summary>A tracked file's session-start findings with their severities (rebuilt as for a baseline when the tracking
    /// file predates <see cref="TrackedFile.OriginSeverities"/>).</summary>
    internal static IEnumerable<ScoredFinding> OriginFindings(TrackedFile t, Dictionary<string, Finding> current, RuleSettings rules)
    {
        if (t.Origin is null)
        {
            foreach (var (id, f) in t.Findings.Where(f => f.Value.State == "open"))
                yield return ScoredFinding.From(id, f.Rule, f.Severity, f.Metric, f.Value, f.Threshold);
            yield break;
        }
        foreach (var (id, value) in t.Origin)
        {
            var rule = id[..Math.Max(0, id.IndexOf('|'))];
            var now = current.GetValueOrDefault(id);
            var (severity, metric, threshold) = HealthBaseline.Judge(rule, value, now, rules);
            yield return ScoredFinding.From(id, rule, t.OriginSeverities?.GetValueOrDefault(id) ?? severity, metric, value, threshold);
        }
    }
}
