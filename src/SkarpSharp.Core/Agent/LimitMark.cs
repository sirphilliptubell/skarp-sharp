using SkarpSharp.Findings;
using SkarpSharp.Health;

namespace SkarpSharp.Agent;

/// <summary>
/// One file's lines in a check's outcome when the file is past a health limit. Its new or worse
/// findings are listed whatever their severity (the notices are what cost the points, an exception to "warnings only"),
/// marked <see cref="AgentLine.PastLimit"/>, and the breach is recorded for the heading and the verdict. Construct it
/// before the file's lines are added, then <see cref="Apply"/>.
/// </summary>
internal readonly struct LimitMark(CheckOutcome outcome, HealthBreach? breach)
{
    readonly int _start = outcome.Lines.Count;

    /// <summary>The severity the file's lines are listed from: every one when the file is past its limit.</summary>
    public Severity MinSeverity(Severity min) => breach is null ? min : Severity.Notice;

    /// <summary>The file's breach against the baseline, or null.</summary>
    public static HealthBreach? Judge(BaselineScore scores, string path, HealthLimits limits) =>
        HealthLimit.Of(path, scores.Before, scores.Now, scores.IsNew, limits);

    /// <summary>Records the breach and marks the file's new or worse lines added since the mark.</summary>
    public void Apply()
    {
        if (breach is null) return;
        // The current root, as every line added now is stamped with it.
        var root = outcome.Stamp(AgentLine.Health(breach.Path, breach.Before, breach.Now)).Root;
        outcome.Breaches.Add(new HealthBreach(breach.Path, breach.Before, breach.Now, breach.Rule, breach.Limits, root));
        var lines = outcome.Lines;
        for (int i = Math.Min(_start, lines.Count); i < lines.Count; i++)
            if (lines[i].Path == breach.Path && lines[i].Kind is LineKind.New or LineKind.Worse) lines[i] = lines[i] with { PastLimit = true };
    }
}

/// <summary>
/// The Claude hook's edit check against the health limits. The comparison point is the file at
/// the session start (the agent's first check of it: git HEAD, or nothing for a file new to git); only the edit that
/// takes the file past a limit reports it, later edits do not repeat it (the Stop gate blocks until it is fixed).
/// </summary>
internal static class EditLimits
{
    /// <summary>The mark of the file's lines and the settings its edit check runs with: with the gate and a limit crossed
    /// by this edit, every severity is listed.</summary>
    public static (LimitMark Mark, CheckSettings Settings) Start(CheckOutcome outcome, CheckSettings s, FileCheckResult current,
        Dictionary<string, TrackedFinding> before, TrackedFile entry, CheckRun.RootContext ctx)
    {
        var breach = s.JudgeHealth ? Crossed(current, before, entry, ctx) : null;
        return (new LimitMark(outcome, breach), breach is null ? s : s.WithNotices());
    }

    /// <summary>The breach this edit caused, or null (none, or already past before this edit, or a file outside git).</summary>
    public static HealthBreach? Crossed(FileCheckResult current, Dictionary<string, TrackedFinding> before, TrackedFile entry, CheckRun.RootContext ctx)
    {
        if (entry.ComparedWith == "outside-git") return null;
        int src = current.File?.Lines.Source ?? 0;
        var limits = ctx.Options.HealthLimits;
        bool isNew = entry.ComparedWith == "untracked";
        double? start = null;
        if (!isNew)
        {
            var byId = new Dictionary<string, Model.Finding>(StringComparer.Ordinal);
            foreach (var f in current.Findings) byId[f.Id] = f;
            start = HealthEngine.SyntaxScore(ctx.Model, StopPass.OriginFindings(entry, byId, ctx.Rules.For(current.Path, current.File?.TestCode ?? false)), src).Score;
        }
        var now = HealthLines.Of(ctx.Model, current.Findings, src).Score;
        if (HealthLimit.Of(current.Path, start, now, isNew, limits) is not { } breach) return null;
        var previous = HealthLines.Of(ctx.Model, before, src).Score;
        return HealthLimit.Judge(start, previous, isNew, limits) is null ? breach : null;
    }
}

/// <summary>A file's syntax score at the baseline (null for a new file) and now. A struct, not a record:
/// Native AOT size (no generated equality members).</summary>
internal readonly struct BaselineScore(double? before, double now, bool isNew)
{
    public double? Before { get; } = before;
    public double Now { get; } = now;
    public bool IsNew { get; } = isNew;

    /// <summary>The scores of <paramref name="path"/> against a baseline that lists <paramref name="files"/>: a file it
    /// does not list is new.</summary>
    public static BaselineScore Of(List<string> files, string path, double before, double now)
    {
        bool isNew = !files.Contains(path, StringComparer.Ordinal);
        return new BaselineScore(isNew ? null : before, now, isNew);
    }
}
