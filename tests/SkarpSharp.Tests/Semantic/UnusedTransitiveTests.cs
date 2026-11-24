using SkarpSharp.Findings;
using SkarpSharp.Semantic;

namespace SkarpSharp.Tests.Semantic;

/// <summary>
/// Transitive deadness (worked example F of dead-code detection), the
/// transitive test label, and the <c>unread-member</c> rule (worked examples of member read and write usage).
/// Written before the implementation.
/// </summary>
public class UnusedTransitiveTests
{
    // ---- F. transitive deadness ------------------------------------------------------------------------------------
    const string ExampleF = """
        public class F {
            private int Ping(int n) => n == 0 ? 0 : Pong(n - 1);
            private int Pong(int n) => Ping(n);
            private void Head() { Mid(); }
            private void Mid() { Leaf(); }
            private void Leaf() {}
        #if NEVER_DEFINED
            void Gone() { Kept(); }
        #endif
            private void Kept() { KeptCallee(); }
            private void KeptCallee() {}
            internal void Hook() { Deeper(); }
            private void Deeper() {}
        }
        internal class DeadType : System.IDisposable {
            public void Dispose() => Helpers.Free();
        }
        internal static class Helpers { internal static void Free() {} }
        internal static class Shared { internal static void Free() {} internal static void Live() {} }
        internal class DeadUser : System.IDisposable { public void Dispose() => Shared.Free(); }
        public class Api { public void Run() => Shared.Live(); }
        """;

    static UnusedRun RunF() => UnusedTestHost.Run([
        new TestProject("Lib", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"Lib.Tests\")]\n" + ExampleF),
        new TestProject("Lib.Tests", "public class FTests { public void T() => new F().Hook(); }") { Test = true, References = ["Lib"] },
    ]);

    [Test]
    public async Task Example_F_cycles_chains_and_dead_types_are_reported_and_roots_keep_their_callees()
    {
        var r = RunF();
        await Assert.That(r.Unused).IsEquivalentTo(["DeadType", "DeadUser", "F::Head", "F::Leaf", "F::Mid", "F::Ping", "F::Pong", "Helpers", "Shared::Free"], CollectionOrdering.Matching);
        await Assert.That(r.TestOnly).IsEquivalentTo(["F::Deeper", "F::Hook"], CollectionOrdering.Matching);
        await Assert.That(r.State("F::Kept")).StartsWith("rooted: name in inactive #if code");
        await Assert.That(r.State("F::KeptCallee")).IsEqualTo("used");
        await Assert.That(r.State("Shared")).IsEqualTo("used");
        await Assert.That(r.State("Helpers::Free")).IsEqualTo("inside an unused type");
    }

    [Test]
    public async Task Example_F_reasons_name_the_unused_users_with_the_depth()
    {
        var r = RunF();
        await Assert.That(r.State("F::Head")).IsEqualTo("unused");
        await Assert.That(r.State("F::Mid")).IsEqualTo("unused (used only by unused code: F::Head)");
        await Assert.That(r.State("F::Leaf")).IsEqualTo("unused (used only by unused code: F::Mid)");
        await Assert.That(r.State("F::Ping")).IsEqualTo("unused (used only by unused code: F::Pong)");
        await Assert.That(r.State("Shared::Free")).IsEqualTo("unused (used only by unused code: DeadUser)");
        await Assert.That(r.State("F::Deeper")).IsEqualTo("used-only-by-tests (via F::Hook)");

        var leaf = r.Findings.Single(f => f.PlaceKey == "F::Leaf");
        await Assert.That(leaf.Rule).IsEqualTo(RuleIds.UnusedMember);
        await Assert.That(leaf.Message).IsEqualTo("private method Leaf is used only by unused code: F::Mid");
        await Assert.That(leaf.UsedOnlyBy!).IsEquivalentTo(["F::Mid"], CollectionOrdering.Matching);
        await Assert.That(leaf.Depth).IsEqualTo(2);
        var head = r.Findings.Single(f => f.PlaceKey == "F::Head");
        await Assert.That(head.Message).IsEqualTo("private method Head has no reference in the solution");
        await Assert.That(head.UsedOnlyBy).IsNull();
        await Assert.That(head.Depth).IsEqualTo(0);
        // A dead cycle that no unreferenced code reaches starts at depth 1.
        await Assert.That(r.Findings.Single(f => f.PlaceKey == "F::Ping").Depth).IsEqualTo(1);
        var deeper = r.Findings.Single(f => f.PlaceKey == "F::Deeper");
        await Assert.That(deeper.Rule).IsEqualTo(RuleIds.UsedOnlyByTests);
        await Assert.That(deeper.Message).IsEqualTo("private method Deeper is used only by tests (via F::Hook)");
        await Assert.That(r.Findings.Single(f => f.PlaceKey == "F::Hook").Message).IsEqualTo("internal method Hook is referenced only from test projects");
    }

    [Test]
    public async Task Several_unused_users_are_counted_in_the_reason()
    {
        var r = UnusedTestHost.Run("""
            public class M {
                private void A() => H();
                private void B() => H();
                private void C() => H();
                private void H() {}
            }
            """);
        await Assert.That(r.State("M::H")).IsEqualTo("unused (used only by unused code: M::A (and 2 more))");
        var h = r.Findings.Single(f => f.PlaceKey == "M::H");
        await Assert.That(h.Message).IsEqualTo("private method H is used only by unused code: M::A (and 2 more)");
        await Assert.That(h.UsedOnlyBy!).IsEquivalentTo(["M::A", "M::B", "M::C"], CollectionOrdering.Matching);
        await Assert.That(h.Depth).IsEqualTo(1);
    }

    [Test]
    public async Task Overrides_interface_implementations_and_pattern_members_keep_callees_alive_only_in_a_live_type()
    {
        var r = UnusedTestHost.Run("""
            public abstract class Base { public abstract void Run(); }
            internal class LiveImpl : Base { public override void Run() => Util.A(); }
            internal class DeadImpl : Base { public override void Run() => Util.B(); }
            internal class Seq { public Seq GetEnumerator() => this; public bool MoveNext() => Util.C(); public int Current => 0; }
            public class BaseI { internal virtual void X() {} }
            internal class DeadDer : BaseI { internal override void X() => Util.E(); }
            internal static class Util { internal static void A() {} internal static void B() {} internal static bool C() => false; internal static void D() {} internal static void E() {} }
            public class Users { public Base Make() => new LiveImpl(); public void Use() => Util.D(); }
            """);
        await Assert.That(r.Unused).IsEquivalentTo(["DeadDer", "DeadImpl", "Seq", "Util::B", "Util::C", "Util::E"], CollectionOrdering.Matching);
        await Assert.That(r.State("DeadDer::X")).IsEqualTo("inside an unused type");         // an override candidate in a dead type
        await Assert.That(r.State("Util::E")).IsEqualTo("unused (used only by unused code: DeadDer::X)");
        await Assert.That(r.State("BaseI::X")).IsEqualTo("rooted: virtual");
        await Assert.That(r.State("Util::A")).IsEqualTo("used");
        await Assert.That(r.State("Util::B")).IsEqualTo("unused (used only by unused code: DeadImpl)");
        await Assert.That(r.State("Util::C")).IsEqualTo("unused (used only by unused code: Seq)");
    }

    [Test]
    public async Task Framework_scanned_types_attributes_strings_and_not_evaluated_code_stay_roots_of_their_callees()
    {
        var hosting = new TestProject("Hosting", "namespace Microsoft.Extensions.Hosting { public interface IHostedService { void Start(); } }");
        var app = new TestProject("App", """
            internal class Worker : Microsoft.Extensions.Hosting.IHostedService { public void Start() => Util.FromWorker(); }
            internal class Marked { [System.ComponentModel.Description("x")] private void M() => Util.FromAttribute(); }
            internal class Named { private void ByName() => Util.FromName(); }
            internal class Broken { private void X() { Missing.Call(); Util.FromBroken(); } }
            internal static class Util {
                internal static void FromWorker() {} internal static void FromAttribute() {} internal static void FromName() {}
                internal static void FromBroken() {} internal static void Dead() {}
            }
            public class Reflect { public object R() => typeof(Named).GetMethod("ByName")!; }
            """) { References = ["Hosting"] };
        var r = UnusedTestHost.Run([hosting, app]);
        await Assert.That(r.Unused).IsEquivalentTo(["Util::Dead"], CollectionOrdering.Matching);
        foreach (var m in new[] { "FromWorker", "FromAttribute", "FromName", "FromBroken" })
            await Assert.That(r.State("Util::" + m)).IsEqualTo("used");
    }

    [Test]
    public async Task A_public_member_of_an_internal_type_speaks_for_its_type()
    {
        var r = UnusedTestHost.Run("""
            internal class DeadHolder { public void Go() => Util.X(); }
            internal class LiveHolder { public void Go() => Util.Y(); }
            internal static class Util { internal static void X() {} internal static void Y() {} }
            public class Api { public object O() => new LiveHolder(); }
            """);
        await Assert.That(r.Unused).IsEquivalentTo(["DeadHolder", "Util::X"], CollectionOrdering.Matching);
        await Assert.That(r.State("Util::Y")).IsEqualTo("used");
    }

    [Test]
    public async Task Generated_code_and_public_code_are_live_sources()
    {
        var r = UnusedTestHost.Run([new TestProject("G",
            "internal static class Util { internal static void FromGenerated() {} internal static void FromPublic() {} internal static void Dead() {} }\npublic class P { public void M() => Util.FromPublic(); }",
            "// <auto-generated/>\ninternal class Gen { private void Call() => Util.FromGenerated(); }")]);
        await Assert.That(r.Unused).IsEquivalentTo(["Util::Dead"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Transitive_findings_have_the_same_identity_as_direct_ones()
    {
        var r = UnusedTestHost.Run("public class C { private void A() => B(); private void B() {} }");
        await Assert.That(r.Findings.Select(f => f.Id).Order(StringComparer.Ordinal)).IsEquivalentTo(["unused-member|T/File0.cs|C::A", "unused-member|T/File0.cs|C::B"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Edges_record_source_target_and_kind()
    {
        var r = UnusedTestHost.Run("""
            public class E {
                private int _f;
                public void W() => _f = 1;
                private int R() => _f;
                public int UseR() => R();
            }
            """);
        var edges = r.Facts.SelectMany(f => f.Edges).ToList();
        await Assert.That(edges).Contains(e => e.Source == ReferenceEdge.Live && e.Target == "T|F:E._f" && e.Kind == EdgeKind.Write);
        await Assert.That(edges).Contains(e => e.Source == "T|M:E.R~System.Int32" && e.Target == "T|F:E._f" && e.Kind == EdgeKind.Read);
        await Assert.That(edges).Contains(e => e.Source == ReferenceEdge.Live && e.Target == "T|M:E.R~System.Int32" && e.Kind == EdgeKind.Other);
    }

    // ---- unread-member ---------------------------------------------------------------------------------------------

    const string UnreadExample = """
        using System;
        public class E {
            private int _b; public E() { _b = 1; }
            private int _c; public void C() { _c++; }
            private int _d; public int D() => _d++;
            private int _o; public void O() { int.TryParse("1", out _o); }
            private int _r; public void Rf() { System.Threading.Interlocked.Increment(ref _r); }
            private S _s; public void W() { _s.X = 1; }
            private readonly object _lock = new();
            private int _k = 5;
            private int Auto { get; } = Compute();
            private static int Compute() => 1;
            private int _n; public string N() => nameof(_n);
            private int _t; private void Dead() => Console.WriteLine(_t); public void T() { _t = 1; }
            private System.Threading.Timer? _timer; public void Start() { _timer = new(_ => {}); }
            private int? _q; public int Q() => _q ??= 1;
            private int _x, _y; public void Dec() { (_x, _y) = (1, 2); }
            internal int Init { get; set; } public static E Make() => new E { Init = 1 };
            private static int s_count; public void Count() { s_count += 1; }
            private int _used; public int Used() => _used;
        }
        public struct S { public int X; }
        """;

    [Test]
    public async Task Unread_member_worked_example()
    {
        var r = UnusedTestHost.Run(UnreadExample);
        await Assert.That(r.Unread).IsEquivalentTo(["E::Auto", "E::Init", "E::_b", "E::_c", "E::_o", "E::_t", "E::_x", "E::_y", "E::s_count"], CollectionOrdering.Matching);
        await Assert.That(r.Unused).IsEquivalentTo(["E::Dead", "E::_k"], CollectionOrdering.Matching);
        foreach (var used in new[] { "E::_d", "E::_r", "E::_s", "E::_n", "E::_q", "E::_used", "E::Compute" })
            await Assert.That(r.State(used)).IsEqualTo("used");
        await Assert.That(r.State("E::_timer")).StartsWith("rooted: written but never read, not reported: ");
        await Assert.That(r.State("E::_timer")).Contains("IDisposable");
        await Assert.That(r.State("E::_lock")).IsEqualTo("rooted: written but never read, not reported: object type (keeps the object alive)");
    }

    [Test]
    public async Task Unread_member_message_rule_and_identity()
    {
        var r = UnusedTestHost.Run(UnreadExample);
        var b = r.Findings.Single(f => f.PlaceKey == "E::_b");
        await Assert.That((b.Rule, b.Severity)).IsEqualTo((RuleIds.UnreadMember, "notice"));
        await Assert.That(b.Message).IsEqualTo("private field _b is written but never read; check its writes for side effects before removing");
        await Assert.That(b.Id).IsEqualTo("unread-member|T/File0.cs|E::_b");
        await Assert.That(r.State("E::_b")).IsEqualTo("unread");
        await Assert.That(Gate.Validate("unread-member:notice")).IsNull();
        await Assert.That(RuleCatalog.Find(RuleIds.UnreadMember)).IsNotNull();
        await Assert.That(r.Analysis.Summary.UnreadMembers).IsEqualTo(9);
    }

    [Test]
    public async Task Unread_member_exclusions_are_never_reported()
    {
        var r = UnusedTestHost.Run("""
            using System;
            using System.Runtime.InteropServices;
            public class X {
                private Action? _callback; public void A(Action a) => _callback = a;
                private IDisposable? _sub; public void B(IDisposable d) => _sub = d;
                private GCHandle _handle; public void C(object o) => _handle = GCHandle.Alloc(o);
                private int _backing; private int WithBody { get => 0; set => _backing = 0; } public void D() => WithBody = 1;
                private int _plain; public void P() => _plain = 1;
            }
            public struct Str { private int _f; public Str(int v) { _f = v; } private static int s_f; public static void S() => s_f = 1; }
            [StructLayout(LayoutKind.Sequential)] public class Layout { private int _f; public Layout() { _f = 1; } }
            [System.Runtime.CompilerServices.InlineArray(4)] public struct Buffer { private int _e; public void Set() => _e = 1; }
            public record Rec { private int _hidden; public Rec() { _hidden = 1; } private static int s_r; public static void Set() => s_r = 1; }
            """);
        await Assert.That(r.Unread).IsEquivalentTo(["Rec::s_r", "Str::s_f", "X::_backing", "X::_plain"], CollectionOrdering.Matching);
        foreach (var place in new[] { "X::_callback", "X::_sub", "X::_handle", "Str::_f", "Rec::_hidden" })
            await Assert.That(r.State(place)).StartsWith("rooted: written but never read, not reported: ");
        // [StructLayout] and [InlineArray] are not neutral attributes: their fields are rooted by the attribute first.
        await Assert.That(r.State("Layout::_f")).StartsWith("rooted: attribute [StructLayout]");
        await Assert.That(r.State("Buffer::_e")).StartsWith("rooted: attribute [InlineArray]");
        await Assert.That(r.State("X::WithBody")).IsEqualTo("used");                       // a property with bodies: any reference is a use
    }

    [Test]
    public async Task Object_and_unconstrained_type_parameter_members_are_lifetime_holders_and_never_reported()
    {
        // Like delegates, IDisposable and GCHandle, a field of type object (or of a type parameter that
        // can be any reference type) is often written only to keep a value alive.
        var r = UnusedTestHost.Run("""
            public class H<T, TClass, TNotNull, TNew, TStruct, TUnmanaged, TBound, TDisp>
                where TClass : class where TNotNull : notnull where TNew : new() where TStruct : struct where TUnmanaged : unmanaged
                where TBound : System.IComparable where TDisp : System.IDisposable
            {
                private object _obj = new();
                private object? _nullable; public void A(object? o) => _nullable = o;
                private dynamic? _dyn; public void Dy(object o) => _dyn = o;
                private T? _t; public void B(T t) => _t = t;
                private TClass? _class; public void C(TClass c) => _class = c;
                private TNotNull? _notNull; public void D(TNotNull n) => _notNull = n;
                private TNew? _new; public void E(TNew n) => _new = n;
                private TStruct _struct; public void F(TStruct s) => _struct = s;
                private TUnmanaged _unmanaged; public void G(TUnmanaged u) => _unmanaged = u;
                private TBound? _bound; public void I(TBound b) => _bound = b;
                private TDisp? _disp; public void J(TDisp d) => _disp = d;
                private T[]? _array; public void K(T[] a) => _array = a;
                private object Prop { get; set; } = new(); public void L() => Prop = 1;
                private string? _string; public void M(string s) => _string = s;
            }
            public class M { private U? _u; private class U {} public void N() => _u = new U(); }
            public class GM { public void Go<TM>(TM v) => new Box<TM>().Set(v); private class Box<TB> { private TB? _v; public void Set(TB v) => _v = v; } }
            """);
        await Assert.That(r.Unread).IsEquivalentTo(["H`8::_array", "H`8::_bound", "H`8::_string", "H`8::_struct", "H`8::_unmanaged", "M::_u"], CollectionOrdering.Matching);
        foreach (var place in new[] { "H`8::_obj", "H`8::_nullable", "H`8::Prop" })
            await Assert.That(r.State(place)).IsEqualTo("rooted: written but never read, not reported: object type (keeps the object alive)");
        await Assert.That(r.State("H`8::_dyn")).IsEqualTo("rooted: written but never read, not reported: dynamic type (keeps the object alive)");
        foreach (var place in new[] { "H`8::_t", "H`8::_class", "H`8::_notNull", "H`8::_new", "GM+Box`1::_v" })
            await Assert.That(r.State(place)).IsEqualTo("rooted: written but never read, not reported: unconstrained type parameter (keeps the object alive)");
        // A type parameter constrained to IDisposable is excluded as an IDisposable.
        await Assert.That(r.State("H`8::_disp")).Contains("IDisposable");
    }

    [Test]
    public async Task Reads_from_unused_code_do_not_count_and_a_member_read_only_by_tests_gets_the_test_label()
    {
        // Written by production code, read only by tests: the "used only by tests" label, never unread.
        var lib = new TestProject("Lib", """
            [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Lib.Tests")]
            public class C {
                private int _deadRead; public void W1() => _deadRead = 1; private int Dead() => _deadRead;
                internal int TestRead; public void W2() => TestRead = 1;
                internal int Both; public void W3() => Both = 1; public int R3() => Both;
                private int _viaHelper; public void W4() => _viaHelper = 1; internal int ForTests() => _viaHelper;
                internal readonly int Initialized = System.Environment.ProcessorCount;
                internal System.IDisposable? Sub; public void W5(System.IDisposable d) => Sub = d;
                internal int TestWrittenToo; public void W6() => TestWrittenToo = 1;
            }
            """);
        var tests = new TestProject("Lib.Tests", """
            public class T {
                public int Check(C c) => c.TestRead + c.Both + c.ForTests() + c.Initialized + (c.Sub is null ? 0 : 1);
                public int Set(C c) { c.TestWrittenToo = 2; return c.TestWrittenToo; }
            }
            """) { Test = true, References = ["Lib"] };
        var r = UnusedTestHost.Run([lib, tests]);
        await Assert.That(r.Unread).IsEquivalentTo(["C::_deadRead"], CollectionOrdering.Matching);
        await Assert.That(r.Unused).IsEquivalentTo(["C::Dead"], CollectionOrdering.Matching);
        await Assert.That(r.TestOnly).IsEquivalentTo(["C::ForTests", "C::Initialized", "C::TestRead", "C::TestWrittenToo", "C::_viaHelper"], CollectionOrdering.Matching);
        await Assert.That(r.State("C::Both")).IsEqualTo("used");
        await Assert.That(r.State("C::TestRead")).IsEqualTo("used-only-by-tests (read only by tests)");
        await Assert.That(r.State("C::_viaHelper")).IsEqualTo("used-only-by-tests (read only by tests via C::ForTests)");
        await Assert.That(r.State("C::ForTests")).IsEqualTo("used-only-by-tests");
        // A lifetime holder that tests read stays rooted by its exclusion: production writes it to keep the object alive.
        await Assert.That(r.State("C::Sub")).StartsWith("rooted: read only by tests, not reported: IDisposable");

        var read = r.Findings.Single(f => f.PlaceKey == "C::TestRead");
        await Assert.That((read.Rule, read.Severity)).IsEqualTo((RuleIds.UsedOnlyByTests, "notice"));
        await Assert.That(read.Message).IsEqualTo("internal field TestRead is written by production code but read only by tests");
        await Assert.That(read.Id).IsEqualTo("used-only-by-tests|Lib/File0.cs|C::TestRead");
        await Assert.That(read.UsedOnlyBy).IsNull();
        await Assert.That(read.Depth).IsNull();
        await Assert.That(r.Findings.Single(f => f.PlaceKey == "C::_viaHelper").Message).IsEqualTo("private field _viaHelper is written by production code but read only by tests (via C::ForTests)");
        await Assert.That(r.Analysis.Summary.UsedOnlyByTests).IsEqualTo(5);
        await Assert.That(r.Analysis.Summary.UnreadMembers).IsEqualTo(1);
    }

    [Test]
    public async Task A_type_passed_to_a_RequiresUnreferencedCode_method_roots_its_members()
    {
        var r = UnusedTestHost.Run("""
            using System.Diagnostics.CodeAnalysis;
            public static class Ser { [RequiresUnreferencedCode("reflection")] public static string Write(object o) => o.ToString()!; }
            internal class Dto { private int _id; internal Dto(int id) { _id = id; } }
            internal class Plain { private int _id; internal Plain(int id) { _id = id; } }
            public class Api {
                public string S() => Ser.Write(new Dto(1));
                public object P() => new Plain(1);
            }
            """);
        await Assert.That(r.Unread).IsEquivalentTo(["Plain::_id"], CollectionOrdering.Matching);
        await Assert.That(r.State("Dto::_id")).StartsWith("rooted: passed to a RequiresUnreferencedCode method");
    }

    [Test]
    public async Task Unread_members_follow_the_rule_settings_and_names_root_them()
    {
        var r = UnusedTestHost.Run([new TestProject("T", UnreadExample)], [], "default", "unread-member.notice=off");
        await Assert.That(r.Unread).IsEmpty();
        var named = UnusedTestHost.Run("public class N { private int _f; public void W() => _f = 1; public object R() => System.Type.GetType(\"N\")!.GetField(\"_f\")!; }");
        await Assert.That(named.Unread).IsEmpty();
        await Assert.That(named.State("N::_f")).StartsWith("rooted: name in a string literal");
    }

    [Test]
    public async Task A_shared_file_is_unread_only_when_no_copy_reads_it()
    {
        const string shim = "namespace Shared { internal class Box { internal int Value; internal Box() { Value = 1; } } }";
        var a = new TestProject("A", shim, "public class UsesBox { public int M() => new Shared.Box().Value; }") { Paths = ["common/Box.cs", "A/Uses.cs"] };
        var b = new TestProject("B", shim, "public class Other { public object M() => new Shared.Box(); }") { Paths = ["common/Box.cs", "B/Other.cs"] };
        var r = UnusedTestHost.Run([a, b]);
        await Assert.That(r.Unread).IsEmpty();
        await Assert.That(r.State("Shared.Box::Value")).IsEqualTo("used");
    }
}