using System.Globalization;
using SkarpSharp.Discovery;
using SkarpSharp.Model;

namespace SkarpSharp.Coverage;

/// <summary>What the coverage stage is given: the reports, the analyzed directory and warnings from resolving the inputs.</summary>
public sealed class CoverageInputs
{
    /// <summary>Report files (absolute), in the order given.</summary>
    public required IReadOnlyList<string> Reports { get; init; }
    /// <summary>Warnings found while resolving the inputs (a settings glob that matched nothing).</summary>
    public IReadOnlyList<CoverageWarning> Warnings { get; init; } = [];
    /// <summary>Compare paths without case (default: Windows and macOS).</summary>
    public bool? IgnoreCase { get; init; }
}

/// <summary>
/// The coverage stage: reads the Cobertura reports, merges them per line (covered when any report
/// covers the line), resolves their file names to analyzed files, and attaches covered and coverable line counts to
/// members, files and projects, with the staleness and inactive-code warnings. Runs after the syntax tier, before the
/// findings (the rule untested-complex-method reads the member facts).
/// </summary>
public static class CoverageEngine
{
    /// <summary>At most this many files are named in one warning.</summary>
    const int FilesNamed = 20;

    public static void Apply(AnalysisResult result, CoverageInputs inputs)
    {
        var reports = new CoberturaReport[inputs.Reports.Count];
        var errors = new CoverageFormatException?[reports.Length];
        Parallel.For(0, reports.Length, i =>
        {
            try { reports[i] = CoberturaReader.Read(inputs.Reports[i]); }
            catch (CoverageFormatException ex) { errors[i] = ex; }
        });
        // The first refused report in the order given (not the first to fail on another thread).
        if (errors.FirstOrDefault(e => e is not null) is { } error) throw error;
        var summary = new CoverageSummary();
        summary.Warnings.AddRange(inputs.Warnings);
        var root = result.Run.Root;
        var analyzed = result.Files.Where(f => f.Error is null).ToDictionary(f => f.Path, StringComparer.Ordinal);
        var notAnalyzed = new HashSet<string>((result.Excluded ?? []).Select(e => e.Path), StringComparer.Ordinal);
        var paths = new CoveragePaths(root, analyzed.Keys.Concat(notAnalyzed), inputs.IgnoreCase);
        var merge = new Merge(root, paths, analyzed, notAnalyzed);
        foreach (var r in reports) summary.Reports.Add(merge.Add(r));
        merge.Count(summary);

        var stale = new List<string>();
        var beyond = new List<string>();
        var inactive = new List<string>();
        foreach (var f in result.Files)
        {
            if (f.Error is not null) continue;
            var lines = merge.Lines.GetValueOrDefault(f.Path);
            var fileFacts = MapFile(f, lines);
            if (fileFacts.Beyond > 0) beyond.Add(f.Path);
            if (fileFacts.StaleLines > 0) stale.Add(f.Path);
            if (fileFacts.InactiveCovered > 0) { inactive.Add(f.Path); summary.InactiveCoveredLines += fileFacts.InactiveCovered; }
            if (f.Generated || f.TestCode) continue;
            if (f.Coverage!.Status == CoverageFacts.Data)
            {
                summary.FilesWithData++;
                summary.CoverableLines += f.Coverage.CoverableLines!.Value;
                summary.CoveredLines += f.Coverage.CoveredLines!.Value;
            }
            else summary.FilesWithoutData++;
        }
        foreach (var f in result.Files) f.CoverageLines = null;
        summary.Projects = Projects(root, result.Files);
        Warn(summary, stale, beyond, inactive, merge, NewerFiles(root, result.Files, reports));
        result.Coverage = summary;
    }

    /// <summary>The facts of one file and its members, and its counts for the warnings.</summary>
    static (int Beyond, int StaleLines, int InactiveCovered) MapFile(FileResult f, Dictionary<int, bool>? lines)
    {
        var info = f.CoverageLines;
        // Generated code and test code (kept with --include-tests) are never measured.
        if (f.Generated || f.TestCode)
        {
            f.Coverage = new CoverageFacts { Status = CoverageFacts.Excluded };
            return (0, 0, 0);
        }
        var members = f.Members.Where(m => m.Kind is not ("Lambda" or "LocalFunction")).OrderBy(m => m.EndLine - m.StartLine).ToList();
        var counts = new Dictionary<MemberResult, (int Coverable, int Covered)>(ReferenceEqualityComparer.Instance);
        int coverable = 0, covered = 0, beyond = 0, stale = 0, inactive = 0;
        foreach (var (line, hit) in lines ?? [])
        {
            if (info is null || line > info.Count) { beyond++; continue; }
            if (info.IsExcluded(line)) continue;
            coverable++;
            if (hit) covered++;
            if (hit && info[line] == CoverageLineKind.NonCode) stale++;
            if (hit && info[line] == CoverageLineKind.Inactive) inactive++;
            if (members.FirstOrDefault(m => m.StartLine <= line && line <= m.EndLine) is { } owner)
            {
                var (a, b) = counts.GetValueOrDefault(owner);
                counts[owner] = (a + 1, b + (hit ? 1 : 0));
            }
        }
        f.Coverage = coverable == 0 ? new CoverageFacts { Status = CoverageFacts.NoData }
            : new CoverageFacts { Status = CoverageFacts.Data, CoverableLines = coverable, CoveredLines = covered };
        foreach (var m in members)
        {
            var (a, b) = counts.GetValueOrDefault(m);
            bool excluded = info is not null && info.IsExcluded(m.StartLine) && info.IsExcluded(m.EndLine);
            m.Coverage = excluded ? new CoverageFacts { Status = CoverageFacts.Excluded }
                : a == 0 ? new CoverageFacts { Status = CoverageFacts.NoData }
                : new CoverageFacts { Status = CoverageFacts.Data, CoverableLines = a, CoveredLines = b };
            if (info is not null && info.HasInactive(m.StartLine, m.EndLine)) m.Coverage.InactiveRegions = true;
        }
        return (beyond, stale, inactive);
    }

    /// <summary>The reports merged per analyzed file and line, with the match counts per report and over all reports.</summary>
    sealed class Merge(string root, CoveragePaths paths, Dictionary<string, FileResult> analyzed, HashSet<string> notAnalyzed)
    {
        public readonly Dictionary<string, Dictionary<int, bool>> Lines = new(StringComparer.Ordinal);
        readonly HashSet<string> matched = new(StringComparer.Ordinal), unmatched = new(StringComparer.Ordinal), ambiguous = new(StringComparer.Ordinal),
            skipped = new(StringComparer.Ordinal), outside = new(StringComparer.Ordinal);
        public readonly SortedSet<string> Missing = new(StringComparer.Ordinal);
        public readonly SortedSet<string> Unmatched = new(StringComparer.Ordinal);
        public readonly SortedSet<string> Ambiguous = new(StringComparer.Ordinal);
        public readonly List<string> Empty = [];

        public CoverageReportInfo Add(CoberturaReport r)
        {
            var info = new CoverageReportInfo
            {
                Path = r.Path, Producer = r.Producer, Files = r.Files.Count,
                Timestamp = r.Timestamp > 0 ? DateTimeOffset.FromUnixTimeSeconds(r.Timestamp).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : null,
            };
            if (r.Files.Count == 0) Empty.Add(r.Path);
            foreach (var (name, lines) in r.Files)
            {
                var (file, outcome) = paths.Resolve(name, r.Sources);
                if (file is not null && analyzed.ContainsKey(file))
                {
                    info.Matched++;
                    if (analyzed[file] is { Generated: true } or { TestCode: true }) { skipped.Add(file); continue; }
                    matched.Add(file);
                    if (!Lines.TryGetValue(file, out var into)) Lines[file] = into = [];
                    foreach (var (line, hit) in lines) into[line] = hit || into.GetValueOrDefault(line);
                }
                else if (file is not null) { info.Matched++; skipped.Add(file); }
                else if (outcome == PathOutcome.Ambiguous) { info.Ambiguous++; ambiguous.Add(name); Ambiguous.Add(CoveragePaths.Norm(name)); }
                else if (outcome == PathOutcome.Outside) outside.Add(name);
                else Unresolved(name, info);
            }
            return info;
        }

        /// <summary>A name inside the root that matches no known file: generator output under obj/ or bin/ (not analyzed),
        /// a file on disk skarp does not analyze, or a file that is gone (a staleness sign).</summary>
        void Unresolved(string name, CoverageReportInfo info)
        {
            var rel = paths.Relative(name);
            if (rel is not null && (rel.Split('/').Any(FileDiscovery.IsPrunedDirectoryName) || notAnalyzed.Contains(rel) || File.Exists(Path.Combine(root, rel))))
            {
                skipped.Add(rel);
                return;
            }
            info.Unmatched++;
            unmatched.Add(name);
            if (rel is not null) Missing.Add(rel);
            else Unmatched.Add(CoveragePaths.Norm(name));
        }

        public void Count(CoverageSummary s)
        {
            s.MatchedFiles = matched.Count;
            s.UnmatchedFiles = unmatched.Count;
            s.AmbiguousFiles = ambiguous.Count;
            s.NotAnalyzedFiles = skipped.Count;
            s.OutsideFiles = outside.Count;
        }
    }

    /// <summary>Per project (the nearest *.csproj), the sums of its analyzed, non-generated files.</summary>
    static List<ProjectCoverage> Projects(string root, IEnumerable<FileResult> files)
    {
        var locator = new TestDetection.ProjectLocator();
        var byProject = new SortedDictionary<string, ProjectCoverage>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            if (f.Error is not null || f.Generated || f.TestCode || f.Coverage is null) continue;
            var dir = Path.GetDirectoryName(Path.Combine(root, f.Path)) ?? root;
            var project = locator.Find(dir)?.FullPath;
            var key = project is null ? "(none)" : Path.GetRelativePath(root, project).Replace('\\', '/');
            if (!byProject.TryGetValue(key, out var p)) byProject[key] = p = new ProjectCoverage { Path = key, Status = CoverageFacts.NoData };
            p.Files++;
            if (f.Coverage.Status != CoverageFacts.Data) continue;
            p.Status = CoverageFacts.Data;
            p.FilesWithData++;
            p.CoverableLines = (p.CoverableLines ?? 0) + f.Coverage.CoverableLines;
            p.CoveredLines = (p.CoveredLines ?? 0) + f.Coverage.CoveredLines;
        }
        return [.. byProject.Values];
    }

    /// <summary>Analyzed files with coverage data modified after the newest report (the weaker staleness sign: a
    /// checkout also sets the modification time).</summary>
    static List<string> NewerFiles(string root, IEnumerable<FileResult> files, CoberturaReport[] reports)
    {
        if (reports.Length == 0) return [];
        var newest = reports.Max(r => r.Timestamp > 0 ? DateTimeOffset.FromUnixTimeSeconds(r.Timestamp).UtcDateTime : File.GetLastWriteTimeUtc(r.Path));
        var list = new List<string>();
        foreach (var f in files)
        {
            if (f.Coverage?.Status != CoverageFacts.Data) continue;
            try
            {
                // The report's timestamp has whole seconds; a file written in the same second is not newer.
                if (File.GetLastWriteTimeUtc(Path.Combine(root, f.Path)) > newest.AddSeconds(1)) list.Add(f.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that cannot be read now has no time to compare; the other signals still apply.
            }
        }
        return list;
    }

    static void Warn(CoverageSummary s, List<string> stale, List<string> beyond, List<string> inactive, Merge m, List<string> newer)
    {
        void Add(string kind, ICollection<string> files, string message)
        {
            if (files.Count == 0) return;
            s.Warnings.Add(new CoverageWarning { Kind = kind, Message = message, Files = [.. files.Take(FilesNamed)], Count = files.Count });
        }
        string N(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";
        Add("stale", stale, $"the reports look older than the code: {N(stale.Count, "file")} {(stale.Count == 1 ? "has" : "have")} covered lines on blank, comment or declaration lines (the lines moved since the report was made); produce the report again");
        Add("missing-files", m.Missing, $"{N(m.Missing.Count, "file")} named in the reports {(m.Missing.Count == 1 ? "is" : "are")} no longer in the analyzed directory; produce the report again");
        Add("beyond-end", beyond, $"{N(beyond.Count, "file")} {(beyond.Count == 1 ? "has" : "have")} report lines beyond the end of the file (dropped)");
        Add("newer-files", newer, $"{N(newer.Count, "file")} changed after the newest report (a weaker sign: checking out files also changes their time)");
        if (inactive.Count > 0)
            Add("inactive-code", inactive, $"{N(s.InactiveCoveredLines, "covered line")} in code inactive in every analyzed variant (#if branches no planned symbol set turns on); members with such regions are never reported as untested");
        Add("unmatched", m.Unmatched, $"{N(m.Unmatched.Count, "report file")} match no file of the analyzed directory (lines dropped)");
        Add("ambiguous", m.Ambiguous, $"{N(m.Ambiguous.Count, "report file")} fit several files equally well and were not used (never guessed)");
        Add("no-data", m.Empty, $"{N(m.Empty.Count, "report")} {(m.Empty.Count == 1 ? "has" : "have")} no files (an empty report: tests did not run, or the code under test was not found)");
        s.Stale = stale.Count > 0 || m.Missing.Count > 0;
    }
}
