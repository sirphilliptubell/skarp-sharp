using SkarpSharp.Analysis;
using SkarpSharp.Model;

namespace SkarpSharp.Tests;

/// <summary>
/// Worked examples: lambdas and anonymous methods in constructor initializers (<c>: base(...)</c>, <c>: this(...)</c>) are
/// nested bodies of the constructor, recorded like lambdas in a member body.
/// The constructor's own values do not change: cyclomatic complexity already folds the initializer (as Microsoft's
/// metrics engine does); the other metrics read the body only.
/// </summary>
public class ConstructorInitializerLambdaTests
{
    // Returns a value, so it checks synchronously (Assert.Fail throws) and its callers stay unchanged.
    static MemberResult Single(FileResult file, Func<MemberResult, bool> match, string what)
    {
        var found = file.Members.Where(match).ToList();
        if (found.Count != 1) Assert.Fail($"{what}: found {found.Count}; members: {string.Join(", ", file.Members.Select(m => m.Id))}");
        return found[0];
    }

    static async Task SameMetrics(MemberResult expected, MemberResult actual)
    {
        await Assert.That(actual.Cyclomatic).IsEqualTo(expected.Cyclomatic);
        await Assert.That(actual.CyclomaticMicrosoft).IsEqualTo(expected.CyclomaticMicrosoft);
        await Assert.That(actual.Cognitive).IsEqualTo(expected.Cognitive);
        await Assert.That(actual.Npath).IsEqualTo(expected.Npath);
        await Assert.That(actual.Nesting).IsEqualTo(expected.Nesting);
        await Assert.That(actual.Statements).IsEqualTo(expected.Statements);
        await Assert.That(actual.Lines.Physical).IsEqualTo(expected.Lines.Physical);
    }

    [Test]
    public async Task Example_1_this_initializer_lambda_is_a_nested_record()
    {
        // Mirrors the edge-case project's MemberKinds(List<int>) constructor.
        const string code = """
            using System.Collections.Generic;
            namespace N;
            class C
            {
                string _name;
                C(int n) { }
                C(List<int> items) : this(items.FindAll(x => x > 0 && x < 100).Count) { _name = items.Count > 0 ? "items" : "none"; }
            }
            class Reference
            {
                System.Predicate<int> M() => x => x > 0 && x < 100;
            }
            """;
        var file = Analyzer.AnalyzeSource(code);
        var ctor = Single(file, m => m.Id == "N.C::.ctor(List<int>)", "constructor");
        var lambda = Single(file, m => m.Parent == ctor.Id, "initializer lambda");

        await Assert.That(lambda.Id).IsEqualTo("N.C::.ctor(List<int>)/lambda@7:45");
        await Assert.That(lambda.Name).IsEqualTo(".ctor/lambda1");
        await Assert.That(lambda.Kind).IsEqualTo("Lambda");
        await Assert.That(lambda.Type).IsEqualTo("N.C");
        await Assert.That((lambda.StartLine, lambda.EndLine)).IsEqualTo((7, 7));
        // By rule: cyclomatic 1 + && = 2 (both presets); cognitive +1 for one && sequence; no nesting; an expression
        // body is one statement. All values equal those of the same lambda inside a method.
        await Assert.That(lambda.Cyclomatic).IsEqualTo(2);
        await Assert.That(lambda.CyclomaticMicrosoft).IsEqualTo(2);
        await Assert.That(lambda.Cognitive).IsEqualTo(1);
        await Assert.That(lambda.Nesting).IsEqualTo(0);
        await Assert.That(lambda.Statements).IsEqualTo(1);
        await SameMetrics(Single(file, m => m.Id.StartsWith("N.Reference::M()/lambda@", StringComparison.Ordinal), "reference lambda"), lambda);

        // The constructor keeps its values: cyclomatic 1 + && (initializer lambda, folded) + ?: = 3, as Microsoft's engine.
        await Assert.That(ctor.Cyclomatic).IsEqualTo(3);
        await Assert.That(ctor.CyclomaticMicrosoft).IsEqualTo(3);
    }

    [Test]
    public async Task Example_2_body_lambdas_keep_their_names_and_initializer_lambdas_follow()
    {
        const string code = """
            using System;
            using System.Linq;
            class B { protected B(object o) { } }
            class C : B
            {
                C(int[] a) : base(a.Select(x => x * 2))
                {
                    Action f = () => { if (a.Length > 0) { } };
                }
            }
            """;
        var file = Analyzer.AnalyzeSource(code);
        var children = file.Members.Where(m => m.Parent == "C::.ctor(int[])").ToList();
        await Assert.That(children.Count).IsEqualTo(2);
        // Body lambda: same name and id as before initializer lambdas were recorded.
        await Assert.That((children[0].Id, children[0].Name)).IsEqualTo(("C::.ctor(int[])/lambda@8:20", ".ctor/lambda1"));
        await Assert.That(children[0].Cyclomatic).IsEqualTo(2);
        // Initializer lambda: numbered after the body's lambdas, id by position as for every lambda.
        await Assert.That((children[1].Id, children[1].Name)).IsEqualTo(("C::.ctor(int[])/lambda@6:32", ".ctor/lambda2"));
        await Assert.That(children[1].Cyclomatic).IsEqualTo(1);
        await Assert.That(children[1].Statements).IsEqualTo(1);
    }

    [Test]
    public async Task Example_3_anonymous_methods_and_lambdas_nested_inside_initializer_lambdas()
    {
        const string code = """
            using System;
            using System.Linq;
            class B { protected B(Func<int, int> f) { } protected B(Func<int> g, int n) { } }
            class C : B
            {
                C() : base(delegate (int x) { if (x > 0) return x; return -x; }) { }
                C(int[] items) : base(() => items.Count(y => y > 0 || y < -10), 1) { }
            }
            """;
        var file = Analyzer.AnalyzeSource(code);

        var anon = Single(file, m => m.Parent == "C::.ctor()", "anonymous method");
        await Assert.That((anon.Id, anon.Name, anon.Kind)).IsEqualTo(("C::.ctor()/lambda@6:16", ".ctor/lambda1", "Lambda"));
        await Assert.That(anon.Cyclomatic).IsEqualTo(2);
        await Assert.That(anon.Cognitive).IsEqualTo(1);
        await Assert.That(anon.Statements).IsEqualTo(3);

        var outer = Single(file, m => m.Parent == "C::.ctor(int[])", "outer initializer lambda");
        await Assert.That((outer.Id, outer.Name)).IsEqualTo(("C::.ctor(int[])/lambda@7:27", ".ctor/lambda1"));
        var inner = Single(file, m => m.Parent == outer.Id, "inner lambda");
        await Assert.That((inner.Id, inner.Name)).IsEqualTo(("C::.ctor(int[])/lambda@7:27/lambda@7:45", ".ctor/lambda1/lambda1"));
        await Assert.That(inner.Cyclomatic).IsEqualTo(2);
        // Folding as for any nested body: the outer lambda's cyclomatic includes the inner || (1 + 1).
        await Assert.That(outer.Cyclomatic).IsEqualTo(2);
        await Assert.That(file.Members.Count(m => m.Parent is not null)).IsEqualTo(3);
    }

    [Test]
    public async Task Example_4_primary_constructor_base_arguments_are_not_recorded_yet()
    {
        // Known gap: there is no constructor record to hang them on.
        const string code = """
            using System;
            class B { protected B(Func<int> f) { } }
            class C(int n) : B(() => n > 0 ? n : 0) { }
            """;
        var file = Analyzer.AnalyzeSource(code);
        await Assert.That(file.Members).DoesNotContain(m => m.Kind == "Lambda");
    }
}