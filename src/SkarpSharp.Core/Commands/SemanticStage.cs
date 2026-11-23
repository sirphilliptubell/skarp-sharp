using System.Diagnostics;
using System.Text.Json;
using SkarpSharp.Baselines;
using SkarpSharp.Configuration;
using SkarpSharp.Findings;
using SkarpSharp.Model;
using SkarpSharp.Semantic;

namespace SkarpSharp.Commands;

/// <summary>
/// The semantic tier inside <c>skarp analyze</c> and <c>skarp baseline</c>: the acquisition starts with the
/// syntax tier (it is mostly child processes), the compile and measure stage follows it; then the semantic output files,
/// the semantic baseline (written or compared) and the gate over semantic findings.
/// </summary>
public static class SemanticStage
{
    /// <param name="ct">The MCP call's token; it stops the tier's child processes (<c>dotnet msbuild</c>).</param>
    public static Task<SemanticAcquisition> Start(SkarpOptions o, CancellationToken ct = default) => SemanticRunner.AcquireAsync(o.Directory, o.SemanticOptions(), ct);

    /// <param name="rules">The run's per-path settings. The syntax tier has judged every kept file by now, so the
    /// combinations in use are known (the unused-code walk runs when any of them switches an unused-code rule on).</param>
    public static async Task<SemanticOutcome> FinishAsync(Task<SemanticAcquisition> acquisition, SkarpOptions o, AnalysisResult result, PathRules rules,
        string toolVersion, CancellationToken ct = default)
    {
        var settings = rules.Base;
        var acq = await acquisition.ConfigureAwait(false);
        var root = Path.GetFullPath(o.Directory);
        var kept = new HashSet<string>(result.Files.Where(f => f.Error is null && (o.IncludeGenerated || !f.Generated)).Select(f => f.Path), StringComparer.Ordinal);
        var commits = result.History is null ? null : result.Files.ToDictionary(f => f.Path, f => f.History?.Commits ?? 0, StringComparer.Ordinal);
        SemanticOutcome outcome;
        try
        {
            var context = new MeasureContext(root, o.SemanticOptions(), kept.Contains, settings, toolVersion, commits)
            {
                UnusedAllow = o.UnusedAllow, IncludeGeneratedTargets = o.IncludeGeneratedTargets,
                RulesOf = SkarpCommand.RulesOf(rules, result), SettingsInUse = rules.Count == 0 ? null : rules.InUse(),
            };
            outcome = await SemanticRunner.MeasureAsync(acq, context, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or ArgumentException)
        {
            // The rest of the run stays valid: the tier reports why it gave no results.
            outcome = new SemanticOutcome { Acquisition = acq.Fail($"compilation failed: {ex.Message}", "--no-semantic skips the semantic tier") };
        }
        Suppress(outcome, result, o.IncludeGenerated);
        // Wall time of the tier: from the start of the acquisition (concurrent with the syntax tier) to the end of measuring.
        acq.Timings.TotalSeconds = Math.Round(Stopwatch.GetElapsedTime(acq.StartedAt).TotalSeconds, 3);
        var peak = Process.GetCurrentProcess().PeakWorkingSet64;
        foreach (var r in outcome.Results) r.Run.PeakWorkingSetBytes = peak;
        var overrides = o.RuleOverrides.Count == 0 ? null : RuleOverrides.FingerprintLines(o.RuleOverrides, rules, semantic: true);
        await BaselineAsync(o, outcome, toolVersion, overrides).ConfigureAwait(false);
        if (outcome.Status != "ran") outcome.NotEvaluatedBaseline = MissingTierBaseline(o);
        var outputs = WriteFiles(o, outcome);
        result.Run.Semantic = TierInfo(outcome, root, outputs);
        result.SemanticTier = new SemanticTierResults
        {
            Status = outcome.Status, Reason = outcome.Acquisition.Reason, Results = outcome.Results, NotEvaluatedBaseline = outcome.NotEvaluatedBaseline,
        };
        return outcome;
    }

    /// <summary>Semantic findings in code suppressed for their rule (the syntax tier read the attributes)
    /// are left out, before the baseline and the gate.</summary>
    static void Suppress(SemanticOutcome outcome, AnalysisResult result, bool includeGenerated)
    {
        var index = SuppressionIndex.Build(result.Files.Where(f => f.Error is null && (includeGenerated || !f.Generated)));
        Suppress(outcome.Results, index);
    }

    /// <summary>The suppression split of each semantic result (findings, counts, the unused-code summary).</summary>
    internal static void Suppress(IEnumerable<SemanticResult> results, SuppressionIndex index)
    {
        if (index.IsEmpty) return;
        foreach (var r in results)
        {
            var (kept, suppressed) = index.Split(r.Findings);
            if (suppressed.Count == 0) continue;
            if (r.UnusedCode is { } unused) LeaveOutSuppressed(unused, r.Findings.Except(kept, ReferenceEqualityComparer.Instance).OfType<Model.Finding>());
            r.Findings = kept;
            r.FindingCounts = SemanticFindings.Counts(kept);
            r.Suppressed = suppressed;
        }
    }

    /// <summary>The unused-code summary counts leave out the findings a suppression hid, as the finding
    /// counts do (the per-project rows still count every candidate).</summary>
    static void LeaveOutSuppressed(Model.UnusedCodeSummary s, IEnumerable<Model.Finding> suppressed)
    {
        foreach (var f in suppressed)
        {
            switch (f.Rule)
            {
                case RuleIds.UnusedMember: s.UnusedMembers--; break;
                case RuleIds.UnusedType: s.UnusedTypes--; break;
                case RuleIds.UnreadMember: s.UnreadMembers--; break;
                case RuleIds.UsedOnlyByTests: s.UsedOnlyByTests--; break;
                default: continue;
            }
            if (f.Rule is RuleIds.UnusedMember or RuleIds.UnusedType && f.UsedOnlyBy is { Count: > 0 }) s.UsedOnlyByUnusedCode--;
        }
    }

    /// <summary>Semantic output files: <c>--semantic-output</c>, else next to <c>--output</c> (<c>x.semantic[.tfm].json</c>).</summary>
    static List<string> WriteFiles(SkarpOptions o, SemanticOutcome outcome)
    {
        var written = new List<string>();
        // With --format sarif, --output names the SARIF file: the semantic JSON is written only to --semantic-output.
        var basePath = o.SemanticOutput ?? (o.Output is { } output && o.Format != "sarif" ? SemanticBaseline.PathFor(output, null) : null);
        if (basePath is null || outcome.Results.Count == 0) return written;
        foreach (var r in outcome.Results)
        {
            var path = r.Run.TargetFramework is { } tfm ? InsertFramework(basePath, tfm) : basePath;
            using (var stream = File.Create(path)) JsonSerializer.Serialize(stream, r, SemanticJsonContext.Default.SemanticResult);
            written.Add(path);
        }
        return written;
    }

    static string InsertFramework(string path, string tfm) =>
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? path[..^5] + "." + tfm + ".json" : path + "." + tfm;

    /// <summary><c>skarp baseline</c> writes the semantic baseline(s); <c>analyze</c> compares and applies the gate.</summary>
    static async Task BaselineAsync(SkarpOptions o, SemanticOutcome outcome, string toolVersion, IReadOnlyList<string>? overrides)
    {
        var syntaxBaseline = Path.GetFullPath(o.BaselineFile());
        if (o.Command == "baseline")
        {
            if (outcome.Status != "ran") return;
            var commit = await SemanticBaseline.CommitAsync(o.Directory).ConfigureAwait(false);
            foreach (var r in outcome.Results)
            {
                var fp = SemanticBaseline.Fingerprint(r.Run.Findings!, r.Run.Generators, r.Run.Targets, r.Namespaces?.GeneratedTargetsKept == true, overrides);
                await File.WriteAllTextAsync(SemanticBaseline.PathFor(syntaxBaseline, r.Run.TargetFramework), SemanticBaseline.Write(r, toolVersion, fp, commit)).ConfigureAwait(false);
            }
            return;
        }
        if (o.NoBaseline) return;
        foreach (var r in outcome.Results)
        {
            var path = SemanticBaseline.PathFor(syntaxBaseline, r.Run.TargetFramework);
            if (!File.Exists(path))
            {
                if (o.Gate) r.Run.Notes.Add($"no semantic baseline file ({Path.GetFileName(path)}): semantic findings are not gated; `skarp baseline` records one");
                continue;
            }
            var file = SemanticBaseline.Read(path);
            var places = new HashSet<string>(r.NotEvaluated.Select(t => t.Path + "|" + t.Name), StringComparer.Ordinal);
            places.UnionWith(r.UnusedNotEvaluatedPlaces); // unused-code candidates not evaluated in this run
            var fp = SemanticBaseline.Fingerprint(r.Run.Findings!, r.Run.Generators, r.Run.Targets, r.Namespaces?.GeneratedTargetsKept == true, overrides);
            r.Baseline = SemanticBaseline.Compare(r.Findings, file, path, fp, places, FailedFolders(o, outcome));
            r.Baseline.Commit = await SemanticBaseline.RelationAsync(o.Directory, file.Commit).ConfigureAwait(false);
            if (o.Command == "analyze" && o.Gate) r.Gate = Gate.Evaluate(r.Findings, o.GateOn);
        }
    }

    /// <summary>Folders (relative to the root, with a trailing slash) of C# projects that gave no compiler arguments.</summary>
    static List<string> FailedFolders(SkarpOptions o, SemanticOutcome outcome)
    {
        var root = Path.GetFullPath(o.Directory);
        return [.. outcome.Acquisition.FailedProjects
            .Select(p => Path.GetRelativePath(root, Path.GetDirectoryName(p)!).Replace('\\', '/'))
            .Where(d => d != "." && !d.StartsWith("..", StringComparison.Ordinal))
            .Select(d => d + "/")];
    }

    /// <summary>The tier did not run: every semantic baseline file that exists is entirely not evaluated.</summary>
    static BaselineSummary? MissingTierBaseline(SkarpOptions o)
    {
        if (o.Command != "analyze" || o.NoBaseline) return null;
        var plain = SemanticBaseline.PathFor(Path.GetFullPath(o.BaselineFile()), null);
        var dir = Path.GetDirectoryName(plain)!;
        var stem = Path.GetFileName(plain)[..^".json".Length];
        if (!Directory.Exists(dir)) return null;
        BaselineSummary? total = null;
        foreach (var f in Directory.GetFiles(dir, stem + "*.json").Where(f => f == plain || Path.GetFileName(f).StartsWith(stem + ".", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            try
            {
                var s = SemanticBaseline.NotEvaluated(SemanticBaseline.Read(f), f);
                if (total is null) total = s;
                else { total.Entries += s.Entries; total.NotEvaluated += s.NotEvaluated; total.Path += ", " + s.Path; }
            }
            catch (InvalidDataException) { } // unreadable: reported when the tier runs and compares it
        }
        return total;
    }

    static SemanticTierInfo TierInfo(SemanticOutcome outcome, string root, List<string> outputs)
    {
        var acq = outcome.Acquisition;
        var info = new SemanticTierInfo
        {
            Status = acq.Status, Reason = acq.Reason, Hint = acq.Hint,
            Solution = acq.Solution is null ? null : Path.GetRelativePath(root, acq.Solution).Replace('\\', '/'),
        };
        if (outcome.Results.Count > 0)
        {
            info.TargetFrameworks = [.. outcome.Results.SelectMany(r => r.Projects).Select(p => p.TargetFramework).Distinct(StringComparer.Ordinal)
                .OrderByDescending(TargetFrameworks.Key)];
            info.Types = outcome.Results.Sum(r => r.Types.Count);
            info.TypesNotEvaluated = outcome.Results.Sum(r => r.NotEvaluated.Count);
            info.Findings = outcome.Results.Sum(r => r.Findings.Count);
        }
        if (outputs.Count > 0) info.Outputs = outputs;
        return info;
    }

    /// <summary>The line <c>skarp baseline</c> prints about the semantic baseline.</summary>
    public static string BaselineLine(SkarpOptions o, AnalysisResult result)
    {
        if (result.SemanticTier is not { } outcome) return "skarp baseline: semantic tier not run";
        if (outcome.Status != "ran")
            return $"skarp baseline: semantic baseline not written (semantic {outcome.Status}: {outcome.Reason}); an existing one is kept";
        var syntaxBaseline = Path.GetFullPath(o.BaselineFile());
        return "skarp baseline: semantic baseline " + string.Join(", ", outcome.Results.Select(r =>
            $"{SemanticBaseline.PathFor(syntaxBaseline, r.Run.TargetFramework)} ({r.Findings.Count} findings)"));
    }

    /// <summary>True when the gate over semantic findings tripped in any result.</summary>
    public static bool Tripped(SemanticTierResults? outcome) => outcome?.Results.Any(r => r.Gate is { Tripped: true }) == true;
}
