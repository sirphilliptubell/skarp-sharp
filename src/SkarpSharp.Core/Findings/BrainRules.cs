using System.Globalization;
using SkarpSharp.Model;

namespace SkarpSharp.Findings;

/// <summary>
/// The composite rules <c>brain-method</c> and <c>brain-class</c>: groups of findings other rules already made, found after those rules ran.
/// A brain method is a member with every part finding (by default long-method, complex-method and deep-nesting); a brain
/// class is a class, struct or record with a large-type finding, at least one brain method and at least
/// <c>minMethods</c> methods (preset <c>lanza-marinescu-2006</c> adds the book's size legs). The composites change no
/// finding: baseline, gate and score keep working on the parts.
/// </summary>
public static class BrainRules
{
    public const string OursForm = "ours";
    public const string BookForm = "lanza-marinescu-2006";

    /// <summary>The member rules a brain method may be made of (the <c>parts</c> parameter).</summary>
    public static readonly IReadOnlyList<string> PartRules =
    [
        RuleIds.LongMethod, RuleIds.ComplexMethod, RuleIds.DeepNesting, RuleIds.BumpyRoad, RuleIds.ComplexCondition,
        RuleIds.TooManyParameters, RuleIds.PathExplosion,
    ];

    /// <summary>The settings of the composites in a run; null when both are off.</summary>
    public sealed record Config(bool Method, bool Class, IReadOnlyList<string> Parts, int MinParts, int MinMethods, string Form)
    {
        public bool IsPart(string rule) => Parts.Contains(rule);
    }

    /// <summary>A <c>parts</c> value as a rule list (in the given order, without repeats); null when invalid.</summary>
    public static List<string>? ParseParts(string value)
    {
        var list = new List<string>();
        foreach (var p in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!PartRules.Contains(p)) return null;
            if (!list.Contains(p)) list.Add(p);
        }
        return list.Count == 0 ? null : list;
    }

    public static Config? From(RuleSettings settings)
    {
        var m = settings[RuleIds.BrainMethod];
        var c = settings[RuleIds.BrainClass];
        if (!m.Enabled && !c.Enabled) return null;
        var parts = ParseParts(m.Parameters.GetValueOrDefault("parts") ?? "") ?? [RuleIds.LongMethod, RuleIds.ComplexMethod, RuleIds.DeepNesting];
        var min = m.Parameters.GetValueOrDefault("minParts") is { } mp && mp != "all" ? Math.Min(int.Parse(mp, CultureInfo.InvariantCulture), parts.Count) : parts.Count;
        int minMethods = int.Parse(c.Parameters.GetValueOrDefault("minMethods") ?? "10", CultureInfo.InvariantCulture);
        return new Config(m.Enabled, c.Enabled, parts, min, minMethods, c.Parameters.GetValueOrDefault("preset") ?? OursForm);
    }

    /// <summary>One composite found over a set of findings: its rule, the finding it is anchored at, its parts and (brain
    /// class) its brain methods.</summary>
    public sealed record Group(string Rule, Finding Anchor, List<Finding> Parts, List<Group> Methods, TypeSummary? Type);

    /// <summary>The composites' settings per path, the settings of the file a composite is reported in
    /// (a brain method: its member's file; a brain class: its large-type finding's file), one <see cref="Config"/> per
    /// distinct <see cref="RuleSettings"/>; null for a file with both composites off.</summary>
    public sealed class PathConfig(Func<string, RuleSettings> of)
    {
        readonly Dictionary<RuleSettings, Config?> _memo = new(ReferenceEqualityComparer.Instance);

        public Config? For(string path)
        {
            var s = of(path);
            if (!_memo.TryGetValue(s, out var c)) _memo[s] = c = From(s);
            return c;
        }
    }

    /// <summary>
    /// The composites of <paramref name="findings"/> (brain methods first, in finding order; then brain classes in type
    /// order). Brain methods are found also where only brain-class is on (they are its parts); they are returned as
    /// groups of their own only where brain-method is on, or with <paramref name="everyMethod"/>.
    /// </summary>
    public static List<Group> Detect(IReadOnlyList<Finding> findings, IReadOnlyList<TypeSummary> types, PathConfig pc, bool everyMethod = false)
    {
        var methods = BrainMethods(findings, pc);
        var groups = new List<Group>();
        groups.AddRange(methods.Where(m => everyMethod || pc.For(m.Anchor.Path)!.Method));
        groups.AddRange(BrainClasses(findings, types, methods, pc));
        return groups;
    }

    static List<Group> BrainMethods(IReadOnlyList<Finding> findings, PathConfig pc)
    {
        var byPlace = new Dictionary<(string Path, string Place), List<Finding>>();
        var order = new List<(string, string)>();
        foreach (var f in findings)
        {
            if (f.TypePlace || f.PathlessId || f.Member is null || pc.For(f.Path) is not { } c || !c.IsPart(f.Rule)) continue;
            var key = (f.Path, FindingEngine.Place(f));
            if (!byPlace.TryGetValue(key, out var list)) { byPlace[key] = list = []; order.Add(key); }
            list.Add(f);
        }
        var groups = new List<Group>();
        foreach (var key in order)
        {
            var c = pc.For(key.Item1)!;
            var parts = byPlace[key];
            if (parts.Select(p => p.Rule).Distinct(StringComparer.Ordinal).Count() < c.MinParts) continue;
            var ordered = parts.OrderBy(p => c.Parts.ToList().IndexOf(p.Rule)).ThenBy(p => p.Line).ToList();
            groups.Add(new Group(RuleIds.BrainMethod, ordered[0], ordered, [], null));
        }
        return groups;
    }

    static IEnumerable<Group> BrainClasses(IReadOnlyList<Finding> findings, IReadOnlyList<TypeSummary> types, List<Group> methods, PathConfig pc)
    {
        var large = new Dictionary<string, Finding>(StringComparer.Ordinal);
        foreach (var f in findings)
            if (f.Rule == RuleIds.LargeType && f.TypePlace && f.PlaceKey is { } name) large.TryAdd(name, f);
        var byType = methods.GroupBy(m => TypeOf(FindingEngine.Place(m.Anchor)), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var t in types)
        {
            // Static classes are exempt, in both presets (they are rarely a design problem of this kind).
            if (t.Kind is not ("class" or "struct" or "record" or "record struct") || t.Static) continue;
            if (!large.TryGetValue(t.Name, out var lt) || !byType.TryGetValue(t.Name, out var bm)) continue;
            // A brain class takes the settings of the file its large-type finding is reported in.
            if (pc.For(lt.Path) is not { Class: true } c) continue;
            if (t.Methods < c.MinMethods || (c.Form == BookForm && !BookLegs(t, bm.Count))) continue;
            yield return new Group(RuleIds.BrainClass, lt, [lt, .. bm.SelectMany(m => m.Parts)], bm, t);
        }
    }

    /// <summary>Lanza and Marinescu 2006 (figures 5.11 and 5.12, Java values secondhand): (2+ brain methods and 195+ source
    /// lines, or 1 brain method with 390+ source lines and WMC 94+) and WMC 47+; the TCC leg needs the semantic tier.</summary>
    public static bool BookLegs(TypeSummary t, int brainMethods) =>
        ((brainMethods >= 2 && t.SourceLines >= 195) || (brainMethods == 1 && t.SourceLines >= 390 && t.SumCyclomatic >= 94)) && t.SumCyclomatic >= 47;

    /// <summary>The type of a member key (<c>Ns.Type::Run#2</c> gives <c>Ns.Type</c>); "" for top-level code.</summary>
    static string TypeOf(string key)
    {
        int sep = key.IndexOf("::", StringComparison.Ordinal);
        return sep < 0 ? "" : key[..sep];
    }

    /// <summary>The member part of a key without the overload ordinal (<c>Ns.Type::Run#2</c> gives <c>Run</c>).</summary>
    static string MemberOf(string key)
    {
        int sep = key.IndexOf("::", StringComparison.Ordinal);
        var rest = sep < 0 ? key : key[(sep + 2)..];
        int hash = rest.LastIndexOf('#');
        if (hash > 0 && rest[(hash + 1)..].All(char.IsAsciiDigit)) rest = rest[..hash];
        return rest switch { ".ctor" => "ctor", ".cctor" => "cctor", _ => rest };
    }

    /// <summary>
    /// Before the per-finding suppression split, the parts of every composite suppressed for its own
    /// rule (at its anchor's line) are taken out of <paramref name="findings"/> and returned with that suppression.
    /// </summary>
    public static List<(Finding Finding, SuppressionAttribute By, string ByPath)> SuppressComposites(List<Finding> findings, IReadOnlyList<TypeSummary> types,
        PathConfig pc, SuppressionIndex suppressions)
    {
        var hidden = new List<(Finding, SuppressionAttribute, string)>();
        if (suppressions.IsEmpty) return hidden;
        // Detection ignores the rule switches here: a brain-class suppression also covers brain methods when brain-method is off.
        var taken = new HashSet<Finding>(ReferenceEqualityComparer.Instance);
        var all = Detect(findings, types, pc, everyMethod: true);
        foreach (var g in all.Where(g => g.Rule == RuleIds.BrainClass).Concat(all.Where(g => g.Rule == RuleIds.BrainMethod && pc.For(g.Anchor.Path)!.Method)))
        {
            if (suppressions.Find(g.Rule, g.Anchor.Path, g.Anchor.Line) is not { } s) continue;
            foreach (var p in g.Parts)
                if (taken.Add(p)) hidden.Add((p, s.By, s.ByPath));
        }
        if (taken.Count > 0) findings.RemoveAll(taken.Contains);
        return hidden;
    }

    /// <summary>The composites of a run's kept findings (ids assigned), anchored as their anchor finding, in report order.</summary>
    public static List<CompositeFinding> Build(IReadOnlyList<Finding> findings, IReadOnlyList<TypeSummary> types, PathConfig pc)
    {
        var position = new Dictionary<Finding, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < findings.Count; i++) position[findings[i]] = i;
        var composites = new List<(int Position, int Rule, CompositeFinding Composite)>();
        foreach (var g in Detect(findings, types, pc))
            composites.Add((position[g.Anchor], g.Rule == RuleIds.BrainClass ? 1 : 0, Make(g)));
        return [.. composites.OrderBy(x => x.Position).ThenBy(x => x.Rule).Select(x => x.Composite)];
    }

    static CompositeFinding Make(Group g)
    {
        var a = g.Anchor;
        var place = FindingEngine.Place(a);
        var id = FindingEngine.MakeId(g.Rule, a.Path, place);
        return new CompositeFinding
        {
            Id = id, Handle = FindingEngine.HandleOf(id), Rule = g.Rule, Path = a.Path, Line = a.Line, Member = a.Member, PlaceKey = place,
            Severity = g.Parts.Any(p => p.Severity == "warning") ? "warning" : "notice",
            Message = g.Rule == RuleIds.BrainMethod ? MethodMessage(g.Parts) : ClassMessage(g),
            Parts = [.. g.Parts.Select(p => p.Id)],
            BrainMethods = g.Rule == RuleIds.BrainClass ? [.. g.Methods.Select(m => FindingEngine.MakeId(RuleIds.BrainMethod, m.Anchor.Path, FindingEngine.Place(m.Anchor)))] : null,
        };
    }

    /// <summary>The word a composite uses for one of its parts (<c>long</c>, <c>complex</c>, <c>deeply nested</c>).</summary>
    public static string Word(string rule) => rule switch
    {
        RuleIds.LongMethod => "long",
        RuleIds.ComplexMethod => "complex",
        RuleIds.DeepNesting => "deeply nested",
        RuleIds.BumpyRoad => "bumpy",
        RuleIds.ComplexCondition => "complex conditions",
        RuleIds.TooManyParameters => "many parameters",
        RuleIds.PathExplosion => "many paths",
        _ => rule,
    };

    /// <summary><c>long (82 physical lines), complex (cognitive complexity 20), deeply nested (nesting depth 5)</c>.</summary>
    static string MethodMessage(IEnumerable<Finding> parts) => string.Join(", ", parts.Select(p => $"{Word(p.Rule)} ({p.Message})"));

    /// <summary><c>large (WMC 60; also 700 source lines), 25 methods, 2 brain methods: Run, Map</c>.</summary>
    static string ClassMessage(Group g)
    {
        int n = g.Methods.Count;
        var names = g.Methods.Select(m => MemberOf(FindingEngine.Place(m.Anchor)));
        var large = g.Anchor.Message;
        int also = large.IndexOf(" (also ", StringComparison.Ordinal);
        if (also > 0 && large.EndsWith(')')) large = $"{large[..also]}; also {large[(also + " (also ".Length)..^1]}";
        var listed = string.Join(", ", names.Take(NamesShown)) + (n > NamesShown ? $" and {n - NamesShown} more" : "");
        return $"large ({large}), {g.Type!.Methods} methods, {n} brain method{(n == 1 ? "" : "s")}: {listed}";
    }

    /// <summary>A brain class names at most this many brain methods; the rest are counted.</summary>
    const int NamesShown = 5;

    /// <summary>
    /// The composites' state against the baseline, from their parts' states: known when every part is known, new when
    /// every part is new, worsened otherwise; null when the parts were not compared.
    /// </summary>
    public static void ApplyStates(IReadOnlyList<CompositeFinding>? composites, IReadOnlyList<Finding> findings)
    {
        if (composites is null || composites.Count == 0) return;
        var byId = new Dictionary<string, Finding>(StringComparer.Ordinal);
        foreach (var f in findings) byId.TryAdd(f.Id, f);
        foreach (var c in composites)
        {
            var states = c.Parts.Select(id => byId.GetValueOrDefault(id)?.BaselineState).ToList();
            c.BaselineState = states.Any(s => s is null) ? null
                : states.All(s => s == "known") ? "known"
                : states.All(s => s == "new") ? "new"
                : "worsened";
        }
    }

    /// <summary>
    /// A report view's findings with each composite's parts replaced by one stand-in finding (<see cref="Finding.Parts"/>),
    /// in report order. The parts of a brain class's brain methods go into the brain class; findings that are no part
    /// stay as they are. Without composites the list is returned unchanged.
    /// </summary>
    public static List<Finding> Collapse(IReadOnlyList<Finding> findings, IReadOnlyList<CompositeFinding>? composites)
    {
        if (composites is null || composites.Count == 0) return [.. findings];
        var byId = new Dictionary<string, Finding>(StringComparer.Ordinal);
        foreach (var f in findings) byId.TryAdd(f.Id, f);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var standIns = new List<Finding>();
        var methods = composites.Where(c => c.Rule == RuleIds.BrainMethod).ToDictionary(c => c.Id, StringComparer.Ordinal);
        foreach (var c in composites.Where(c => c.Rule == RuleIds.BrainClass))
        {
            var lt = c.Parts.Count > 0 ? byId.GetValueOrDefault(c.Parts[0]) : null;
            if (lt is null) continue;
            var inner = new List<Finding> { lt };
            var methodPartIds = c.Parts.Skip(1).ToHashSet(StringComparer.Ordinal);
            foreach (var m in c.BrainMethods ?? [])
                if (methods.TryGetValue(m, out var bm)) { inner.Add(StandIn(bm, byId)); used.Add(bm.Id); methodPartIds.ExceptWith(bm.Parts); }
            // Brain methods whose own composite is off are listed through their parts.
            inner.AddRange(methodPartIds.Select(byId.GetValueOrDefault).OfType<Finding>());
            standIns.Add(StandIn(c, byId, inner));
            used.UnionWith(c.Parts);
        }
        foreach (var c in methods.Values.Where(m => !used.Contains(m.Id)))
        {
            standIns.Add(StandIn(c, byId));
            used.UnionWith(c.Parts);
        }
        var list = findings.Where(f => !used.Contains(f.Id)).Concat(standIns).ToList();
        FindingEngine.Sort(list);
        return list;
    }

    /// <summary>One stand-in per composite with its parts flattened (SARIF: the composite next to its part results).</summary>
    public static List<Finding> StandIns(IReadOnlyList<Finding> findings, IReadOnlyList<CompositeFinding> composites)
    {
        var byId = new Dictionary<string, Finding>(StringComparer.Ordinal);
        foreach (var f in findings) byId.TryAdd(f.Id, f);
        return [.. composites.Select(c => StandIn(c, byId))];
    }

    static Finding StandIn(CompositeFinding c, Dictionary<string, Finding> byId, List<Finding>? parts = null)
    {
        parts ??= [.. c.Parts.Select(byId.GetValueOrDefault).OfType<Finding>()];
        return new Finding
        {
            Id = c.Id, Handle = c.Handle, Rule = c.Rule, Severity = c.Severity, Path = c.Path, Line = c.Line, Member = c.Member, Message = c.Message,
            BaselineState = c.BaselineState, Commits = parts.FirstOrDefault()?.Commits, PlaceKey = c.PlaceKey, TypePlace = c.Rule == RuleIds.BrainClass,
            Parts = parts,
        };
    }

    /// <summary>The findings a composite stand-in replaces, flattened (a finding that is no stand-in gives itself).</summary>
    public static IEnumerable<Finding> Flatten(Finding f) => f.Parts is { } parts ? parts.SelectMany(Flatten) : [f];
}
