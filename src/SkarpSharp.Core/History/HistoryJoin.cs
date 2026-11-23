using SkarpSharp.Model;

namespace SkarpSharp.History;

/// <summary>Both axes of one file for the hotspot table.</summary>
public readonly record struct HotspotCandidate(string Path, int Commits, long Added, long Deleted, long CognitiveSum, long CognitiveMax, int SourceLines);

/// <summary>
/// Activity hotspots: a two-axis table, never a combined score. Files are ordered by commits in the window
/// (descending), then by cognitive complexity sum (descending), then by path (ordinal) so the order is total.
/// </summary>
public static class Hotspots
{
    public const int DefaultTop = 20;

    public static List<HotspotEntry> Rank(IEnumerable<HotspotCandidate> candidates, int top)
    {
        var ordered = candidates
            .Where(c => c.Commits > 0)
            .OrderByDescending(c => c.Commits)
            .ThenByDescending(c => c.CognitiveSum)
            .ThenBy(c => c.Path, StringComparer.Ordinal)
            .Take(Math.Max(0, top));
        var list = new List<HotspotEntry>();
        foreach (var c in ordered)
            list.Add(new HotspotEntry
            {
                Path = c.Path, Rank = list.Count + 1, Commits = c.Commits, Added = c.Added, Deleted = c.Deleted,
                CognitiveSum = c.CognitiveSum, CognitiveMax = c.CognitiveMax, SourceLines = c.SourceLines,
            });
        return list;
    }
}

/// <summary>Joins a history run with file results (paths of the analyzed directory) and builds the hotspot list.</summary>
public static class HistoryJoin
{
    /// <summary>
    /// Adds <c>run.git</c>, <c>history</c>, <c>files[].history</c> and <c>hotspots</c> to an analysis result. Files without
    /// history in the window get zero counts. Generated files (generated-code rules, including the header rule) and unreadable
    /// files are not hotspot candidates unless <paramref name="includeGenerated"/> is set.
    /// </summary>
    public static void Apply(AnalysisResult result, HistoryRun run, int top, bool includeGenerated)
    {
        result.Run.Git = run.Git;
        if (run.Summary is null || run.RepositoryRoot is null) return;
        result.History = run.Summary;
        var prefix = RepositoryPrefix(run.RepositoryRoot, result.Run.Root);
        if (prefix is null)
        {
            run.Git.Notices.Add("analyzed folder is not inside the repository work tree as seen by git; files have no history");
            return;
        }
        var folded = CaseFoldedIndex(run.Files);
        if (result.Excluded is { Count: > 0 } excluded)
            SubtractExcluded(run.Summary, run.Files, folded, prefix, excluded.Select(e => e.Path));
        var candidates = new List<HotspotCandidate>();
        foreach (var f in result.Files)
        {
            f.History = Lookup(run.Files, folded, prefix + f.Path) ?? new FileHistory();
            if (f.Error is not null || (f.Generated && !includeGenerated) || f.History.Commits == 0) continue;
            long sum = 0, max = 0;
            foreach (var m in f.Members)
            {
                if (m.Parent is not null) continue; // nested bodies are folded into their parent
                sum += m.Cognitive;
                max = Math.Max(max, m.Cognitive);
            }
            candidates.Add(new HotspotCandidate(f.Path, f.History.Commits, f.History.Added, f.History.Deleted, sum, max, f.Lines.Source));
        }
        result.Hotspots = Hotspots.Rank(candidates, top);
    }

    /// <summary>
    /// Removes files left out of the analysis (tests, --exclude; paths relative to the analyzed directory) from the summary
    /// Totals, so history describes the same files as the metrics. Commit and people counts are not recomputed.
    /// </summary>
    static void SubtractExcluded(HistorySummary summary, IReadOnlyDictionary<string, FileHistory> files, Dictionary<string, FileHistory?>? folded,
        string prefix, IEnumerable<string> excludedPaths)
    {
        foreach (var path in excludedPaths)
        {
            if (Lookup(files, folded, prefix + path) is not { } h) continue;
            summary.Files--;
            summary.Rows -= h.Rows;
            summary.Added -= h.Added;
            summary.Deleted -= h.Deleted;
            summary.Dropped.ExcludedFiles++;
            summary.Dropped.ExcludedRows += h.Rows;
        }
    }

    /// <summary>
    /// Files of a history run under the analyzed directory, with paths relative to it, ordered by path. Files in
    /// <paramref name="excluded"/> (relative paths) are left out and subtracted from the run's summary.
    /// </summary>
    public static List<HistoryFileResult> FilesUnder(HistoryRun run, string analyzedRoot, IReadOnlyCollection<string>? excluded = null)
    {
        var list = new List<HistoryFileResult>();
        if (run.RepositoryRoot is null) return list;
        var prefix = RepositoryPrefix(run.RepositoryRoot, analyzedRoot);
        if (prefix is null) return list;
        var comparison = CaseInsensitiveFileSystem ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var skip = excluded is null ? null : new HashSet<string>(excluded, CaseInsensitiveFileSystem ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var (path, h) in run.Files)
        {
            if (!path.StartsWith(prefix, comparison)) continue;
            var rel = path[prefix.Length..];
            if (skip is not null && skip.Contains(rel)) continue;
            list.Add(new HistoryFileResult { Path = rel, History = h });
        }
        if (excluded is { Count: > 0 } && run.Summary is not null)
            SubtractExcluded(run.Summary, run.Files, CaseFoldedIndex(run.Files), prefix, excluded);
        list.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return list;
    }

    /// <summary>
    /// True on Windows and macOS, whose default file systems ignore case: there the path on disk (from the folder argument
    /// and the file walk) can differ in case from the path git records, and the join falls back to a case-insensitive match.
    /// </summary>
    internal static bool CaseInsensitiveFileSystem { get; set; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>Case-folded index of history paths, or null on case-sensitive file systems. Paths that collide when folded map to null (ambiguous).</summary>
    internal static Dictionary<string, FileHistory?>? CaseFoldedIndex(IReadOnlyDictionary<string, FileHistory> files)
    {
        if (!CaseInsensitiveFileSystem) return null;
        var index = new Dictionary<string, FileHistory?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, h) in files)
            index[path] = index.ContainsKey(path) ? null : h;
        return index;
    }

    /// <summary>Exact match first; then, on case-insensitive file systems, an unambiguous case-insensitive match.</summary>
    internal static FileHistory? Lookup(IReadOnlyDictionary<string, FileHistory> files, Dictionary<string, FileHistory?>? folded, string path)
        => files.TryGetValue(path, out var h) ? h : folded is not null && folded.TryGetValue(path, out var f) ? f : null;

    /// <summary>Path of the analyzed directory relative to the repository root, as a '/'-terminated prefix ("" at the root); null when outside.</summary>
    public static string? RepositoryPrefix(string repositoryRoot, string analyzedRoot)
    {
        var rel = Path.GetRelativePath(Path.GetFullPath(repositoryRoot), Path.GetFullPath(analyzedRoot)).Replace('\\', '/');
        if (rel == ".") return "";
        if (rel == ".." || rel.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return null;
        return rel.TrimEnd('/') + "/";
    }

    /// <summary>Output detail <c>files</c>: keep file-level facts, drop member and per-declaration type records.</summary>
    public static void ToFileDetail(AnalysisResult result)
    {
        foreach (var f in result.Files)
        {
            f.Members.Clear();
            f.Types.Clear();
        }
    }
}
