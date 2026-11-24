using System.Globalization;
using SkarpSharp.Findings;
using SkarpSharp.Health;
using SkarpSharp.Model;

namespace SkarpSharp.Reporting;

/// <summary>
/// <c>skarp analyze</c> in the text format, short by default. The verdict and summary
/// lines, the findings grouped by file (one heading per file with its health, one line per finding, by line), the
/// notes that matter for the result, and the About lines. <c>--verbose</c> adds the run header, the health block, each
/// rule's one-line explanation and the tables (thresholds used, findings per rule).
/// </summary>
public static class TextReport
{
    static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>What the lines of a report collect for the closing About lines.</summary>
    sealed class Shown
    {
        public List<string> Rules { get; } = [];
        public string? FirstId { get; set; }
        public HashSet<string> Explained { get; } = new(StringComparer.Ordinal);
        public bool Scale { get; set; }

        public void Add(Finding f)
        {
            TextFormat.Note(Rules, f.Rule);
            FirstId ??= f.Handle;
        }
    }

    public static void WriteAnalyze(TextWriter w, AnalysisResult r, ReportOptions o)
    {
        var all = r.Findings ?? [];
        bool compared = r.Baseline is not null && (o.NewOnly || r.Gate is not null);
        var shown = new Shown();
        if (r.Gate is { } g) w.WriteLine(LimitText.GateVerdict(g));
        w.WriteLine((r.Gate is null ? "Skarp: " : "") + Summary(r, o, shown));
        var view = all.Count == 0 ? null : o.Group == "rule" ? null : ReportLayout.Select(HumanReport.Eligible(r, o), o);
        w.WriteLine(Counts(r, o, all, view));
        if (o.Verbose) Verbose(w, r, o);
        // The files past a health limit first, with every new or worse finding (notices too).
        bool scale = shown.Scale;
        var limited = LimitText.WriteFiles(w, r, o.GateMaxLines, ref scale, shown.Add);
        shown.Scale = scale;
        if (all.Count > 0)
        {
            if (o.Group == "rule") WriteByRule(w, r, o, compared, shown);
            else WriteByFile(w, r, o, view!, compared, shown, limited);
        }
        SemanticReport.WriteSection(w, r, o, compared, s => shown.Add(s));
        if (o.ShowSuppressed) WriteSuppressed(w, [.. r.Suppressed ?? [], .. r.SemanticTier?.Results.SelectMany(x => x.Suppressed ?? []) ?? []]);
        w.WriteLine();
        Notes(w, r, o);
        if (o.Verbose) VerboseTail(w, r, o, view);
        if (o.ExcludeTestsWhy) { HumanReport.WriteExcluded(w, r); w.WriteLine(); }
        foreach (var line in TextFormat.About(o.ShowIds ? shown.FirstId : null, shown.Rules)) w.WriteLine(line);
    }

    /// <summary><c>analyze src, 19 files, health 9.3 average (10 = healthy)</c>.</summary>
    static string Summary(AnalysisResult r, ReportOptions o, Shown shown)
    {
        var text = $"analyze {o.Directory.Replace('\\', '/')}, {TextFormat.Count(r.Repository.Files, "file")}";
        if (r.Health?.Average is not double a) return text;
        var was = r.Health.Baseline?.Average is double b && HealthText.Shown(b) != HealthText.Shown(a) ? $", was {HealthText.Score(b)}" : "";
        shown.Scale = true;
        return $"{text}, health {HealthText.Score(a)} average{was}{TextFormat.Scale}";
    }

    /// <summary>The counts line: new and worse first (with a baseline), then warnings and notices, places, files, and
    /// what is shown.</summary>
    static string Counts(AnalysisResult r, ReportOptions o, IReadOnlyList<Finding> all, ReportView? view)
    {
        if (all.Count == 0) return "No findings: nothing passed the thresholds in effect.";
        int warnings = all.Count(f => f.Severity == "warning"), notices = all.Count - warnings;
        int places = all.Select(f => (f.Path, FindingEngine.Place(f))).Distinct().Count();
        int files = all.Select(f => f.Path).Distinct(StringComparer.Ordinal).Count();
        var changed = r.Baseline is { } b ? $"{N(b.New)} new, {N(b.Worsened)} worse than the baseline. " : "";
        var levels = warnings == 0 ? $"No warnings. {TextFormat.Count(notices, "notice")}"
            : notices == 0 ? TextFormat.Count(warnings, "warning") : $"{TextFormat.Count(warnings, "warning")}, {TextFormat.Count(notices, "notice")}";
        var showing = view is not null && view.ShownPlaces < view.TotalPlaces ? $"; showing the {N(view.ShownPlaces)} worst" : "";
        var filters = Filters(r, o, all);
        return $"{changed}{levels} at {TextFormat.Count(places, "place")} in {TextFormat.Count(files, "file")}{showing}{(filters.Count > 0 ? $" ({string.Join("; ", filters)})" : "")}.";
    }

    /// <summary>The filters that hide findings from the view.</summary>
    static List<string> Filters(AnalysisResult r, ReportOptions o, IReadOnlyList<Finding> all)
    {
        var filters = new List<string>();
        if (o.MinSeverity == Severity.Warning) filters.Add("warnings only");
        if (o.NewOnly && r.Baseline is not null) filters.Add("new or worse only");
        if (!(o.All || o.ListMagicNumbers) && all.Any(f => f.Rule == RuleIds.MagicNumber)) filters.Add("magic numbers counted, not listed");
        return filters;
    }

    /// <summary>The worst places per file, files ordered by health (with a comparison the largest drop first,
    /// else the lowest score first), findings by line.</summary>
    static void WriteByFile(TextWriter w, AnalysisResult r, ReportOptions o, ReportView view, bool compared, Shown shown, HashSet<string> written)
    {
        var results = r.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var files = view.Files.Where(f => f.Shown.Count > 0 && !written.Contains(f.Path)).ToList();
        files.Sort((a, b) => FileOrder(results.GetValueOrDefault(a.Path)?.Health, results.GetValueOrDefault(b.Path)?.Health, compared) is var c && c != 0 ? c : string.CompareOrdinal(a.Path, b.Path));
        foreach (var file in files)
        {
            w.WriteLine();
            var health = results.GetValueOrDefault(file.Path)?.Health;
            bool scale = shown.Scale;
            w.WriteLine(TextFormat.Heading(file.Path, compared ? health?.BaselineScore : null, health?.Score, ref scale));
            shown.Scale = scale;
            foreach (var f in file.Shown.SelectMany(p => p.Findings).OrderBy(f => f.Line)) WriteFinding(w, f, null, o, compared, shown);
            if (file.Hidden > 0) w.WriteLine(TextFormat.MoreInFile(file.Hidden, file.CutByTop ? "--top or --all lists them" : "--per-file 0 or --all lists them"));
        }
    }

    /// <summary>Files by the largest drop (compared) or the lowest score.</summary>
    static int FileOrder(FileHealth? a, FileHealth? b, bool compared)
    {
        double Key(FileHealth? h) => h is null ? 10 : compared ? HealthText.Shown(h.Score) - HealthText.Shown(h.BaselineScore ?? h.Score) : h.Score;
        return Key(a).CompareTo(Key(b));
    }

    /// <summary>The top findings grouped by rule (<c>--group rule</c>): the file before <c>line</c>.</summary>
    static void WriteByRule(TextWriter w, AnalysisResult r, ReportOptions o, bool compared, Shown shown)
    {
        var eligible = HumanReport.Eligible(r, o);
        var listed = o.All ? eligible : [.. eligible.Take(Math.Max(0, o.Top))];
        foreach (var rule in RuleCatalog.All.Concat(RuleCatalog.Composite))
        {
            var group = listed.Where(f => f.Rule == rule.Id).ToList();
            if (group.Count == 0) continue;
            w.WriteLine();
            w.WriteLine(o.Verbose ? $"{rule.Id}: {rule.Short}" : rule.Id);
            shown.Explained.Add(rule.Id);
            foreach (var f in group) WriteFinding(w, f, f.Path, o, compared, shown);
        }
        if (listed.Count < eligible.Count) w.WriteLine($"+{N(eligible.Count - listed.Count)} more (--top or --all lists them)");
    }

    /// <summary>One finding's line; with <c>--verbose</c> its rule's sentence once; with <c>--all</c> every copy of a clone
    /// group and a composite's parts.</summary>
    static void WriteFinding(TextWriter w, Finding f, string? file, ReportOptions o, bool compared, Shown shown)
    {
        w.WriteLine(TextFormat.FindingLine(f, compared, file));
        shown.Add(f);
        if (o.All && f.Clone is { Instances.Count: > 3 } c)
            foreach (var i in c.Instances) w.WriteLine($"      copy {ReportLayout.PathLine(i.Path, i.StartLine)}-{i.EndLine}{(i.Member is { } m ? "  " + m : "")}");
        if (o.Verbose && shown.Explained.Add(f.Rule)) w.WriteLine($"      {RuleCatalog.Get(f.Rule).Short}");
        if (o.All && f.Parts is { } parts) WriteParts(w, parts, compared, 1);
    }

    /// <summary>The parts of a composite under its line (<c>--all</c>), a brain class's brain methods with their own parts.</summary>
    static void WriteParts(TextWriter w, List<Finding> parts, bool compared, int depth)
    {
        var indent = new string(' ', 2 * depth);
        foreach (var p in parts)
        {
            w.WriteLine(indent + "  part" + TextFormat.FindingLine(p, compared)[1..]);
            if (p.Parts is { } inner) WriteParts(w, inner, compared, depth + 1);
        }
    }

    /// <summary>The findings inline suppression left out.</summary>
    static void WriteSuppressed(TextWriter w, IReadOnlyList<SuppressedFinding> suppressed)
    {
        w.WriteLine();
        w.WriteLine($"Suppressed ({N(suppressed.Count)}; not in the findings, the gate or the score):");
        foreach (var s in suppressed.DistinctBy(s => s.Id))
            w.WriteLine(new TextLine(TextFormat.Level(s.Severity), s.Rule, $"{s.Message} (by {s.SuppressedBy}: {s.Justification})") { File = s.Path, Line = s.Line }.Render());
    }

    /// <summary>The notes that matter for the result, one short line each: no baseline, the baseline's notes, history and
    /// semantic analysis off, and where the run details are.</summary>
    static void Notes(TextWriter w, AnalysisResult r, ReportOptions o)
    {
        if (r.Baseline is null)
            w.WriteLine(o.NewOnly ? "No baseline file: --new-only shows everything (`skarp baseline` records one)."
                : "No baseline file: `skarp baseline` records one; later runs then mark findings new or worse.");
        foreach (var n in r.Baseline?.Notes ?? []) w.WriteLine($"Baseline: {n}.");
        if (r.Gate?.Health?.Note is { } limits) w.WriteLine($"Health limits: {limits}.");
        if (o.Verbose) return;
        bool history = r.Run.Git is not null && r.History is not null;
        bool semantic = r.Run.Semantic is { Status: "ran" };
        var off = (history, semantic) switch
        {
            (false, false) => "History and semantic analysis were off. ",
            (false, true) => "History was off. ",
            (true, false) => "Semantic analysis was off. ",
            _ => "",
        };
        w.WriteLine($"{off}`skarp analyze --verbose` shows the run details.");
    }

    /// <summary><c>--verbose</c>: the run header, the variant details with <c>--all</c>, the composite line and the
    /// health block, before the findings.</summary>
    static void Verbose(TextWriter w, AnalysisResult r, ReportOptions o)
    {
        w.WriteLine();
        HumanReport.WriteHeader(w, "analyze", r);
        if (o.All) HumanReport.WriteVariantDetails(w, r);
        if (HumanReport.CompositeLine(r) is { } composites) w.WriteLine(composites);
        // The health block ends with a blank line of its own; the file groups that follow start with one.
        var health = new StringWriter();
        HealthReport.WriteHuman(health, r);
        var lines = health.ToString().TrimEnd().Split('\n');
        if (lines is [{ Length: 0 }]) return;
        w.WriteLine();
        foreach (var line in lines) w.WriteLine(line.TrimEnd('\r'));
    }

    /// <summary><c>--verbose</c>: the thresholds used by the findings shown, the findings per rule, and the options.</summary>
    static void VerboseTail(TextWriter w, AnalysisResult r, ReportOptions o, ReportView? view)
    {
        var shownFindings = view is not null ? view.Files.SelectMany(f => f.Shown).SelectMany(p => p.Findings) : HumanReport.Eligible(r, o);
        w.WriteLine();
        if (HumanReport.WriteThresholdsUsed(w, shownFindings)) w.WriteLine();
        HumanReport.WriteCounts(w, r.FindingCounts ?? []);
        w.WriteLine();
        w.WriteLine("More: --all lists every place and finding, --per-file <n>, --sort health, --group rule, --min-severity warning, --new-only,");
        w.WriteLine("      `skarp explain <rule>`; --format json|markdown|github|sarif; --gate; `skarp config` shows the settings in effect.");
        w.WriteLine();
    }
}
