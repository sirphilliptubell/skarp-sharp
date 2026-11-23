using SkarpSharp.Findings;
using SkarpSharp.Health;
using SkarpSharp.Model;

namespace SkarpSharp.Agent;

/// <summary>
/// When the Claude hook prints a message whose only news is a file's health (no finding line
/// under its heading). Only when the score moved <see cref="Step"/> or more since the score last shown to the agent for
/// that file (small drifts add up until they reach it), or rose because a finding was fixed; never for a drop that comes
/// only from findings the hook does not list (notices, unless <c>--include-notices</c>).
/// </summary>
public static class HealthNote
{
    /// <summary>The move (in shown points, one decimal) that makes a health-only message worth a turn.</summary>
    public const double Step = 0.5;

    /// <summary>Whether a health-only message is due: <paramref name="reference"/> is the score last shown (or the score
    /// the file started from), <paramref name="now"/> the score now.</summary>
    public static bool Due(double reference, double now, bool noticeOnly, bool fixedAny)
    {
        double r = HealthText.Shown(reference), n = HealthText.Shown(now);
        if (n > r && fixedAny) return true;
        if (n < r && noticeOnly) return false;
        return Math.Round(Math.Abs(n - r), 1) >= Step;
    }

    /// <summary>Whether the score of the findings the hook lists (warnings; every finding with notices on) did not drop
    /// between <paramref name="before"/> and <paramref name="current"/>: a drop then came only from unlisted findings.</summary>
    public static bool NoticeOnly(IScoreModel model, IEnumerable<KeyValuePair<string, TrackedFinding>> before, FileCheckResult current, Severity min)
    {
        bool all = min == Severity.Notice;
        var then = new List<ScoredFinding>();
        foreach (var (id, f) in before)
            if (f.State == "open" && (all || f.Severity == "warning")) then.Add(ScoredFinding.From(id, f.Rule, f.Severity, f.Metric, f.Value, f.Threshold));
        var now = new List<ScoredFinding>();
        foreach (var f in current.Findings) if (all || f.Severity == "warning") now.Add(ScoredFinding.From(f));
        int src = current.File?.Lines.Source ?? 0;
        return HealthText.Shown(HealthEngine.SyntaxScore(model, now, src).Score) >= HealthText.Shown(HealthEngine.SyntaxScore(model, then, src).Score);
    }

    /// <summary>The Stop's inputs to <see cref="Due"/> against a starting point's findings: whether the listed findings'
    /// score did not drop, and whether a start finding is gone.</summary>
    public static (bool NoticeOnly, bool Fixed) Against(IScoreModel model, List<ScoredFinding> start, FileCheckResult current, Severity min)
    {
        bool all = min == Severity.Notice;
        var present = new HashSet<string>(StringComparer.Ordinal);
        var now = new List<ScoredFinding>();
        foreach (var f in current.Findings)
        {
            present.Add(f.Id);
            if (all || f.Severity == "warning") now.Add(ScoredFinding.From(f));
        }
        var then = start.FindAll(f => all || f.Severity == "warning");
        int src = current.File?.Lines.Source ?? 0;
        bool noticeOnly = HealthText.Shown(HealthEngine.SyntaxScore(model, now, src).Score) >= HealthText.Shown(HealthEngine.SyntaxScore(model, then, src).Score);
        return (noticeOnly, start.Exists(f => !present.Contains(f.Id)));
    }

    /// <summary>
    /// The Stop's health-only rule, after the lines already seen are left out: a file's health line with no finding line
    /// of its file left is kept only when <see cref="Due"/> from the score last shown (else the start's score).
    /// <paramref name="rules"/>: by root and path, the inputs from <see cref="Against"/>; a file without them keeps its line.
    /// </summary>
    public static void AtStop(CheckOutcome? outcome, SortedDictionary<string, string>? shown, Dictionary<string, (bool NoticeOnly, bool Fixed)> rules)
    {
        if (outcome is null) return;
        for (int i = outcome.Health.Count - 1; i >= 0; i--)
        {
            var h = outcome.Health[i].Line;
            if (h.Value is not double now || !rules.TryGetValue(h.Root + "|" + h.Path, out var rule)) continue;
            if (outcome.Lines.Exists(l => l.Path == h.Path && l.Root == h.Root)) continue;
            double? reference = (shown is null ? null : ShownLines.Score(shown, h.Id)) ?? h.From;
            if (reference is double r && !Due(r, now, rule.NoticeOnly, rule.Fixed)) outcome.Health.RemoveAt(i);
        }
    }

    /// <summary>Whether a finding open in <paramref name="before"/> is gone from <paramref name="current"/>.</summary>
    public static bool Fixed(IEnumerable<KeyValuePair<string, TrackedFinding>> before, FileCheckResult current)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in current.Findings) present.Add(f.Id);
        foreach (var (id, f) in before) if (f.State == "open" && !present.Contains(id)) return true;
        return false;
    }
}

/// <summary>
/// Where one file's lines start in a check's outcome, so the file's health line can be judged by
/// <see cref="HealthNote"/> once its finding lines are known.
/// </summary>
public readonly struct HealthMark(CheckOutcome outcome)
{
    readonly CheckOutcome _outcome = outcome;
    readonly int _lines = outcome.Lines.Count, _health = outcome.Health.Count;

    /// <summary>
    /// Keeps, restates or drops the file's health line: with no finding line after the mark, <paramref name="due"/> (the
    /// hook's rule; null: no rule) decides from <paramref name="reference"/>; a kept health-only line then states the move
    /// from that score. Returns the score the reader has now seen for the file (the reference when nothing was shown).
    /// </summary>
    public double? Apply(double? reference, double now, Func<double, bool>? due)
    {
        if (_outcome.Health.Count == _health) return reference;
        if (due is not null && _outcome.Lines.Count == _lines && reference is double r)
        {
            if (!due(r)) { _outcome.Health.RemoveAt(_health); return reference; }
            var (line, _) = _outcome.Health[_health];
            double shown = HealthText.Shown(now);
            if (shown == r) { _outcome.Health.RemoveAt(_health); return reference; }
            _outcome.Health[_health] = (line with { From = r, NewFile = false }, shown - r);
        }
        return HealthText.Shown(now);
    }
}
