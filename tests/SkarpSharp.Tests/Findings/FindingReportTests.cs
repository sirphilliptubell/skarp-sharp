using System.Text.Json;
using SkarpSharp.Analysis;
using SkarpSharp.Findings;
using SkarpSharp.History;
using SkarpSharp.Model;
using SkarpSharp.Reporting;
using SkarpSharp.Tests.History;

namespace SkarpSharp.Tests.Findings;

/// <summary>Ordering and the activity join, active-complex-file, test exclusion in history, renderers and JSON schema 1.</summary>
public class FindingReportTests
{
    static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1700000000);

    static Finding F(string rule, string severity, string path, int line, int? commits, string place = "M", double? value = null, double? threshold = null) =>
        new() { Rule = rule, Severity = severity, Path = path, Line = line, Commits = commits, PlaceKey = place, Value = value, Threshold = threshold };

    [Test]
    public async Task Order_is_file_activity_then_places_by_distance_over_the_limit()
    {
        // Files by activity; within a file, places by value / threshold.
        var list = new List<Finding>
        {
            F(RuleIds.MagicNumber, "notice", "a.cs", 3, 1, "M1", 4, null),
            F(RuleIds.LongMethod, "warning", "a.cs", 2, 1, "M1", 80, 70),       // 1.14, a warning
            F(RuleIds.ComplexMethod, "notice", "a.cs", 9, 1, "M2", 45, 15),     // 3.0
            F(RuleIds.ComplexMethod, "notice", "z.cs", 3, 7, "X", 16, 15),      // most active file
            F(RuleIds.ComplexMethod, "notice", "b.cs", 5, 1, "Y", 100, 15),     // 6.7: b.cs before a.cs at equal activity
            F(RuleIds.EmptyCatch, "warning", "c.cs", 1, 0, "Z"),
        };
        FindingEngine.Sort(list);
        await Assert.That(list.Select(f => $"{f.Path}:{f.Line}")).IsEquivalentTo(new[] { "z.cs:3", "b.cs:5", "a.cs:9", "a.cs:2", "a.cs:3", "c.cs:1" }, CollectionOrdering.Matching);
        // Without history (commits null) files go by their worst ratio.
        foreach (var f in list) f.Commits = null;
        FindingEngine.Sort(list);
        await Assert.That(list.Select(f => $"{f.Path}:{f.Line}")).IsEquivalentTo(new[] { "b.cs:5", "a.cs:9", "a.cs:2", "a.cs:3", "z.cs:3", "c.cs:1" }, CollectionOrdering.Matching);
        // One report entry per place, rules inside in rule order.
        var entries = ReportLayout.Entries(list);
        await Assert.That(entries.Count).IsEqualTo(5);
        await Assert.That(entries[2].Findings.Select(f => f.Rule)).IsEquivalentTo(new[] { RuleIds.LongMethod, RuleIds.MagicNumber }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Equal_ratios_put_the_warning_place_first()
    {
        var list = new List<Finding>
        {
            F(RuleIds.ComplexMethod, "notice", "a.cs", 1, null, "A", 30, 15),
            F(RuleIds.LongMethod, "warning", "a.cs", 50, null, "B", 140, 70),
        };
        FindingEngine.Sort(list);
        await Assert.That(list.Select(f => f.PlaceKey)).IsEquivalentTo(new string?[] { "B", "A" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Within_a_file_member_places_come_before_type_and_file_places()
    {
        // A type far over its limit (20x) still comes after every member place; the file place comes last.
        Finding Member(string place, int line, double value, double threshold) =>
            new() { Rule = RuleIds.ComplexMethod, Severity = "notice", Path = "a.cs", Line = line, Member = $"T::{place}()", PlaceKey = $"T::{place}", Value = value, Threshold = threshold };
        var list = new List<Finding>
        {
            new() { Rule = RuleIds.LargeFile, Severity = "notice", Path = "a.cs", Line = 1, Value = 5000, Threshold = 500 },
            new() { Rule = RuleIds.LargeType, Severity = "notice", Path = "a.cs", Line = 2, Member = "T", PlaceKey = "T", TypePlace = true, Value = 2000, Threshold = 100 },
            Member("Small", 30, 16, 15),
            Member("Big", 10, 45, 15),
            new() { Rule = RuleIds.ComplexMethod, Severity = "notice", Path = "b.cs", Line = 1, Member = "U::M()", PlaceKey = "U::M", Value = 16, Threshold = 15 },
        };
        FindingEngine.Sort(list);
        // Files still go by their worst ratio (a.cs has the 20x type), then member, type, file places within a.cs.
        await Assert.That(list.Select(f => $"{f.Path}:{f.Line}")).IsEquivalentTo(new[] { "a.cs:10", "a.cs:30", "a.cs:2", "a.cs:1", "b.cs:1" }, CollectionOrdering.Matching);
        await Assert.That(list.Select(ReportLayout.ShortName)).IsEquivalentTo(new[] { "T.Big", "T.Small", "type T", "(file)", "U.M" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Short_names_drop_namespaces_parameters_and_ordinals()
    {
        // Type and member; nested types joined with '.'; lambdas and local functions under their parent.
        await Assert.That(ReportLayout.ShortKey("SkarpSharp.Syntax.MemberCollector::Collect/Add")).IsEqualTo("MemberCollector.Collect/Add");
        await Assert.That(ReportLayout.ShortKey("Ns.Sub.Outer+Inner::Run")).IsEqualTo("Outer.Inner.Run");
        await Assert.That(ReportLayout.ShortKey("Ns.Repo`1::Get#2")).IsEqualTo("Repo.Get");
        await Assert.That(ReportLayout.ShortKey("Outer`2+Inner::Map/lambda2/Local/lambda1")).IsEqualTo("Outer.Inner.Map/lambda2/Local/lambda1");
        await Assert.That(ReportLayout.ShortKey("T::_net/lambda1")).IsEqualTo("T._net/lambda1");
        await Assert.That(ReportLayout.ShortKey("Geo.Point::.ctor#2")).IsEqualTo("Point.ctor");
        await Assert.That(ReportLayout.ShortKey("Geo.Point::.cctor")).IsEqualTo("Point.cctor");
        await Assert.That(ReportLayout.ShortKey("Bag::this[].get")).IsEqualTo("Bag.this[].get");
        await Assert.That(ReportLayout.ShortKey("<top-level>")).IsEqualTo("<top-level>");
        await Assert.That(ReportLayout.ShortKey("<top-level>/lambda1")).IsEqualTo("<top-level>/lambda1");
        await Assert.That(ReportLayout.ShortKey("<top-level>/Local")).IsEqualTo("<top-level>/Local");
        await Assert.That(ReportLayout.ShortType("Ns.Outer`1+Inner")).IsEqualTo("Outer.Inner");
        var type = new Finding { Rule = RuleIds.LargeType, Path = "a.cs", Member = "Ns.Outer+Inner", PlaceKey = "Ns.Outer+Inner", TypePlace = true };
        await Assert.That(ReportLayout.ShortName(type)).IsEqualTo("type Outer.Inner");
        await Assert.That(ReportLayout.PathLine("src\\a.cs", 12)).IsEqualTo("src/a.cs:12");
        await Assert.That(ReportLayout.RenamedName("long-method|src/a.cs|Sample.OrderBook::Describe")).IsEqualTo("OrderBook.Describe");
        await Assert.That(ReportLayout.RenamedName("large-type|src/a.cs|Sample.OrderBook")).IsEqualTo("type OrderBook");
        await Assert.That(ReportLayout.RenamedName("large-file|src/a.cs|")).IsEqualTo("(file)");
        await Assert.That(ReportLayout.RenamedName("empty-catch|a.cs|Bag::Add~2")).IsEqualTo("Bag.Add");

        // Names from real code: nested types, lambdas, local functions, top-level statements.
        var result = new AnalysisResult
        {
            Files =
            [
                Analyzer.AnalyzeSource("namespace N;\nclass Outer\n{\n    class Inner\n    {\n        System.Func<int, int> _f = x => x > 1 && x < 9 || x == 5 ? 1 : 0;\n" +
                    "        void Run(int a, int b, int c, int d, int e) { int L(int p) => p > 1 && p < 9 || p == 5 ? 1 : 0; }\n    }\n}\n", "c.cs"),
                Analyzer.AnalyzeSource("var q = args.Length > 1 && args.Length < 9 || args.Length == 5;\nif (args.Length > 1 && args.Length < 9 || args.Length == 5) { }\n", "p.cs"),
            ],
        };
        Aggregation.Fill(result, false);
        FindingEngine.Apply(result, RuleSettings.Resolve("default", null, []), false);
        var names = result.Findings!.Select(f => ReportLayout.ShortName(f)).Distinct().ToList();
        await Assert.That(names).Contains("Outer.Inner._f/lambda1");
        await Assert.That(names).Contains("Outer.Inner.Run");
        await Assert.That(names).Contains("Outer.Inner.Run/L");
        await Assert.That(names).Contains("<top-level>");
        var w = new StringWriter();
        HumanReport.WriteAnalyze(w, result, new ReportOptions());
        await Assert.That(w.ToString()).Contains("  notice line 6 Outer.Inner._f/lambda1 complex-condition: ");
        await Assert.That(w.ToString()).DoesNotContain("lambda@");
        await Assert.That(result.Findings!.First(x => x.Rule == RuleIds.ComplexCondition && x.Path == "c.cs").Member).IsEqualTo("N.Outer+Inner::_f/lambda@6:36"); // JSON keeps the member id
    }

    [Test]
    public async Task Large_type_names_the_value_that_crossed_the_limit()
    {
        // The crossing metric comes first, the limit follows it, the other values are context.
        var f = new Finding { Rule = RuleIds.LargeType, Message = "WMC 21 (also 62 source lines)", Threshold = 20, Comparison = "over" };
        await Assert.That(ReportLayout.Measure(f)).IsEqualTo("WMC 21 (over 20) (also 62 source lines)");
        var t = new TypeSummary { Name = "T", Kind = "class", Path = "a.cs", Line = 1, SourceLines = 62, SumCyclomatic = 21, Declarations = 1 };
        var finding = Rules.LargeType(t, RuleSettings.Resolve("default", null, [])[RuleIds.LargeType])!;
        await Assert.That(finding.Message).IsEqualTo("WMC 21 (also 62 source lines)");
        var big = new TypeSummary { Name = "T", Kind = "class", Path = "a.cs", Line = 1, SourceLines = 900, SumCyclomatic = 5, Declarations = 2 };
        var byLines = Rules.LargeType(big, RuleSettings.Resolve("default", null, [])[RuleIds.LargeType])!;
        await Assert.That(byLines.Message).StartsWith("900 source lines in 2 partial declarations (also WMC 5");
    }

    [Test]
    public async Task Top_counts_places_and_defaults_to_20()
    {
        // --top counts places (not findings), 20 by default, from the command line too.
        var parsed = SkarpSharp.Configuration.CommandLine.Parse(["analyze", ".", "--no-config"]);
        await Assert.That(parsed.Options!.ReportOptions().Top).IsEqualTo(20);
        var result = Sample(25); // two findings per place
        var w = new StringWriter();
        HumanReport.WriteAnalyze(w, result, parsed.Options!.ReportOptions());
        await Assert.That(w.ToString()).Contains(" at 25 places in 25 files; showing the 20 worst.");
        await Assert.That(w.ToString().Split('\n').Count(l => l.StartsWith("  notice line ", StringComparison.Ordinal))).IsEqualTo(40);
        var five = SkarpSharp.Configuration.CommandLine.Parse(["analyze", ".", "--no-config", "--top", "5"]).Options!.ReportOptions();
        var w5 = new StringWriter();
        HumanReport.WriteAnalyze(w5, result, five);
        await Assert.That(w5.ToString()).Contains("; showing the 5 worst.");
    }

    static string Complex(string name, int ifs, int edit) =>
        $"class {name}\n{{\n    void M(bool b)\n    {{\n{string.Concat(Enumerable.Repeat("        if (b) { }\n", ifs))}    }}\n}}\n// edit {edit}\n";

    [Test]
    public async Task Findings_in_active_files_come_first_and_active_complex_file_fires_on_the_top_share()
    {
        using var r = new SyntheticRepo();
        // Hot.cs: changed in 5 commits, complex. Cold.cs: 1 commit, more complex. Quiet.cs: 3 commits, simple.
        for (int k = 1; k <= 5; k++)
        {
            r.Write("Hot.cs", Complex("Hot", 16, k));
            if (k == 1) r.Write("Cold.cs", Complex("Cold", 40, k));
            if (k <= 3) r.Write("Quiet.cs", Complex("Quiet", 1, k));
            r.Commit($"c{k}", T0.AddHours(k));
        }
        var run = await HistoryRunner.RunAsync(r.Root, new HistoryOptions { Environment = r.Environment, AllHistory = true });
        var result = Analyzer.AnalyzeDirectory(r.Root, new AnalyzerOptions { Parallelism = 2 });
        HistoryJoin.Apply(result, run, 20, false);
        FindingEngine.Apply(result, RuleSettings.Resolve("default", null, []), false);
        var findings = result.Findings!;
        await Assert.That(findings[0].Path).IsEqualTo("Hot.cs");
        await Assert.That(findings[0].Commits).IsEqualTo(5);
        await Assert.That(findings[^1].Path).IsEqualTo("Cold.cs");
        // 3 changed files, top 4 percent = 1 file (Hot.cs, which has a complexity finding).
        var active = await Assert.That(findings).HasSingleItem(f => f.Rule == RuleIds.ActiveComplexFile);
        await Assert.That((active.Path, active.Value)).IsEqualTo(("Hot.cs", 5.0));
        // With a larger share Quiet.cs (no complexity finding) still does not fire; Cold.cs does.
        FindingEngine.Apply(result, RuleSettings.Resolve("default", null, ["active-complex-file.share=100"]), false);
        await Assert.That(result.Findings!.Where(f => f.Rule == RuleIds.ActiveComplexFile).Select(f => f.Path)).IsEquivalentTo(new[] { "Hot.cs", "Cold.cs" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Without_history_active_complex_file_is_skipped_with_a_note()
    {
        var result = new AnalysisResult { Files = [Analyzer.AnalyzeSource(Complex("A", 20, 1))] };
        Aggregation.Fill(result, false);
        FindingEngine.Apply(result, RuleSettings.Resolve("default", null, []), false);
        await Assert.That(result.Findings!).DoesNotContain(f => f.Rule == RuleIds.ActiveComplexFile);
        await Assert.That(result.Run.Findings!.Notes).Contains(n => n.StartsWith("active-complex-file skipped", StringComparison.Ordinal));
        await Assert.That(result.Findings!).All(f => f.Commits == null);
    }

    [Test]
    public async Task Test_files_are_left_out_of_history_hotspots_and_findings()
    {
        using var r = new SyntheticRepo();
        r.Write("src/Lib.cs", Complex("Lib", 20, 1));
        r.Write("tests/T.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"xunit\" Version=\"2.9.3\" /></ItemGroup></Project>");
        r.Write("tests/LibTests.cs", Complex("LibTests", 30, 1));
        r.Commit("c1", T0);
        r.Write("tests/LibTests.cs", Complex("LibTests", 30, 2));
        r.Commit("c2", T0.AddHours(1));
        var run = await HistoryRunner.RunAsync(r.Root, new HistoryOptions { Environment = r.Environment, AllHistory = true });
        var result = Analyzer.AnalyzeDirectory(r.Root, new AnalyzerOptions { Parallelism = 2 });
        HistoryJoin.Apply(result, run, 20, false);
        FindingEngine.Apply(result, RuleSettings.Resolve("default", null, []), false);
        await Assert.That(result.Files.Select(f => f.Path)).IsEquivalentTo(new[] { "src/Lib.cs" }, CollectionOrdering.Matching);
        await Assert.That(result.Hotspots!.Select(h => h.Path)).IsEquivalentTo(new[] { "src/Lib.cs" }, CollectionOrdering.Matching);
        foreach (var f in result.Findings!) await Assert.That(f.Path).IsEqualTo("src/Lib.cs");
        await Assert.That((result.History!.Files, result.History.Dropped.ExcludedFiles, result.History.Dropped.ExcludedRows - 1)).IsEqualTo((1, 1, 1L));

        // skarp history: the same files.
        var run2 = await HistoryRunner.RunAsync(r.Root, new HistoryOptions { Environment = r.Environment, AllHistory = true });
        var excluded = Analyzer.FindExcluded(r.Root, new AnalyzerOptions());
        await Assert.That(HistoryJoin.FilesUnder(run2, r.Root, [.. excluded.Select(e => e.Path)]).Select(f => f.Path)).IsEquivalentTo(new[] { "src/Lib.cs" }, CollectionOrdering.Matching);
        await Assert.That(run2.Summary!.Files).IsEqualTo(1);

        // --include-tests brings them back everywhere.
        var all = Analyzer.AnalyzeDirectory(r.Root, new AnalyzerOptions { Parallelism = 2, IncludeTests = true });
        HistoryJoin.Apply(all, await HistoryRunner.RunAsync(r.Root, new HistoryOptions { Environment = r.Environment, AllHistory = true }), 20, false);
        await Assert.That(all.Hotspots!.Select(h => h.Path)).IsEquivalentTo(new[] { "tests/LibTests.cs", "src/Lib.cs" }, CollectionOrdering.Matching);
    }

    static AnalysisResult Sample(int files)
    {
        var result = new AnalysisResult { Run = new RunInfo { ToolVersion = "t", Root = "/r", Runtime = "rt" } };
        for (int i = 0; i < files; i++) result.Files.Add(Analyzer.AnalyzeSource(Complex($"T{i}", 16, i), $"f{i:00}.cs"));
        Aggregation.Fill(result, false);
        FindingEngine.Apply(result, RuleSettings.Resolve("default", null, []), false);
        return result;
    }

    [Test]
    public async Task Human_report_shows_top_20_with_counts_per_rule()
    {
        var result = Sample(30); // each file: complex-method notice (16 > 15) and path-explosion notice (2^16)
        var w = new StringWriter();
        HumanReport.WriteAnalyze(w, result, new ReportOptions());
        var text = w.ToString();
        await Assert.That(text).Contains("No warnings. 60 notices at 30 places in 30 files; showing the 20 worst.");
        // One line per finding (two per member here), each file under its own heading.
        await Assert.That(text.Split('\n').Count(l => l.StartsWith("  notice line 3 T", StringComparison.Ordinal))).IsEqualTo(40);
        await Assert.That(text.Split('\n').Count(l => System.Text.RegularExpressions.Regex.IsMatch(l.TrimEnd('\r'), @"^f\d+\.cs$"))).IsEqualTo(20);
        await Assert.That(text).DoesNotContain("Findings per rule");                 // the table is in --verbose
        await Assert.That(text).DoesNotContain("violat").IgnoringCase();
        var verbose = new StringWriter();
        HumanReport.WriteAnalyze(verbose, result, new ReportOptions { Verbose = true });
        await Assert.That(verbose.ToString()).Matches(@"(?m)^  complex-method +0 +30\r?$");
        await Assert.That(verbose.ToString()).Matches(@"(?m)^  path-explosion +0 +30\r?$");
        await Assert.That(verbose.ToString()).Contains("      Much branching logic");     // each rule's sentence, once

        var all = new StringWriter();
        HumanReport.WriteAnalyze(all, result, new ReportOptions { All = true, Group = "rule", Verbose = true });
        await Assert.That(all.ToString().Split('\n').Count(l => l.StartsWith("  notice f", StringComparison.Ordinal))).IsEqualTo(60);
        await Assert.That(all.ToString()).Contains("complex-method: Much branching logic");

        var warnings = new StringWriter();
        HumanReport.WriteAnalyze(warnings, result, new ReportOptions { MinSeverity = Severity.Warning });
        await Assert.That(warnings.ToString().Split('\n').Count(l => l.StartsWith("  notice", StringComparison.Ordinal))).IsEqualTo(0);
        await Assert.That(warnings.ToString()).Contains("No warnings. 60 notices at 30 places in 30 files (warnings only).");
    }

    [Test]
    public async Task Json_has_schema_1_findings_ids_counts_and_settings()
    {
        var result = Sample(2);
        result.SchemaVersion = Analyzer.SchemaVersion;
        var json = JsonSerializer.Serialize(result, ResultJsonContext.Default.AnalysisResult);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        await Assert.That(root.GetProperty("schemaVersion").GetInt32()).IsEqualTo(1);
        var f = root.GetProperty("findings")[0];
        await Assert.That(f.GetProperty("rule").GetString()).IsEqualTo("complex-method");
        await Assert.That(f.GetProperty("severity").GetString()).IsEqualTo("notice");
        await Assert.That(f.GetProperty("path").GetString()).IsEqualTo("f00.cs");
        await Assert.That(f.GetProperty("member").GetString()).IsEqualTo("T0::M(bool)");
        await Assert.That(f.GetProperty("id").GetString()).IsEqualTo("complex-method|f00.cs|T0::M");
        await Assert.That(f.TryGetProperty("baselineState", out _)).IsFalse(); // no baseline
        await Assert.That(f.TryGetProperty("placeKey", out _)).IsFalse();
        await Assert.That(f.GetProperty("value").GetDouble()).IsEqualTo(16);
        await Assert.That(f.GetProperty("threshold").GetDouble()).IsEqualTo(15);
        await Assert.That(f.GetProperty("comparison").GetString()).IsEqualTo("over");
        await Assert.That(f.GetProperty("preset").GetString()).IsEqualTo("default");
        await Assert.That(f.GetProperty("thresholdSource").GetString()).StartsWith("Sonar S3776");
        await Assert.That(root.GetProperty("findingCounts").GetArrayLength()).IsEqualTo(15);
        await Assert.That(root.GetProperty("run").GetProperty("findings").GetProperty("complexMetric").GetString()).IsEqualTo("cognitive");
        await Assert.That(root.GetProperty("run").GetProperty("findings").GetProperty("longMethodLines").GetString()).IsEqualTo("physical");
        await Assert.That(root.GetProperty("run").GetProperty("findings").GetProperty("recordParameters").GetString()).IsEqualTo("exempt");
        await Assert.That(root.GetProperty("files")[0].GetProperty("members")[0].GetProperty("key").GetString()).IsEqualTo("T0::M");
        await Assert.That(json.Contains("testUsing", StringComparison.OrdinalIgnoreCase)).IsFalse();
    }

    [Test]
    public async Task Explain_lists_thresholds_per_preset()
    {
        var w = new StringWriter();
        HumanReport.WriteExplain(w, RuleCatalog.Get(RuleIds.LongMethod));
        var text = w.ToString();
        await Assert.That(text).Contains("long-method: Long method");
        await Assert.That(text).Contains("notice:  lines over 50  (Clean-CaDET: 50 lines, measured on 2,574 C# methods)");
        await Assert.That(text).Contains("warning: lines over 70  (CodeScene: 70 lines)");
        await Assert.That(text).Contains("--long-method-lines source");
        await Assert.That(text).Contains("doomen");
        await Assert.That(text).Contains("microsoft: no value (the default is used)");
        var list = new StringWriter();
        HumanReport.WriteRuleList(list);
        await Assert.That(list.ToString()).Contains("magic-number");
        await Assert.That(list.ToString()).Contains("alves-percentile");
    }
}