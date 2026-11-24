using SkarpSharp.Model;

namespace SkarpSharp.Findings;

/// <summary>
/// Runs the v1 rules over an analysis result (after the history join), gives each finding its identity and
/// stores all findings in report order (<see cref="Sort"/>: files by activity, places by distance over the limit).
/// </summary>
public static class FindingEngine
{
    public static void Apply(AnalysisResult result, RuleSettings settings, bool includeGenerated) =>
        Apply(result, PathRules.Single(settings), includeGenerated);

    /// <summary>
    /// Each file is judged by its own settings (<paramref name="rules"/>):
    /// member, type and file rules by the file's, a type's rules by the file it is reported in (its first declaration), a
    /// suppression by the attribute's file. The cross-file rules (duplicate code, active-complex-file's ranking) and the
    /// run header use the repository settings; an override that switches active-complex-file off drops its findings for
    /// the files it matches, after ranking.
    /// </summary>
    public static void Apply(AnalysisResult result, PathRules rules, bool includeGenerated)
    {
        var settings = rules.Base;
        var files = result.Files.Where(f => f.Error is null && (includeGenerated || !f.Generated)).ToList();
        // The settings of a path by its test status in this run (a path the run did not keep counts as production code).
        Dictionary<string, bool>? testByPath = null;
        if (rules.Count > 0)
        {
            testByPath = new(StringComparer.Ordinal);
            foreach (var f in files) testByPath.TryAdd(f.Path, f.TestCode);
        }
        RuleSettings Of(string path) => testByPath is null ? settings : rules.For(path, testByPath.GetValueOrDefault(path));
        // The coverage rule runs only when a coverage report was read (the stage before set the member facts).
        bool coverage = result.Coverage is not null;
        var findings = FileRules(files, rules, coverage);
        foreach (var t in result.Types)
        {
            var typeSettings = Of(t.Path);
            var large = Rules.LargeType(t, typeSettings[RuleIds.LargeType]);
            // A type whose roll-up a variant raised is judged on the base view too.
            if (large is not null && (t.BaseView is not null || t.OnlyInVariants))
                VariantFindings.Reconcile([large], t.BaseView is { } b ? Rules.LargeType(b, typeSettings[RuleIds.LargeType]) is { } x ? [x] : [] : [], _ => t.Variant);
            AddPlace(findings, large, t.Name);
        }

        // Inline suppression and the duplicate-code rules, which match across files.
        var suppressions = SuppressionIndex.Build(files);
        findings.AddRange(Unjustified(suppressions, p => Of(p)[RuleIds.SuppressionWithoutJustification]));
        var clones = CloneRules.Run(files, settings, suppressions, result.Repository.Lines.Source);
        findings.AddRange(clones.Findings);
        foreach (var f in files) f.CloneData = null;
        if (clones.Summary is not null) result.Repository.Duplication = clones.Summary;
        // A suppressed composite (brain-method, brain-class) takes its parts with it. Each composite
        // takes the settings of the file it is reported in; they run when any settings in use switch one on.
        var brain = rules.InUse().Any(s => BrainRules.From(s) is not null) ? new BrainRules.PathConfig(Of) : null;
        var byComposite = brain is null ? [] : BrainRules.SuppressComposites(findings, result.Types, brain, suppressions);
        var (kept, hidden) = Split(findings, suppressions);
        hidden.AddRange(byComposite);
        hidden.AddRange(clones.Suppressed);
        findings = kept;

        var info = settings.ToInfo(coverage);
        if (result.History is not null)
            findings.AddRange(Rules.ActiveComplexFile(files, findings, settings[RuleIds.ActiveComplexFile])
                .Where(f => testByPath is null || Of(f.Path)[RuleIds.ActiveComplexFile].Enabled));
        else
            info.Notes.Add("active-complex-file skipped: no git history (not a repository, git missing or history off)");

        var commits = result.History is null ? null
            : files.ToDictionary(f => f.Path, f => f.History?.Commits ?? 0, StringComparer.Ordinal);
        if (commits is not null)
            foreach (var x in findings) x.Commits = commits.GetValueOrDefault(x.Path);

        AssignIds([.. findings, .. hidden.Select(h => h.Finding)]);
        Sort(findings);
        result.Findings = findings;
        if (brain is not null) result.Composites = BrainRules.Build(findings, result.Types, brain);
        result.FindingCounts = Counts(findings, RuleCatalog.WithCoverage(coverage));
        result.Suppressed = hidden.Count == 0 ? null
            : [.. hidden.Select(h => SuppressionIndex.Suppressed(h.Finding, h.By, h.ByPath)).OrderBy(s => s.Path, StringComparer.Ordinal).ThenBy(s => s.Line).ThenBy(s => s.Id, StringComparer.Ordinal)];
        result.Run.Findings = info;
    }

    /// <summary>The member, type and file rules of each file.</summary>
    static List<Finding> FileRules(List<FileResult> files, PathRules rules, bool coverage = false)
    {
        var findings = new List<Finding>();
        foreach (var f in files)
        {
            var settings = rules.For(f.Path, f.TestCode);
            var own = FileRules(f, settings, coverage);
            // A file with variants is judged on its base view too; what only a variant shows is a
            // notice that names the variant.
            if (VariantFindings.HasVariants(f)) VariantFindings.Reconcile(own, FileRules(VariantFindings.BaseView(f), settings, coverage), x => VariantFindings.LabelOf(f, x));
            findings.AddRange(own);
        }
        return findings;
    }

    /// <summary>The member, type and file rules of one file.</summary>
    static List<Finding> FileRules(FileResult f, RuleSettings settings, bool coverage)
    {
        var findings = new List<Finding>();
        var ids = new HashSet<string>(f.Members.Select(m => m.Id), StringComparer.Ordinal);
        var indexers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in f.Members)
        {
            void Add(Finding? x)
            {
                if (x is null) return;
                x.PlaceKey = m.Key.Length > 0 ? m.Key : m.Id;
                findings.Add(x);
            }
            // A record whose parent is another record is a nested body; lambdas of field initializers have no parent record.
            bool topLevel = m.Parent is null || !ids.Contains(m.Parent);
            Add(Rules.ComplexMethod(m, f.Path, topLevel, settings[RuleIds.ComplexMethod]));
            Add(Rules.DeepNesting(m, f.Path, topLevel, settings[RuleIds.DeepNesting]));
            Add(Rules.LongMethod(m, f.Path, settings[RuleIds.LongMethod]));
            // Indexer accessors share one parameter list: judge it once per indexer declaration.
            if (m.Kind is not ("IndexerGet" or "IndexerSet" or "IndexerInit") || indexers.Add(m.Type + "::" + m.Name[..m.Name.LastIndexOf('.')]))
                Add(Rules.TooManyParameters(m, f.Path, settings[RuleIds.TooManyParameters]));
            Add(Rules.PathExplosion(m, f.Path, settings[RuleIds.PathExplosion]));
            Add(Rules.BumpyRoad(m, f.Path, settings[RuleIds.BumpyRoad]));
            Add(Rules.ComplexCondition(m, f.Path, settings[RuleIds.ComplexCondition]));
            foreach (var c in Rules.EmptyCatch(m, f.Path, settings[RuleIds.EmptyCatch])) Add(c);
            Add(Rules.MagicNumber(m, f.Path, settings[RuleIds.MagicNumber]));
            if (coverage && !f.TestCode) Add(Rules.UntestedComplexMethod(m, f.Path, settings[RuleIds.UntestedComplexMethod]));
        }
        foreach (var t in f.Types)
        {
            // Positional records are data carriers; their parameters are exempt unless checked explicitly.
            if (t.Kind == "record" && !settings.CheckRecordParameters) continue;
            AddPlace(findings, Rules.TooManyParameters(t, f.Path, settings[RuleIds.TooManyParameters]), t.Name);
        }
        AddPlace(findings, Rules.LargeFile(f, settings[RuleIds.LargeFile]), null);
        return findings;
    }

    /// <summary>Findings in suppressed code, with the suppression; the rest is kept.</summary>
    static (List<Finding> Kept, List<(Finding Finding, SuppressionAttribute By, string ByPath)> Hidden) Split(List<Finding> findings, SuppressionIndex suppressions)
    {
        var hidden = new List<(Finding, SuppressionAttribute, string)>();
        if (suppressions.IsEmpty) return (findings, hidden);
        var kept = new List<Finding>(findings.Count);
        foreach (var f in findings)
        {
            if (suppressions.Find(f) is { } s) hidden.Add((f, s.By, s.ByPath));
            else kept.Add(f);
        }
        return (kept, hidden);
    }

    /// <summary>A notice for each skarp suppression without a justification, at the attribute.</summary>
    static IEnumerable<Finding> Unjustified(SuppressionIndex suppressions, Func<string, ActiveRule> ruleOf)
    {
        foreach (var (path, a) in suppressions.Unjustified)
        {
            var rule = ruleOf(path);
            if (rule.Notice is not { } level) continue;
            var place = $"suppress {a.Rule} on {a.Place}";
            yield return new Finding
            {
                Rule = RuleIds.SuppressionWithoutJustification, Severity = "notice", Path = path, Line = a.Line, Member = place, PlaceKey = place,
                Preset = rule.NoticePreset, ThresholdSource = level.Source,
                Message = $"[SuppressMessage(\"skarp\", \"{a.Rule}\")] on {a.Place} has no justification, so it does not count; add Justification = \"<why>\"",
            };
        }
    }

    static void AddPlace(List<Finding> list, Finding? f, string? place)
    {
        if (f is null) return;
        f.PlaceKey = place;
        f.TypePlace = place is not null;
        list.Add(f);
    }

    /// <summary>Level of a finding's place (order within a file): 0 member, 1 type, 2 file.</summary>
    public static int PlaceLevel(Finding f) => f.Member is null ? 2 : f.TypePlace ? 1 : 0;

    /// <summary>
    /// Finding identity v1: <c>rule|path|place</c>. A second finding with the same id (several empty catch clauses
    /// in one member) gets <c>~2</c>, <c>~3</c>, in evaluation order (source order within the member).
    /// </summary>
    public static void AssignIds(List<Finding> findings)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in findings)
        {
            var id = MakeId(f.Rule, f.PathlessId ? "" : f.Path, f.PlaceKey);
            int n = seen.GetValueOrDefault(id) + 1;
            seen[id] = n;
            f.Id = n == 1 ? id : $"{id}~{n}";
            f.Handle = HandleOf(f.Id);
        }
    }

    /// <summary>First 8 lowercase hex digits of SHA-256 over the id's UTF-8 bytes; shell-safe and
    /// the same for the same finding in every run.</summary>
    public static string HandleOf(string id) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)))[..8];

    public static string MakeId(string rule, string path, string? place) => $"{rule}|{path}|{place}";

    /// <summary>Place of a finding (one report entry per place): member key, type name, or the file itself.</summary>
    public static string Place(Finding f) => f.PlaceKey ?? f.Member ?? "";

    /// <summary>
    /// How far a finding is over its limit (value divided by threshold). Findings without a numeric threshold
    /// (empty catch, magic numbers) count as exactly at the limit (1). NPath grows exponentially with sequential
    /// branches, so its distance is taken on log2 (NPath 2^20 against 200 is 2.6 times over, not 5,243 times), which
    /// keeps path explosion from outranking every other rule.
    /// </summary>
    public static double Ratio(Finding f)
    {
        // A composite stand-in is as far over as its worst part.
        if (f.Parts is { Count: > 0 } parts) return parts.Max(Ratio);
        if (f.Value is not double v || f.Threshold is not double t || t <= 0) return 1.0;
        if (f.Metric == "npath" && t > 1) return Math.Log2(Math.Max(v, 1)) / Math.Log2(t);
        return v / t;
    }

    /// <summary>
    /// Report order: files by commits in the history window (most active first; without history
    /// all files tie), then by their worst ratio, then path; within a file one entry per place, member places first,
    /// then type places, then the file itself, each by the place's worst ratio, then its worst severity, then
    /// first line; within a place by rule order, then line. JSON keeps this order.
    /// </summary>
    public static void Sort(List<Finding> findings)
    {
        var fileWorst = new Dictionary<string, double>(StringComparer.Ordinal);
        var place = new Dictionary<(string, string), (double Ratio, int Severity, int Line)>();
        foreach (var f in findings)
        {
            double r = Ratio(f);
            fileWorst[f.Path] = Math.Max(fileWorst.GetValueOrDefault(f.Path), r);
            var key = (f.Path, Place(f));
            int sev = SeverityRank(f.Severity);
            place[key] = place.TryGetValue(key, out var p)
                ? (Math.Max(p.Ratio, r), Math.Min(p.Severity, sev), Math.Min(p.Line, f.Line))
                : (r, sev, f.Line);
        }
        findings.Sort((a, b) =>
        {
            int c = (b.Commits ?? 0).CompareTo(a.Commits ?? 0);
            if (c != 0) return c;
            if (a.Path != b.Path)
            {
                c = fileWorst[b.Path].CompareTo(fileWorst[a.Path]);
                return c != 0 ? c : string.CompareOrdinal(a.Path, b.Path);
            }
            string pa = Place(a), pb = Place(b);
            if (pa != pb)
            {
                c = PlaceLevel(a).CompareTo(PlaceLevel(b));
                if (c != 0) return c;
                var (x, y) = (place[(a.Path, pa)], place[(b.Path, pb)]);
                c = y.Ratio.CompareTo(x.Ratio);
                if (c != 0) return c;
                c = x.Severity.CompareTo(y.Severity);
                if (c != 0) return c;
                c = x.Line.CompareTo(y.Line);
                return c != 0 ? c : string.CompareOrdinal(pa, pb);
            }
            c = RuleCatalog.Get(a.Rule).Order.CompareTo(RuleCatalog.Get(b.Rule).Order);
            if (c != 0) return c;
            c = a.Line.CompareTo(b.Line);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Message, b.Message);
            return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
        });
    }

    public static int SeverityRank(string severity) => severity == "warning" ? 0 : 1;

    /// <summary>Findings per rule and severity, in the order of <paramref name="rules"/> (default: the syntax rules).</summary>
    public static List<FindingCount> Counts(IEnumerable<Finding> findings, IReadOnlyList<RuleInfo>? rules = null)
    {
        rules ??= RuleCatalog.All;
        var counts = rules.ToDictionary(r => r.Id, r => new FindingCount { Rule = r.Id }, StringComparer.Ordinal);
        foreach (var f in findings)
        {
            if (f.Severity == "warning") counts[f.Rule].Warning++;
            else counts[f.Rule].Notice++;
        }
        return [.. rules.Select(r => counts[r.Id])];
    }
}
