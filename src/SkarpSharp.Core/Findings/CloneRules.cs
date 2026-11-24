using System.Globalization;
using SkarpSharp.Clones;
using SkarpSharp.Model;

namespace SkarpSharp.Findings;

/// <summary>An instance before it becomes JSON: its corpus position and its place.</summary>
sealed record Instance(int Start, int Last, CloneInstance Json);

/// <summary>
/// The rules <c>duplicate-code</c> (Type 1/2) and <c>duplicate-code-near</c> (Type 3) over the
/// clone token streams of a run: production and test code are matched separately (test-to-production clones are never
/// reported); instances in code suppressed for the rule are dropped and a group needs two instances left;
/// one finding per group, path-less id from the seed window, located at the ordinal-first instance.
/// </summary>
public static class CloneRules
{
    /// <summary>The hash window: one pass at 50 tokens serves the default and the jscpd preset.</summary>
    public const int Window = 50;
    /// <summary>Instances listed in a finding's message.</summary>
    const int MessageInstances = 3;

    public sealed record Outcome(List<Finding> Findings, List<(Finding Finding, SuppressionAttribute By, string ByPath)> Suppressed, DuplicationSummary? Summary);

    public static Outcome Run(IReadOnlyList<FileResult> files, RuleSettings settings, SuppressionIndex suppressions, int sourceLines)
    {
        var run = new Accumulator(settings[RuleIds.DuplicateCode], settings[RuleIds.DuplicateCodeNear], suppressions);
        var withData = files.Where(f => f.CloneData is not null).ToList();
        if (withData.Count == 0 || (!run.Dup.Enabled && !run.Near.Enabled)) return new([], [], null);
        // Production and test code are matched apart: test-to-production clones are never reported.
        foreach (var set in withData.GroupBy(f => f.TestCode).OrderBy(g => g.Key))
            run.Add(CloneCorpus.Build(set.Select(f => (f.Path, f.CloneData!))));
        return new(run.Findings, run.Suppressed, run.Summary(sourceLines));
    }

    /// <summary>The findings, suppressed groups and duplication counts of a run, one corpus (file set) at a time.</summary>
    sealed class Accumulator(ActiveRule dup, ActiveRule near, SuppressionIndex suppressions)
    {
        public ActiveRule Dup => dup;
        public ActiveRule Near => near;
        public List<Finding> Findings { get; } = [];
        public List<(Finding Finding, SuppressionAttribute By, string ByPath)> Suppressed { get; } = [];
        int _groups, _instances, _nearGroups, _duplicated;

        public void Add(CloneCorpus corpus)
        {
            var tokenGroups = dup.Enabled ? Type12(corpus, dup) : [];
            var lines = new HashSet<(int File, int Line)>();
            foreach (var g in SkipLowContent(dup) ? LowContent.Without(corpus, tokenGroups) : tokenGroups)
            {
                var built = Build(corpus, g, dup, suppressions);
                if (built.Finding is { } f) { Findings.Add(f); _groups++; _instances += built.Kept.Count; AddLines(corpus, built.Kept, lines); }
                if (built.Suppressed is { } s) Suppressed.Add(s);
            }
            _duplicated += lines.Count;
            if (!near.Enabled) return;
            foreach (var g in Type3(corpus, near, tokenGroups))
            {
                var built = BuildNear(corpus, g, near, suppressions);
                if (built.Finding is { } f) { Findings.Add(f); _nearGroups++; }
                if (built.Suppressed is { } s) Suppressed.Add(s);
            }
        }

        public DuplicationSummary Summary(int sourceLines) => new()
        {
            Groups = _groups, Instances = _instances, DuplicatedLines = _duplicated, NearGroups = near.Enabled ? _nearGroups : null,
            Share = sourceLines > 0 ? Math.Round((double)_duplicated / sourceLines, 4) : 0,
        };
    }

    /// <summary>The minimum size of a duplicate-code group: the notice level's tokens and lines, else the default's.</summary>
    public static (int Tokens, int Lines) Minimum(ActiveRule rule)
    {
        var level = rule.Notice ?? Presets.Default.Rules[RuleIds.DuplicateCode].Notice!;
        return ((int)Math.Ceiling(level.Find("tokens")?.Value ?? 100), (int)Math.Ceiling(level.Find("lines")?.Value ?? 10));
    }

    /// <summary>True unless the rule's <c>lowContent</c> parameter is <c>report</c>: low-content groups (LowContent) are
    /// left out. Near-miss matching still sees them as covering, so they never come back as duplicate-code-near.</summary>
    public static bool SkipLowContent(ActiveRule rule) => rule.Parameters.GetValueOrDefault("lowContent") != "report";

    static List<TokenGroup> Type12(CloneCorpus corpus, ActiveRule rule)
    {
        var (tokens, lines) = Minimum(rule);
        return TokenMatcher.Find(corpus, Math.Min(Window, Math.Max(2, tokens)), tokens, lines);
    }

    static List<NearGroup> Type3(CloneCorpus corpus, ActiveRule rule, List<TokenGroup> covering)
    {
        var level = rule.Notice ?? Presets.Default.Rules[RuleIds.DuplicateCodeNear].Notice!;
        double similarity = level.Find("similarity")?.Value ?? 0.75;
        int lines = (int)Math.Ceiling(level.Find("lines")?.Value ?? 10);
        return NearMatcher.Find(corpus, similarity, lines, covering);
    }

    // ---- Type 1/2 findings -----------------------------------------------------------------------------------------

    static (Finding? Finding, (Finding, SuppressionAttribute, string)? Suppressed, List<Instance> Kept) Build(CloneCorpus c, TokenGroup g, ActiveRule rule, SuppressionIndex suppressions)
    {
        var all = g.Starts.Select(s => Instance(c, s, s + g.Length - 1, g.Length)).ToList();
        var (kept, by) = Unsuppressed(all, RuleIds.DuplicateCode, suppressions);
        var severity = Severity(rule, kept.Select(i => i.Json.Lines).ToList());
        if (severity is null) return (null, null, kept);
        var finding = NewFinding(c, RuleIds.DuplicateCode, rule, severity.Value, kept);
        finding.Clone = new CloneInfo { Kind = g.Exact ? "exact" : "renamed", Tokens = g.Length, DuplicatedLines = TokenLines(c, kept), Instances = [.. kept.Select(i => i.Json)] };
        finding.Message = Message(kept, $"{g.Length} tokens, {finding.Clone.Kind}");
        if (by is null) return (finding, null, kept);
        finding.Clone.Instances = [.. all.Select(i => i.Json)];
        return (null, (finding, by!.Value.By, by.Value.ByPath), kept);
    }

    static Instance Instance(CloneCorpus c, int first, int last, int? tokens)
    {
        int file = c.FileOf[first];
        int member = 0;
        for (int k = first; k <= last && member == 0; k++) member = c.Members[k];
        return new Instance(first, last, new CloneInstance
        {
            Path = c.Paths[file], StartLine = c.Lines[first], EndLine = c.EndLines[last], Lines = c.SpanLines(first, last), Tokens = tokens,
            Member = member == 0 ? null : c.Files[file].MemberNames[member - 1],
        });
    }

    /// <summary>The instances outside code suppressed for the rule; when fewer than two remain, the suppression of the
    /// first suppressed instance (the group is suppressed).</summary>
    static (List<Instance> Kept, (SuppressionAttribute By, string ByPath)? By) Unsuppressed(List<Instance> all, string rule, SuppressionIndex suppressions)
    {
        if (suppressions.IsEmpty) return (all, null);
        var kept = new List<Instance>();
        (SuppressionAttribute, string)? first = null;
        foreach (var i in all)
        {
            if (suppressions.FindCopy(rule, i.Json.Path, i.Json.StartLine, i.Json.EndLine) is { } s) first ??= s;
            else kept.Add(i);
        }
        return kept.Count >= 2 ? (kept, null) : (all, first);
    }

    /// <summary>Warning when a warning condition holds (<c>lines</c>: the longest instance; <c>copies</c>: instances of at
    /// least <c>copyLines</c> lines), else notice when the notice level is on; null when neither level is on.</summary>
    static Severity? Severity(ActiveRule rule, List<int> lines)
    {
        if (rule.Warning is { } w)
        {
            int copyLines = int.TryParse(rule.Parameters.GetValueOrDefault("copyLines"), NumberStyles.None, CultureInfo.InvariantCulture, out var cl) ? cl : 20;
            foreach (var cond in w.AnyOf)
            {
                double value = cond.Metric switch { "lines" => lines.Max(), "copies" => lines.Count(l => l >= copyLines), _ => double.NaN };
                if (!double.IsNaN(value) && cond.Test(value)) return Findings.Severity.Warning;
            }
        }
        return rule.Notice is not null ? Findings.Severity.Notice : null;
    }

    static Finding NewFinding(CloneCorpus c, string ruleId, ActiveRule rule, Severity severity, List<Instance> instances)
    {
        var level = rule.Level(severity)!;
        var first = instances[0];
        var place = "clone " + Seed(c, first.Start, Math.Min(Window, first.Last - first.Start + 1));
        return new Finding
        {
            Rule = ruleId, Severity = severity == Findings.Severity.Warning ? "warning" : "notice", Path = first.Json.Path, Line = first.Json.StartLine,
            Member = first.Json.Member ?? "(outside members)", Value = instances.Sum(i => i.Json.Lines), Metric = "copiedLines",
            Preset = rule.LevelPreset(severity), ThresholdSource = level.Source,
            PlaceKey = place, PathlessId = true,
        };
    }

    /// <summary>Identity: 16 hex digits of SHA-256 over the normalized tokens of the seed window (the first window
    /// of the ordinal-first instance); the same for the same code in every run and under file reordering.</summary>
    public static string Seed(CloneCorpus c, int start, int length)
    {
        var bytes = new byte[length * 8];
        for (int k = 0; k < length; k++) BitConverter.TryWriteBytes(bytes.AsSpan(k * 8), c.Value(start + k, start));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
    }

    static string Message(List<Instance> instances, string detail)
    {
        int min = instances.Min(i => i.Json.Lines), max = instances.Max(i => i.Json.Lines);
        var size = min == max ? $"{min} lines" : $"{min} to {max} lines";
        return $"{instances.Count} copies of {size} ({detail}): {Locations(instances.Select(i => i.Json))}";
    }

    /// <summary><c>a.cs:10-33, b.cs:40-63, +2 more</c>.</summary>
    public static string Locations(IEnumerable<CloneInstance> instances)
    {
        var list = instances.ToList();
        var shown = string.Join(", ", list.Take(MessageInstances).Select(i => $"{i.Path}:{i.StartLine}-{i.EndLine}"));
        return list.Count > MessageInstances ? $"{shown}, +{list.Count - MessageInstances} more" : shown;
    }

    static int TokenLines(CloneCorpus c, List<Instance> instances)
    {
        var lines = new HashSet<(int, int)>();
        AddLines(c, instances, lines);
        return lines.Count;
    }

    /// <summary>Adds the lines holding tokens of the instances (file index, line).</summary>
    static void AddLines(CloneCorpus c, List<Instance> instances, HashSet<(int File, int Line)> lines)
    {
        foreach (var i in instances)
            for (int k = i.Start; k <= i.Last; k++)
                for (int l = c.Lines[k]; l <= c.EndLines[k]; l++) lines.Add((c.FileOf[k], l));
    }

    // ---- Type 3 findings -------------------------------------------------------------------------------------------

    static (Finding? Finding, (Finding, SuppressionAttribute, string)? Suppressed) BuildNear(CloneCorpus c, NearGroup g, ActiveRule rule, SuppressionIndex suppressions)
    {
        if (rule.Notice is null && rule.Warning is null) return (null, null);
        var all = g.Units.Select(u => NearInstance(c, u)).ToList();
        var (kept, by) = Unsuppressed(all, RuleIds.DuplicateCodeNear, suppressions);
        var severity = rule.Notice is not null ? Findings.Severity.Notice : Findings.Severity.Warning;
        var finding = NewFinding(c, RuleIds.DuplicateCodeNear, rule, severity, kept);
        finding.Clone = new CloneInfo
        {
            Kind = "near-miss", DuplicatedLines = kept.Sum(i => i.Json.Lines), Instances = [.. kept.Select(i => i.Json)],
            Pairs = JoiningPairs(g, all, kept),
        };
        var sims = finding.Clone.Pairs.Count > 0 ? finding.Clone.Pairs.Select(p => p.Similarity).ToList() : [0.0];
        var range = sims.Min() == sims.Max() ? Num(sims.Min()) : $"{Num(sims.Min())} to {Num(sims.Max())}";
        finding.Message = Message(kept, $"similar methods, similarity {range}");
        if (by is null) return (finding, null);
        finding.Clone.Instances = [.. all.Select(i => i.Json)];
        return (null, (finding, by!.Value.By, by.Value.ByPath));
    }

    /// <summary>
    /// The pairs that join a near-miss group: a maximum spanning tree over its similar pairs among the kept instances
    /// (Kruskal, most similar first), so a clique of n overloads lists n - 1 pairs, not n(n-1)/2. Indexes are into the
    /// kept instances.
    /// </summary>
    static List<ClonePair> JoiningPairs(NearGroup g, List<Instance> all, List<Instance> kept)
    {
        var indexOfUnit = g.Units.Select((u, i) => (u, i)).ToDictionary(x => x.u, x => x.i);
        var keptIndex = kept.Select((k, i) => (all.IndexOf(k), i)).ToDictionary(x => x.Item1, x => x.i);
        var parent = Enumerable.Range(0, kept.Count).ToArray();
        int Find(int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }
        var result = new List<ClonePair>();
        var candidates = g.Pairs
            .Select(p => (A: keptIndex.GetValueOrDefault(indexOfUnit[p.A], -1), B: keptIndex.GetValueOrDefault(indexOfUnit[p.B], -1), p.Similarity))
            .Where(p => p.A >= 0 && p.B >= 0)
            .OrderByDescending(p => p.Similarity).ThenBy(p => Math.Min(p.A, p.B)).ThenBy(p => Math.Max(p.A, p.B));
        foreach (var (a, b, sim) in candidates)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb) continue;
            parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
            result.Add(new ClonePair { A = Math.Min(a, b), B = Math.Max(a, b), Similarity = Math.Round(sim, 3) });
        }
        return [.. result.OrderBy(p => p.A).ThenBy(p => p.B)];
    }

    static string Num(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    static Instance NearInstance(CloneCorpus c, NearUnit u)
    {
        int start = u.Unit.FirstToken < 0 ? c.FileStart[u.File] : c.FileStart[u.File] + u.Unit.FirstToken;
        int last = u.Unit.LastToken < 0 ? start : c.FileStart[u.File] + u.Unit.LastToken;
        return new Instance(start, last, new CloneInstance
        {
            Path = c.Paths[u.File], StartLine = u.Unit.StartLine, EndLine = u.Unit.EndLine, Lines = u.Unit.EndLine - u.Unit.StartLine + 1,
            Tokens = u.Unit.LastToken < 0 ? null : u.Unit.LastToken - u.Unit.FirstToken + 1, Member = u.Unit.Name,
        });
    }
}
