namespace SkarpSharp.Semantic;

/// <summary>One method-like member for TCC and LCC: whether it counts (visible), the state it uses directly and the members of the same type it calls on the same object.</summary>
public sealed class CohesionMember
{
    public required string Name { get; init; }
    public bool Visible { get; init; }
    public HashSet<int> Fields { get; } = [];
    public HashSet<int> Calls { get; } = [];
}

/// <summary>TCC and LCC of one type, or why they are not given.</summary>
public sealed record CohesionValue(int Methods, int? Pairs, int? Direct, int? Indirect, double? Tcc, double? Lcc, string? Note);

/// <summary>
/// Tight and loose class cohesion after Bieman and Kang 1995:
/// over the visible methods (constructors excluded), each method's abstracted state is the state it uses directly plus
/// the state used by every member it reaches through calls on the same object (transitive, private members included).
/// Two methods are directly connected when those sets share a variable; LCC adds pairs connected through other methods.
/// TCC = NDC / NP, LCC = (NDC + NIC) / NP with NP = N(N-1)/2. Not applicable with fewer than two visible methods or when
/// no visible method uses any state.
/// </summary>
public static class Cohesion
{
    public static CohesionValue Compute(IReadOnlyList<CohesionMember> members)
    {
        var visible = Enumerable.Range(0, members.Count).Where(i => members[i].Visible).ToList();
        int n = visible.Count;
        if (n < 2) return new CohesionValue(n, null, null, null, null, null, n == 0 ? "no visible methods" : "one visible method");
        var abstracted = visible.Select(i => Reach(members, i)).ToList();
        if (abstracted.All(a => a.Count == 0)) return new CohesionValue(n, null, null, null, null, null, "no visible method uses the type's state");
        int pairs = n * (n - 1) / 2, direct = 0;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (int a = 0; a < n; a++)
            for (int b = a + 1; b < n; b++)
                if (abstracted[a].Overlaps(abstracted[b]))
                {
                    direct++;
                    parent[Find(a)] = Find(b);
                }
        int connected = Enumerable.Range(0, n).GroupBy(Find).Sum(g => g.Count() * (g.Count() - 1) / 2);
        return new CohesionValue(n, pairs, direct, connected - direct,
            Math.Round((double)direct / pairs, 4), Math.Round((double)connected / pairs, 4), null);
    }

    /// <summary>The state used by member <paramref name="start"/> and every member it reaches through calls.</summary>
    static HashSet<int> Reach(IReadOnlyList<CohesionMember> members, int start)
    {
        var fields = new HashSet<int>();
        var seen = new HashSet<int> { start };
        var stack = new Stack<int>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var m = members[stack.Pop()];
            fields.UnionWith(m.Fields);
            foreach (var c in m.Calls)
                if (seen.Add(c)) stack.Push(c);
        }
        return fields;
    }
}
