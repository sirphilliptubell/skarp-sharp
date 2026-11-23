using SkarpSharp.Analysis;
using SkarpSharp.Findings;
using SkarpSharp.Model;

namespace SkarpSharp.Tests.Findings;

/// <summary>Finding identity v1: stable under line shifts, reordering, overload and lambda insertion.</summary>
public class IdentityTests
{
    static List<string> Keys(string code) => [.. Analyzer.AnalyzeSource(code).Members.Select(m => m.Key)];

    static List<Finding> Findings(string code, string path = "A.cs")
    {
        var result = new AnalysisResult { Files = [Analyzer.AnalyzeSource(code, path)] };
        Aggregation.Fill(result, false);
        // Low thresholds so every member with a branch or a parameter has findings.
        FindingEngine.Apply(result, RuleSettings.Resolve("default", null, ["complex-method.notice=0", "too-many-parameters.notice=0", "path-explosion.notice=0"]), false);
        return result.Findings!;
    }

    static Dictionary<string, double?> ById(List<Finding> f) => f.ToDictionary(x => x.Id, x => x.Value);

    const string Before = """
        namespace N;
        class C
        {
            int _x = 1;
            Func<int, int> F = v => v + 1;
            static C() { }
            C(int a) { }
            C(int a, int b) { }
            void M(int a) { if (a > 0) { } }
            void M(string s) { if (s == null) { } }
            int this[int i] { get { return i; } set { } }
            int this[string s] => 1;
            int P { get => _x; set { if (value > 0) _x = value; } }
            void Run(bool b)
            {
                var f = (int x) => { if (x > 1) return 1; return 0; };
                var g = (int y) => y * 2;
                int Local(int z) { if (z > 0) return z; return 0; }
            }
            public static C operator +(C l, C r) => l;
        }
        """;

    [Test]
    public async Task Keys_drop_parameters_and_number_overloads_and_nested_bodies()
    {
        await Assert.That(Keys(Before)).IsEquivalentTo(new[]
        {
            "N.C::.cctor", "N.C::.ctor", "N.C::.ctor#2", "N.C::M", "N.C::M#2",
            "N.C::this[].get", "N.C::this[].set", "N.C::this[].get#2", "N.C::P.get", "N.C::P.set",
            "N.C::Run", "N.C::Run/lambda1", "N.C::Run/lambda2", "N.C::Run/Local", "N.C::operator +",
            "N.C::F/lambda1",
        }, CollectionOrdering.Matching);
        await Assert.That(Keys("var a = () => 1; int L() => 2;")).IsEquivalentTo(["<top-level>", "<top-level>/lambda1", "<top-level>/L"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Line_shifts_do_not_change_ids()
    {
        var a = ById(Findings(Before));
        var shifted = ById(Findings("// a new header comment\n\nusing System;\n\n" + Before.Replace("void Run(bool b)", "\n\n    // moved down\n    void Run(bool b)")));
        await Assert.That(shifted).IsEquivalentTo(a, CollectionOrdering.Any);
    }

    [Test]
    public async Task Reordering_members_keeps_ids_except_between_same_named_overloads()
    {
        var a = ById(Findings(Before));
        // Move Run above the constructors: different names, same ids.
        var run = Before[Before.IndexOf("    void Run", StringComparison.Ordinal)..Before.IndexOf("    public static C operator", StringComparison.Ordinal)];
        var moved = Before.Replace(run, "").Replace("    static C() { }", run + "    static C() { }");
        await Assert.That(moved).IsNotEqualTo(Before);
        await Assert.That(ById(Findings(moved))).IsEquivalentTo(a, CollectionOrdering.Any);

        // Swapping the two M overloads swaps their ordinals (documented limit: overloads are numbered in order).
        var swapped = ById(Findings(Before.Replace("    void M(int a) { if (a > 0) { } }\n    void M(string s) { if (s == null) { } }",
            "    void M(string s) { if (s == null) { } }\n    void M(int a) { if (a > 0) { } }")));
        await Assert.That(swapped.Keys.Order()).IsEquivalentTo(a.Keys.Order(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Inserting_an_overload_after_keeps_the_first_and_others()
    {
        var a = ById(Findings(Before));
        var b = ById(Findings(Before.Replace("    void M(string s)", "    void M(double d, int e) { if (d > e) { } }\n    void M(string s)")));
        // Every old id is still there; the new overload takes #2 and the old second overload becomes #3.
        foreach (var k in a.Keys.Where(k => !k.Contains("::M#", StringComparison.Ordinal))) await Assert.That(b.Keys).Contains(k);
        await Assert.That(b.Keys).Contains("too-many-parameters|A.cs|N.C::M#3");
        await Assert.That(b["too-many-parameters|A.cs|N.C::M"]).IsEqualTo(a["too-many-parameters|A.cs|N.C::M"]);

        // Appending an overload at the end of the type changes nothing that existed.
        var c = ById(Findings(Before.Replace("    public static C operator", "    void M(char c) { if (c > 'a') { } }\n    public static C operator")));
        foreach (var kv in a) await Assert.That(c[kv.Key]).IsEqualTo(kv.Value);
        await Assert.That(c.Keys).Contains("complex-method|A.cs|N.C::M#3");
    }

    [Test]
    public async Task Inserting_a_lambda_keeps_the_parent_and_earlier_lambdas()
    {
        var a = ById(Findings(Before));
        var b = ById(Findings(Before.Replace("        var g = (int y) => y * 2;", "        var g = (int y) => y * 2;\n        var h = (bool q) => { if (q) return 1; return 2; };")));
        // Every id stays (the parent's folded cognitive complexity grows: its value changes, not its id).
        foreach (var k in a.Keys) await Assert.That(b.Keys).Contains(k);
        await Assert.That(b["path-explosion|A.cs|N.C::Run/lambda1"]).IsEqualTo(a["path-explosion|A.cs|N.C::Run/lambda1"]);
        await Assert.That(b["path-explosion|A.cs|N.C::Run/lambda2"]).IsEqualTo(a["path-explosion|A.cs|N.C::Run/lambda2"]);
        await Assert.That(b.Keys).Contains("path-explosion|A.cs|N.C::Run/lambda3");
        // Inserted before: the later lambda's ordinal shifts (documented limit); the parent and local function stay.
        var c = ById(Findings(Before.Replace("        var f = (int x)", "        var h = (bool q) => { if (q) return 1; return 2; };\n        var f = (int x)")));
        await Assert.That(c.Keys).Contains("complex-method|A.cs|N.C::Run");
        await Assert.That(c.Keys).Contains("path-explosion|A.cs|N.C::Run/Local");
        await Assert.That(c["path-explosion|A.cs|N.C::Run/lambda2"]).IsEqualTo(a["path-explosion|A.cs|N.C::Run/lambda1"]);
    }

    [Test]
    public async Task Ids_contain_no_line_numbers_or_parameter_text_and_name_type_and_file_places()
    {
        var all = Findings(Before);
        foreach (var f in all) await Assert.That(f.Id.Replace("operator +", "", StringComparison.Ordinal)).DoesNotContain("(");
        foreach (var f in all) await Assert.That(f.Id).DoesNotContain("@");
        await Assert.That(all.Select(f => f.Id).Distinct().Count()).IsEqualTo(all.Count);

        var big = Findings("record R(int A, int B, int C, int D, int E, int F, int G, int H);\nclass K(int a, int b, int c, int d, int e, int f) { }",
            "Big.cs");
        await Assert.That(big).Contains(f => f.Id == "too-many-parameters|Big.cs|K");
        var result = new AnalysisResult { Files = [Analyzer.AnalyzeSource("class Q {\n" + string.Concat(Enumerable.Repeat("    int f;\n", 30)) + "}\n", "Long.cs")] };
        Aggregation.Fill(result, false);
        FindingEngine.Apply(result, RuleSettings.Resolve("default", null, ["large-file.notice=10"]), false);
        await Assert.That((await Assert.That(result.Findings!).HasSingleItem(f => f.Rule == RuleIds.LargeFile)).Id).IsEqualTo("large-file|Long.cs|");
    }

    [Test]
    public async Task Renaming_a_member_makes_its_findings_new()
    {
        var a = ById(Findings(Before));
        var b = ById(Findings(Before.Replace("void Run(bool b)", "void Execute(bool b)")));
        await Assert.That(b.Keys).DoesNotContain("path-explosion|A.cs|N.C::Run/lambda1");
        await Assert.That(b.Keys).Contains("path-explosion|A.cs|N.C::Execute/lambda1");
        await Assert.That(b.Count).IsEqualTo(a.Count);
    }
}