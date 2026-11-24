using System.Globalization;
using SkarpSharp.Findings;
using SkarpSharp.Model;
using SkarpSharp.Semantic;

namespace SkarpSharp.Reporting;

/// <summary>
/// The semantic tier in the reports: one header line (status, solution, frameworks, counts), and in the text
/// report a "Semantic tier" section per output file (findings; with --verbose the types with the highest class coupling,
/// types not evaluated, baseline); a short section for the Markdown and GitHub formats.
/// </summary>
public static class SemanticReport
{
    static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The header line (after <c>settings:</c> and <c>baseline:</c>).</summary>
    public static string HeaderLine(SemanticTierInfo s) => s.Status switch
    {
        "ran" => $"ran on {s.Solution}; target framework{(s.TargetFrameworks?.Count == 1 ? "" : "s")} {string.Join(", ", s.TargetFrameworks ?? [])}; " +
            $"{N(s.Types ?? 0)} types measured, {N(s.TypesNotEvaluated ?? 0)} not evaluated; {N(s.Findings ?? 0)} finding{(s.Findings == 1 ? "" : "s")}" +
            (s.Outputs is { Count: > 0 } o ? $"; written to {string.Join(", ", o)}" : ""),
        "off" => $"off ({s.Reason})",
        _ => $"not evaluated: {s.Reason}" + (s.Hint is null ? "" : $" ({s.Hint})"),
    };

    /// <summary>
    /// The semantic tier in the text report, after the syntax findings: per output file a summary line and its
    /// findings in the text format, grouped by file (<paramref name="shown"/> gets each finding printed); with
    /// <c>--verbose</c> also its baseline and notes, the highest class coupling, the types not evaluated, unused code and
    /// namespace cycles.
    /// </summary>
    public static void WriteSection(TextWriter w, AnalysisResult r, ReportOptions o, bool compared, Action<Finding> shown)
    {
        if (r.SemanticTier is not { } outcome) return;
        if (outcome.NotEvaluatedBaseline is { } nb)
        {
            w.WriteLine();
            w.WriteLine($"Semantic tier: not evaluated ({outcome.Reason}); {N(nb.Entries)} semantic baseline finding{(nb.Entries == 1 ? "" : "s")} not evaluated (neither fixed nor new; the gate ignores them).");
        }
        foreach (var result in outcome.Results)
        {
            w.WriteLine();
            WriteResult(w, result, o, compared, shown);
            if (o.Verbose && result.Namespaces is { } ns) WriteNamespaces(w, ns);
        }
    }

    static void WriteResult(TextWriter w, SemanticResult s, ReportOptions o, bool compared, Action<Finding> shown)
    {
        var tfm = s.Run.TargetFramework is { } t ? $" ({t})" : "";
        w.WriteLine($"Semantic tier{tfm}: {N(s.Types.Count)} types measured in {N(s.Projects.Count)} project{(s.Projects.Count == 1 ? "" : "s")}, {N(s.NotEvaluated.Count)} not evaluated; " +
            $"{N(s.Findings.Count)} finding{(s.Findings.Count == 1 ? "" : "s")}.");
        if (s.Gate is { } g) w.WriteLine(Gate.Describe(g).Replace("in the gate set", "(semantic) in the gate set", StringComparison.Ordinal));
        if (o.Verbose) WriteBaseline(w, s);
        var listed = s.Findings.Where(f => (o.MinSeverity == Severity.Notice || f.Severity == "warning") && (!o.NewOnly || s.Baseline is null || f.BaselineState is "new" or "worsened"))
            .Take(o.All ? int.MaxValue : Math.Max(0, o.Top)).ToList();
        foreach (var file in listed.GroupBy(f => f.Path, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            w.WriteLine(file.Key.Length > 0 ? file.Key : "(namespaces)");
            foreach (var f in file.OrderBy(f => f.Line))
            {
                w.WriteLine(TextFormat.FindingLine(f, compared && s.Baseline is not null));
                shown(f);
            }
        }
        if (listed.Count < s.Findings.Count) w.WriteLine($"+{N(s.Findings.Count - listed.Count)} more (--all lists them; all are in the semantic JSON file)");
        if (o.Verbose) WriteTables(w, s);
    }

    /// <summary>The semantic baseline's counts and notes (<c>--verbose</c>).</summary>
    static void WriteBaseline(TextWriter w, SemanticResult s)
    {
        w.WriteLine("Class coupling and ATFD without framework types (System.*).");
        if (s.Baseline is { } b)
        {
            w.WriteLine($"baseline: {b.Path}: {N(b.Entries)} recorded; {N(b.New)} new, {N(b.Worsened)} worsened, {N(b.Known)} known, {N(b.Fixed)} fixed, {N(b.NotEvaluated ?? 0)} not evaluated");
            if (b.Commit is { } c) w.WriteLine($"note: {c}");
            foreach (var n in b.Notes) w.WriteLine($"note: {n}");
        }
        foreach (var n in s.Run.Notes.Skip(1)) w.WriteLine($"note: {n}");
    }

    /// <summary>The highest class coupling, the types not evaluated and unused code (<c>--verbose</c>).</summary>
    static void WriteTables(TextWriter w, SemanticResult s)
    {
        var top = s.Types.OrderByDescending(x => x.Cbo).ThenBy(x => x.Name, StringComparer.Ordinal).Take(5).ToList();
        if (top.Count > 0)
        {
            w.WriteLine("  Highest class coupling:  cbo  all   ms   ca  atfd   tcc   lcc  wmc");
            foreach (var x in top)
                w.WriteLine($"    {Trim(ReportLayout.ShortType(x.Name), 22),-22} {x.Cbo,4} {x.CboAll,4} {x.CboMicrosoft,4} {x.Ca,4} {x.Atfd,5} {Ratio(x.Tcc),5} {Ratio(x.Lcc),5} {x.Wmc,4}");
        }
        foreach (var reason in s.NotEvaluatedReasons)
            w.WriteLine($"  not evaluated: {N(reason.Types)} type{(reason.Types == 1 ? "" : "s")}, {reason.Reason}" +
                (s.NotEvaluated.FirstOrDefault(x => x.Reason == reason.Reason) is { } ex ? $" (e.g. {ReportLayout.ShortType(ex.Name)}: {ex.Diagnostic})" : ""));
        if (s.UnusedCode is { } u)
        {
            w.WriteLine("  " + UnusedCode.Line(u));
            foreach (var reason in u.NotEvaluatedReasons.Take(5))
                w.WriteLine($"  unused code not evaluated: {N(reason.Count)} candidate{(reason.Count == 1 ? "" : "s")}, {reason.Reason}");
            foreach (var n in u.Notes) w.WriteLine($"  note: {n}");
        }
    }

    const int CyclesShown = 5;
    const int CutsShown = 3;

    /// <summary>One line on the namespace graph, then one per cycle (largest first, at most 5).</summary>
    static void WriteNamespaces(TextWriter w, NamespaceSummary ns)
    {
        var sizes = ns.Components.Count == 0 ? "none" : string.Join(", ", ns.Components.Select(c => N(c.Size)));
        w.WriteLine($"  Namespace cycles: {N(ns.Components.Count)} (sizes {sizes}) over {N(ns.Namespaces)} namespaces, {N(ns.Dependencies)} dependencies, " +
            $"{N(ns.TypePairs)} type pairs; sources {N(ns.SourceFiles)} files ({N(ns.GeneratedFilesExcluded)} generated and {N(ns.TestOrExcludedFiles)} test or excluded left out)");
        foreach (var c in ns.Components.Take(CyclesShown))
            w.WriteLine($"    {c.Place}: {N(c.Size)} namespaces; cut {string.Join(", ", c.Cut.Take(CutsShown).Select(e => $"{e.From} -> {e.To} ({N(e.Weight)})"))}" +
                (c.Cut.Count > CutsShown ? $" and {N(c.Cut.Count - CutsShown)} more" : ""));
        if (ns.Components.Count > CyclesShown) w.WriteLine($"    +{N(ns.Components.Count - CyclesShown)} more (all are in the semantic JSON file)");
        foreach (var n in ns.Notes.Skip(1)) w.WriteLine($"  note: {n}");
    }

    /// <summary>A level's value in <c>skarp config</c>: god-class names its rule set instead of "structural".</summary>
    public static string LevelText(string rule, ActiveRule active, LevelThreshold? level) =>
        level is null ? "blank"
        : rule == RuleIds.GodClass ? (GodClassPresets.Find(active.Parameters.GetValueOrDefault("preset", "")) ?? GodClassPresets.Sets[0]).Describe()
        : level.Describe();

    /// <summary>The end of <c>skarp explain god-class</c> (its rule sets) and of the unused-code rules; false for other rules.</summary>
    public static bool ExplainExtra(TextWriter w, RuleInfo rule)
    {
        if (rule.Id is RuleIds.UnusedMember or RuleIds.UnusedType or RuleIds.UsedOnlyByTests or RuleIds.UnreadMember)
        {
            w.WriteLine();
            w.WriteLine($"Switch off: --rule {rule.Id}=off on skarp analyze or baseline, or \"rules\": {{ \"{rule.Id}\": false }} in .skarp.json. Allowlist: \"unusedCode\": {{ \"allow\": [\"M:Ns.Type.Member\", \"Ns.Type.*\"] }} in .skarp.json." +
                (rule.Id == RuleIds.UsedOnlyByTests ? " Never gated." : $" Gate: --gate-on {rule.Id}:notice on skarp analyze, or \"gate\": {{ \"on\": [\"{rule.Id}:notice\"] }} in .skarp.json."));
            w.WriteLine("Sources: Roslyn IDE0051 and IDE0052 (unused and unread private members), NDepend's \"code used only by tests\" rule.");
            return true;
        }
        if (rule.Id == RuleIds.NamespaceCycle)
        {
            w.WriteLine();
            w.WriteLine("Switch off: --rule namespace-cycle=off on skarp analyze or baseline, or \"rules\": { \"namespace-cycle\": false } in .skarp.json. " +
                "Keep references to types declared only in generated code: --include-generated-targets on skarp analyze or baseline, " +
                "or \"namespaceCycles\": { \"includeGeneratedTargets\": true } in .skarp.json (experimental). The finding id has no path: namespace-cycle||namespace <first member>.");
            w.WriteLine("Sources: Tarjan 1972 (strongly connected components); NDepend, Structure101, Lattix, ArchUnit and import-linter (namespace and package cycles).");
            return true;
        }
        if (rule.Id != RuleIds.GodClass) return false;
        w.WriteLine();
        w.WriteLine("Rule sets (--threshold god-class.preset=<id> on skarp analyze or baseline, or thresholds.god-class.preset in .skarp.json; all must hold; notice only):");
        foreach (var s in GodClassPresets.Sets)
            w.WriteLine($"  {s.Id,-21} {s.Describe()}{(s == GodClassPresets.Sets[0] ? " (default)" : "")}  ({s.Source})");
        w.WriteLine("Switch off: --rule god-class=off on skarp analyze or baseline, or \"rules\": { \"god-class\": false } in .skarp.json. " +
            "Sources: Lanza and Marinescu 2006 (Object-Oriented Metrics in Practice), Marinescu's detection strategies, PMD GodClassRule.");
        return true;
    }

    static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "~";

    static string Ratio(double? v) => v is double d ? d.ToString("0.00", CultureInfo.InvariantCulture) : "-";

    /// <summary>Markdown section (appended after the syntax summary).</summary>
    public static void WriteMarkdown(TextWriter w, AnalysisResult r)
    {
        if (r.Run.Semantic is not { } tier) return;
        w.WriteLine();
        w.WriteLine("### Semantic tier");
        w.WriteLine();
        w.WriteLine(HeaderLine(tier).Replace("|", "\\|", StringComparison.Ordinal));
        if (r.SemanticTier?.NotEvaluatedBaseline is { } nb)
            w.WriteLine($"\n{N(nb.Entries)} semantic baseline findings not evaluated (neither fixed nor new; not gated).");
        foreach (var s in r.SemanticTier?.Results ?? [])
        {
            if (s.Gate is { } g) w.WriteLine($"\n**Semantic gate {(g.Tripped ? "tripped" : "passed")}**: {g.New} new, {g.Worsened} worsened in the gate set.");
            var changed = s.Findings.Where(f => s.Baseline is null || f.BaselineState is "new" or "worsened").Take(100).ToList();
            if (changed.Count == 0) continue;
            w.WriteLine();
            w.WriteLine(s.Baseline is null ? "| Place | Severity | Rule | Measured |" : "| Place | Severity | Rule | Measured | Change |");
            w.WriteLine(s.Baseline is null ? "|---|---|---|---|" : "|---|---|---|---|---|");
            foreach (var f in changed)
                w.WriteLine($"| `{ReportLayout.PathLine(f.Path, f.Line)}` `{ReportLayout.ShortName(f)}` | {f.Severity} | {f.Rule} | {TextFormat.Measured(f, s.Baseline is not null)} |" +
                    (s.Baseline is null ? "" : $" {TextFormat.Lead(f, true)} |"));
        }
    }

    /// <summary>GitHub format: plain log lines, and an error annotation when the semantic gate tripped.</summary>
    public static void WriteGitHub(TextWriter w, AnalysisResult r)
    {
        if (r.Run.Semantic is not { } tier) return;
        w.WriteLine("skarp semantic: " + HeaderLine(tier));
        foreach (var s in r.SemanticTier?.Results ?? [])
            if (s.Gate is { Tripped: true } g)
                w.WriteLine($"::error title=skarp semantic gate::semantic gate tripped: {g.New} new and {g.Worsened} worsened findings in the gate set");
    }
}
