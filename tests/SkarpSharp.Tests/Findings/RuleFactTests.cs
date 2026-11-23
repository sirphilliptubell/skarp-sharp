using SkarpSharp.Analysis;
using SkarpSharp.Model;

namespace SkarpSharp.Tests.Findings;

/// <summary>Syntax facts for the finding rules, computed in the per-file pass.</summary>
public class RuleFactTests
{
    // Returns a value, so it checks synchronously (Assert.Fail throws).
    static MemberResult Member(string code, string name)
    {
        var file = Analyzer.AnalyzeSource(code);
        var matches = file.Members.Where(m => m.Name == name).ToList();
        if (matches.Count != 1) Assert.Fail($"member {name}: {string.Join(", ", file.Members.Select(m => m.Name))}");
        return matches[0];
    }

    [Test]
    [Arguments("class C { void F(int a, int b, int c, int d, int e) { } }", "F", 5)]
    [Arguments("class C { C(int a, params int[] b) { } }", ".ctor", 2)]
    [Arguments("static class C { static int F(this string s, int a) => a; }", "F", 1)]
    [Arguments("class C { int this[int a, int b] { get => a; set { } } }", "this[int,int].get", 2)]
    [Arguments("class C { int this[int a, int b] => a; }", "this[int,int].get", 2)]
    [Arguments("class C { void M() { int L(int x, int y = 3) => x; } }", "M/L", 2)]
    [Arguments("class C { void M() { System.Func<int,int,int> f = (a, b) => a; } }", "M/lambda1", 2)]
    [Arguments("class C { public static C operator +(C a, C b) => a; }", "operator +", 2)]
    public async Task Parameter_count(string code, string member, int expected) =>
        await Assert.That(Member(code, member).Parameters).IsEqualTo(expected);

    [Test]
    public async Task Properties_have_no_parameter_count_and_overrides_are_marked()
    {
        await Assert.That(Member("class C { int P { get; set { } } }", "P.set").Parameters).IsNull();
        await Assert.That(Member("class C : B { public override void F(int a) { } }", "F").InheritedSignature).IsTrue();
        await Assert.That(Member("class C : I { void I.F(int a) { } }", "F").InheritedSignature).IsTrue();
        await Assert.That(Member("class C { void F(int a) { } }", "F").InheritedSignature).IsNull();
    }

    [Test]
    public async Task Primary_constructor_parameters_are_on_the_type()
    {
        var file = Analyzer.AnalyzeSource("record R(int A, int B, int C, int D, int E);\nclass K(int a) { }\nclass P { }");
        await Assert.That(file.Types.Single(t => t.Name == "R").PrimaryConstructorParameters).IsEqualTo(5);
        await Assert.That(file.Types.Single(t => t.Name == "K").PrimaryConstructorParameters).IsEqualTo(1);
        await Assert.That(file.Types.Single(t => t.Name == "P").PrimaryConstructorParameters).IsNull();
    }

    [Test]
    [Arguments("if (a && b || c) { }", 2)]
    [Arguments("if (a && b) { }", 1)]
    [Arguments("if (a) { }", null)]
    [Arguments("if (!(a && b) || (c && !d)) { }", 3)]
    [Arguments("if (o is 1 or 2 && a) { }", 2)]
    [Arguments("if (o is not null and not 3) { }", 1)]
    [Arguments("switch (o) { case 1 or 2 or 3: break; }", null)]
    [Arguments("F(a && b); if (c || d || a) { }", 2)]
    [Arguments("System.Func<bool> f = () => a && b && c && d; if (a && b) { }", 1)]
    public async Task Boolean_operators_per_condition(string body, int? expected)
    {
        var m = Member($"class C {{ bool a, b, c, d; object o; void F(bool x) {{ }} void M() {{ {body} }} }}", "M");
        await Assert.That(m.BooleanOperatorsMax).IsEqualTo(expected);
    }

    // Only branch conditions count for complex-condition.
    [Test]
    [Arguments("if (a && b && c) { }")]
    [Arguments("while (a && b && c) { }")]
    [Arguments("do { } while (a && b && c);")]
    [Arguments("for (int i = 0; a && b && c; i++) { }")]
    [Arguments("var x = a && b && c ? 1 : 0;")]
    [Arguments("switch (o) { case int i when a && b && c: break; }")]
    [Arguments("var x = o switch { int i when a && b && c => 1, _ => 0 };")]
    [Arguments("try { } catch (System.Exception) when (a && b && c) { F(a); }")]
    public async Task Branch_conditions_are_counted(string body) =>
        await Assert.That(Member($"class C {{ bool a, b, c; object o; void F(bool x) {{ }} void M() {{ {body} }} }}", "M").BooleanOperatorsMax).IsEqualTo(2);

    [Test]
    [Arguments("var x = a && b && c;")]
    [Arguments("bool x; x = a && b && c;")]
    [Arguments("F(a && b && c);")]
    [Arguments("System.Func<bool> f = () => a && b && c;")]
    [Arguments("var x = a ? b && c && a : false;")]
    [Arguments("if (F2(a && b && c)) { }")]
    public async Task Other_boolean_expressions_are_not_counted(string body) =>
        await Assert.That(Member($"class C {{ bool a, b, c; void F(bool x) {{ }} bool F2(bool x) => x; void M() {{ {body} }} }}", "M").BooleanOperatorsMax).IsNull();

    [Test]
    [Arguments("bool M() => a && b && c;")]
    [Arguments("bool M() { return a && b && c; }")]
    public async Task Returned_boolean_expressions_are_not_counted(string member) =>
        await Assert.That(Member($"class C {{ bool a, b, c; {member} }}", "M").BooleanOperatorsMax).IsNull();

    [Test]
    public async Task Empty_catch_facts()
    {
        var m = Member("""
            class C { void M() {
              try { } catch { }
              try { } catch (System.Exception) { /* ignored */ }
              try { } catch (OperationCanceledException e) when (e != null) {
                // cancelled
              }
              try { } catch (Exception) { Log(); }
            } void Log() { } }
            """, "M");
        await Assert.That(m.EmptyCatches!.Count).IsEqualTo(3);
        await Assert.That((m.EmptyCatches[0].Caught, m.EmptyCatches[0].HasComment, m.EmptyCatches[0].HasFilter)).IsEqualTo((null, false, false));
        await Assert.That((m.EmptyCatches[1].Caught, m.EmptyCatches[1].HasComment)).IsEqualTo(("System.Exception", true));
        await Assert.That((m.EmptyCatches[2].Caught, m.EmptyCatches[2].HasComment, m.EmptyCatches[2].HasFilter)).IsEqualTo(("OperationCanceledException", true, true));
        await Assert.That(m.EmptyCatches.Select(c => c.Line)).IsEquivalentTo(new[] { 2, 3, 4 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Numeric_literals_skip_exempt_positions()
    {
        // The worked example (2 and 3600 are flagged) plus the other exempt positions.
        var m = Member("""
            class C { int[] arr = new int[9]; void M(int y) {
              var x = y / 2; var t = 3600; const int Max = 50;
              var ms = y * 1000; var z = -7; var h = 0x1F;
              var e = arr[4];
              [System.Obsolete("x", false)] int L(int q = 42) => q;
              System.Func<int> f = () => 99;
            } }
            """, "M");
        await Assert.That(m.NumericLiterals!.Select(l => l.Text)).IsEquivalentTo(new[] { "2", "3600", "-7", "0x1F" }, CollectionOrdering.Matching);
        await Assert.That(m.NumericLiterals!.Select(l => l.Value)).IsEquivalentTo(new[] { 2.0, 3600, -7, 31 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Array_sizes_are_exempt_but_initializer_values_are_not()
    {
        // Array sizes in array creation are exempt; the power-of-ten multiply and divide exemption stays.
        var m = Member("""
            class C { unsafe void M(int n) {
              var a = new byte[4096]; var b = new int[16, 32]; var j = new int[64][];
              System.Span<char> s = stackalloc char[256];
              var c = new int[3] { 5, 6, 7 };
              var d = n * 1000; var e = n / 100; var f = n * 1024; var g = n + 1000;
            } }
            """, "M");
        await Assert.That(m.NumericLiterals!.Select(l => l.Text)).IsEquivalentTo(new[] { "5", "6", "7", "1024", "1000" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Nested_blocks_for_bumpy_road()
    {
        var m = Member("""
            class C { void M(int[] a, bool b) {
              if (b) { foreach (var x in a) { if (x > 0) { } } }
              Log();
              try {
                while (b) { if (b) { } }
                if (b) { }
              } finally { }
              for (;;) { }
              switch (a.Length) { case 1: if (b) { } break; }
            } void Log() { } }
            """, "M");
        var blocks = m.NestedBlocks!;
        await Assert.That(blocks.Select(b => (b.Seq, b.Depth))).IsEquivalentTo(new[] { (0, 3), (2, 2), (5, 2) }, CollectionOrdering.Matching);
        await Assert.That((blocks[0].StartLine, blocks[0].EndLine)).IsEqualTo((2, 2));
    }

    [Test]
    public async Task Lambda_and_local_function_bodies_belong_to_their_own_records()
    {
        var code = """
            class C { void M(bool a, bool b, bool c) {
              System.Action f = () => { try { } catch { } if (a && b || c) { } var n = 77; };
              void L() { if (a || b || c) { } }
            } }
            """;
        var m = Member(code, "M");
        await Assert.That(m.EmptyCatches).IsNull();
        await Assert.That(m.BooleanOperatorsMax).IsNull();
        await Assert.That(m.NumericLiterals).IsNull();
        var lambda = Member(code, "M/lambda1");
        await Assert.That(lambda.EmptyCatches!).HasSingleItem();
        await Assert.That(lambda.BooleanOperatorsMax).IsEqualTo(2);
        await Assert.That((await Assert.That(lambda.NumericLiterals!).HasSingleItem()).Text).IsEqualTo("77");
        await Assert.That(Member(code, "M/L").BooleanOperatorsMax).IsEqualTo(2);
    }

    [Test]
    public async Task Type_span_and_source_lines()
    {
        var file = Analyzer.AnalyzeSource("namespace N;\n// c\n[System.Obsolete]\nclass A\n{\n\n    int x;\n    class B { }\n}\n");
        var a = file.Types.Single(t => t.Name == "N.A");
        await Assert.That((a.StartLine, a.EndLine, a.SourceLines)).IsEqualTo((3, 9, 6));
    }
}