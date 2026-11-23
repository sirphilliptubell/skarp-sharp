using System.Text.Json;
using SkarpSharp.Findings;
using SkarpSharp.Model;
using SkarpSharp.Semantic;

namespace SkarpSharp.Tests.Semantic;

/// <summary>
/// Semantic metrics (coupling, cohesion, access to foreign data and the smell detection strategies) on worked examples
/// and the Microsoft coupling oracle (Metrics.exe 5.6.0 values committed in
/// Oracle/microsoft-coupling-examples.json, measured on tests/oracle/Coupling).
/// </summary>
public class SemanticMetricTests
{
    static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SkarpSharp.slnx"))) dir = dir.Parent;
        return Path.Combine([dir!.FullName, .. parts]);
    }

    [Test]
    public async Task Coupling_worked_examples_match_Microsoft()
    {
        var result = SemanticTestHost.Measure(File.ReadAllText(RepoFile("tests", "oracle", "Coupling", "CouplingExamples.cs")));
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Oracle", "microsoft-coupling-examples.json")));
        var expected = doc.RootElement.GetProperty("types").EnumerateObject().ToList();
        await Assert.That(expected.Count).IsEqualTo(59);
        await Assert.That(result.NotEvaluated).IsEmpty();
        var diffs = new List<string>();
        foreach (var e in expected)
        {
            var t = result.Types.SingleOrDefault(x => x.Name == e.Name);
            if (t is null) { diffs.Add($"{e.Name}: not measured"); continue; }
            int ms = e.Value.GetProperty("microsoft").GetInt32(), ours = e.Value.GetProperty("ours").GetInt32(), all = e.Value.GetProperty("all").GetInt32();
            if (t.CboMicrosoft != ms) diffs.Add($"{e.Name}: microsoft {ms}, skarp {t.CboMicrosoft}");
            if (t.Cbo != ours) diffs.Add($"{e.Name}: ours {ours}, skarp {t.Cbo}");
            if (t.CboAll != all) diffs.Add($"{e.Name}: all {all}, skarp {t.CboAll}");
        }
        await Assert.That(diffs).IsEmpty();
        await Assert.That(result.Types.Count).IsEqualTo(59);
    }

    [Test]
    public async Task Afferent_coupling_example_11()
    {
        var r = SemanticTestHost.Measure("class A { B b = null!; C c = null!; } class B { C c = null!; } class C { }");
        await Assert.That((r.Type("A").Cbo, r.Type("A").Ca)).IsEqualTo((2, 0));
        await Assert.That((r.Type("B").Cbo, r.Type("B").Ca)).IsEqualTo((1, 1));
        await Assert.That((r.Type("C").Cbo, r.Type("C").Ca)).IsEqualTo((0, 2));
    }

    [Test]
    public async Task Coupling_lists_the_counted_types_and_drops_enclosing_and_nested()
    {
        var r = SemanticTestHost.Measure("""
            class Customer { } class Order { }
            class Outer { class Inner { Outer o = null!; Customer c = null!; } Inner i = null!; Order[] orders = null!; System.Collections.Generic.List<Customer>? list; }
            """);
        await Assert.That(r.Type("Outer").CoupledTypes).IsEquivalentTo(["Customer", "Order"], CollectionOrdering.Matching);
        await Assert.That(r.Type("Outer").CboAll).IsEqualTo(3);           // List<T> is framework
        await Assert.That(r.Type("Outer+Inner").CoupledTypes).IsEquivalentTo(["Customer"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Tcc_example_1_direct_and_indirect_pairs()
    {
        var t = SemanticTestHost.Measure("""
            class Counter {
                int a, b, c;
                public void M1() { a++; }
                public void M2() { a--; b++; }
                public void M3() { b = 0; }
                public void M4() { c = 1; }
            }
            """).Type("Counter");
        await Assert.That((t.CohesionMethods, t.CohesionPairs, t.DirectPairs, t.IndirectPairs)).IsEqualTo((4, 6, 2, 1));
        await Assert.That(t.Tcc).IsEqualTo(0.3333);
        await Assert.That(t.Lcc).IsEqualTo(0.5);
    }

    [Test]
    public async Task Tcc_example_2_auto_properties_are_state_and_call_trees_count()
    {
        var t = SemanticTestHost.Measure("""
            class Person {
                public string Name { get; set; } = "";
                public int Age { get; set; }
                public string Greet() => "Hi " + Name;
                public bool IsAdult() => Age >= 18;
                public string Describe() => Greet() + IsAdult();
            }
            """).Type("Person");
        await Assert.That((t.CohesionMethods, t.CohesionPairs, t.DirectPairs, t.IndirectPairs)).IsEqualTo((3, 3, 2, 1));
        await Assert.That(t.Tcc).IsEqualTo(0.6667);
        await Assert.That(t.Lcc).IsEqualTo(1.0);
    }

    [Test]
    public async Task Tcc_example_4_Bieman_Kang_stack_constructor_excluded()
    {
        var t = SemanticTestHost.Measure("""
            class Stack {
                int[] array; int top; int size;
                public Stack(int n) { array = new int[n]; top = 0; size = n; }
                public bool IsEmpty() => top == 0;
                public int Size() => size;
                public int Vtop() => array[top - 1];
                public void Push(int x) { if (top < size) array[top++] = x; }
                public int Pop() => --top;
            }
            """).Type("Stack");
        await Assert.That((t.CohesionMethods, t.CohesionPairs, t.DirectPairs)).IsEqualTo((5, 10, 7));
        await Assert.That(t.Tcc).IsEqualTo(0.7);
        await Assert.That(t.Lcc).IsEqualTo(1.0);
    }

    [Test]
    public async Task Tcc_private_helpers_reached_through_calls_inherited_fields_and_not_applicable_cases()
    {
        var r = SemanticTestHost.Measure("""
            class Base { protected int shared; private int hidden; }
            class Derived : Base {
                int own;
                public void A() { shared++; }
                public void B() { Helper(); }
                private void Helper() { shared--; own++; }
                public void C() { var other = new Derived(); other.own = 1; }   // another object: not a use of own state
            }
            class One { int x; public void M() { x++; } }
            class NoState { public int A() => 1; public int B() => 2; }
            interface I { void M(); void N(); }
            static class S { public static void M() { } public static void N() { } }
            """);
        var d = r.Type("Derived");
        await Assert.That((d.CohesionMethods, d.CohesionPairs, d.DirectPairs)).IsEqualTo((3, 3, 1)); // A-B through Helper and the inherited field
        await Assert.That(r.Type("One").CohesionNote).IsEqualTo("one visible method");
        await Assert.That(r.Type("One").Tcc).IsNull();
        await Assert.That(r.Type("NoState").CohesionNote).IsEqualTo("no visible method uses the type's state");
        await Assert.That(r.Type("I").CohesionNote).IsEqualTo("interface");
        await Assert.That(r.Type("S").CohesionNote).IsEqualTo("static class");
    }

    [Test]
    public async Task Tcc_and_lcc_count_captured_primary_constructor_parameters_as_state()
    {
        // The same class written with fields and with a primary constructor gives the same
        // cohesion (Tcc example 1); before the fix the primary-constructor form had no state at all.
        var r = SemanticTestHost.Measure("""
            class Fields {
                int a, b, c;
                public void M1() { a++; }
                public void M2() { a--; b++; }
                public void M3() { b = 0; }
                public void M4() { c = 1; }
            }
            class Primary(int a, int b, int c) {
                public void M1() { a++; }
                public void M2() { a--; b++; }
                public void M3() { b = 0; }
                public void M4() { c = 1; }
            }
            class Mixed(System.Text.StringBuilder log) {
                int count;
                public void Add() { count++; log.Append('+'); }
                public void Write() { log.Append(count); }
                public void Reset() { count = 0; }
                public string Text() => log.ToString();
            }
            class InitializerOnly(int seed) {
                int value = seed;
                public int A() => value;
                public int B() => value + 1;
            }
            class ShadowedOther(int x) {
                public int A() => x;
                public int B(int x) => x;   // a method parameter of the same name, not the captured one
            }
            """);
        var f = r.Type("Fields");
        var p = r.Type("Primary");
        await Assert.That((p.CohesionMethods, p.CohesionPairs, p.DirectPairs, p.IndirectPairs)).IsEqualTo((4, 6, 2, 1));
        await Assert.That((p.Tcc, p.Lcc)).IsEqualTo((f.Tcc, f.Lcc));
        await Assert.That(p.Tcc).IsEqualTo(0.3333);
        await Assert.That(p.Lcc).IsEqualTo(0.5);
        // Add-Write (count, log), Add-Reset (count), Add-Text (log), Write-Reset (count), Write-Text (log): 5 of 6 pairs.
        var m = r.Type("Mixed");
        await Assert.That((m.CohesionMethods, m.CohesionPairs, m.DirectPairs)).IsEqualTo((4, 6, 5));
        await Assert.That(m.Tcc).IsEqualTo(0.8333);
        await Assert.That(m.Lcc).IsEqualTo(1.0);
        // A parameter used only in a field initializer is not captured: the field is the state.
        await Assert.That((r.Type("InitializerOnly").CohesionMethods, r.Type("InitializerOnly").CohesionPairs, r.Type("InitializerOnly").DirectPairs)).IsEqualTo((2, 1, 1));
        var sh = r.Type("ShadowedOther");
        await Assert.That((sh.CohesionMethods, sh.CohesionPairs, sh.DirectPairs)).IsEqualTo((2, 1, 0));
    }

    [Test]
    public async Task Atfd_through_a_captured_primary_constructor_parameter_equals_the_field_form()
    {
        // The captured parameter is own state (never foreign data); what is read through it counts as through a field.
        var r = SemanticTestHost.Measure("""
            class Customer { public string Name { get; set; } = ""; public int Age { get; set; } }
            class WithField { readonly Customer c = new(); string A() => c.Name; int B() => c.Age; }
            class WithPrimary(Customer c) { string A() => c.Name; int B() => c.Age; }
            """);
        await Assert.That(r.Type("WithPrimary").Atfd).IsEqualTo(2);
        await Assert.That(r.Type("WithPrimary").ForeignData).IsEquivalentTo(r.Type("WithField").ForeignData!, CollectionOrdering.Matching);
        await Assert.That(r.Type("WithPrimary").ForeignData).IsEquivalentTo(["Customer.Age", "Customer.Name"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Atfd_worked_examples()
    {
        var r = SemanticTestHost.Measure("""
            class Address { public string Zip { get; set; } = ""; }
            class Customer { public string Name { get; set; } = ""; public Address Address { get; set; } = new(); }
            class Order { public decimal Total { get; set; } public Customer Customer { get; set; } = new(); }
            class Report {
                int pages;
                void Print(Order o) {
                    var n = o.Customer.Name;
                    var t = o.Total;
                    var z = o.Customer.Address.Zip;
                    pages++;
                }
            }
            class Base { protected int x; }
            class Other { public int y; public const int K = 1; }
            class Derived : Base { int M() => x + new Other().y + Other.K; }
            class Framework { int M(System.Collections.Generic.List<int> list, Order o) { string s = o.Customer.Name.Trim(); return list.Count; } }
            """);
        await Assert.That(r.Type("Report").Atfd).IsEqualTo(5);
        await Assert.That(r.Type("Report").ForeignData).IsEquivalentTo(["Address.Zip", "Customer.Address", "Customer.Name", "Order.Customer", "Order.Total"], CollectionOrdering.Matching);
        await Assert.That(r.Type("Derived").Atfd).IsEqualTo(1);   // Other.y; own hierarchy and constants excluded
        await Assert.That(r.Type("Framework").Atfd).IsEqualTo(2);  // Order.Customer, Customer.Name
        await Assert.That(r.Type("Framework").AtfdAll).IsEqualTo(3); // and List<T>.Count
    }

    [Test]
    public async Task Atfd_is_distinct_over_the_type_member_sum_kept_and_wmc_sums_member_cyclomatic()
    {
        var r = SemanticTestHost.Measure("""
            class P { public int X { get; set; } }
            class C {
                int A(P p) { if (p.X > 0) return 1; return p.X; }
                int B(P p) => p.X > 1 && p.X < 5 ? 1 : 0;
                int Prop { get { return 0; } }
            }
            """);
        var c = r.Type("C");
        await Assert.That(c.Atfd).IsEqualTo(1);                   // P.X, counted once for the type (variant B)
        await Assert.That(c.AtfdMemberSum).IsEqualTo(2);          // P.X in A, P.X in B (variant A)

        var same = SemanticTestHost.Measure("""
            namespace A { public class Opt { public int V { get; set; } } }
            namespace B { public class Opt { public int V { get; set; } } }
            class C { int M(A.Opt a, B.Opt b) => a.V + b.V; }
            """);
        await Assert.That(same.Type("C").Atfd).IsEqualTo(2);      // equal short names of different types stay apart
        await Assert.That(c.Wmc).IsEqualTo(2 + 3 + 1);
    }

    static string GodClassShape()
    {
        var methods = string.Concat(Enumerable.Range(1, 12).Select(i => $$"""
                public void M{{i}}(Order order, User user, Config config) {
                    if (order.Status == 1) { _f{{i}} += order.Total; }
                    if (user.Role == 2) { _f{{i}} += user.Level; }
                    if (config.Mode == 3) { _f{{i}} += config.Limit; }
                    if (order.Total > 0 && user.Name != null) { _f{{i}} += 1; }
                }
                int _f{{i}};

            """));
        return $$"""
            class Order { public int Status { get; set; } public int Total { get; set; } }
            class User { public int Role { get; set; } public int Level { get; set; } public string? Name { get; set; } }
            class Config { public int Mode { get; set; } public int Limit { get; set; } }
            class Hub {
            {{methods}}
            }
            """;
    }

    [Test]
    public async Task God_class_example_2_fires_under_every_rule_set()
    {
        foreach (var preset in GodClassPresets.Sets.Select(s => s.Id))
        {
            var r = SemanticTestHost.Measure(GodClassShape(), true, $"god-class.preset={preset}");
            var hub = r.Type("Hub");
            await Assert.That(hub.Wmc).IsEqualTo(72);
            await Assert.That(hub.Atfd).IsEqualTo(7);             // distinct over the class (detection strategies example 2)
            await Assert.That(hub.AtfdMemberSum).IsEqualTo(84);   // 7 per method, summed over 12 methods (variant A)
            await Assert.That((hub.CohesionPairs, hub.DirectPairs)).IsEqualTo((66, 0));
            var f = await Assert.That(r.Findings).HasSingleItem(x => x.Rule == RuleIds.GodClass);
            await Assert.That(f.Severity).IsEqualTo("notice");
            await Assert.That(f.Id).IsEqualTo($"god-class|Test.cs|Hub");
            await Assert.That(f.Message).Contains($"God Class {preset}");
        }
        var off = SemanticTestHost.Measure(GodClassShape(), true, "god-class.notice=off");
        await Assert.That(off.Findings).DoesNotContain(x => x.Rule == RuleIds.GodClass);
    }

    static SemanticType Shape(int wmc, int atfd, int pairs, int direct) =>
        new() { Name = "T", Kind = "class", Path = "T.cs", Wmc = wmc, Atfd = atfd, CohesionPairs = pairs, DirectPairs = direct };

    [Test]
    [Arguments("lanza-marinescu-2006", 47, 6, 3, 0, true)]
    [Arguments("lanza-marinescu-2006", 46, 6, 3, 0, false)]  // WMC at least 47
    [Arguments("lanza-marinescu-2006", 47, 5, 3, 0, false)]  // ATFD over 5
    [Arguments("lanza-marinescu-2006", 47, 6, 3, 1, false)]  // TCC exactly 1/3 is not below one third (example 1)
    [Arguments("lanza-marinescu-2006", 99, 9, 100, 33, true)] // 33/100 is below 1/3 (0.33 would not be)
    [Arguments("iplasma", 20, 5, 3, 0, false)]               // WMC over 20
    [Arguments("iplasma", 21, 5, 3, 0, true)]
    [Arguments("iplasma", 21, 4, 3, 0, false)]               // ATFD over 4
    [Arguments("atfd-2", 47, 3, 3, 0, true)]
    [Arguments("atfd-2", 47, 2, 3, 0, false)]
    public async Task God_class_boundaries(string preset, int wmc, int atfd, int pairs, int direct, bool fires)
    {
        var rule = RuleSettings.Resolve("default", null, [$"god-class.preset={preset}"])[RuleIds.GodClass];
        await Assert.That(GodClassRule.Judge(Shape(wmc, atfd, pairs, direct), rule) is not null).IsEqualTo(fires);
    }

    [Test]
    public async Task God_class_needs_a_defined_tcc()
    {
        var rule = RuleSettings.Resolve("default", null, [])[RuleIds.GodClass];
        await Assert.That(GodClassRule.Judge(new SemanticType { Name = "T", Wmc = 200, Atfd = 50, CohesionNote = "one visible method" }, rule)).IsNull();
    }

    [Test]
    public async Task High_coupling_follows_the_preset_metric()
    {
        var source = string.Concat(Enumerable.Range(1, 22).Select(i => $"class T{i} {{ }} ")) + "class Big { " +
            string.Concat(Enumerable.Range(1, 22).Select(i => $"T{i} f{i} = null!; ")) + "}";
        var r = SemanticTestHost.Measure(source);
        var f = await Assert.That(r.Findings).HasSingleItem(x => x.Rule == RuleIds.HighCoupling);
        await Assert.That((f.Value, f.Threshold, f.Metric)).IsEqualTo((22.0, 20.0, "cbo"));
        await Assert.That(f.Id).IsEqualTo("high-coupling|Test.cs|Big");
        await Assert.That(SemanticTestHost.Measure(source, true, "high-coupling.notice=30").Findings).IsEmpty();
        var ms = RuleSettings.Resolve("microsoft", null, [])[RuleIds.HighCoupling];
        await Assert.That(ms.Notice!.Describe()).IsEqualTo("cboMicrosoft over 95");
    }

    [Test]
    public async Task Unresolved_symbols_make_a_type_not_evaluated_other_errors_do_not()
    {
        var r = SemanticTestHost.Measure("""
            public class UsesMissing { public MissingType? Field; public int M() => Helper.Compute(1); }
            public class TypeError { public int N() { int x = "text"; return x; } }
            public class Clean { public TypeError Make() => new TypeError(); }
            public class Outer { public class Bad { MissingToo? m; } public Clean c = null!; }
            """, generators: false);
        await Assert.That(r.NotEvaluated.Select(t => t.Name)).IsEquivalentTo(["UsesMissing", "Outer+Bad"], CollectionOrdering.Matching);
        await Assert.That(r.NotEvaluated[0].Diagnostic).StartsWith("CS0246");
        await Assert.That(r.NotEvaluated[0].Reason).IsEqualTo("unresolved symbols (source generators not run: --no-generators)");
        await Assert.That(r.Type("Clean").Cbo).IsEqualTo(1);
        await Assert.That(r.Type("TypeError").Cbo).IsEqualTo(0);
        await Assert.That(r.Type("Outer").Cbo).IsEqualTo(1);   // the nested type's error does not touch the outer type
        await Assert.That(r.Type("Clean").Ca).IsEqualTo(1);
    }

    [Test]
    public async Task Unbound_generic_in_typeof_is_not_an_unresolved_symbol()
    {
        // A real-world case: typeof(IDictionary<,>) has placeholder (error) type arguments but no diagnostic.
        var r = SemanticTestHost.Measure("class G<T> { } class C { System.Type M() => typeof(G<>); System.Type N() => typeof(System.Collections.Generic.IDictionary<,>); System.Type O() => typeof(System.Nullable<>); }");
        await Assert.That(r.NotEvaluated).IsEmpty();
        await Assert.That(r.Type("C").CoupledTypes).IsEquivalentTo(["G<T>"], CollectionOrdering.Matching);
        await Assert.That(r.Type("C").CboAll).IsEqualTo(4);   // G<T>, Type, IDictionary<TKey, TValue>, Nullable<T>
    }

    [Test]
    public async Task Enums_and_delegates_are_not_measured_but_count_as_targets()
    {
        var r = SemanticTestHost.Measure("enum Color { Red } delegate void D(); class C { Color c; D? d; }");
        await Assert.That(r.Types.Select(t => t.Name)).IsEquivalentTo(["C"], CollectionOrdering.Matching);
        await Assert.That(r.Type("C").Cbo).IsEqualTo(2);
    }
}