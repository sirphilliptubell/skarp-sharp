using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SkarpSharp.Findings;
using SkarpSharp.Semantic;

namespace SkarpSharp.Tests.Semantic;

/// <summary>
/// The worked examples of dead-code detection (A to D; E is the
/// fixture test against IDE0051) and of dependency extraction, one test per root kind, neutral attributes, the
/// string-literal root, self-recursion, test-only use, multi-targeting, unresolved symbols and the allowlist.
/// Written before the implementation.
/// </summary>
public class UnusedCodeTests
{
    // ---- A. unused, used, and what v1 deliberately misses ----------------------------------------------------------
    const string ExampleA = """
        internal class A1 {
            private int _a;
            private void Helper() {}
            private int Prop => 1;
            private event System.Action Changed;
            private int _b; public A1() { _b = 1; }
            private A1(int x) {}
            public void P() { Used(); }
            private void Used() {}
            private void Dead2() { OnlyFromDead2(); }
            private void OnlyFromDead2() {}
            private int Rec(int n) => n == 0 ? 0 : Rec(n - 1);
            internal void I() {}
        }
        internal class A2 { }
        internal class A3 { internal static A3 Make() => new A3(); }
        internal class A4 { public static int X; }
        public class Caller { public void M() { var x = A4.X; } public object Keep() => new A1(); }
        """;

    [Test]
    public async Task Example_A_unused_members_and_types()
    {
        var r = UnusedTestHost.Run(ExampleA);
        await Assert.That(r.Unused).IsEquivalentTo(["A1::.ctor#2", "A1::Changed", "A1::Dead2", "A1::Helper", "A1::I", "A1::OnlyFromDead2", "A1::Prop", "A1::Rec", "A1::_a", "A2", "A3"], CollectionOrdering.Matching);
        await Assert.That(r.State("A1::_b")).IsEqualTo("unread");               // written-only: unread-member (IDE0052)
        await Assert.That(r.Unread).IsEquivalentTo(["A1::_b"], CollectionOrdering.Matching);
        await Assert.That(r.State("A1::Used")).IsEqualTo("used");
        await Assert.That(r.State("A1::OnlyFromDead2")).IsEqualTo("unused (used only by unused code: A1::Dead2)");   // transitive
        await Assert.That(r.State("A4")).IsEqualTo("used");                     // a member used from outside uses its type
        await Assert.That(r.State("A3::Make")).IsEqualTo("inside an unused type");
        await Assert.That(r.TestOnly).IsEmpty();
    }

    [Test]
    public async Task Example_A_messages_and_rules()
    {
        var r = UnusedTestHost.Run(ExampleA);
        var helper = r.Findings.Single(f => f.PlaceKey == "A1::Helper");
        await Assert.That((helper.Rule, helper.Severity)).IsEqualTo((RuleIds.UnusedMember, "notice"));
        await Assert.That(helper.Message).Contains("private method");
        await Assert.That(helper.Id).IsEqualTo("unused-member|T/File0.cs|A1::Helper");
        var a2 = r.Findings.Single(f => f.PlaceKey == "A2");
        await Assert.That(a2.Rule).IsEqualTo(RuleIds.UnusedType);
        await Assert.That(a2.TypePlace).IsTrue();
        await Assert.That(a2.Message).Contains("internal class");
    }

    [Test]
    public async Task Nameof_counts_as_a_use_and_a_documentation_cref_does_not()
    {
        var r = UnusedTestHost.Run("""
            public class C {
                private void N() {}
                public string S => nameof(N);
                /// <see cref="OnlyInDocs"/>
                public void Doc() {}
                private void OnlyInDocs() {}
                [System.ComponentModel.Description(nameof(InAttribute))] public void Attributed() {}
                private int InAttribute;
            }
            """);
        await Assert.That(r.Unused).IsEquivalentTo(["C::OnlyInDocs"], CollectionOrdering.Matching);
        await Assert.That(r.State("C::N")).IsEqualTo("used");
        await Assert.That(r.State("C::InAttribute")).IsEqualTo("used");
    }

    // ---- B. what counts as a reference --------------------------------------------------------------------------------

    [Test]
    public async Task Example_B_every_reference_kind_keeps_the_target()
    {
        var r = UnusedTestHost.Run("""
            internal class Foo4 {}
            internal class Foo5 {}
            public class B {
                private void M1() {}   public void R1() { System.Action a = M1; }
                private void M2() {}   public void R2() { Call(() => M2()); }   static void Call(System.Action a) => a();
                private void M3() {}   public void R3() { void Local() => M3(); Local(); }
                public void R4() { var t = typeof(Foo4); }
                public void R5() { var l = new System.Collections.Generic.List<Foo5>(); }
                class Attr : System.Attribute {}  [Attr] public void R6() {}
                private static int Op;  public void R7() { Op++; }
            }
            internal class Base { internal Base(int x = 0) {} }
            public class Derived : Base { }
            internal struct P { private P(int x) {} public static P Make() => new P(1); }
            public class UsesP { public object O() => P.Make(); }
            internal class Seq { public Seq GetEnumerator() => this; public bool MoveNext() => false; public int Current => 0; }
            public class R8 { public void M() { foreach (var i in new Seq()) {} } }
            internal class Coll : System.Collections.IEnumerable { internal void Add(int x) {} public System.Collections.IEnumerator GetEnumerator() => null!; }
            public class R9 { public void M() { var c = new Coll { 1, 2 }; } }
            internal class Dec { internal void Deconstruct(out int a, out int b) { a = b = 0; } }
            public class R10 { public void M() { var (a, b) = new Dec(); } }
            internal class Pat { internal int Level { get; init; } }
            public class R11 { public bool M(Pat p) => p is { Level: > 1 }; public Pat W(Pat p) => p with { Level = 2 }; }
            """);
        await Assert.That(r.Unused).IsEmpty();
    }

    [Test]
    [Arguments("private void M1() {}", "public void R() { System.Action a = M1; }", "C::M1", "used")]
    [Arguments("private void M2() {}", "public void R() { System.Action a = () => M2(); a(); }", "C::M2", "used")]
    [Arguments("private void M3() {}", "public void R() { void Local() => M3(); Local(); }", "C::M3", "used")]
    // Written only: a static field is unread; instance members of a record are read by its synthesized members.
    [Arguments("private static int Op;", "public void R() { Op++; }", "C::Op", "unread")]
    [Arguments("private int F;", "public void R() { F = 1; }", "C::F", "rooted: written but never read, not reported: record member (synthesized Equals and ToString read it)")]
    [Arguments("private event System.Action E;", "public void R() { E += () => {}; }", "C::E", "used")]
    [Arguments("private int P { get; set; }", "public C W(C c) => c with { P = 1 };", "C::P", "rooted: written but never read, not reported: record member (synthesized Equals and ToString read it)")]
    [Arguments("private C(int x) {}", "public static C Make() => new C(1);", "C::.ctor#2", "used")]
    [Arguments("private const int K = 1;", "public int R(int v) => v switch { K => 1, _ => 0 };", "C::K", "used")]
    [Arguments("private static int Add(int a) => a;", "public System.Func<int, int> R() => Add;", "C::Add", "used")]
    public async Task Each_reference_kind_is_a_use_and_without_it_the_member_is_unused(string declaration, string use, string place, string state)
    {
        var used = UnusedTestHost.Run($"public record C {{ public C() {{}} {declaration} {use} }}");
        await Assert.That(used.State(place)).IsEqualTo(state);
        var unused = UnusedTestHost.Run($"public record C {{ public C() {{}} {declaration} }}");
        await Assert.That(unused.Unused).IsEquivalentTo([place], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Types_used_in_signatures_base_lists_casts_patterns_and_generic_arguments()
    {
        var r = UnusedTestHost.Run("""
            internal class InSig {}  internal class InBase {}  internal interface IInList {}  internal class InCast {}
            internal class InPattern {}  internal class InCatch : System.Exception {}  internal class InLocal {}  internal class InArg {}
            internal class InConstraint {}  internal class InNested { internal class Inner {} }
            public class U : InBase, IInList {
                public void S(InSig s) {}
                public object C(object o) => (InCast)o;
                public bool P(object o) => o is InPattern;
                public void T() { try {} catch (InCatch) {} }
                public void L() { InLocal? x = null; _ = x; }
                public int G() => System.Array.Empty<InArg>().Length;
                public void K<TX>() where TX : InConstraint {}
                public object N() => new InNested.Inner();
            }
            """);
        await Assert.That(r.Unused).IsEmpty();
        await Assert.That(r.State("InNested")).IsEqualTo("used");               // a use of a nested type uses its containers
    }

    [Test]
    public async Task Implicit_base_constructor_call_uses_the_base_constructor()
    {
        var r = UnusedTestHost.Run("""
            public class Base { internal Base(int x = 0) {} }
            public class Derived : Base { }
            public class Base2 { internal Base2(int x = 0) {} public Base2(string s) {} }
            """);
        await Assert.That(r.State("Base::.ctor")).IsEqualTo("used");
        await Assert.That(r.Unused).IsEquivalentTo(["Base2::.ctor"], CollectionOrdering.Matching);
    }

    // ---- C. roots --------------------------------------------------------------------------------------------------

    const string Roots = """
        using System;
        using System.Runtime.Serialization;
        class Program { static void Main() {} }
        interface IOp { void Run(); void Stop(); }
        internal class Impl : IOp { public void Run() {} void IOp.Stop() {} }
        internal class Impl2 : IOp { public void Run() {} void IOp.Stop() {} }
        public class Base2 { internal virtual void X() {} }
        internal class Der : Base2 { internal override void X() {} }
        partial class Attrs {
            [System.Text.Json.Serialization.JsonPropertyName("n")] private int _name;
            [DataMember] internal int Id;
            [System.Runtime.CompilerServices.ModuleInitializer] internal static void Init() {}
            static Attrs() {}
            ~Attrs() {}
            [System.Runtime.InteropServices.DllImport("x")] private static extern void Native();
            private void Button_Click(object s, EventArgs e) {}
            partial void OnLoaded();
        #if DEBUG
            void D() { Hidden(); }
        #endif
            private void Hidden() {}
            private bool ShouldSerializeTitle() => true;  public string Title { get; set; } = "";
            private bool ShouldSerializeNope() => true;
            [Obsolete] private void Old2() {}
            [UsedImplicitly] private void Implicit() {}
        }
        class UsedImplicitlyAttribute : Attribute {}
        class Str { private int Reflected; }
        [System.Diagnostics.DebuggerDisplay("{Shown}")] class Disp { private int Shown; private int NotShown; }
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)] internal class Dam { private int P; }
        internal class Dam2 { private int Q; }
        internal record Rec(int A);
        class Ser : ISerializable {
            public Ser() {}
            private Ser(SerializationInfo i, StreamingContext c) {}
            public void GetObjectData(SerializationInfo i, StreamingContext c) {}
        }
        class Unrooted { private void Old() {} }
        class Awaitable { internal Awaiter GetAwaiter() => new(); }
        class Awaiter : System.Runtime.CompilerServices.INotifyCompletion {
            internal bool IsCompleted => true; internal void GetResult() {} public void OnCompleted(Action a) {}
        }
        public class Users {
            public object[] All() => [new Impl(), new Der(), new Attrs(), new Disp(), new Dam(), new Rec(1), new Unrooted(), new Awaitable()];
            public object R() => Type.GetType("Str")!.GetField("Reflected")!;
            public Type D2() => typeof(Dam2);
            public object S() => new Ser();
        }
        """;

    [Test]
    public async Task Example_C_roots()
    {
        var r = UnusedTestHost.Run(Roots);
        await Assert.That(r.Unused).IsEquivalentTo(["Attrs::Old2", "Attrs::ShouldSerializeNope", "Disp::NotShown", "Impl2", "Unrooted::Old"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("Program::Main", "entry point")]
    [Arguments("Program", "entry point")]
    [Arguments("Impl::Stop", null)]                         // explicit interface implementation: not a candidate
    [Arguments("Der::X", "override")]
    [Arguments("Base2::X", "virtual")]
    [Arguments("Attrs::_name", "attribute")]
    [Arguments("Attrs::Id", "attribute")]
    [Arguments("Attrs::Init", "attribute")]
    [Arguments("Attrs::Native", "extern")]
    [Arguments("Attrs::Button_Click", "event handler")]
    [Arguments("Attrs::OnLoaded", "partial")]
    [Arguments("Attrs::Hidden", "inactive")]
    [Arguments("Attrs::ShouldSerializeTitle", "designer")]
    [Arguments("Attrs::Implicit", "attribute")]
    [Arguments("Str::Reflected", "string")]
    [Arguments("Str", "string")]
    [Arguments("Disp::Shown", "string")]
    [Arguments("Dam::P", "attribute")]
    [Arguments("Dam2::Q", "typeof")]
    [Arguments("Ser::.ctor#2", "deserialization")]
    [Arguments("Awaitable::GetAwaiter", "compiler pattern")]
    [Arguments("Awaiter::IsCompleted", "compiler pattern")]
    [Arguments("Awaiter::GetResult", "compiler pattern")]
    public async Task Root_kinds(string place, string? reason)
    {
        var r = UnusedTestHost.Run(Roots);
        if (reason is null)
        {
            await Assert.That(r.Analysis.Classified).DoesNotContain(c => c.Candidate.PlaceKey == place);
            return;
        }
        var state = r.State(place);
        await Assert.That(state).StartsWith("rooted: ");
        await Assert.That(state).Contains(reason);
    }

    [Test]
    public async Task Suppressed_unused_members_leave_the_summary_counts()
    {
        // The unusedCode summary counts leave out what an inline suppression hid, as the finding counts do.
        const string source = """
            using System.Diagnostics.CodeAnalysis;
            public class N {
                private void A() {}
                [SuppressMessage("skarp", "unused-member", Justification = "called by the plugin loader")] private void B() {}
                public void Use() {}
            }
            """;
        var r = UnusedTestHost.Run(source);
        await Assert.That(r.Unused).IsEquivalentTo(["N::A", "N::B"], CollectionOrdering.Matching);
        var path = r.Findings.First(f => f.Rule == RuleIds.UnusedMember).Path;
        var result = new SkarpSharp.Model.SemanticResult { Findings = [.. r.Findings], UnusedCode = r.Analysis.Summary };
        int before = result.UnusedCode.UnusedMembers;
        var file = SkarpSharp.Analysis.Analyzer.AnalyzeText(Microsoft.CodeAnalysis.Text.SourceText.From(source), path);
        SkarpSharp.Commands.SemanticStage.Suppress([result], SuppressionIndex.Build([file]));
        await Assert.That(result.UnusedCode.UnusedMembers).IsEqualTo(before - 1);
        var s = await Assert.That(result.Suppressed!).HasSingleItem();
        await Assert.That(s.Id).EndsWith("N::B");
    }

    [Test]
    public async Task Neutral_attributes_do_not_make_code_used_and_other_attributes_do()
    {
        var r = UnusedTestHost.Run("""
            using System.Diagnostics;
            using System.Diagnostics.CodeAnalysis;
            public class N {
                [System.Obsolete] private void A() {}
                [SuppressMessage("x", "y")] private void B() {}
                [Conditional("DEBUG")] private void C() {}
                [DebuggerStepThrough] private void D() {}
                [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] private void E() {}
                [ExcludeFromCodeCoverage] private void F() {}
                [return: NotNull] private object G() => new();
                [System.ComponentModel.Description("x")] private void H() {}
                [Mine] private void I() {}
            }
            [System.Diagnostics.DebuggerDisplay("x")] internal class OnlyDisplay {}
            [System.Serializable] internal class Marked { private int _f; }
            class MineAttribute : System.Attribute {}
            """);
        await Assert.That(r.Unused).IsEquivalentTo(["N::A", "N::B", "N::C", "N::D", "N::E", "N::F", "N::G", "OnlyDisplay"], CollectionOrdering.Matching);
        await Assert.That(r.State("N::H")).Contains("attribute");
        await Assert.That(r.State("Marked::_f")).Contains("attribute");   // attribute on the containing type
    }

    [Test]
    public async Task Names_in_string_literals_root_every_candidate_of_that_name()
    {
        var r = UnusedTestHost.Run("""
            public class S {
                public object R() => typeof(string).GetMethod("ByName")!;
                public string Q => "Ns.Outer+ByType, Assembly";
                public string Sentence => "Spaced words do not count";
            }
            internal class Other { private void ByName() {} private void Spaced() {} }
            internal class ByType {}
            public class Keep { public object K() => new Other(); }
            """);
        await Assert.That(r.State("Other::ByName")).Contains("string");
        await Assert.That(r.State("ByType")).Contains("string");
        await Assert.That(r.Unused).IsEquivalentTo(["Other::Spaced"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Dynamic_member_names_root_candidates_of_that_name()
    {
        var r = UnusedTestHost.Run("""
            public class D { public object Call(dynamic d) => d.Hidden(); }
            internal class H { internal int Hidden() => 1; internal int Other() => 2; }
            public class Keep { public object K() => new H(); }
            """);
        await Assert.That(r.State("H::Hidden")).StartsWith("rooted: ");
        await Assert.That(r.Unused).IsEquivalentTo(["H::Other"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Self_recursion_and_uses_inside_the_own_type_do_not_count()
    {
        var r = UnusedTestHost.Run("""
            public class R {
                private int Rec(int n) => n == 0 ? 0 : Rec(n - 1);
                private int Mutual1(int n) => Mutual2(n);
                private int Mutual2(int n) => Mutual1(n);
                private int Prop { get => Prop2; set => Prop = value; }
                private int Prop2 => 0;
            }
            internal class Self { internal static Self Make() => new Self(); internal Self Clone() => Make(); }
            internal class Outer { internal class Inner { Outer O = null!; } }
            """);
        // Mutual recursion is dead together, and Prop2 is used only by the unused Prop.
        await Assert.That(r.Unused).IsEquivalentTo(["Outer", "R::Mutual1", "R::Mutual2", "R::Prop", "R::Prop2", "R::Rec", "Self"], CollectionOrdering.Matching);
        await Assert.That(r.State("Outer+Inner")).IsEqualTo("inside an unused type");
    }

    [Test]
    public async Task Framework_scanned_base_types_root_the_type_and_other_bases_do_not()
    {
        var hosting = new TestProject("Hosting", """
            namespace Microsoft.Extensions.Hosting { public interface IHostedService {} public abstract class BackgroundService : IHostedService {} }
            namespace MediatR { public interface IRequestHandler<TRequest, TResponse> {} }
            """);
        var app = new TestProject("App", """
            internal class Worker : Microsoft.Extensions.Hosting.BackgroundService {}
            internal class Handler : MediatR.IRequestHandler<int, string> {}
            internal class Plain : System.IDisposable { public void Dispose() {} }
            internal class Derived : System.Collections.Generic.List<int> {}
            """) { References = ["Hosting"] };
        var r = UnusedTestHost.Run([hosting, app]);
        await Assert.That(r.State("Worker")).Contains("framework");
        await Assert.That(r.State("Handler")).Contains("framework");
        await Assert.That(r.Unused).IsEquivalentTo(["Derived", "Plain"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Struct_fields_with_native_layout_use_are_rooted()
    {
        var r = UnusedTestHost.Run("""
            internal struct Native { private int _reserved; internal int Value; }
            internal struct Managed { private int _unused; internal int Value; }
            public static unsafe class Interop {
                [System.Runtime.InteropServices.DllImport("x")] private static extern void Fill(Native* n);
                public static int Get() { Native n = default; Fill(&n); return n.Value; }
                public static int M() => new Managed().Value;
            }
            """);
        await Assert.That(r.State("Native::_reserved")).Contains("layout");
        await Assert.That(r.Unused).IsEquivalentTo(["Managed::_unused"], CollectionOrdering.Matching);
    }

    // ---- D. InternalsVisibleTo, tests, targets ----------------------------------------------------------------------

    [Test]
    public async Task Example_D_internals_visible_to_a_friend_outside_the_solution_root_internal_candidates()
    {
        var r = UnusedTestHost.Run("""
            [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ExternalFriend")]
            internal class L1 { private void P() {} internal void I() {} }
            """);
        await Assert.That(r.State("L1")).Contains("InternalsVisibleTo");
        await Assert.That(r.State("L1::I")).Contains("InternalsVisibleTo");
        await Assert.That(r.Unused).IsEquivalentTo(["L1::P"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Example_D_used_only_by_tests()
    {
        var lib = new TestProject("Lib", """
            [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Lib.Tests")]
            internal class L2 {}
            internal class L3 {}
            public class Api { public object Use() => new L3(); internal void ForTests() {} internal void Nowhere() {} }
            """);
        var tests = new TestProject("Lib.Tests", """
            public class LibTests { public void T() { _ = new L2(); _ = new L3(); new Api().ForTests(); } private void NeverReported() {} }
            """) { Test = true, References = ["Lib"] };
        var r = UnusedTestHost.Run([lib, tests]);
        await Assert.That(r.TestOnly).IsEquivalentTo(["Api::ForTests", "L2"], CollectionOrdering.Matching);
        await Assert.That(r.Unused).IsEquivalentTo(["Api::Nowhere"], CollectionOrdering.Matching);
        await Assert.That(r.State("L3")).IsEqualTo("used");
        foreach (var f in r.Findings.Where(f => f.Rule == RuleIds.UsedOnlyByTests)) await Assert.That(f.Severity).IsEqualTo("notice");
    }

    [Test]
    public async Task Used_only_by_tests_is_never_gated()
    {
        var findings = new List<SkarpSharp.Model.Finding>
        {
            new() { Rule = RuleIds.UsedOnlyByTests, Severity = "notice", BaselineState = "new" },
            new() { Rule = RuleIds.UnusedMember, Severity = "notice", BaselineState = "new" },
        };
        var g = Gate.Evaluate(findings, ["*:*"]);
        await Assert.That((g.New, g.Outside)).IsEqualTo((1, 0));
        await Assert.That(Gate.Validate("used-only-by-tests:notice")).IsNotNull();
        await Assert.That(Gate.Validate("unused-member:notice")).IsNull();
    }

    const string MultiSource = """
        public class M {
        #if NET10_0_OR_GREATER
            internal void OnlyNew() {}
            internal void UsedNew() {}
            public void CallNew() => UsedNew();
        #endif
            internal void ForOld() {}
        #if NET8_0
            public void UseOld() { ForOld(); }
        #endif
            internal void Never() {}
        }
        """;

    [Test]
    public async Task Example_D_multi_target_certain_with_all_targets()
    {
        var r = UnusedTestHost.Run([new TestProject("Multi", MultiSource) { Frameworks = ["net8.0", "net10.0"] }]);
        await Assert.That(r.Unused).IsEquivalentTo(["M::Never", "M::OnlyNew"], CollectionOrdering.Matching);
        await Assert.That(r.State("M::ForOld")).IsEqualTo("used");
        await Assert.That(r.State("M::UsedNew")).IsEqualTo("used");
        await Assert.That(r.Analysis.Summary.NotEvaluated).IsEqualTo(0);
    }

    [Test]
    public async Task Example_D_multi_target_reports_from_the_highest_target_without_all_targets()
    {
        // The highest target alone is analyzed and reported; a member used only inside an inactive #if
        // Block for another target counts as used by its name.
        var r = UnusedTestHost.Run([new TestProject("Multi", MultiSource) { Frameworks = ["net8.0", "net10.0"], Analyzed = ["net10.0"] }]);
        await Assert.That(r.Unused).IsEquivalentTo(["M::Never", "M::OnlyNew"], CollectionOrdering.Matching);
        await Assert.That(r.State("M::ForOld")).IsEqualTo("rooted: name in inactive #if code (ForOld)");
        await Assert.That(r.State("M::UsedNew")).IsEqualTo("used");
        await Assert.That(r.Analysis.Summary.NotEvaluated).IsEqualTo(0);
        var never = r.Findings.Single(f => f.PlaceKey == "M::Never");
        await Assert.That(never.Message).EndsWith(" (net10.0 only; --all-targets for all)");
        await Assert.That(r.Analysis.Summary.Notes).Contains(n => n.Contains("Multi.csproj: net10.0 of net10.0, net8.0", StringComparison.Ordinal));
        var row = await Assert.That(r.Analysis.Summary.Projects).HasSingleItem();
        await Assert.That(row.TargetFrameworksNotAnalyzed).IsEquivalentTo(["net8.0"], CollectionOrdering.Matching);
        await Assert.That(row.NotEvaluatedReason).IsNull();

        // Finding identity does not depend on the targets analyzed (baselines stay comparable).
        var all = UnusedTestHost.Run([new TestProject("Multi", MultiSource) { Frameworks = ["net8.0", "net10.0"] }]);
        await Assert.That(all.Findings.Select(f => f.Id).Order(StringComparer.Ordinal)).IsEquivalentTo(r.Findings.Select(f => f.Id).Order(StringComparer.Ordinal), CollectionOrdering.Matching);
        await Assert.That(all.Findings.Single(f => f.PlaceKey == "M::Never").Message).DoesNotContain("only;");
    }

    [Test]
    public async Task A_member_used_only_from_a_file_compiled_for_a_lower_target_is_not_reported()
    {
        // A conditional <Compile Include> for net8.0: the file is not compiled for net10.0, so its names count as used.
        var multi = new TestProject("Multi",
            "public class Lib { internal void ViaOldFile() {} internal void Dead() {} }",
            "internal static class OldOnly { public static void M() => new Lib().ViaOldFile(); }")
        {
            Frameworks = ["net8.0", "net10.0"], Analyzed = ["net10.0"], FileFrameworks = new Dictionary<string, string[]> { ["File1.cs"] = ["net8.0"] },
        };
        var r = UnusedTestHost.Run([multi]);
        await Assert.That(r.Unused).IsEquivalentTo(["Lib::Dead"], CollectionOrdering.Matching);
        await Assert.That(r.State("Lib::ViaOldFile")).IsEqualTo("rooted: name in a file compiled only by targets not analyzed (ViaOldFile)");
        await Assert.That(r.Analysis.Classified).DoesNotContain(c => c.Candidate.PlaceKey.StartsWith("OldOnly", StringComparison.Ordinal));
        await Assert.That(r.Analysis.Summary.Notes).Contains(n => n.Contains("1 file compiled only by targets not analyzed", StringComparison.Ordinal));

        // With every target analyzed the file is walked: OldOnly is a candidate nothing uses, so ViaOldFile,
        // whose only user it is, is used only by unused code.
        var all = UnusedTestHost.Run([multi with { Analyzed = null }]);
        await Assert.That(all.Unused).IsEquivalentTo(["Lib::Dead", "Lib::ViaOldFile", "OldOnly"], CollectionOrdering.Matching);
        await Assert.That(all.State("Lib::ViaOldFile")).IsEqualTo("unused (used only by unused code: OldOnly)");
    }

    [Test]
    public async Task An_extension_method_named_like_a_framework_method_called_in_the_analyzed_target_is_kept()
    {
        // Open-XML-SDK (precision check): d.Remove(key, out v) binds to Dictionary.Remove in net10.0 and to the
        // polyfill extension in netstandard2.0; the polyfill and its class are kept when a lower target is not analyzed.
        const string source = """
            using System.Collections.Generic;
            internal static class Polyfills {
                internal static bool Remove<TKey, TValue>(this Dictionary<TKey, TValue> d, TKey key, out TValue value) where TKey : notnull { value = default!; return false; }
                internal static int NeverCalled(this string s) => 0;
            }
            public class User { public bool M(Dictionary<string, int> d) => d.Remove("a", out _); }
            """;
        var r = UnusedTestHost.Run([new TestProject("Multi", source) { Frameworks = ["net8.0", "net10.0"], Analyzed = ["net10.0"] }]);
        await Assert.That(r.Unused).IsEquivalentTo(["Polyfills::NeverCalled"], CollectionOrdering.Matching);
        await Assert.That(r.State("Polyfills::Remove")).StartsWith("rooted: name of a framework method called in the analyzed target");
        await Assert.That(r.State("Polyfills")).StartsWith("rooted: contains an extension method kept by its name (Remove: name of a framework method");

        // Every target analyzed: no target binds to the polyfill, so the class is unused (certain).
        var all = UnusedTestHost.Run([new TestProject("Multi", source) { Frameworks = ["net8.0", "net10.0"] }]);
        await Assert.That(all.Unused).IsEquivalentTo(["Polyfills"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_type_whose_extension_method_is_used_only_in_inactive_code_is_kept()
    {
        var r = UnusedTestHost.Run([new TestProject("Multi", """
            internal static class Ext { public static int Twice(this int x) => 2 * x; }
            public class C {
            #if !NET10_0_OR_GREATER
                public int M() => 3.Twice();
            #endif
            }
            """) { Frameworks = ["net8.0", "net10.0"], Analyzed = ["net10.0"] }]);
        // Twice is public (not a candidate); its class is the candidate, kept by the method's name.
        await Assert.That(r.Unused).IsEmpty();
        await Assert.That(r.State("Ext")).IsEqualTo("rooted: contains an extension method kept by its name (Twice: name in inactive #if code (Twice))");
    }

    [Test]
    public async Task A_friend_project_analyzed_for_its_highest_target_reports_the_internals_it_sees_with_the_scope()
    {
        var lib = new TestProject("Lib", """
            [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Friend")]
            public class C { internal void I() {} private void P() {} internal void ForOldFriend() {} }
            """);
        var friend = new TestProject("Friend", """
            public class F {
            #if !NET10_0_OR_GREATER
                public void Old() => new C().ForOldFriend();
            #endif
            }
            """) { References = ["Lib"], Frameworks = ["net8.0", "net10.0"], Analyzed = ["net10.0"] };
        var r = UnusedTestHost.Run([lib, friend]);
        await Assert.That(r.Unused).IsEquivalentTo(["C::I", "C::P"], CollectionOrdering.Matching);
        await Assert.That(r.State("C::ForOldFriend")).IsEqualTo("rooted: name in inactive #if code (ForOldFriend)");
        await Assert.That(r.Findings.Single(f => f.PlaceKey == "C::I").Message).EndsWith(" (friend Friend: net10.0 only; --all-targets for all)");
        await Assert.That(r.Findings.Single(f => f.PlaceKey == "C::P").Message).DoesNotContain("only;");
    }

    // ---- not evaluated: unresolved symbols ---------------------------------------------------------------------------

    [Test]
    public async Task Types_with_unresolved_symbols_are_not_evaluated_and_their_references_still_count()
    {
        var r = UnusedTestHost.Run("""
            internal class Broken { private void X() {} public void Y() { Missing.Call(new Target()); Other.Run(Named); } }
            internal class Target {}
            internal class Fine { private void Z() {} internal static void Named() {} }
            public class Keep { public object K() => new Fine(); }
            """);
        await Assert.That(r.State("Broken")).StartsWith("not evaluated: ");
        await Assert.That(r.State("Broken::X")).StartsWith("not evaluated: ");
        await Assert.That(r.State("Target")).IsEqualTo("used");
        await Assert.That(r.Unused).IsEquivalentTo(["Fine::Z"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Generated_code_is_walked_and_never_reported()
    {
        var r = UnusedTestHost.Run([new TestProject("G",
            "internal class Hand { private void Dead() {} internal void UsedByGenerated() {} }",
            "// <auto-generated/>\ninternal class Gen { private void NeverReported() {} void Call() => new Hand().UsedByGenerated(); }",
            "internal class FromGenerator { private void NeverReported() {} }")
            { GeneratedFiles = ["File2.cs"] }]);
        await Assert.That(r.Unused).IsEquivalentTo(["Hand::Dead"], CollectionOrdering.Matching);
        await Assert.That(r.Analysis.Classified).DoesNotContain(c => c.Candidate.PlaceKey.StartsWith("Gen", StringComparison.Ordinal) || c.Candidate.PlaceKey.StartsWith("FromGenerator", StringComparison.Ordinal));
    }

    [Test]
    public async Task Markup_projects_without_generated_code_and_generators_off_are_not_evaluated()
    {
        var dir = Path.Combine(UnusedTestHost.Root, "Wpf");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "MainWindow.xaml"), "<Window/>");
        var wpf = UnusedTestHost.Run([new TestProject("Wpf", "internal class Handlers { private void OnClick() {} }")]);
        await Assert.That(wpf.State("Handlers::OnClick")).StartsWith("not evaluated: .xaml files");

        var off = UnusedTestHost.Run([new TestProject("Gen", "internal class C { private void M() {} }") { Analyzers = ["gen.dll"], Generators = false }]);
        await Assert.That(off.State("C::M")).Contains("--no-generators");
        var noAnalyzers = UnusedTestHost.Run([new TestProject("NoAnalyzers", "internal class C { private void M() {} }") { Generators = false }]);
        await Assert.That(noAnalyzers.Unused).IsEquivalentTo(["C"], CollectionOrdering.Matching);
    }

    // ---- found by the precision check ---------------------------------------------------------

    [Test]
    public async Task A_file_linked_into_several_projects_is_unused_only_when_every_copy_is()
    {
        const string shim = "namespace Shared { internal static class Shim { internal static int Twice(int x) => 2 * x; internal static int Never() => 0; } }";
        var a = new TestProject("A", shim, "public class UsesShim { public int M() => Shared.Shim.Twice(2); }") { Paths = ["common/Shim.cs", "A/Uses.cs"] };
        var b = new TestProject("B", shim, "public class Other { }") { Paths = ["common/Shim.cs", "B/Other.cs"] };
        var r = UnusedTestHost.Run([a, b]);
        await Assert.That(r.Unused).IsEquivalentTo(["Shared.Shim::Never"], CollectionOrdering.Matching);                      // one finding, not one per project
        await Assert.That(r.State("Shared.Shim")).IsEqualTo("used");
        await Assert.That(r.Analysis.Classified).HasSingleItem(c => c.Candidate.PlaceKey == "Shared.Shim::Twice");
    }

    [Test]
    public async Task A_shared_file_also_compiled_by_a_lower_target_of_another_project_counts_that_targets_own_files()
    {
        // Open-XML-SDK: a shim file compiled by a single-target project A and by the lower target of a multi-targeted
        // project B whose highest target does not compile it (such files are analyzed, not reported as "not evaluated").
        const string shim = "namespace Shared { internal static class Shim { internal static int Twice(int x) => 2 * x; internal static int Never() => 0; } }";
        var a = new TestProject("A", shim, "public class Own { private void Dead() {} }") { Paths = ["common/Shim.cs", "A/Own.cs"] };
        var b = new TestProject("B", shim, "public class Old { public int M() => Shared.Shim.Twice(1); }", "public class Other { }")
        {
            Paths = ["common/Shim.cs", "B/Old.cs", "B/Other.cs"], Frameworks = ["net8.0", "net10.0"], Analyzed = ["net10.0"],
            FileFrameworks = new Dictionary<string, string[]> { ["File0.cs"] = ["net8.0"], ["File1.cs"] = ["net8.0"] },
        };
        var r = UnusedTestHost.Run([a, b]);
        await Assert.That(r.Unused).IsEquivalentTo(["Own::Dead", "Shared.Shim::Never"], CollectionOrdering.Matching);
        await Assert.That(r.State("Shared.Shim::Twice")).IsEqualTo("rooted: name in a file compiled only by targets not analyzed (Twice)");
    }

    [Test]
    public async Task Polyfills_in_System_namespaces_are_rooted()
    {
        var r = UnusedTestHost.Run("""
            namespace System.Runtime.CompilerServices { internal static class IsExternalInit {} }
            namespace System.Diagnostics.CodeAnalysis { internal sealed class MyShimAttribute : Attribute { private void Unused() {} } }
            namespace App { internal class NotAShim {} }
            """);
        await Assert.That(r.State("System.Runtime.CompilerServices.IsExternalInit")).Contains("polyfill");
        await Assert.That(r.State("System.Diagnostics.CodeAnalysis.MyShimAttribute::Unused")).Contains("polyfill");
        await Assert.That(r.Unused).IsEquivalentTo(["App.NotAShim"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Fields_written_by_a_non_constant_initializer_are_unread_members_like_IDE0052()
    {
        var r = UnusedTestHost.Run("""
            public class F {
                private readonly object _lock = new();
                private int _constant = 5;
                private static int Compute() => 1;
                private int _computed = Compute();
                private int Auto { get; } = Compute();
            }
            """);
        // The unread-member rule replaces a "non-constant initializer" root.
        await Assert.That(r.Unused).IsEquivalentTo(["F::_constant"], CollectionOrdering.Matching);
        await Assert.That(r.Unread).IsEquivalentTo(["F::Auto", "F::_computed"], CollectionOrdering.Matching);
        await Assert.That(r.State("F::_lock")).StartsWith("rooted: written but never read, not reported: object type");
        await Assert.That(r.State("F::Compute")).IsEqualTo("used");
    }

    // ---- allowlist --------------------------------------------------------------------------------------------------

    [Test]
    public async Task Allowlist_by_documentation_id_and_by_name_pattern()
    {
        const string source = "namespace Ns { public class C { private void A() {} private void B() {} private void Keep1() {} } internal class Gone {} }";
        await Assert.That(UnusedTestHost.Run(source).Unused).IsEquivalentTo(["Ns.C::A", "Ns.C::B", "Ns.C::Keep1", "Ns.Gone"], CollectionOrdering.Matching);
        var r = UnusedTestHost.Run(source, "M:Ns.C.A", "Ns.C.Keep*", "*.Gone");
        await Assert.That(r.Unused).IsEquivalentTo(["Ns.C::B"], CollectionOrdering.Matching);
        await Assert.That(r.State("Ns.C::A")).Contains("allowlist");
    }

    [Test]
    public async Task Rules_switched_off_give_no_findings()
    {
        var r = UnusedTestHost.Run([new TestProject("T", ExampleA)], [], "default", "unused-member.notice=off");
        await Assert.That(r.Unused).IsEquivalentTo(["A2", "A3"], CollectionOrdering.Matching);
    }

    // ---- dependency-extraction worked example and symbol ids --------------------------------------------------------

    const string ExtractionExample = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        namespace N {
          internal class Marker : Attribute { internal Marker(string s) {} }
          internal class Base {}
          internal class Item {}
          internal class Widget { internal int Size { get; set; } }
          internal class Seq { public Seq GetEnumerator() => this; public bool MoveNext() => false; public int Current => 0; }
          internal class Pair { internal void Deconstruct(out int a, out int b) { a = b = 0; } }
          internal class Pat { internal int Level => 0; }
          [Marker(nameof(Helper))] internal class C<T> : Base where T : Item {
            int _f = Compute();
            List<Pat> list = new();
            internal void M<U>(T t, U u, ref int r) {
              var x = new Widget { Size = 2 };
              Func<int> g = Helper;
              Log(typeof(Item));
              foreach (var e in Make()) {}
              var (a, b) = new Pair();
              list.Where(p => p.Level > 1);
            }
            /// <see cref="Helper"/>
            void Doc() {}
            static int Helper() => 1;  static int Compute() => 2;
            static void Log(Type t) {}  static Seq Make() => new();
          }
          public class UseC { public object O() => new C<Item>(); }
        }
        """;

    [Test]
    public async Task Dependency_extraction_worked_example_edges()
    {
        var r = UnusedTestHost.Run(ExtractionExample);
        var used = r.UsedIds();
        string[] expected =
        [
            "T|T:N.Base", "T|T:N.Item", "T|T:N.Marker", "T|M:N.Marker.#ctor(System.String)", "T|M:N.C`1.Helper~System.Int32", "T|M:N.C`1.Compute~System.Int32",
            "T|T:N.Widget", "T|P:N.Widget.Size", "T|M:N.C`1.Log(System.Type)", "T|M:N.C`1.Make~N.Seq", "T|T:N.Seq",
            "T|T:N.Pair", "T|M:N.Pair.Deconstruct(System.Int32@,System.Int32@)", "T|P:N.Pat.Level", "T|T:N.Pat",
        ];
        await Assert.That(expected.All(used.Contains)).IsTrue().Because("missing: " + string.Join(", ", expected.Where(e => !used.Contains(e))) + " in " + string.Join(", ", used.Order(StringComparer.Ordinal)));
        await Assert.That(used).DoesNotContain("T|M:N.C`1.Doc");
        // [Marker] on C roots C and its members (any attribute counts); the edges above are what this test pins.
        await Assert.That(r.Unused).IsEmpty();
        await Assert.That(r.State("N.C`1::Doc")).IsEqualTo("rooted: attribute [Marker] on containing type C");
    }

    [Test]
    public async Task Symbol_ids_follow_the_documentation_comment_id_with_the_assembly()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            namespace N {
              public class C<T> { internal void M<U>(T t, U u, ref int r) { void Local() {} Local(); } int _f; public int this[int i] => i; internal C() {} public class Inner {} }
              public struct V { public static V operator +(V a, V b) => a; public static implicit operator int(V v) => 0; }
              interface IOp { void Stop(); }
              class Impl : IOp { void IOp.Stop() {} }
              partial class P { partial void OnLoaded(); partial void OnLoaded() {} }
            }
            """);
        var comp = CSharpCompilation.Create("Asm", [tree], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var c = comp.GetTypeByMetadataName("N.C`1")!;
        await Assert.That(SymbolIds.Of(c)).IsEqualTo("Asm|T:N.C`1");
        await Assert.That(SymbolIds.Of(c.GetMembers("M")[0])).IsEqualTo("Asm|M:N.C`1.M``1(`0,``0,System.Int32@)");
        await Assert.That(SymbolIds.Of(c.GetMembers("_f")[0])).IsEqualTo("Asm|F:N.C`1._f");
        await Assert.That(SymbolIds.Of(c.GetMembers("this[]")[0])).IsEqualTo("Asm|P:N.C`1.Item(System.Int32)");
        await Assert.That(SymbolIds.Of(c.InstanceConstructors[0])).IsEqualTo("Asm|M:N.C`1.#ctor");
        await Assert.That(SymbolIds.Of(c.GetTypeMembers("Inner")[0])).IsEqualTo("Asm|T:N.C`1.Inner");
        var v = comp.GetTypeByMetadataName("N.V")!;
        // Roslyn 5.9 appends the return type to the id of every non-void method (not only conversions, as the documented id format suggests).
        await Assert.That(SymbolIds.Of(v.GetMembers("op_Addition")[0])).IsEqualTo("Asm|M:N.V.op_Addition(N.V,N.V)~N.V");
        await Assert.That(SymbolIds.Of(v.GetMembers("op_Implicit")[0])).IsEqualTo("Asm|M:N.V.op_Implicit(N.V)~System.Int32");
        await Assert.That(SymbolIds.Of(comp.GetTypeByMetadataName("N.Impl")!.GetMembers().OfType<IMethodSymbol>().Single(m => m.Name.EndsWith("Stop", StringComparison.Ordinal)))).IsEqualTo("Asm|M:N.Impl.N#IOp#Stop");
        // Constructed generics map to the definition; partial parts share one id.
        var constructed = c.Construct(comp.GetSpecialType(SpecialType.System_Int32));
        await Assert.That(SymbolIds.Of(constructed.GetMembers("_f")[0])).IsEqualTo("Asm|F:N.C`1._f");
        var partial = (IMethodSymbol)comp.GetTypeByMetadataName("N.P")!.GetMembers("OnLoaded")[0];
        await Assert.That(SymbolIds.Of(partial.PartialImplementationPart ?? partial.PartialDefinitionPart!)).IsEqualTo(SymbolIds.Of(partial));
        // Local functions and lambdas have no id of their own (their uses belong to the enclosing member).
        var local = comp.GetSemanticModel(tree).GetDeclaredSymbol(tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.LocalFunctionStatementSyntax>().First())!;
        await Assert.That(SymbolIds.Of(local)).IsNull();
    }

    [Test]
    public async Task Partial_method_call_binds_to_the_one_id()
    {
        var r = UnusedTestHost.Run([new TestProject("T",
            "public partial class P { private partial void Load(); public void Run() => Load(); }",
            "public partial class P { private partial void Load() {} }")]);
        await Assert.That(r.UsedIds()).Contains("T|M:P.Load");
        await Assert.That(r.State("P::Load")).IsEqualTo("rooted: partial member");
    }
}