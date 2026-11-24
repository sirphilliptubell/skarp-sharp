using SkarpSharp.Discovery;
using SkarpSharp.Model;

namespace SkarpSharp.Coverage;

/// <summary>The <c>coverage</c> globs of <c>.skarp.json</c>, relative to the settings file's folder.</summary>
public static class CoverageGlobs
{
    /// <summary>Folders never searched for reports (version control and package caches).</summary>
    static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", ".vs", ".idea" };

    /// <summary>The report files the globs match (absolute, sorted, each once) and a warning per glob that matched none.</summary>
    public static (List<string> Files, List<CoverageWarning> Warnings) Expand(string folder, IReadOnlyList<string> globs)
    {
        folder = Path.GetFullPath(folder);
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var empty = new List<string>();
        List<string>? all = null;
        foreach (var g in globs)
        {
            var pattern = g.Replace('\\', '/').Trim();
            var before = found.Count;
            if (pattern.IndexOfAny(['*', '?']) < 0)
            {
                var file = Path.GetFullPath(Path.Combine(folder, pattern));
                if (File.Exists(file)) found.Add(file);
            }
            else
            {
                all ??= Files(folder);
                var glob = new Glob(pattern);
                foreach (var rel in all) if (glob.Matches(rel)) found.Add(Path.GetFullPath(Path.Combine(folder, rel)));
            }
            if (found.Count == before) empty.Add(g);
        }
        var warnings = empty.Count == 0 ? new List<CoverageWarning>()
            : [new CoverageWarning
            {
                Kind = "no-match", Files = empty, Count = empty.Count,
                Message = $"{(empty.Count == 1 ? "a coverage glob" : $"{empty.Count} coverage globs")} of the settings file matched no file: {string.Join(", ", empty)}",
            }];
        return ([.. found], warnings);
    }

    /// <summary>Every file under the folder, relative with '/' separators.</summary>
    static List<string> Files(string root) => FileDiscovery.Walk(root, "*", Skipped.Contains);
}
