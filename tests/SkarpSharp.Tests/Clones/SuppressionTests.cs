using Microsoft.CodeAnalysis.Text;
using SkarpSharp.Agent;
using SkarpSharp.Analysis;
using SkarpSharp.Clones;
using SkarpSharp.Findings;
using SkarpSharp.Model;

namespace SkarpSharp.Tests.Clones;

/// <summary>
/// Inline suppression with [SuppressMessage("skarp", "&lt;rule&gt;", Justification = ...)]
/// for every rule: member and type scope, assembly-level targets, the justification requirement, clone groups, the
/// suppressed list, and skarp check.
/// </summary>
public sealed class SuppressionTests
{
    /// <summary>Every member with a branch is a complex-method notice, every member a long-method notice.</summary>
    static readonly string[] Low = ["complex-method.notice=0", "long-method.notice=0"];

    static AnalysisResult Run(params (string Path, string Code)[] files) =>
        CloneHost.Run(files.Select(f => new CloneHost.Source(f.Path, f.Code)), Low);

    static List<Finding> Of(AnalysisResult r, string rule) => [.. (r.Findings ?? []).Where(f => f.Rule == rule)];

    const string Two = """
        using System.Diagnostics.CodeAnalysis;
        namespace N;
        class C
        {
            [SuppressMessage("skarp", "complex-method", Justification = "a parser: branching is the point")]
            int Kept(int x) { if (x > 0) return 1; return 0; }

            int Flagged(int x) { if (x > 0) return 1; return 0; }
        }
        """;

    [Test]
    public async Task A_member_attribute_suppresses_that_members_findings_of_the_rule()
    {
        var r = Run(("C.cs", Two));
        var complex = Of(r, RuleIds.ComplexMethod);
        await Assert.That(complex.Select(f => f.Member![(f.Member!.LastIndexOf(':') + 1)..].Split('(')[0])).IsEquivalentTo(["Flagged"], CollectionOrdering.Matching);
        await Assert.That(Of(r, RuleIds.LongMethod).Count).IsEqualTo(2); // other rules are not suppressed
        var s = await Assert.That(r.Suppressed!).HasSingleItem();
        await Assert.That((s.Rule, s.Path, s.Justification, s.SuppressedBy)).IsEqualTo((RuleIds.ComplexMethod, "C.cs", "a parser: branching is the point", "C.cs:5"));
        await Assert.That(s.Id).StartsWith("complex-method|C.cs|");
        await Assert.That(r.FindingCounts!.Single(c => c.Rule == RuleIds.ComplexMethod).Notice).IsEqualTo(1);
    }

    [Test]
    public async Task A_type_attribute_suppresses_the_type_and_its_members()
    {
        var code = """
            namespace N;
            [System.Diagnostics.CodeAnalysis.SuppressMessage("skarp", "complex-method", Justification = "generated-like dispatch")]
            class C
            {
                int A(int x) { if (x > 0) return 1; return 0; }
                class Inner { int B(int x) { if (x > 0) return 1; return 0; } }
            }
            class D { int E(int x) { if (x > 0) return 1; return 0; } }
            """;
        var r = Run(("C.cs", code));
        await Assert.That(Of(r, RuleIds.ComplexMethod)).HasSingleItem();
        await Assert.That(r.Suppressed!.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments("[SuppressMessage(\"skarp\", \"complex-method\")]")]
    [Arguments("[SuppressMessage(\"skarp\", \"complex-method\", Justification = \"\")]")]
    [Arguments("[SuppressMessage(\"skarp\", \"complex-method\", Justification = \"   \")]")]
    public async Task A_suppression_without_justification_does_not_count_and_is_a_notice(string attribute)
    {
        var r = Run(("C.cs", Two.Replace("[SuppressMessage(\"skarp\", \"complex-method\", Justification = \"a parser: branching is the point\")]", attribute, StringComparison.Ordinal)));
        await Assert.That(Of(r, RuleIds.ComplexMethod).Count).IsEqualTo(2);
        var n = await Assert.That(Of(r, RuleIds.SuppressionWithoutJustification)).HasSingleItem();
        await Assert.That((n.Severity, n.Path, n.Line)).IsEqualTo(("notice", "C.cs", 5));
        await Assert.That(n.Message).Contains("complex-method");
        await Assert.That(n.Member).Contains("C.Kept");
        await Assert.That(r.Suppressed).IsNull();
    }

    [Test]
    [Arguments("SuppressMessage(\"skarp.design\", \"complex-method\", Justification = \"j\")", true)]
    [Arguments("SuppressMessageAttribute(\"skarp\", \"complex-method:Some title\", Justification = \"j\")", true)]
    [Arguments("UnconditionalSuppressMessage(\"skarp\", \"complex-method\", Justification = \"j\")", true)]
    [Arguments("global::System.Diagnostics.CodeAnalysis.SuppressMessage(\"skarp\", \"complex-method\", Justification = \"j\")", true)]
    [Arguments("SuppressMessage(category: \"skarp\", checkId: \"complex-method\", Justification = \"j\")", true)]
    [Arguments("SuppressMessage(\"Microsoft.Design\", \"complex-method\", Justification = \"j\")", false)]
    [Arguments("SuppressMessage(\"skarpish\", \"complex-method\", Justification = \"j\")", false)]
    [Arguments("SuppressMessage(\"skarp\", \"long-method\", Justification = \"j\")", false)]
    [Arguments("My.Own.SuppressMessage(\"skarp\", \"complex-method\", Justification = \"j\")", false)]
    public async Task Attribute_forms(string attribute, bool suppresses)
    {
        var code = $"namespace N;\nclass C\n{{\n    [{attribute}]\n    int A(int x) {{ if (x > 0) return 1; return 0; }}\n}}\n";
        await Assert.That(Of(Run(("C.cs", code)), RuleIds.ComplexMethod).Count).IsEqualTo(suppresses ? 0 : 1);
    }

    [Test]
    [Arguments("~M:N.C.A(System.Int32)", null, 1)]
    [Arguments("N.C.A", null, 1)]
    [Arguments("~T:N.C", null, 2)]
    [Arguments("N", "namespaceanddescendants", 2)]
    [Arguments("~M:N.C.Missing", null, 0)]
    public async Task Assembly_level_attributes_with_a_target(string target, string? scope, int suppressed)
    {
        var global = $"[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage(\"skarp\", \"complex-method\", Justification = \"j\", Target = \"{target}\"{(scope is null ? "" : $", Scope = \"{scope}\"")})]\n";
        var code = "namespace N;\nclass C\n{\n    int A(int x) { if (x > 0) return 1; return 0; }\n    int B(int x) { if (x > 0) return 1; return 0; }\n}\n";
        var r = Run(("GlobalSuppressions.cs", global), ("C.cs", code));
        await Assert.That(Of(r, RuleIds.ComplexMethod).Count).IsEqualTo(2 - suppressed);
        foreach (var s in r.Suppressed ?? []) await Assert.That(s.SuppressedBy).IsEqualTo("GlobalSuppressions.cs:1");
    }

    static string Suppressed(string code, string method) => code.Replace(
        $"    public int {method}(", $"    [System.Diagnostics.CodeAnalysis.SuppressMessage(\"skarp\", \"duplicate-code\", Justification = \"kept apart on purpose\")]\n    public int {method}(", StringComparison.Ordinal);

    [Test]
    public async Task A_clone_group_is_suppressed_when_every_copy_is_and_shrinks_when_some_are()
    {
        var code = CloneHost.Method("A", "Run", 12);
        string Copy(string type) => code.Replace("class A", "class " + type, StringComparison.Ordinal);

        // Both copies suppressed: the group is listed as suppressed, with the justification.
        var both = CloneHost.Run([new("A.cs", Suppressed(code, "Run")), new("B.cs", Suppressed(Copy("B"), "Run"))]);
        await Assert.That(CloneHost.Clones(both)).IsEmpty();
        var s = await Assert.That(both.Suppressed!).HasSingleItem();
        await Assert.That((s.Rule, s.Justification)).IsEqualTo((RuleIds.DuplicateCode, "kept apart on purpose"));
        await Assert.That(s.Id).StartsWith("duplicate-code||clone ");
        await Assert.That(both.Repository.Duplication!.Groups).IsEqualTo(0);

        // One of two: fewer than two copies remain, so the group is suppressed.
        var one = CloneHost.Run([new("A.cs", Suppressed(code, "Run")), new("B.cs", Copy("B"))]);
        await Assert.That(CloneHost.Clones(one)).IsEmpty();
        await Assert.That(one.Suppressed!).HasSingleItem();

        // One of three: the other two still form a group.
        var three = CloneHost.Run([new("A.cs", Suppressed(code, "Run")), new("B.cs", Copy("B")), new("C.cs", Copy("C"))]);
        var g = await Assert.That(CloneHost.Clones(three)).HasSingleItem();
        await Assert.That(g.Clone!.Instances.Select(i => i.Path)).IsEquivalentTo(["B.cs", "C.cs"], CollectionOrdering.Matching);
        await Assert.That(three.Suppressed).IsNull();
    }

    [Test]
    public async Task Suppressing_another_rule_does_not_hide_a_clone()
    {
        var code = CloneHost.Method("A", "Run", 12);
        var other = code.Replace("    public int Run(", "    [System.Diagnostics.CodeAnalysis.SuppressMessage(\"skarp\", \"long-method\", Justification = \"j\")]\n    public int Run(", StringComparison.Ordinal);
        await Assert.That(CloneHost.Clones(CloneHost.Run([new("A.cs", other), new("B.cs", other.Replace("class A", "class B", StringComparison.Ordinal))]))).HasSingleItem();
    }

    [Test]
    public async Task The_index_suppresses_findings_with_a_path_from_any_tier()
    {
        var file = Analyzer.AnalyzeText(SourceText.From(Two), "C.cs");
        var index = SuppressionIndex.Build([file]);
        var semantic = new Finding { Id = "complex-method|C.cs|x", Rule = RuleIds.ComplexMethod, Path = "C.cs", Line = 6, Severity = "notice" };
        var elsewhere = new Finding { Id = "complex-method|C.cs|y", Rule = RuleIds.ComplexMethod, Path = "C.cs", Line = 8, Severity = "notice" };
        var cycle = new Finding { Id = "complex-method||x", Rule = RuleIds.ComplexMethod, Path = "C.cs", Line = 6, PathlessId = true };
        var (kept, suppressed) = index.Split([semantic, elsewhere, cycle]);
        await Assert.That(kept).IsEquivalentTo([elsewhere, cycle], CollectionOrdering.Matching);
        await Assert.That((await Assert.That(suppressed).HasSingleItem()).Id).IsEqualTo("complex-method|C.cs|x");
    }

    [Test]
    public async Task Check_honours_suppressions_in_the_edited_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "skarp-suppress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var o = SkarpSharp.Configuration.CommandLine.Parse(["analyze", root, "--no-history", "--no-semantic", "--no-config", "--threshold", "complex-method.notice=0"]).Options!;
            var check = FileCheck.Analyze(o, o.RuleSettings(), root, "C.cs", Two);
            await Assert.That(check.Findings).HasSingleItem(f => f.Rule == RuleIds.ComplexMethod);
            File.WriteAllText(Path.Combine(root, "C.cs"), Two);
            var full = Run(("C.cs", Two));
            await Assert.That(check.Findings.Where(f => f.Rule == RuleIds.ComplexMethod).Select(f => f.Id)).IsEquivalentTo(Of(full, RuleIds.ComplexMethod).Select(f => f.Id), CollectionOrdering.Matching);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}