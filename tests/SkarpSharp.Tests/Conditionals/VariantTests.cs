using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using SkarpSharp.Analysis;
using SkarpSharp.Findings;
using SkarpSharp.Metrics;
using SkarpSharp.Model;
using SkarpSharp.Conditionals;
using SkarpSharp.Syntax;

namespace SkarpSharp.Tests.Conditionals;

/// <summary>
/// The SDK symbol table and classifier, the directive evaluator against
/// Roslyn's BranchTaken, the planner (worked examples A to E as oracles,
/// exclusive groups, caps and priority order, the symbols setting, the project-file filter, the fallback framework,
/// conditional #define, unreachable branches), and the merge (identity, worst values, sizes, lines, coverage kinds,
/// notices, the JSON view).
/// </summary>
public class VariantTests
{
    // ---------- helpers ----------

    static SyntaxTree Parse(string code, IEnumerable<string>? symbols = null) =>
        CSharpSyntaxTree.ParseText(code, symbols is null ? Analyzer.ParseOptions : Analyzer.ParseOptions.WithPreprocessorSymbols(symbols), "T.cs");

    static DirectiveMap Map(string code) => DirectiveMap.Collect(Parse(code).GetRoot()) ?? throw new InvalidOperationException("no #if");

    static VariantPlan Plan(string code, int cap = VariantSettings.DefaultCap, IReadOnlyList<string>? symbols = null, IReadOnlySet<string>? project = null) =>
        VariantPlanner.Plan(Map(code), new PlannerSettings { Symbols = symbols ?? [], ProjectSymbols = project }, cap);

    static FileResult Analyze(string code, IReadOnlySet<string>? project = null, IReadOnlyList<string>? symbols = null, int cap = VariantSettings.DefaultCap, bool coverage = false) =>
        Analyzer.AnalyzeText(SourceText.From(code), "T.cs", coverageLines: coverage, variants: new VariantOptions
        {
            Settings = new VariantSettings { Symbols = symbols ?? [], Cap = cap }, Cap = cap,
            ProjectSymbols = project is null ? null : new Lazy<IReadOnlySet<string>>(() => project),
        });

    static string[] Names(VariantPlan p) => [.. p.Variants.Select(v => v.Name)];

    static HashSet<string> Set(params string[] s) => new(s, StringComparer.Ordinal);

    // ---------- SDK symbols and the classifier ----------

    [Test]
    public async Task Reference_symbol_sets_match_the_expected_sets()
    {
        string[] Implied(string symbol) => [.. SdkSymbols.Implied(SymbolClassifier.Classify(symbol).Target!.Value).Order(StringComparer.Ordinal)];
        await Assert.That(Implied("NET8_0")).IsEquivalentTo(["NET", "NETCOREAPP", "NET8_0", "NET5_0_OR_GREATER", "NET6_0_OR_GREATER", "NET7_0_OR_GREATER",
            "NET8_0_OR_GREATER", "NETCOREAPP1_0_OR_GREATER", "NETCOREAPP1_1_OR_GREATER", "NETCOREAPP2_0_OR_GREATER", "NETCOREAPP2_1_OR_GREATER",
            "NETCOREAPP2_2_OR_GREATER", "NETCOREAPP3_0_OR_GREATER", "NETCOREAPP3_1_OR_GREATER"]);
        await Assert.That(Implied("NETSTANDARD2_0")).IsEquivalentTo(["NETSTANDARD", "NETSTANDARD2_0", "NETSTANDARD1_0_OR_GREATER", "NETSTANDARD1_1_OR_GREATER",
            "NETSTANDARD1_2_OR_GREATER", "NETSTANDARD1_3_OR_GREATER", "NETSTANDARD1_4_OR_GREATER", "NETSTANDARD1_5_OR_GREATER", "NETSTANDARD1_6_OR_GREATER",
            "NETSTANDARD2_0_OR_GREATER"]);
        await Assert.That(Implied("NET48")).IsEquivalentTo(["NETFRAMEWORK", "NET48", "NET20_OR_GREATER", "NET30_OR_GREATER", "NET35_OR_GREATER", "NET40_OR_GREATER",
            "NET45_OR_GREATER", "NET451_OR_GREATER", "NET452_OR_GREATER", "NET46_OR_GREATER", "NET461_OR_GREATER", "NET462_OR_GREATER", "NET47_OR_GREATER",
            "NET471_OR_GREATER", "NET472_OR_GREATER", "NET48_OR_GREATER"]);
        await Assert.That(Implied("NETCOREAPP3_1")).IsEquivalentTo(["NETCOREAPP", "NETCOREAPP3_1", "NETCOREAPP1_0_OR_GREATER", "NETCOREAPP1_1_OR_GREATER",
            "NETCOREAPP2_0_OR_GREATER", "NETCOREAPP2_1_OR_GREATER", "NETCOREAPP2_2_OR_GREATER", "NETCOREAPP3_0_OR_GREATER", "NETCOREAPP3_1_OR_GREATER"]);
    }

    [Test]
    public async Task Classifier_tells_framework_families_apart()
    {
        var net99 = SymbolClassifier.Classify("NET99_0");
        await Assert.That(net99.Target?.Moniker).IsEqualTo("net99.0");
        await Assert.That(SdkSymbols.Implied(net99.Target!.Value).Count(s => System.Text.RegularExpressions.Regex.IsMatch(s, @"^NET\d+_0_OR_GREATER$"))).IsEqualTo(95);
        await Assert.That(SymbolClassifier.Classify("NET30_OR_GREATER").Target?.Moniker).IsEqualTo("net35");
        await Assert.That(SymbolClassifier.Classify("NET48").Target?.Family).IsEqualTo(FrameworkFamily.Framework);
        await Assert.That(SymbolClassifier.Classify("NET5_0").Target?.Family).IsEqualTo(FrameworkFamily.Net);
        await Assert.That(SymbolClassifier.Classify("NET8_0_OR_GREATER").Target?.Moniker).IsEqualTo("net8.0");
        foreach (var other in new[] { "NET30", "DNXCORE50", "PORTABLE", "NET10", "NET11", "NET4_0", "HAVE_ASYNC" })
            await Assert.That(SymbolClassifier.Classify(other).Kind).IsEqualTo(SymbolCategory.Other).Because(other);
        await Assert.That(SymbolClassifier.Classify("NETFRAMEWORK").Kind).IsEqualTo(SymbolCategory.Versionless);
        await Assert.That(SymbolClassifier.Classify("DEBUG").Kind).IsEqualTo(SymbolCategory.Configuration);
        await Assert.That(SymbolClassifier.Classify("TRACE").Kind).IsEqualTo(SymbolCategory.Trace);
        await Assert.That(SymbolClassifier.Classify("WINDOWS10_0_19041_0").Os).IsEqualTo("WINDOWS");
    }

    // ---------- the evaluator against Roslyn's BranchTaken ----------

    const string Mixed = """
        #define LOCAL
        #if NET9_0_OR_GREATER
        #define BCL_INVOCATION_LIST
        #elif NET8_0_OR_GREATER
        #define UNSAFE_ACCESSOR
        #endif
        class C
        {
        #if BCL_INVOCATION_LIST
            void A() { }
        #elif UNSAFE_ACCESSOR
            void B() { }
        #else
            void Z() { }
        #endif
        #if LOCAL && (DEBUG || !RELEASE)
            void L() { }
        #endif
        #if HAVE_X
        #if NET7_0_OR_GREATER
            void N7() { }
        #else
            void Old() { }
        #endif
        #endif
        #if false
            void Never() { }
        #endif
        #if NET20 == NETFRAMEWORK
            void Eq() { }
        #endif
        }
        """;

    [Test]
    public async Task Evaluator_agrees_with_BranchTaken_on_every_planned_variant()
    {
        foreach (var code in new[] { Mixed, ExampleA, ExampleB, ExampleD, CapFile(40) })
        {
            var map = Map(code);
            var plan = VariantPlanner.Plan(map, new PlannerSettings(), 64);
            foreach (var v in plan.Variants.Select(v => v.Symbols).Prepend(new HashSet<string>()))
                await Assert.That(map.Mismatches(Parse(code, v), v)).IsEmpty();
            // And on a few hand-made sets.
            foreach (var set in new[] { Set("NET9_0_OR_GREATER"), Set("NET8_0_OR_GREATER", "DEBUG"), Set("HAVE_X", "RELEASE"), Set("NET20", "NETFRAMEWORK") })
                await Assert.That(map.Mismatches(Parse(code, set), set)).IsEmpty();
        }
    }

    [Test]
    public async Task Conditional_define_counts_only_where_its_branch_is_taken()
    {
        var map = Map(Mixed);
        var b = map.Branches.Single(x => x.Text == "#elif UNSAFE_ACCESSOR");
        await Assert.That(map.Evaluate(Set("NET8_0_OR_GREATER"))[b.Index]).IsTrue();
        await Assert.That(map.Evaluate(Set("NET9_0_OR_GREATER", "NET8_0_OR_GREATER"))[b.Index]).IsFalse();
        // The defined names are not planning symbols; the conditions they depend on are on the path.
        await Assert.That(map.Symbols).DoesNotContain("BCL_INVOCATION_LIST");
        await Assert.That(b.PathSymbols).Contains("NET8_0_OR_GREATER");
        await Assert.That(Plan(Mixed, project: Set("HAVE_X")).Unreachable.Select(u => u.Reason)).IsEquivalentTo(["never true"]);
    }

    // ---------- worked examples A to E ----------

    const string ExampleA = """
        class C
        {
        #if NET48
            void A() { }
        #elif NET8_0_OR_GREATER
            void B() { }
        #endif
        #if DEBUG
            void C() { }
        #endif
        }
        """;

    [Test]
    public async Task Example_A_plans_net8_with_DEBUG_and_net48()
    {
        // DEBUG rides on the first variant in plan order (the highest tier).
        var plan = Plan(ExampleA);
        await Assert.That(Names(plan)).IsEquivalentTo(["NET8_0+DEBUG", "NET48"], CollectionOrdering.Matching);
        var file = Analyze(ExampleA);
        await Assert.That(file.Members.Select(m => (m.Key, m.OnlyInVariants))).IsEquivalentTo(
            [("C::A", (bool?)true), ("C::B", true), ("C::C", true)], CollectionOrdering.Matching);
    }

    const string ExampleB = """
        class C
        {
        #if NET20
            void M1() { }
        #endif
        #if NET40
            void M2() { }
        #endif
        #if NET48
            void M3() { }
        #endif
        #if NETSTANDARD2_0
            void M4() { }
        #endif
        #if NETSTANDARD2_1
            void M5() { }
        #endif
        #if NETCOREAPP3_1
            void M6() { }
        #endif
        #if NET6_0_OR_GREATER
            void M7() { }
        #endif
        #if NET8_0_OR_GREATER
            void M8() { }
        #endif
        #if NET10_0_OR_GREATER
            void M9() { }
        #endif
        }
        """;

    [Test]
    public async Task Example_B_plans_four_tiers_and_three_extras()
    {
        var plan = Plan(ExampleB);
        await Assert.That(Names(plan)).IsEquivalentTo(["NET10_0", "NET20", "NET48", "NETCOREAPP3_1", "NET40", "NETSTANDARD2_0", "NETSTANDARD2_1"], CollectionOrdering.Matching);
        await Assert.That(plan.Variants.Take(4).All(v => v.Role == VariantRole.Tier)).IsTrue();
        // Check: cap 4 with the base counted in keeps the first three in priority order.
        await Assert.That(Names(Plan(ExampleB, cap: VariantSettings.DefaultCheckCap - 1))).IsEquivalentTo(["NET10_0", "NET20", "NET48"], CollectionOrdering.Matching);
        await Assert.That(Plan(ExampleB, cap: 3).Capped).IsTrue();
    }

    [Test]
    public async Task Example_C_symbols_setting_combines_frameworks_and_symbols()
    {
        const string code = """
            class C
            {
            #if NET462
                void A() { }
            #elif NET10_0
                void B() { }
            #endif
            #if DEBUG
                void D() { }
            #endif
            #if FEATURE_X
                void X() { }
            #endif
            }
            """;
        var plan = Plan(code, symbols: ["NET462", "NET10_0", "DEBUG", "TRACE"]);
        await Assert.That(Names(plan)).IsEquivalentTo(["NET10_0", "NET462", "NET10_0+DEBUG", "NET462+DEBUG"], CollectionOrdering.Matching);
        await Assert.That(plan.Variants.All(v => !v.Symbols.Contains("FEATURE_X"))).IsTrue();
        await Assert.That(plan.Variants[1].Symbols.Order(StringComparer.Ordinal)).IsEquivalentTo(
            [.. SdkSymbols.Implied(SymbolClassifier.Classify("NET462").Target!.Value).Append("TRACE").Order(StringComparer.Ordinal)], CollectionOrdering.Matching);
        // FEATURE_X is not listed, so its branch is not planned (and it is never filtered either).
        await Assert.That(plan.UnplannedBranches).IsEqualTo(1);
        await Assert.That(plan.FilteredSymbols).IsEmpty();
        // Combination count: 2 frameworks times DEBUG on or off; with a listed other symbol too, times 2 again.
        await Assert.That(Plan(code, symbols: ["NET462", "NET10_0", "DEBUG", "FEATURE_X"]).Variants.Count).IsEqualTo(8);
        await Assert.That(Plan(code, symbols: ["DEBUG", "FEATURE_X"]).Variants.Count).IsEqualTo(3);
    }

    const string ExampleD = """
        class T
        {
            void M() { }
        #if NET20
            void M(int x) { }
        #endif
        #if A
            void Twin() { int a = 1; }
        #else
            void Twin() { int b = 2; }
        #endif
            int K(int x)
            {
                if (x > 0) return 1;
        #if NET20
                if (x > 1) return 2;
                if (x > 2) return 3;
                if (x > 3) return 4;
                if (x > 4) return 5;
                if (x > 5) return 6;
                if (x > 6) return 7;
        #endif
                return 0;
            }
        }
        """;

    [Test]
    public async Task Example_D_identity_and_worst_value()
    {
        var file = Analyze(ExampleD);
        await Assert.That(file.Members.Select(m => m.Key)).IsEquivalentTo(["T::M", "T::Twin", "T::K", "T::M#2", "T::Twin#2"], CollectionOrdering.Matching);
        await Assert.That(file.Members.Select(m => m.Id)).IsEquivalentTo(["T::M()", "T::Twin()", "T::K(int)", "T::M(int)", "T::Twin()#2"], CollectionOrdering.Matching);
        var k = file.Members.Single(m => m.Key == "T::K");
        await Assert.That((k.Cyclomatic, k.BaseValues?.Cyclomatic)).IsEqualTo((8, (int?)2));
        await Assert.That(k.Variant!).StartsWith("NET20");
        // Base ids are those of the base view alone.
        var plain = Analyzer.AnalyzeText(SourceText.From(ExampleD), "T.cs");
        await Assert.That(file.Members.Take(plain.Members.Count).Select(m => m.Id)).IsEquivalentTo(plain.Members.Select(m => m.Id), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Example_E_covered_line_in_a_variant_is_code()
    {
        const string code = """
            class C
            {
                int F(int x)
                {
            #if NET20
                    x++;
            #endif
                    return x;
                }
            }
            """;
        var plain = Analyzer.AnalyzeText(SourceText.From(code), "T.cs", coverageLines: true);
        var merged = Analyze(code, coverage: true);
        await Assert.That(plain.CoverageLines!.Kinds[5]).IsEqualTo(CoverageLineKind.Inactive);
        await Assert.That(merged.CoverageLines!.Kinds[5]).IsEqualTo(CoverageLineKind.Code);
        await Assert.That(merged.CoverageLines.HasInactive(3, 9)).IsFalse();
    }

    // ---------- planning rules ----------

    [Test]
    public async Task Exclusive_groups_hold_in_every_variant()
    {
        const string code = """
            class C
            {
            #if NET20 && NET8_0
                void Two() { }
            #endif
            #if DEBUG && RELEASE
                void Both() { }
            #endif
            #if DEBUG
                void D() { }
            #endif
            #if RELEASE
                void R() { }
            #endif
            #if WINDOWS
                void W() { }
            #endif
            #if ANDROID
                void An() { }
            #endif
            #if NET48
                void F() { }
            #endif
            #if NET8_0
                void N() { }
            #endif
            }
            """;
        var plan = Plan(code);
        foreach (var v in plan.Variants)
        {
            var s = v.Symbols;
            await Assert.That(s.Contains("DEBUG") && s.Contains("RELEASE")).IsFalse().Because(v.Name);
            await Assert.That(s.Contains("WINDOWS") && s.Contains("ANDROID")).IsFalse().Because(v.Name);
            await Assert.That(s.Contains("NETFRAMEWORK") && (s.Contains("WINDOWS") || s.Contains("ANDROID"))).IsFalse().Because(v.Name);
            await Assert.That(s.Contains("NETFRAMEWORK") && s.Contains("NET")).IsFalse().Because(v.Name);
        }
        await Assert.That(plan.Unreachable.Select(u => (u.Directive, u.Reason))).IsEquivalentTo(
            [("#if NET20 && NET8_0", "exclusive"), ("#if DEBUG && RELEASE", "exclusive")]);
        var file = Analyze(code);
        await Assert.That(file.Members.Select(m => m.Name)).IsEquivalentTo(["D", "R", "W", "An", "F", "N"]);
    }

    static string CapFile(int n)
    {
        var sb = new System.Text.StringBuilder("class C\n{\n#if NET5_0\n    void M5() { }\n");
        for (int i = 6; i < 5 + n; i++) sb.Append($"#elif NET{i}_0\n    void M{i}() {{ }}\n");
        return sb.Append("#endif\n}\n").ToString();
    }

    [Test]
    public async Task A_file_over_the_cap_keeps_the_priority_order()
    {
        var code = CapFile(40);
        var full = Plan(code, cap: 100);
        await Assert.That(full.Variants.Count).IsEqualTo(40);
        var capped = Plan(code);
        await Assert.That((capped.Capped, capped.Variants.Count, capped.Planned)).IsEqualTo((true, 32, 40));
        // Tiers first (highest, lowest, then the key transitions), then the extras in plan order.
        await Assert.That(Names(capped).Take(2)).IsEquivalentTo(["NET44_0", "NET5_0"], CollectionOrdering.Matching);
        await Assert.That(Names(capped)).IsEquivalentTo(Names(full).Take(32), CollectionOrdering.Matching);
        await Assert.That(capped.UnplannedBranches).IsEqualTo(8);
        var file = Analyze(code);
        await Assert.That(file.VariantsCapped).IsEqualTo(true);
    }

    [Test]
    public async Task Versionless_symbol_takes_the_newest_of_its_family()
    {
        await Assert.That(Names(Plan("#if NETFRAMEWORK\nclass A { }\n#endif\n"))).IsEquivalentTo(["NET481"]);
        await Assert.That(Names(Plan("#if NET\nclass A { }\n#endif\n"))).IsEquivalentTo(["NET10_0"]);
        // A family variant already exists: no variant of its own.
        await Assert.That(Names(Plan("#if NETFRAMEWORK\nclass A { }\n#endif\n#if NET48\nclass B { }\n#endif\n"))).IsEquivalentTo(["NET48"]);
    }

    [Test]
    public async Task Fallback_framework_activates_a_branch_outside_the_files_frameworks()
    {
        const string code = """
            class C
            {
            #if HAVE_X
            #if NET7_0_OR_GREATER
                void New() { }
            #else
                void Old() { }
            #endif
            #endif
            }
            """;
        var plan = Plan(code, project: Set("HAVE_X"));
        await Assert.That(plan.Variants.Single(v => v.Role == VariantRole.Fallback).Name).IsEqualTo("NET6_0+HAVE_X");
        await Assert.That(plan.UnplannedBranches + plan.Unreachable.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Symbols_no_project_file_mentions_are_filtered()
    {
        const string code = """
            class C
            {
            #if MOCK_FEATURE
                void Real() { }
            #endif
            #if DEV_SWITCH
                void Dev() { }
            #endif
            #if DEBUG
                void D() { }
            #endif
            }
            """;
        var plan = Plan(code, project: Set("MOCK_FEATURE"));
        await Assert.That(plan.FilteredSymbols).IsEquivalentTo(["DEV_SWITCH"]);
        await Assert.That(plan.Variants.Any(v => v.Symbols.Contains("DEV_SWITCH"))).IsFalse();
        await Assert.That(plan.Variants.Any(v => v.Symbols.Contains("MOCK_FEATURE"))).IsTrue();
        // DEBUG is a configuration symbol: never filtered.
        await Assert.That(plan.Variants.Any(v => v.Symbols.Contains("DEBUG"))).IsTrue();
        await Assert.That((plan.UnplannedBranches, plan.Unreachable.Count)).IsEqualTo((1, 0));
        // The symbols setting forces a filtered symbol back on.
        await Assert.That(Plan(code, symbols: ["DEV_SWITCH"], project: Set("MOCK_FEATURE")).Variants.Single().Name).IsEqualTo("DEV_SWITCH");
    }

    [Test]
    public async Task Project_symbols_come_from_project_files_under_the_root()
    {
        var dir = Directory.CreateTempSubdirectory("skarp-projsym-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "a.csproj"), "<Project><PropertyGroup><DefineConstants>$(DefineConstants);HAVE_X</DefineConstants></PropertyGroup></Project>");
            Directory.CreateDirectory(Path.Combine(dir.FullName, "obj"));
            File.WriteAllText(Path.Combine(dir.FullName, "obj", "b.props"), "<Project>PRUNED_SYMBOL</Project>");
            File.WriteAllText(Path.Combine(dir.FullName, "Directory.Build.targets"), "<Project Condition=\"'$(X)' == 'FROM_TARGETS'\" />");
            var names = ProjectSymbols.For(dir.FullName);
            await Assert.That(names).Contains("HAVE_X");
            await Assert.That(names).Contains("FROM_TARGETS");
            await Assert.That(names).DoesNotContain("PRUNED_SYMBOL");
        }
        finally { dir.Delete(true); }
    }

    [Test]
    public async Task Plans_are_deterministic()
    {
        foreach (var code in new[] { Mixed, ExampleB, ExampleD })
            await Assert.That(Names(Plan(code, project: Set("HAVE_X", "A")))).IsEquivalentTo(Names(Plan(code, project: Set("A", "HAVE_X"))), CollectionOrdering.Matching);
    }

    // ---------- the merge ----------

    [Test]
    public async Task Declaration_offsets_are_identical_across_variants()
    {
        var baseBodies = MemberCollector.Collect(Parse(ExampleD), "T.cs");
        var variantBodies = MemberCollector.Collect(Parse(ExampleD, SdkSymbols.Implied(SymbolClassifier.Classify("NET20").Target!.Value)), "T.cs");
        foreach (var b in baseBodies)
            await Assert.That(variantBodies.Any(v => VariantMerge.MatchKey(v) == VariantMerge.MatchKey(b) && v.Declaration.SpanStart == b.Declaration.SpanStart)).IsTrue().Because(b.Id);
    }

    [Test]
    public async Task Attribute_under_if_before_a_method_is_no_phantom_twin()
    {
        const string code = """
            class C
            {
            #if DEBUG
                [System.Obsolete]
            #endif
                public void M(int x) { if (x > 0) x++; }
                void L() { System.Action a = () => { };
            #if DEBUG
                    System.Action b = () => { };
            #endif
                    System.Action c = () => { }; }
            }
            """;
        var file = Analyze(code);
        await Assert.That(file.Members.Count(m => m.Name == "M")).IsEqualTo(1);
        await Assert.That(file.Members.Where(m => m.OnlyInVariants == true).Select(m => m.Key)).IsEquivalentTo(["C::L/lambda3"]);
        // The base lambdas keep their keys; the variant's lambda is numbered after them.
        await Assert.That(file.Members.Where(m => m.Kind == "Lambda" && m.OnlyInVariants != true).Select(m => m.Key)).IsEquivalentTo(["C::L/lambda1", "C::L/lambda2"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Line_kinds_of_changed_branches_equal_a_full_classification()
    {
        foreach (var code in new[] { Mixed, ExampleA, ExampleB, ExampleD })
        {
            var map = Map(code);
            var baseTree = Parse(code);
            var baseKinds = LineClassifier.Classify(baseTree);
            var baseTaken = map.Evaluate(new HashSet<string>());
            foreach (var v in VariantPlanner.Plan(map, new PlannerSettings(), 64).Variants)
            {
                var tree = Parse(code, v.Symbols);
                await Assert.That(VariantMerge.ClassifyChanged(tree, baseKinds, map, baseTaken, map.Evaluate(v.Symbols)))
                    .IsEquivalentTo(LineClassifier.Classify(tree), CollectionOrdering.Matching).Because(v.Name);
            }
        }
    }

    [Test]
    public async Task Twin_types_take_the_largest_single_variant()
    {
        const string code = """
            #if NET8_0_OR_GREATER
            class Mapper
            {
                int A(int x) { if (x > 0) return 1; return 0; }
                int B(int x) { if (x > 0) return 1; return 0; }
            }
            #else
            class Mapper
            {
                int A(int x) { if (x > 0) return 1; return 0; }
            }
            #endif
            """;
        var file = Analyze(code);
        await Assert.That(file.Types.Count).IsEqualTo(1);
        await Assert.That(file.Types[0].Methods).IsEqualTo(2);
        var result = new AnalysisResult { Files = [file] };
        Aggregation.Fill(result, false);
        // WMC of the larger variant (2 x 2), never the union of both twins (6).
        await Assert.That(result.Types.Single().SumCyclomatic).IsEqualTo(4);
        await Assert.That(result.Types.Single().BaseView!.SumCyclomatic).IsEqualTo(2);
        await Assert.That(file.Lines.Source).IsLessThanOrEqualTo(file.Lines.Physical - 2);
    }

    [Test]
    public async Task A_partial_type_keeps_the_place_of_its_first_base_declaration()
    {
        // A.Async.cs sorts first but is entirely under #if: the type's place (and its large-type id) stays in B.cs.
        const string async = """
            #if HAVE_ASYNC
            partial class Reader
            {
                void ReadAsync() { }
            }
            #endif
            """;
        var a = Analyze(async, project: Set("HAVE_ASYNC"));
        var b = Analyze("partial class Reader { void Read() { } }");
        a.Path = "A.Async.cs";
        b.Path = "B.cs";
        var result = new AnalysisResult { Files = [a, b] };
        Aggregation.Fill(result, false);
        var t = result.Types.Single();
        await Assert.That((t.Path, t.Line, t.Declarations, t.Methods)).IsEqualTo(("B.cs", 1, 2, 2));
    }

    [Test]
    public async Task A_file_without_conditional_directives_is_unchanged()
    {
        const string code = """
            #region R
            class C
            {
                #pragma warning disable CS0168
                int F(int x) { if (x > 0) return 1; return 0; }
            }
            #endregion
            """;
        static string Json(FileResult f) => JsonSerializer.Serialize(new AnalysisResult { Files = [f] }, ResultJsonContext.Default.AnalysisResult);
        await Assert.That(Json(Analyze(code))).IsEqualTo(Json(Analyzer.AnalyzeText(SourceText.From(code), "T.cs")));
        await Assert.That(Analyze(code).Symbols).IsNull();
    }

    [Test]
    public async Task Syntax_error_guard_drops_unreal_mixes()
    {
        const string code = """
            class C
            {
            #if BROKEN
                void M( { }
            #endif
                void N() { }
            }
            """;
        var file = Analyze(code);
        await Assert.That(file.Variants!.Single().Discarded).IsEqualTo(true);
        await Assert.That(file.Members.Select(m => m.Name)).IsEquivalentTo(["N"]);
    }

    [Test]
    public async Task Variant_only_findings_are_notices_that_name_the_variant()
    {
        var file = Analyze(ExampleD.Replace("if (x > 6) return 7;", "if (x > 6) return 7; if (x > 7) return 8; if (x > 8) return 9; if (x > 9) return 10; if (x > 10) return 11; if (x > 11) return 12; if (x > 12) return 13; if (x > 13) return 14; if (x > 14) return 15; if (x > 15) return 16; if (x > 16) return 17; if (x > 17) return 18; if (x > 18) return 19;"));
        var result = new AnalysisResult { Files = [file] };
        Aggregation.Fill(result, false);
        FindingEngine.Apply(result, SkarpSharp.Findings.RuleSettings.Resolve("default", null, [], "physical", [], false), false);
        var complex = result.Findings!.Single(f => f.Rule == RuleIds.ComplexMethod);
        await Assert.That(complex.Severity).IsEqualTo("notice");
        await Assert.That(complex.Message).Contains("(with NET20");
        await Assert.That(complex.Variant!).StartsWith("NET20");
        await Assert.That(complex.Id).IsEqualTo("complex-method|T.cs|T::K");
    }

    [Test]
    public async Task JSON_keeps_base_values_primary_with_merged_values_alongside()
    {
        var file = Analyze(ExampleD);
        var result = new AnalysisResult { Files = [file] };
        string json = "";
        VariantView.Writing(result, () => json = JsonSerializer.Serialize(result, ResultJsonContext.Default.AnalysisResult));
        using var doc = JsonDocument.Parse(json);
        var k = doc.RootElement.GetProperty("files")[0].GetProperty("members").EnumerateArray().Single(m => m.GetProperty("key").GetString() == "T::K");
        await Assert.That(k.GetProperty("cyclomatic").GetInt32()).IsEqualTo(2);
        await Assert.That(k.GetProperty("merged").GetProperty("cyclomatic").GetInt32()).IsEqualTo(8);
        // In memory the merged values are back after writing, and a read of the JSON restores them too.
        await Assert.That(file.Members.Single(m => m.Key == "T::K").Cyclomatic).IsEqualTo(8);
        var read = JsonSerializer.Deserialize(json, ResultJsonContext.Default.AnalysisResult)!;
        VariantView.ToMerged(read);
        var r = read.Files[0].Members.Single(m => m.Key == "T::K");
        await Assert.That((r.Cyclomatic, r.BaseValues?.Cyclomatic, r.Merged)).IsEqualTo((8, (int?)2, (MemberValues?)null));
    }

    [Test]
    public async Task Check_cap_counts_the_base()
    {
        var o = VariantOptions.ForCheck(Path.GetTempPath(), VariantSettings.Default);
        await Assert.That((o.Cap, o.Parallel)).IsEqualTo((3, true));
        await Assert.That(VariantOptions.ForRun(Path.GetTempPath(), new VariantSettings { Cap = 7 }).Cap).IsEqualTo(7);
    }

    [Test]
    public async Task Variant_text_is_the_shortest_form()
    {
        await Assert.That(PlannedVariant.WithText("NET20")).IsEqualTo("with NET20 on");
        await Assert.That(PlannedVariant.WithText("NET8_0+DEBUG")).IsEqualTo("with NET8_0 and DEBUG on");
        await Assert.That(PlannedVariant.WithText("NET8_0+DEBUG+X")).IsEqualTo("with NET8_0, DEBUG and X on");
    }
}
