using SkarpSharp.Analysis;
using SkarpSharp.Baselines;
using SkarpSharp.Findings;
using SkarpSharp.Model;
using SkarpSharp.Reporting;
using SkarpSharp.Tests.Baselines;

namespace SkarpSharp.Tests;

/// <summary>
/// The gate (rule and severity sets, exit codes, no baseline), possibly-renamed marking, the per-file cap and
/// --top, and the CI outputs (annotation limits and escaping). Golden outputs are in <see cref="CliGoldenTests"/>.
/// </summary>
public class GateAndCiTests
{
    static readonly string Sample = Path.Combine(FindRepo(), "tests", "SkarpSharp.Tests", "Golden", "sample");

    static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SkarpSharp.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }

    static (int Exit, string Out, string Err, string Json) Run(params string[] args) =>
        BaselineTests.Run(["analyze", Sample, "--parallelism", "2", "--no-save", .. args]); // no .skarp/ in the source tree

    static Finding F(string rule, string severity, string? state, string path = "a.cs", int line = 1, double? value = null) =>
        new() { Rule = rule, Severity = severity, BaselineState = state, Path = path, Line = line, Value = value, Member = "T::M()", PlaceKey = "T::M" };

    [Test]
    public async Task Gate_set_matches_rule_and_severity_pairs()
    {
        var findings = new List<Finding>
        {
            F(RuleIds.LongMethod, "warning", "new"),
            F(RuleIds.LongMethod, "notice", "worsened"),
            F(RuleIds.DeepNesting, "notice", "new"),
            F(RuleIds.EmptyCatch, "warning", "known"),
            F(RuleIds.MagicNumber, "notice", null),
        };
        var byDefault = Gate.Evaluate(findings, []);
        await Assert.That((byDefault.Tripped, byDefault.New, byDefault.Worsened, byDefault.Outside, byDefault.On.Single())).IsEqualTo((true, 1, 0, 2, "*:warning"));
        var notices = Gate.Evaluate(findings, ["*:notice"]);
        await Assert.That((notices.Tripped, notices.New, notices.Worsened, notices.Outside)).IsEqualTo((true, 1, 1, 1));
        var anything = Gate.Evaluate(findings, ["*:*"]);
        await Assert.That((anything.New, anything.Worsened, anything.Outside)).IsEqualTo((2, 1, 0));
        var oneRule = Gate.Evaluate(findings, ["long-method:*"]);
        await Assert.That((oneRule.New, oneRule.Worsened, oneRule.Outside)).IsEqualTo((1, 1, 1));
        var known = Gate.Evaluate(findings, ["empty-catch:warning"]);   // known findings never trip
        await Assert.That(known.Tripped).IsFalse();
        var other = Gate.Evaluate(findings, ["deep-nesting:warning", "complex-method:*"]);
        await Assert.That(other.Tripped).IsFalse();
        await Assert.That(Gate.Describe(other)).Contains("gate passed");
        await Assert.That(Gate.Describe(byDefault)).Contains("`skarp baseline`");
        await Assert.That(Gate.Describe(byDefault)).Contains("exit code 3");
    }

    [Test]
    // The golden sample against its baseline: deep-nesting notice new, long-method notice worsened, the rest known.
    [Arguments(new string[0], 0)]
    [Arguments(new[] { "--gate-on", "*:notice" }, 3)]
    [Arguments(new[] { "--gate-on", "*:*" }, 3)]
    [Arguments(new[] { "--gate-on", "long-method:notice" }, 3)]
    [Arguments(new[] { "--gate-on", "deep-nesting:*" }, 3)]
    [Arguments(new[] { "--gate-on", "deep-nesting:warning" }, 0)]
    [Arguments(new[] { "--gate-on", "empty-catch:*", "--gate-on", "too-many-parameters:*" }, 0)]
    public async Task Gate_exit_codes_on_the_sample(string[] set, int exit)
    {
        var r = Run(["--gate", .. set]);
        await Assert.That(r.Exit).IsEqualTo(exit);
        await Assert.That(r.Out).StartsWith(exit == 3 ? "Skarp: gate failed: " : "Skarp: gate passed");
        // Without --gate the same run never fails.
        await Assert.That(Run(set.Length == 0 ? [] : [.. set, "--no-gate"]).Exit).IsEqualTo(0);
    }

    [Test]
    public async Task Gate_needs_a_baseline_and_reports_in_every_format()
    {
        var noBaseline = Run("--gate", "--no-baseline");
        await Assert.That(noBaseline.Exit).IsEqualTo(2);
        await Assert.That(noBaseline.Err).Contains("--gate needs the baseline");
        var dir = Path.Combine(Path.GetTempPath(), "skarp-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "A.cs"), "class A { void M() { } }\n");
            var missing = BaselineTests.Run("analyze", dir, "--no-config", "--no-history", "--gate");
            await Assert.That(missing.Exit).IsEqualTo(2);
            await Assert.That(missing.Err).Contains("--gate needs a baseline file");
            await Assert.That(missing.Err).Contains("skarp baseline");
            // skarp baseline is not gated, even with the gate on in the settings file.
            File.WriteAllText(Path.Combine(dir, ".skarp.json"), "{ \"version\": 1, \"gate\": { \"enabled\": true, \"on\": [\"*:*\"] }, \"history\": { \"enabled\": false } }");
            await Assert.That(BaselineTests.Run("baseline", dir).Exit).IsEqualTo(0);
            await Assert.That(BaselineTests.Run("analyze", dir).Exit).IsEqualTo(0);
            File.WriteAllText(Path.Combine(dir, "A.cs"), "class A { void M() { try { } catch { } } }\n");
            var tripped = BaselineTests.Run("analyze", dir);
            await Assert.That(tripped.Exit).IsEqualTo(3);
            await Assert.That(tripped.Out).StartsWith("Skarp: gate failed: 1 finding new or worse (gate set *:*)");
        }
        finally { Directory.Delete(dir, true); }

        var json = Run("--gate", "--gate-on", "*:*", "--format", "json");
        await Assert.That(json.Exit).IsEqualTo(3);
        await Assert.That(json.Err).Contains("skarp: gate tripped");
        await Assert.That(json.Json).Contains("\"gate\": {");
        await Assert.That(json.Json).Contains("\"tripped\": true");
        var md = Run("--gate", "--format", "markdown");
        await Assert.That(md.Exit).IsEqualTo(0);
        await Assert.That(md.Out).Contains("**Gate passed**");
        var gh = Run("--gate", "--gate-on", "*:notice", "--format", "github");
        await Assert.That(gh.Exit).IsEqualTo(3);
        await Assert.That(gh.Out).Contains("::error title=skarp gate::gate tripped");
        await Assert.That(Run("--format", "json").Json).DoesNotContain("\"gate\""); // gate off: no gate object
    }

    [Test]
    public async Task A_fixed_and_a_new_finding_with_the_same_rule_and_value_in_one_file_are_possibly_renamed()
    {
        var baseline = new BaselineFile
        {
            Version = 1,
            Findings =
            [
                new() { Id = "long-method|a.cs|T::Old", Value = 80 },
                new() { Id = "long-method|a.cs|T::Other", Value = 90 },
                new() { Id = "long-method|b.cs|T::Gone", Value = 80 },
                new() { Id = "empty-catch|a.cs|T::Old", Value = null },
            ],
        };
        Finding New(string rule, string place, double? value, string path = "a.cs") =>
            new() { Rule = rule, Path = path, Id = FindingEngine.MakeId(rule, path, place), PlaceKey = place, Member = place + "()", Value = value };
        var findings = new List<Finding>
        {
            New(RuleIds.LongMethod, "T::Renamed", 80),      // same rule, file and value as T::Old: possibly renamed
            New(RuleIds.LongMethod, "T::Fresh", 80),        // T::Old is used up; b.cs is another file
            New(RuleIds.LongMethod, "T::Grown", 95),        // value differs from T::Other
            New(RuleIds.EmptyCatch, "T::Renamed", null),    // structural findings pair on "no value"
        };
        var summary = Baseline.Compare(findings, baseline, "x", null);
        await Assert.That((summary.New, summary.Fixed, summary.PossiblyRenamed)).IsEqualTo((4, 4, 2));
        await Assert.That(findings[0].PossiblyRenamedFrom).IsEqualTo("long-method|a.cs|T::Old");
        await Assert.That(findings[1].PossiblyRenamedFrom).IsNull();
        await Assert.That(findings[2].PossiblyRenamedFrom).IsNull();
        await Assert.That(findings[3].PossiblyRenamedFrom).IsEqualTo("empty-catch|a.cs|T::Old");
        foreach (var f in findings) await Assert.That(f.BaselineState).IsEqualTo("new"); // still counted as new
        await Assert.That(TextFormat.FindingLine(findings[0], true)).Contains("  new ").And.Contains(" (possibly renamed from T.Old)");
        await Assert.That(Gate.Evaluate(findings, ["long-method:*"]).New).IsEqualTo(3);
    }

    static AnalysisResult Files(params (string Path, int Members)[] files)
    {
        // Each member: complex-method (16 > 15) and path-explosion; member k has k extra branches, so later members rank higher.
        var result = new AnalysisResult { Run = new RunInfo { ToolVersion = "t", Root = "/r", Runtime = "rt" } };
        foreach (var (path, members) in files)
        {
            var body = string.Concat(Enumerable.Range(1, members).Select(k =>
                $"    void M{k}(bool b)\n    {{\n{string.Concat(Enumerable.Repeat("        if (b) { }\n", 15 + k))}    }}\n"));
            result.Files.Add(Analyzer.AnalyzeSource($"class {Path.GetFileNameWithoutExtension(path).ToUpperInvariant()}\n{{\n{body}}}\n", path));
        }
        Aggregation.Fill(result, false);
        FindingEngine.Apply(result, RuleSettings.Resolve("default", null, ["path-explosion.notice=off", "large-type.notice=off", "large-file.notice=off"]), false);
        return result;
    }

    [Test]
    public async Task Per_file_cap_and_top()
    {
        var result = Files(("a.cs", 6), ("b.cs", 2), ("c.cs", 4));
        var all = HumanReport.Eligible(result, new ReportOptions());
        // Default: 3 places per file, files by their worst ratio (a.cs has M6 with 21 branches), places within by ratio.
        var view = ReportLayout.Select(all, new ReportOptions());
        await Assert.That(view.Files.Select(f => f.Path)).IsEquivalentTo(new[] { "a.cs", "c.cs", "b.cs" }, CollectionOrdering.Matching);
        await Assert.That(view.Files.Select(f => f.Shown.Count)).IsEquivalentTo(new[] { 3, 3, 2 }, CollectionOrdering.Matching);
        await Assert.That(view.Files[0].Shown.Select(p => ReportLayout.ShortName(p.First))).IsEquivalentTo(new[] { "A.M6", "A.M5", "A.M4" }, CollectionOrdering.Matching);
        await Assert.That((view.Files[0].Hidden, view.Files[1].Hidden, view.Files[2].Hidden)).IsEqualTo((3, 1, 0));
        await Assert.That((view.ShownPlaces, view.TotalPlaces, view.ShownFiles, view.TotalFiles)).IsEqualTo((8, 12, 3, 3));

        var text = new StringWriter();
        HumanReport.WriteAnalyze(text, result, new ReportOptions());
        var s = text.ToString();
        await Assert.That(s).Contains(" at 12 places in 3 files; showing the 8 worst.");
        await Assert.That(s).Contains("  +3 more in this file (--per-file 0 or --all lists them)");
        await Assert.That(s).Contains("\na.cs\n".Replace("\n", Environment.NewLine, StringComparison.Ordinal));
        await Assert.That(s).Matches(@"\n  notice line \d+ A\.M6 complex-method: ");

        // --top still counts places: 4 places = a.cs 3, then 1 of c.cs.
        var top4 = ReportLayout.Select(all, new ReportOptions { Top = 4 });
        await Assert.That(top4.Files.Select(f => f.Shown.Count)).IsEquivalentTo(new[] { 3, 1 }, CollectionOrdering.Matching);
        await Assert.That(ReportLayout.Select(all, new ReportOptions { PerFile = 0, Top = 100 }).Files.Select(f => f.Shown.Count)).IsEquivalentTo(new[] { 6, 4, 2 }, CollectionOrdering.Matching);
        await Assert.That(ReportLayout.Select(all, new ReportOptions { All = true, Top = 1, PerFile = 1 }).ShownPlaces).IsEqualTo(12);
    }

    [Test]
    public async Task Per_file_cap_always_shows_a_files_warnings_and_fills_with_notices()
    {
        // Report order within a.cs: N1, N2, N3, W4, N5, W6 (warnings need not come first); then b.cs with two notices.
        Finding P(string path, string place, string severity) =>
            new() { Rule = RuleIds.LongMethod, Severity = severity, Path = path, Line = 1, Member = place + "()", PlaceKey = "T::" + place };
        var eligible = new List<Finding>
        {
            P("a.cs", "N1", "notice"), P("a.cs", "N2", "notice"), P("a.cs", "N3", "notice"), P("a.cs", "W4", "warning"),
            P("a.cs", "N5", "notice"), P("a.cs", "W6", "warning"), P("b.cs", "N1", "notice"), P("b.cs", "N2", "notice"),
        };
        string Names(FileGroup g) => string.Join(",", g.Shown.Select(p => p.First.PlaceKey![3..]));

        var view = ReportLayout.Select(eligible, new ReportOptions());
        await Assert.That(Names(view.Files[0])).IsEqualTo("N1,W4,W6"); // both warnings, one notice fills the cap of 3, report order kept
        await Assert.That((view.Files[0].Hidden, view.Files[0].CutByTop)).IsEqualTo((3, false));
        await Assert.That(Names(view.Files[1])).IsEqualTo("N1,N2");

        var one = ReportLayout.Select(eligible, new ReportOptions { PerFile = 1 });
        await Assert.That(Names(one.Files[0])).IsEqualTo("W4,W6"); // warnings past the cap; no room for notices
        await Assert.That(one.Files[0].Hidden).IsEqualTo(4);
        await Assert.That(Names(one.Files[1])).IsEqualTo("N1");

        var five = ReportLayout.Select(eligible, new ReportOptions { PerFile = 5 });
        await Assert.That(Names(five.Files[0])).IsEqualTo("N1,N2,N3,W4,W6");

        // --top still bounds the total and prefers the warnings of the file it cuts.
        var top1 = ReportLayout.Select(eligible, new ReportOptions { Top = 1 });
        await Assert.That(Names((await Assert.That(top1.Files).HasSingleItem()))).IsEqualTo("W4");
        await Assert.That(top1.Files[0].CutByTop).IsTrue();
        var top4 = ReportLayout.Select(eligible, new ReportOptions { Top = 4 });
        await Assert.That(top4.Files.Select(Names)).IsEquivalentTo(new[] { "N1,W4,W6", "N1" }, CollectionOrdering.Matching);
        await Assert.That(top4.Files[1].CutByTop).IsTrue();
        await Assert.That(ReportLayout.Select(eligible, new ReportOptions { All = true, PerFile = 1 }).ShownPlaces).IsEqualTo(8);
    }

    [Test]
    public async Task Annotations_respect_the_per_step_limits_and_are_escaped()
    {
        var changed = new List<Finding>();
        for (int i = 1; i <= 25; i++) changed.Add(F(RuleIds.LongMethod, "warning", i % 2 == 0 ? "new" : "worsened", line: i, value: 100));
        for (int i = 1; i <= 3; i++) changed.Add(F(RuleIds.DeepNesting, "notice", "new", line: 100 + i));
        var (annotated, dropped) = CiReports.Annotations(changed);
        await Assert.That((annotated.Count, dropped)).IsEqualTo((13, 15));
        await Assert.That(annotated.Count(f => f.Severity == "warning")).IsEqualTo(10);
        await Assert.That(annotated.Where(f => f.Severity == "warning").Take(3).Select(f => f.Line)).IsEquivalentTo(new[] { 1, 2, 3 }, CollectionOrdering.Matching); // report order

        foreach (var f in changed) { f.Message = "84 physical lines"; f.Threshold = 70; f.Comparison = "over"; f.BaselineValue = 90; }
        var result = new AnalysisResult { Findings = changed, Baseline = new BaselineSummary() };
        var w = new StringWriter();
        CiReports.WriteGitHub(w, result, new ReportOptions(), "src/");
        var lines = w.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        await Assert.That(lines.Count(l => l.StartsWith("::warning ", StringComparison.Ordinal))).IsEqualTo(10);
        await Assert.That(lines.Count(l => l.StartsWith("::notice ", StringComparison.Ordinal))).IsEqualTo(3);
        await Assert.That(lines[0]).StartsWith("::warning file=src/a.cs,line=1,title=skarp long-method::T.M: 84 physical lines (over 70) [worsened from 90]. ");
        await Assert.That(lines).Contains(l => l.StartsWith("skarp: 28 new or worsened findings; 13 annotated, 15 not annotated (GitHub shows at most 10 warnings and 10 notices per step", StringComparison.Ordinal));

        await Assert.That(CiReports.EscapeData("a%b\r\nc")).IsEqualTo("a%25b%0D%0Ac");
        await Assert.That(CiReports.EscapeProperty("dir,x:y.cs")).IsEqualTo("dir%2Cx%3Ay.cs");

        var md = new StringWriter();
        CiReports.WriteMarkdown(md, result, new ReportOptions());
        await Assert.That(md.ToString()).Contains("### New and worsened (28)");
        await Assert.That(md.ToString()).Contains("15 are listed here only");

        // Without a baseline: warnings only (first 10 in report order), no notices, and a baseline suggestion.
        foreach (var f in changed) f.BaselineState = null;
        var noBaseline = new StringWriter();
        CiReports.WriteGitHub(noBaseline, new AnalysisResult { Findings = changed }, new ReportOptions(), "");
        var nb = noBaseline.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        await Assert.That(nb.Count(l => l.StartsWith("::warning ", StringComparison.Ordinal))).IsEqualTo(10);
        await Assert.That(nb).DoesNotContain(l => l.StartsWith("::notice ", StringComparison.Ordinal));
        await Assert.That(nb[0]).StartsWith("::warning file=a.cs,line=1,title=skarp long-method::T.M: 84 physical lines (over 70). ");
        await Assert.That(nb).Contains("skarp: no baseline file; 25 warnings, 10 annotated, 15 not annotated (GitHub shows at most 10 warnings per step); notices are not annotated without a baseline.");
        await Assert.That(nb).Contains(l => l.Contains("commit .skarp-baseline.json", StringComparison.Ordinal));
        var mdNoBaseline = new StringWriter();
        CiReports.WriteMarkdown(mdNoBaseline, new AnalysisResult { Findings = changed }, new ReportOptions());
        await Assert.That(mdNoBaseline.ToString()).Contains("run `skarp baseline` and commit `.skarp-baseline.json`");
    }

    [Test]
    public async Task Repository_prefix_makes_annotation_paths_relative_to_the_git_root()
    {
        await Assert.That(ReportLayout.RepositoryPrefix(Sample)).IsEqualTo("tests/SkarpSharp.Tests/Golden/sample/");
        await Assert.That(ReportLayout.RepositoryPrefix(FindRepo())).IsEqualTo("");
        var gh = Run("--format", "github", "--gate-on", "*:*");
        await Assert.That(gh.Out).Contains("::notice file=tests/SkarpSharp.Tests/Golden/sample/Program.cs,line=3,");
    }
}
