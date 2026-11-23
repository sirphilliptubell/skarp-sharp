namespace SkarpSharp.Clones;

/// <summary>A Type 1/2 clone group: instances of equal normalized token sequences (global token index of each start).</summary>
public sealed class TokenGroup
{
    public required int[] Starts { get; init; }
    public required int Length { get; init; }
    /// <summary>True when the raw token text is equal in every instance (Type 1), else renamed (Type 2).</summary>
    public required bool Exact { get; init; }
}

/// <summary>
/// The Type 1/2 matcher (our own implementation):
/// a rolling hash over windows of Baker-encoded tokens, windows bucketed and verified token by token, pairs extended to
/// pairwise maximal matches at their left-maximal window and unioned into groups; then each group is trimmed to whole
/// members and statements, filtered on size and on body share, and groups contained in larger ones are dropped.
/// </summary>
public sealed class TokenMatcher(CloneCorpus c)
{
    const ulong Base = 0x9E3779B97F4A7C15UL;
    /// <summary>Classes larger than this are paired as a star plus a chain instead of all pairs.</summary>
    const int AllPairsLimit = 64;

    public static List<TokenGroup> Find(CloneCorpus c, int window, int minTokens, int minLines) =>
        new TokenMatcher(c).Run(Math.Max(2, window), minTokens, minLines);

    List<TokenGroup> Run(int window, int minTokens, int minLines)
    {
        var classes = Classes(window);
        var raw = Extend(classes);
        var groups = new List<TokenGroup>();
        foreach (var (starts, length) in raw)
            if (Trim(starts, length, minTokens, minLines) is { } g) groups.Add(g);
        return DropContained(groups);
    }

    // ---- windows ---------------------------------------------------------------------------------------------------

    /// <summary>Calls <paramref name="visit"/> with the hash and start of every window of <paramref name="n"/> tokens
    /// that does not cross a run break.</summary>
    void Windows(int n, Action<ulong, int> visit)
    {
        var pow = new ulong[n + 1];
        pow[0] = 1;
        for (int k = 1; k <= n; k++) pow[k] = pow[k - 1] * Base;
        for (int runStart = 0; runStart < c.Total;)
        {
            int runEnd = runStart + 1;
            while (runEnd < c.Total && (c.Flags[runEnd] & TokenFlags.Break) == 0) runEnd++;
            if (runEnd - runStart >= n) RunWindows(runStart, runEnd, n, pow, visit);
            runStart = runEnd;
        }
    }

    /// <summary>The windows of one run (no break inside), the hash rolled forward one token at a time.</summary>
    void RunWindows(int runStart, int runEnd, int n, ulong[] pow, Action<ulong, int> visit)
    {
        ulong reset = CloneCorpus.IdentifierCode(0);
        ulong h = 0;
        for (int k = 0; k < n; k++) h = h * Base + c.Value(runStart + k, runStart);
        for (int s = runStart; ; s++)
        {
            visit(h, s);
            if (s + n >= runEnd) return;
            h -= c.Value(s, s) * pow[n - 1];
            // The next occurrence of the outgoing name loses its back reference: its code becomes 0.
            int j = c.Next[s];
            if (j > s && j <= s + n - 1) h += (reset - CloneCorpus.IdentifierCode(j - s)) * pow[n - 1 - (j - s)];
            h = h * Base + c.Value(s + n, s + 1);
        }
    }

    /// <summary>Classes of verified-equal windows (two or more members); <c>Class[i]</c> is the class of the window at i.</summary>
    (List<int[]> Members, int[] Class) Classes(int n)
    {
        // Singleton pruning: a first pass marks hashes seen twice in a bit table, so the
        // second pass stores only windows that can have a partner.
        int bits = 1 << Math.Clamp((int)Math.Ceiling(Math.Log2(Math.Max(1024, c.Total) * 8.0)), 10, 30);
        var once = new System.Collections.BitArray(bits);
        var twice = new System.Collections.BitArray(bits);
        ulong mask = (ulong)bits - 1;
        Windows(n, (h, _) =>
        {
            int slot = (int)(StableHash.Mix(h) & mask);
            if (once[slot]) { if (!twice[slot]) twice[slot] = true; } else once[slot] = true;
        });
        once = null!;
        var hashes = new List<ulong>();
        var starts = new List<int>();
        Windows(n, (h, s) =>
        {
            if (!twice[(int)(StableHash.Mix(h) & mask)]) return;
            hashes.Add(h);
            starts.Add(s);
        });
        var hashArray = hashes.ToArray();
        var startArray = starts.ToArray();
        hashes = null!;
        starts = null!;
        Array.Sort(hashArray, startArray);
        return Verify(hashArray, startArray, n);
    }

    (List<int[]> Members, int[] Class) Verify(ulong[] hashes, int[] starts, int n)
    {
        var windowClass = new int[c.Total];
        Array.Fill(windowClass, -1);
        var classes = new List<int[]>();
        var reps = new List<List<int>>();
        for (int b0 = 0; b0 < hashes.Length;)
        {
            int b1 = b0 + 1;
            while (b1 < hashes.Length && hashes[b1] == hashes[b0]) b1++;
            if (b1 - b0 >= 2)
            {
                reps.Clear();
                for (int k = b0; k < b1; k++) Place(reps, starts[k], n);
                foreach (var grp in reps.Where(g => g.Count >= 2))
                {
                    grp.Sort();
                    foreach (var p in grp) windowClass[p] = classes.Count;
                    classes.Add([.. grp]);
                }
            }
            b0 = b1;
        }
        return (classes, windowClass);
    }

    void Place(List<List<int>> reps, int p, int n)
    {
        foreach (var grp in reps)
            if (WindowEqual(grp[0], p, n)) { grp.Add(p); return; }
        reps.Add([p]);
    }

    bool WindowEqual(int a, int b, int n)
    {
        for (int k = 0; k < n; k++)
            if (!c.Equal(a + k, a, b + k, b)) return false;
        return true;
    }

    // ---- pairwise maximal matches -----------------------------------------------------------------------------------

    /// <summary>Every pair of a class is extended to its maximal match at its left-maximal window only; overlapping
    /// (periodic) matches are dropped; matches are unioned into groups of equal-length instances.</summary>
    List<(int[] Starts, int Length)> Extend((List<int[]> Members, int[] Class) classes)
    {
        var nodes = new Dictionary<(int Start, int Length), int>();
        var keys = new List<(int Start, int Length)>();
        var parent = new List<int>();
        int Node(int s, int l)
        {
            if (nodes.TryGetValue((s, l), out var id)) return id;
            id = keys.Count;
            nodes[(s, l)] = id;
            keys.Add((s, l));
            parent.Add(id);
            return id;
        }
        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        var cls = classes.Class;
        foreach (var members in classes.Members)
            foreach (var (p, q) in Pairs(members))
            {
                if (p > 0 && cls[p - 1] >= 0 && cls[p - 1] == cls[q - 1]) continue; // not left-maximal
                int length = ExtendPair(p, q);
                if (q < p + length) continue;
                int a = Find(Node(p, length)), b = Find(Node(q, length));
                if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
            }
        return Components(keys, Find);
    }

    static List<(int[] Starts, int Length)> Components(List<(int Start, int Length)> keys, Func<int, int> find)
    {
        var result = new List<(int[] Starts, int Length)>();
        foreach (var comp in Enumerable.Range(0, keys.Count).GroupBy(find).OrderBy(g => g.Key))
        {
            int length = keys[comp.First()].Length;
            var kept = new List<int>();
            int lastEnd = -1;
            foreach (var p in comp.Select(i => keys[i].Start).Distinct().Order())
                if (p >= lastEnd) { kept.Add(p); lastEnd = p + length; }
            if (kept.Count >= 2) result.Add(([.. kept], length));
        }
        return result;
    }

    static IEnumerable<(int, int)> Pairs(int[] members)
    {
        if (members.Length <= AllPairsLimit)
        {
            for (int i = 0; i < members.Length; i++)
                for (int j = i + 1; j < members.Length; j++) yield return (members[i], members[j]);
            yield break;
        }
        for (int k = 1; k < members.Length; k++)
        {
            yield return (members[0], members[k]);
            if (k > 1) yield return (members[k - 1], members[k]);
        }
    }

    int ExtendPair(int p, int q)
    {
        int length = 0;
        while (p + length < c.Total && q + length < c.Total)
        {
            if (length > 0 && ((c.Flags[p + length] & TokenFlags.Break) != 0 || (c.Flags[q + length] & TokenFlags.Break) != 0)) break;
            if (!c.Equal(p + length, p, q + length, q)) break;
            length++;
        }
        return length;
    }

    // ---- trim and filter --------------------------------------------------------------------------------------------

    /// <summary>
    /// Trims a raw group to whole members at its ends (an instance that crosses from one body into another is cut back),
    /// then to statement boundaries in every instance; keeps it when it still has the minimum tokens and lines and at
    /// least half of its first instance lies in method, accessor, constructor or operator bodies.
    /// </summary>
    TokenGroup? Trim(int[] starts, int length, int minTokens, int minLines)
    {
        var (a, b) = MemberTrim(starts, length);
        while (a <= b && !All(starts, a, TokenFlags.Start)) a++;
        while (b >= a && !All(starts, b, TokenFlags.End)) b--;
        int len = b - a + 1;
        if (len < minTokens) return null;
        if (starts.Min(p => c.SpanLines(p + a, p + b)) < minLines) return null;
        int inBody = 0;
        for (int k = a; k <= b; k++) if (c.Members[starts[0] + k] != 0) inBody++;
        if (inBody * 2 < len) return null;
        return new TokenGroup { Starts = [.. starts.Select(p => p + a)], Length = len, Exact = IsExact(starts, a, b) };
    }

    (int First, int Last) MemberTrim(int[] starts, int length)
    {
        int a = 0, b = length - 1;
        foreach (var p in starts)
        {
            int m0 = c.Members[p], m1 = c.Members[p + length - 1];
            bool crosses = false;
            for (int k = 1; k < length && !crosses; k++) crosses = c.Members[p + k] != m0;
            if (!crosses) continue;
            if (m0 != 0 && (c.Flags[p] & TokenFlags.MemberFirst) == 0)
            {
                int k = 0;
                while (k < length && c.Members[p + k] == m0) k++;
                a = Math.Max(a, k);
            }
            if (m1 != 0 && (c.Flags[p + length - 1] & TokenFlags.MemberLast) == 0)
            {
                int k = length - 1;
                while (k >= 0 && c.Members[p + k] == m1) k--;
                b = Math.Min(b, k);
            }
        }
        return (a, b);
    }

    bool All(int[] starts, int offset, byte flag)
    {
        foreach (var p in starts)
            if ((c.Flags[p + offset] & flag) == 0) return false;
        return true;
    }

    bool IsExact(int[] starts, int a, int b)
    {
        for (int k = a; k <= b; k++)
            for (int x = 1; x < starts.Length; x++)
                if (c.Raw[starts[x] + k] != c.Raw[starts[0] + k]) return false;
        return true;
    }

    /// <summary>Drops a group when every instance overlaps (90% of its tokens) an instance of a kept group with at least
    /// as many instances; groups with more instances, then longer ones, are kept first.</summary>
    List<TokenGroup> DropContained(List<TokenGroup> groups)
    {
        var keptByFile = new Dictionary<int, List<(int Start, int End, int Count)>>();
        var result = new List<TokenGroup>();
        foreach (var g in groups.OrderByDescending(g => g.Starts.Length).ThenByDescending(g => g.Length).ThenBy(g => g.Starts[0]))
        {
            if (g.Starts.All(s => Covered(keptByFile, g, s))) continue;
            result.Add(g);
            foreach (var s in g.Starts)
            {
                if (!keptByFile.TryGetValue(c.FileOf[s], out var list)) keptByFile[c.FileOf[s]] = list = [];
                list.Add((s, s + g.Length - 1, g.Starts.Length));
            }
        }
        result.Sort((x, y) => x.Starts[0].CompareTo(y.Starts[0]) is var o && o != 0 ? o : y.Length.CompareTo(x.Length));
        return result;
    }

    bool Covered(Dictionary<int, List<(int Start, int End, int Count)>> kept, TokenGroup g, int s)
    {
        if (!kept.TryGetValue(c.FileOf[s], out var list)) return false;
        int e = s + g.Length - 1;
        foreach (var (ks, ke, n) in list)
            if (n >= g.Starts.Length && Math.Min(e, ke) - Math.Max(s, ks) + 1 >= 0.9 * g.Length) return true;
        return false;
    }
}
