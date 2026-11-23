namespace SkarpSharp.Semantic;

/// <summary>
/// A weighted directed graph on nodes 0..n-1 for dependency cycles:
/// strongly connected components (Tarjan 1972, iterative), the shortest cycle (breadth-first search from each member),
/// mutual pairs, and a greedy weighted feedback-edge suggestion (repeatedly the lightest edge of the shortest remaining
/// cycle, then a minimality pass). Node numbers are the ordinal order of the node names, so every tie is broken by
/// that order (neighbours are visited in ascending order) and the results are deterministic.
/// </summary>
public sealed class CycleGraph
{
    readonly List<int>[] _out;
    readonly Dictionary<(int From, int To), int> _weights = [];

    public CycleGraph(int nodes)
    {
        _out = new List<int>[nodes];
        for (int i = 0; i < nodes; i++) _out[i] = [];
    }

    public int Count => _out.Length;

    /// <summary>A two-node cycle as a node list (first node repeated): no cycle is shorter.</summary>
    const int ShortestPossible = 3;

    /// <summary>Adds (or adds weight to) the edge; self-loops are ignored.</summary>
    public void Add(int from, int to, int weight)
    {
        if (from == to) return;
        if (_weights.TryGetValue((from, to), out var w)) { _weights[(from, to)] = w + weight; return; }
        _weights[(from, to)] = weight;
        var list = _out[from];
        int at = list.BinarySearch(to);
        list.Insert(~at, to);
    }

    public int Weight(int from, int to) => _weights.GetValueOrDefault((from, to));

    public bool HasEdge(int from, int to) => _weights.ContainsKey((from, to));

    public IReadOnlyList<int> Successors(int node) => _out[node];

    /// <summary>Strongly connected components of two or more nodes, each sorted, ordered by their first node.</summary>
    public List<List<int>> Components()
    {
        var tarjan = new Tarjan(this);
        for (int v = 0; v < Count; v++) tarjan.Visit(v);
        return [.. tarjan.Found.Where(c => c.Count > 1).Select(c => c.Order().ToList()).OrderBy(c => c[0])];
    }

    /// <summary>Iterative Tarjan: index and low-link per node, an explicit call stack of (node, next successor).</summary>
    sealed class Tarjan(CycleGraph g)
    {
        readonly int[] _index = Enumerable.Repeat(-1, g.Count).ToArray();
        readonly int[] _low = new int[g.Count];
        readonly bool[] _onStack = new bool[g.Count];
        readonly Stack<int> _stack = new();
        int _next;
        public List<List<int>> Found { get; } = [];

        public void Visit(int root)
        {
            if (_index[root] >= 0) return;
            var calls = new Stack<(int Node, int Edge)>();
            Open(root);
            calls.Push((root, 0));
            while (calls.Count > 0)
            {
                var (v, e) = calls.Pop();
                var succ = g._out[v];
                if (e < succ.Count)
                {
                    calls.Push((v, e + 1));
                    Step(v, succ[e], calls);
                    continue;
                }
                if (_low[v] == _index[v]) Close(v);
                if (calls.Count > 0) _low[calls.Peek().Node] = Math.Min(_low[calls.Peek().Node], _low[v]);
            }
        }

        void Step(int v, int w, Stack<(int Node, int Edge)> calls)
        {
            if (_index[w] < 0)
            {
                Open(w);
                calls.Push((w, 0));
            }
            else if (_onStack[w]) _low[v] = Math.Min(_low[v], _index[w]);
        }

        void Open(int v)
        {
            _index[v] = _low[v] = _next++;
            _stack.Push(v);
            _onStack[v] = true;
        }

        void Close(int v)
        {
            var component = new List<int>();
            int w;
            do
            {
                w = _stack.Pop();
                _onStack[w] = false;
                component.Add(w);
            } while (w != v);
            Found.Add(component);
        }
    }

    /// <summary>
    /// The shortest cycle among <paramref name="members"/> using only edges not in <paramref name="removed"/>: a
    /// breadth-first search from each member in ascending order; a strictly shorter cycle replaces the one found.
    /// Returned as the node list starting and ending at the same node, or null when there is no cycle.
    /// </summary>
    public List<int>? ShortestCycle(IReadOnlyList<int> members, IReadOnlySet<(int, int)>? removed = null)
    {
        var inside = members.ToHashSet();
        List<int>? best = null;
        foreach (var start in members.Order())
        {
            var cycle = CycleThrough(start, inside, removed, best?.Count - 1 ?? int.MaxValue);
            if (cycle is not null) best = cycle;
            if (best?.Count == ShortestPossible) break;
        }
        return best;
    }

    /// <summary>The shortest cycle through <paramref name="start"/> shorter than <paramref name="limit"/> edges, or null.</summary>
    List<int>? CycleThrough(int start, HashSet<int> inside, IReadOnlySet<(int, int)>? removed, int limit)
    {
        var parent = new Dictionary<int, int> { [start] = -1 };
        var depth = new Dictionary<int, int> { [start] = 0 };
        var queue = new Queue<int>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            if (depth[u] + 1 >= limit) return null;
            foreach (var w in _out[u])
            {
                if (!inside.Contains(w) || removed?.Contains((u, w)) == true) continue;
                if (w == start) return Path(parent, u, start);
                if (!parent.TryAdd(w, u)) continue;
                depth[w] = depth[u] + 1;
                queue.Enqueue(w);
            }
        }
        return null;
    }

    static List<int> Path(Dictionary<int, int> parent, int last, int start)
    {
        var path = new List<int> { start };
        for (int v = last; v != -1; v = parent[v]) path.Add(v);
        path.Reverse();
        return path;
    }

    /// <summary>Pairs of members with edges both ways, as (lighter direction from, to); a tie keeps the ordinal-first
    /// direction. Ordered by the lighter weight, then the nodes.</summary>
    public List<(int From, int To)> MutualPairs(IReadOnlyList<int> members)
    {
        var inside = members.ToHashSet();
        var pairs = new List<(int, int)>();
        foreach (var u in members)
            foreach (var v in _out[u])
                if (u < v && inside.Contains(v) && HasEdge(v, u)) pairs.Add(Weight(v, u) < Weight(u, v) ? (v, u) : (u, v));
        return [.. pairs.OrderBy(p => Weight(p.Item1, p.Item2)).ThenBy(p => p.Item1).ThenBy(p => p.Item2)];
    }

    /// <summary>
    /// Greedy weighted feedback-edge suggestion inside one component: repeatedly cut the lightest edge (ties: ordinal
    /// order of from, then to) of the shortest remaining cycle, at most <paramref name="maxEdges"/> times; then a
    /// minimality pass puts back, heaviest first, every cut edge whose return closes no cycle. Complete is false when
    /// the cap stopped it with cycles left. The cut is ordered by weight, then nodes (cheapest first).
    /// </summary>
    public (List<(int From, int To)> Cut, bool Complete) GreedyCut(IReadOnlyList<int> members, int maxEdges)
    {
        var removed = new HashSet<(int, int)>();
        var order = new List<(int, int)>();
        bool complete = false;
        while (order.Count < maxEdges)
        {
            if (ShortestCycle(members, removed) is not { } cycle) { complete = true; break; }
            var edge = Lightest(cycle);
            removed.Add(edge);
            order.Add(edge);
        }
        if (!complete && ShortestCycle(members, removed) is null) complete = true;
        if (complete) Minimize(members, removed, order);
        return ([.. removed.OrderBy(e => Weight(e.Item1, e.Item2)).ThenBy(e => e.Item1).ThenBy(e => e.Item2)], complete);
    }

    (int, int) Lightest(List<int> cycle)
    {
        (int, int) best = (cycle[0], cycle[1]);
        for (int i = 1; i < cycle.Count - 1; i++)
        {
            var e = (cycle[i], cycle[i + 1]);
            int c = Weight(e.Item1, e.Item2).CompareTo(Weight(best.Item1, best.Item2));
            if (c < 0 || (c == 0 && e.CompareTo(best) < 0)) best = e;
        }
        return best;
    }

    void Minimize(IReadOnlyList<int> members, HashSet<(int, int)> removed, List<(int, int)> order)
    {
        var inside = members.ToHashSet();
        foreach (var e in order.OrderByDescending(e => Weight(e.Item1, e.Item2)).ThenBy(e => e))
        {
            removed.Remove(e);
            if (Reaches(e.Item2, e.Item1, inside, removed)) removed.Add(e);
        }
    }

    bool Reaches(int from, int to, HashSet<int> inside, HashSet<(int, int)> removed)
    {
        var seen = new HashSet<int> { from };
        var queue = new Queue<int>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            if (u == to) return true;
            foreach (var w in _out[u])
                if (inside.Contains(w) && !removed.Contains((u, w)) && seen.Add(w)) queue.Enqueue(w);
        }
        return false;
    }
}
