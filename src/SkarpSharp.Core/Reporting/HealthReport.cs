using System.Globalization;
using SkarpSharp.Health;
using SkarpSharp.Model;

namespace SkarpSharp.Reporting;

/// <summary>
/// The "Code health" block of the human report and the Markdown summary: average and hotspot
/// health (with the baseline values when there is one), the worst files with their main contributions, and the files
/// whose score changed since the baseline.
/// </summary>
public static class HealthReport
{
    /// <summary>Files listed as changed since the baseline.</summary>
    public const int ChangedShown = 10;

    static string Kind(HealthSummary h) => h.Kind == HealthEngine.Full ? "full scores (both tiers)" : "syntax scores";

    static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The baseline part of a value: <c>(baseline 7.6, -0.2)</c>, or, when the baseline covers fewer files than
    /// the run (files created since are left out), <c>(baseline 7.6 over the 97 files it had, 7.4 now: -0.2)</c>.</summary>
    static string Versus(double? now, double? before, string? subset)
    {
        if (now is not double n || before is not double b) return "";
        double d = HealthText.Shown(n) - HealthText.Shown(b);
        var change = Math.Abs(d) < 0.05 ? "unchanged" : HealthText.Delta(d);
        return subset is null ? $" (baseline {HealthText.Score(b)}, {change})" : $" (baseline {HealthText.Score(b)} over {subset}, {HealthText.Score(n)} now: {change})";
    }

    /// <summary>The average line: <c>average 7.4 over 120 files (baseline ...); hotspots 6.9 (baseline ...)</c>.</summary>
    public static string Averages(HealthSummary h)
    {
        var b = h.Baseline;
        bool subset = b is not null && b.Files < h.Files;
        var average = h.Average is double a ? HealthText.Score(a) : "none";
        var files = subset ? $"the {N(b!.Files)} file{(b.Files == 1 ? "" : "s")}" : null;
        var versus = Versus(b?.AverageNow, b?.Average, subset ? $"{files} it had" : null);
        var hot = h.Hotspot is double hs ? $"hotspots {HealthText.Score(hs)}{Versus(b?.HotspotNow, b?.Hotspot, subset ? "the hotspots it had" : null)}" : h.HotspotNote ?? "";
        return $"average {average} over {N(h.Files)} files{versus}; {hot}";
    }

    /// <summary>Files whose shown score differs from the baseline's, worst drop first.</summary>
    public static List<(FileResult File, double Delta)> Changed(AnalysisResult r) =>
        [.. r.Files.Where(f => f.Health is { BaselineScore: double })
            .Select(f => (f, HealthText.Shown(f.Health!.Score) - HealthText.Shown(f.Health.BaselineScore!.Value)))
            .Where(x => Math.Abs(x.Item2) >= 0.05)
            .OrderBy(x => x.Item2).ThenBy(x => x.f.Path, StringComparer.Ordinal)];

    /// <summary>Files the baseline does not hold (created since), lowest score first.</summary>
    public static List<FileResult> NotInBaseline(AnalysisResult r)
    {
        if (r.Health?.Baseline is null) return [];
        return [.. r.Files.Where(f => f.Health is { InBaseline: false })
            .OrderBy(f => f.Health!.Score).ThenBy(f => f.Path, StringComparer.Ordinal)];
    }

    /// <summary>The heading of the files created since the baseline.</summary>
    const string NotInLabel = "new since the baseline";

    public static void WriteHuman(TextWriter w, AnalysisResult r)
    {
        if (r.Health is not { } h) return;
        w.WriteLine($"Code health ({h.Model} model, {Kind(h)}; 1 to 10, 10 = healthy; informs only):");
        w.WriteLine($"  {Averages(h)}");
        if (h.Worst.Count == 0) w.WriteLine($"  no file scores below {Below(r)}.");
        else
        {
            w.WriteLine($"  worst files (below {Below(r)}):");
            foreach (var f in h.Worst)
                w.WriteLine($"    {HealthText.Score(f.Score),4} {f.Band,-11}  {f.Path}  ({HealthText.Contributions(f.Contributions)})");
        }
        var changed = Changed(r);
        if (changed.Count > 0)
        {
            w.WriteLine($"  changed since the baseline ({changed.Count} file{(changed.Count == 1 ? "" : "s")}):");
            foreach (var (f, d) in changed.Take(ChangedShown))
                w.WriteLine($"    {HealthText.Score(f.Health!.BaselineScore!.Value)} -> {HealthText.Score(f.Health.Score)} ({HealthText.Delta(d)})  {f.Path}");
            if (changed.Count > ChangedShown) w.WriteLine($"    +{changed.Count - ChangedShown} more (the JSON output has every file's baselineScore)");
        }
        var added = NotInBaseline(r);
        if (added.Count > 0)
        {
            w.WriteLine($"  {NotInLabel} ({added.Count} file{(added.Count == 1 ? "" : "s")}, lowest first):");
            foreach (var f in added.Take(ChangedShown)) w.WriteLine($"    {HealthText.Score(f.Health!.Score),4}  {f.Path}");
            if (added.Count > ChangedShown) w.WriteLine($"    +{added.Count - ChangedShown} more (inBaseline false in the JSON output)");
        }
        w.WriteLine("  `skarp show --file <path>` lists where a file's points went.");
        w.WriteLine();
    }

    static string Below(AnalysisResult r) => ScoreNumber(WorstBelow(r));

    static string ScoreNumber(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    static double WorstBelow(AnalysisResult r) => r.Health?.WorstBelow ?? ScoreSettings.DefaultWorstBelow;

    public static void WriteMarkdown(TextWriter w, AnalysisResult r)
    {
        if (r.Health is not { } h) return;
        w.WriteLine("### Code health");
        w.WriteLine();
        w.WriteLine($"{Averages(h)} ({Kind(h)}, 1 to 10, 10 = healthy; informs only).");
        w.WriteLine();
        if (h.Worst.Count == 0) w.WriteLine($"No file scores below {Below(r)}.");
        else
        {
            w.WriteLine("| Score | File | Main contributions |");
            w.WriteLine("|---:|---|---|");
            foreach (var f in h.Worst) w.WriteLine($"| {HealthText.Score(f.Score)} | `{f.Path}` | {HealthText.Contributions(f.Contributions)} |");
        }
        var changed = Changed(r);
        if (changed.Count > 0)
        {
            w.WriteLine();
            w.WriteLine($"Changed since the baseline: " + string.Join(", ", changed.Take(ChangedShown).Select(x =>
                $"`{x.File.Path}` {HealthText.Score(x.File.Health!.BaselineScore!.Value)} -> {HealthText.Score(x.File.Health.Score)}")) +
                (changed.Count > ChangedShown ? $", +{changed.Count - ChangedShown} more." : "."));
        }
        var added = NotInBaseline(r);
        if (added.Count > 0)
        {
            w.WriteLine();
            w.WriteLine($"{char.ToUpperInvariant(NotInLabel[0])}{NotInLabel[1..]} ({added.Count}): " + string.Join(", ", added.Take(ChangedShown).Select(f =>
                $"`{f.Path}` {HealthText.Score(f.Health!.Score)}")) + (added.Count > ChangedShown ? $", +{added.Count - ChangedShown} more." : "."));
        }
        w.WriteLine();
    }

    /// <summary>The score shown on a file line of the report: <c>health 6.9</c>.</summary>
    public static string? FileScore(FileResult? f) => f?.Health is { } h ? $"health {HealthText.Score(h.Score)}" : null;
}
