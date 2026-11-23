using SkarpSharp.Baselines;
using SkarpSharp.Configuration;
using SkarpSharp.Findings;
using SkarpSharp.Health;
using SkarpSharp.History;
using SkarpSharp.Model;

namespace SkarpSharp.Agent;

/// <summary>The settings of one <c>skarp check</c> run (command-line flags and hook input).</summary>
public sealed class CheckSettings
{
    public bool Track { get; init; }
    /// <summary><c>--gate</c> (the Stop summary then keeps every blocking line).</summary>
    public bool Gate { get; init; }
    public string Session { get; init; } = TrackingStore.NoSession;
    public string Agent { get; init; } = "main";
    public string? AgentType { get; init; }
    public Severity MinSeverity { get; init; } = Severity.Warning;
    /// <summary>False with <c>--no-save</c>; otherwise the root's <c>saveLastRun</c> setting decides.</summary>
    public bool Save { get; init; } = true;
    /// <summary>Flags passed to the settings of every root (<c>--config</c>, <c>--no-config</c>, <c>--baseline</c>, <c>--no-baseline</c>).</summary>
    public IReadOnlyList<string> SettingsArgs { get; init; } = [];
    public required string Home { get; init; }
    public required DateTimeOffset Now { get; init; }
    /// <summary>The system and personal settings files (tests); null: <see cref="SettingsLocations.Default"/>.</summary>
    public SettingsLocations? Locations { get; init; }
    /// <summary>The Claude hook (<c>--hook claude</c>): messages with only health in them follow its rule
    /// (<see cref="HealthNote"/>).</summary>
    public bool Hook { get; init; }
    /// <summary>Judge each file's health limits (<c>gate.health.*</c> of its root): with the gate, and in
    /// MCP <c>check</c>, which states them as facts.</summary>
    public bool JudgeHealth { get; init; }
    /// <summary>Variables for the git processes that read <c>HEAD</c> (tests isolate git); null: inherited only.</summary>
    public IReadOnlyDictionary<string, string?>? GitEnvironment { get; init; }

    /// <summary>The same settings listing every severity (a file past its health limit lists its notices).</summary>
    public CheckSettings WithNotices() => new()
    {
        Track = Track, Gate = Gate, Session = Session, Agent = Agent, AgentType = AgentType, MinSeverity = Severity.Notice, Save = Save,
        SettingsArgs = SettingsArgs, Home = Home, Now = Now, Locations = Locations, Hook = Hook, JudgeHealth = JudgeHealth, GitEnvironment = GitEnvironment,
    };
}

/// <summary>A usage problem found while checking (an invalid settings file, a bad flag value): exit code 2.</summary>
public sealed class CheckUsageException(string message) : Exception(message);

/// <summary>What a check found: the lines (unordered, unfolded), whether the gate set was hit, and the budget.</summary>
public sealed class CheckOutcome
{
    public List<AgentLine> Lines { get; } = [];
    /// <summary>Notes for standard error (files left out); never part of the agent output.</summary>
    public List<string> Notes { get; } = [];
    /// <summary>Notes printed before the findings (for example no baseline).</summary>
    public List<string> Header { get; } = [];
    /// <summary>The line budget from the first root's settings (<c>output.checkMaxLines</c>).</summary>
    public int? MaxLines { get; set; }
    /// <summary>The Stop summary's budget with <c>--gate</c> (<c>output.gateMaxLines</c>).</summary>
    public int? GateMaxLines { get; set; }
    /// <summary>The outcome of the Stop summary (not of an edit check).</summary>
    public bool EndOfTurn { get; init; }

    /// <summary>The budget of finding lines: an edit check's (<c>output.checkMaxLines</c>), or the Stop summary's
    /// (<see cref="Configuration.SkarpOptions.StopMaxLines"/>, with <c>--gate</c> <c>output.gateMaxLines</c>).</summary>
    public int Budget(bool gate) => !EndOfTurn ? MaxLines ?? Configuration.SkarpOptions.DefaultCheckMaxLines
        : gate ? GateMaxLines ?? Configuration.SkarpOptions.DefaultGateMaxLines : Configuration.SkarpOptions.StopMaxLines;
    /// <summary>True when no baseline was found in baseline mode (the gate needs one).</summary>
    public bool MissingBaseline { get; set; }
    /// <summary>Health lines with their score change (printed first, the worst drop first).</summary>
    public List<(AgentLine Line, double Delta)> Health { get; } = [];
    /// <summary>A composite's part lines (replaced by the composite's line) had a new or worse warning.</summary>
    public bool PartsTripped { get; set; }
    /// <summary>Ids of the composite lines (the gate judges their parts, not them).</summary>
    public HashSet<string> CompositeIds { get; } = new(StringComparer.Ordinal);
    /// <summary>By composite id, the part lines its line replaced (what an edit check printed with it, and
    /// what decides whether the Stop summary repeats it).</summary>
    public Dictionary<string, List<AgentLine>> CompositeParts { get; } = new(StringComparer.Ordinal);
    /// <summary>A gate-set warning that is new or worse (a composite: its parts) trips the gate.
    /// So does a file past its health limit.</summary>
    public bool GateTripped => PartsTripped || Breaches.Count > 0 || Lines.Any(l => l.Blocks && !CompositeIds.Contains(l.Id));

    /// <summary>The files past a health limit (with the gate, and in MCP <c>check</c>).</summary>
    public List<Health.HealthBreach> Breaches { get; } = [];



    string? _root;
    IReadOnlyList<string> _gateOn = [];

    /// <summary>The root the lines added next belong to, its gate set, and (from the first root) the budgets.</summary>
    public void Enter(string root, Configuration.SkarpOptions o)
    {
        MaxLines ??= o.CheckMaxLines;
        GateMaxLines ??= o.GateMaxLines;
        _root = root;
        _gateOn = o.GateOn;
    }

    /// <summary>A line with the current root and its gate-set membership.</summary>
    public AgentLine Stamp(AgentLine line) => line with { Root = _root, Gated = line.Kind != LineKind.Health && Gate.Severe(_gateOn, line.Rule, line.Severity) };

    /// <summary>Adds a finding line (stamped).</summary>
    public void Add(AgentLine line) => Lines.Add(Stamp(line));

    /// <summary>Adds a health line (stamped with the root).</summary>
    public void AddHealth((AgentLine Line, double Delta) health) => Health.Add((Stamp(health.Line), health.Delta));

    /// <summary>The findings behind the lines (a composite: its stand-in with the parts), by root and id, for
    /// the commit hook's report; never printed by the agent format.</summary>
    public Dictionary<string, Finding> Sources { get; } = new(StringComparer.Ordinal);

    /// <summary>Each checked file's syntax score before (the baseline's) and now, whether it changed or not.</summary>
    public List<HookHealth> Scores { get; } = [];

    static string SourceKey(string? root, string id) => $"{root}|{id}";

    /// <summary>Records the finding behind a line of the current root.</summary>
    public void AddSource(Finding f) => Sources[SourceKey(_root, f.Id)] = f;

    /// <summary>The finding behind <paramref name="line"/>, or null.</summary>
    public Finding? SourceOf(AgentLine line) => Sources.GetValueOrDefault(SourceKey(line.Root, line.Id));

    /// <summary>Records a file's score of the current root.</summary>
    public void AddScore(string path, double? before, double now) => Scores.Add(new HookHealth(path, before, now, _root));
}

/// <summary>
/// <c>skarp check &lt;file&gt;...</c>: the syntax findings of each file's current
/// content, compared with the baseline (without <c>--track</c>) or with the file's earlier state in this session (with
/// <c>--track</c>: the tracking file, an earlier day's record, git <c>HEAD</c>, or nothing).
/// </summary>
public static class CheckRun
{
    internal sealed record RootContext(SkarpOptions Options, PathRules Rules, IScoreModel Model);

    internal static RootContext Context(string root, CheckSettings s, Dictionary<string, RootContext> cache)
    {
        if (cache.TryGetValue(root, out var c)) return c;
        var parsed = CommandLine.Parse(["analyze", root, "--no-history", "--no-semantic", .. s.SettingsArgs], s.Locations);
        if (parsed.Options is not { } o) throw new CheckUsageException(parsed.Error ?? "invalid settings");
        var pr = o.PathRules();
        cache[root] = c = new RootContext(o, pr, ScoreModels.Create(o.Score));
        return c;
    }

    public static async Task<CheckOutcome> RunAsync(IReadOnlyList<string> fullPaths, CheckSettings s)
    {
        var outcome = new CheckOutcome();
        var roots = new Dictionary<string, RootContext>(StringComparer.OrdinalIgnoreCase);
        var store = s.Track ? new TrackingStore(s.Home, s.Now) : null;
        int retention = 30;
        var noBaselineRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var full in fullPaths)
        {
            if (!full.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) continue;
            var root = FileCheck.RootOf(full);
            var ctx = Context(root, s, roots);
            outcome.Enter(root, ctx.Options);
            retention = ctx.Options.TrackingRetentionDays;
            var rel = FileCheck.Relative(root, full);
            if (store is not null) await TrackedAsync(store, full, root, rel, ctx, s, outcome).ConfigureAwait(false);
            else if (!AgainstBaseline(full, root, rel, ctx, s, outcome)) noBaselineRoots.Add(root);
        }
        if (noBaselineRoots.Count > 0)
        {
            outcome.MissingBaseline = true;
            outcome.Header.Add(NoBaselineNote);
        }
        store?.Cleanup(retention);
        return outcome;
    }

    /// <summary>The note of a check without a baseline file.</summary>
    public const string NoBaselineNote = "no baseline file, so every warning is shown; `skarp baseline` records one, then only new or worse findings are shown";

    internal static FileCheckResult Current(string full, string root, string rel, RootContext ctx, out byte[] bytes)
    {
        bytes = File.ReadAllBytes(full);
        return FileCheck.Analyze(ctx.Options, ctx.Rules, root, rel, FileCheck.Decode(bytes));
    }

    /// <summary>Without <c>--track</c>: new or worsened against the baseline, else every warning. False when no baseline.</summary>
    static bool AgainstBaseline(string full, string root, string rel, RootContext ctx, CheckSettings s, CheckOutcome outcome)
    {
        var current = Current(full, root, rel, ctx, out var bytes);
        if (current.ExcludedReason is { } why) { outcome.Notes.Add($"skarp check: {rel}: left out ({why})"); return true; }
        var o = ctx.Options;
        var baselinePath = Path.GetFullPath(o.BaselineFile());
        bool baseline = !o.NoBaseline && File.Exists(baselinePath);
        var changes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (baseline) AgainstBaselineFile(Baseline.Read(baselinePath), baselinePath, current, ctx, s, outcome, changes);
        else
        {
            foreach (var f in current.Findings.Where(f => Shown(f.Severity, s)))
                Add(outcome, AgentFormat.For(f, LineKind.Present), changes);
            CompositeLines.Apply(outcome, current, f => Shown(f.Severity, s) ? LineKind.Present : null, s.MinSeverity, changes);
        }
        if (s.Save && o.SaveLastRun) Record(root, current, bytes, [], changes, s.Now);
        return baseline;
    }

    /// <summary>The lines of one file's findings that are new or worse than in <paramref name="file"/> (also
    /// for a staged blob and a staged baseline), with its health line.</summary>
    internal static void AgainstBaselineFile(BaselineFile file, string baselinePath, FileCheckResult current, RootContext ctx, CheckSettings s,
        CheckOutcome outcome, SortedDictionary<string, string> changes)
    {
        var scores = BaselineHealth(file, current.Path, current, ctx, outcome);
        var breach = s.JudgeHealth ? LimitMark.Judge(scores, current.Path, ctx.Options.HealthLimits) : null;
        Baseline.Compare(current.Findings, file, baselinePath, null);
        BrainRules.ApplyStates(current.Composites, current.Findings);
        foreach (var f in current.Findings) outcome.AddSource(f);
        var mark = new LimitMark(outcome, breach);
        foreach (var f in current.Findings.Where(f => Shown(f.Severity, s) || breach is not null))
        {
            if (f.BaselineState == "worsened") Add(outcome, AgentFormat.For(f, LineKind.Worse, f.BaselineValue), changes);
            else if (f.BaselineState == "new") Add(outcome, AgentFormat.For(f, LineKind.New), changes);
        }
        CompositeLines.Apply(outcome, current, f => f.BaselineState switch { "worsened" => LineKind.Worse, "new" => LineKind.New, _ => null },
            mark.MinSeverity(s.MinSeverity), changes);
        mark.Apply();
    }

    /// <summary>The health line against the file's score recomputed from the baseline's entries. A
    /// file the baseline's file list does not hold is new (its heading says so, not a drop from 10); returns the
    /// scores the health limits judge (<see cref="LimitMark.Judge"/>).</summary>
    static BaselineScore BaselineHealth(BaselineFile file, string rel, FileCheckResult current, RootContext ctx, CheckOutcome outcome)
    {
        var byId = current.Findings.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var entries = file.Findings.Where(e => HealthEngine.IdPath(e.Id) == rel).ToList();
        var before = HealthBaseline.ByPath(entries, byId, ctx.Rules.For(rel, current.File?.TestCode ?? false)).GetValueOrDefault(rel) ?? [];
        int src = current.File?.Lines.Source ?? 0;
        var then = HealthEngine.SyntaxScore(ctx.Model, before, src);
        var now = HealthLines.Of(ctx.Model, current.Findings, src);
        var scores = BaselineScore.Of(file.Files, rel, then.Score, now.Score);
        AddBaselineHealth(outcome, rel, scores.IsNew ? null : then, now);
        return scores;
    }

    /// <summary>A file's score against the baseline and its health line (<paramref name="then"/> null: a new file, its
    /// score alone, below 10).</summary>
    static void AddBaselineHealth(CheckOutcome outcome, string rel, ScoreResult? then, ScoreResult now)
    {
        outcome.AddScore(rel, then?.Score, now.Score);
        if ((then is null ? HealthLines.New(rel, now) : HealthLines.Change(rel, then, now)) is { } line) outcome.AddHealth(line);
    }

    static void Add(CheckOutcome outcome, AgentLine line, SortedDictionary<string, string> changes)
    {
        outcome.Add(line);
        changes[line.Id] = line.Said;
    }

    internal static bool Shown(string severity, CheckSettings s) => s.MinSeverity == Severity.Notice || severity == "warning";

    /// <summary>The file as committed at <c>HEAD</c> (<c>git show HEAD:./name</c> in its folder); null bytes when it is not in HEAD.</summary>
    static async Task<byte[]?> HeadAsync(string full, IReadOnlyDictionary<string, string?>? environment)
    {
        var dir = Path.GetDirectoryName(full)!;
        using var buffer = new MemoryStream();
        var r = await GitProcess.RunStreamAsync(dir, ["show", "HEAD:./" + Path.GetFileName(full)],
            async (stream, ct) => await stream.CopyToAsync(buffer, ct).ConfigureAwait(false), environment).ConfigureAwait(false);
        return r.Succeeded ? buffer.ToArray() : null;
    }

    /// <summary>With <c>--track</c>: compare with the file's previous state and record the new one.</summary>
    static async Task TrackedAsync(TrackingStore store, string full, string root, string rel, RootContext ctx, CheckSettings s, CheckOutcome outcome)
    {
        var key = TrackingStore.Key(full);
        var (previous, _) = store.Previous(s.Session, s.Agent, key, ctx.Options.TrackingLookbackDays);
        // The git call (about 150 ms on Windows) runs while the current content is parsed.
        bool inGit = previous is null && Paths.WorkTreeRoot(Path.GetDirectoryName(full)!) is not null;
        var headTask = inGit ? HeadAsync(full, s.GitEnvironment) : null;
        var current = Current(full, root, rel, ctx, out var bytes);
        if (current.ExcludedReason is not null)
        {
            if (headTask is not null) await headTask.ConfigureAwait(false);
            return;
        }

        Dictionary<string, TrackedFinding> before;
        string comparedWith;
        if (previous is not null) { before = new(previous.Findings, StringComparer.Ordinal); comparedWith = previous.ComparedWith; }
        else (before, comparedWith) = Earlier(headTask is null ? null : await headTask.ConfigureAwait(false), headTask is not null, root, rel, ctx);

        var entry = new TrackedFile
        {
            Size = bytes.LongLength, Modified = Paths.UtcText(File.GetLastWriteTimeUtc(full)), Sha256 = Paths.Sha256(bytes),
            Checked = Paths.UtcText(s.Now), ComparedWith = comparedWith, Root = root, Path = rel,
            Reported = previous?.Reported ?? 0, ResolvedCount = previous?.ResolvedCount ?? 0,
            // The session-start state stays what the first check of the file compared with.
            Origin = previous is not null ? previous.Origin ?? OpenValues(previous.Findings) : OpenValues(before),
            OriginSeverities = previous is not null ? (previous.Origin is null ? OpenSeverities(previous.Findings) : previous.OriginSeverities) : OpenSeverities(before),
        };
        var mark = new HealthMark(outcome);
        // The edit that takes the file past a health limit since the session start lists its notices too.
        var (limit, edit) = EditLimits.Start(outcome, s, current, before, entry, ctx);
        var (resolved, changes, then, now) = AgainstEarlier(current, before, previous is null ? comparedWith : null, entry, ctx, edit, outcome);
        limit.Apply();
        entry.HealthShown = mark.Apply(previous?.HealthShown ?? Shown(then), now.Score,
            s.Hook ? r => HealthNote.Due(r, now.Score, HealthNote.NoticeOnly(ctx.Model, before, current, s.MinSeverity), HealthNote.Fixed(before, current)) : null);

        store.Update(s.Session, state =>
        {
            if (!state.Agents.TryGetValue(s.Agent, out var agent)) state.Agents[s.Agent] = agent = new TrackedAgent();
            agent.AgentType ??= s.AgentType;
            agent.Files[key] = entry;
        });
        if (s.Save && ctx.Options.SaveLastRun) Record(root, current, bytes, resolved, changes, s.Now);
    }

    /// <summary>
    /// A file's earlier state when nothing was recorded for it: the file at <c>HEAD</c> (<paramref name="head"/>,
    /// analyzed with the same settings; <c>head</c>), else nothing (<c>untracked</c> in a git work tree, else
    /// <c>outside-git</c>). Reads and writes nothing.
    /// </summary>
    internal static (Dictionary<string, TrackedFinding> Before, string ComparedWith) Earlier(byte[]? head, bool inGit, string root, string rel, RootContext ctx)
    {
        if (head is not null)
        {
            var old = FileCheck.Analyze(ctx.Options, ctx.Rules, root, rel, FileCheck.Decode(head));
            return (old.Findings.ToDictionary(f => f.Id, f => Tracked(f, 0), StringComparer.Ordinal), "head");
        }
        return (new(StringComparer.Ordinal), inGit ? "untracked" : "outside-git");
    }

    /// <summary>
    /// The file now against an earlier state, in memory only: its
    /// health line (<paramref name="first"/>: what a first comparison compared with, null for later ones), a line for
    /// each finding that is new, worse, better or fixed, and the composites. <paramref name="entry"/> receives the
    /// findings as the tracking file would record them; nothing is written. Returns the fixed findings shown, what each
    /// line said by id (a per-file record's changes), and the earlier and current scores.
    /// </summary>
    internal static (List<ResolvedFinding> Resolved, SortedDictionary<string, string> Changes, ScoreResult? Then, ScoreResult Now) AgainstEarlier(
        FileCheckResult current, Dictionary<string, TrackedFinding> before, string? first, TrackedFile entry, RootContext ctx, CheckSettings s, CheckOutcome outcome)
    {
        var changes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var (then, now) = TrackedHealth(current, before, first, ctx, outcome);
        var kinds = new Dictionary<string, LineKind?>(StringComparer.Ordinal);
        var resolved = Compare(current, before, entry, s, outcome, changes, kinds);
        FixedComposites(outcome, before, current, ctx, changes);
        CompositeLines.Apply(outcome, current, f => kinds.GetValueOrDefault(f.Id), s.MinSeverity, changes);
        return (resolved, changes, then, now);
    }

    /// <summary>
    /// Fills <paramref name="entry"/> with the current findings and the earlier ones now gone, and adds a line for each
    /// finding that is new (also when crossing its threshold again), worse, or resolved for the first time; returns the
    /// resolved ones that were printed.
    /// </summary>
    static List<ResolvedFinding> Compare(FileCheckResult current, Dictionary<string, TrackedFinding> before, TrackedFile entry, CheckSettings s,
        CheckOutcome outcome, SortedDictionary<string, string> changes, Dictionary<string, LineKind?> kinds)
    {
        var resolved = new List<ResolvedFinding>();
        foreach (var f in current.Findings)
        {
            before.TryGetValue(f.Id, out var p);
            var t = Tracked(f, p?.Reported ?? 0);
            LineKind? kind = p is null || p.State == "resolved" ? LineKind.New
                : f.Value is double v && p.Value is double pv && v != pv ? (v > pv ? LineKind.Worse : LineKind.Better) : null;
            kinds[f.Id] = kind;
            if (kind is { } k && Shown(f.Severity, s))
            {
                Add(outcome, AgentFormat.For(f, k, p?.Value), changes);
                t.Reported++;
                entry.Reported++;
            }
            entry.Findings[f.Id] = t;
        }
        var present = new HashSet<string>(current.Findings.Select(f => f.Id), StringComparer.Ordinal);
        foreach (var (id, p) in before.Where(b => !present.Contains(b.Key)).OrderBy(b => b.Key, StringComparer.Ordinal))
        {
            // The large-type finding of a type that became partial cannot be judged by a check: it stays as it was.
            if (p.Rule == RuleIds.LargeType && current.PartialTypes.Contains(PlaceOf(id))) { entry.Findings[id] = p; continue; }
            var copy = new TrackedFinding { Rule = p.Rule, Severity = p.Severity, Metric = p.Metric, Value = p.Value, Threshold = p.Threshold, Line = p.Line, State = "resolved", Reported = p.Reported };
            entry.Findings[id] = copy;
            if (p.State != "open") continue;
            entry.ResolvedCount++;
            if (!Shown(p.Severity, s)) continue;
            bool removed = !PlaceExists(current, id);
            var r = new ResolvedFinding
            {
                Id = id, Rule = p.Rule, Severity = p.Severity, Path = current.Path, Line = removed ? 1 : p.Line, Value = p.Value, Threshold = p.Threshold, Metric = p.Metric,
                Removed = removed ? true : null,
            };
            var line = AgentFormat.Resolved(r);
            r.Handle = line.Handle;
            Add(outcome, line, changes);
            copy.Reported++;
            entry.Reported++;
            resolved.Add(r);
        }
        return resolved;
    }

    /// <summary>
    /// A brain method the earlier state had (its part findings open at one place, enough of
    /// them) and the file no longer has is reported fixed under <c>brain-method</c>, the rule its line was shown with,
    /// in place of its parts' fixed lines (one line, no fake line number for a removed member).
    /// </summary>
    static void FixedComposites(CheckOutcome outcome, Dictionary<string, TrackedFinding> before, FileCheckResult current, RootContext ctx,
        SortedDictionary<string, string> changes)
    {
        if (BrainRules.From(ctx.Rules.For(current.Path, current.File?.TestCode ?? false)) is not { Method: true } config) return;
        var rulesAt = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (id, p) in before)
        {
            if (p.State != "open" || !config.IsPart(p.Rule)) continue;
            var place = BarePlace(id);
            if (!rulesAt.TryGetValue(place, out var rules)) rulesAt[place] = rules = new(StringComparer.Ordinal);
            rules.Add(p.Rule);
        }
        foreach (var (place, rules) in rulesAt)
        {
            if (rules.Count < config.MinParts || current.Composites.Exists(c => c.Rule == RuleIds.BrainMethod && c.Path == current.Path && c.PlaceKey == place)) continue;
            var parts = outcome.Lines.FindAll(l => l.Kind == LineKind.Resolved && l.Path == current.Path && config.IsPart(l.Rule) && BarePlace(l.Id) == place);
            if (parts.Count == 0) continue;
            outcome.Lines.RemoveAll(parts.Contains);
            foreach (var part in parts) changes.Remove(part.Id);
            var id = FindingEngine.MakeId(RuleIds.BrainMethod, current.Path, place);
            bool removed = parts.TrueForAll(l => l.Removed);
            var first = parts.MinBy(l => l.Line)!;
            Add(outcome, new AgentLine(parts.Exists(l => l.Severity == "warning") ? "warning" : "notice", current.Path, first.Line, RuleIds.BrainMethod,
                FindingEngine.HandleOf(id), Reporting.TextFormat.Fixed(null, null, removed, first.Place), LineKind.Resolved, id)
            { Removed = removed, Place = first.Place }, changes);
        }
    }

    /// <summary>The place of a finding id without its <c>~n</c> ordinal.</summary>
    static string BarePlace(string id)
    {
        var place = PlaceOf(id);
        int tilde = place.LastIndexOf('~');
        return tilde > 0 && place[(tilde + 1)..].All(char.IsAsciiDigit) ? place[..tilde] : place;
    }

    static TrackedFinding Tracked(Finding f, int reported) => new()
    {
        Rule = f.Rule, Severity = f.Severity, Metric = f.Metric, Value = f.Value, Threshold = f.Threshold, Line = f.Line, State = "open", Reported = reported,
    };

    static string PlaceOf(string id)
    {
        var parts = id.Split('|', 3);
        return parts.Length == 3 ? parts[2] : "";
    }

    /// <summary>
    /// Whether the place of a finding id (a member key or id, a type name; empty for file findings) still exists in the
    /// checked file. A deleted or renamed member or type is reported as removed, at line 1, since its old line is stale.
    /// </summary>
    static bool PlaceExists(FileCheckResult current, string id)
    {
        var place = PlaceOf(id);
        int tilde = place.LastIndexOf('~');
        if (tilde > 0 && place[(tilde + 1)..].All(char.IsAsciiDigit)) place = place[..tilde];
        if (place.Length == 0 || current.File is not { } file) return true;
        return file.Members.Any(m => m.Key == place || m.Id == place) || file.Types.Any(t => t.Name == place);
    }

    internal static void Record(string root, FileCheckResult current, byte[] bytes, List<ResolvedFinding> resolved, SortedDictionary<string, string> changes, DateTimeOffset now)
    {
        var members = new HashSet<string>(current.Findings.Select(f => f.Member ?? ""), StringComparer.Ordinal);
        try
        {
            CheckRecords.Write(root, new CheckRecord
            {
                Version = CheckRecords.Version, WrittenAt = Paths.UtcText(now), Path = current.Path, Sha256 = Paths.Sha256(bytes),
                Findings = current.Findings, Resolved = resolved, Changes = changes, PartialTypes = current.PartialTypes,
                Members = current.File is null ? [] : [.. current.File.Members.Where(m => members.Contains(m.Id))],
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // a record is a convenience for show
    }

    static SortedDictionary<string, string> OpenSeverities(IDictionary<string, TrackedFinding> findings) =>
        new(findings.Where(f => f.Value.State == "open").ToDictionary(f => f.Key, f => f.Value.Severity, StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>The health line against the previous state (a file new to git: its score when below 10).
    /// A health line needs a known earlier score (an earlier check or git HEAD), and says "new"
    /// only for a file new to git; a file outside git, checked for the first time (<paramref name="first"/>: what the
    /// first check compared with, null for later checks), has neither. Returns the earlier score (null when
    /// none is known) and the score now.</summary>
    static (ScoreResult? Then, ScoreResult Now) TrackedHealth(FileCheckResult current, Dictionary<string, TrackedFinding> before, string? first, RootContext ctx, CheckOutcome outcome)
    {
        int src = current.File?.Lines.Source ?? 0;
        var now = HealthLines.Of(ctx.Model, current.Findings, src);
        if (first == "outside-git") return (null, now);
        var then = first == "untracked" ? null : HealthLines.Of(ctx.Model, before, src);
        var line = then is null ? HealthLines.New(current.Path, now) : HealthLines.Change(current.Path, then, now);
        if (line is { } l) outcome.AddHealth(l);
        return (then, now);
    }

    static double? Shown(ScoreResult? score) => score is null ? null : Health.HealthText.Shown(score.Score);

    internal static SortedDictionary<string, double?> OpenValues(IDictionary<string, TrackedFinding> findings) =>
        new(findings.Where(f => f.Value.State == "open").ToDictionary(f => f.Key, f => f.Value.Value, StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>
    /// The <c>Stop</c> hook: the files tracked in this session whose content changed
    /// since the last <c>Stop</c>, each compared with its session-start state per agent (<see cref="TrackedFile.Origin"/>:
    /// git HEAD, an earlier day's record, or nothing, as the first PostToolUse check of the file saw it). Only findings
    /// new or worse than that are reported; a finding the baseline already holds at that value or higher counts as
    /// known. Returns null when no tracked file changed (nothing is recorded then either).
    /// </summary>
    public static CheckOutcome? Stop(CheckSettings s, Func<string, IReadOnlyList<string>, List<Finding>>? semantic = null,
        Func<string, IReadOnlyList<string>, List<Finding>>? clones = null)
    {
        var store = new TrackingStore(s.Home, s.Now);
        var state = TrackingStore.Read(store.SessionFile(s.Session));
        if (state is null) return null;
        var tracked = TrackedByPath(state);
        var origins = new SortedDictionary<string, List<SortedDictionary<string, double?>>>(
            tracked.ToDictionary(t => t.Key, t => t.Value.Select(f => f.Origin ?? OpenValues(f.Findings)).ToList()), StringComparer.Ordinal);
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var changed = new List<string>();
        foreach (var path in origins.Keys)
        {
            if (Paths.FileSha256(path) is not { } h) continue;
            hashes[path] = h;
            if (state.Stop?.Files.GetValueOrDefault(path) != h) changed.Add(path);
        }
        if (changed.Count == 0) return Finish(store, s, state, null, null);

        var outcome = new CheckOutcome { EndOfTurn = true };
        var roots = new Dictionary<string, RootContext>(StringComparer.OrdinalIgnoreCase);
        using var heads = new HeadFiles(s.GitEnvironment);
        var pass = new StopPass(s, outcome, tracked, origins, heads);
        foreach (var group in changed.GroupBy(FileCheck.RootOf, StringComparer.OrdinalIgnoreCase))
        {
            var root = group.Key;
            var ctx = Context(root, s, roots);
            outcome.Enter(root, ctx.Options);
            var baselinePath = Path.GetFullPath(ctx.Options.BaselineFile());
            var known = new Dictionary<string, double?>(StringComparer.Ordinal);
            if (!ctx.Options.NoBaseline && File.Exists(baselinePath))
                foreach (var e in Baseline.Read(baselinePath).Findings) known[e.Id] = e.Value;
            foreach (var full in group) pass.File(root, full, ctx, known);
            // Semantic findings (--semantic) and clone groups (they span files, so the whole root is matched)
            // have no session-start state: their state against the baseline counts.
            List<string> relative = [.. group.Select(g => FileCheck.Relative(root, g))];
            if (semantic is not null) AgainstBaseline(semantic(root, relative), s, outcome);
            if (clones is not null) AgainstBaseline(clones(root, relative), s, outcome);
        }
        var result = Finish(store, s, state, hashes, outcome);
        if (s.Hook) HealthNote.AtStop(outcome, state.Shown, pass.Rules);
        return result;
    }

    /// <summary>
    /// The end of a Stop that runs: its lines without what the hook already showed, and the Stop state recorded
    /// when files changed. What was shown stays recorded across Stops (an earlier Stop's lines
    /// count as seen too).
    /// </summary>
    static CheckOutcome? Finish(TrackingStore store, CheckSettings s, TrackingState state, SortedDictionary<string, string>? hashes, CheckOutcome? outcome)
    {
        if (outcome is not null && state.Shown is { } shown) ShownLines.Drop(outcome, shown, s.Gate);
        if (hashes is not null) store.Update(s.Session, st => st.Stop = new TrackedStop { At = Paths.UtcText(s.Now), Files = hashes });
        return outcome;
    }

    /// <summary>
    /// Records the lines a hook message printed (after the budget) in the session's tracking state, with the
    /// part lines of each composite printed, so a later <c>Stop</c> summary does not repeat them. In
    /// the same write, the agent's About-line rhythm (<see cref="AboutRhythm"/>); returns whether the About line is
    /// due on this message (<paramref name="aboutFits"/>: the budget leaves room for it, so printing it is recorded).
    /// </summary>
    public static bool RecordShown(CheckSettings s, IReadOnlyList<AgentLine> printed, CheckOutcome outcome, bool aboutFits)
    {
        bool due = false;
        new TrackingStore(s.Home, s.Now).Update(s.Session, st =>
        {
            if (printed.Count > 0) ShownLines.Add(st.Shown ??= new(StringComparer.Ordinal), printed, outcome);
            if (!st.Agents.TryGetValue(s.Agent, out var agent)) st.Agents[s.Agent] = agent = new TrackedAgent();
            agent.AgentType ??= s.AgentType;
            due = AboutRhythm.Printed(agent, st.Compactions, aboutFits);
        });
        return due;
    }

    /// <summary>Stop lines for findings marked against a baseline: worsened and new ones.</summary>
    static void AgainstBaseline(IEnumerable<Finding> findings, CheckSettings s, CheckOutcome outcome)
    {
        foreach (var f in findings.Where(f => Shown(f.Severity, s)))
        {
            if (f.BaselineState == "worsened") outcome.Add(AgentFormat.For(f, LineKind.Worse, f.BaselineValue));
            else if (f.BaselineState == "new") outcome.Add(AgentFormat.For(f, LineKind.New));
        }
    }

    /// <summary>Every agent's entry of each tracked file, by absolute path.</summary>
    static Dictionary<string, List<TrackedFile>> TrackedByPath(TrackingState state) =>
        state.Agents.Values.SelectMany(a => a.Files).GroupBy(f => f.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(f => f.Value).ToList(), StringComparer.Ordinal);

    /// <summary>A Stop line for a finding: new or worse against the session-start state of any agent that tracked the
    /// file (the most favourable for the agents: reported only when worse than every agent's start); null when not.</summary>
    internal static AgentLine? AgainstOrigin(Finding f, List<SortedDictionary<string, double?>> origins, Dictionary<string, double?> known)
    {
        AgentLine? line = null;
        foreach (var origin in origins)
        {
            AgentLine? candidate;
            double? start = origin.TryGetValue(f.Id, out var o) ? o : known.TryGetValue(f.Id, out var k) ? k : null;
            bool present = origin.ContainsKey(f.Id) || known.ContainsKey(f.Id);
            if (!present) candidate = AgentFormat.For(f, LineKind.New);
            else if (f.Value is double v && start is double sv && v > sv) candidate = AgentFormat.For(f, LineKind.Worse, sv);
            else return null; // not worse for this agent's session: nothing to report
            line ??= candidate;
        }
        return line;
    }
}
