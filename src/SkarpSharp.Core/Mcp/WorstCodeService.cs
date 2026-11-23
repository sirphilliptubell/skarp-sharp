using System.Diagnostics;
using System.Globalization;
using ModelContextProtocol;
using SkarpSharp.Agent;
using SkarpSharp.Configuration;
using SkarpSharp.Model;
using SkarpSharp.Ranking;

namespace SkarpSharp.Mcp;

/// <summary>What <c>find_worst_code</c> was asked for, after the arguments were read.</summary>
public sealed record WorstCodeRequest(string Path, int Count, bool Semantic, RankChoice Choice, int Offset);

/// <summary>
/// The analysis behind <c>find_worst_code</c>. The root is found as <c>skarp check</c>
/// finds it (nearest <c>.skarp.json</c>, else the git work tree root, else the folder); the whole root is analyzed as
/// <c>skarp analyze &lt;root&gt;</c> does and the ranking covers the path. The run is saved in <c>.skarp/last-run/</c> as
/// <c>analyze</c> saves it (<see cref="AnalysisRuns"/>, which also holds the reuse and the one-run lock at semantic depth).
/// </summary>
public sealed class WorstCodeService(McpHost host)
{
    readonly AnalysisRuns _runs = new(host);

    public async Task<string> RunAsync(WorstCodeRequest q, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(host.Cwd, q.Path));
        var (root, scope) = ScopeOf(q.Path, full);
        var ranking = await RankAsync(q, root, scope, ct).ConfigureAwait(false);
        var w = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        RankingText.Write(w, ranking, RankingFooter.Mcp);
        host.Write($"find_worst_code {q.Path} ({full}) depth {(q.Semantic ? "semantic" : "syntax")} sort {q.Choice.Sort} unit {q.Choice.Unit}: " +
            $"{ranking.Entries.Count} of {ranking.Total}{(ranking.Reused ? ", saved run reused" : "")} in {sw.ElapsedMilliseconds} ms");
        return w.ToString();
    }

    /// <summary>The root of <paramref name="full"/> and the scope under it (one file, or a folder prefix).</summary>
    /// <exception cref="McpException">The path does not exist.</exception>
    static (string Root, RankScope Scope) ScopeOf(string shown, string full)
    {
        bool isFile = File.Exists(full);
        if (!isFile && !Directory.Exists(full)) throw new McpException($"path not found: {full}");
        var root = FileCheck.RootOfFolder(isFile ? System.IO.Path.GetDirectoryName(full)! : full);
        var rel = FileCheck.Relative(root, full);
        var prefix = isFile ? rel : rel == "." ? "" : rel.TrimEnd('/') + "/";
        return (root, new RankScope { Shown = shown, SingleFile = isFile, Prefix = prefix });
    }

    /// <summary>The analysis (none for a file that is not C#) and its ranking, with the skipped input listed.</summary>
    async Task<Ranking.Ranking> RankAsync(WorstCodeRequest q, string root, RankScope scope, CancellationToken ct)
    {
        if (scope.SingleFile && !scope.Prefix.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            scope.Skipped.Add(new SkippedInput(scope.Prefix, "not C#"));
            return WorstCode.Rank(new AnalysisResult(), new RankQuery(q.Choice, scope, q.Count, q.Offset), _runs.Options(root, false).PathRules());
        }
        var (o, result, reused) = q.Semantic ? await _runs.SemanticAsync(root, null, ct).ConfigureAwait(false) : await _runs.SyntaxAsync(root, ct).ConfigureAwait(false);
        if (scope.SingleFile && !result.Files.Any(f => f.Path == scope.Prefix)) scope.Skipped.Add(new SkippedInput(scope.Prefix, WhySkipped(result, scope.Prefix)));
        var ranking = WorstCode.Rank(result, new RankQuery(q.Choice, scope, q.Count, q.Offset, o.IncludeGenerated), o.PathRules());
        ranking.Reused = reused;
        return ranking;
    }

    /// <summary>Why a C# file under the root is not in the analysis: test code, an exclude glob or a skipped folder,
    /// generated code, or missing.</summary>
    static string WhySkipped(AnalysisResult result, string rel)
    {
        if (result.Excluded?.FirstOrDefault(e => e.Path == rel) is { } e) return e.Kind == "test" ? "test code" : $"excluded: {e.Reason}";
        return "excluded: a folder Skarp skips (bin, obj and the like) or generated code";
    }
}
