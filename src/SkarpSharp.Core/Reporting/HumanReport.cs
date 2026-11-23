using System.Globalization;
using SkarpSharp.Configuration;
using SkarpSharp.Findings;
using SkarpSharp.Model;

namespace SkarpSharp.Reporting;

public sealed record ReportOptions
{
    /// <summary>Places shown per file in the default view.</summary>
    public const int DefaultPerFile = SkarpOptions.DefaultPerFile;
    /// <summary>Places shown per file (0 = no cap); ignored with <see cref="All"/>.</summary>
    public int PerFile { get; init; } = DefaultPerFile;
    /// <summary>Findings shown (20 by default); ignored with <see cref="All"/>.</summary>
    public int Top { get; init; } = 20;
    public bool All { get; init; }
    public Severity MinSeverity { get; init; } = Severity.Notice;
    /// <summary><c>file</c> (default) or <c>rule</c>.</summary>
    public string Group { get; init; } = "file";
    public bool ExcludeTestsWhy { get; init; }
    /// <summary>Show only findings that are new or worse than in the baseline.</summary>
    public bool NewOnly { get; init; }
    /// <summary>List magic-number findings in the top list (counted only by default; <see cref="All"/> lists them too).</summary>
    public bool ListMagicNumbers { get; init; }
    /// <summary>List the findings an inline suppression left out.</summary>
    public bool ShowSuppressed { get; init; }
    /// <summary>The analyzed directory as given (the summary line names it).</summary>
    public string Directory { get; init; } = ".";
    /// <summary><c>--verbose</c>.</summary>
    public bool Verbose { get; init; }
    /// <summary>Finding lines under the files past a health limit (<c>output.gateMaxLines</c>).</summary>
    public int GateMaxLines { get; init; } = SkarpOptions.DefaultGateMaxLines;
    /// <summary>The run was saved, so <c>skarp show</c> resolves the ids (the About line names it).</summary>
    public bool ShowIds { get; init; }
}

/// <summary>The report options of a run's settings. Here rather than in the options class, so configuration does not
/// depend on reporting (that dependency would close a namespace cycle).</summary>
public static class ReportOptionsOf
{
    public static ReportOptions ReportOptions(this SkarpOptions o) => new()
    {
        Top = o.Top, All = o.All, MinSeverity = o.MinSeverity, Group = o.Group, ExcludeTestsWhy = o.ExcludeTestsWhy,
        NewOnly = o.NewOnly, ListMagicNumbers = o.ListMagicNumbers, PerFile = o.PerFile, ShowSuppressed = o.ShowSuppressed,
        Directory = o.Directory, Verbose = o.Verbose, GateMaxLines = o.GateMaxLines,
    };
}

/// <summary>
/// The run header, the tables and the other commands' reports; <c>skarp analyze</c>'s findings are in the
/// text format (<see cref="TextReport"/>), which uses the header and tables here with <c>--verbose</c>. Writes to any
/// TextWriter (the core assumes no console). Findings say "worth a look", never "violation".
/// </summary>
public static class HumanReport
{
    static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    public static void WriteHeader(TextWriter w, string command, AnalysisResult r)
    {
        var repo = r.Repository;
        w.WriteLine($"skarp {r.Run.ToolVersion} {command} {r.Run.Root}");
        var parts = new List<string> { $"{N(repo.Files)} files analyzed" };
        if (repo.GeneratedFiles > 0) parts.Add($"{N(repo.GeneratedFiles)} generated ({(r.Run.IncludeGenerated ? "included" : "excluded")})");
        parts.Add(r.Run.IncludeTests ? "test files included" : $"{N(repo.TestFiles)} test files excluded");
        if (repo.ExcludedFiles > 0) parts.Add($"{N(repo.ExcludedFiles)} excluded by exclude globs");
        if (repo.FilesWithErrors > 0) parts.Add($"{N(repo.FilesWithErrors)} unreadable");
        w.WriteLine($"files:      {string.Join(", ", parts)}; {N(repo.Members)} members, {N(repo.Lines.Source)} source lines");
        if (repo.Duplication is { } d) w.WriteLine("duplicates: " + DuplicationLine(d));
        if (r.Coverage is { } cov)
        {
            w.WriteLine("coverage:   " + CoverageLine(cov));
            foreach (var warning in cov.Warnings) w.WriteLine("warning:    " + Coverage.CoverageBaseline.Describe(warning));
        }
        if (r.Run.Variants is { } v) w.WriteLine("variants:   " + VariantLine(v));
        if (r.Suppressed is { Count: > 0 } s) w.WriteLine($"suppressed: {N(s.Count)} finding{(s.Count == 1 ? "" : "s")} left out by [SuppressMessage(\"skarp\", ...)] (--show-suppressed lists them; JSON: suppressed)");
        if (r.Run.Git is { } g)
        {
            if (r.History is { } h)
                w.WriteLine($"history:    git {g.Version}, HEAD {g.Head?[..Math.Min(12, g.Head.Length)]}, window {Day(g.WindowStart) ?? "all"} to {Day(g.WindowEnd)}; " +
                    $"{N(h.CommitsUsed)} commits used, {N(h.Files)} files changed{RepositoryCommits(h)}");
            foreach (var n in g.Notices) w.WriteLine($"note:       {n}");
        }
        else w.WriteLine("history:    off (--no-history or history.enabled false)");
        if (r.Run.Findings is { } fs)
        {
            w.WriteLine($"thresholds: preset {fs.PresetName}; complex-method reads {fs.ComplexMetric} complexity; long-method counts {fs.LongMethodLines} lines; " +
                $"record parameters {fs.RecordParameters}" + (fs.Overrides.Count > 0 ? $"; overrides: {string.Join(", ", fs.Overrides)}" : "") +
                (fs.Rules.Any(x => x.Off == true) ? $"; off: {string.Join(", ", fs.Rules.Where(x => x.Off == true).Select(x => x.Rule))}" : ""));
            // The repository file's per-path entries.
            if (fs.PathOverrides is { Count: > 0 } po)
            {
                int applying = po.Count(p => p.Matched > 0), none = po.Count - applying;
                w.WriteLine($"overrides:  {N(applying)} apply ({N(fs.PathOverrideFiles ?? 0)} file{(fs.PathOverrideFiles == 1 ? "" : "s")} matched)" +
                    (none > 0 ? $"; {N(none)} match{(none == 1 ? "es" : "")} nothing (skarp config lists them)" : ""));
            }
            foreach (var n in fs.Notes) w.WriteLine($"note:       {n}");
        }
        w.WriteLine($"settings:   {SettingsLine(r.Run.SettingsFiles)}");
        if (r.Baseline is { } b)
        {
            w.WriteLine($"baseline:   {b.Path}: {N(b.Entries)} recorded; {N(b.New)} new, {N(b.Worsened)} worsened, {N(b.Known)} known, {N(b.Fixed)} fixed");
            foreach (var n in b.Notes) w.WriteLine($"note:       {n}");
        }
        if (r.Run.Semantic is { } sem) w.WriteLine($"semantic:   {SemanticReport.HeaderLine(sem)}");
        w.WriteLine($"run:        syntax tier {r.Run.DurationSeconds:0.000} s" + (r.Run.Git is { } git ? $", history {git.DurationSeconds:0.000} s (concurrent)" : "") +
            $", peak working set {r.Run.PeakWorkingSetBytes / (1024 * 1024)} MB, {r.Run.Runtime}");
    }

    /// <summary><c>12 files with #if, 15 variants parsed (covering set, cap 32); 1 capped, ...</c>.</summary>
    public static string VariantLine(RunVariants v)
    {
        var parts = new List<string>();
        if (v.CappedFiles.Count > 0) parts.Add($"{N(v.CappedFiles.Count)} capped");
        if (v.DiscardedVariants > 0) parts.Add($"{N(v.DiscardedVariants)} discarded by the syntax-error guard");
        if (v.UnreachableBranches > 0) parts.Add($"{N(v.UnreachableBranches)} unreachable branch{(v.UnreachableBranches == 1 ? "" : "es")}");
        if (v.FilteredSymbols > 0) parts.Add($"{N(v.FilteredSymbols)} symbol{(v.FilteredSymbols == 1 ? "" : "s")} in no project file left off");
        if (v.UnplannedBranches > 0) parts.Add($"{N(v.UnplannedBranches)} branch{(v.UnplannedBranches == 1 ? "" : "es")} not planned");
        return $"{N(v.FilesWithConditionals)} file{(v.FilesWithConditionals == 1 ? "" : "s")} with #if, {N(v.Variants)} variant{(v.Variants == 1 ? "" : "s")} parsed " +
            $"({(v.Mode == "symbols" ? "symbols setting" : "covering set")}, cap {v.Cap})" + (parts.Count > 0 ? "; " + string.Join(", ", parts) : "") +
            (v.CappedFiles.Count + v.UnreachableBranches > 0 ? " (--all lists them)" : "");
    }

    /// <summary>With <c>--all</c>, the capped files and the unreachable branches.</summary>
    public static void WriteVariantDetails(TextWriter w, AnalysisResult r)
    {
        if (r.Run.Variants is not { } v || v.CappedFiles.Count + v.UnreachableBranches == 0) return;
        w.WriteLine();
        w.WriteLine("#if variants");
        foreach (var f in r.Files)
        {
            if (f.VariantsCapped == true) w.WriteLine($"  capped  {f.Path}: {f.Variants?.Count ?? 0} variants kept of more planned (variants.cap {v.Cap})");
            foreach (var u in f.UnreachableBranches ?? [])
                w.WriteLine($"  unreachable  {f.Path}:{u.Line} {u.Directive} ({u.Reason})");
        }
    }

    static string? Day(string? iso) => iso is { Length: >= 10 } ? iso[..10] : iso;

    /// <summary><c>2 clone groups (5 copies), 120 duplicated lines (1.4% of source lines)</c>.</summary>
    public static string DuplicationLine(DuplicationSummary d) =>
        $"{N(d.Groups)} clone group{(d.Groups == 1 ? "" : "s")} ({N(d.Instances)} copies), {N(d.DuplicatedLines)} duplicated lines " +
        $"({(d.Share * 100).ToString("0.0", CultureInfo.InvariantCulture)}% of source lines)" +
        (d.NearGroups is int n ? $"; {N(n)} near-duplicate group{(n == 1 ? "" : "s")}" : "");

    /// <summary><c>1,234 of 2,000 coverable lines covered in 40 files with data, 3 without (2 reports: microsoft)</c>.</summary>
    public static string CoverageLine(CoverageSummary c) =>
        $"{N(c.CoveredLines)} of {N(c.CoverableLines)} coverable lines covered in {N(c.FilesWithData)} file{(c.FilesWithData == 1 ? "" : "s")} with data, " +
        $"{N(c.FilesWithoutData)} without (no data, not 0%); {N(c.Reports.Count)} report{(c.Reports.Count == 1 ? "" : "s")} " +
        $"({string.Join(", ", c.Reports.Select(x => x.Producer).Distinct(StringComparer.Ordinal))}), {N(c.MatchedFiles)} report files matched" +
        (c.UnmatchedFiles + c.AmbiguousFiles > 0 ? $", {N(c.UnmatchedFiles)} unmatched, {N(c.AmbiguousFiles)} ambiguous" : "");

    /// <summary>The findings a view lists (severity filter, magic numbers, <c>--new-only</c>), in report order.</summary>
    public static List<Finding> Eligible(AnalysisResult r, ReportOptions o)
    {
        bool listMagic = o.All || o.ListMagicNumbers;
        bool newOnly = o.NewOnly && r.Baseline is not null;
        // Each composite (brain-method, brain-class) is one entry in place of its parts.
        return [.. BrainRules.Collapse(r.Findings ?? [], r.Composites).Where(f =>
            (o.MinSeverity == Severity.Notice || f.Severity == "warning")
            && (listMagic || f.Rule != RuleIds.MagicNumber)
            && (!newOnly || f.BaselineState is "new" or "worsened"))];
    }

    /// <summary>The text report of <c>skarp analyze</c> (see <see cref="TextReport"/>).</summary>
    public static void WriteAnalyze(TextWriter w, AnalysisResult r, ReportOptions o) => TextReport.WriteAnalyze(w, r, o);

    /// <summary>How many composites group the findings (null when there are none: brain-method is on
    /// by default, so the line appears only when it says something).</summary>
    public static string? CompositeLine(AnalysisResult r)
    {
        if (r.Composites is not { Count: > 0 } list) return null;
        int methods = list.Count(c => c.Rule == RuleIds.BrainMethod), classes = list.Count(c => c.Rule == RuleIds.BrainClass);
        var parts = new List<string>();
        if (methods > 0) parts.Add($"{N(methods)} brain method{(methods == 1 ? "" : "s")}");
        if (classes > 0) parts.Add($"{N(classes)} brain class{(classes == 1 ? "" : "es")} (experimental)");
        return $"Composites: {string.Join(", ", parts)}; each is one line in place of its parts (counted above; --all lists them).";
    }

    /// <summary>The thresholds used by the findings shown (the source once per rule); false when there are none.</summary>
    public static bool WriteThresholdsUsed(TextWriter w, IEnumerable<Finding> shown)
    {
        var rows = ReportLayout.ThresholdsUsed(shown);
        if (rows.Count == 0) return false;
        w.WriteLine("Thresholds used (`skarp explain <rule>` shows every preset and its source):");
        foreach (var (rule, sev, limit, source) in rows)
            w.WriteLine($"  {rule,-20} {sev,-7}  {limit,-28}  {source}");
        return true;
    }

    /// <summary>
    /// The run header's settings line: the repository-level file (repository or environment) alone; with a system or personal file, every level
    /// that applied, lowest first (<c>personal C:\Users\me\.skarp\settings.json; repository D:\repo\.skarp.json</c>).
    /// </summary>
    internal static string SettingsLine(List<SettingsFileInfo>? files)
    {
        if (files is null || !files.Any(f => f.Level is "system" or "personal"))
            return files?.FirstOrDefault(f => f.Level is "repository" or "environment")?.Path ?? "no settings file (defaults and flags)";
        return string.Join("; ", files.Select(f => $"{f.Level} {f.Path}"));
    }

    // ---- config -------------------------------------------------------------------------------------------------------

    /// <summary><c>skarp config</c>: the effective settings, where each came from, and the thresholds in effect.</summary>
    public static void WriteConfig(TextWriter w, SkarpOptions o, RuleSettings rules, string toolVersion) => WriteConfig(w, o, rules, toolVersion, null);

    /// <param name="overrides">The overrides section and the <c>--file</c> view, or null (no overrides, no flag).</param>
    public static void WriteConfig(TextWriter w, SkarpOptions o, RuleSettings rules, string toolVersion, ConfigOverrides? overrides)
    {
        w.WriteLine($"skarp {toolVersion} config {Path.GetFullPath(o.Directory)}");
        WriteLevels(w, o);
        var baseline = o.BaselineFile();
        w.WriteLine($"baseline file: {baseline}{(o.NoBaseline ? " (ignored: --no-baseline)" : File.Exists(baseline) ? "" : " (not found; skarp baseline writes it)")}");
        w.WriteLine();
        w.WriteLine($"  {"setting",-40} {"value",-24} origin");
        foreach (var s in o.Settings())
            w.WriteLine($"  {s.Key,-40} {s.Value,-24} {(o.Origins ? OriginDetail(o, s.Key) : s.Origin)}");
        w.WriteLine();
        w.WriteLine("Thresholds in effect (origin: the preset, or the level that changed the value):");
        w.WriteLine($"  {"rule",-20} {"level",-7} {"value",-52} origin");
        foreach (var info in RuleCatalog.Every)
        {
            var a = rules[info.Id];
            if (!a.Enabled) { w.WriteLine($"  {info.Id,-20} {"",-7} {"off",-52} {o.OriginText($"rules.{info.Id}")}"); continue; }
            foreach (var sev in (ReadOnlySpan<Severity>)[Severity.Notice, Severity.Warning])
            {
                var level = a.Level(sev);
                var name = sev == Severity.Warning ? "warning" : "notice";
                var origin = ThresholdOrigin(o, info.Id, name) ?? $"preset {a.LevelPreset(sev)}";
                w.WriteLine($"  {info.Id,-20} {name,-7} {SemanticReport.LevelText(info.Id, a, level),-52} {origin}");
            }
            foreach (var (k, v) in a.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
                w.WriteLine($"  {info.Id,-20} {k,-7} {v,-52} {ThresholdOrigin(o, info.Id, k) ?? $"preset {a.Preset}"}");
        }
        // The composite rules (no thresholds of their own), once switched on.
        foreach (var info in RuleCatalog.Composite.Where(c => rules[c.Id].Enabled))
        {
            w.WriteLine($"  {info.Id,-20} {"",-7} {"on (composite: groups its parts' findings)",-52} {o.OriginText($"rules.{info.Id}")}");
            foreach (var (k, v) in rules[info.Id].Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
                w.WriteLine($"  {info.Id,-20} {k,-7} {v,-52} {ThresholdOrigin(o, info.Id, k) ?? $"preset {rules[info.Id].Preset}"}");
        }
        foreach (var n in rules.Notes) w.WriteLine($"note: {n}");
        if (overrides is null) return;
        WriteOverrides(w, overrides);
        if (overrides.File is { } f) WriteFileView(w, o, f);
    }

    /// <summary>Each <c>overrides</c> entry with its selectors, what it changes and the files it matches.</summary>
    static void WriteOverrides(TextWriter w, ConfigOverrides v)
    {
        if (v.Entries.Count == 0) return;
        w.WriteLine();
        w.WriteLine($"Overrides (repository file; matching entries apply in file order after its own values, later wins key by key, the flags win over all; {N(v.MatchedFiles)} of {N(v.AnalyzedFiles)} analyzed files matched):");
        foreach (var r in v.Entries)
        {
            int matched = v.Matched?[r.Index] ?? 0;
            w.WriteLine($"  {r.Name}: {r.SelectorText()}");
            w.WriteLine($"    changes: {ConfigOverrideRows.Changes(r)}");
            w.WriteLine($"    matches: {N(matched)} file{(matched == 1 ? "" : "s")}" + (matched == 0 ? (r.MatchesNothing ? " (matches nothing: every glob lies outside the analyzed folder)" : " (matches nothing)") : ""));
        }
        foreach (var (entry, rule) in v.WalkRules)
            w.WriteLine($"note: {entry.Name} switches {rule} on: the unused-code walk then runs over every input (whether code is used is decided repository-wide)");
    }

    /// <summary><c>skarp config --file</c>: one file's rules and thresholds with their origins.</summary>
    static void WriteFileView(TextWriter w, SkarpOptions o, ConfigFileView f)
    {
        w.WriteLine();
        var matched = f.Matched.Count == 0 ? "no override matches" : string.Join(", ", f.Matched.Select(i => o.RuleOverrides[i].Name)) + (f.Matched.Count == 1 ? " matches" : " match");
        w.WriteLine($"File {f.Path}: {(f.TestCode ? "test code" : "production code")}; {matched}" + (f.Excluded is { } why ? $"; a full run leaves it out ({why})" : ""));
        w.WriteLine($"  {"rule",-20} {"level",-9} {"value",-52} origin");
        foreach (var row in ConfigOverrideRows.Rows(o, f))
            w.WriteLine($"  {row.Rule,-20} {row.Part,-9} {row.Value,-52} {row.Origin}");
    }

    /// <summary>
    /// The settings levels, lowest first, each with its file and whether it applied or why not (the
    /// gate, <c>useInRepositories</c>, <c>--no-user-config</c>, <c>--no-config</c>).
    /// </summary>
    static void WriteLevels(TextWriter w, SkarpOptions o)
    {
        w.WriteLine("settings levels (lowest first; each overrides the ones before):");
        foreach (var l in o.Levels)
        {
            var text = l.Level switch
            {
                SettingOrigin.Default => "skarp's built-in defaults",
                SettingOrigin.Plugin => "(reserved; empty)",
                SettingOrigin.CommandLine => "flags",
                SettingOrigin.Repository when l.Path is null => "none (.skarp.json in the directory or a parent up to the git root)",
                _ => $"{l.Path} ({(l.Applied ? "applied" : !l.Found ? "not found" : "skipped: " + l.Skipped)})",
            };
            w.WriteLine($"  {l.Level.Name(),-13} {text}");
        }
    }

    /// <summary>
    /// <c>--origins</c>: the origin level with its file and the values it overrode, the latest first
    /// (<c>personal C:\Users\me\.skarp\settings.json; overrides 10 from system</c>).
    /// </summary>
    internal static string OriginDetail(SkarpOptions o, string key)
    {
        var chain = o.OriginChain(key);
        if (chain.Count == 0) return SettingOrigin.Default.Name();
        var last = chain[^1];
        var text = last.Path is null ? last.Level.Name() : $"{last.Level.Name()} {last.Path}";
        if (last.Value is null) text += " (reset to the default)";
        var verb = last.Mode == "append" ? "appends to" : "overrides";
        for (int i = chain.Count - 2; i >= 0; i--)
        {
            var e = chain[i];
            var value = e.Value is null ? "a reset" : e.Value.Length == 0 ? (e.Level == SettingOrigin.CommandLine ? "a flag" : "(none)") : e.Value;
            text += $"; {verb} {value} from {e.Level.Name()}";
            verb = e.Mode == "append" ? "appends to" : "overrides";
        }
        return text;
    }

    static string? ThresholdOrigin(SkarpOptions o, string rule, string level)
    {
        var prefix = $"thresholds.{rule}.{level}";
        var keys = OriginKeys(o, prefix);
        return keys.Count == 0 ? null : string.Join(", ", keys.Distinct());
    }

    static List<string> OriginKeys(SkarpOptions o, string prefix)
    {
        var list = new List<string>();
        foreach (var t in o.Thresholds)
        {
            int eq = t.IndexOf('=');
            if (eq <= 0) continue;
            var key = "thresholds." + t[..eq].Trim();
            if (key == prefix || key.StartsWith(prefix + ".", StringComparison.Ordinal)) list.Add(o.OriginText(key));
        }
        return list;
    }

    public static void WriteCounts(TextWriter w, IReadOnlyList<FindingCount> counts)
    {
        w.WriteLine("Findings per rule:");
        int width = Math.Max(20, counts.Count == 0 ? 0 : counts.Max(c => c.Rule.Length));
        w.WriteLine($"  {"rule".PadRight(width)} {"warning",7} {"notice",7}");
        foreach (var c in counts)
            w.WriteLine($"  {c.Rule.PadRight(width)} {c.Warning,7} {c.Notice,7}");
    }

    public static void WriteExcluded(TextWriter w, AnalysisResult r) => WriteExcluded(w, r.Excluded ?? [], r.Run.IncludeTests);

    public static void WriteExcluded(TextWriter w, IReadOnlyList<ExcludedFile> excluded, bool includeTests)
    {
        w.WriteLine(includeTests ? "Files left out (test files are included: --include-tests):" : "Files left out as tests or by --exclude, and why:");
        if (excluded.Count == 0) { w.WriteLine("  none"); return; }
        foreach (var g in excluded.GroupBy(e => e.Reason, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var paths = g.Select(e => e.Path).ToList();
            var folder = CommonFolder(paths);
            if (g.Key.StartsWith("test project ", StringComparison.Ordinal) && folder.Length > 0)
                w.WriteLine($"  {g.Key}: {paths.Count} file{(paths.Count == 1 ? "" : "s")} under {folder}/");
            else
            {
                // Grouped by folder so large repositories stay readable; the JSON "excluded" list has every file.
                var folders = paths.GroupBy(p => p.Contains('/') ? p[..p.LastIndexOf('/')] : ".", StringComparer.Ordinal).ToList();
                w.WriteLine($"  {g.Key}: {paths.Count} file{(paths.Count == 1 ? "" : "s")}" + (folders.Count > 1 ? $" in {folders.Count} folders" : ""));
                foreach (var f in folders)
                    w.WriteLine(f.Count() == 1 ? $"    {f.First()}" : $"    {f.Key}/ ({f.Count()} files)");
            }
        }
        w.WriteLine("  Bring files back with --include <glob> or --include-tests; leave more out with --exclude <glob>.");
    }

    static string CommonFolder(List<string> paths)
    {
        if (paths.Count == 0) return "";
        var first = paths[0].Split('/');
        int n = first.Length - 1;
        foreach (var p in paths.Skip(1))
        {
            var s = p.Split('/');
            int i = 0;
            while (i < n && i < s.Length - 1 && s[i] == first[i]) i++;
            n = i;
        }
        return string.Join('/', first.Take(n));
    }

    public static void WriteHotspots(TextWriter w, AnalysisResult r, ReportOptions o)
    {
        WriteHeader(w, "hotspots", r);
        w.WriteLine();
        if (r.Hotspots is { Count: > 0 } hs)
        {
            w.WriteLine($"Activity hotspots (top {hs.Count}: commits in the window, then cognitive complexity sum):");
            var scores = r.Files.Where(f => f.Health is not null).ToDictionary(f => f.Path, f => Health.HealthText.Score(f.Health!.Score), StringComparer.Ordinal);
            w.WriteLine($"  {"rank",4} {"commits",7} {"added",8} {"deleted",8} {"cogSum",7} {"cogMax",6} {"srcLines",8} {"health",6}  path");
            foreach (var h in hs)
                w.WriteLine($"  {h.Rank,4} {h.Commits,7} {h.Added,8} {h.Deleted,8} {h.CognitiveSum,7} {h.CognitiveMax,6} {h.SourceLines,8} {scores.GetValueOrDefault(h.Path, "-"),6}  {h.Path}" +
                    (h.Untested == true ? "  (untested: a complex member no test runs)" : ""));
            if (r.Health is { } health) w.WriteLine($"  code health: {HealthReport.Averages(health)}");
        }
        else w.WriteLine(r.History is null ? "No activity hotspots: no git history." : "No activity hotspots: no analyzed file changed in the window.");
        if (r.Findings is { } f)
        {
            w.WriteLine();
            w.WriteLine($"{N(f.Count)} findings worth a look ({N(f.Count(x => x.Severity == "warning"))} warning); `skarp analyze` shows them.");
        }
        if (o.ExcludeTestsWhy) { w.WriteLine(); WriteExcluded(w, r); }
    }

    /// <summary>The text "; N commits in repository" (all commits, context only) and the C# total when the baseline gives it.</summary>
    static string RepositoryCommits(HistorySummary h) =>
        h.CommitsInRepository is not int all ? ""
        : $"; {N(all)} commits in repository" + (h.CSharpCommitsInRepository is int cs
            ? $" ({N(cs)} C#, via baseline {h.CSharpCommitsBaselineCommit?[..Math.Min(12, h.CSharpCommitsBaselineCommit.Length)]})" : "");

    public static void WriteHistory(TextWriter w, HistoryReport report, int top, IReadOnlyList<ExcludedFile>? excluded, bool includeTests, bool why)
    {
        var g = report.Run.Git;
        w.WriteLine($"skarp {report.Run.ToolVersion} history {report.Run.Root}");
        if (g is not null)
        {
            if (report.History is { } h)
            {
                var d = h.Dropped;
                w.WriteLine($"history:    git {g.Version}, HEAD {g.Head?[..Math.Min(12, g.Head.Length)]}, window {Day(g.WindowStart) ?? "all"} to {Day(g.WindowEnd)}; " +
                    $"{N(h.CommitsRead)} commits read, {N(h.CommitsUsed)} used, {N(h.Files)} files, +{N(h.Added)} -{N(h.Deleted)} lines{RepositoryCommits(h)}");
                w.WriteLine($"dropped:    {d.MergeCommits} merges, {d.OutsideWindowCommits} outside window, {d.BotCommits} bot, {d.BulkCommits} bulk, " +
                    $"{d.ShallowBoundaryCommits} shallow-boundary commits; {d.GeneratedRows} generated, {d.OtherFileRows} non-C#, {d.PureMoveRows} pure-move, " +
                    $"{d.NoChangeRows} no-change, {d.NotAtHeadRows} not-at-HEAD rows; {d.ExcludedFiles} files excluded ({(includeTests ? "tests included" : "tests and --exclude")})");
                if (h.FlaggedBulk.Count > 0)
                    w.WriteLine($"bulk:       {string.Join(", ", h.FlaggedBulk.Take(10).Select(b => $"{b.Hash[..Math.Min(12, b.Hash.Length)]} ({b.Files} files)"))}" +
                        (h.FlaggedBulk.Count > 10 ? $" and {h.FlaggedBulk.Count - 10} more" : ""));
            }
            foreach (var n in g.Notices) w.WriteLine($"note:       {n}");
        }
        w.WriteLine($"run:        {report.Run.DurationSeconds:0.000} s, peak working set {report.Run.PeakWorkingSetBytes / (1024 * 1024)} MB, {report.Run.Runtime}");
        if (report.Files.Count > 0)
        {
            w.WriteLine();
            w.WriteLine($"Most active files (top {Math.Min(top, report.Files.Count)} by commits):");
            w.WriteLine($"  {"commits",7} {"added",8} {"deleted",8} {"authors",7}  {"last change",-10}  path");
            foreach (var f in report.Files.OrderByDescending(f => f.History.Commits).ThenBy(f => f.Path, StringComparer.Ordinal).Take(top))
                w.WriteLine($"  {f.History.Commits,7} {f.History.Added,8} {f.History.Deleted,8} {f.History.Authors,7}  {Day(f.History.LastChange),-10}  {f.Path}");
        }
        if (why) { w.WriteLine(); WriteExcluded(w, excluded ?? [], includeTests); }
    }

    // ---- explain ------------------------------------------------------------------------------------------------------

    public static void WriteRuleList(TextWriter w)
    {
        w.WriteLine("Rules (skarp explain <rule> for the long explanation and the thresholds per preset):");
        foreach (var r in RuleCatalog.Every.Concat(RuleCatalog.Composite).Concat(RuleCatalog.Coverage)) w.WriteLine($"  {r.Id,-20} {r.Short}");
        w.WriteLine();
        w.WriteLine("On by default: every rule except brain-class (experimental). brain-method and duplicate-code-near (notice, score");
        w.WriteLine("weight 0) are on because another code health tool ships them on for C#; untested-complex-method is on whenever a");
        w.WriteLine("coverage report is given (--coverage on skarp analyze or baseline, or \"coverage\" in .skarp.json) and not evaluated");
        w.WriteLine("without one. --rule <id>=on|off on skarp analyze or baseline, or \"rules\": { \"<id>\": true|false } in .skarp.json,");
        w.WriteLine("switches a rule.");
        w.WriteLine();
        w.WriteLine("Presets (--preset <id> on skarp analyze or baseline, or \"preset\": \"<id>\" in .skarp.json; rules a preset leaves");
        w.WriteLine("blank use the default):");
        foreach (var p in Presets.All) w.WriteLine($"  {p.Id,-17} {p.Name}: {p.Description}");
    }

    /// <summary>The settings of a composite rule (it has no thresholds of its own; its parts keep theirs).</summary>
    static void WriteCompositeSettings(TextWriter w, RuleInfo rule)
    {
        var t = Presets.Default.Rules[rule.Id];
        w.WriteLine($"Definition: {t.Notice!.Source}.");
        w.WriteLine($"Parameters (default): {string.Join(", ", (t.Parameters ?? new Dictionary<string, string>()).Select(kv => $"{kv.Key}={kv.Value}"))}; " +
            $"change with --threshold {rule.Id}.<parameter>=<value> on skarp analyze or baseline, or thresholds.{rule.Id}.<parameter> in .skarp.json.");
        w.WriteLine(rule.DefaultOff
            ? $"Switch on: --rule {rule.Id}=on on skarp analyze or baseline, or \"rules\": {{ \"{rule.Id}\": true }} in .skarp.json."
            : $"On by default; switch off: --rule {rule.Id}=off on skarp analyze or baseline, or \"rules\": {{ \"{rule.Id}\": false }} in .skarp.json.");
        w.WriteLine("Sources: Lanza and Marinescu 2006 (Object-Oriented Metrics in Practice), CodeScene's Brain Method and Brain Class.");
    }

    /// <summary>The thresholds in force (one preset, after the settings files and overrides).</summary>
    static void WriteInForce(TextWriter w, RuleInfo rule, ActiveRule a)
    {
        bool blank = a.Notice is null && a.Warning is null;
        if (!a.Enabled || blank)
        {
            w.WriteLine($"In force here: off (\"rules\": {{ \"{rule.Id}\": true }} in .skarp.json switches it on).");
            return;
        }
        w.WriteLine($"Thresholds in force here (preset {a.Preset}; \"over\" = greater than, \"at least\" = from the value on):");
        if (a.Notice is { } n) w.WriteLine($"  notice:  {SemanticReport.LevelText(rule.Id, a, n)}  ({n.Source})");
        w.WriteLine(a.Warning is { } wl ? $"  warning: {SemanticReport.LevelText(rule.Id, a, wl)}  ({wl.Source})" : "  warning: blank (notice only)");
        if (a.Parameters.Count > 0) w.WriteLine($"  parameters: {string.Join(", ", a.Parameters.Select(kv => $"{kv.Key}={kv.Value}"))}");
        w.WriteLine($"Change them in .skarp.json: \"thresholds\": {{ \"{rule.Id}\": ... }}, or \"preset\".");
    }

    /// <summary>The coverage rule's threshold (the default's only; presets leave it blank) and sources.</summary>
    static void WriteCoverageSettings(TextWriter w, RuleInfo rule)
    {
        var n = Presets.Default.Rules[rule.Id].Notice!;
        w.WriteLine($"Threshold (every preset): notice at {n.Describe()} with no covered line ({n.Source}); no warning level.");
        w.WriteLine($"Change it: --threshold {rule.Id}.notice=<n> on skarp analyze or baseline, or thresholds.{rule.Id}.notice in .skarp.json; --rule {rule.Id}=off switches it off.");
        w.WriteLine("Sources: Savoia and Evans 2007 (CRAP), Inozemtseva and Holmes 2014 (coverage and test suite effectiveness).");
    }

    public static void WriteExplain(TextWriter w, RuleInfo rule) => WriteExplain(w, rule, null);

    /// <param name="inForce">For the MCP tool <c>explain</c>: the thresholds in force for a repository, printed in
    /// place of the table of every preset; null prints the table.</param>
    public static void WriteExplain(TextWriter w, RuleInfo rule, ActiveRule? inForce)
    {
        w.WriteLine($"{rule.Id}: {rule.Title} (judged per {rule.Unit})");
        w.WriteLine();
        w.WriteLine(rule.Short);
        w.WriteLine();
        foreach (var line in rule.Long.Split('\n')) w.WriteLine(line.TrimEnd('\r'));
        w.WriteLine();
        if (RuleCatalog.IsComposite(rule.Id)) { WriteCompositeSettings(w, rule); return; }
        if (RuleCatalog.IsCoverage(rule.Id)) { WriteCoverageSettings(w, rule); return; }
        if (inForce is not null) { WriteInForce(w, rule, inForce); return; }
        w.WriteLine("Thresholds per preset (\"over\" = greater than; \"at least\" = from the value on; a blank warning means notice only):");
        if (rule.Id == RuleIds.LongMethod)
        {
            w.WriteLine("  (\"lines\" = physical lines of the member: every line of the declaration; to count only lines with code use");
            w.WriteLine("   `--long-method-lines source` on skarp analyze or baseline, or \"longMethodLines\": \"source\" in .skarp.json)");
        }
        foreach (var p in Presets.All) WritePresetRow(w, rule, p);
        if (SemanticReport.ExplainExtra(w, rule)) return;
        w.WriteLine();
        w.WriteLine($"Change a value: --threshold {rule.Id}.notice=<n>, --threshold {rule.Id}.warning=<n> or =off" +
            (rule.ParameterNames.Count > 0 ? $", --threshold {rule.Id}.{rule.ParameterNames[0]}=<value>" : "") +
            $" on skarp analyze or baseline, or thresholds.{rule.Id} in .skarp.json.");
    }

    /// <summary>One preset's values of a rule in the table of <c>skarp explain</c> (complex-method: both metrics).</summary>
    static void WritePresetRow(TextWriter w, RuleInfo rule, Preset p)
    {
        var keys = rule.Id == RuleIds.ComplexMethod ? new[] { $"{rule.Id}/cognitive", $"{rule.Id}/cyclomatic" } : [rule.Id];
        bool any = false;
        foreach (var key in keys)
        {
            if (!p.Rules.TryGetValue(key, out var t)) continue;
            var variant = key.Contains('/') ? $" [{key[(key.IndexOf('/') + 1)..]}]" : "";
            w.WriteLine($"  {p.Id}{variant}");
            if (t.Notice is { } n) w.WriteLine($"    notice:  {n.Describe()}  ({n.Source})");
            w.WriteLine(t.Warning is { } wl ? $"    warning: {wl.Describe()}  ({wl.Source})" : "    warning: blank");
            if (t.Parameters is { Count: > 0 } ps) w.WriteLine($"    parameters: {string.Join(", ", ps.Select(kv => $"{kv.Key}={kv.Value}"))}");
            any = true;
        }
        if (!any && p.Id != Presets.Default.Id) w.WriteLine($"  {p.Id}: no value (the default is used)");
    }
}
