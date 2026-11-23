using System.Text;
using System.Text.Json;
using SkarpSharp.Coverage;
using SkarpSharp.Tests.Baselines;

namespace SkarpSharp.Tests.Coverage;

/// <summary>
/// The Cobertura reader on the sample reports of both producers, the path
/// recipe, and the coverage stage and untested-complex-method end to end on a small folder with a hand-written report
/// (Microsoft form: absolute paths). The mock solution with real reports is MockSolutionTests.
/// </summary>
public sealed class CoverageTests
{
    static readonly string Repo = FindRepo();
    static readonly string Samples = Path.Combine(Repo, "tests", "SkarpSharp.Tests", "Coverage", "Samples");

    static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SkarpSharp.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    /// <summary>The samples were produced from a small sample project (L\Sut.cs); the resolver needs no disk.</summary>
    static readonly CoveragePaths SamplePaths = new("C:/work/sample", ["L/Sut.cs"], ignoreCase: true);

    /// <summary>Covered and coverable lines of Sut.cs in one or more sample reports, merged per line (any covered wins).</summary>
    static (SortedSet<int> Coverable, SortedSet<int> Covered) Lines(params string[] reports)
    {
        var coverable = new SortedSet<int>();
        var covered = new SortedSet<int>();
        foreach (var name in reports)
        {
            var r = CoberturaReader.Read(Path.Combine(Samples, name));
            foreach (var (file, lines) in r.Files)
            {
                var (resolved, _) = SamplePaths.Resolve(file, r.Sources);
                if (resolved != "L/Sut.cs") throw new InvalidOperationException($"{name}: {file} did not resolve");
                foreach (var (line, hit) in lines) { coverable.Add(line); if (hit) covered.Add(line); }
            }
        }
        return (coverable, covered);
    }

    [Test]
    public async Task Both_producers_parse_to_the_same_covered_set()
    {
        // Exit condition: the samples of both producers give the same covered set, apart from
        // the documented producer difference: Microsoft reports the record's synthesized constructor on line 4.
        foreach (var run in new[] { "A", "B" })
        {
            var coverlet = Lines($"coverlet-run-{run}.cobertura.xml");
            var microsoft = Lines($"ms-run-{run}.cobertura.xml");
            microsoft.Coverable.Remove(4);
            microsoft.Covered.Remove(4);
            await Assert.That(microsoft.Coverable).IsEquivalentTo(coverlet.Coverable, CollectionOrdering.Matching);
            await Assert.That(microsoft.Covered).IsEquivalentTo(coverlet.Covered, CollectionOrdering.Matching);
        }
        // The worked example: 19 of 24 lines covered in Coverlet run A; 66 (NeverCalled) uncovered.
        var a = Lines("coverlet-run-A.cobertura.xml");
        await Assert.That((a.Covered.Count, a.Coverable.Count)).IsEqualTo((19, 24));
        await Assert.That(a.Covered).DoesNotContain(66);
    }

    [Test]
    public async Task Merging_two_runs_is_the_union_the_producers_merge_to()
    {
        var union = Lines("ms-run-A.cobertura.xml", "ms-run-B.cobertura.xml");
        var merged = Lines("dotnet-coverage-merged-A-B.cobertura.xml");
        await Assert.That(union.Covered).IsEquivalentTo(merged.Covered, CollectionOrdering.Matching);
        // Distinct lines: 24 of 25 (the report's own lines-valid, 27, counts lines repeated under lambda classes).
        await Assert.That((union.Covered.Count, union.Coverable.Count)).IsEqualTo((24, 25));
        var coverletUnion = Lines("coverlet-run-A.cobertura.xml", "coverlet-run-B.cobertura.xml");
        var reportGenerator = Lines("reportgenerator-merged-A-B.cobertura.xml");
        await Assert.That(coverletUnion.Covered).IsEquivalentTo(reportGenerator.Covered, CollectionOrdering.Matching);
        await Assert.That((coverletUnion.Covered.Count, coverletUnion.Coverable.Count)).IsEqualTo((23, 24));
    }

    [Test]
    public async Task Producers_are_told_apart()
    {
        await Assert.That(CoberturaReader.Read(Path.Combine(Samples, "coverlet-run-A.cobertura.xml")).Producer).IsEqualTo("coverlet");
        await Assert.That(CoberturaReader.Read(Path.Combine(Samples, "ms-run-A.cobertura.xml")).Producer).IsEqualTo("microsoft");
        await Assert.That(CoberturaReader.Read(Path.Combine(Samples, "reportgenerator-merged-A-B.cobertura.xml")).Producer).IsEqualTo("reportgenerator");
        var ms = CoberturaReader.Read(Path.Combine(Samples, "ms-run-A.cobertura.xml"));
        await Assert.That(ms.Timestamp).IsEqualTo(1794927229L);
    }

    static CoberturaReport Parse(string text, bool bom = false)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bom) bytes = [0xEF, 0xBB, 0xBF, .. bytes];
        return CoberturaReader.Read(new MemoryStream(bytes), "r.xml");
    }

    [Test]
    public async Task Other_formats_are_refused_with_a_clear_message()
    {
        foreach (var (text, word) in new[]
        {
            ("TN:\nSF:/src/a.cs\nDA:1,1\n", "lcov"),
            ("{ \"Module\": {} }", "JSON"),
            ("<?xml version=\"1.0\"?><CoverageSession><Modules/></CoverageSession>", "OpenCover"),
            ("\u0001\u0002binary", "binary"),
            ("", "empty"),
        })
        {
            var ex = Assert.Throws<CoverageFormatException>(() => Parse(text));
            await Assert.That(ex.Message).Contains(word);
        }
    }

    [Test]
    public async Task Hits_and_duplicates_fold_to_covered_when_any_entry_is()
    {
        var r = Parse("""
            <coverage version="1.9" timestamp="5"><packages><package name="p"><classes>
              <class name="C" filename="/x/A.cs"><methods><method name="M"><lines><line number="3" hits="0"/></lines></method></methods>
                <lines><line number="3" hits="0"/><line number="4" hits="99999999999999999999999"/><line number="5" hits="0,5"/><line number="6"/></lines></class>
              <class name="C+&lt;&gt;c" filename="/x/A.cs"><lines><line number="3" hits="1"/></lines></class>
            </classes></package></packages></coverage>
            """, bom: true);
        var lines = r.Files["/x/A.cs"];
        await Assert.That(lines[3]).IsTrue();
        await Assert.That(lines[4]).IsTrue();
        await Assert.That(lines[5]).IsTrue();
        await Assert.That(lines[6]).IsFalse();
        await Assert.That(r.Producer).IsEqualTo("microsoft");
    }

    [Test]
    public async Task Path_recipe()
    {
        var p = new CoveragePaths("C:/repo", ["src/A/Util.cs", "src/B/Util.cs", "src/A/Only.cs", "tests/T.cs"], ignoreCase: true);
        await Assert.That(p.Resolve(@"C:\repo\src\A\Util.cs", []).File).IsEqualTo("src/A/Util.cs");
        await Assert.That(p.Resolve("src/A/Util.cs", ["C:/repo/"]).Outcome).IsEqualTo(PathOutcome.Source);
        await Assert.That(p.Resolve("/_/src/B/Util.cs", []).Outcome).IsEqualTo(PathOutcome.Deterministic);
        await Assert.That(p.Resolve("/home/ci/work/src/B/Util.cs", []).File).IsEqualTo("src/B/Util.cs");     // another machine
        await Assert.That(p.Resolve("https://raw.example.com/org/repo/abc/src/A/Only.cs", []).File).IsEqualTo("src/A/Only.cs");
        await Assert.That(p.Resolve("D:/elsewhere/Util.cs", []).Outcome).IsEqualTo(PathOutcome.Ambiguous);     // never guessed
        await Assert.That(p.Resolve("C:/repo/src/A/Gone.cs", []).Outcome).IsEqualTo(PathOutcome.Unmatched);
        await Assert.That(p.Resolve("D:/other/Missing.cs", []).Outcome).IsEqualTo(PathOutcome.Outside);
    }

    // ---- the coverage stage and the rule, end to end -----------------------------------------------------------------

    static readonly string Calc = CalcText.ReplaceLineEndings("\n");

    const string CalcText = """
        namespace Shop;
        public static class Calc
        {
            public static int Rate(int a)
            {
                if (a > 1) return 1;
                if (a > 2) return 2;
                if (a > 3) return 3;
                if (a > 4) return 4;
                if (a > 5) return 5;
                return 0;
            }

            public static int Simple(int a)
            {
                return a + 1;
            }
        }
        """;

    /// <summary>A folder with Calc.cs (Rate: cyclomatic 6, lines 6 to 11; Simple: line 16) and a Microsoft-form report.</summary>
    sealed class Folder : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "skarp-cov-" + Guid.NewGuid().ToString("N")[..8]);
        public string Report => Path.Combine(Root, "reports", "coverage.cobertura.xml");

        public Folder(string? code = null, params (int Line, int Hits)[] lines)
        {
            Directory.CreateDirectory(Path.Combine(Root, "reports"));
            File.WriteAllText(Path.Combine(Root, "Calc.cs"), code ?? Calc);
            WriteReport(lines.Length > 0 ? lines : [(6, 0), (7, 0), (8, 0), (9, 0), (10, 0), (11, 0), (16, 1)]);
            // The report is newer than the code (as when tests run after the build).
            File.SetLastWriteTimeUtc(Path.Combine(Root, "Calc.cs"), DateTime.UtcNow.AddMinutes(-5));
        }

        public void WriteReport(params (int Line, int Hits)[] lines)
        {
            var sb = new StringBuilder($"<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n<coverage version=\"1.9\" complexity=\"1\" timestamp=\"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\"><packages><package name=\"Shop\"><classes>\n");
            sb.Append($"<class name=\"Shop.Calc\" filename=\"{Path.Combine(Root, "Calc.cs")}\"><lines>");
            foreach (var (l, h) in lines) sb.Append($"<line number=\"{l}\" hits=\"{h}\" />");
            sb.Append("</lines></class>\n</classes></package></packages></coverage>\n");
            File.WriteAllText(Report, sb.ToString(), new UTF8Encoding(true));
        }

        public (int Exit, string Out, string Err, JsonDocument? Json) Analyze(params string[] extra)
        {
            var r = BaselineTests.Run(["analyze", Root, "--no-save", "--no-history", "--no-semantic", "--no-config", "--format", "json", .. extra]);
            return (r.Exit, r.Out, r.Err, r.Json.Length > 0 ? JsonDocument.Parse(r.Json) : null);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static IEnumerable<JsonElement> Findings(JsonDocument d, string rule) =>
        d.RootElement.TryGetProperty("findings", out var f) ? f.EnumerateArray().Where(x => x.GetProperty("rule").GetString() == rule) : [];

    static JsonElement Member(JsonDocument d, string name) =>
        d.RootElement.GetProperty("files")[0].GetProperty("members").EnumerateArray().Single(m => m.GetProperty("name").GetString() == name);

    [Test]
    public async Task Untested_complex_member_is_a_notice_with_both_numbers()
    {
        using var f = new Folder();
        var (exit, _, err, json) = f.Analyze("--coverage", f.Report);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(err).DoesNotContain("warning");
        var finding = await Assert.That(Findings(json!, "untested-complex-method").ToList()).HasSingleItem();
        await Assert.That(finding.GetProperty("severity").GetString()).IsEqualTo("notice");
        await Assert.That(finding.GetProperty("message").GetString()).IsEqualTo("cyclomatic complexity 6, 0 of 6 coverable lines covered");
        await Assert.That(finding.GetProperty("id").GetString()).IsEqualTo("untested-complex-method|Calc.cs|Shop.Calc::Rate");
        var rate = Member(json!, "Rate").GetProperty("coverage");
        await Assert.That((rate.GetProperty("status").GetString(), rate.GetProperty("coverableLines").GetInt32(), rate.GetProperty("coveredLines").GetInt32())).IsEqualTo(("data", 6, 0));
        var file = json!.RootElement.GetProperty("files")[0].GetProperty("coverage");
        await Assert.That((file.GetProperty("coverableLines").GetInt32(), file.GetProperty("coveredLines").GetInt32())).IsEqualTo((7, 1));
        var counts = json.RootElement.GetProperty("findingCounts").EnumerateArray().Single(c => c.GetProperty("rule").GetString() == "untested-complex-method");
        await Assert.That(counts.GetProperty("notice").GetInt32()).IsEqualTo(1);
        // The threshold is a setting.
        var (_, _, _, high) = f.Analyze("--coverage", f.Report, "--threshold", "untested-complex-method.notice=7");
        await Assert.That(Findings(high!, "untested-complex-method")).IsEmpty();
        var (_, _, _, off) = f.Analyze("--coverage", f.Report, "--rule", "untested-complex-method=off");
        await Assert.That(Findings(off!, "untested-complex-method")).IsEmpty();
    }

    [Test]
    public async Task Without_a_report_nothing_changes_and_the_rule_is_not_listed()
    {
        using var f = new Folder();
        var (_, _, _, json) = f.Analyze();
        var text = json!.RootElement.GetRawText();
        await Assert.That(text).DoesNotContain("coverage");
        await Assert.That(text).DoesNotContain("untested-complex-method");
    }

    [Test]
    public async Task Score_and_default_gate_are_unchanged()
    {
        using var f = new Folder();
        BaselineTests.Run(["baseline", f.Root, "--no-history", "--no-semantic", "--no-config"]);
        var (exitOff, _, _, without) = f.Analyze("--gate");
        var (exitOn, _, _, with) = f.Analyze("--gate", "--coverage", f.Report);
        static double Score(JsonDocument d) => d.RootElement.GetProperty("files")[0].GetProperty("health").GetProperty("score").GetDouble();
        await Assert.That(Score(with!)).IsEqualTo(Score(without!));
        await Assert.That((exitOff, exitOn)).IsEqualTo((0, 0));
        await Assert.That(with!.RootElement.GetProperty("baseline").GetProperty("new").GetInt32()).IsEqualTo(1);
        // Gateable on request.
        var (gated, _, _, _) = f.Analyze("--gate", "--gate-on", "untested-complex-method:notice", "--coverage", f.Report);
        await Assert.That(gated).IsEqualTo(3);
    }

    [Test]
    public async Task Not_evaluated_without_a_report_and_the_baseline_keeps_its_entries()
    {
        using var f = new Folder();
        BaselineTests.Run(["baseline", f.Root, "--no-history", "--no-semantic", "--no-config", "--coverage", f.Report]);
        var baselineFile = Path.Combine(f.Root, ".skarp-baseline.json");
        await Assert.That(File.ReadAllText(baselineFile)).Contains("{ \"rule\": \"untested-complex-method\", \"place\": \"Shop.Calc::Rate\"");

        var (_, _, _, json) = f.Analyze();
        var b = json!.RootElement.GetProperty("baseline");
        await Assert.That((b.GetProperty("fixed").GetInt32(), b.GetProperty("notEvaluated").GetInt32())).IsEqualTo((0, 1));
        await Assert.That(b.GetProperty("notes").EnumerateArray().Select(n => n.GetString()!)).Contains(n => n.Contains("not evaluated", StringComparison.Ordinal));

        // A baseline written without a report keeps the recorded entry as it was.
        var before = File.ReadAllText(baselineFile);
        BaselineTests.Run(["baseline", f.Root, "--no-history", "--no-semantic", "--no-config"]);
        await Assert.That(File.ReadAllText(baselineFile)).IsEqualTo(before);

        // With a report that says nothing about the file: still not evaluated.
        f.WriteReport();
        var (_, _, _, noData) = f.Analyze("--coverage", f.Report);
        await Assert.That(noData!.RootElement.GetProperty("files")[0].GetProperty("coverage").GetProperty("status").GetString()).IsEqualTo("no data");
        await Assert.That(noData.RootElement.GetProperty("baseline").GetProperty("fixed").GetInt32()).IsEqualTo(0);

        // With a report that covers the member now: fixed.
        f.WriteReport((6, 1), (7, 0), (8, 0), (9, 0), (10, 0), (11, 0), (16, 1));
        var (_, _, _, tested) = f.Analyze("--coverage", f.Report);
        await Assert.That(tested!.RootElement.GetProperty("baseline").GetProperty("fixed").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task Suppression_works()
    {
        var code = Calc.Replace("    public static int Rate", "    [System.Diagnostics.CodeAnalysis.SuppressMessage(\"skarp\", \"untested-complex-method\", Justification = \"generator code\")]\n    public static int Rate", StringComparison.Ordinal);
        using var f = new Folder(code, (7, 0), (8, 0), (9, 0), (10, 0), (11, 0), (12, 0), (17, 1));
        var (_, _, _, json) = f.Analyze("--coverage", f.Report);
        await Assert.That(Findings(json!, "untested-complex-method")).IsEmpty();
        var suppressed = json!.RootElement.GetProperty("suppressed").EnumerateArray().Single();
        await Assert.That(suppressed.GetProperty("rule").GetString()).IsEqualTo("untested-complex-method");
    }

    [Test]
    public async Task Covered_line_on_a_blank_line_is_stale_and_strict_fails()
    {
        using var f = new Folder();
        f.WriteReport((6, 0), (7, 0), (8, 0), (9, 0), (10, 0), (11, 0), (13, 1), (16, 1), (40, 1));
        var (exit, _, err, json) = f.Analyze("--coverage", f.Report);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(err).Contains("skarp coverage: warning: the reports look older than the code: 1 file has covered lines on blank");
        await Assert.That(err).Contains("report lines beyond the end of the file");
        var cov = json!.RootElement.GetProperty("coverage");
        await Assert.That(cov.GetProperty("stale").GetBoolean()).IsTrue();
        await Assert.That(cov.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("kind").GetString())).Contains("stale");
        var (strict, _, strictErr, _) = f.Analyze("--coverage", f.Report, "--strict-coverage");
        await Assert.That(strict).IsEqualTo(2);
        await Assert.That(strictErr).Contains("--strict-coverage");
    }

    [Test]
    public async Task Covered_lines_in_inactive_code_warn_and_the_member_is_never_reported()
    {
        var code = Calc.Replace("        return 0;\n    }\n\n    public static int Simple", "#if FAST\n        return -1;\n#endif\n        return 0;\n    }\n\n    public static int Simple", StringComparison.Ordinal);
        using var f = new Folder(code, (6, 0), (7, 0), (8, 0), (9, 0), (10, 0), (12, 1), (14, 0), (19, 1));
        var (_, _, err, json) = f.Analyze("--coverage", f.Report);
        await Assert.That(err).Contains("1 covered line in code inactive in every analyzed variant");
        await Assert.That(Findings(json!, "untested-complex-method")).IsEmpty();
        await Assert.That(Member(json!, "Rate").GetProperty("coverage").GetProperty("inactiveRegions").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task Settings_globs_find_reports_and_an_empty_glob_warns()
    {
        using var f = new Folder();
        File.WriteAllText(Path.Combine(f.Root, ".skarp.json"), "{ \"version\": 1, \"coverage\": [\"reports/*.cobertura.xml\", \"nothing/*.xml\"] }");
        var r = BaselineTests.Run(["analyze", f.Root, "--no-save", "--no-history", "--no-semantic", "--format", "json"]);
        using var json = JsonDocument.Parse(r.Json);
        await Assert.That(Findings(json, "untested-complex-method").Count()).IsEqualTo(1);
        await Assert.That(r.Err).Contains("a coverage glob of the settings file matched no file: nothing/*.xml");
        // A missing --coverage file and a report that is not Cobertura are usage errors.
        await Assert.That(BaselineTests.Run(["analyze", f.Root, "--no-save", "--coverage", Path.Combine(f.Root, "none.xml")]).Exit).IsEqualTo(2);
        File.WriteAllText(f.Report, "TN:\nSF:Calc.cs\n");
        var lcov = BaselineTests.Run(["analyze", f.Root, "--no-save", "--no-history", "--no-semantic", "--format", "json"]);
        await Assert.That(lcov.Exit).IsEqualTo(2);
        await Assert.That(lcov.Err).Contains("an lcov report, not Cobertura");
    }

    [Test]
    public async Task Explain_and_check_help_name_the_rule()
    {
        var explain = BaselineTests.Run(["explain", "untested-complex-method"]);
        await Assert.That(explain.Exit).IsEqualTo(0);
        await Assert.That(explain.Out).Contains("Inozemtseva and Holmes");
        await Assert.That(explain.Out).Contains("CRAP itself is not computed");
        await Assert.That(SkarpSharp.Commands.CheckCommand.Usage).Contains("untested-complex-method");
    }
}
