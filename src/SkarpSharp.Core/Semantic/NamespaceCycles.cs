using System.Globalization;
using SkarpSharp.Findings;
using SkarpSharp.Model;

namespace SkarpSharp.Semantic;

/// <summary>
/// Namespace dependency cycles over the namespace references of every walked project.
/// Nodes are (assembly, namespace), parent and child namespaces
/// separate; edge weight = distinct (source type, target type) pairs; references to types declared only in generated
/// code are dropped unless kept. Each strongly connected component of two or more nodes gets its shortest
/// cycle, its mutual pairs and a suggested cut, and becomes one <c>namespace-cycle</c> finding.
/// </summary>
public static class NamespaceCycles
{
    /// <summary>Cap of the greedy cut search per component.</summary>
    public const int MaxCutEdges = 200;
    /// <summary>Example references kept per dependency.</summary>
    public const int MaxExamples = 5;
    /// <summary>Cut dependencies named in a finding message.</summary>
    const int MessageCuts = 3;
    /// <summary>Type pairs named per cut dependency in a message.</summary>
    const int MessagePairs = 2;
    /// <summary>Members named in a finding message.</summary>
    const int MessageMembers = 8;

    readonly record struct Node(string Namespace, string Assembly);

    sealed class Edge
    {
        public Dictionary<(string From, string To), NamespaceSite> Pairs { get; } = [];
        public int References { get; set; }
    }

    /// <summary>Builds the namespace summary (components largest first, then by place).</summary>
    public static NamespaceSummary Analyze(IEnumerable<ProjectFacts> facts, bool keepGeneratedTargets)
    {
        var projects = facts.Where(f => f.Namespaces is not null && !f.IsTest).Select(f => f.Namespaces!).ToList();
        var summary = new NamespaceSummary
        {
            GeneratedTargetsKept = keepGeneratedTargets, SourceFiles = projects.Sum(p => p.SourceFiles),
            GeneratedFilesExcluded = projects.Sum(p => p.GeneratedFiles), TestOrExcludedFiles = projects.Sum(p => p.ExcludedFiles),
        };
        var edges = Aggregate(projects, keepGeneratedTargets, summary);
        var nodes = edges.Keys.SelectMany(k => (Node[])[k.From, k.To]).Distinct()
            .OrderBy(n => n.Namespace, StringComparer.Ordinal).ThenBy(n => n.Assembly, StringComparer.Ordinal).ToList();
        var names = Names(nodes, projects);
        var index = nodes.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
        var graph = new CycleGraph(nodes.Count);
        foreach (var ((from, to), e) in edges) graph.Add(index[from], index[to], e.Pairs.Count);
        summary.Namespaces = nodes.Count;
        summary.Dependencies = edges.Count;
        summary.TypePairs = edges.Values.Sum(e => e.Pairs.Count);
        summary.References = edges.Values.Sum(e => e.References);
        var byIndex = edges.ToDictionary(kv => (index[kv.Key.From], index[kv.Key.To]), kv => kv.Value);
        var view = new GraphView(graph, names, nodes, byIndex);
        var components = graph.Components().Select(c => Component(c, view)).ToList();
        summary.Components = [.. components.OrderByDescending(c => c.Size).ThenBy(c => c.Place, StringComparer.Ordinal)];
        Notes(summary, components.Count(c => !c.CutComplete));
        return summary;
    }

    static Dictionary<(Node From, Node To), Edge> Aggregate(List<NamespaceFacts> projects, bool keepGenerated, NamespaceSummary summary)
    {
        var generatedOnly = projects.SelectMany(p => p.GeneratedOnlyTypes.Select(k => p.Assembly + "" + k)).ToHashSet(StringComparer.Ordinal);
        var edges = new Dictionary<(Node, Node), Edge>();
        var generatedPairs = new HashSet<(string, string, string)>();
        foreach (var (assembly, r, site) in projects.SelectMany(p => p.References.Select(kv => (p.Assembly, kv.Key, kv.Value))))
        {
            if (generatedOnly.Contains(r.TargetAssembly + "" + NamespaceFacts.TypeKey(r.TargetNamespace, r.TargetType)))
            {
                summary.GeneratedTargetReferences += site.References;
                generatedPairs.Add((assembly + "" + r.SourceNamespace + "." + r.SourceType, r.TargetAssembly, r.TargetNamespace + "." + r.TargetType));
                if (!keepGenerated) continue;
            }
            Add(edges, assembly, r, site);
        }
        summary.GeneratedTargetTypePairs = generatedPairs.Count;
        return edges;
    }

    /// <summary>Adds one reference to its node pair: references summed, the earliest site per type pair kept.</summary>
    static void Add(Dictionary<(Node, Node), Edge> edges, string assembly, NamespaceReference r, NamespaceSite site)
    {
        var key = (new Node(r.SourceNamespace, assembly), new Node(r.TargetNamespace, r.TargetAssembly));
        if (!edges.TryGetValue(key, out var e)) edges[key] = e = new Edge();
        e.References += site.References;
        var pair = (r.SourceType, r.TargetType);
        if (!e.Pairs.TryGetValue(pair, out var had) || NamespaceSite.Before(site.Path, site.Line, had)) e.Pairs[pair] = site;
    }

    /// <summary>Node names: the namespace (<c>&lt;global&gt;</c> for the global one), with the assembly in parentheses when the
    /// name is declared in, or used as a node of, more than one assembly.</summary>
    static List<string> Names(List<Node> nodes, List<NamespaceFacts> projects)
    {
        var assemblies = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Note(string ns, string asm)
        {
            if (!assemblies.TryGetValue(ns, out var set)) assemblies[ns] = set = new(StringComparer.Ordinal);
            set.Add(asm);
        }
        foreach (var p in projects)
            foreach (var ns in p.DeclaredNamespaces) Note(ns, p.Assembly);
        foreach (var n in nodes) Note(n.Namespace, n.Assembly);
        return [.. nodes.Select(n => Display(n.Namespace) + (assemblies[n.Namespace].Count > 1 ? $" ({n.Assembly})" : ""))];
    }

    static string Display(string ns) => ns.Length == 0 ? "<global>" : ns;

    /// <summary>The finding place (part of the id): the ordinal-first member, always with its assembly, so the
    /// id does not change when another assembly starts declaring the same namespace name. Names in messages keep the
    /// assembly only when the name is shared.</summary>
    static string PlaceOf(Node n) => $"namespace {Display(n.Namespace)} ({n.Assembly})";

    /// <summary>The graph with its node names, nodes and edge data, by node number.</summary>
    sealed record GraphView(CycleGraph Graph, List<string> Names, List<Node> Nodes, Dictionary<(int, int), Edge> Edges);

    static NamespaceComponent Component(List<int> members, GraphView view)
    {
        var (graph, names, nodes, edges) = view;
        var (cut, complete) = graph.GreedyCut(members, MaxCutEdges);
        var inside = members.ToHashSet();
        var deps = members.SelectMany(u => graph.Successors(u).Where(inside.Contains).Select(v => (u, v))).ToList();
        return new NamespaceComponent
        {
            Place = PlaceOf(nodes[members.Min()]), Size = members.Count,
            Assemblies = [.. members.Select(m => nodes[m].Assembly).Distinct().Order(StringComparer.Ordinal)],
            Members = [.. members.Select(m => names[m])],
            TypePairs = deps.Sum(d => graph.Weight(d.u, d.v)),
            ShortestCycle = [.. (graph.ShortestCycle(members) ?? []).Select(m => names[m])],
            MutualPairs = [.. graph.MutualPairs(members).Select(p => new NamespaceMutualPair
            {
                From = names[p.From], To = names[p.To], Weight = graph.Weight(p.From, p.To), OtherWeight = graph.Weight(p.To, p.From),
            })],
            Cut = [.. cut.Select(e => new NamespaceEdge { From = names[e.From], To = names[e.To], Weight = graph.Weight(e.From, e.To) })],
            CutTypePairs = cut.Sum(e => graph.Weight(e.From, e.To)),
            CutComplete = complete,
            Dependencies = [.. deps.Select(d => Dependency(names[d.u], names[d.v], edges[d]))],
        };
    }

    static NamespaceDependency Dependency(string from, string to, Edge e) => new()
    {
        From = from, To = to, Weight = e.Pairs.Count, References = e.References,
        Examples = [.. e.Pairs.OrderBy(p => p.Value.Path, StringComparer.Ordinal).ThenBy(p => p.Value.Line)
            .ThenBy(p => p.Key.From, StringComparer.Ordinal).ThenBy(p => p.Key.To, StringComparer.Ordinal).Take(MaxExamples)
            .Select(p => new NamespaceExample { Path = p.Value.Path, Line = p.Value.Line, FromType = p.Key.From, ToType = p.Key.To })],
    };

    static void Notes(NamespaceSummary s, int incomplete)
    {
        s.Notes.Add("nodes are (assembly, namespace); parent and child namespaces are separate nodes; test projects, test files, generated files, using directives and doc-comment crefs are not sources");
        if (s.GeneratedTargetReferences > 0)
            s.Notes.Add(s.GeneratedTargetsKept
                ? $"{N(s.GeneratedTargetReferences)} references to types declared only in generated code are kept (--include-generated-targets)"
                : $"{N(s.GeneratedTargetReferences)} references ({N(s.GeneratedTargetTypePairs)} type pairs) to types declared only in generated code are left out; --include-generated-targets keeps them");
        if (incomplete > 0) s.Notes.Add($"{N(incomplete)} suggested cut{(incomplete == 1 ? "" : "s")} stopped at {MaxCutEdges} dependencies with cycles left");
    }

    static string N(int v) => v.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>One <c>namespace-cycle</c> finding per component: path-less id, the component size
    /// as the value, located at the first example reference of the cheapest cut dependency.</summary>
    public static List<Finding> Findings(NamespaceSummary? summary, ActiveRule rule)
    {
        var level = rule.Warning ?? rule.Notice;
        if (summary is null || level is null) return [];
        var severity = rule.Warning is not null ? Severity.Warning : Severity.Notice;
        var list = new List<Finding>();
        foreach (var c in summary.Components)
        {
            var anchor = Anchor(c);
            list.Add(new Finding
            {
                Rule = RuleIds.NamespaceCycle, Severity = severity == Severity.Warning ? "warning" : "notice",
                Path = anchor?.Path ?? "", Line = anchor?.Line ?? 1, Member = c.Place, Value = c.Size, Metric = "namespaces",
                Preset = rule.LevelPreset(severity), ThresholdSource = level.Source,
                Message = Message(c), PlaceKey = c.Place, PathlessId = true,
            });
        }
        return list;
    }

    static NamespaceExample? Anchor(NamespaceComponent c)
    {
        var first = c.Cut.Count > 0 ? c.Cut[0] : null;
        var dep = first is null ? c.Dependencies.FirstOrDefault() : c.Dependencies.First(d => d.From == first.From && d.To == first.To);
        return dep?.Examples.FirstOrDefault();
    }

    /// <summary>"cheapest cut: B -> A (1 type pair: B1 -> A1); cycle of 2 namespaces: A, B; shortest cycle A -> B -> A".</summary>
    public static string Message(NamespaceComponent c)
    {
        var cuts = c.Cut.Take(MessageCuts).Select(e => CutText(e, c)).ToList();
        var head = $"cheapest cut{(c.Cut.Count == 1 ? "" : "s")}: {string.Join(", ", cuts)}";
        if (c.Cut.Count > MessageCuts) head += $", and {N(c.Cut.Count - MessageCuts)} more";
        if (c.Cut.Count > 1) head += $" (suggested cut: {N(c.Cut.Count)} dependencies, {N(c.CutTypePairs)} type pairs{(c.CutComplete ? "" : ", incomplete")})";
        var members = string.Join(", ", c.Members.Take(MessageMembers)) + (c.Members.Count > MessageMembers ? $", and {N(c.Members.Count - MessageMembers)} more" : "");
        return $"{head}; cycle of {N(c.Size)} namespaces: {members}; shortest cycle {string.Join(" -> ", c.ShortestCycle)}";
    }

    static string CutText(NamespaceEdge e, NamespaceComponent c)
    {
        var dep = c.Dependencies.First(d => d.From == e.From && d.To == e.To);
        var pairs = string.Join(", ", dep.Examples.Take(MessagePairs).Select(x => $"{x.FromType} -> {x.ToType}"));
        var more = e.Weight > MessagePairs ? $", and {N(e.Weight - MessagePairs)} more" : "";
        return $"{e.From} -> {e.To} ({N(e.Weight)} type pair{(e.Weight == 1 ? "" : "s")}: {pairs}{more})";
    }
}
