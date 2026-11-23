using SkarpSharp.Configuration;
using SkarpSharp.Findings;
using SkarpSharp.Model;
using SkarpSharp.Reporting;

namespace SkarpSharp.Ranking;

/// <summary>An input the ranking left out, with the reason (not C#, test code, excluded, outside the root, missing).</summary>
public sealed record SkippedInput(string Path, string Reason);

/// <summary>What one ranking covers: the scope (the files under a folder, or one file) and how it is named.</summary>
public sealed class RankScope
{
    /// <summary>The path as the reader gave it (the first line names it).</summary>
    public required string Shown { get; init; }
    /// <summary>A path relative to the root: a folder prefix ending in <c>/</c>, one file, or empty for the whole root.</summary>
    public string Prefix { get; init; } = "";
    public bool SingleFile { get; init; }
    public List<SkippedInput> Skipped { get; init; } = [];

    public bool Contains(string path) => SingleFile ? path == Prefix : path.StartsWith(Prefix, StringComparison.Ordinal);
}

/// <summary>What to rank: the sort and unit, the scope, the page (<paramref name="Count"/> entries from
/// <paramref name="Offset"/>) and whether generated files count.</summary>
public sealed record RankQuery(RankChoice Choice, RankScope Scope, int Count, int Offset, bool IncludeGenerated = false);

/// <summary>One ranked entry: a file, or a method of a file.</summary>
public sealed class RankEntry
{
    public required FileResult File { get; init; }
    /// <summary>The method of a method entry, or the worst method of a file entry of a method rule.</summary>
    public MemberResult? Member { get; init; }
    /// <summary>The worst type of a file entry of large-type.</summary>
    public TypeSummary? Type { get; init; }
    public double Value { get; init; }
    public string? Limit { get; init; }
    public int Commits { get; init; }
    /// <summary>The entry's findings, worst first (a method entry: the method's own).</summary>
    public required List<Finding> Findings { get; init; }
    public string Path => File.Path;
}

/// <summary>A computed ranking: the choice, the page of entries and what the closing lines say.</summary>
public sealed class Ranking
{
    public required RankChoice Choice { get; init; }
    /// <summary>The sort used: health when hotspot had no git history.</summary>
    public required string Sort { get; init; }
    /// <summary>The metric of a rule sort (<c>physicalLines</c>), else null.</summary>
    public string? Metric { get; init; }
    public required RankScope Scope { get; init; }
    public int Total { get; init; }
    public int Offset { get; init; }
    public required List<RankEntry> Entries { get; init; }
    /// <summary><c>syntax</c> or <c>semantic</c>: the tier the findings and scores come from.</summary>
    public required string Tier { get; init; }
    /// <summary>Why the semantic tier did not run, when it was asked for and did not.</summary>
    public string? NotEvaluated { get; init; }
    public string? NotEvaluatedHint { get; init; }
    /// <summary>True when hotspot was asked for and the run had no git history.</summary>
    public bool NoHistory { get; init; }
    /// <summary>True when the answer came from the saved run (the MCP tool at semantic depth).</summary>
    public bool Reused { get; set; }

    public bool HasNext => Offset + Entries.Count < Total;
}

/// <summary>
/// The worst-first ranking over an analysis result. Health ranks the
/// files with findings (notices count) by score from worst, then path; hotspot orders them as <c>skarp hotspots</c> does
/// (commits in the history window, then the sum of cognitive complexity, then path); a rule ranks every method or file
/// with a measure by that measure, worst first, under every threshold too. Each entry keeps its findings worst first.
/// </summary>
public static class WorstCode
{
    /// <summary>Finding lines per entry before the fold line.</summary>
    public const int PlacesPerEntry = 3;

    /// <summary>The score of a file without one (no findings, so the top of the scale).</summary>
    const double Healthiest = 10;

    /// <summary><c>skarp analyze --sort</c>: the ranking over the whole analyzed folder, <c>--top</c> entries, also added
    /// to the result for the JSON document.</summary>
    public static Ranking Of(SkarpOptions o, AnalysisResult result)
    {
        var ranking = Rank(result, new RankQuery(RankChoice.Resolve(o.Sort, o.Unit).Choice!, new RankScope { Shown = o.Directory }, o.Top, 0, o.IncludeGenerated), o.PathRules());
        result.Ranking = Info(ranking);
        return ranking;
    }

    public static Ranking Rank(AnalysisResult r, RankQuery q, PathRules rules)
    {
        var choice = q.Choice;
        bool noHistory = choice.Sort == RankChoice.Hotspot && r.History is null;
        var sort = noHistory ? RankChoice.Health : choice.Sort;
        var metric = choice.ByRule ? Measures.MetricOf(rules.Base, choice.Sort) : null;
        var all = Entries(r, q, metric is null ? null : new Measures(choice.Sort, metric, rules, r.Types), sort == RankChoice.Hotspot);
        var notRun = r.SemanticTier is { Status: not ("ran" or "off") } t ? t : null;
        return new Ranking
        {
            Choice = choice, Sort = sort, Metric = metric, Scope = q.Scope, Total = all.Count, Offset = q.Offset,
            Entries = [.. all.Skip(q.Offset).Take(Math.Max(0, q.Count))], Tier = r.SemanticTier?.Status == "ran" ? "semantic" : "syntax",
            NoHistory = noHistory, NotEvaluated = notRun?.Reason, NotEvaluatedHint = notRun is null ? null : r.Run.Semantic?.Hint,
        };
    }

    /// <summary>Every entry in rank order: files with findings (no <paramref name="measure"/>), else methods or files by it.</summary>
    static List<RankEntry> Entries(AnalysisResult r, RankQuery q, Measures? measure, bool hotspot)
    {
        var findings = FindingsByFile(r);
        var files = r.Files.Where(f => f.Error is null && (q.IncludeGenerated || !f.Generated) && q.Scope.Contains(f.Path)).ToList();
        if (measure is null) return RankFiles(files, findings, hotspot);
        return q.Choice.Unit == RankChoice.Methods ? RankMethods(files, findings, measure) : RankFilesBy(files, findings, measure);
    }

    /// <summary>The findings of every file, worst first: syntax findings (a composite in place of its parts) and those of
    /// the semantic tier when it ran (the highest target framework's).</summary>
    static Dictionary<string, List<Finding>> FindingsByFile(AnalysisResult r)
    {
        var list = BrainRules.Collapse(r.Findings ?? [], r.Composites);
        if (r.SemanticTier is { Status: "ran", Results: { Count: > 0 } results })
            list.AddRange(results.OrderByDescending(s => s.Run.TargetFramework is { } t ? Semantic.TargetFrameworks.Key(t) : default).First().Findings);
        var byFile = new Dictionary<string, List<Finding>>(StringComparer.Ordinal);
        foreach (var f in list.Where(f => f.Path.Length > 0))
        {
            // Findings read back from a saved run lack the place key (not serialized): recovered from the id.
            Agent.AgentFormat.Restore(f);
            if (!byFile.TryGetValue(f.Path, out var l)) byFile[f.Path] = l = [];
            l.Add(f);
        }
        foreach (var l in byFile.Values) l.Sort(Worst);
        return byFile;
    }

    /// <summary>Warnings first, then how far over the limit, then line.</summary>
    static int Worst(Finding a, Finding b)
    {
        int c = FindingEngine.SeverityRank(a.Severity).CompareTo(FindingEngine.SeverityRank(b.Severity));
        if (c == 0) c = FindingEngine.Ratio(b).CompareTo(FindingEngine.Ratio(a));
        return c != 0 ? c : a.Line.CompareTo(b.Line);
    }

    /// <summary>Files with findings by score (health) or as the hotspot table orders them.</summary>
    static List<RankEntry> RankFiles(List<FileResult> files, Dictionary<string, List<Finding>> findings, bool hotspot)
    {
        var withFindings = files.Where(f => findings.ContainsKey(f.Path)).Select(f => new RankEntry
        {
            File = f, Findings = findings[f.Path], Commits = f.History?.Commits ?? 0,
            Value = hotspot ? f.Members.Where(m => m.Parent is null).Sum(m => (long)m.Cognitive) : f.Health?.Score ?? Healthiest,
        });
        return hotspot
            ? [.. withFindings.OrderByDescending(e => e.Commits).ThenByDescending(e => e.Value).ThenBy(e => e.Path, StringComparer.Ordinal)]
            : [.. withFindings.OrderBy(e => e.Value).ThenBy(e => e.Path, StringComparer.Ordinal)];
    }

    /// <summary>Every eligible method by the rule's measure, worst first.</summary>
    static List<RankEntry> RankMethods(List<FileResult> files, Dictionary<string, List<Finding>> findings, Measures measure)
    {
        var entries = new List<RankEntry>();
        foreach (var f in files)
        {
            var own = findings.GetValueOrDefault(f.Path) ?? [];
            foreach (var m in f.Members)
            {
                if (measure.Of(f, m) is not double v) continue;
                entries.Add(new RankEntry { File = f, Member = m, Value = v, Limit = measure.Limit(f, v, m), Findings = [.. own.Where(x => x.Member == m.Id)] });
            }
        }
        entries.Sort((a, b) => b.Value.CompareTo(a.Value) is var c && c != 0 ? c
            : string.CompareOrdinal(a.Path, b.Path) is var p && p != 0 ? p : a.Member!.StartLine.CompareTo(b.Member!.StartLine));
        return entries;
    }

    /// <summary>Every file with a measure: a method rule by its worst method, large-type by its largest type, large-file by
    /// the file itself.</summary>
    static List<RankEntry> RankFilesBy(List<FileResult> files, Dictionary<string, List<Finding>> findings, Measures measure)
    {
        var entries = new List<RankEntry>();
        foreach (var f in files)
            if (measure.WorstOf(f) is { } worst)
                entries.Add(new RankEntry
                {
                    File = f, Member = worst.Member, Type = worst.Type, Value = worst.Value, Limit = measure.Limit(f, worst.Value, worst.Member),
                    Findings = findings.GetValueOrDefault(f.Path) ?? [],
                });
        entries.Sort((a, b) => b.Value.CompareTo(a.Value) is var c && c != 0 ? c : string.CompareOrdinal(a.Path, b.Path));
        return entries;
    }

    /// <summary>The ranking for the JSON document.</summary>
    public static RankingInfo Info(Ranking ranking) => new()
    {
        Sort = ranking.Sort, Requested = ranking.Sort != ranking.Choice.Sort ? ranking.Choice.Sort : null, Unit = ranking.Choice.Unit,
        Metric = ranking.Metric, Total = ranking.Total, Offset = ranking.Offset,
        Entries = [.. ranking.Entries.Select((e, i) => new RankingEntry
        {
            Rank = ranking.Offset + i + 1, Path = e.Path, Line = e.Member?.StartLine ?? e.Type?.Line,
            Member = e.Member is { } m ? MemberName(m) : e.Type is { } t ? "type " + ReportLayout.ShortType(t.Name) : null,
            Score = e.File.Health?.Score, Band = e.File.Health?.Band,
            Commits = ranking.Sort == RankChoice.Hotspot ? e.Commits : null,
            Value = ranking.Choice.ByRule ? e.Value : null, Limit = e.Limit, Findings = e.Findings.Count,
        })],
    };

    /// <summary>A member's short name (<c>Orders.Ship</c>, <c>Orders.Ship/lambda1</c>).</summary>
    public static string MemberName(MemberResult m) => m.Key.Length > 0 ? ReportLayout.ShortKey(m.Key) : m.Name;
}
