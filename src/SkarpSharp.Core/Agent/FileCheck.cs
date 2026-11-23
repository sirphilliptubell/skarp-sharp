using System.Text;
using SkarpSharp.Analysis;
using SkarpSharp.Configuration;
using SkarpSharp.Discovery;
using SkarpSharp.Findings;
using SkarpSharp.Model;
using Microsoft.CodeAnalysis.Text;

namespace SkarpSharp.Agent;

/// <summary>The syntax findings of one file as a full run of its root would give them.</summary>
public sealed class FileCheckResult
{
    public required string Root { get; init; }
    /// <summary>The file relative to <see cref="Root"/>, '/' separators (the path in finding ids).</summary>
    public required string Path { get; init; }
    /// <summary>Why a full run leaves the file out (test code, <c>--exclude</c>, a pruned folder); null when analyzed.</summary>
    public string? ExcludedReason { get; init; }
    /// <summary>What kind of exclusion: <c>test</c> (test code), <c>exclude</c> (a glob), <c>walk</c> (a folder a
    /// full run skips), or another kind the test detection gives; null when analyzed.</summary>
    public string? ExcludedKind { get; init; }
    public FileResult? File { get; init; }
    public List<Finding> Findings { get; init; } = [];
    /// <summary>Types declared <c>partial</c> in the file: their <c>large-type</c> needs every file of the type, so it is left out.</summary>
    public List<string> PartialTypes { get; init; } = [];
    /// <summary>The composites (brain-method, brain-class) over <see cref="Findings"/> when switched on; a brain
    /// class of a partial type is left out with its large-type finding.</summary>
    public List<CompositeFinding> Composites { get; init; } = [];
}

/// <summary>
/// The per-file engine of <c>skarp check</c>: the syntax tier on one file's current content, with the
/// root, settings, test detection and exclusion a full <c>analyze</c> of that root uses, so the ids are the same. Left
/// out: <c>large-type</c> of partial types (the type spans files), <c>active-complex-file</c> (needs history) and the
/// semantic tier. Known difference: a global using of a test framework in another file of the same project makes a full
/// run leave the whole project out; a check sees only the file itself. With an override selecting
/// <c>"tests": true</c>, the same difference can make a check apply other per-path settings than the full run.
/// </summary>
public static class FileCheck
{
    /// <summary>Rules a check never reports (contracts.md section 10, <c>skarp check --help</c>).</summary>
    public static readonly IReadOnlyList<string> LeftOut = [RuleIds.ActiveComplexFile, RuleIds.DuplicateCode, RuleIds.DuplicateCodeNear, RuleIds.UntestedComplexMethod];

    /// <summary>
    /// The root a check uses for a file: the folder of the nearest <c>.skarp.json</c> (up to the git work tree root), else
    /// the git work tree root, else the file's folder.
    /// </summary>
    public static string RootOf(string fullPath) => RootOfFolder(System.IO.Path.GetDirectoryName(fullPath) ?? fullPath);

    /// <summary>The root of a folder (the project of the working folder or of a hook's <c>cwd</c>), found as
    /// for a file in it.</summary>
    public static string RootOfFolder(string dir)
    {
        if (SettingsFileReader.Find(dir) is { } settings) return System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(settings))!;
        return Paths.WorkTreeRoot(dir) ?? dir;
    }

    /// <summary>Decodes file bytes as <c>File.ReadAllText</c> does (UTF-8 unless a byte order mark says otherwise).</summary>
    public static string Decode(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Whether a full run's directory walk reaches the file (it prunes bin, obj, .git and similar folders).</summary>
    static bool Pruned(string relativePath)
    {
        var parts = relativePath.Split('/');
        for (int i = 0; i < parts.Length - 1; i++)
            if (FileDiscovery.IsPrunedDirectoryName(parts[i])) return true;
        return false;
    }

    /// <summary>The findings of one file's text (current content or another version of it) under <paramref name="o"/>.</summary>
    public static FileCheckResult Analyze(SkarpOptions o, RuleSettings settings, string root, string relativePath, string text) =>
        Analyze(o, PathRules.Single(settings), root, relativePath, text);

    /// <summary>The same with the run's per-path settings (the file is judged by the overrides that match it).</summary>
    public static FileCheckResult Analyze(SkarpOptions o, PathRules settings, string root, string relativePath, string text)
    {
        if (Pruned(relativePath) || relativePath.StartsWith("../", StringComparison.Ordinal))
            return new FileCheckResult { Root = root, Path = relativePath, ExcludedReason = "outside the folders a full run walks", ExcludedKind = "walk" };
        var classifier = new TestDetection.Classifier(root, o.IncludeTests,
            [.. o.Include.Select(g => new Glob(g))], [.. o.Exclude.Select(g => new Glob(g))]);
        if (classifier.ExcludeBeforeParse(relativePath))
            return new FileCheckResult { Root = root, Path = relativePath, ExcludedReason = classifier.Excluded[^1].Reason, ExcludedKind = classifier.Excluded[^1].Kind };
        // Variants at the check cap, the base parse counted in, parsed in parallel.
        var file = Analyzer.AnalyzeText(SourceText.From(text), relativePath, variants: VariantOptions.ForCheck(root, o.Variants));
        var kept = classifier.ExcludeParsed([file]);
        if (kept.Count == 0)
            return new FileCheckResult { Root = root, Path = relativePath, ExcludedReason = classifier.Excluded[^1].Reason, ExcludedKind = classifier.Excluded[^1].Kind, File = file };
        var result = new AnalysisResult { SchemaVersion = Analyzer.SchemaVersion, Files = kept, Run = new RunInfo { Root = root } };
        Aggregation.Fill(result, o.IncludeGenerated);
        FindingEngine.Apply(result, settings, o.IncludeGenerated);
        var partial = file.Types.Where(t => t.Partial).Select(t => t.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var partialSet = new HashSet<string>(partial, StringComparer.Ordinal);
        var findings = (result.Findings ?? []).Where(f => !(f.Rule == RuleIds.LargeType && partialSet.Contains(f.Member ?? ""))).ToList();
        var composites = (result.Composites ?? []).Where(c => !(c.Rule == RuleIds.BrainClass && partialSet.Contains(c.Member ?? ""))).ToList();
        return new FileCheckResult { Root = root, Path = relativePath, File = file, Findings = findings, PartialTypes = partial, Composites = composites };
    }

    /// <summary>The path of <paramref name="fullPath"/> relative to <paramref name="root"/> with '/' separators.</summary>
    public static string Relative(string root, string fullPath) => System.IO.Path.GetRelativePath(root, fullPath).Replace('\\', '/');
}
