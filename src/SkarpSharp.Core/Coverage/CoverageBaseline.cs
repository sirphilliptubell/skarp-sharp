using SkarpSharp.Baselines;
using SkarpSharp.Findings;
using SkarpSharp.Model;

namespace SkarpSharp.Coverage;

/// <summary>
/// The "not evaluated" state of the coverage rule. A run without a report, or a file the reports
/// say nothing about, cannot judge recorded untested-complex-method findings: the comparison neither fixes them nor
/// counts them new, and <c>skarp baseline</c> keeps them as recorded.
/// </summary>
public static class CoverageBaseline
{
    static readonly string Prefix = RuleIds.UntestedComplexMethod + "|";

    /// <summary>The ids of recorded findings this run cannot judge: every untested-complex-method id without a report, and
    /// with one those of files without coverage data.</summary>
    public static Func<string, bool> NotEvaluated(AnalysisResult result)
    {
        if (result.Coverage is null) return id => id.StartsWith(Prefix, StringComparison.Ordinal);
        var withData = result.Files.Where(f => f.Coverage?.Status == CoverageFacts.Data).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        return id => id.StartsWith(Prefix, StringComparison.Ordinal) && !withData.Contains(PathOf(id));
    }

    static string PathOf(string id)
    {
        int a = id.IndexOf('|'), b = a < 0 ? -1 : id.IndexOf('|', a + 1);
        return b < 0 ? "" : id[(a + 1)..b];
    }

    /// <summary>The entries of the existing baseline file that <paramref name="notEvaluated"/> keeps, as findings to write
    /// again; none when the file is missing or unreadable.</summary>
    public static List<Finding> Kept(string baselinePath, Func<string, bool> notEvaluated)
    {
        if (!File.Exists(baselinePath)) return [];
        BaselineFile file;
        try { file = Baseline.Read(baselinePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return []; }
        return [.. file.Findings.Where(e => notEvaluated(e.Id)).Select(e => new Finding { Id = e.Id, Rule = RuleIds.UntestedComplexMethod, Value = e.Value })];
    }

    /// <summary>The "untested" label of activity hotspots whose file has an untested-complex-method finding.</summary>
    public static void LabelHotspots(AnalysisResult result)
    {
        if (result.Coverage is null || result.Hotspots is not { } hotspots) return;
        var untested = (result.Findings ?? []).Where(f => f.Rule == RuleIds.UntestedComplexMethod).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var h in hotspots) if (untested.Contains(h.Path)) h.Untested = true;
    }

    /// <summary>One warning in words, with the files it names.</summary>
    public static string Describe(CoverageWarning w)
    {
        if (w.Files is not { Count: > 0 } files || w.Kind == "no-match") return w.Message;
        int more = (w.Count ?? files.Count) - files.Count;
        return $"{w.Message}: {string.Join(", ", files)}{(more > 0 ? $" and {more} more" : "")}";
    }
}
