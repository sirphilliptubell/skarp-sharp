using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using SkarpSharp.Baselines;
using SkarpSharp.Findings;
using SkarpSharp.Model;
using SkarpSharp.Semantic;

namespace SkarpSharp.Tests.Semantic;

/// <summary>One in-memory project of a namespace-cycle test: sources (<c>Name/File{i}.cs</c>), referenced projects, and
/// the file names of the sources the file selection drops (test files, exclude globs).</summary>
internal sealed record NsProject(string Name, params string[] Sources)
{
    public string[] References { get; init; } = [];
    public string[] Excluded { get; init; } = [];
    public OutputKind Kind { get; init; } = OutputKind.DynamicallyLinkedLibrary;
}

/// <summary>Compiles in-memory projects in order, runs the production reference walk with namespace references on, and
/// the namespace-cycle analysis and rule over them.</summary>
internal static class NsHost
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "skarp-ns-unit");

    static readonly Lazy<MetadataReference[]> Framework = new(() =>
        [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
            .Select(p => MetadataReference.CreateFromFile(p))]);

    public static (NamespaceSummary Summary, List<Finding> Findings, List<ProjectFacts> Facts) Run(NsProject[] projects, bool keepGenerated = false, bool unused = false)
    {
        var images = new Dictionary<string, MetadataReference>(StringComparer.Ordinal);
        var assemblies = new HashSet<string>(projects.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var facts = new List<ProjectFacts>();
        foreach (var p in projects)
        {
            var parse = new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.Parse);
            var trees = p.Sources.Select((s, i) => CSharpSyntaxTree.ParseText(s, parse, Path.Combine(Root, p.Name, $"File{i}.cs"))).ToList();
            var options = new CSharpCompilationOptions(p.Kind, nullableContextOptions: NullableContextOptions.Enable);
            var comp = CSharpCompilation.Create(p.Name, trees, [.. Framework.Value, .. p.References.Select(r => images[r])], options);
            var input = new CompileInput
            {
                Record = new RecordedProject { Project = Path.Combine(Root, p.Name, p.Name + ".csproj"), TargetFramework = "net10.0", AssemblyName = p.Name, AssetsFile = "", RoslynTargetsPath = "", Arguments = [] },
                AssemblyName = p.Name, SourcePaths = [.. trees.Select(t => t.FilePath)], References = [], AnalyzerPaths = [], ParseOptions = parse, Options = options,
                Measure = true, Walk = true,
            };
            facts.Add(ReferenceIndex.Extract(new CompiledProject(comp, new HashSet<SyntaxTree>(), input, 0), new ReferenceOptions
            {
                Root = Root, KeptFile = path => !p.Excluded.Contains(Path.GetFileName(path)), SolutionAssemblies = assemblies,
                Namespaces = true, Candidates = unused,
            }));
            using var ms = new MemoryStream();
            images[p.Name] = comp.Emit(ms, options: new EmitOptions(metadataOnly: true)).Success ? MetadataReference.CreateFromImage(ms.ToArray()) : comp.ToMetadataReference();
        }
        var summary = NamespaceCycles.Analyze(facts, keepGenerated);
        var findings = NamespaceCycles.Findings(summary, RuleSettings.Resolve("default", null, [])[RuleIds.NamespaceCycle]);
        FindingEngine.AssignIds(findings);
        return (summary, findings, facts);
    }

    public static (NamespaceSummary Summary, List<Finding> Findings, List<ProjectFacts> Facts) Run(params string[] sources) => Run([new NsProject("App", sources)]);

    public static string Edges(this NamespaceComponent c) =>
        string.Join(", ", c.Dependencies.Select(d => $"{d.From}->{d.To} {d.Weight}"));
}

/// <summary>
/// The worked examples of namespace cycle detection (written before the implementation),
/// the graph algorithms on their own, and the reference rules of the namespace walk (what counts, sources, sites).
/// </summary>
public class NamespaceCycleTests
{
    // ---- worked examples ------------------------------------------------------------------------------------------
    [Test]
    public async Task Example_1_mutual_pair()
    {
        var (s, findings, _) = NsHost.Run("""
            namespace A { class A1 { B.B1 x; B.B2 y; }  class A2 { B.B1 z; } }
            namespace B { class B1 { A.A1 back; } class B2 { } }
            """);
        var c = await Assert.That(s.Components).HasSingleItem();
        await Assert.That(c.Members).IsEquivalentTo(["A", "B"], CollectionOrdering.Matching);
        await Assert.That(c.Edges()).IsEqualTo("A->B 3, B->A 1");
        await Assert.That(c.ShortestCycle).IsEquivalentTo(["A", "B", "A"], CollectionOrdering.Matching);
        var m = await Assert.That(c.MutualPairs).HasSingleItem();
        await Assert.That((m.From, m.To, m.Weight, m.OtherWeight)).IsEqualTo(("B", "A", 1, 3));
        await Assert.That(string.Join(", ", c.Cut.Select(e => $"{e.From}->{e.To} {e.Weight}"))).IsEqualTo("B->A 1");
        var f = await Assert.That(findings).HasSingleItem();
        await Assert.That((f.Id, f.Severity, f.Value)).IsEqualTo(("namespace-cycle||namespace A (App)", "warning", 2.0));
        await Assert.That(f.Message).StartsWith("cheapest cut: B -> A (1 type pair: B1 -> A1); cycle of 2 namespaces: A, B; shortest cycle A -> B -> A");
        await Assert.That((f.Path, f.Line)).IsEqualTo(("App/File0.cs", 2));   // the first reference of the cut
    }

    [Test]
    public async Task Example_2_three_cycle()
    {
        var (s, findings, _) = NsHost.Run("""
            namespace A { class A1 { B.B1 b; } class A2 { B.B1 b; } }
            namespace B { class B1 { C.C1 c; } }
            namespace C { class C1 { A.A1 a1; A.A2 a2; } class C2 { A.A1 a; } }
            """);
        var c = await Assert.That(s.Components).HasSingleItem();
        await Assert.That(c.Edges()).IsEqualTo("A->B 2, B->C 1, C->A 3");
        await Assert.That(c.MutualPairs).IsEmpty();
        await Assert.That(c.ShortestCycle).IsEquivalentTo(["A", "B", "C", "A"], CollectionOrdering.Matching);
        await Assert.That(string.Join(", ", c.Cut.Select(e => $"{e.From}->{e.To}"))).IsEqualTo("B->C");
        await Assert.That((findings[0].Id, findings[0].Value)).IsEqualTo(("namespace-cycle||namespace A (App)", 3.0));
    }

    static string Pairs(string from, string to, int n) =>
        string.Concat(Enumerable.Range(1, n).Select(i => $"class {from}{to}{i} {{ {to}.{to}{from}T{i} x; }} "));

    static string Targets(string ns, string from, int n) =>
        string.Concat(Enumerable.Range(1, n).Select(i => $"class {ns}{from}T{i} {{ }} "));

    [Test]
    public async Task Example_3_giant_component_with_a_cheapest_cut()
    {
        // Each unit of weight is one distinct type pair: class AB1 { B.BAT1 x; } and so on.
        (string F, string T, int W)[] edges = [("A", "B", 5), ("B", "A", 1), ("B", "C", 4), ("C", "D", 3), ("D", "B", 2), ("C", "A", 6)];
        var code = string.Join("\n", "ABCD".Select(ns =>
            $"namespace {ns} {{ " + string.Concat(edges.Where(e => e.F == ns.ToString()).Select(e => Pairs(e.F, e.T, e.W)))
            + string.Concat(edges.Where(e => e.T == ns.ToString()).Select(e => Targets(e.T, e.F, e.W))) + "}"));
        var (s, findings, _) = NsHost.Run(code);
        var c = await Assert.That(s.Components).HasSingleItem();
        await Assert.That(c.Edges()).IsEqualTo("A->B 5, B->A 1, B->C 4, C->A 6, C->D 3, D->B 2");
        await Assert.That(c.Size).IsEqualTo(4);
        await Assert.That(c.ShortestCycle).IsEquivalentTo(["A", "B", "A"], CollectionOrdering.Matching);
        await Assert.That(string.Join(", ", c.Cut.Select(e => $"{e.From}->{e.To} {e.Weight}"))).IsEqualTo("B->A 1, B->C 4");
        await Assert.That((c.CutTypePairs, c.CutComplete)).IsEqualTo((5, true));
        var f = await Assert.That(findings).HasSingleItem();
        await Assert.That((f.Id, f.Value)).IsEqualTo(("namespace-cycle||namespace A (App)", 4.0));
        await Assert.That(f.Message).StartsWith("cheapest cuts: B -> A (1 type pair: BA1 -> ABT1), B -> C (4 type pairs: BC1 -> CBT1, BC2 -> CBT2, and 2 more) (suggested cut: 2 dependencies, 5 type pairs);");
    }

    [Test]
    public async Task Example_4_same_namespace_in_two_assemblies()
    {
        var (s, findings, _) = NsHost.Run([
            new NsProject("Lib1", "namespace Common { class C1 { Shared.S1 s; } }\nnamespace Shared { class S1 { } }"),
            new NsProject("Lib2", "namespace Shared { class S2 { Common.C1 c; App.P1 p; } }\nnamespace App    { class P1 { Shared.S2 s; } }") { References = ["Lib1"] },
        ]);
        var c = await Assert.That(s.Components).HasSingleItem();
        await Assert.That(c.Members).IsEquivalentTo(["App", "Shared (Lib2)"], CollectionOrdering.Matching);
        await Assert.That(c.Assemblies).IsEquivalentTo(["Lib2"], CollectionOrdering.Matching);
        await Assert.That(string.Join(", ", c.Cut.Select(e => $"{e.From}->{e.To} {e.Weight}"))).IsEqualTo("App->Shared (Lib2) 1");
        await Assert.That((await Assert.That(findings).HasSingleItem()).Id).IsEqualTo("namespace-cycle||namespace App (Lib2)");
        await Assert.That(s.Dependencies).IsEqualTo(4);   // Common -> Shared (Lib1) is no cycle with Shared (Lib2) -> Common
    }

    const string GeneratedBridge = """
        // <auto-generated/>
        namespace Gen { public sealed class PackageAttribute : System.Attribute { } }
        """;

    const string HandBridge = """
        namespace Runtime { [Gen.Package] public class Doc { } }
        namespace Gen { public class Tool { Runtime.Doc d; } }
        """;

    [Test]
    public async Task Example_5_generated_target_bridge_is_dropped_by_default()
    {
        var (s, findings, _) = NsHost.Run(GeneratedBridge, HandBridge);
        await Assert.That(s.Components).IsEmpty();
        await Assert.That(findings).IsEmpty();
        await Assert.That((s.GeneratedTargetReferences, s.GeneratedTargetTypePairs, s.GeneratedTargetsKept)).IsEqualTo((1, 1, false));
        await Assert.That((s.SourceFiles, s.GeneratedFilesExcluded)).IsEqualTo((1, 1));
    }

    [Test]
    public async Task Example_5_generated_target_bridge_kept_with_the_opt_in()
    {
        var (s, findings, _) = NsHost.Run([new NsProject("App", GeneratedBridge, HandBridge)], keepGenerated: true);
        var c = await Assert.That(s.Components).HasSingleItem();
        await Assert.That(c.Members).IsEquivalentTo(["Gen", "Runtime"], CollectionOrdering.Matching);
        await Assert.That(string.Join(", ", c.Cut.Select(e => $"{e.From}->{e.To}"))).IsEqualTo("Gen->Runtime");
        await Assert.That((await Assert.That(findings).HasSingleItem()).Id).IsEqualTo("namespace-cycle||namespace Gen (App)");
        await Assert.That(s.Notes).Contains(n => n.Contains("are kept", StringComparison.Ordinal));
    }

    // ---- graph algorithms -------------------------------------------------------------------------------------------

    static CycleGraph Graph(int n, params (int From, int To, int W)[] edges)
    {
        var g = new CycleGraph(n);
        foreach (var (f, t, w) in edges) g.Add(f, t, w);
        return g;
    }

    [Test]
    public async Task Tarjan_finds_components_of_two_or_more()
    {
        // 0<->1, 2->3->4->2, 5 alone, 1->2 joins nothing back.
        var g = Graph(6, (0, 1, 1), (1, 0, 1), (1, 2, 1), (2, 3, 1), (3, 4, 1), (4, 2, 1), (4, 5, 1));
        await Assert.That(g.Components().Select(c => string.Join(",", c))).IsEquivalentTo(["0,1", "2,3,4"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Tarjan_handles_a_long_chain_without_recursion()
    {
        const int n = 200_000;
        var g = new CycleGraph(n);
        for (int i = 0; i < n - 1; i++) g.Add(i, i + 1, 1);
        g.Add(n - 1, 0, 1);
        await Assert.That((await Assert.That(g.Components()).HasSingleItem()).Count).IsEqualTo(n);
    }

    [Test]
    public async Task Greedy_cut_example_3_and_minimality()
    {
        // A=0, B=1, C=2, D=3.
        var g = Graph(4, (0, 1, 5), (1, 0, 1), (1, 2, 4), (2, 3, 3), (3, 1, 2), (2, 0, 6));
        var members = await Assert.That(g.Components()).HasSingleItem();
        await Assert.That(g.ShortestCycle(members)).IsEquivalentTo([0, 1, 0], CollectionOrdering.Matching);
        var (cut, complete) = g.GreedyCut(members, 200);
        await Assert.That(complete).IsTrue();
        await Assert.That(cut).IsEquivalentTo([(1, 0), (1, 2)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Greedy_cut_drops_edges_the_minimality_pass_finds_redundant()
    {
        // Two triangles sharing the light edge 1->2 would both be cut there; a heavy chord is cut first by the shortest
        // cycle and becomes redundant once the shared edge goes: 0->1 (5), 1->2 (1), 2->0 (5), 1->0 (2), 2->3 (5), 3->1 (5).
        var g = Graph(4, (0, 1, 5), (1, 2, 1), (2, 0, 5), (1, 0, 2), (2, 3, 5), (3, 1, 5));
        var (cut, complete) = g.GreedyCut((await Assert.That(g.Components()).HasSingleItem()), 200);
        await Assert.That(complete).IsTrue();
        await Assert.That(cut).DoesNotContain(e => !IsNeeded(g, cut, e));
    }

    static bool IsNeeded(CycleGraph g, List<(int From, int To)> cut, (int From, int To) edge)
    {
        var rest = cut.Where(e => e != edge).ToHashSet();
        return g.ShortestCycle(Enumerable.Range(0, g.Count).ToList(), rest.Select(e => (e.From, e.To)).ToHashSet()) is not null;
    }

    [Test]
    public async Task Greedy_cut_stops_at_its_cap()
    {
        var g = Graph(3, (0, 1, 1), (1, 0, 1), (1, 2, 1), (2, 1, 1), (0, 2, 1), (2, 0, 1));
        var (cut, complete) = g.GreedyCut([0, 1, 2], 1);
        await Assert.That(complete).IsFalse();
        await Assert.That(cut).HasSingleItem();
    }

    // ---- what counts as a namespace reference -----------------------------------------------------------------------

    [Test]
    public async Task Using_directives_and_crefs_do_not_count()
    {
        var (s, _, _) = NsHost.Run("""
            using B;
            namespace A
            {
                /// <summary>See <see cref="B.B1"/>.</summary>
                class A1 { }
            }
            namespace B { class B1 { A.A1 a; } }
            """);
        await Assert.That(s.Dependencies).IsEqualTo(1);   // B -> A only
        await Assert.That(s.Components).IsEmpty();
    }

    [Test]
    public async Task Members_calls_and_nested_types_count_for_the_outermost_types()
    {
        var (s, _, _) = NsHost.Run("""
            namespace A { partial class A1 { class Inner { void M() => B.B1.Make(); } } }
            namespace B { class B1 { public static int Make() => A.A1.Value; } }
            namespace A { partial class A1 { public const int Value = 1; } }
            """);
        var c = await Assert.That(s.Components).HasSingleItem();
        await Assert.That(c.Edges()).IsEqualTo("A->B 1, B->A 1");
        await Assert.That($"{c.Dependencies[0].Examples[0].FromType} -> {c.Dependencies[0].Examples[0].ToType}").IsEqualTo("A1 -> B1");
    }

    [Test]
    public async Task Excluded_files_are_not_sources_but_their_types_are_targets()
    {
        var (s, _, _) = NsHost.Run([new NsProject("App", "namespace A { class A1 { B.B1 b; } }", "namespace B { class B1 { A.A1 a; } }") { Excluded = ["File1.cs"] }]);
        await Assert.That((s.SourceFiles, s.TestOrExcludedFiles, s.Dependencies)).IsEqualTo((1, 1, 1));
        await Assert.That(s.Components).IsEmpty();
    }

    [Test]
    public async Task Top_level_statements_are_the_global_namespace()
    {
        var (s, _, _) = NsHost.Run([new NsProject("App", "System.Console.WriteLine(A.A1.Run());", "namespace A { public class A1 { public static int Run() => 1; } }")
            { Kind = OutputKind.ConsoleApplication }]);
        await Assert.That((s.Dependencies, s.Namespaces)).IsEqualTo((1, 2));
    }

    [Test]
    public async Task Partial_type_headers_count_once_at_the_first_declaration()
    {
        // The base list sits in File1 but the header is used at the first declaration in path order (File0), whichever
        // tree the parallel walk reaches first.
        for (int run = 0; run < 5; run++)
        {
            var (s, _, _) = NsHost.Run("namespace A { partial class A1 { } }", "namespace A { partial class A1 : B.B1 { } }", "namespace B { public class B1 { A.A1 a; } }");
            var c = await Assert.That(s.Components).HasSingleItem();
            var ab = c.Dependencies.Single(d => d.From == "A");
            await Assert.That((ab.Examples[0].Path, ab.Examples[0].Line)).IsEqualTo(("App/File0.cs", 1));
        }
    }

    [Test]
    public async Task Unused_code_facts_do_not_change_with_namespace_references()
    {
        const string code = """
            namespace A { internal class A1 { private B.B1 _b = new(); private void Dead() { } internal int Live() => _b.Get(); } }
            namespace B { internal class B1 { internal int Get() => new A.A1().Live(); } }
            """;
        var plain = UnusedTestHost.Run(code);
        var (_, _, facts) = NsHost.Run([new NsProject("T", code)], unused: true);
        await Assert.That(facts[0].Edges.OrderBy(e => e.ToString(), StringComparer.Ordinal)).IsEquivalentTo(plain.Facts[0].Edges.OrderBy(e => e.ToString(), StringComparer.Ordinal), CollectionOrdering.Matching);
        await Assert.That(facts[0].Candidates.Select(c => c.Id)).IsEquivalentTo(plain.Facts[0].Candidates.Select(c => c.Id), CollectionOrdering.Matching);
    }

    // ---- identity and baseline -----------------------------------------------------------------------------------

    [Test]
    public async Task Pathless_ids_round_trip_through_the_semantic_baseline()
    {
        var (s, findings, _) = NsHost.Run("namespace A { class A1 { B.B1 b; } }\nnamespace B { class B1 { A.A1 a; } }");
        var settings = RuleSettings.Resolve("default", null, []);
        var result = new SemanticResult { Run = new SemanticRunInfo { Findings = settings.ToSemanticInfo(), Targets = "highest" }, Findings = findings, Namespaces = s };
        var text = SemanticBaseline.Write(result, "test", "fp", null);
        await Assert.That(text).Contains("\"repository\":").And.Contains("{ \"rule\": \"namespace-cycle\", \"place\": \"namespace A (App)\", \"value\": 2 }");
        var baseline = SemanticBaseline.Parse(text, "b.json");
        var summary = SemanticBaseline.Compare(findings, baseline, "b.json", "fp", new HashSet<string>(), []);
        await Assert.That((summary.New, summary.Known, summary.Worsened, summary.Fixed)).IsEqualTo((0, 1, 0, 0));

        // A growing cycle is worsened.
        var (_, grown, _) = NsHost.Run("namespace A { class A1 { B.B1 b; } }\nnamespace B { class B1 { C.C1 c; } }\nnamespace C { class C1 { A.A1 a; } }");
        var worse = SemanticBaseline.Compare(grown, baseline, "b.json", "fp", new HashSet<string>(), []);
        await Assert.That((worse.New, worse.Worsened)).IsEqualTo((0, 1));
        await Assert.That(grown[0].BaselineState).IsEqualTo("worsened");
    }

    [Test]
    public async Task The_rule_switches_off_and_can_be_a_notice()
    {
        var (s, _, _) = NsHost.Run("namespace A { class A1 { B.B1 b; } }\nnamespace B { class B1 { A.A1 a; } }");
        await Assert.That(NamespaceCycles.Findings(s, RuleSettings.Resolve("default", null, [], disabledRules: [RuleIds.NamespaceCycle])[RuleIds.NamespaceCycle])).IsEmpty();
        await Assert.That(NamespaceCycles.Findings(s, RuleSettings.Resolve("default", null, ["namespace-cycle.warning=off"])[RuleIds.NamespaceCycle])).IsEmpty();
    }
}