using SkarpSharp.Agent;
using SkarpSharp.Analysis;
using SkarpSharp.Configuration;
using SkarpSharp.Discovery;
using SkarpSharp.Findings;
using SkarpSharp.Health;
using SkarpSharp.Model;

namespace SkarpSharp.Tests.Agent;

/// <summary>
/// For every .cs file of a repository, the findings of
/// <c>skarp check &lt;file&gt;</c> (its per-file engine, the root and settings the command uses) equal the full analysis of
/// that root (syntax tier, no history) filtered to the file, ids included, minus the documented exceptions:
/// <c>active-complex-file</c> (history) and <c>large-type</c> of types declared partial or in several files (the full
/// run sums every declaration of a name).
/// The syntax score a check computes for each file equals the full run's
/// <c>syntaxScore</c> (else <c>score</c>) for every file without an excepted finding.
/// <c>duplicate-code</c> and <c>duplicate-code-near</c> are left out of check (they match across files), so
/// their groups are excepted, and so are the scores of files holding a copy (the full run's score counts the group).
/// Files whose full run parsed more <c>#if</c> variants than check's cap allows are counted, not compared.
/// </summary>
// XUnit ran the tests of a class one at a time; each of these analyzes a whole repository, so they stay sequential
// (memory). The summary line goes to the console, which TUnit captures per test (was ITestOutputHelper).
[NotInParallel(nameof(CheckEqualityTests))]
public class CheckEqualityTests
{
    static readonly string Repo = FindRepo();

    static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SkarpSharp.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root (SkarpSharp.slnx) not found");
    }

    [Test]
    public async Task Check_equals_the_full_run_on_every_file_of_this_repository() => await AssertEqual(Repo);

    /// <summary>With the composites on, a check's composites equal the full run's for the file (a brain class of
    /// a partial or repeated type excepted, as its large-type is); the parts are compared as above.</summary>
    [Test]
    public async Task Check_equals_the_full_run_with_the_composites_on() => await AssertEqual(Repo, Composites);

    static readonly string[] Composites = ["--rule", "brain-method=on", "--rule", "brain-class=on"];

    sealed record Full(AnalysisResult Result, HashSet<string> MultiOrPartial);

    static string Key(Finding f) => $"{f.Id} {f.Severity} {f.Value} {f.Threshold} {f.Line} {f.Message}";

    static string Key(CompositeFinding c) => $"{c.Id} {c.Severity} {c.Line} {c.Message} {string.Join(",", c.Parts)}";

    async Task AssertEqual(string repository, string[]? extra = null)
    {
        int composites = 0;
        var fulls = new Dictionary<string, Full>(StringComparer.OrdinalIgnoreCase);
        var mismatches = new List<string>();
        int files = 0, compared = 0, excepted = 0, scored = 0, capped = 0;
        // Every .cs file the directory walk of the repository root reaches (nested roots get their own full run).
        foreach (var rel in FileDiscovery.Find(repository))
        {
            var full = Path.GetFullPath(Path.Combine(repository, rel));
            var root = FileCheck.RootOf(full);
            var options = CommandLine.Parse(["analyze", root, "--no-history", "--no-semantic", .. extra ?? []]).Options!;
            var settings = options.RuleSettings();
            if (!fulls.TryGetValue(root, out var run))
            {
                var result = Analyzer.AnalyzeDirectory(root, options.AnalyzerOptions());
                FindingEngine.Apply(result, settings, options.IncludeGenerated);
                HealthEngine.Apply(result, new HealthInputs { Settings = options.Score, Rules = settings, IncludeGenerated = options.IncludeGenerated });
                var multi = new HashSet<string>(result.Types.Where(t => t.Declarations > 1).Select(t => t.Name), StringComparer.Ordinal);
                multi.UnionWith(result.Files.SelectMany(f => f.Types).Where(t => t.Partial).Select(t => t.Name));
                fulls[root] = run = new Full(result, multi);
            }
            bool Exception(Finding f) => FileCheck.LeftOut.Contains(f.Rule) || (f.Rule == RuleIds.LargeType && run.MultiOrPartial.Contains(f.Member ?? ""));
            var path = FileCheck.Relative(root, full);
            // A file whose full run parsed more #if variants than check's cap allows (the base
            // counts in) is analyzed in fewer variants by check, by design; it is counted, not compared.
            if (run.Result.Files.FirstOrDefault(f => f.Path == path)?.Variants is { } planned && planned.Count > options.Variants.CheckCap - 1) { capped++; continue; }
            var check = FileCheck.Analyze(options, settings, root, path, FileCheck.Decode(File.ReadAllBytes(full)));
            var expected = (run.Result.Findings ?? []).Where(f => f.Path == path && !Exception(f)).Select(Key).Order(StringComparer.Ordinal).ToList();
            var actual = check.Findings.Where(f => !Exception(f)).Select(Key).Order(StringComparer.Ordinal).ToList();
            files++;
            compared += expected.Count;
            excepted += (run.Result.Findings ?? []).Count(f => f.Path == path && Exception(f));
            if (ScoreMismatch(run.Result, check, options, path, Exception) is { } scoreError) mismatches.Add($"{root} {scoreError}");
            else if (check.ExcludedReason is null) scored++;
            if (!expected.SequenceEqual(actual))
                mismatches.Add($"{root} {path}:\n  only full run: {string.Join("\n    ", expected.Except(actual))}\n  only check: {string.Join("\n    ", actual.Except(expected))}");
            var expectedComposites = (run.Result.Composites ?? []).Where(c => c.Path == path && !(c.Rule == RuleIds.BrainClass && run.MultiOrPartial.Contains(c.Member ?? "")))
                .Select(Key).Order(StringComparer.Ordinal).ToList();
            var actualComposites = check.Composites.Select(Key).Order(StringComparer.Ordinal).ToList();
            composites += expectedComposites.Count;
            if (!expectedComposites.SequenceEqual(actualComposites))
                mismatches.Add($"{root} {path} composites:\n  only full run: {string.Join("\n    ", expectedComposites.Except(actualComposites))}\n  only check: {string.Join("\n    ", actualComposites.Except(expectedComposites))}");
        }
        await Assert.That(files > 0 && compared > 0).IsTrue().Because($"no .cs files or no findings under {repository}");
        Console.WriteLine($"{repository}: {files} files, {fulls.Count} roots, {compared} findings equal, {excepted} full-run findings excepted (large-type of partial or repeated types); {scored} syntax scores equal; {composites} composites equal; {capped} files over check's variant cap not compared");
        await Assert.That(mismatches.Count == 0).IsTrue().Because($"{mismatches.Count} of {files} files differ ({compared} findings compared):\n" + string.Join("\n", mismatches.Take(20)));
    }

    /// <summary>The check's syntax score against the full run's; null when equal, or when an excepted finding makes them
    /// differ by construction (the file is then not counted).</summary>
    static string? ScoreMismatch(AnalysisResult full, FileCheckResult check, SkarpOptions options, string path, Func<Finding, bool> exception)
    {
        if (check.ExcludedReason is not null) return null;
        if ((full.Findings ?? []).Any(f => f.Path == path && exception(f) && f.Rule == RuleIds.LargeType)) return null;
        if ((full.Findings ?? []).Any(f => f.Clone is { } c && c.Instances.Any(i => i.Path == path))) return null;
        var file = full.Files.SingleOrDefault(f => f.Path == path);
        if (file?.Health is not { } h) return file is null ? $"{path}: not in the full run" : null;   // generated: not scored
        var score = HealthText.Json(SkarpSharp.Agent.HealthLines.Of(ScoreModels.Create(options.Score), check.Findings, check.File?.Lines.Source ?? 0).Score);
        var expected = h.SyntaxScore ?? h.Score;
        return score == expected ? null : $"{path}: check syntax score {score}, full run {expected}";
    }
}

