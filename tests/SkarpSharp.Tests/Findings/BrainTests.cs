using System.Text;
using Microsoft.CodeAnalysis.Text;
using SkarpSharp.Agent;
using SkarpSharp.Analysis;
using SkarpSharp.Baselines;
using SkarpSharp.Configuration;
using SkarpSharp.Findings;
using SkarpSharp.Health;
using SkarpSharp.Model;
using SkarpSharp.Reporting;

namespace SkarpSharp.Tests.Findings;

/// <summary>
/// The composites brain-method and brain-class, on a worked example
/// (an Importer whose Run is long, complex and deeply nested, and whose Map is
/// long and complex only), with these exit conditions: off changes nothing; relabelling
/// an unchanged baseline is 0 new and 0 worsened; a new part makes the composite worsened; scores and the gate are equal
/// on and off; suppression works on the composite and on a part; check equals the full run.
/// </summary>
public sealed class BrainTests
{
    static readonly string[] On = ["brain-method", "brain-class"];

    /// <summary>A method of about <paramref name="lines"/> physical lines: <paramref name="depth"/> nested ifs around
    /// filler statements, plus <paramref name="branches"/> sequential ifs (each with an inner if, cognitive 3 apiece).</summary>
    public static string Method(string name, int lines, int depth, int branches, string extra = "")
    {
        var sb = new StringBuilder();
        sb.Append("    public int ").Append(name).Append("(int a, int b)\n    {\n        int x = 0;\n");
        int used = 3;
        for (int i = 0; i < branches; i++) { sb.Append($"        if (a > {i}) {{ if (b > {i}) x++; }}\n"); used++; }
        for (int d = 0; d < depth; d++) { sb.Append(new string(' ', 8 + 4 * d)).Append($"if (a > b + {d})\n").Append(new string(' ', 8 + 4 * d)).Append("{\n"); used += 2; }
        sb.Append(extra);
        int fill = Math.Max(0, lines - used - depth - 2);
        var pad = new string(' ', 8 + 4 * depth);
        for (int i = 0; i < fill; i++) sb.Append(pad).Append($"x += a * {i % 7};\n");
        for (int d = depth - 1; d >= 0; d--) sb.Append(new string(' ', 8 + 4 * d)).Append("}\n");
        sb.Append("        return x;\n    }\n");
        return sb.ToString();
    }

    /// <summary>The worked example: Run (80 lines, nested 5, complex) is a brain method; Map (60 lines, complex, nesting 2)
    /// is long and complex only; ten small methods make the member-count leg; Importer is a large type.</summary>
    public static string Importer(int runBranches = 3, int runDepth = 5, string? attributeOnRun = null, string? attributeOnType = null, string? attributeOnMap = null)
    {
        var sb = new StringBuilder("namespace Shop;\n");
        if (attributeOnType is not null) sb.Append(attributeOnType).Append('\n');
        sb.Append("public class Importer\n{\n");
        if (attributeOnRun is not null) sb.Append("    ").Append(attributeOnRun).Append('\n');
        sb.Append(Method("Run", 80, runDepth, runBranches));
        if (attributeOnMap is not null) sb.Append("    ").Append(attributeOnMap).Append('\n');
        sb.Append(Method("Map", 60, 1, 6));
        for (int i = 0; i < 10; i++) sb.Append($"    public int Small{i}(int v) => v + {i};\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    static AnalysisResult Run(string code, string[]? on = null, params string[] overrides) => RunFiles([("Importer.cs", code)], on ?? On, overrides);

    static AnalysisResult RunFiles((string Path, string Code)[] files, string[] on, params string[] overrides)
    {
        var parsed = files.Select(f => Analyzer.AnalyzeText(SourceText.From(f.Code), f.Path)).ToList();
        var result = new AnalysisResult { Files = parsed };
        Aggregation.Fill(result, false);
        FindingEngine.Apply(result, Settings(on, overrides), false);
        return result;
    }

    static RuleSettings Settings(string[] on, params string[] overrides) =>
        RuleSettings.Resolve("default", null, overrides,
            disabledRules: [RuleIds.DuplicateCodeNear, .. RuleCatalog.Composite.Select(r => r.Id).Where(id => !on.Contains(id))]);

    static List<string> Rules(IEnumerable<Finding> fs, string member) =>
        [.. fs.Where(f => f.Member is { } m && m.Contains("::" + member, StringComparison.Ordinal)).Select(f => f.Rule).Order(StringComparer.Ordinal)];

    [Test]
    public async Task Fixture_has_the_worked_example_parts()
    {
        var r = Run(Importer(), []);
        await Assert.That(Rules(r.Findings!, "Run").Intersect([RuleIds.ComplexMethod, RuleIds.DeepNesting, RuleIds.LongMethod])).IsEquivalentTo([RuleIds.ComplexMethod, RuleIds.DeepNesting, RuleIds.LongMethod], CollectionOrdering.Matching);
        await Assert.That(Rules(r.Findings!, "Map").Intersect([RuleIds.ComplexMethod, RuleIds.DeepNesting, RuleIds.LongMethod])).IsEquivalentTo([RuleIds.ComplexMethod, RuleIds.LongMethod], CollectionOrdering.Matching);
        await Assert.That(r.Findings!).HasSingleItem(f => f.Rule == RuleIds.LargeType);
        await Assert.That(r.Composites).IsNull(); // off by default: no composites block
    }

    [Test]
    public async Task Brain_method_is_on_by_default_and_brain_class_off()
    {
        // brain-method is on by default, brain-class is off.
        var o = CommandLine.Parse(["analyze", "."]).Options!;
        await Assert.That(o.RuleOn(RuleIds.BrainMethod)).IsTrue();
        await Assert.That(o.RuleOn(RuleIds.BrainClass)).IsFalse();
        var settings = o.RuleSettings();
        var parsed = new[] { Analyzer.AnalyzeText(SourceText.From(Importer()), "Importer.cs") }.ToList();
        var r = new AnalysisResult { Files = parsed };
        Aggregation.Fill(r, false);
        FindingEngine.Apply(r, settings, false);
        var bm = await Assert.That(r.Composites!).HasSingleItem();
        await Assert.That(bm.Rule).IsEqualTo(RuleIds.BrainMethod);

        // An existing baseline recorded before the change (brain-method off) gives 0 new and 0 worsened.
        var baseline = BaselineOf(Run(Importer(), []));
        var summary = Baseline.Compare(r.Findings!, baseline, "b", null);
        await Assert.That((summary.New, summary.Worsened, summary.Fixed)).IsEqualTo((0, 0, 0));
    }

    [Test]
    [Arguments("ours")]
    [Arguments("lanza-marinescu-2006")]
    public async Task Static_classes_are_never_brain_classes(string preset)
    {
        // A static class that meets every other leg is not reported (both presets).
        var big = new StringBuilder("namespace Shop;\npublic static class Big\n{\n");
        for (int i = 0; i < 4; i++) big.Append(Method($"Run{i}", 120, 5, 12).Replace("    public int ", "    public static int ", StringComparison.Ordinal));
        for (int i = 0; i < 10; i++) big.Append($"    public static int Small{i}(int v) => v + {i};\n");
        big.Append("}\n");
        var text = big.ToString();
        var r = Run(text, On, $"brain-class.preset={preset}");
        await Assert.That(r.Composites!).DoesNotContain(c => c.Rule == RuleIds.BrainClass);
        await Assert.That(r.Composites!.Count(c => c.Rule == RuleIds.BrainMethod)).IsEqualTo(4);
        // The same class without static is one.
        var plain = Run(text.Replace("static ", "", StringComparison.Ordinal), On, $"brain-class.preset={preset}");
        await Assert.That(plain.Composites!).Contains(c => c.Rule == RuleIds.BrainClass);
    }

    [Test]
    public async Task A_brain_class_names_at_most_five_brain_methods()
    {
        var sb = new StringBuilder("namespace Shop;\npublic class Big\n{\n");
        for (int i = 0; i < 7; i++) sb.Append(Method($"Run{i}", 80, 5, 3));
        for (int i = 0; i < 10; i++) sb.Append($"    public int Small{i}(int v) => v + {i};\n");
        sb.Append("}\n");
        var bc = await Assert.That(Run(sb.ToString()).Composites!).HasSingleItem(c => c.Rule == RuleIds.BrainClass);
        await Assert.That(bc.Message).EndsWith("7 brain methods: Run0, Run1, Run2, Run3, Run4 and 2 more");
        await Assert.That(bc.BrainMethods!.Count).IsEqualTo(7);   // the JSON keeps all of them
    }

    [Test]
    public async Task Brain_method_and_brain_class_of_the_worked_example()
    {
        var r = Run(Importer());
        var composites = r.Composites!;
        var bm = await Assert.That(composites).HasSingleItem(c => c.Rule == RuleIds.BrainMethod);
        await Assert.That(bm.Id).IsEqualTo("brain-method|Importer.cs|Shop.Importer::Run");
        await Assert.That(bm.Handle).IsEqualTo(FindingEngine.HandleOf(bm.Id));
        var parts = bm.Parts.Select(id => id[..id.IndexOf('|')]).ToList();
        await Assert.That(parts).IsEquivalentTo([RuleIds.LongMethod, RuleIds.ComplexMethod, RuleIds.DeepNesting], CollectionOrdering.Matching);
        await Assert.That(bm.Message).StartsWith("long (80 physical lines), complex (cognitive complexity ");
        await Assert.That(bm.Message).EndsWith("deeply nested (nesting depth 5)");
        await Assert.That(bm.Severity).IsEqualTo("warning");   // long-method over 70 is a warning: the highest part
        await Assert.That(composites).DoesNotContain(c => c.Id.Contains("::Map", StringComparison.Ordinal));

        var bc = await Assert.That(composites).HasSingleItem(c => c.Rule == RuleIds.BrainClass);
        await Assert.That(bc.Id).IsEqualTo("brain-class|Importer.cs|Shop.Importer");
        await Assert.That(bc.Parts).IsEquivalentTo([$"large-type|Importer.cs|Shop.Importer", .. bm.Parts], CollectionOrdering.Matching);
        await Assert.That(bc.BrainMethods).IsEquivalentTo([bm.Id], CollectionOrdering.Matching);
        await Assert.That(bc.Message).Contains("12 methods, 1 brain method: Run");
        await Assert.That(bc.Severity).IsEqualTo("warning");
        await Assert.That(bc.BaselineState).IsNull();
        // The part findings stay in findings, unchanged.
        await Assert.That(r.Findings!.Select(f => f.Id)).IsEquivalentTo(Run(Importer(), []).Findings!.Select(f => f.Id), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Brain_class_needs_the_member_count_and_the_book_preset_adds_size_legs()
    {
        await Assert.That(Run(Importer(), On, "brain-class.minMethods=13").Composites!).DoesNotContain(c => c.Rule == RuleIds.BrainClass);
        // Book form: one brain method needs 390 source lines and WMC 94; the Importer is far smaller.
        await Assert.That(Run(Importer(), On, "brain-class.preset=lanza-marinescu-2006").Composites!).DoesNotContain(c => c.Rule == RuleIds.BrainClass);
        var t = new TypeSummary { SourceLines = 200, SumCyclomatic = 50 };
        await Assert.That(BrainRules.BookLegs(t, 2)).IsTrue();
        await Assert.That(BrainRules.BookLegs(t, 1)).IsFalse();
        await Assert.That(BrainRules.BookLegs(new TypeSummary { SourceLines = 200, SumCyclomatic = 46 }, 2)).IsFalse();
        await Assert.That(BrainRules.BookLegs(new TypeSummary { SourceLines = 390, SumCyclomatic = 94 }, 1)).IsTrue();
        // brain-class alone still finds its brain methods, but does not report them on their own.
        var classOnly = Run(Importer(), [RuleIds.BrainClass]).Composites!;
        var only = await Assert.That(classOnly).HasSingleItem();
        await Assert.That(only.Rule).IsEqualTo(RuleIds.BrainClass);
        await Assert.That(only.BrainMethods).IsEquivalentTo(["brain-method|Importer.cs|Shop.Importer::Run"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Part_set_is_a_setting()
    {
        var r = Run(Importer(), [RuleIds.BrainMethod], "brain-method.parts=long-method,complex-method");
        await Assert.That(r.Composites!.Select(c => c.Id[(c.Id.LastIndexOf(':') + 1)..])).IsEquivalentTo(["Run", "Map"], CollectionOrdering.Matching);
        var two = Run(Importer(), [RuleIds.BrainMethod], "brain-method.minParts=2");
        await Assert.That(two.Composites!.Count).IsEqualTo(2);
        Assert.Throws<ArgumentException>(() => Settings(On, "brain-method.parts=large-type"));
        Assert.Throws<ArgumentException>(() => Settings(On, "brain-method.notice=3"));
        Assert.Throws<ArgumentException>(() => Settings(On, "brain-class.preset=book"));
    }

    static BaselineFile BaselineOf(AnalysisResult r) => Baseline.Parse(Baseline.Write(r.Findings ?? [], "test", r.Run.Findings ?? new FindingSettings()), "b");

    [Test]
    public async Task Baseline_status_follows_the_parts()
    {
        // Recorded with the rules off, compared with them on: relabelling gives 0 new and 0 worsened.
        var baseline = BaselineOf(Run(Importer(), []));
        var same = Run(Importer());
        var summary = Baseline.Compare(same.Findings!, baseline, "b", null);
        BrainRules.ApplyStates(same.Composites, same.Findings!);
        await Assert.That((summary.New, summary.Worsened)).IsEqualTo((0, 0));
        foreach (var c in same.Composites!) await Assert.That(c.BaselineState).IsEqualTo("known");

        // A part got worse (more branches: complex-method grows): the composites are worsened.
        var worse = Run(Importer(runBranches: 6));
        Baseline.Compare(worse.Findings!, baseline, "b", null);
        BrainRules.ApplyStates(worse.Composites, worse.Findings!);
        foreach (var c in worse.Composites!) await Assert.That(c.BaselineState).IsEqualTo("worsened");

        // A part was added (the baseline had no deep-nesting at Run): worsened, not new.
        var shallow = BaselineOf(Run(Importer(runDepth: 3), []));
        await Assert.That(shallow.Findings).DoesNotContain(e => e.Id.StartsWith("deep-nesting|", StringComparison.Ordinal) && e.Id.EndsWith("::Run", StringComparison.Ordinal));
        var added = Run(Importer());
        Baseline.Compare(added.Findings!, shallow, "b", null);
        BrainRules.ApplyStates(added.Composites, added.Findings!);
        await Assert.That(added.Composites!.Single(c => c.Rule == RuleIds.BrainMethod).BaselineState).IsEqualTo("worsened");

        // Every part new: new.
        var empty = Baseline.Parse(Baseline.Write([], "test", new FindingSettings()), "b");
        var fresh = Run(Importer());
        Baseline.Compare(fresh.Findings!, empty, "b", null);
        BrainRules.ApplyStates(fresh.Composites, fresh.Findings!);
        foreach (var c in fresh.Composites!) await Assert.That(c.BaselineState).IsEqualTo("new");
    }

    [Test]
    public async Task Scores_and_the_gate_are_equal_on_and_off()
    {
        var baseline = BaselineOf(Run(Importer(runDepth: 3), []));
        string Outcome(string[] on)
        {
            var r = Run(Importer(), on);
            Baseline.Compare(r.Findings!, baseline, "b", null);
            BrainRules.ApplyStates(r.Composites, r.Findings!);
            HealthEngine.Apply(r, new HealthInputs { Settings = ScoreSettings.Default, Rules = Settings(on), IncludeGenerated = false });
            var gate = Gate.Evaluate(r.Findings!, []);
            return $"{r.Files[0].Health!.Score} {r.Health!.Average} {gate.Tripped} {gate.New} {gate.Worsened} {gate.Outside}";
        }
        await Assert.That(Outcome(On)).IsEqualTo(Outcome([]));
        await Assert.That(Gate.Validate("brain-method:warning")).IsNotNull();
    }

    [Test]
    public async Task Suppressing_the_composite_hides_it_and_its_parts()
    {
        const string Why = "Justification = \"a parser: the state machine is the point\"";
        var r = Run(Importer(attributeOnRun: $"[System.Diagnostics.CodeAnalysis.SuppressMessage(\"skarp\", \"brain-method\", {Why})]"));
        await Assert.That(Rules(r.Findings!, "Run").Intersect([RuleIds.LongMethod, RuleIds.ComplexMethod, RuleIds.DeepNesting])).IsEmpty();
        await Assert.That(r.Suppressed!.Count(s => s.Id.EndsWith("::Run", StringComparison.Ordinal))).IsEqualTo(3);
        await Assert.That(r.Composites!).IsEmpty();   // the class lost its only brain method
        await Assert.That(r.Findings!).HasSingleItem(f => f.Rule == RuleIds.LargeType);

        var t = Run(Importer(attributeOnType: $"[System.Diagnostics.CodeAnalysis.SuppressMessage(\"skarp\", \"brain-class\", {Why})]"));
        await Assert.That(t.Findings!).DoesNotContain(f => f.Rule == RuleIds.LargeType);
        await Assert.That(Rules(t.Findings!, "Run").Intersect([RuleIds.LongMethod, RuleIds.ComplexMethod, RuleIds.DeepNesting])).IsEmpty();
        await Assert.That(Rules(t.Findings!, "Map").Intersect([RuleIds.ComplexMethod, RuleIds.LongMethod])).IsEquivalentTo([RuleIds.ComplexMethod, RuleIds.LongMethod], CollectionOrdering.Matching);
        await Assert.That(t.Composites!).IsEmpty();
    }

    [Test]
    public async Task Suppressing_one_part_removes_that_part_and_the_composite_only_if_it_no_longer_holds()
    {
        const string Why = "Justification = \"deliberate\"";
        var r = Run(Importer(attributeOnRun: $"[System.Diagnostics.CodeAnalysis.SuppressMessage(\"skarp\", \"deep-nesting\", {Why})]"));
        await Assert.That(Rules(r.Findings!, "Run").Intersect([RuleIds.ComplexMethod, RuleIds.DeepNesting, RuleIds.LongMethod])).IsEquivalentTo([RuleIds.ComplexMethod, RuleIds.LongMethod], CollectionOrdering.Matching);
        await Assert.That(r.Composites!).IsEmpty();
        // With two of three parts required, the composite still holds on the remaining parts.
        var two = Run(Importer(attributeOnRun: $"[System.Diagnostics.CodeAnalysis.SuppressMessage(\"skarp\", \"deep-nesting\", {Why})]"), On, "brain-method.minParts=2");
        var bm = two.Composites!.Single(c => c.Rule == RuleIds.BrainMethod && c.Id.EndsWith("::Run", StringComparison.Ordinal));
        await Assert.That(bm.Parts.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Reports_show_one_line_per_composite_and_all_lists_the_parts()
    {
        var r = Run(Importer());
        var eligible = HumanReport.Eligible(r, new ReportOptions());
        var bc = await Assert.That(eligible).HasSingleItem(f => f.Rule == RuleIds.BrainClass);
        await Assert.That(eligible).DoesNotContain(f => f.Rule == RuleIds.LargeType || f.Rule == RuleIds.BrainMethod);
        await Assert.That(Rules(eligible, "Run")).IsEmpty();
        await Assert.That(Rules(eligible, "Map")).Contains(RuleIds.LongMethod);
        await Assert.That(BrainRules.Flatten(bc).Count()).IsEqualTo(4);   // large-type and Run's three parts

        var w = new StringWriter();
        HumanReport.WriteAnalyze(w, r, new ReportOptions());
        var text = w.ToString();
        await Assert.That(text).Contains(" brain-class: large (");
        await Assert.That(text).DoesNotContain("Composites: ").And.DoesNotContain("part ");
        var verbose = new StringWriter();
        HumanReport.WriteAnalyze(verbose, r, new ReportOptions { Verbose = true });
        await Assert.That(verbose.ToString()).Contains("Composites: 1 brain method, 1 brain class (experimental)");
        var all = new StringWriter();
        HumanReport.WriteAnalyze(all, r, new ReportOptions { All = true });
        var lines = all.ToString().Split('\n');
        await Assert.That(lines).Contains(l => l.TrimStart().StartsWith("part warning line ", StringComparison.Ordinal) && l.Contains(" brain-method: ", StringComparison.Ordinal));
        await Assert.That(lines).Contains(l => l.TrimStart().StartsWith("part notice line ", StringComparison.Ordinal) && l.Contains(" deep-nesting: ", StringComparison.Ordinal));

        var md = new StringWriter();
        CiReports.WriteMarkdown(md, r, new ReportOptions());
        await Assert.That(md.ToString()).Contains("brain-class: large (");

        var agent = AgentSummary.Entries(r, Severity.Warning, null);
        await Assert.That(agent).Contains(l => l.Path == "Importer.cs" && l.Rule == RuleIds.BrainClass);
        await Assert.That(agent).DoesNotContain(l => l.Rule == RuleIds.LongMethod && l.Place == "Importer.Run");
    }

    [Test]
    public async Task Rules_off_change_no_output()
    {
        var off = Run(Importer(), []);
        var w = new StringWriter();
        HumanReport.WriteAnalyze(w, off, new ReportOptions { All = true });
        await Assert.That(w.ToString()).DoesNotContain("brain");
        await Assert.That(System.Text.Json.JsonSerializer.Serialize(off, ResultJsonContext.Default.AnalysisResult)).DoesNotContain("composites");
    }

    [Test]
    public async Task Sarif_reports_the_composite_with_its_parts_as_related_locations()
    {
        var r = Run(Importer());
        var log = SarifReport.Build(new SarifInput
        {
            ToolVersion = "0.1.0.0", SourceRoot = Path.GetTempPath(), PathPrefix = "", Settings = Settings(On),
            SyntaxFindings = r.Findings!, Composites = r.Composites,
        });
        var run = log.Runs[0];
        await Assert.That(run.Tool.Driver.Rules!).Contains(d => d.Id == RuleIds.BrainClass);
        var bc = await Assert.That(run.Results).HasSingleItem(x => x.RuleId == RuleIds.BrainClass);
        await Assert.That(bc.RelatedLocations!.Count).IsEqualTo(4);
        await Assert.That(bc.RuleIndex).IsEqualTo(run.Tool.Driver.Rules!.FindIndex(d => d.Id == RuleIds.BrainClass));
        await Assert.That(run.Results.Count).IsEqualTo(r.Findings!.Count + 2);   // the parts' results stay
        var off = SarifReport.Build(new SarifInput { ToolVersion = "0.1.0.0", SourceRoot = Path.GetTempPath(), PathPrefix = "", Settings = Settings([]), SyntaxFindings = r.Findings! });
        await Assert.That(off.Runs[0].Tool.Driver.Rules!).DoesNotContain(d => d.Id == RuleIds.BrainClass);
    }

    [Test]
    public async Task Check_equals_the_full_run_and_reports_one_line()
    {
        var root = Path.Combine(Path.GetTempPath(), "skarp-brain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var code = Importer();
            File.WriteAllText(Path.Combine(root, "Importer.cs"), code);
            File.WriteAllText(Path.Combine(root, ".skarp.json"), """{ "version": 1, "rules": { "brain-method": true, "brain-class": true } }""");
            var o = CommandLine.Parse(["analyze", root, "--no-history", "--no-semantic", "--no-config", "--rule", "brain-method=on", "--rule", "brain-class=on"]).Options!;
            var check = FileCheck.Analyze(o, o.RuleSettings(), root, "Importer.cs", code);
            var full = Run(code);
            await Assert.That(check.Composites.Select(c => $"{c.Id} {c.Severity} {c.Message} {string.Join(",", c.Parts)}")).IsEquivalentTo(full.Composites!.Select(c => $"{c.Id} {c.Severity} {c.Message} {string.Join(",", c.Parts)}"), CollectionOrdering.Matching);

            // No baseline: the warnings, with the composite in place of its parts.
            var outcome = CheckRun.RunAsync([Path.Combine(root, "Importer.cs")], new CheckSettings { Home = Path.Combine(root, ".home"), Now = DateTimeOffset.UtcNow, Save = false })
                .GetAwaiter().GetResult();
            await Assert.That(outcome.Lines).Contains(l => l.Rule == RuleIds.BrainClass && l.Kind == LineKind.Present);
            await Assert.That(outcome.Lines).DoesNotContain(l => l.Rule is RuleIds.LargeType or RuleIds.LongMethod && l.Text.Contains("Run", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task Check_against_a_baseline_new_part_is_worse_and_the_gate_judges_parts()
    {
        var root = Path.Combine(Path.GetTempPath(), "skarp-brain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, ".skarp.json"), """{ "version": 1, "rules": { "brain-method": true, "brain-class": true } }""");
            var file = Path.Combine(root, "Importer.cs");
            File.WriteAllText(file, Importer(runDepth: 3));
            var o = CommandLine.Parse(["baseline", root, "--no-history", "--no-semantic"]).Options!;
            var settings = o.RuleSettings();
            var r = Analyzer.AnalyzeDirectory(root, o.AnalyzerOptions());
            FindingEngine.Apply(r, settings, false);
            File.WriteAllText(Path.Combine(root, Baseline.FileName), Baseline.Write(r.Findings!, "test", r.Run.Findings!));

            File.WriteAllText(file, Importer());   // Run gains deep-nesting (a notice): the brain method appears
            var outcome = CheckRun.RunAsync([file], new CheckSettings { Home = Path.Combine(root, ".home"), Now = DateTimeOffset.UtcNow, Save = false, MinSeverity = Severity.Notice })
                .GetAwaiter().GetResult();
            var line = await Assert.That(outcome.Lines).HasSingleItem(l => l.Rule == RuleIds.BrainClass);
            await Assert.That(line.Kind).IsEqualTo(LineKind.Worse);
            await Assert.That(line.Render()).StartsWith("  worse line ").And.Contains(" brain-class: large (");
            await Assert.That(outcome.Lines).DoesNotContain(l => l.Rule == RuleIds.DeepNesting);
            await Assert.That(outcome.GateTripped).IsFalse();   // the new part is a notice: the default gate (warnings) does not trip
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task Explain_gives_why_and_fixes()
    {
        foreach (var id in On)
        {
            var w = new StringWriter();
            HumanReport.WriteExplain(w, RuleCatalog.Get(id));
            var text = w.ToString();
            await Assert.That(text).Contains("Why it matters:");
            await Assert.That(text).Contains("How to improve:");
            // Brain-method is on by default (explain says how to switch it off), brain-class stays off.
            await Assert.That(text).Contains(id == RuleIds.BrainMethod ? $"--rule {id}=off" : $"--rule {id}=on");
            await Assert.That(text).DoesNotContain("Thresholds per preset");
        }
    }
}