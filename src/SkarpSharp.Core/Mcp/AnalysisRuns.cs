using System.Collections.Concurrent;
using System.Text.Json;
using ModelContextProtocol;
using SkarpSharp.Agent;
using SkarpSharp.Commands;
using SkarpSharp.Configuration;
using SkarpSharp.Model;

namespace SkarpSharp.Mcp;

/// <summary>An analysis of a root for a tool: its options, the result, and whether it came from the saved run.</summary>
public sealed record AnalysisRun(SkarpOptions Options, AnalysisResult Result, bool Reused);

/// <summary>
/// The analyses the MCP tools run on a root, as
/// <c>skarp analyze &lt;root&gt;</c> runs them, and the saved run in <c>.skarp/last-run/</c> (an atomic folder swap).
/// At semantic depth a saved run is reused when its tier ran, every file hash and the settings fingerprint match, it was
/// made with the same solution, and no baseline file is newer than it; one semantic run per root at a time in this
/// process; never restores. A syntax run does not replace a saved run that is fresh in that sense and has semantic
/// results (so the next semantic call stays fast and the saved findings keep their semantic part).
/// </summary>
public sealed class AnalysisRuns(McpHost host)
{
    static readonly ConcurrentDictionary<string, bool> Running = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>The options of <c>skarp analyze &lt;root&gt;</c> with the semantic tier on or off and the solution given
    /// (null: the settings or the tier choose); a settings error is the tool's error.</summary>
    public SkarpOptions Options(string root, bool semantic, string? solution = null)
    {
        List<string> args = ["analyze", root, semantic ? "--semantic" : "--no-semantic"];
        if (solution is not null) args.AddRange(["--solution", solution]);
        var parsed = CommandLine.Parse(args, host.SettingsLocations);
        return parsed.Options ?? throw new McpException(parsed.Error ?? "the settings could not be read");
    }

    /// <summary>The syntax tier over the root, saved unless the saved run is a fresh one with semantic results.</summary>
    public async Task<AnalysisRun> SyntaxAsync(string root, CancellationToken ct)
    {
        var o = Options(root, false);
        var gitTask = o.SaveLastRun ? GitState.ReadAsync(root, host.GitEnvironment) : null;
        var (result, _, fingerprint, _) = await SkarpCommand.AnalyzeResultAsync(o, ct: ct).ConfigureAwait(false);
        if (gitTask is null) return new AnalysisRun(o, result, false);
        if (Fresh(root, result, fingerprint, o, AnySolution) is null) await SaveAsync(root, result, fingerprint, gitTask, o.Solution).ConfigureAwait(false);
        else
        {
            await gitTask.ConfigureAwait(false);
            host.Write("the saved run has fresh semantic results: kept");
        }
        return new AnalysisRun(o, result, false);
    }

    /// <summary>The semantic tier, one run per root: the saved run when it is fresh, else a full run (saved).
    /// <paramref name="solution"/>: the solution to use (absolute), null to let the settings or the tier choose.</summary>
    /// <exception cref="McpException">A semantic run for the root is already going.</exception>
    public async Task<AnalysisRun> SemanticAsync(string root, string? solution, CancellationToken ct)
    {
        if (!Running.TryAdd(root, true)) throw new McpException($"a semantic run is already going for {root}; try again when it ends");
        try
        {
            var syntax = Options(root, false, solution);
            var (current, _, fingerprint, _) = await SkarpCommand.AnalyzeResultAsync(syntax, ct: ct).ConfigureAwait(false);
            if (Fresh(root, current, fingerprint, syntax, LastRun.SolutionKey(root, syntax.Solution)) is { } saved) return new AnalysisRun(syntax, saved, true);
            var o = Options(root, true, solution);
            var gitTask = o.SaveLastRun ? GitState.ReadAsync(root, host.GitEnvironment) : null;
            var (result, _, fp, _) = await SkarpCommand.AnalyzeResultAsync(o, ct: ct).ConfigureAwait(false);
            if (gitTask is not null) await SaveAsync(root, result, fp, gitTask, o.Solution).ConfigureAwait(false);
            return new AnalysisRun(o, result, false);
        }
        finally { Running.TryRemove(root, out _); }
    }

    /// <summary>Stands for "any solution" in <see cref="Fresh"/> (a syntax run does not depend on it).</summary>
    const string AnySolution = "*";

    /// <summary>
    /// The saved run of <paramref name="root"/> with its semantic results, when its semantic tier ran, it holds exactly
    /// the files of <paramref name="current"/> with the same hashes and the same settings fingerprint, it was asked for
    /// <paramref name="solution"/> (as <see cref="LastRun.SolutionKey"/> gives it; null: the tier chose), and no baseline
    /// file of <paramref name="o"/> is newer than it (its new and worse marks would be out of date).
    /// </summary>
    static AnalysisResult? Fresh(string root, AnalysisResult current, string fingerprint, SkarpOptions o, string? solution)
    {
        SavedRun? saved;
        try { saved = LastRun.Load(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException) { return null; }
        if (saved is not { Analysis: { Run.Semantic.Status: "ran" } analysis }) return null;
        if (saved.Header.SettingsFingerprint != fingerprint || !SameSolution(root, saved.Header.Solution, solution)) return null;
        if (BaselineNewer(root, o) || !SameFiles(root, current, saved.Header.Files)) return null;
        analysis.SemanticTier = new SemanticTierResults { Status = "ran", Results = saved.Semantic };
        return analysis;
    }

    /// <summary>The saved run's solution is the one asked for: the same one, both chosen by the tier, or one chosen by
    /// the tier and the other the only solution under the root (which the tier then chooses).</summary>
    static bool SameSolution(string root, string? saved, string? asked)
    {
        if (asked == AnySolution || Same(saved, asked)) return true;
        if ((saved is null) == (asked is null)) return false;
        var only = Semantic.SolutionChoice.Find(root) is [var one] ? LastRun.SolutionKey(root, one) : null;
        return only is not null && Same(only, saved ?? asked);
    }

    static bool Same(string? a, string? b) => string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>A baseline file (syntax, or semantic of any target framework) was written after the saved run.</summary>
    static bool BaselineNewer(string root, SkarpOptions o)
    {
        var header = new FileInfo(System.IO.Path.Combine(LastRun.Directory(root), LastRun.HeaderFile));
        if (!header.Exists) return true;
        var syntax = System.IO.Path.GetFullPath(o.BaselineFile());
        var plain = Baselines.SemanticBaseline.PathFor(syntax, null);
        var dir = System.IO.Path.GetDirectoryName(plain)!;
        var stem = System.IO.Path.GetFileName(plain)[..^".json".Length];
        IEnumerable<string> files = Directory.Exists(dir) ? [syntax, .. Directory.EnumerateFiles(dir, stem + "*.json")] : [syntax];
        return files.Any(f => File.Exists(f) && File.GetLastWriteTimeUtc(f) > header.LastWriteTimeUtc);
    }

    /// <summary>The analyzed files are exactly those of <paramref name="hashes"/>, each with the same SHA-256.</summary>
    static bool SameFiles(string root, AnalysisResult current, IReadOnlyDictionary<string, string> hashes)
    {
        var paths = current.Files.Select(f => f.Path).ToList();
        if (paths.Count != hashes.Count || !paths.All(hashes.ContainsKey)) return false;
        var same = new bool[paths.Count];
        Parallel.For(0, paths.Count, i => same[i] = Paths.FileSha256(System.IO.Path.Combine(root, paths[i])) == hashes[paths[i]]);
        return !same.Contains(false);
    }

    /// <summary>Saves the run as <c>analyze</c> does; a failure is logged, never the tool's error.</summary>
    async Task SaveAsync(string root, AnalysisResult result, string fingerprint, Task<GitState> gitTask, string? solution)
    {
        try { LastRun.Save(root, result, fingerprint, SkarpCommand.ToolVersion, await gitTask.ConfigureAwait(false), host.Now, solution); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { host.Write($"the run was not saved in .skarp/last-run ({ex.Message})"); }
    }
}
