using System.Globalization;
using SkarpSharp.Model;

namespace SkarpSharp.Findings;

/// <summary>
/// The rules as pure functions of member, type and file records and the
/// active thresholds. Each returns the findings it produces, unordered; <see cref="FindingEngine"/> orders them.
/// </summary>
public static class Rules
{
    /// <summary>Rules whose findings make a file count as complex for active-complex-file (complex-method to path-explosion, bumpy-road and complex-condition).</summary>
    public static readonly IReadOnlySet<string> ComplexityRules = new HashSet<string>(StringComparer.Ordinal)
    {
        RuleIds.ComplexMethod, RuleIds.DeepNesting, RuleIds.LongMethod, RuleIds.LargeType, RuleIds.LargeFile,
        RuleIds.TooManyParameters, RuleIds.PathExplosion, RuleIds.BumpyRoad, RuleIds.ComplexCondition,
    };

    /// <summary>Result of judging a value set against a rule: the severity, the condition that fired and its level.</summary>
    public readonly record struct Verdict(Severity Severity, Condition Condition, LevelThreshold Level, double Value);

    /// <summary>Warning first, then notice; within a level the first condition that holds.</summary>
    public static Verdict? Judge(ActiveRule rule, Func<string, double?> value)
    {
        foreach (var sev in (ReadOnlySpan<Severity>)[Severity.Warning, Severity.Notice])
        {
            if (rule.Level(sev) is not { } level) continue;
            foreach (var c in level.AnyOf)
                if (value(c.Metric) is double v && c.Test(v)) return new Verdict(sev, c, level, v);
        }
        return null;
    }

    public static double? MemberMetric(MemberResult m, string metric) => metric switch
    {
        "cognitive" => m.Cognitive,
        "cyclomatic" => m.Cyclomatic,
        "cyclomaticMicrosoft" => m.CyclomaticMicrosoft,
        "nesting" => m.Nesting,
        "sourceLines" => m.Lines.Source,
        "physicalLines" => m.Lines.Physical,
        "statements" => m.Statements,
        "npath" => m.Npath,
        _ => null,
    };

    public static string MetricWords(string metric) => metric switch
    {
        "cognitive" => "cognitive complexity",
        "cyclomatic" => "cyclomatic complexity",
        "cyclomaticMicrosoft" => "cyclomatic complexity (Microsoft counting)",
        "nesting" => "nesting depth",
        "sourceLines" => "source lines",
        "physicalLines" => "physical lines",
        "lines" => "lines (physical by default, source with --long-method-lines source)",
        "statements" => "statements",
        "npath" => "NPath",
        "parameters" => "parameters",
        "constructorParameters" => "constructor parameters",
        "wmc" => "WMC (sum of member cyclomatic complexity)",
        "methods" => "methods",
        "fields" => "fields",
        "bumps" => "bumps",
        "booleanOperators" => "boolean operators",
        "commits" => "commits",
        "magicNumbers" => "magic numbers",
        "cbo" => "class coupling (framework types excluded)",
        "cboAll" => "class coupling (framework types counted)",
        "cboMicrosoft" => "class coupling (Microsoft counting)",
        _ => metric,
    };

    static string N(double v) => v >= 1e15 ? v.ToString("0.##e0", CultureInfo.InvariantCulture) : ((long)v).ToString("N0", CultureInfo.InvariantCulture);

    internal static Finding Make(string rule, Verdict v, ActiveRule active, string path, int line, string? member, string message) => new()
    {
        Rule = rule,
        Severity = v.Severity == Severity.Warning ? "warning" : "notice",
        Path = path,
        Line = line,
        Member = member,
        Value = v.Value,
        Metric = v.Condition.Metric,
        Threshold = v.Condition.Value,
        Comparison = Condition.Word(v.Condition.Comparison),
        Preset = active.LevelPreset(v.Severity),
        ThresholdSource = v.Level.Source,
        Message = message,
    };

    static Finding MakeStructural(string rule, Severity sev, LevelThreshold level, ActiveRule active, string path, int line, string? member, string message, double? value = null, string? metric = null) => new()
    {
        Rule = rule,
        Severity = sev == Severity.Warning ? "warning" : "notice",
        Path = path,
        Line = line,
        Member = member,
        Value = value,
        Metric = metric,
        Preset = active.LevelPreset(sev),
        ThresholdSource = level.Source,
        Message = message,
    };

    // ---- 1 complex method ---------------------------------------------------------------------------------------------

    /// <summary>Folded metric: judged on top-level records only (a nested record never repeats its parent's value).</summary>
    public static Finding? ComplexMethod(MemberResult m, string path, bool topLevel, ActiveRule rule)
    {
        if (!topLevel) return null;
        return Judge(rule, k => MemberMetric(m, k)) is { } v
            ? Make(RuleIds.ComplexMethod, v, rule, path, m.StartLine, m.Id, $"{MetricWords(v.Condition.Metric)} {N(v.Value)}")
            : null;
    }

    // ---- 2 deep nesting -----------------------------------------------------------------------------------------------

    public static Finding? DeepNesting(MemberResult m, string path, bool topLevel, ActiveRule rule)
    {
        if (!topLevel) return null;
        return Judge(rule, k => MemberMetric(m, k)) is { } v
            ? Make(RuleIds.DeepNesting, v, rule, path, m.StartLine, m.Id, $"nesting depth {N(v.Value)}")
            : null;
    }

    // ---- 3 long method ------------------------------------------------------------------------------------------------

    /// <summary>Lambdas and local functions are lines of their parent and are not judged themselves.</summary>
    public static Finding? LongMethod(MemberResult m, string path, ActiveRule rule)
    {
        if (m.Kind is "Lambda" or "LocalFunction") return null;
        return Judge(rule, k => MemberMetric(m, k)) is { } v
            ? Make(RuleIds.LongMethod, v, rule, path, m.StartLine, m.Id, $"{N(v.Value)} {MetricWords(v.Condition.Metric)}")
            : null;
    }

    // ---- 4 large type -------------------------------------------------------------------------------------------------

    public static double? TypeMetric(TypeSummary t, string metric) => metric switch
    {
        "sourceLines" => t.SourceLines,
        "wmc" => t.SumCyclomatic,
        "methods" => t.Methods + t.Constructors,
        "fields" => t.Fields,
        "statements" => t.Statements,
        _ => null,
    };

    public static Finding? LargeType(TypeSummary t, ActiveRule rule)
    {
        if (t.Kind is "enum" or "" || t.Path.Length == 0) return null;
        if (Judge(rule, k => TypeMetric(t, k)) is not { } v) return null;
        // The value that crossed the limit comes first, the other measured values follow as context, so the
        // limit shown next to the message reads against the right number ("WMC 21; also 62 source lines").
        string Part(string metric) => metric == "wmc" ? $"WMC {N(TypeMetric(t, metric) ?? 0)}" : $"{N(TypeMetric(t, metric) ?? 0)} {MetricWords(metric)}";
        var others = v.Level.AnyOf.Select(c => c.Metric).Where(m => m != v.Condition.Metric).Distinct().Select(Part).ToList();
        var decl = t.Declarations > 1 ? $" in {t.Declarations} partial declarations" : "";
        return Make(RuleIds.LargeType, v, rule, t.Path, t.Line, t.Name,
            $"{Part(v.Condition.Metric)}{decl}" + (others.Count > 0 ? $" (also {string.Join(", ", others)})" : ""));
    }

    // ---- 5 large file -------------------------------------------------------------------------------------------------

    public static Finding? LargeFile(FileResult f, ActiveRule rule)
    {
        double? Metric(string k) => k switch { "sourceLines" => f.Lines.Source, "physicalLines" => f.Lines.Physical, _ => null };
        return Judge(rule, Metric) is { } v
            ? Make(RuleIds.LargeFile, v, rule, f.Path, 1, null, $"{N(v.Value)} {MetricWords(v.Condition.Metric)}")
            : null;
    }

    // ---- 6 too many parameters ----------------------------------------------------------------------------------------

    /// <summary>Constructors use the constructor limit when the preset has one, everything else the general limit.</summary>
    public static Verdict? JudgeParameters(ActiveRule rule, int parameters, bool constructor)
    {
        foreach (var sev in (ReadOnlySpan<Severity>)[Severity.Warning, Severity.Notice])
        {
            if (rule.Level(sev) is not { } level) continue;
            var c = constructor ? level.Find("constructorParameters") ?? level.Find("parameters") : level.Find("parameters");
            if (c is not null && c.Test(parameters)) return new Verdict(sev, c, level, parameters);
        }
        return null;
    }

    public static Finding? TooManyParameters(MemberResult m, string path, ActiveRule rule)
    {
        if (m.Parameters is not int n || m.Kind == "Lambda" || m.InheritedSignature == true) return null;
        bool ctor = m.Kind == "Constructor";
        return JudgeParameters(rule, n, ctor) is { } v
            ? Make(RuleIds.TooManyParameters, v, rule, path, m.StartLine, m.Id, $"{n} {(ctor ? "constructor parameters" : "parameters")}")
            : null;
    }

    public static Finding? TooManyParameters(TypeCounts t, string path, ActiveRule rule)
    {
        if (t.PrimaryConstructorParameters is not int n) return null;
        return JudgeParameters(rule, n, constructor: true) is { } v
            ? Make(RuleIds.TooManyParameters, v, rule, path, t.StartLine, t.Name, $"primary constructor with {n} parameters")
            : null;
    }

    // ---- 7 path explosion ---------------------------------------------------------------------------------------------

    /// <summary>NPath excludes nested bodies, so every record is judged on its own value.</summary>
    public static Finding? PathExplosion(MemberResult m, string path, ActiveRule rule)
    {
        if (Judge(rule, k => MemberMetric(m, k)) is not { } v) return null;
        var text = m.NpathSaturated ? "NPath over 9.2e18 (saturated)" : $"NPath {N(v.Value)} (about 2^{m.NpathLog2})";
        return Make(RuleIds.PathExplosion, v, rule, path, m.StartLine, m.Id, text);
    }

    // ---- 9 bumpy road -------------------------------------------------------------------------------------------------

    /// <summary>Chunks: maximal runs of adjacent top-level statements that reach the bump depth.</summary>
    public static List<NestedBlockFact[]> Bumps(MemberResult m, int depth)
    {
        var chunks = new List<NestedBlockFact[]>();
        if (m.NestedBlocks is null) return chunks;
        var current = new List<NestedBlockFact>();
        int lastSeq = int.MinValue;
        foreach (var b in m.NestedBlocks.Where(b => b.Depth >= depth).OrderBy(b => b.Seq))
        {
            if (current.Count > 0 && b.Seq != lastSeq + 1) { chunks.Add([.. current]); current.Clear(); }
            current.Add(b);
            lastSeq = b.Seq;
        }
        if (current.Count > 0) chunks.Add([.. current]);
        return chunks;
    }

    public static Finding? BumpyRoad(MemberResult m, string path, ActiveRule rule)
    {
        if (m.NestedBlocks is null) return null;
        int depth = int.TryParse(rule.Parameters.GetValueOrDefault("depth"), NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d : 2;
        var bumps = Bumps(m, depth);
        if (Judge(rule, k => k == "bumps" ? bumps.Count : null) is not { } v) return null;
        var spans = string.Join(", ", bumps.Select(c => $"{c[0].StartLine}-{c[^1].EndLine}"));
        int deepest = bumps.Max(c => c.Max(b => b.Depth));
        return Make(RuleIds.BumpyRoad, v, rule, path, m.StartLine, m.Id, $"{bumps.Count} bumps (lines {spans}; deepest nesting {deepest})");
    }

    // ---- 10 complex condition -----------------------------------------------------------------------------------------

    public static Finding? ComplexCondition(MemberResult m, string path, ActiveRule rule)
    {
        if (m.BooleanOperatorsMax is not int ops) return null;
        return Judge(rule, k => k == "booleanOperators" ? ops : null) is { } v
            ? Make(RuleIds.ComplexCondition, v, rule, path, m.BooleanOperatorsLine ?? m.StartLine, m.Id, $"{ops} boolean operators in one branch condition")
            : null;
    }

    // ---- 11 empty catch -----------------------------------------------------------------------------------------------

    static readonly HashSet<string> CatchAll = new(StringComparer.Ordinal) { "Exception", "System.Exception", "global::System.Exception" };

    /// <summary>Warning: no comment, catches everything (bare catch or Exception), no filter. Otherwise notice.</summary>
    public static Severity EmptyCatchSeverity(EmptyCatchFact c) =>
        !c.HasComment && !c.HasFilter && (c.Caught is null || CatchAll.Contains(c.Caught)) ? Severity.Warning : Severity.Notice;

    public static IEnumerable<Finding> EmptyCatch(MemberResult m, string path, ActiveRule rule)
    {
        if (m.EmptyCatches is null) yield break;
        foreach (var c in m.EmptyCatches)
        {
            var sev = EmptyCatchSeverity(c);
            if (sev == Severity.Warning && rule.Warning is null) sev = Severity.Notice; // warning level switched off
            if (rule.Level(sev) is not { } level) continue;
            var what = c.Caught is null ? "bare catch" : $"catch ({c.Caught})";
            var detail = (c.HasComment ? "with a comment" : "without a comment") + (c.HasFilter ? ", with a when filter" : "");
            yield return MakeStructural(RuleIds.EmptyCatch, sev, level, rule, path, c.Line, m.Id, $"empty {what} {detail}");
        }
    }

    // ---- 12 magic numbers ---------------------------------------------------------------------------------------------

    /// <summary>One notice per member with the count of literals outside the ignore list (exempt positions are not in the facts).</summary>
    public static Finding? MagicNumber(MemberResult m, string path, ActiveRule rule)
    {
        if (m.NumericLiterals is null || rule.Notice is not { } level) return null;
        var ignore = RuleCatalog.ParseNumberList(rule.Parameters.GetValueOrDefault("ignore") ?? "") ?? [];
        var magic = m.NumericLiterals.Where(l => !ignore.Contains(l.Value)).ToList();
        if (magic.Count == 0) return null;
        var sample = string.Join(", ", magic.Select(l => l.Text).Distinct().Take(5)) + (magic.Select(l => l.Text).Distinct().Count() > 5 ? ", ..." : "");
        return MakeStructural(RuleIds.MagicNumber, Severity.Notice, level, rule, path, magic[0].Line, m.Id,
            $"{magic.Count} magic number{(magic.Count == 1 ? "" : "s")}: {sample}", magic.Count, "magicNumbers");
    }

    // ---- 8 active complex file ----------------------------------------------------------------------------------------

    /// <summary>
    /// Files in the top share of changed files (commits in the window; ties broken like the hotspot table: cognitive sum,
    /// then path) that already have a complexity-type finding.
    /// </summary>
    public static List<Finding> ActiveComplexFile(IReadOnlyList<FileResult> files, IReadOnlyCollection<Finding> otherFindings, ActiveRule rule)
    {
        var list = new List<Finding>();
        // The "untested" label, from untested-complex-method findings (only with a coverage report).
        var untested = otherFindings.Where(f => f.Rule == RuleIds.UntestedComplexMethod).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        if (rule.Notice is not { } level) return list;
        double share = double.TryParse(rule.Parameters.GetValueOrDefault("share"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 4;
        var changed = files
            .Where(f => f.History is { Commits: > 0 })
            .Select(f => (File: f, Cog: f.Members.Where(m => m.Parent is null).Sum(m => (long)m.Cognitive)))
            .OrderByDescending(x => x.File.History!.Commits).ThenByDescending(x => x.Cog).ThenBy(x => x.File.Path, StringComparer.Ordinal)
            .ToList();
        if (changed.Count == 0) return list;
        int k = Math.Max(1, (int)Math.Ceiling(changed.Count * share / 100.0));
        var complexByFile = otherFindings.Where(f => ComplexityRules.Contains(f.Rule)).GroupBy(f => f.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        int cutoff = changed[Math.Min(k, changed.Count) - 1].File.History!.Commits;
        for (int i = 0; i < Math.Min(k, changed.Count); i++)
        {
            var f = changed[i].File;
            if (!complexByFile.TryGetValue(f.Path, out var n)) continue;
            int commits = f.History!.Commits;
            list.Add(new Finding
            {
                Rule = RuleIds.ActiveComplexFile, Severity = "notice", Path = f.Path, Line = 1,
                Value = commits, Metric = "commits", Threshold = cutoff, Comparison = Condition.Word(Comparison.AtLeast),
                Preset = rule.NoticePreset, ThresholdSource = level.Source,
                Message = $"{commits} commits in the window, rank {i + 1} of {changed.Count} changed files (top {Condition.Num(share)} percent = {k} files); {n} other finding{(n == 1 ? "" : "s")} in the file"
                    + (untested.Contains(f.Path) ? "; untested: a complex member no test runs" : ""),
            });
        }
        return list;
    }

    // ---- 25 untested complex method ------------------------------------------------------------------------

    /// <summary>
    /// A member with cyclomatic complexity at the threshold (default 6) or more, at least
    /// one coverable line in the coverage reports and none of them covered. Never for members with an inactive region,
    /// excluded or generated code, test code, or without coverage facts (no report: not evaluated).
    /// </summary>
    public static Finding? UntestedComplexMethod(MemberResult m, string path, ActiveRule rule)
    {
        if (m.Coverage is not { Status: Model.CoverageFacts.Data, CoverableLines: int coverable and > 0, CoveredLines: 0, InactiveRegions: null }) return null;
        return Judge(rule, k => MemberMetric(m, k)) is { } v
            ? Make(RuleIds.UntestedComplexMethod, v, rule, path, m.StartLine, m.Id,
                $"{MetricWords(v.Condition.Metric)} {N(v.Value)}, 0 of {coverable} coverable line{(coverable == 1 ? "" : "s")} covered")
            : null;
    }
}
