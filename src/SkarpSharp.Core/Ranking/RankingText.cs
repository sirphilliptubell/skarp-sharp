using SkarpSharp.Configuration;
using System.Globalization;
using SkarpSharp.Health;
using SkarpSharp.Model;
using SkarpSharp.Reporting;

namespace SkarpSharp.Ranking;

/// <summary>Where a ranking is printed: the closing lines differ (the CLI names its commands, the MCP answer its tools).</summary>
public enum RankingFooter { Cli, Mcp }

/// <summary>
/// A ranking in the text format. The first line says what was ranked
/// and how; each entry is a heading (a file with its health and band, or a method with its measure) and at most
/// <see cref="WorstCode.PlacesPerEntry"/> finding lines with ids, then a fold line; the closing lines state how many of the
/// listed entries are healthy, what the syntax tier did not look at, and where to read about a rule. Facts, not orders.
/// </summary>
public static class RankingText
{
    /// <summary>The rule families only the semantic tier looks at.</summary>
    public const string NotLookedAt = "Not looked at at syntax depth: class coupling, god classes, unused code, namespace cycles.";

    static string N(int v) => v.ToString("N0", CultureInfo.InvariantCulture);

    public static void Write(TextWriter w, Ranking r, RankingFooter footer, bool showIds = false)
    {
        w.WriteLine(FirstLine(r));
        foreach (var s in r.Scope.Skipped) w.WriteLine($"Skipped: {s.Path} ({s.Reason})");
        if (r.NoHistory) w.WriteLine("No git history in this run: sorted by health instead.");
        if (r.Reused) w.WriteLine("From the saved run: no analyzed file and no setting changed since it was made.");
        if (r.Entries.Count == 0) w.WriteLine(Empty(r));
        var shown = WriteEntries(w, r, footer);
        w.WriteLine();
        if (r.Entries.Count > 0) w.WriteLine(HealthyLine(r));
        if (r.NotEvaluated is { } reason)
            w.WriteLine($"Semantic tier: not evaluated: {reason}" + (footer == RankingFooter.Cli && r.NotEvaluatedHint is { } hint ? $" ({hint})" : ""));
        if (r.Tier == "syntax") w.WriteLine(NotLookedAt);
        if (footer == RankingFooter.Mcp) WriteMcpFooter(w, r, shown.Rules, shown.FirstId);
        else WriteCliFooter(w, r, shown.Rules, showIds ? shown.FirstId : null);
    }

    /// <summary>Each entry: its heading, its worst findings by line, the fold line (in an MCP answer: the findings call
    /// that lists the file); the rules shown and the first id.</summary>
    static (List<string> Rules, string? FirstId) WriteEntries(TextWriter w, Ranking r, RankingFooter footer)
    {
        bool scale = false;
        string? firstId = null;
        var rules = new List<string>();
        var unit = r.Choice.Unit == RankChoice.Methods ? "method" : "file";
        foreach (var e in r.Entries)
        {
            w.WriteLine();
            w.WriteLine(Heading(r, e, ref scale));
            var shown = e.Findings.Take(WorstCode.PlacesPerEntry).OrderBy(f => f.Line).ToList();
            foreach (var f in shown)
            {
                w.WriteLine(TextFormat.FindingLine(f, compared: false));
                TextFormat.Note(rules, f.Rule);
                firstId ??= f.Handle;
            }
            if (e.Findings.Count <= shown.Count) continue;
            var more = $"{TextFormat.Indent}+{N(e.Findings.Count - shown.Count)} more";
            w.WriteLine(footer == RankingFooter.Mcp ? $"{more}: findings file={e.Path}" : $"{more} in this {unit}");
        }
        return (rules, firstId);
    }

    /// <summary>The MCP answer's last lines: the next page, the findings tool and the explain tool (never a CLI command).</summary>
    static void WriteMcpFooter(TextWriter w, Ranking r, List<string> rules, string? firstId)
    {
        if (r.HasNext) w.WriteLine($"next: offset={r.Offset + r.Entries.Count}");
        if (firstId is not null) w.WriteLine($"About a finding: findings id={firstId}");
        if ((r.Choice.ByRule ? r.Choice.Sort : rules.FirstOrDefault()) is { } about) w.WriteLine($"About a rule and how to fix it: explain rule={about}");
    }

    /// <summary>The CLI's About lines, the sorted-by rule first.</summary>
    static void WriteCliFooter(TextWriter w, Ranking r, List<string> rules, string? firstId)
    {
        if (r.Choice.ByRule) { rules.Remove(r.Choice.Sort); rules.Insert(0, r.Choice.Sort); }
        foreach (var line in TextFormat.About(firstId, rules)) w.WriteLine(line);
    }

    /// <summary><c>Skarp: worst 10 of 42 files with findings in src (syntax tier), sorted by health</c>.</summary>
    public static string FirstLine(Ranking r)
    {
        var what = r.Choice.Unit == RankChoice.Methods ? "methods" : r.Choice.ByRule ? "files" : "files with findings";
        var page = r.Offset > 0 && r.Entries.Count > 0 ? $", entries {N(r.Offset + 1)} to {N(r.Offset + r.Entries.Count)}" : "";
        var by = r.Choice.ByRule ? $"{r.Sort} ({ReportLayout.MetricLabel(r.Metric)})" : r.Sort;
        return $"Skarp: worst {N(r.Entries.Count)} of {N(r.Total)} {what} in {r.Scope.Shown.Replace('\\', '/')} ({r.Tier} tier), sorted by {by}{page}";
    }

    static string Empty(Ranking r) =>
        r.Total > 0 ? $"Nothing at offset {N(r.Offset)}: the ranking has {N(r.Total)} entries."
        : r.Choice.ByRule ? "Nothing to rank: no analyzed C# code here has what this rule measures."
        : "Nothing to rank: no analyzed C# file here has a finding.";

    /// <summary>A file entry's heading (<c>src/A.cs health 5.0 (problematic)</c>, with the measure of a rule sort and the
    /// commits of a hotspot sort), or a method entry's (<c>src/A.cs line 30 Orders.Ship: physical lines 120 (limit 70)</c>).</summary>
    static string Heading(Ranking r, RankEntry e, ref bool scale)
    {
        var measure = r.Choice.ByRule ? Measure(r, e) : "";
        if (r.Choice.Unit == RankChoice.Methods) return $"{e.Path} line {e.Member!.StartLine.ToString(CultureInfo.InvariantCulture)} {WorstCode.MemberName(e.Member)}: {measure}";
        var health = e.File.Health is { } h ? $" health {HealthText.Score(h.Score)} ({h.Band}{(scale ? "" : "; 10 = healthy")})" : "";
        if (health.Length > 0) scale = true;
        if (r.Sort == RankChoice.Hotspot) return $"{e.Path}{health}, {TextFormat.Count(e.Commits, "commit")} in the history window";
        if (!r.Choice.ByRule) return e.Path + health;
        var where = e.Member is { } m ? $" at line {m.StartLine.ToString(CultureInfo.InvariantCulture)} {WorstCode.MemberName(m)}"
            : e.Type is { } t ? $" at line {t.Line.ToString(CultureInfo.InvariantCulture)} type {ReportLayout.ShortType(t.Name)}" : "";
        return $"{e.Path}{health}; {measure}{where}";
    }

    static string Measure(Ranking r, RankEntry e) =>
        $"{ReportLayout.MetricLabel(r.Metric)} {Findings.Condition.Num(e.Value)}" + (e.Limit is { } l ? $" (limit {l})" : " (rule off)");

    /// <summary><c>7 of these 10 are healthy (9 or more)</c>; for methods, the files they are in.</summary>
    static string HealthyLine(Ranking r)
    {
        int healthy = r.Entries.Count(e => e.File.Health?.Band == Bands.Healthy);
        return r.Choice.Unit == RankChoice.Methods
            ? $"{N(healthy)} of these {N(r.Entries.Count)} are in healthy files (9 or more)"
            : $"{N(healthy)} of these {N(r.Entries.Count)} are healthy (9 or more)";
    }
}
