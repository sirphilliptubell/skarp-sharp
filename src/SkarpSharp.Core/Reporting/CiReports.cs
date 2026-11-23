using System.Globalization;
using SkarpSharp.Findings;
using SkarpSharp.Model;

namespace SkarpSharp.Reporting;

/// <summary>
/// CI outputs: a Markdown summary for the GitHub Actions job summary (<c>$GITHUB_STEP_SUMMARY</c>) and GitHub
/// Workflow commands that annotate new and worsened findings. Both write to any TextWriter.
/// </summary>
public static class CiReports
{
    /// <summary>
    /// GitHub shows at most 10 annotations of each type (error, warning, notice) per step and 50 per job (see "Limitations" in the
    /// actions/toolkit repository's docs/problem-matchers.md). Commands beyond that are dropped silently.
    /// </summary>
    public const int AnnotationsPerTypePerStep = 10;

    /// <summary>Rows of the Markdown table of new and worsened findings (the step summary is limited to 1 MiB).</summary>
    public const int MarkdownMaxRows = 100;

    static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    static string Count(long n, string noun) => $"{N(n)} {noun}{(n == 1 ? "" : "s")}";

    /// <summary>New and worsened findings among the listed ones (severity filter and magic-number mode apply).</summary>
    public static List<Finding> Changed(AnalysisResult r, ReportOptions o)
    {
        var listed = HumanReport.Eligible(r, o with { NewOnly = false });
        return [.. listed.Where(f => f.BaselineState is "new" or "worsened")];
    }

    /// <summary>Which of the changed findings get an annotation: report order, up to the per-type limit of each severity.</summary>
    public static (List<Finding> Annotated, int Dropped) Annotations(IReadOnlyList<Finding> changed)
    {
        var list = new List<Finding>();
        int warnings = 0, notices = 0;
        foreach (var f in changed)
        {
            if (f.Severity == "warning" ? warnings++ < AnnotationsPerTypePerStep : notices++ < AnnotationsPerTypePerStep) list.Add(f);
        }
        return (list, changed.Count - list.Count);
    }

    // ---- Markdown -----------------------------------------------------------------------------------------------------

    /// <summary>Escapes text for a Markdown table cell (pipes, line breaks, HTML-like angle brackets).</summary>
    static string Cell(string s) => s.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>Code span for names (lambdas, generics and <c>&lt;top-level&gt;</c> stay literal).</summary>
    static string Code(string s) => "`" + s.Replace("`", "'", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal) + "`";

    /// <summary>The Markdown line without a baseline file (suggests committing one).</summary>
    static readonly string NoBaselineMarkdown =
        $"No baseline file: findings are not marked new or worsened, and `--format github` annotates warnings only (the first {AnnotationsPerTypePerStep}). " +
        "Suggestion: run `skarp baseline` and commit `.skarp-baseline.json`, so CI shows only new and worsened findings.";

    public static void WriteMarkdown(TextWriter w, AnalysisResult r, ReportOptions o)
    {
        var all = r.Findings ?? [];
        int warnings = all.Count(f => f.Severity == "warning");
        int places = all.Select(f => (f.Path, FindingEngine.Place(f))).Distinct().Count();
        int files = all.Select(f => f.Path).Distinct(StringComparer.Ordinal).Count();
        w.WriteLine($"## skarp analyze: {N(r.Repository.Files)} files");
        w.WriteLine();
        w.WriteLine($"{N(all.Count)} findings worth a look ({N(warnings)} warning, {N(all.Count - warnings)} notice) at {N(places)} places in {N(files)} files. " +
            "Findings are places worth a look, not violations.");
        if (HumanReport.CompositeLine(r) is { } composites) { w.WriteLine(); w.WriteLine(composites); }
        if (r.Coverage is { } cov)
        {
            w.WriteLine();
            w.WriteLine("Coverage: " + HumanReport.CoverageLine(cov) + ".");
            foreach (var warning in cov.Warnings) w.WriteLine($"- warning: {Cell(Coverage.CoverageBaseline.Describe(warning))}");
        }
        w.WriteLine();
        HealthReport.WriteMarkdown(w, r);
        if (r.Gate is { } g)
        {
            w.WriteLine(g.Tripped ? $"**Gate tripped**: {Gate.Describe(g, LimitText.Describe(g.Health))["gate tripped: ".Length..]}" : $"**Gate passed**: {Gate.Describe(g, LimitText.Describe(g.Health))["gate passed: ".Length..]}");
            w.WriteLine();
        }

        if (r.Baseline is { } b)
        {
            w.WriteLine("| New | Worsened | Known | Fixed | Possibly renamed |");
            w.WriteLine("|---:|---:|---:|---:|---:|");
            w.WriteLine($"| {N(b.New)} | {N(b.Worsened)} | {N(b.Known)} | {N(b.Fixed)} | {N(b.PossiblyRenamed)} |");
            foreach (var n in b.Notes) { w.WriteLine(); w.WriteLine($"Note: {Cell(n)}"); }
            w.WriteLine();
            var changed = Changed(r, o);
            w.WriteLine($"### New and worsened ({N(changed.Count)})");
            w.WriteLine();
            if (changed.Count == 0) w.WriteLine("None: nothing is new or worse than in the baseline.");
            else
            {
                // The text format's wording (lead, place, rule, numbers, limit, id).
                w.WriteLine("| Change | Place | Rule | Measured | Id |");
                w.WriteLine("|---|---|---|---|---|");
                foreach (var f in changed.Take(MarkdownMaxRows))
                {
                    var lead = TextFormat.Lead(f, true) + (f.PossiblyRenamedFrom is { } from ? $" (possibly renamed from {Code(ReportLayout.RenamedName(from))})" : "");
                    w.WriteLine($"| {lead} | {Code(ReportLayout.PathLine(f.Path, f.Line))} {Code(ReportLayout.ShortName(f))} | {f.Rule}{(f.Severity == "notice" ? " (notice)" : "")} | {Cell(TextFormat.Measured(f, true))} | {Code(f.Handle ?? "")} |");
                }
                if (changed.Count > MarkdownMaxRows) { w.WriteLine(); w.WriteLine($"+{N(changed.Count - MarkdownMaxRows)} more (the JSON output lists every finding)."); }
                var (_, dropped) = Annotations(changed);
                if (dropped > 0)
                {
                    w.WriteLine();
                    w.WriteLine($"Annotations (`--format github`) show the first {AnnotationsPerTypePerStep} warnings and {AnnotationsPerTypePerStep} notices of these " +
                        $"(GitHub's limit per step); {N(dropped)} are listed here only.");
                }
            }
        }
        else
        {
            w.WriteLine(NoBaselineMarkdown);
        }
        w.WriteLine();

        WriteTopPlaces(w, r, o);
        w.WriteLine();
        w.WriteLine($"skarp {r.Run.ToolVersion}; `skarp explain <rule>` explains a rule.");
    }

    /// <summary>The Markdown table of the top places (each file with its health score) and the thresholds they used.</summary>
    static void WriteTopPlaces(TextWriter w, AnalysisResult r, ReportOptions o)
    {
        var view = ReportLayout.Select(HumanReport.Eligible(r, o), o);
        w.WriteLine($"### Top places ({N(view.ShownPlaces)} of {N(view.TotalPlaces)} in {N(view.ShownFiles)} of {N(view.TotalFiles)} files)");
        w.WriteLine();
        if (view.ShownPlaces == 0) w.WriteLine("None.");
        else
        {
            w.WriteLine("| File | Place | Findings |");
            w.WriteLine("|---|---|---|");
            var results = r.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
            foreach (var file in view.Files)
            {
                var head = $"{Code(file.Path)}<br>{(HealthReport.FileScore(results.GetValueOrDefault(file.Path)) is { } hs ? hs + ", " : "")}{(file.Commits is int c ? $"{c} commit{(c == 1 ? "" : "s")}, " : "")}{Count(file.FindingCount, "finding")} at {Count(file.Places.Count, "place")}, worst {ReportLayout.RatioText(file.WorstRatio)}" +
                    (file.Hidden > 0 ? $", +{Count(file.Hidden, "more place")}" : "");
                bool first = true;
                foreach (var p in file.Shown)
                {
                    var list = string.Join("<br>", p.Findings.Select(f => Cell(TextFormat.Compact(f, r.Baseline is not null))));
                    w.WriteLine($"| {(first ? head : "")} | {Code(ReportLayout.PathLine(p.Path, p.Line))} {Code(ReportLayout.ShortName(p.First))} | {list} |");
                    first = false;
                }
            }
        }
        var rows = ReportLayout.ThresholdsUsed(view.Files.SelectMany(f => f.Shown).SelectMany(p => p.Findings));
        if (rows.Count > 0)
        {
            w.WriteLine();
            w.WriteLine("<details><summary>Thresholds used</summary>");
            w.WriteLine();
            w.WriteLine("| Rule | Level | Limit | Source |");
            w.WriteLine("|---|---|---|---|");
            foreach (var (rule, sev, limit, source) in rows) w.WriteLine($"| {rule} | {sev} | {Cell(limit)} | {Cell(source)} |");
            w.WriteLine();
            w.WriteLine("</details>");
        }
    }

    // ---- GitHub workflow commands -------------------------------------------------------------------------------------

    /// <summary>One <c>::warning</c> or <c>::notice</c> command; <paramref name="state"/> is the baseline mark (null without a baseline).</summary>
    static void WriteAnnotation(TextWriter w, Finding f, string? state, string pathPrefix)
    {
        var message = $"{ReportLayout.ShortName(f)}: {ReportLayout.Measure(f)}{(state is null ? "" : $" [{state}]")}. {RuleCatalog.Get(f.Rule).Short}";
        var props = $"file={EscapeProperty(pathPrefix + f.Path.Replace('\\', '/'))},line={f.Line},title={EscapeProperty($"skarp {f.Rule}")}";
        w.WriteLine($"::{f.Severity} {props}::{EscapeData(message)}");
    }

    /// <summary>Message escaping of workflow commands (actions/toolkit <c>escapeData</c>): %, CR, LF.</summary>
    public static string EscapeData(string s) => s.Replace("%", "%25", StringComparison.Ordinal)
        .Replace("\r", "%0D", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal);

    /// <summary>Property escaping (actions/toolkit <c>escapeProperty</c>): %, CR, LF, ':' and ','.</summary>
    public static string EscapeProperty(string s) => EscapeData(s).Replace(":", "%3A", StringComparison.Ordinal).Replace(",", "%2C", StringComparison.Ordinal);

    /// <summary>
    /// Without a baseline nothing is new or worsened, so warnings are annotated (notices are not), within GitHub's
    /// per-step limit, and the log suggests committing a baseline.
    /// </summary>
    static void WriteWarningsWithoutBaseline(TextWriter w, AnalysisResult r, ReportOptions o, string pathPrefix)
    {
        var warnings = HumanReport.Eligible(r, o with { NewOnly = false }).Where(f => f.Severity == "warning").ToList();
        var annotated = warnings.Take(AnnotationsPerTypePerStep).ToList();
        foreach (var f in annotated) WriteAnnotation(w, f, null, pathPrefix);
        int dropped = warnings.Count - annotated.Count;
        w.WriteLine($"skarp: no baseline file; {N(warnings.Count)} warning{(warnings.Count == 1 ? "" : "s")}, {N(annotated.Count)} annotated" +
            (dropped > 0 ? $", {N(dropped)} not annotated (GitHub shows at most {AnnotationsPerTypePerStep} warnings per step)" : "") +
            "; notices are not annotated without a baseline.");
        w.WriteLine("skarp: suggestion: run `skarp baseline` and commit .skarp-baseline.json, so annotations show only new and worsened findings.");
    }

    /// <summary>
    /// <c>::warning</c> and <c>::notice</c> annotations for new and worsened findings (report order, at most
    /// <see cref="AnnotationsPerTypePerStep"/> per severity); without a baseline, warnings only and a suggestion to
    /// commit one; an <c>::error</c> when the gate tripped, and plain log lines saying how many were not annotated.
    /// <paramref name="pathPrefix"/> turns paths relative to the analyzed folder into paths relative to the repository
    /// root, which GitHub expects.
    /// </summary>
    public static void WriteGitHub(TextWriter w, AnalysisResult r, ReportOptions o, string pathPrefix)
    {
        if (r.Baseline is null) WriteWarningsWithoutBaseline(w, r, o, pathPrefix);
        else
        {
            var changed = Changed(r, o);
            var (annotated, dropped) = Annotations(changed);
            foreach (var f in annotated)
            {
                var state = f.BaselineState == "worsened"
                    ? f.Parts is not null ? "worsened" : $"worsened from {(f.BaselineValue is double v ? Condition.Num(v) : "?")}"
                    : f.PossiblyRenamedFrom is { } from ? $"new, possibly renamed from {ReportLayout.RenamedName(from)}" : "new";
                WriteAnnotation(w, f, state, pathPrefix);
            }
            w.WriteLine($"skarp: {N(changed.Count)} new or worsened finding{(changed.Count == 1 ? "" : "s")}; {N(annotated.Count)} annotated" +
                (dropped > 0 ? $", {N(dropped)} not annotated (GitHub shows at most {AnnotationsPerTypePerStep} warnings and {AnnotationsPerTypePerStep} notices per step; the Markdown summary lists them all)." : "."));
        }
        if (r.Gate is { } g)
        {
            if (g.Tripped) w.WriteLine($"::error title={EscapeProperty("skarp gate")}::{EscapeData(Gate.Describe(g, LimitText.Describe(g.Health)))}");
            else w.WriteLine("skarp: " + Gate.Describe(g, LimitText.Describe(g.Health)));
        }
    }
}
