namespace SkarpSharp.Semantic;

/// <summary>One candidate as the liveness pass sees it: a seed (root or not evaluated), and the containing type a
/// type-bound member (override, interface implementation, pattern member, initialized field) depends on.</summary>
internal readonly record struct LivenessNode(string Id, bool Seed, bool Bound, string? Container);

/// <summary>Why a candidate is dead or only test-reached: its unused users (ids), its depth, and the test-only user it
/// was reached through.</summary>
internal readonly record struct DeadFacts(IReadOnlyList<string> Users, int Depth, string? Via);

/// <summary>
/// Liveness over the reference edges of the whole solution. Live: seeds, targets of edges whose source is not a
/// candidate (<see cref="ReferenceEdge.Live"/> or an id that is no candidate), everything a live candidate references,
/// and type-bound members of live types (or of types that are not candidates). A second pass from
/// <see cref="ReferenceEdge.Test"/> reaches the test-only candidates. The rest is dead; cycles die together.
/// </summary>
internal sealed class UnusedLiveness
{
    static readonly IReadOnlyList<(string, EdgeKind)> NoEdges = [];

    readonly Dictionary<string, LivenessNode> _nodes;
    readonly Dictionary<string, List<(string Target, EdgeKind Kind)>> _out = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<(string Source, EdgeKind Kind)>> _in = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<string>> _bound = new(StringComparer.Ordinal);
    readonly HashSet<string> _live = new(StringComparer.Ordinal);
    readonly Dictionary<string, string?> _testVia = new(StringComparer.Ordinal);
    Dictionary<string, int>? _depth;

    public UnusedLiveness(IEnumerable<ReferenceEdge> edges, IEnumerable<LivenessNode> nodes)
    {
        _nodes = new Dictionary<string, LivenessNode>(StringComparer.Ordinal);
        foreach (var n in nodes) _nodes.TryAdd(n.Id, n);
        foreach (var e in edges)
        {
            if (e.Source == e.Target) continue;
            Add(_out, e.Source, (e.Target, e.Kind));
            Add(_in, e.Target, (e.Source, e.Kind));
        }
        foreach (var n in _nodes.Values)
            if (n.Bound && IsCandidate(n.Container)) Add(_bound, n.Container!, n.Id);
        MarkLive();
        MarkTestOnly();
    }

    static void Add<T>(Dictionary<string, List<T>> map, string key, T value)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(value);
    }

    public bool IsLive(string id) => _live.Contains(id);

    /// <summary>Reached only from test code (directly or through other test-only candidates).</summary>
    public bool IsTestOnly(string id) => _testVia.ContainsKey(id);

    /// <summary>A source whose references count: a non-candidate (or the LIVE pseudo-source) or a live candidate.</summary>
    bool IsLiveSource(string source) => source != ReferenceEdge.Test && (!_nodes.ContainsKey(source) || _live.Contains(source));

    IReadOnlyList<(string Source, EdgeKind Kind)> Incoming(string id) => _in.TryGetValue(id, out var list) ? list : NoEdges;

    /// <summary>Any reference from live code.</summary>
    public bool LiveReferenced(string id) => Incoming(id).Any(e => IsLiveSource(e.Source));

    /// <summary>A reference that is not a write, from live code.</summary>
    public bool LiveRead(string id) => Incoming(id).Any(e => e.Kind != EdgeKind.Write && IsLiveSource(e.Source));

    /// <summary>A write from live code.</summary>
    public bool LiveWrite(string id) => Incoming(id).Any(e => e.Kind == EdgeKind.Write && IsLiveSource(e.Source));

    /// <summary>
    /// Whether test code reads the candidate, directly (a test project or file; <c>Via</c> null) or only
    /// through test-only candidates (<c>Via</c>: the first of them in ordinal order).
    /// </summary>
    public (bool Read, string? Via) TestRead(string id)
    {
        var readers = Incoming(id).Where(e => e.Kind != EdgeKind.Write).Select(e => e.Source).ToList();
        if (readers.Contains(ReferenceEdge.Test)) return (true, null);
        var via = readers.Where(IsTestOnly).Order(StringComparer.Ordinal).FirstOrDefault();
        return (via is not null, via);
    }

    /// <summary>The candidate's containing type is live (or not a candidate).</summary>
    public bool ContainerLive(string id) => !_nodes.TryGetValue(id, out var n) || !IsCandidate(n.Container) || _live.Contains(n.Container!);

    /// <summary>Written by live code, or initialized in a live type.</summary>
    public bool Written(UnusedCandidate c) => LiveWrite(c.Id) || (c.Initialized && ContainerLive(c.Id));

    bool IsCandidate(string? id) => id is not null && _nodes.ContainsKey(id);

    void MarkLive()
    {
        var queue = new Queue<string>();
        foreach (var id in Seeds()) Mark(id, queue);
        while (queue.Count > 0)
            foreach (var next in Successors(queue.Dequeue())) Mark(next, queue);
    }

    /// <summary>Seed candidates, type-bound members of types that are not candidates, and what code outside the
    /// candidates references.</summary>
    IEnumerable<string> Seeds()
    {
        foreach (var n in _nodes.Values)
            if (n.Seed || (n.Bound && !IsCandidate(n.Container))) yield return n.Id;
        foreach (var (source, targets) in _out)
            if (IsOutsideSource(source))
                foreach (var (t, _) in targets) yield return t;
    }

    bool IsOutsideSource(string source) => source == ReferenceEdge.Live || (source != ReferenceEdge.Test && !_nodes.ContainsKey(source));

    void Mark(string id, Queue<string> queue)
    {
        if (_live.Add(id)) queue.Enqueue(id);
    }

    /// <summary>What a reached candidate keeps reached: its references and, for a type, its type-bound members.</summary>
    IEnumerable<string> Successors(string id)
    {
        if (_out.TryGetValue(id, out var targets))
            foreach (var (t, _) in targets) yield return t;
        if (_bound.TryGetValue(id, out var members))
            foreach (var m in members) yield return m;
    }

    void MarkTestOnly()
    {
        var queue = new Queue<string>();
        if (_out.TryGetValue(ReferenceEdge.Test, out var direct))
            foreach (var (t, _) in direct)
                if (!_live.Contains(t) && _testVia.TryAdd(t, null)) queue.Enqueue(t);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var next in Successors(id))
                if (!_live.Contains(next) && _testVia.TryAdd(next, id)) queue.Enqueue(next);
        }
    }

    /// <summary>The users, depth and test path of a candidate that is neither live nor (for <c>Via</c>) direct test use.</summary>
    public DeadFacts Facts(string id)
    {
        _depth ??= Depths();
        _testVia.TryGetValue(id, out var via);
        return new DeadFacts(Users(id), _depth.GetValueOrDefault(id), via);
    }

    /// <summary>The dead candidates that reference a dead candidate (distinct, ordinal order).</summary>
    List<string> Users(string id) =>
        [.. Incoming(id).Select(e => e.Source).Where(s => s != id && IsDead(s)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    bool IsDead(string id) => _nodes.ContainsKey(id) && !_live.Contains(id) && !_testVia.ContainsKey(id);

    /// <summary>
    /// Depth of every dead candidate: 0 without dead users; else the shortest distance from a depth-0 candidate over
    /// dead edges. Dead cycles no depth-0 candidate reaches start at 1 (their source components), and what they reach
    /// follows from there.
    /// </summary>
    Dictionary<string, int> Depths()
    {
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        var dead = _nodes.Keys.Where(IsDead).ToList();
        Spread(dead.Where(d => Users(d).Count == 0), 0, depth);
        var rest = dead.Where(d => !depth.ContainsKey(d)).ToHashSet(StringComparer.Ordinal);
        if (rest.Count > 0) Spread(SourceComponents(rest), 1, depth);
        return depth;
    }

    /// <summary>Breadth-first over dead edges from the starts at the given depth (nodes already placed keep theirs).</summary>
    void Spread(IEnumerable<string> starts, int start, Dictionary<string, int> depth)
    {
        var queue = new Queue<string>();
        foreach (var s in starts)
            if (depth.TryAdd(s, start)) queue.Enqueue(s);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var next in Successors(id))
                if (IsDead(next) && depth.TryAdd(next, depth[id] + 1)) queue.Enqueue(next);
        }
    }

    /// <summary>The members of the strongly connected components of <paramref name="rest"/> that no other component of
    /// it reaches (iterative Tarjan over the dead edges inside the set).</summary>
    List<string> SourceComponents(HashSet<string> rest)
    {
        var component = new Tarjan(rest, id => Successors(id).Where(rest.Contains)).Components();
        var reached = new HashSet<int>();
        foreach (var id in rest)
            foreach (var next in Successors(id))
                if (rest.Contains(next) && component[next] != component[id]) reached.Add(component[next]);
        return [.. rest.Where(id => !reached.Contains(component[id])).Order(StringComparer.Ordinal)];
    }

    /// <summary>Iterative Tarjan: the component number of every node.</summary>
    sealed class Tarjan(HashSet<string> nodes, Func<string, IEnumerable<string>> next)
    {
        readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
        readonly Dictionary<string, int> _low = new(StringComparer.Ordinal);
        readonly Dictionary<string, int> _component = new(StringComparer.Ordinal);
        readonly Stack<string> _stack = new();
        readonly HashSet<string> _onStack = new(StringComparer.Ordinal);
        int _counter;
        int _components;

        public Dictionary<string, int> Components()
        {
            foreach (var n in nodes.Order(StringComparer.Ordinal))
                if (!_index.ContainsKey(n)) Visit(n);
            return _component;
        }

        void Visit(string root)
        {
            var work = new Stack<(string Node, IEnumerator<string> Next)>();
            Open(root, work);
            while (work.Count > 0)
            {
                var (node, children) = work.Peek();
                if (children.MoveNext())
                {
                    var child = children.Current;
                    if (!_index.ContainsKey(child)) Open(child, work);
                    else if (_onStack.Contains(child)) _low[node] = Math.Min(_low[node], _index[child]);
                    continue;
                }
                work.Pop();
                if (work.Count > 0) _low[work.Peek().Node] = Math.Min(_low[work.Peek().Node], _low[node]);
                if (_low[node] == _index[node]) Close(node);
            }
        }

        void Open(string node, Stack<(string, IEnumerator<string>)> work)
        {
            _index[node] = _low[node] = _counter++;
            _stack.Push(node);
            _onStack.Add(node);
            work.Push((node, next(node).GetEnumerator()));
        }

        void Close(string node)
        {
            int id = _components++;
            string member;
            do
            {
                member = _stack.Pop();
                _onStack.Remove(member);
                _component[member] = id;
            } while (member != node);
        }
    }
}

/// <summary>
/// The state of one project's copy of a candidate after liveness. Order: not evaluated; a symbol root
/// (type-bound ones only in a live type); used (for fields and auto-properties: read) by live code; a solution root;
/// written by live code but never read (<c>unread-member</c>, a root naming the exclusion, or the test label
/// when only tests read it); reached only from tests; unused (directly, or used only by unused code).
/// </summary>
internal static class UnusedJudge
{
    public static UnusedCode.Copy Judge(UnusedCode.Pending p, UnusedLiveness live, IReadOnlyDictionary<string, string> places)
    {
        var (state, reason) = State(p, live);
        var detail = state switch
        {
            UnusedState.UsedOnlyByTests when !live.IsTestOnly(p.C.Id) => ReadDetail(p.C.Id, live, places),
            UnusedState.Unused or UnusedState.UsedOnlyByTests => Detail(p.C.Id, live, places),
            _ => null,
        };
        return new UnusedCode.Copy(p.C, p.Tfm, state, reason, p.Row, p.Scope, detail);
    }

    /// <summary>A member live code writes and only tests read: the test-only reader it is read through.</summary>
    static DeadDetail ReadDetail(string id, UnusedLiveness live, IReadOnlyDictionary<string, string> places)
    {
        var via = live.TestRead(id).Via;
        return new DeadDetail([], 0, via is null ? null : places.GetValueOrDefault(via, via), ReadOnlyByTests: true);
    }

    static (UnusedState State, string? Reason) State(UnusedCode.Pending p, UnusedLiveness live)
    {
        var c = p.C;
        if (p.NotEvaluated is { } notEvaluated) return (UnusedState.NotEvaluated, notEvaluated);
        if (c.Root is { } root && (!c.TypeBound || live.IsLive(c.Id))) return (UnusedState.Rooted, root);
        if (c.ValueMember ? live.LiveRead(c.Id) : live.LiveReferenced(c.Id)) return (UnusedState.Used, null);
        if (p.OtherRoot is { } other) return (UnusedState.Rooted, other);
        if (c.ValueMember && live.Written(c)) return Written(c, live);
        return (live.IsTestOnly(c.Id) ? UnusedState.UsedOnlyByTests : UnusedState.Unused, null);
    }

    /// <summary>Written by live code and not read by it: excluded (rooted with the reason), read only by tests (the test
    /// label) or unread.</summary>
    static (UnusedState State, string? Reason) Written(UnusedCandidate c, UnusedLiveness live)
    {
        bool testRead = live.TestRead(c.Id).Read;
        if (c.UnreadExclusion is { } why) return (UnusedState.Rooted, (testRead ? "read only by tests, not reported: " : "written but never read, not reported: ") + why);
        return (testRead ? UnusedState.UsedOnlyByTests : UnusedState.Unread, null);
    }

    static DeadDetail Detail(string id, UnusedLiveness live, IReadOnlyDictionary<string, string> places)
    {
        var facts = live.Facts(id);
        string Place(string other) => places.GetValueOrDefault(other, other);
        return new DeadDetail([.. facts.Users.Select(Place).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], facts.Depth,
            facts.Via is { } via ? Place(via) : null);
    }
}
