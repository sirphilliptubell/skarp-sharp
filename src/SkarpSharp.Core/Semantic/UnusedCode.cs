using System.Globalization;
using System.Text.RegularExpressions;
using SkarpSharp.Findings;
using SkarpSharp.Model;

namespace SkarpSharp.Semantic;

/// <summary>What the solution-wide unused-code analysis needs besides the facts.</summary>
public sealed class UnusedCodeOptions
{
    /// <summary>Every target framework of each project (absolute project path), as the design-time build listed them.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> ProjectFrameworks { get; init; }
    /// <summary>Assembly names of C# projects of the solution that gave no compiler arguments.</summary>
    public IReadOnlyList<string> FailedAssemblies { get; init; } = [];
    /// <summary><c>unusedCode.allow</c>: documentation ids (<c>M:Ns.Type.Member</c>) or names (<c>Ns.Type.Member</c>), with <c>*</c> and <c>?</c>.</summary>
    public IReadOnlyList<string> Allow { get; init; } = [];
    public string Root { get; init; } = "";
    /// <summary>Source files (absolute paths) that only target frameworks not analyzed compile (conditional
    /// <c>Compile</c> items): the identifiers in their text count as used names, as inactive <c>#if</c> text does.</summary>
    public IReadOnlyList<string> UnanalyzedOnlyFiles { get; init; } = [];
    /// <summary>Files (relative to the root) that some target framework not analyzed compiles: there a call may bind
    /// to an extension method declared in them where the analyzed target binds it to a framework member.</summary>
    public IReadOnlySet<string> UnanalyzedCompiledFiles { get; init; } = new HashSet<string>();
    /// <summary>Reads a source file's text (null when it cannot be read).</summary>
    public Func<string, string?> ReadText { get; init; } = ReadFile;

    static string? ReadFile(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>A reported candidate: rule <c>unused-member</c>, <c>unused-type</c> or the label <c>used-only-by-tests</c>,
/// the target framework whose output file it goes to (the highest that declares it), and the scope when not every
/// target framework that could use it was analyzed (for example <c>net10.0 only; --all-targets for all</c>), else null.</summary>
public sealed record UnusedVerdict(UnusedCandidate Candidate, string Rule, string TargetFramework, string? Scope = null, DeadDetail? Detail = null);

/// <summary>Why an unused or test-only candidate is reported: the places of its unused users (empty: no reference
/// at all), its depth below unreferenced code, and the test-only place it is reached through (null: tests use it directly).
/// <paramref name="ReadOnlyByTests"/>: a field or auto-property live code writes and only tests read
/// (<paramref name="Via"/>: the test-only reader, null when a test reads it directly).</summary>
public sealed record DeadDetail(IReadOnlyList<string> UsedOnlyBy, int Depth, string? Via, bool ReadOnlyByTests = false)
{
    /// <summary>The label's detail: <c>read only by tests</c>, <c>read only by tests via X</c>, <c>via X</c>, or null.</summary>
    public string? TestDetail => ReadOnlyByTests ? "read only by tests" + (Via is { } v ? " via " + v : "") : Via is { } via ? "via " + via : null;

    /// <summary><c>X</c> or <c>X (and N more)</c>.</summary>
    public string Users => UsedOnlyBy.Count == 0 ? "" : UsedOnlyBy[0] + (UsedOnlyBy.Count > 1 ? $" (and {UsedOnlyBy.Count - 1} more)" : "");
}

/// <summary>
/// How a candidate was judged. The order is the precedence when the copies of a shared file disagree (a file compiled
/// into several projects is one place): any use or root keeps it, then not evaluated, then the test label, then unused.
/// </summary>
public enum UnusedState
{
    Used,
    /// <summary>A root keeps it; the reason names the root.</summary>
    Rooted,
    /// <summary>It cannot be judged; the reason says why.</summary>
    NotEvaluated,
    /// <summary>A field or auto-property written but never read by live code.</summary>
    Unread,
    UsedOnlyByTests,
    InsideTypeUsedOnlyByTests,
    Unused,
    InsideUnusedType,
}

/// <summary>How a candidate was judged, with the reason for <see cref="UnusedState.Rooted"/> and
/// <see cref="UnusedState.NotEvaluated"/> (null otherwise).</summary>
public sealed record UnusedClassification(UnusedCandidate Candidate, UnusedState State, string? Reason = null, DeadDetail? Detail = null)
{
    /// <summary>The state as text: <c>unused</c> (or <c>unused (used only by unused code: X)</c>), <c>used</c>,
    /// <c>unread</c>, <c>used-only-by-tests</c> (or <c>... (via X)</c>, <c>... (read only by tests)</c>, <c>... (read only by tests via X)</c>), <c>rooted: reason</c>, <c>not evaluated: reason</c>,
    /// <c>inside an unused type</c> or <c>inside a type used only by tests</c>.</summary>
    public string Text => State switch
    {
        UnusedState.Used => "used",
        UnusedState.Rooted => "rooted: " + Reason,
        UnusedState.NotEvaluated => "not evaluated: " + Reason,
        UnusedState.Unread => "unread",
        UnusedState.UsedOnlyByTests => Detail?.TestDetail is { } detail ? $"used-only-by-tests ({detail})" : "used-only-by-tests",
        UnusedState.InsideTypeUsedOnlyByTests => "inside a type used only by tests",
        UnusedState.Unused => Detail is { UsedOnlyBy.Count: > 0 } d ? $"unused (used only by unused code: {d.Users})" : "unused",
        _ => "inside an unused type",
    };
}

public sealed class UnusedCodeAnalysis
{
    public required UnusedCodeSummary Summary { get; init; }
    public List<UnusedVerdict> Verdicts { get; } = [];
    public List<UnusedClassification> Classified { get; } = [];
    /// <summary><c>path|place</c> of candidates not evaluated (their baseline entries are neither fixed nor new).</summary>
    public HashSet<string> NotEvaluatedPlaces { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Solution-wide unused code over the per-compilation facts of <see cref="ReferenceIndex"/>.
/// A candidate (private or internal member or type) is reported when it is unused in the analyzed targets: its type has
/// no unresolved symbols, no root applies (symbol roots, InternalsVisibleTo a friend outside the solution, the
/// allowlist, typeof or DynamicallyAccessedMembers on a containing type, its name in a string literal, dynamic or
/// invalid code, in inactive #if code, or in a file only targets not analyzed compile, native layout of struct fields)
/// and no compiled target of any project references it from outside itself. A multi-targeted project analyzed for some
/// of its targets (by default the highest) is reported from them, with that scope on the finding; with
/// every target analyzed (<c>--all-targets</c>) the result is certain. A candidate referenced only from test projects
/// gets the label <c>used-only-by-tests</c>. Members inside an unused type are not reported on their own (the type is).
/// </summary>
public static class UnusedCode
{
    /// <summary>Partial projects named in the summary note before "and N more".</summary>
    const int ListedPartialProjects = 3;

    public static UnusedCodeAnalysis Analyze(IReadOnlyList<ProjectFacts> facts, UnusedCodeOptions options)
    {
        var solution = new SolutionFacts(facts, options);
        var analysis = new UnusedCodeAnalysis { Summary = new UnusedCodeSummary() };
        var pending = new List<Pending>();
        foreach (var g in solution.Projects) pending.AddRange(solution.Prepare(g, analysis.Summary));
        var liveness = solution.Liveness(pending);
        var places = pending.GroupBy(p => p.C.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().C.PlaceKey, StringComparer.Ordinal);
        Combine(FoldIntoTypes([.. pending.Select(p => UnusedJudge.Judge(p, liveness, places))]), analysis);
        var summary = analysis.Summary;
        if (solution.Partial.Count > 0) summary.Notes.Add(PartialNote(solution.Partial, options.UnanalyzedOnlyFiles.Count));
        if (options.FailedAssemblies.Count > 0)
            summary.Notes.Add($"{options.FailedAssemblies.Count} project{(options.FailedAssemblies.Count == 1 ? "" : "s")} gave no compiler arguments: internal members visible to them are not evaluated");
        return analysis;
    }

    static string PartialNote(Dictionary<string, PartialProject> partial, int files)
    {
        int n = partial.Count;
        var listed = string.Join("; ", partial.Values.Take(ListedPartialProjects).Select(p => p.Note)) + (n > ListedPartialProjects ? $"; and {n - ListedPartialProjects} more" : "");
        return $"unused code of {n} multi-targeted project{(n == 1 ? "" : "s")} from the analyzed target{(partial.Values.Any(p => p.Analyzed.Count > 1) ? "s" : "")} only ({listed}); " +
            $"names in inactive #if code and in {files} file{(files == 1 ? "" : "s")} compiled only by targets not analyzed count as used; --all-targets analyzes every target";
    }

    /// <summary>A multi-targeted project analyzed for some of its targets: the analyzed and the other targets,
    /// and the note text (<c>Lib/Lib.csproj: net10.0 of net10.0, net8.0</c>).</summary>
    sealed record PartialProject(List<string> Analyzed, List<string> NotAnalyzed, string Note);

    /// <summary>One project's copy of a candidate with its state (and the reason for rooted and not evaluated), the
    /// project's summary row, the finding scope and (unused, test-only) the dead detail.</summary>
    internal sealed record Copy(UnusedCandidate C, string Tfm, UnusedState State, string? Reason, UnusedCodeProject Row, string? Scope, DeadDetail? Detail = null);

    /// <summary>One project's copy of a candidate before liveness: why it cannot be judged, and the root decided over
    /// the whole solution (names, reflection, friends, allowlist, layout), or null.</summary>
    internal sealed record Pending(UnusedCandidate C, string Tfm, UnusedCodeProject Row, string? Scope, string? NotEvaluated, string? OtherRoot);

    /// <summary>The reason counts of the summary (grouped by the kind of reason).</summary>
    sealed class ReasonCounts
    {
        public Dictionary<string, int> Rooted { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> NotEvaluated { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// A file compiled into several projects (linked or shared sources) is one place: it is unused only when every
    /// project's copy is unused (for example the shared framework shims of Open-XML-SDK). Counts, verdicts and
    /// the not-evaluated places come from the deciding copy.
    /// </summary>
    static void Combine(List<Copy> copies, UnusedCodeAnalysis analysis)
    {
        var counts = new ReasonCounts();
        foreach (var place in copies.GroupBy(x => x.C.Path + "|" + x.C.PlaceKey, StringComparer.Ordinal))
        {
            // The enum order is the precedence: the first copy with the lowest state decides.
            var decided = place.MinBy(x => x.State)!;
            foreach (var copy in place) CountRow(copy.Row, decided.State);
            Add(decided, analysis, counts);
        }
        analysis.Summary.RootReasons = Counts(counts.Rooted);
        analysis.Summary.NotEvaluatedReasons = Counts(counts.NotEvaluated);
    }

    /// <summary>The deciding copy of a place: its classification, summary counts and verdict.</summary>
    static void Add(Copy decided, UnusedCodeAnalysis analysis, ReasonCounts counts)
    {
        var (c, tfm, state, reason, _, scope, detail) = decided;
        var summary = analysis.Summary;
        analysis.Classified.Add(new UnusedClassification(c, state, reason, detail));
        summary.Candidates++;
        switch (state)
        {
            case UnusedState.Unused:
                analysis.Verdicts.Add(new UnusedVerdict(c, c.IsType ? RuleIds.UnusedType : RuleIds.UnusedMember, tfm, scope, detail));
                if (c.IsType) summary.UnusedTypes++; else summary.UnusedMembers++;
                if (detail is { UsedOnlyBy.Count: > 0 }) summary.UsedOnlyByUnusedCode++;
                break;
            case UnusedState.Unread:
                analysis.Verdicts.Add(new UnusedVerdict(c, RuleIds.UnreadMember, tfm, scope));
                summary.UnreadMembers++;
                break;
            case UnusedState.UsedOnlyByTests:
                analysis.Verdicts.Add(new UnusedVerdict(c, RuleIds.UsedOnlyByTests, tfm, scope, detail));
                summary.UsedOnlyByTests++;
                break;
            case UnusedState.Used: summary.Used++; break;
            case UnusedState.InsideUnusedType or UnusedState.InsideTypeUsedOnlyByTests: summary.InsideReportedTypes++; break;
            case UnusedState.Rooted: summary.Rooted++; Count(counts.Rooted, reason!); break;
            case UnusedState.NotEvaluated:
                summary.NotEvaluated++;
                Count(counts.NotEvaluated, reason!);
                analysis.NotEvaluatedPlaces.Add(c.Path + "|" + c.PlaceKey);
                break;
        }
    }

    static void CountRow(UnusedCodeProject row, UnusedState state)
    {
        row.Candidates++;
        if (state == UnusedState.Unused) row.Unused++;
        else if (state == UnusedState.Unread) row.Unread++;
        else if (state == UnusedState.UsedOnlyByTests) row.UsedOnlyByTests++;
        else if (state == UnusedState.NotEvaluated) row.NotEvaluated++;
    }

    /// <summary>What a project's friend assemblies mean for its candidates: a friend not fully analyzed (not evaluated),
    /// a friend outside the solution (root), and the finding scopes of a partially analyzed project or friend.</summary>
    readonly record struct FriendView(string? PartialFriend, string? OutsideFriend, string? OwnScope, string? FriendScope);

    /// <summary>The solution-wide facts: reference edges, the solution roots, multi-targeted projects analyzed for some
    /// targets, projects not analyzed.</summary>
    sealed class SolutionFacts
    {
        readonly HashSet<ReferenceEdge> _edges = [];
        readonly SolutionRoots _roots;
        readonly HashSet<string> _walked;
        readonly HashSet<string> _partialAssemblies;
        /// <summary>Assembly names of multi-targeted projects analyzed for some targets, with the analyzed ones.</summary>
        readonly Dictionary<string, string> _partialByAssembly = new(StringComparer.OrdinalIgnoreCase);
        public List<IGrouping<string, ProjectFacts>> Projects { get; }
        /// <summary>Multi-targeted projects analyzed for some of their targets, by project file.</summary>
        public Dictionary<string, PartialProject> Partial { get; } = new(StringComparer.OrdinalIgnoreCase);

        public SolutionFacts(IReadOnlyList<ProjectFacts> facts, UnusedCodeOptions options)
        {
            _roots = new SolutionRoots(options);
            foreach (var f in facts)
            {
                _edges.UnionWith(f.Edges);
                _roots.Add(f);
            }
            Projects = [.. facts.GroupBy(f => f.ProjectFile, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.First().Project, StringComparer.Ordinal)];
            _walked = new HashSet<string>(facts.Select(f => f.AssemblyName), StringComparer.OrdinalIgnoreCase);
            _partialAssemblies = new HashSet<string>(options.FailedAssemblies, StringComparer.OrdinalIgnoreCase);
            foreach (var g in Projects)
            {
                _roots.AddInactiveWords(g);
                AddPartial(g, options);
            }
        }

        /// <summary>A multi-targeted project analyzed for some targets is reported from them; uses only the
        /// others have count through names.</summary>
        void AddPartial(IGrouping<string, ProjectFacts> g, UnusedCodeOptions options)
        {
            var analyzed = g.Select(f => f.TargetFramework).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(TargetFrameworks.Key).ToList();
            var all = options.ProjectFrameworks.TryGetValue(g.Key, out var listed) ? listed : analyzed;
            var others = all.Where(t => !analyzed.Contains(t, StringComparer.OrdinalIgnoreCase)).OrderByDescending(TargetFrameworks.Key).ToList();
            if (others.Count == 0) return;
            Partial[g.Key] = new PartialProject(analyzed, others, $"{g.First().Project}: {string.Join(", ", analyzed)} of {string.Join(", ", all.OrderByDescending(TargetFrameworks.Key))}");
            foreach (var f in g) _partialByAssembly.TryAdd(f.AssemblyName, string.Join(", ", analyzed));
        }

        /// <summary>One project's candidates (all its targets merged by id) with what is known before liveness.</summary>
        public List<Pending> Prepare(IGrouping<string, ProjectFacts> g, UnusedCodeSummary summary)
        {
            var ordered = g.OrderByDescending(f => TargetFrameworks.Key(f.TargetFramework)).ToList();
            var row = Row(g.Key, ordered);
            summary.Projects.Add(row);
            var view = Friends(g.Key, ordered);
            return [.. Merge(ordered).OrderBy(x => x.C.Path, StringComparer.Ordinal).ThenBy(x => x.C.Line).ThenBy(x => x.C.PlaceKey, StringComparer.Ordinal)
                .Select(x => Pend(x.C, x.Tfm, row, view))];
        }

        /// <summary>Liveness over the whole solution's edges: seeds are candidates not evaluated, with an
        /// unconditional symbol root or with a solution root.</summary>
        public UnusedLiveness Liveness(List<Pending> pending) => new(_edges, pending.Select(p => new LivenessNode(p.C.Id,
            Seed: p.NotEvaluated is not null || p.OtherRoot is not null || (p.C.Root is not null && !p.C.TypeBound),
            Bound: p.C.TypeBound || p.C.Initialized, Container: p.C.Containers.Count > 0 ? p.C.Containers[0] : null)));

        /// <summary>The project's summary row (the counts are added by <see cref="CountRow"/>).</summary>
        UnusedCodeProject Row(string projectFile, List<ProjectFacts> ordered)
        {
            var first = ordered[0];
            return new UnusedCodeProject
            {
                Path = first.Project, Test = first.IsTest, TargetFrameworks = [.. ordered.Select(f => f.TargetFramework).Distinct(StringComparer.OrdinalIgnoreCase)],
                TargetFrameworksNotAnalyzed = Partial.GetValueOrDefault(projectFile)?.NotAnalyzed,
                NotEvaluatedReason = ordered.Select(f => f.NotEvaluated).FirstOrDefault(r => r is not null),
            };
        }

        FriendView Friends(string projectFile, List<ProjectFacts> ordered)
        {
            var friends = ordered.SelectMany(f => f.Friends).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // The finding scope: the project's own analyzed targets, else those of a friend that sees its internals.
            var ownScope = Partial.GetValueOrDefault(projectFile) is { } own ? $"{string.Join(", ", own.Analyzed)} only; --all-targets for all" : null;
            var friendScope = friends.FirstOrDefault(_partialByAssembly.ContainsKey) is { } pf ? $"friend {pf}: {_partialByAssembly[pf]} only; --all-targets for all" : null;
            return new FriendView(
                friends.FirstOrDefault(_partialAssemblies.Contains),
                friends.FirstOrDefault(x => !_walked.Contains(x) && !_partialAssemblies.Contains(x)),
                ownScope, friendScope);
        }

        /// <summary>One candidate per id: the highest target's record, roots and not-evaluated reasons of any target.</summary>
        static IEnumerable<(UnusedCandidate C, string Tfm)> Merge(List<ProjectFacts> ordered)
        {
            var merged = new Dictionary<string, (UnusedCandidate C, string Tfm)>(StringComparer.Ordinal);
            foreach (var f in ordered)
                foreach (var c in f.Candidates)
                {
                    if (!merged.TryGetValue(c.Id, out var m)) { merged[c.Id] = (c, f.TargetFramework); continue; }
                    m.C.Root ??= c.Root;
                    m.C.NotEvaluated ??= c.NotEvaluated;
                }
            return merged.Values;
        }

        Pending Pend(UnusedCandidate c, string tfm, UnusedCodeProject row, FriendView view)
        {
            var notEvaluated = row.NotEvaluatedReason ?? c.NotEvaluated
                ?? (c.FriendVisible && view.PartialFriend is not null ? $"friend assembly {view.PartialFriend} was not fully analyzed" : null);
            return new Pending(c, tfm, row, view.OwnScope ?? (c.FriendVisible ? view.FriendScope : null), notEvaluated, _roots.OtherRoot(c, view.OutsideFriend));
        }
    }

    /// <summary>The roots decided over the whole solution: InternalsVisibleTo outside it, the allowlist, reflection
    /// (typeof, [DynamicallyAccessedMembers], [RequiresUnreferencedCode] calls), names (strings, dynamic and invalid code,
    /// inactive #if code, files of targets not analyzed, framework calls of polyfill extensions), native struct layout.</summary>
    sealed class SolutionRoots(UnusedCodeOptions options)
    {
        readonly HashSet<string> _unreferencedCode = new(StringComparer.Ordinal);
        readonly HashSet<string> _names = new(StringComparer.Ordinal);
        readonly HashSet<string> _inactive = new(StringComparer.Ordinal);
        readonly HashSet<string> _unanalyzedWords = UnanalyzedWords(options);
        readonly HashSet<string> _externalCalls = new(StringComparer.Ordinal);
        readonly HashSet<string> _reflected = new(StringComparer.Ordinal);
        readonly HashSet<string> _layout = new(StringComparer.Ordinal);
        readonly List<(Regex Glob, string Pattern)> _allow = [.. options.Allow.Select(p => (Glob(p), p))];

        static HashSet<string> UnanalyzedWords(UnusedCodeOptions options)
        {
            var words = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in options.UnanalyzedOnlyFiles)
                if (options.ReadText(file) is { } text) ReferenceIndex.AddIdentifierWords(text, words);
            return words;
        }

        public void Add(ProjectFacts f)
        {
            _unreferencedCode.UnionWith(f.UnreferencedCodeTypes);
            _names.UnionWith(f.Names);
            _reflected.UnionWith(f.ReflectedTypes);
            _layout.UnionWith(f.LayoutTypes);
            _externalCalls.UnionWith(f.ExternalCallNames);
        }

        /// <summary>Words of inactive code root names only when inactive in every analyzed target of the project (active
        /// code was walked).</summary>
        public void AddInactiveWords(IGrouping<string, ProjectFacts> g)
        {
            HashSet<string>? common = null;
            foreach (var f in g)
                if (common is null) common = new HashSet<string>(f.InactiveWords, StringComparer.Ordinal);
                else common.IntersectWith(f.InactiveWords);
            _inactive.UnionWith(common ?? []);
        }

        /// <summary>Roots decided over the whole solution, strongest first.</summary>
        public string? OtherRoot(UnusedCandidate c, string? outsideFriend) =>
            FriendRoot(c, outsideFriend) ?? AllowRoot(c) ?? ReflectedRoot(c) ?? UnreferencedCodeRoot(c) ?? NameRoot(c.Name, c.Extension, c.Path) ?? ExtensionTypeRoot(c) ?? LayoutRoot(c);

        /// <summary>The candidate or a containing type was passed to a [RequiresUnreferencedCode] method.</summary>
        string? UnreferencedCodeRoot(UnusedCandidate c)
        {
            if (c.Containers.FirstOrDefault(_unreferencedCode.Contains) is { } t) return $"passed to a RequiresUnreferencedCode method ({Short(t)})";
            return c.IsType && _unreferencedCode.Contains(c.Id) ? "passed to a RequiresUnreferencedCode method" : null;
        }

        static string? FriendRoot(UnusedCandidate c, string? outsideFriend) =>
            c.FriendVisible && outsideFriend is not null ? $"InternalsVisibleTo {outsideFriend} (outside the solution)" : null;

        string? AllowRoot(UnusedCandidate c)
        {
            var docId = c.Id[(c.Id.IndexOf('|') + 1)..];
            foreach (var (glob, pattern) in _allow)
                if (glob.IsMatch(IsDocId(pattern) ? docId : c.FullName)) return $"allowlist {pattern} (unusedCode.allow)";
            return null;
        }

        string? ReflectedRoot(UnusedCandidate c)
        {
            if (c.Containers.FirstOrDefault(_reflected.Contains) is { } r) return $"typeof or [DynamicallyAccessedMembers] on {Short(r)}";
            return c.IsType && _reflected.Contains(c.Id) ? "typeof or [DynamicallyAccessedMembers]" : null;
        }

        /// <summary>A call to an extension method names the method, not its type: the type is kept with its methods.</summary>
        string? ExtensionTypeRoot(UnusedCandidate c)
        {
            foreach (var e in c.ExtensionNames)
                if (NameRoot(e, true, c.Path) is { } kept) return $"contains an extension method kept by its name ({e}: {kept})";
            return null;
        }

        string? LayoutRoot(UnusedCandidate c) =>
            c.StructField && c.Containers.Count > 0 && _layout.Contains(c.Containers[0]) ? "field of a struct with native layout use (pointer, sizeof, extern or interop)" : null;

        /// <summary>Roots by name: string literals, dynamic or invalid code, inactive #if code, files only targets not analyzed
        /// compile, and (extension methods in a file a target not analyzed compiles) a call bound to a framework method of
        /// the same name, which that target may bind to the extension instead (for example Dictionary.Remove(key, out value) in
        /// net10.0, the polyfill in netstandard2.0; Open-XML-SDK CollectionsExtensions and SpanExtensions).</summary>
        string? NameRoot(string name, bool extension, string path)
        {
            if (_names.Contains(name)) return $"name in a string literal, dynamic or invalid code ({name})";
            if (_inactive.Contains(name)) return $"name in inactive #if code ({name})";
            if (_unanalyzedWords.Contains(name)) return $"name in a file compiled only by targets not analyzed ({name})";
            if (extension && MayBindElsewhere(name, path))
                return $"name of a framework method called in the analyzed target; a target not analyzed may bind the call to this extension method ({name})";
            return null;
        }

        bool MayBindElsewhere(string name, string path) => options.UnanalyzedCompiledFiles.Contains(path) && _externalCalls.Contains(name);
    }

    /// <summary>Members and nested types inside an unused type (or a test-only type) are covered by the type's finding.</summary>
    static List<Copy> FoldIntoTypes(List<Copy> states)
    {
        var unusedTypes = TypeIds(states, UnusedState.Unused);
        var testOnlyTypes = TypeIds(states, UnusedState.UsedOnlyByTests);
        return [.. states.Select(s => Fold(s, unusedTypes, testOnlyTypes))];
    }

    static HashSet<string> TypeIds(List<Copy> states, UnusedState state) =>
        states.Where(s => s.C.IsType && s.State == state).Select(s => s.C.Id).ToHashSet(StringComparer.Ordinal);

    static Copy Fold(Copy s, HashSet<string> unusedTypes, HashSet<string> testOnlyTypes)
    {
        if (s.State is not (UnusedState.Unused or UnusedState.UsedOnlyByTests)) return s;
        if (s.C.Containers.Any(unusedTypes.Contains)) return s with { State = UnusedState.InsideUnusedType };
        if (s.State == UnusedState.UsedOnlyByTests && s.C.Containers.Any(testOnlyTypes.Contains)) return s with { State = UnusedState.InsideTypeUsedOnlyByTests };
        return s;
    }

    static string Short(string id)
    {
        var doc = id[(id.IndexOf('|') + 1)..];
        var name = doc.Length > 2 ? doc[2..] : doc;
        int dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[(dot + 1)..];
    }

    static bool IsDocId(string pattern) => pattern.Length > 2 && pattern[1] == ':' && pattern[0] is 'T' or 'M' or 'F' or 'P' or 'E' or 'N';

    static Regex Glob(string pattern) => new("^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    static void Count(Dictionary<string, int> counts, string reason)
    {
        // Group by the kind of reason (the text before details in brackets, parentheses or after a colon).
        int cut = reason.IndexOfAny(['[', '(', ':']);
        var kind = (cut > 0 ? reason[..cut] : reason).Trim();
        counts[kind] = counts.GetValueOrDefault(kind) + 1;
    }

    static List<UnusedCodeCount> Counts(Dictionary<string, int> counts) =>
        [.. counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new UnusedCodeCount { Reason = kv.Key, Count = kv.Value })];

    /// <summary>The findings of the verdicts (notice level); rules switched off give none. Each by
    /// the settings of its candidate's file (<paramref name="rulesOf"/>, null: <paramref name="settings"/>).</summary>
    public static List<Finding> Findings(IEnumerable<UnusedVerdict> verdicts, RuleSettings settings, Func<string, RuleSettings>? rulesOf = null)
    {
        var list = new List<Finding>();
        foreach (var v in verdicts)
        {
            var active = (rulesOf is null ? settings : rulesOf(v.Candidate.Path))[v.Rule];
            if (active.Notice is not { } level) continue;
            var c = v.Candidate;
            bool unused = v.Rule is RuleIds.UnusedMember or RuleIds.UnusedType;
            list.Add(new Finding
            {
                Rule = v.Rule, Severity = "notice", Path = c.Path, Line = c.Line, Member = c.PlaceKey, PlaceKey = c.PlaceKey, TypePlace = c.IsType,
                Preset = active.NoticePreset, ThresholdSource = level.Source,
                Message = Message(v) + (v.Scope is { } scope ? $" ({scope})" : ""),
                UsedOnlyBy = unused && v.Detail is { UsedOnlyBy.Count: > 0 } d ? [.. d.UsedOnlyBy] : null,
                Depth = unused ? v.Detail?.Depth ?? 0 : null,
            });
        }
        return list;
    }

    static string Message(UnusedVerdict v)
    {
        var c = v.Candidate;
        var what = $"{c.Accessibility} {c.Kind} {c.Display}";
        return v.Rule switch
        {
            RuleIds.UnreadMember => $"{what} is written but never read; check its writes for side effects before removing",
            RuleIds.UsedOnlyByTests => TestOnlyMessage(what, v.Detail),
            _ => v.Detail is { UsedOnlyBy.Count: > 0 } d ? $"{what} is used only by unused code: {d.Users}" : $"{what} has no reference in the solution",
        };
    }

    static string TestOnlyMessage(string what, DeadDetail? d)
    {
        var via = d?.Via is { } v ? $" (via {v})" : "";
        if (d is { ReadOnlyByTests: true }) return $"{what} is written by production code but read only by tests{via}";
        return via.Length > 0 ? $"{what} is used only by tests{via}" : $"{what} is referenced only from test projects";
    }

    /// <summary>
    /// The source files that only target frameworks not analyzed compile (for example a conditional
    /// <c>&lt;Compile Include&gt;</c> for net462), from the compiler arguments the design-time build recorded for every
    /// target (no extra compile). Absolute paths, sorted.
    /// </summary>
    public static List<string> FilesOnlyInUnanalyzedTargets(IEnumerable<(string Project, string TargetFramework, IReadOnlyList<string> Sources, bool Analyzed)> inputs)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var analyzed = new HashSet<string>(cmp);
        var other = new HashSet<string>(cmp);
        foreach (var i in inputs) (i.Analyzed ? analyzed : other).UnionWith(i.Sources);
        other.ExceptWith(analyzed);
        return [.. other.Order(StringComparer.Ordinal)];
    }

    /// <summary>Every source file (relative to <paramref name="root"/>) that some target framework not analyzed compiles.</summary>
    public static HashSet<string> FilesInUnanalyzedTargets(IEnumerable<(string Project, string TargetFramework, IReadOnlyList<string> Sources, bool Analyzed)> inputs, string root) =>
        new(inputs.Where(i => !i.Analyzed).SelectMany(i => i.Sources).Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')), StringComparer.Ordinal);

    public static string Line(UnusedCodeSummary s)
    {
        static string N(int v) => v.ToString("N0", CultureInfo.InvariantCulture);
        return $"Unused code: {N(s.Candidates)} private and internal candidates; {N(s.UnusedMembers)} unused members, {N(s.UnusedTypes)} unused types " +
            $"({N(s.UsedOnlyByUnusedCode)} used only by unused code), {N(s.UnreadMembers)} written but never read, " +
            $"{N(s.UsedOnlyByTests)} used only by tests; {N(s.Rooted)} rooted, {N(s.NotEvaluated)} not evaluated.";
    }
}
