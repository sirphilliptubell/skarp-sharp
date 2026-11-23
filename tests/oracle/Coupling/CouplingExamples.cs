// Worked examples for class coupling (one namespace per example). Oracle input: the expected values per preset are in
// tools/oracle/microsoft-coupling-examples.json (Microsoft column measured with Microsoft.CodeAnalysis.Metrics 5.6.0,
// see tools/oracle/README.md) and in SemanticMetricTests (ours and cboAll).
using System;
using System.Collections.Generic;
using System.Linq;

namespace Ex1
{
    interface I { }
    class C1 { }
    class C2<T> { }
    class C3 : C2<C1>, I { }
}

namespace Ex2
{
    class C
    {
        C1 M1(C2 s, C3 o) { return null; }
        object M2(C1 c) { C2 c2 = C4.MyC2; return (C3)null; }
    }
    class C1 { }
    class C2 { }
    class C3 : C1 { }
    class C4 { public static C2 MyC2 = null; }
}

namespace Ex3
{
    class A { }
    class B { }
    class C { void M() { IEnumerable<A> a = null; IEnumerable<B> b = null; new object[] { a, b }.ToString(); } }
    class G<T> { }
    class D1 { }
    class D2 { }
    class E { void M() { G<int> a = null; G<string> b = null; G<D1> c = null; G<G<D2>> d = null; new object[] { a, b, c, d }.ToString(); } }
}

namespace Ex4
{
    class A1 : System.Attribute { }
    [A1] class A2 { }
    class C1
    {
        [CAttr(C2.MyConst)]
        [return: CAttr(C3.MyConst)]
        void M([CAttr(C4.MyConst)] int p) { }
    }
    class CAttr : System.Attribute { public CAttr(string s) { } }
    class C2 { public const string MyConst = nameof(MyConst); }
    class C3 { public const string MyConst = nameof(MyConst); }
    class C4 { public const string MyConst = nameof(MyConst); }
}

namespace Ex5
{
    [System.Runtime.CompilerServices.CompilerGenerated] class A { }
    [System.CodeDom.Compiler.GeneratedCode("g", "1")] class B { }
    class C { void M() { A a = null; B b = null; new object[] { a, b }.ToString(); } }
    class K1 { }
    class K2 { public K2(K1 a) { } }
    class L { void M() { K1 a = new K1(); new System.Action(() => { K2 b = new K2(a); })(); } }
}

namespace Ex6
{
    class Customer { }
    class Order { }
    class D1 { Customer f; }
    class D2 { Order P { get; set; } }
    class D3 { Customer this[Order o] => null; }
    class D4 { event System.EventHandler<Order> Changed; }
}

namespace Ex7
{
    class Customer { }
    class Order { }
    struct Money { }
    class A1 { Customer[] items = null!; }
    class A2 { Customer? maybe; }
    class A3 { Money? price; }
    class A4 { (Customer, Order) pair; }
    class A5 { void M(dynamic d) { d.Run(); } }
}

namespace Ex8
{
    class Customer { }
    static class Runner { public static void Run<T>() { } public static int Twice(this Customer c) => 2; }
    class B1 { void M() => Runner.Run<Customer>(); }
    class B2 { object M() => typeof(Customer); }
    class B3 { int M(Customer c) => c.Twice(); }
    class B4 { void M() { System.Console.WriteLine("x"); } }
}

namespace Ex9
{
    class Customer { }
    class Order { }
    class Node { Node next = null!; Node M(Node n) => n; }
    class Outer { class Inner { Outer o = null!; } Inner i = null!; }
    partial class P { Customer c = null!; }
    partial class P { Order o = null!; }
}

namespace Ex10
{
    class F { decimal d; System.DateTime t; string s = ""; object o = ""; int i; System.Threading.Tasks.Task k = null!; }
}

namespace Ex11
{
    class A { B b = null!; C c = null!; }
    class B { C c = null!; }
    class C { }
}
