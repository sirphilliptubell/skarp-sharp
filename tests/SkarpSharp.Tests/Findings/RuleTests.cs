using SkarpSharp.Analysis;
using SkarpSharp.Findings;
using SkarpSharp.Model;

namespace SkarpSharp.Tests.Findings;

/// <summary>
/// Rules on worked examples (the Example_n tests), severity
/// boundaries (over versus at least, as each source states) and the presets.
/// </summary>
public class RuleTests
{
    internal static List<Finding> Run(string code, string preset = "default", string? complexMetric = null, string[]? overrides = null, string longMethodLines = "physical", bool checkRecordParameters = false)
    {
        var result = new AnalysisResult { Files = [Analyzer.AnalyzeSource(code)] };
        Aggregation.Fill(result, includeGenerated: false);
        FindingEngine.Apply(result, RuleSettings.Resolve(preset, complexMetric, overrides ?? [], longMethodLines, checkRecordParameters: checkRecordParameters), includeGenerated: false);
        return result.Findings!;
    }

    static List<Finding> Of(List<Finding> all, string rule) => [.. all.Where(f => f.Rule == rule)];

    static string Lines(int n, string indent = "        ") => string.Concat(Enumerable.Range(0, n).Select(i => $"{indent}F({i % 3});\n"));

    // ---- worked examples ----------------------------------------------------------------------------------------------

    [Test]
    public async Task Example_1_parameters_notice_not_warning_and_constructor_allowance()
    {
        var f = Of(Run("class C { void F(int a, int b, int c, int d, int e) { } C(int a, int b, int c, int d, int e) { } }"), RuleIds.TooManyParameters);
        var only = await Assert.That(f).HasSingleItem();
        await Assert.That((only.Severity, only.Member, only.Value, only.Threshold)).IsEqualTo(("notice", "C::F(int,int,int,int,int)", 5.0, 4.0));
    }

    [Test]
    public async Task Example_2_positional_record_uses_the_constructor_limit()
    {
        const string code = "record R(int A, int B, int C, int D, int E);";
        await Assert.That(Of(Run(code), RuleIds.TooManyParameters)).IsEmpty();
        // Positional records are exempt by default, even under a stricter preset ...
        await Assert.That(Of(Run(code, "doomen"), RuleIds.TooManyParameters)).IsEmpty();
        // ... and judged with the constructor limit when checked explicitly (recordParameters: check).
        var d = await Assert.That(Of(Run(code, "doomen", checkRecordParameters: true), RuleIds.TooManyParameters)).HasSingleItem();
        await Assert.That((d.Member, d.Preset, d.Threshold)).IsEqualTo(("R", "doomen", 3.0));
        // A class with a primary constructor is not a record: still judged.
        await Assert.That(Of(Run("class K(int A, int B, int C, int D, int E);", "doomen"), RuleIds.TooManyParameters)).HasSingleItem();
    }

    [Test]
    public async Task Example_3_complex_condition()
    {
        var f = await Assert.That(Of(Run("class C { void M(bool a, bool b, bool c) { if (a && b || c) { } } }"), RuleIds.ComplexCondition)).HasSingleItem();
        await Assert.That((f.Severity, f.Value)).IsEqualTo(("notice", 2.0));
        // A condition extracted into a named variable is not flagged.
        await Assert.That(Of(Run("class C { void M(bool a, bool b, bool c) { var ok = a && b || c; if (ok) { } } }"), RuleIds.ComplexCondition)).IsEmpty();
        await Assert.That(Of(Run("class C { void M(bool a, bool b) { if (a && b) { } } }"), RuleIds.ComplexCondition)).IsEmpty();
    }

    [Test]
    public async Task Example_4_empty_catch_severity()
    {
        var f = Of(Run("""
            using System;
            class C { void M() {
              try { } catch { }
              try { } catch (Exception) { /* ignored */ }
              try { } catch (OperationCanceledException) { }
              try { } catch (Exception) { }
              try { } catch (Exception e) when (e is null) { }
            } }
            """), RuleIds.EmptyCatch);
        // Findings of one place come in rule order, then line (one report entry per member).
        await Assert.That(f.Select(x => (x.Line, x.Severity))).IsEquivalentTo(new[] { (3, "warning"), (4, "notice"), (5, "notice"), (6, "warning"), (7, "notice") }, CollectionOrdering.Matching);
        // Identity: one member, several clauses: numbered in source order.
        await Assert.That(f.Select(x => x.Id)).IsEquivalentTo(new[] { "empty-catch|Test.cs|C::M", "empty-catch|Test.cs|C::M~2", "empty-catch|Test.cs|C::M~3", "empty-catch|Test.cs|C::M~4", "empty-catch|Test.cs|C::M~5" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Example_5_magic_numbers()
    {
        var f = await Assert.That(Of(Run("""
            class C { void M(int y) {
              var x = y / 2; var t = 3600; const int Max = 50;
              [Foo(7)] void L() { }
            } }
            """), RuleIds.MagicNumber)).HasSingleItem();
        await Assert.That((f.Severity, f.Value, f.Message)).IsEqualTo(("notice", 1.0, "1 magic number: 3600"));
        await Assert.That(f.Line).IsEqualTo(2);
    }

    const string Bump = "if (b) { for (int i = 0; i < n; i++) { if (i > 2) { F(i); } } }";

    [Test]
    public async Task Example_6_bumpy_road()
    {
        var two = await Assert.That(Of(Run($"class C {{ void F(int i) {{ }} void M(bool b, int n) {{ {Bump}\n F(0);\n {Bump} }} }}"), RuleIds.BumpyRoad)).HasSingleItem();
        await Assert.That((two.Severity, two.Value)).IsEqualTo(("notice", 2.0));
        await Assert.That(Of(Run($"class C {{ void F(int i) {{ }} void M(bool b, int n) {{ {Bump} }} }}"), RuleIds.BumpyRoad)).IsEmpty();
    }

    [Test]
    public async Task Bumpy_road_adjacent_blocks_form_one_chunk_and_depth_is_a_parameter()
    {
        // A bump is a maximal run of adjacent qualifying top-level statements, so
        // two qualifying blocks directly after each other are one bump (no finding), also through a try wrapper.
        var code = $"class C {{ void F(int i) {{ }} void M(bool b, int n) {{ {Bump}\n {Bump} }} }}";
        await Assert.That(Of(Run(code), RuleIds.BumpyRoad)).IsEmpty();
        await Assert.That(Of(Run($"class C {{ void F(int i) {{ }} void M(bool b, int n) {{ {Bump}\n {Bump}\n {Bump} }} }}"), RuleIds.BumpyRoad)).IsEmpty();
        await Assert.That(Of(Run($"class C {{ void F(int i) {{ }} void M(bool b, int n) {{ try {{ {Bump} }} finally {{ }}\n {Bump} }} }}"), RuleIds.BumpyRoad)).IsEmpty();
        var three = $"class C {{ void F(int i) {{ }} void M(bool b, int n) {{ {Bump}\n {Bump}\n F(1);\n {Bump}\n F(2);\n {Bump} }} }}";
        await Assert.That((await Assert.That(Of(Run(three), RuleIds.BumpyRoad)).HasSingleItem()).Value).IsEqualTo(3.0);
        var sep = $"class C {{ void F(int i) {{ }} void M(bool b, int n) {{ {Bump}\n F(1);\n {Bump} }} }}";
        await Assert.That(Of(Run(sep, overrides: ["bumpy-road.depth=4"]), RuleIds.BumpyRoad)).IsEmpty();
        await Assert.That(Of(Run(sep, overrides: ["bumpy-road.depth=3"]), RuleIds.BumpyRoad)).HasSingleItem();
    }

    [Test]
    public async Task Example_7_long_method_levels_and_lambda_lines()
    {
        static string Method(int sourceLines) => $"class C {{\n    void F(int x) {{ }}\n    void M()\n    {{\n{Lines(sourceLines - 3)}    }}\n}}\n";
        await Assert.That((await Assert.That(Of(Run(Method(55)), RuleIds.LongMethod)).HasSingleItem()).Severity).IsEqualTo("notice");
        await Assert.That((await Assert.That(Of(Run(Method(75)), RuleIds.LongMethod)).HasSingleItem()).Severity).IsEqualTo("warning");
        // 10 lines of own code plus a 200-line lambda: the parent has 210 source lines, the lambda record is not judged.
        var code = $"class C {{\n    void F(int x) {{ }}\n    void M()\n    {{\n{Lines(5)}        System.Action a = () =>\n        {{\n{Lines(197, "            ")}        }};\n{Lines(2)}    }}\n}}\n";
        var file = Analyzer.AnalyzeSource(code);
        await Assert.That(file.Members.Single(m => m.Name == "M").Lines.Source).IsEqualTo(210);
        var f = await Assert.That(Of(Run(code), RuleIds.LongMethod)).HasSingleItem();
        await Assert.That(f.Member).IsEqualTo("C::M()");
    }

    // ---- severity boundaries ------------------------------------------------------------------------------------------

    [Test]
    [Arguments(50, null)]
    [Arguments(51, "notice")]
    [Arguments(70, "notice")]
    [Arguments(71, "warning")]
    public async Task Long_method_is_over_the_threshold(int lines, string? severity)
    {
        var code = $"class C {{\n    void F(int x) {{ }}\n    void M()\n    {{\n{Lines(lines - 3)}    }}\n}}\n";
        await Assert.That(Of(Run(code), RuleIds.LongMethod).SingleOrDefault()?.Severity).IsEqualTo(severity);
        await Assert.That(Of(Run(code, longMethodLines: "source"), RuleIds.LongMethod).SingleOrDefault()?.Severity).IsEqualTo(severity);
    }

    [Test]
    public async Task Long_method_counts_physical_lines_by_default_and_source_lines_on_request()
    {
        // 40 code lines, each followed by a blank or comment-only line, plus a doc comment and the signature.
        var body = string.Concat(Enumerable.Range(0, 40).Select(i => $"        F({i % 3});\n" + (i % 2 == 0 ? "\n" : "        // why\n")));
        var code = $"class C {{\n    void F(int x) {{ }}\n    /// <summary>Doc.</summary>\n    void M()\n    {{\n{body}    }}\n}}\n";
        var m = Analyzer.AnalyzeSource(code).Members.Single(x => x.Name == "M");
        await Assert.That(m.Lines.Source).IsEqualTo(43);
        await Assert.That(m.Lines.Physical > 70).IsTrue().Because($"physical {m.Lines.Physical}");
        var f = await Assert.That(Of(Run(code), RuleIds.LongMethod)).HasSingleItem();
        await Assert.That((f.Severity, f.Metric, f.Value!.Value, f.Threshold!.Value, f.Message)).IsEqualTo(("warning", "physicalLines", (double)m.Lines.Physical, 70.0, $"{m.Lines.Physical} physical lines"));
        await Assert.That(Of(Run(code, longMethodLines: "source"), RuleIds.LongMethod)).IsEmpty();
        var s = RuleSettings.Resolve("default", null, [], "source");
        await Assert.That((s.LongMethodLines, s[RuleIds.LongMethod].Notice!.Describe())).IsEqualTo(("source", "sourceLines over 50"));
        await Assert.That(RuleSettings.Resolve("default", null, [])[RuleIds.LongMethod].Notice!.Describe()).IsEqualTo("physicalLines over 50");
        // The unit applies to every line threshold of the rule; statement thresholds stay statements.
        await Assert.That(RuleSettings.Resolve("checkstyle", null, [], "source")[RuleIds.LongMethod].Notice!.Describe()).IsEqualTo("sourceLines over 150");
        await Assert.That(RuleSettings.Resolve("doomen", null, [], "source")[RuleIds.LongMethod].Notice!.Describe()).IsEqualTo("statements over 15");
        await Assert.That(RuleSettings.Resolve("default", null, ["long-method.notice.lines=60"])[RuleIds.LongMethod].Notice!.Describe()).IsEqualTo("physicalLines over 60");
        Assert.Throws<ArgumentException>(() => RuleSettings.Resolve("default", null, [], "logical"));
    }

    [Test]
    [Arguments(4, false, null)]
    [Arguments(5, false, "notice")]
    [Arguments(7, false, "notice")]
    [Arguments(8, false, "warning")]
    [Arguments(5, true, null)]
    [Arguments(6, true, "notice")]
    [Arguments(8, true, "warning")]
    public async Task Parameters_over_4_constructor_over_5_warning_at_8(int n, bool ctor, string? severity)
    {
        var ps = string.Join(", ", Enumerable.Range(0, n).Select(i => $"int p{i}"));
        var code = ctor ? $"class C {{ C({ps}) {{ }} }}" : $"class C {{ void F({ps}) {{ }} }}";
        await Assert.That(Of(Run(code), RuleIds.TooManyParameters).SingleOrDefault()?.Severity).IsEqualTo(severity);
    }

    [Test]
    public async Task Overrides_extension_receivers_and_lambdas_are_not_judged_for_parameters()
    {
        var ps = "int a, int b, int c, int d, int e, int f";
        await Assert.That(Of(Run($"class C : B {{ public override void F({ps}) {{ }} }}"), RuleIds.TooManyParameters)).IsEmpty();
        await Assert.That(Of(Run("static class C { static void F(this string s, int a, int b, int c, int d) { } }"), RuleIds.TooManyParameters)).IsEmpty();
        await Assert.That(Of(Run($"class C {{ void M() {{ System.Action<int,int,int,int,int,int> x = ({"a, b, c, d, e, f"}) => {{ }}; }} }}"), RuleIds.TooManyParameters)).IsEmpty();
        await Assert.That(Of(Run($"class C {{ void M() {{ void L({ps}) {{ }} }} }}"), RuleIds.TooManyParameters)).HasSingleItem();
        // An indexer with get and set is reported once.
        await Assert.That(Of(Run($"class C {{ int this[{ps}] {{ get => 0; set {{ }} }} }}"), RuleIds.TooManyParameters)).HasSingleItem();
    }

    static string Ifs(int n) => $"class C {{ void M(bool b) {{ {string.Concat(Enumerable.Repeat("if (b) { } ", n))}}} }}";

    [Test]
    public async Task Path_explosion_over_200_notice_only()
    {
        // 7 sequential ifs: NPath 128; 8: 256.
        await Assert.That(Of(Run(Ifs(7)), RuleIds.PathExplosion)).IsEmpty();
        var f = await Assert.That(Of(Run(Ifs(8)), RuleIds.PathExplosion)).HasSingleItem();
        await Assert.That((f.Severity, f.Value)).IsEqualTo(("notice", 256.0));
        await Assert.That(Of(Run(Ifs(8), "pmd"), RuleIds.PathExplosion)).HasSingleItem();
    }

    [Test]
    public async Task Complex_method_cognitive_by_default_and_cyclomatic_switch()
    {
        // 16 sequential ifs: cognitive 16, cyclomatic 17.
        var cog = await Assert.That(Of(Run(Ifs(16)), RuleIds.ComplexMethod)).HasSingleItem();
        await Assert.That((cog.Metric, cog.Value, cog.Severity)).IsEqualTo(("cognitive", 16.0, "notice"));
        await Assert.That(Of(Run(Ifs(15)), RuleIds.ComplexMethod)).IsEmpty();
        var cyc = await Assert.That(Of(Run(Ifs(16), complexMetric: "cyclomatic"), RuleIds.ComplexMethod)).HasSingleItem();
        await Assert.That((cyc.Metric, cyc.Value, cyc.Severity)).IsEqualTo(("cyclomatic", 17.0, "notice"));
        await Assert.That((await Assert.That(Of(Run(Ifs(30), complexMetric: "cyclomatic"), RuleIds.ComplexMethod)).HasSingleItem()).Severity).IsEqualTo("warning"); // 31 > 30
        await Assert.That((await Assert.That(Of(Run(Ifs(29), complexMetric: "cyclomatic"), RuleIds.ComplexMethod)).HasSingleItem()).Severity).IsEqualTo("notice"); // 30
    }

    [Test]
    public async Task Folded_rules_do_not_repeat_on_nested_records()
    {
        var code = $"class C {{ void M(bool b) {{ System.Action a = () => {{ {string.Concat(Enumerable.Repeat("if (b) { if (b) { if (b) { if (b) { } } } } ", 2))} }}; }} }}";
        var all = Run(code);
        await Assert.That((await Assert.That(Of(all, RuleIds.DeepNesting)).HasSingleItem()).Member).IsEqualTo("C::M(bool)");
        await Assert.That((await Assert.That(Of(all, RuleIds.ComplexMethod)).HasSingleItem()).Member).IsEqualTo("C::M(bool)");
        // Field-initializer lambdas have no parent record and are judged themselves.
        var field = Run($"class C {{ static bool b; System.Action a = () => {{ if (b) {{ if (b) {{ if (b) {{ if (b) {{ }} }} }} }} }}; }}");
        await Assert.That(Of(field, RuleIds.DeepNesting)).HasSingleItem();
    }

    [Test]
    public async Task Deep_nesting_over_3()
    {
        await Assert.That(Of(Run("class C { void M(bool b) { if (b) { if (b) { if (b) { } } } } }"), RuleIds.DeepNesting)).IsEmpty();
        var f = await Assert.That(Of(Run("class C { void M(bool b) { if (b) { if (b) { if (b) { if (b) { } } } } } }"), RuleIds.DeepNesting)).HasSingleItem();
        await Assert.That((f.Severity, f.Value)).IsEqualTo(("notice", 4.0));
    }

    [Test]
    public async Task Large_type_merges_partials_and_skips_enums()
    {
        var body = string.Concat(Enumerable.Range(0, 60).Select(i => $"    int F{i};\n"));
        var result = new AnalysisResult
        {
            Files = [Analyzer.AnalyzeSource($"partial class P\n{{\n{body}}}\n", "a.cs"), Analyzer.AnalyzeSource($"partial class P\n{{\n{body.Replace("F", "G")}}}\nenum E {{ {string.Join(",\n", Enumerable.Range(0, 120).Select(i => $"V{i}"))} }}\n", "b.cs")],
        };
        Aggregation.Fill(result, false);
        FindingEngine.Apply(result, RuleSettings.Resolve("default", null, []), false);
        var f = await Assert.That(result.Findings!).HasSingleItem(x => x.Rule == RuleIds.LargeType);
        await Assert.That((f.Path, f.Line, f.Member, f.Value)).IsEqualTo(("a.cs", 1, "P", 126.0));
        await Assert.That(f.Message).Contains("2 partial declarations");
    }

    [Test]
    public async Task Large_file_over_1000()
    {
        static string File(int lines) => "class C {\n" + string.Concat(Enumerable.Range(0, lines - 2).Select(i => $"int f{i};\n")) + "}\n";
        await Assert.That(Of(Run(File(1000)), RuleIds.LargeFile)).IsEmpty();
        await Assert.That((await Assert.That(Of(Run(File(1001)), RuleIds.LargeFile)).HasSingleItem()).Severity).IsEqualTo("notice");
    }

    // ---- presets and overrides ----------------------------------------------------------------------------------------

    [Test]
    public async Task Composite_default_values()
    {
        var s = RuleSettings.Resolve("default", null, []);
        await Assert.That(s.ComplexMetric).IsEqualTo("cognitive");
        await Assert.That(s[RuleIds.ComplexMethod].Notice!.Describe()).IsEqualTo("cognitive over 15");
        await Assert.That(s[RuleIds.ComplexMethod].Warning).IsNull();
        await Assert.That(s[RuleIds.LargeType].Notice!.Describe()).IsEqualTo("sourceLines over 100 or wmc over 20");
        await Assert.That(s[RuleIds.LargeType].Warning).IsNull();
        foreach (var single in new[] { RuleIds.DeepNesting, RuleIds.PathExplosion, RuleIds.BumpyRoad, RuleIds.ComplexCondition, RuleIds.ActiveComplexFile, RuleIds.MagicNumber })
            await Assert.That(s[single].Warning).IsNull(); // notice only
        await Assert.That(s[RuleIds.TooManyParameters].Warning!.Describe()).IsEqualTo("parameters at least 8");
        // Every preset rule key belongs to a known rule; the composite covers every rule.
        foreach (var p in Presets.All)
            foreach (var key in p.Rules.Keys)
                await Assert.That(RuleCatalog.Find(key.Split('/')[0])).IsNotNull();
        foreach (var r in RuleCatalog.All)
            await Assert.That(r.Id == RuleIds.ComplexMethod || Presets.Default.Rules.ContainsKey(r.Id)).IsTrue().Because(r.Id);
    }

    [Test]
    public async Task Named_preset_falls_back_to_the_composite_and_says_so()
    {
        var s = RuleSettings.Resolve("doomen", null, []);
        await Assert.That(s[RuleIds.LongMethod].Preset).IsEqualTo("doomen");
        await Assert.That(s[RuleIds.LongMethod].Notice!.Describe()).IsEqualTo("statements over 15");
        await Assert.That(s[RuleIds.LargeFile].Preset).IsEqualTo("default");
        await Assert.That(s.Notes).Contains(n => n.StartsWith("large-file:", StringComparison.Ordinal));
    }

    [Test]
    [Arguments("codescene")]
    [Arguments("clean-cadet")]
    [Arguments("reportgenerator")]
    [Arguments("ndepend")]
    [Arguments("checkstyle")]
    [Arguments("microsoft")]
    [Arguments("alves-percentile")]
    [Arguments("doomen")]
    public async Task Complex_method_stays_cognitive_for_presets_without_a_cognitive_threshold(string preset)
    {
        // The default's cognitive threshold, credited to its source; cyclomatic only with the explicit switch.
        var s = RuleSettings.Resolve(preset, null, []);
        await Assert.That((s.ComplexMetric, s[RuleIds.ComplexMethod].Preset, s[RuleIds.ComplexMethod].Notice!.Describe())).IsEqualTo(("cognitive", "default", "cognitive over 15"));
        await Assert.That(s.Notes).Contains(n => n.StartsWith($"complex-method: preset {preset} has no cognitive threshold", StringComparison.Ordinal));
        var f = await Assert.That(Of(Run(Ifs(16), preset), RuleIds.ComplexMethod)).HasSingleItem();
        await Assert.That((f.Metric, f.Preset, f.Threshold!.Value)).IsEqualTo(("cognitive", "default", 15.0));
        await Assert.That(f.ThresholdSource).StartsWith("Sonar S3776");
        var cyc = RuleSettings.Resolve(preset, "cyclomatic", []);
        await Assert.That((cyc.ComplexMetric, cyc[RuleIds.ComplexMethod].Preset)).IsEqualTo(("cyclomatic", preset == "doomen" ? "default" : preset));
    }

    [Test]
    public async Task Complex_method_uses_the_preset_cognitive_value_when_it_has_one()
    {
        var s = RuleSettings.Resolve("pmd", null, []);
        await Assert.That((s[RuleIds.ComplexMethod].Preset, s[RuleIds.ComplexMethod].Notice!.Describe())).IsEqualTo(("pmd", "cognitive at least 15"));
        await Assert.That(s.Notes).DoesNotContain(n => n.StartsWith("complex-method", StringComparison.Ordinal));
        var ms = RuleSettings.Resolve("microsoft", "cyclomatic", []);
        await Assert.That((ms.ComplexMetric, ms[RuleIds.ComplexMethod].Preset, ms[RuleIds.ComplexMethod].Notice!.Describe())).IsEqualTo(("cyclomatic", "microsoft", "cyclomaticMicrosoft over 25"));
    }

    [Test]
    public async Task Threshold_overrides()
    {
        var s = RuleSettings.Resolve("default", null, ["long-method.notice=10", "long-method.warning=off", "large-type.notice.methods=5", "bumpy-road.depth=3", "deep-nesting.warning=6"]);
        await Assert.That((s[RuleIds.LongMethod].Notice!.Describe(), s[RuleIds.LongMethod].NoticePreset)).IsEqualTo(("physicalLines over 10", "custom"));
        await Assert.That(s[RuleIds.LongMethod].Warning).IsNull();
        await Assert.That(s[RuleIds.LargeType].Notice!.Describe()).IsEqualTo("sourceLines over 100 or wmc over 20 or methods over 5");
        await Assert.That(s[RuleIds.BumpyRoad].Parameters["depth"]).IsEqualTo("3");
        await Assert.That(s[RuleIds.DeepNesting].Warning!.Describe()).IsEqualTo("nesting over 6"); // a blank level borrows the notice's metric
        var f = await Assert.That(Of(Run("class C { void M(bool a, bool b) { if (a && b) { } } }", overrides: ["complex-condition.notice=1"]), RuleIds.ComplexCondition)).HasSingleItem();
        await Assert.That((f.Preset, f.ThresholdSource, f.Threshold)).IsEqualTo(("custom", "custom (threshold override)", 1.0));
        await Assert.That(Of(Run("class C { void M() { var x = 42; } }", overrides: ["magic-number.notice=off"]), RuleIds.MagicNumber)).IsEmpty();
        await Assert.That(Of(Run("class C { void M() { var x = 42; } }"), RuleIds.MagicNumber)).HasSingleItem();
    }

    [Test]
    [Arguments("long-method=5")]
    [Arguments("nope.notice=5")]
    [Arguments("long-method.notice=abc")]
    [Arguments("long-method.notice.bogus=5")]
    [Arguments("empty-catch.notice=3")]
    [Arguments("bumpy-road.depth=1")]
    [Arguments("magic-number.ignore=a,b")]
    [Arguments("long-method.size=5")]
    public void Invalid_overrides_are_rejected(string text) =>
        Assert.Throws<ArgumentException>(() => RuleSettings.Resolve("default", null, [text]));

    [Test]
    public void Unknown_preset_and_metric_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => RuleSettings.Resolve("sonar", null, []));
        Assert.Throws<ArgumentException>(() => RuleSettings.Resolve("default", "npath", []));
    }

    [Test]
    public async Task Every_rule_has_two_level_explanations()
    {
        // The first twelve rules (orders 1 to 12), then duplicate-code, duplicate-code-near, suppression-without-justification
        // (orders 20 to 22, after the semantic rules 13 to 19).
        await Assert.That(RuleCatalog.All.Count).IsEqualTo(15);
        await Assert.That(RuleCatalog.All.Select(r => r.Order)).IsEquivalentTo([.. Enumerable.Range(1, 12), 20, 21, 22], CollectionOrdering.Matching);
        foreach (var r in RuleCatalog.All)
        {
            await Assert.That(string.IsNullOrWhiteSpace(r.Short)).IsFalse().Because(r.Id);
            await Assert.That(r.Short).DoesNotContain('\n');
            await Assert.That(r.Long).Contains("Why it matters");
            await Assert.That(r.Long).Contains("How to improve");
            await Assert.That(r.Short + r.Long).DoesNotContain("violat").IgnoringCase();
        }
    }
}