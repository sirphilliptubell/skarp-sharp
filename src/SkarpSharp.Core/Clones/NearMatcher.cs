using SkarpSharp.Model;

namespace SkarpSharp.Clones;

/// <summary>One Type 3 unit in a corpus: its file and the unit.</summary>
public sealed record NearUnit(int File, CloneUnit Unit);

/// <summary>Two similar units and their multiset Jaccard similarity.</summary>
public sealed record NearPair(int A, int B, double Similarity);

/// <summary>A Type 3 group: units joined by similar pairs (connected components).</summary>
public sealed record NearGroup(IReadOnlyList<NearUnit> Units, IReadOnlyList<(NearUnit A, NearUnit B, double Similarity)> Pairs);

/// <summary>
/// Type 3 at method level (our own implementation): each method's normalized subtree fingerprints form a multiset; multiset Jaccard via (fingerprint, occurrence)
/// elements ranked rarest first; an exact AllPairs join with a prefix and a size filter, so there is no all-pairs step.
/// Units that only forward a call are skipped; pairs already covered by a Type 1/2 group are dropped; the rest is merged
/// into groups.
/// </summary>
public static class NearMatcher
{
    public static List<NearGroup> Find(CloneCorpus c, double threshold, int minLines, IReadOnlyList<TokenGroup> covering)
    {
        var units = new List<NearUnit>();
        for (int f = 0; f < c.Files.Length; f++)
            foreach (var u in c.Files[f].Units ?? [])
                if (!u.Forwarding && u.Lines >= minLines && u.Fingerprints.Length > 0) units.Add(new NearUnit(f, u));
        var elements = Rank(units);
        var pairs = Join(elements, threshold).Where(p => !Covered(c, units[p.A], units[p.B], covering)).ToList();
        return Groups(units, pairs);
    }

    /// <summary>Turns each multiset into a sorted array of element ranks (rarest first), numbering repeats so that
    /// multiset Jaccard equals set Jaccard on (fingerprint, occurrence).</summary>
    static int[][] Rank(List<NearUnit> units)
    {
        var freq = new Dictionary<ulong, int>();
        var elems = new ulong[units.Count][];
        for (int u = 0; u < units.Count; u++)
        {
            var fps = (ulong[])units[u].Unit.Fingerprints.Clone();
            Array.Sort(fps);
            var e = new ulong[fps.Length];
            int occurrence = 0;
            for (int i = 0; i < fps.Length; i++)
            {
                occurrence = i > 0 && fps[i] == fps[i - 1] ? occurrence + 1 : 0;
                e[i] = StableHash.Combine(fps[i], (ulong)occurrence);
                freq[e[i]] = freq.GetValueOrDefault(e[i]) + 1;
            }
            elems[u] = e;
        }
        var order = freq.Keys.ToArray();
        Array.Sort(order, (a, b) => freq[a] != freq[b] ? freq[a].CompareTo(freq[b]) : a.CompareTo(b));
        var rank = new Dictionary<ulong, int>(order.Length);
        for (int i = 0; i < order.Length; i++) rank[order[i]] = i;
        var result = new int[units.Count][];
        for (int u = 0; u < units.Count; u++)
        {
            var r = elems[u].Select(x => rank[x]).ToArray();
            Array.Sort(r);
            result[u] = r;
        }
        return result;
    }

    /// <summary>All pairs with similarity at least <paramref name="t"/> (exact: the prefix filter misses none).</summary>
    public static List<NearPair> Join(int[][] elements, double t)
    {
        var order = Enumerable.Range(0, elements.Length).Where(i => elements[i].Length > 0)
            .OrderBy(i => elements[i].Length).ThenBy(i => i).ToArray();
        int maxRank = elements.Where(e => e.Length > 0).Select(e => e[^1]).DefaultIfEmpty(0).Max();
        var join = new PrefixJoin(elements, t, new List<int>?[maxRank + 1]);
        foreach (var a in order) join.Probe(a);
        return join.Pairs;
    }

    /// <summary>AllPairs with a prefix and a size filter: each unit probes the inverted index of its prefix elements
    /// (rarest first) for earlier, shorter units, then adds its own prefix to the index.</summary>
    sealed class PrefixJoin(int[][] elements, double t, List<int>?[] index)
    {
        readonly int[] _stamp = Enumerable.Repeat(-1, elements.Length).ToArray();
        public List<NearPair> Pairs { get; } = [];

        public void Probe(int a)
        {
            var A = elements[a];
            int prefix = Math.Min(A.Length, A.Length - (int)Math.Ceiling(t * A.Length - 1e-9) + 1);
            for (int k = 0; k < prefix; k++)
                foreach (var b in index[A[k]] ?? [])
                    Compare(a, b);
            for (int k = 0; k < prefix; k++) (index[A[k]] ??= []).Add(a);
        }

        void Compare(int a, int b)
        {
            if (_stamp[b] == a) return;
            _stamp[b] = a;
            if (elements[b].Length < t * elements[a].Length) return;
            double sim = Similarity(elements[a], elements[b]);
            if (sim >= t - 1e-12) Pairs.Add(new NearPair(Math.Min(a, b), Math.Max(a, b), sim));
        }
    }

    public static double Similarity(int[] a, int[] b)
    {
        int i = 0, j = 0, inter = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j]) { inter++; i++; j++; }
            else if (a[i] < b[j]) i++;
            else j++;
        }
        return (double)inter / (a.Length + b.Length - inter);
    }

    /// <summary>A pair is covered when one Type 1/2 group has two different instances that each cover at least half of
    /// one unit's tokens: Type 3 never reports a copy that duplicate-code already reports.</summary>
    static bool Covered(CloneCorpus c, NearUnit a, NearUnit b, IReadOnlyList<TokenGroup> groups)
    {
        if (a.Unit.FirstToken < 0 || b.Unit.FirstToken < 0) return false;
        var (a0, a1) = (c.FileStart[a.File] + a.Unit.FirstToken, c.FileStart[a.File] + a.Unit.LastToken);
        var (b0, b1) = (c.FileStart[b.File] + b.Unit.FirstToken, c.FileStart[b.File] + b.Unit.LastToken);
        foreach (var g in groups)
        {
            int ia = Array.FindIndex(g.Starts, s => Overlap(s, s + g.Length - 1, a0, a1) * 2 >= a1 - a0 + 1);
            if (ia < 0) continue;
            for (int ib = 0; ib < g.Starts.Length; ib++)
                if (ib != ia && Overlap(g.Starts[ib], g.Starts[ib] + g.Length - 1, b0, b1) * 2 >= b1 - b0 + 1) return true;
        }
        return false;
    }

    static int Overlap(int s, int e, int a, int b) => Math.Max(0, Math.Min(e, b) - Math.Max(s, a) + 1);

    static List<NearGroup> Groups(List<NearUnit> units, List<NearPair> pairs)
    {
        var parent = Enumerable.Range(0, units.Count).ToArray();
        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        foreach (var p in pairs)
        {
            int a = Find(p.A), b = Find(p.B);
            if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
        }
        return [.. pairs.GroupBy(p => Find(p.A)).OrderBy(g => g.Key).Select(g => new NearGroup(
            [.. g.SelectMany(p => new[] { p.A, p.B }).Distinct().Order().Select(i => units[i])],
            [.. g.Select(p => (units[p.A], units[p.B], p.Similarity))]))];
    }
}
