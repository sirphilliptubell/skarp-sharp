using SkarpSharp.Baselines;
using SkarpSharp.Findings;
using SkarpSharp.Model;

namespace SkarpSharp.Health;

/// <summary>What the scores of a run are computed from besides its syntax findings.</summary>
public sealed class HealthInputs
{
    public required ScoreSettings Settings { get; init; }
    /// <summary>The rule settings in effect (judge the severity of baseline entries).</summary>
    public required RuleSettings Rules { get; init; }
    /// <summary>The settings of a path, for re-judging recorded baseline entries; null: <see cref="Rules"/> everywhere.</summary>
    public Func<string, RuleSettings>? RulesOf { get; init; }
    public bool IncludeGenerated { get; init; }
    /// <summary>The semantic document whose findings count (the highest target framework); null when the tier did not run.</summary>
    public SemanticResult? Semantic { get; init; }
    /// <summary>The syntax baseline the run compared with; null without one.</summary>
    public BaselineFile? SyntaxBaseline { get; init; }
    /// <summary>The semantic baseline of <see cref="Semantic"/>'s target; needed for baseline values of <c>full</c> scores.</summary>
    public BaselineFile? SemanticBaseline { get; init; }
}

/// <summary>
/// Scores every analyzed file from its findings and fills the
/// roll-ups. A pure function of the findings and the file sizes; previous scores (the baseline's) are recomputed from
/// the baseline's findings, never stored.
/// </summary>
public static class HealthEngine
{
    public const string Syntax = "syntax", Full = "full";

    /// <summary>The files that get a score: analyzed without error, generated files only with <c>--include-generated</c>.</summary>
    public static IEnumerable<FileResult> Scored(AnalysisResult result, bool includeGenerated) =>
        result.Files.Where(f => f.Error is null && (includeGenerated || !f.Generated));

    /// <summary>Findings by the path of their id; a clone group counts once in every file holding one of its
    /// instances; other path-less findings (namespace-cycle) count nowhere.</summary>
    public static Dictionary<string, List<ScoredFinding>> ByPath(IEnumerable<Finding> findings, Func<string, bool>? tierFilter = null)
    {
        var map = new Dictionary<string, List<ScoredFinding>>(StringComparer.Ordinal);
        foreach (var f in findings)
        {
            if (tierFilter is not null && !tierFilter(f.Rule)) continue;
            foreach (var path in ScoredPaths(f))
            {
                if (!map.TryGetValue(path, out var list)) map[path] = list = [];
                list.Add(ScoredFinding.From(f));
            }
        }
        return map;
    }

    /// <summary>The files a finding's points are taken from: the distinct instance files of a clone group, the path of
    /// other findings with a path in their id, none for other path-less findings.</summary>
    public static IEnumerable<string> ScoredPaths(Finding f)
    {
        if (f.Clone is { } c) return c.Instances.Select(i => i.Path).Distinct(StringComparer.Ordinal);
        if (f.Path.Length == 0 || f.PathlessId || IdPath(f.Id) is not { Length: > 0 }) return [];
        return [f.Path];
    }

    /// <summary>The path part of a finding id (<c>rule|path|place</c>); null when the id has no such part.</summary>
    public static string? IdPath(string id)
    {
        int a = id.IndexOf('|');
        if (a < 0) return null;
        int b = id.IndexOf('|', a + 1);
        return b < 0 ? id[(a + 1)..] : id[(a + 1)..b];
    }

    /// <summary>True for the rules a <c>syntax</c> score counts.</summary>
    public static bool SyntaxRule(string rule) => RuleCatalog.Find(rule)?.Tier != "semantic";

    /// <summary>The syntax score of one file's findings (what <c>skarp check</c> computes).</summary>
    public static ScoreResult SyntaxScore(IScoreModel model, IEnumerable<ScoredFinding> findings, int sourceLines) =>
        model.Score(new ScoreInput([.. findings.Where(f => SyntaxRule(f.Rule))], sourceLines));

    public static void Apply(AnalysisResult result, HealthInputs inputs)
    {
        var model = ScoreModels.Create(inputs.Settings);
        bool full = inputs.Semantic is not null;
        var syntax = ByPath(result.Findings ?? [], SyntaxRule);
        var semantic = ByPath(inputs.Semantic?.Findings ?? []);
        var files = Scored(result, inputs.IncludeGenerated).ToList();
        foreach (var file in files)
        {
            var own = syntax.GetValueOrDefault(file.Path) ?? [];
            var s = model.Score(new ScoreInput(own, file.Lines.Source));
            var score = full ? model.Score(new ScoreInput([.. own, .. semantic.GetValueOrDefault(file.Path) ?? []], file.Lines.Source)) : s;
            file.Health = Dto(score, full ? Full : Syntax, full ? s.Score : null);
        }
        var baseline = BaselineScores(model, inputs, result, full);
        var held = HeldFiles(inputs);
        if (baseline is not null) MarkBaseline(files, baseline, held);
        result.Health = Summary(result, files, inputs.Settings, model.Name, full ? Full : Syntax);
        if (baseline is not null) result.Health.Baseline = BaselineValues(result, files);
    }

    /// <summary>The files the baseline holds: its <c>files</c> list (every analyzed file, so a file without findings is
    /// told apart from a file created since).</summary>
    internal static IReadOnlySet<string> HeldFiles(HealthInputs inputs) =>
        inputs.SyntaxBaseline is { } b ? b.Files.ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Each file's baseline score; <c>inBaseline: false</c> when the baseline's file list does not hold it (created
    /// since).</summary>
    static void MarkBaseline(List<FileResult> files, Dictionary<string, double> baseline, IReadOnlySet<string> held)
    {
        foreach (var file in files)
        {
            if (held.Contains(file.Path)) file.Health!.BaselineScore = HealthText.Json(baseline.GetValueOrDefault(file.Path, BaselineDefault));
            else file.Health!.InBaseline = false;
        }
    }

    /// <summary>The score of a file the baseline holds with no entry.</summary>
    internal const double BaselineDefault = 10;

    /// <summary>Average and hotspot health of the baseline and of the same files now: the files with a baseline score
    /// (those the baseline holds).</summary>
    static HealthBaselineValues BaselineValues(AnalysisResult result, List<FileResult> files)
    {
        var compared = files.Where(f => f.Health!.BaselineScore is not null).ToList();
        var hot = result.Hotspots is { } hs ? new HashSet<string>(hs.Select(h => h.Path), StringComparer.Ordinal) : null;
        var hotFiles = hot is null ? null : compared.Where(f => hot.Contains(f.Path)).ToList();
        static (double, int) Then(FileResult f) => (f.Health!.BaselineScore!.Value, f.Lines.Source);
        static (double, int) Now(FileResult f) => (f.Health!.Score, f.Lines.Source);
        return new HealthBaselineValues
        {
            Files = compared.Count,
            Average = Rollup.Mean(compared.Select(Then)), AverageNow = Rollup.Mean(compared.Select(Now)),
            Hotspot = hotFiles is null ? null : Rollup.Mean(hotFiles.Select(Then)), HotspotNow = hotFiles is null ? null : Rollup.Mean(hotFiles.Select(Now)),
        };
    }

    /// <summary>The JSON form of a score (two decimals).</summary>
    public static FileHealth Dto(ScoreResult r, string kind, double? syntaxScore) => new()
    {
        Score = HealthText.Json(r.Score), Band = r.Band, Kind = kind, Penalty = HealthText.Json(r.Penalty),
        SyntaxScore = syntaxScore is double s ? HealthText.Json(s) : null,
        Contributions = [.. r.Contributions.Select(Rounded)],
    };

    static HealthContribution Rounded(HealthContribution c) =>
        new() { Rule = c.Rule, Severity = c.Severity, Count = c.Count, Points = HealthText.Json(c.Points), Capped = c.Capped };

    /// <summary>Per file, the score recomputed from the baseline's findings (same kind as the run); null when that cannot be done.</summary>
    internal static Dictionary<string, double>? BaselineScores(IScoreModel model, HealthInputs inputs, AnalysisResult result, bool full)
    {
        if (inputs.SyntaxBaseline is not { } sb || (full && inputs.SemanticBaseline is null)) return null;
        var current = (result.Findings ?? []).Concat(inputs.Semantic?.Findings ?? [])
            .GroupBy(f => f.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var entries = sb.Findings.Where(e => full || SyntaxRule(RuleOf(e.Id))).Concat(full ? inputs.SemanticBaseline!.Findings : []);
        var byPath = HealthBaseline.ByPath(entries, current, inputs.Rules, inputs.RulesOf);
        var sizes = result.Files.ToDictionary(f => f.Path, f => f.Lines.Source, StringComparer.Ordinal);
        return byPath.ToDictionary(p => p.Key, p => model.Score(new ScoreInput(p.Value, sizes.GetValueOrDefault(p.Key))).Score, StringComparer.Ordinal);
    }

    static string RuleOf(string id) => id[..Math.Max(0, id.IndexOf('|'))];

    static HealthSummary Summary(AnalysisResult result, List<FileResult> files, ScoreSettings settings, string model, string kind)
    {
        var hot = result.Hotspots is { } hs ? new HashSet<string>(hs.Select(h => h.Path), StringComparer.Ordinal) : null;
        var summary = new HealthSummary
        {
            Model = model, Kind = kind, Files = files.Count, WorstBelow = settings.WorstBelow,
            Average = Rollup.Mean(files.Select(f => (f.Health!.Score, f.Lines.Source))),
            Worst = Rollup.Worst(files, settings),
            Folders = Rollup.Folders(files),
        };
        if (hot is null) summary.HotspotNote = "hotspot health needs git history";
        else
        {
            summary.Hotspot = Rollup.Mean(files.Where(f => hot.Contains(f.Path)).Select(f => (f.Health!.Score, f.Lines.Source)));
            if (summary.Hotspot is null) summary.HotspotNote = "no scored file among the activity hotspots";
        }
        return summary;
    }
}

/// <summary>The roll-ups: size-weighted means, the worst files and the folder means.</summary>
public static class Rollup
{
    /// <summary>sum(score x sourceLines) / sum(sourceLines), files with no source lines skipped; null when none is left.</summary>
    public static double? Mean(IEnumerable<(double Score, int SourceLines)> files)
    {
        double sum = 0;
        long lines = 0;
        foreach (var (score, n) in files)
        {
            if (n <= 0) continue;
            sum += score * n;
            lines += n;
        }
        return lines == 0 ? null : HealthText.Json(sum / lines);
    }

    /// <summary>At most <c>worst.count</c> files below <c>worst.below</c>, lowest first; ties by penalty, then path.</summary>
    public static List<HealthWorstFile> Worst(IEnumerable<FileResult> files, ScoreSettings settings) =>
        [.. files.Where(f => f.Health!.Score < settings.WorstBelow)
            .OrderBy(f => f.Health!.Score).ThenByDescending(f => f.Health!.Penalty).ThenBy(f => f.Path, StringComparer.Ordinal)
            .Take(settings.WorstCount)
            .Select(f => new HealthWorstFile
            {
                Path = f.Path, Score = f.Health!.Score, Band = f.Health.Band, Penalty = f.Health.Penalty, Contributions = [.. f.Health.Contributions.Take(3)],
            })];

    /// <summary>The mean of every folder level that holds scored files with source lines.</summary>
    public static List<FolderHealth> Folders(IEnumerable<FileResult> files)
    {
        var folders = new SortedDictionary<string, (double Sum, long Lines, int Files)>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            int n = f.Lines.Source;
            if (n <= 0) continue;
            for (int i = f.Path.IndexOf('/'); i > 0; i = f.Path.IndexOf('/', i + 1))
            {
                var (sum, lines, count) = folders.GetValueOrDefault(f.Path[..i]);
                folders[f.Path[..i]] = (sum + f.Health!.Score * n, lines + n, count + 1);
            }
        }
        return [.. folders.Select(p => new FolderHealth { Path = p.Key, Average = HealthText.Json(p.Value.Sum / p.Value.Lines), Files = p.Value.Files, SourceLines = p.Value.Lines })];
    }
}
