namespace SkarpSharp.Conditionals;

/// <summary>Why a variant is in the plan; also its rank when a plan is cut at the cap.</summary>
public enum VariantRole
{
    /// <summary>A framework tier, or under the <c>symbols</c> setting a framework with no other symbol on.</summary>
    Tier,
    /// <summary>An extra framework variant for a framework branch no tier activates.</summary>
    Extra,
    /// <summary>A fallback framework: the newest known framework that activates a branch outside the file's frameworks;
    /// ranked with the extra variants.</summary>
    Fallback,
    /// <summary>A covering variant for other symbols, or a <c>symbols</c> combination with symbols on.</summary>
    Cover,
}

/// <summary>One planned symbol set of a file.</summary>
public sealed class PlannedVariant
{
    public TargetFramework? Framework { get; init; }
    /// <summary>Decision symbols switched on (true) or explicitly kept off (false): other symbols, <c>DEBUG</c>/<c>RELEASE</c>,
    /// platform OS names (an OS on turns on the file's symbols of that OS).</summary>
    public SortedDictionary<string, bool> Settings { get; internal set; } = new(StringComparer.Ordinal);
    public VariantRole Role { get; init; }
    /// <summary>The symbols the variant parses with (set by the planner).</summary>
    public IReadOnlySet<string> Symbols { get; internal set; } = new HashSet<string>();

    /// <summary>The shortest form: the framework's own symbol plus the symbols switched on (<c>NET8_0+DEBUG</c>).</summary>
    public string Name
    {
        get
        {
            var parts = new List<string>();
            if (Framework is { } t) parts.Add(t.ExactSymbol);
            parts.AddRange(Settings.Where(s => s.Value).Select(s => s.Key));
            return parts.Count == 0 ? "(no symbols)" : string.Join("+", parts);
        }
    }

    /// <summary>The finding text: <c>with NET20 on</c>, <c>with NET8_0 and DEBUG on</c>.</summary>
    public static string WithText(string name)
    {
        var parts = name.Split('+');
        return "with " + (parts.Length == 1 ? parts[0] : string.Join(", ", parts[..^1]) + " and " + parts[^1]) + " on";
    }
}

/// <summary>A branch no variant can ever activate: <c>never true</c> (<c>#if false</c>, a contradiction with an
/// enclosing condition) or <c>exclusive</c> (it needs two frameworks, <c>DEBUG</c> and <c>RELEASE</c>, or two platforms).
/// Counted and listed, never reported as a finding.</summary>
public sealed record UnreachableBranch(int Line, string Directive, string Reason);

/// <summary>The plan of one file.</summary>
public sealed class VariantPlan
{
    /// <summary>The file's planning symbols (condition names without its own defines).</summary>
    public required IReadOnlyList<string> Symbols { get; init; }
    /// <summary>Other symbols no project file under the repository mentions: never switched on.</summary>
    public IReadOnlyList<string> FilteredSymbols { get; init; } = [];
    /// <summary>The variants to parse, in priority order, at most the cap.</summary>
    public List<PlannedVariant> Variants { get; } = [];
    /// <summary>Variants planned before the cap cut them.</summary>
    public int Planned { get; internal set; }
    public bool Capped => Planned > Variants.Count;
    public List<UnreachableBranch> Unreachable { get; } = [];
    /// <summary>Branches no kept variant activates although some symbol set could: they need a filtered symbol, a symbol
    /// the <c>symbols</c> setting leaves off, or a variant the cap cut.</summary>
    public int UnplannedBranches { get; internal set; }
}

/// <summary>What the planner reads besides the file: the <c>symbols</c> setting and the project-file symbols.</summary>
public sealed class PlannerSettings
{
    /// <summary>The <c>symbols</c> setting; when not empty it replaces automatic planning.</summary>
    public IReadOnlyList<string> Symbols { get; init; } = [];
    /// <summary>Names mentioned in the repository's project files; null: no filter.</summary>
    public IReadOnlySet<string>? ProjectSymbols { get; init; }
}

/// <summary>
/// Plans the symbol variants of one file (mode (a), the covering set). Deterministic:
/// the same file and settings give the same plan. Branch activity is decided by <see cref="DirectiveMap.Evaluate"/>, never
/// by trial parses.
/// </summary>
public static class VariantPlanner
{
    public static VariantPlan Plan(DirectiveMap map, PlannerSettings settings, int cap) => new Planner(map, settings).Run(Math.Max(0, cap));

    /// <summary>Frameworks in priority order: the highest, the lowest, then upward from the lowest.</summary>
    public static List<TargetFramework> PriorityOrder(IEnumerable<TargetFramework> frameworks)
    {
        var sorted = frameworks.Distinct().Order().ToList();
        if (sorted.Count <= 1) return sorted;
        return [sorted[^1], sorted[0], .. sorted.Skip(1).Take(sorted.Count - 2)];
    }

    const int MaxFreeSymbols = 16;
    /// <summary>Combinations of the symbols setting generated at most (far above any cap; bounds the work).</summary>
    const int MaxSettingCombinations = 4096;

    sealed class Planner
    {
        readonly DirectiveMap map;
        readonly PlannerSettings settings;
        readonly Dictionary<string, SymbolClass> classes = new(StringComparer.Ordinal);
        /// <summary>Platform symbol to its OS (the decision variable).</summary>
        readonly Dictionary<string, string> platformOs = new(StringComparer.Ordinal);
        /// <summary>Symbols the planner may switch: other symbols (unless filtered), DEBUG/RELEASE, OS names.</summary>
        readonly HashSet<string> decisions = new(StringComparer.Ordinal);
        /// <summary>Every switchable symbol, filtered ones included (for telling unreachable from unplanned branches).</summary>
        readonly HashSet<string> allDecisions = new(StringComparer.Ordinal);
        readonly List<string> filtered = [];
        readonly SortedSet<TargetFramework> frameworks = [];
        readonly List<PlannedVariant> variants = [];
        int newestNet = SdkSymbols.NewestNet;

        public Planner(DirectiveMap map, PlannerSettings settings)
        {
            this.map = map;
            this.settings = settings;
            foreach (var s in map.Symbols) classes[s] = SymbolClassifier.Classify(s);
        }

        public VariantPlan Run(int cap)
        {
            Classify();
            var plan = new VariantPlan { Symbols = [.. map.Symbols], FilteredSymbols = filtered };
            if (map.Unbalanced) return plan;
            if (settings.Symbols.Count > 0) PlanSetting();
            else PlanCovering();
            var kept = Deduplicate();
            plan.Planned = kept.Count;
            plan.Variants.AddRange(kept.Take(cap));
            foreach (var v in plan.Variants) v.Symbols = SymbolSet(v.Framework, v.Settings);
            ClassifyInactive(plan);
            return plan;
        }

        void Classify()
        {
            var listed = settings.Symbols.Count > 0 ? new HashSet<string>(settings.Symbols, StringComparer.Ordinal) : null;
            foreach (var (s, c) in classes)
            {
                switch (c.Kind)
                {
                    case SymbolCategory.Framework: frameworks.Add(c.Target!.Value); break;
                    case SymbolCategory.Platform: platformOs[s] = c.Os!; allDecisions.Add(c.Os!); break;
                    case SymbolCategory.Configuration: allDecisions.Add(s); break;
                    case SymbolCategory.Other: allDecisions.Add(s); break;
                }
            }
            foreach (var (s, c) in classes)
                if (c.Kind == SymbolCategory.Versionless && !frameworks.Any(t => SdkSymbols.InFamily(t, c.Family!.Value)))
                    frameworks.Add(SdkSymbols.NewestOf(c.Family!.Value));
            // TRACE is on in every framework variant; a file without framework symbols switches it like an other symbol.
            if (classes.ContainsKey("TRACE") && frameworks.Count == 0) allDecisions.Add("TRACE");
            newestNet = Math.Max(SdkSymbols.NewestNet, frameworks.Where(t => t.Family == FrameworkFamily.Net).Select(t => t.Index).DefaultIfEmpty(0).Max());
            foreach (var d in allDecisions)
            {
                bool other = classes.TryGetValue(d, out var c) && c.Kind == SymbolCategory.Other;
                if (listed is not null)
                {
                    // Under the symbols setting only the listed symbols switch (an OS through any listed symbol of it).
                    bool isListed = listed.Contains(d) || platformOs.Any(p => p.Value == d && listed.Contains(p.Key));
                    if (isListed) decisions.Add(d);
                }
                else if (other && settings.ProjectSymbols is { } project && !project.Contains(d)) filtered.Add(d);
                else decisions.Add(d);
            }
            filtered.Sort(StringComparer.Ordinal);
        }

        // ---------- automatic planning (mode a) ----------

        void PlanCovering()
        {
            var tiers = Tiers();
            foreach (var t in tiers) variants.Add(new PlannedVariant { Framework = t, Role = VariantRole.Tier });
            AddExtras();
            AddCovering(tiers);
        }

        /// <summary>Every framework with 3 or fewer; else the lowest, the highest, the last .NET Framework and
        /// the first .NET Core or .NET; in priority order.</summary>
        List<TargetFramework> Tiers()
        {
            if (frameworks.Count <= 3) return PriorityOrder(frameworks);
            var picked = new SortedSet<TargetFramework> { frameworks.Min, frameworks.Max };
            if (frameworks.Where(t => t.Family == FrameworkFamily.Framework).ToList() is { Count: > 0 } fx) picked.Add(fx.Max());
            if (frameworks.Where(t => t.Family is FrameworkFamily.CoreApp or FrameworkFamily.Net).ToList() is { Count: > 0 } core) picked.Add(core.Min());
            return PriorityOrder(picked);
        }

        bool IsFrameworkSymbol(string s) =>
            classes.TryGetValue(s, out var c) && (c.Kind is SymbolCategory.Framework or SymbolCategory.Versionless || (c.Kind == SymbolCategory.Trace && !allDecisions.Contains(s)));

        /// <summary>Branches over framework symbols only that no variant activates get extra framework
        /// variants, greedily (the framework that activates the most such branches first).</summary>
        void AddExtras()
        {
            if (frameworks.Count == 0) return;
            var frameworkBranches = map.Branches.Where(b => b.PathSymbols.Count > 0 && b.PathSymbols.All(IsFrameworkSymbol)).Select(b => b.Index).ToList();
            var candidates = frameworks.Concat(SdkSymbols.Known(newestNet)).Distinct().ToList();
            while (true)
            {
                var active = Active();
                var open = frameworkBranches.Where(i => !active[i]).ToList();
                if (open.Count == 0) return;
                TargetFramework? best = null;
                int bestCount = 0;
                foreach (var c in candidates)
                {
                    if (variants.Any(v => v.Framework == c)) continue;
                    var taken = map.Evaluate(SymbolSet(c, null));
                    int n = open.Count(i => taken[i]);
                    if (n > bestCount) { bestCount = n; best = c; }
                }
                if (best is null) return;
                variants.Add(new PlannedVariant { Framework = best, Role = VariantRole.Extra });
            }
        }

        /// <summary>Each branch still inactive is placed on an existing variant when its symbols can be set
        /// there, else on a new variant over the file's frameworks, else on the newest known framework that activates it.</summary>
        void AddCovering(List<TargetFramework> tiers)
        {
            var choices = tiers.Concat(variants.Where(v => v.Role == VariantRole.Extra).Select(v => v.Framework!.Value)).Concat(frameworks)
                .Distinct().Select(t => (TargetFramework?)t).ToList();
            if (choices.Count == 0) choices.Add(null);
            var given = new HashSet<int>();
            for (int pass = 0; pass < 4; pass++)
            {
                bool changed = false;
                var active = Active();
                foreach (var b in map.Branches)
                {
                    if (active[b.Index] || given.Contains(b.Index)) continue;
                    if (Place(b.Index, choices)) { changed = true; active = Active(); }
                    else given.Add(b.Index);
                }
                if (!changed) return;
            }
        }

        bool Place(int branch, List<TargetFramework?> choices)
        {
            foreach (var v in variants)
            {
                if (Solve(branch, v.Framework, v.Settings, decisions) is not { } s) continue;
                v.Settings = s;
                return true;
            }
            foreach (var fw in choices)
            {
                if (Solve(branch, fw, null, decisions) is not { } s) continue;
                variants.Add(new PlannedVariant { Framework = fw, Settings = s, Role = VariantRole.Cover });
                return true;
            }
            if (frameworks.Count == 0) return false;
            foreach (var fw in SdkSymbols.Known(newestNet).OrderDescending())
            {
                if (Solve(branch, fw, null, decisions) is not { } s) continue;
                variants.Add(new PlannedVariant { Framework = fw, Settings = s, Role = VariantRole.Fallback });
                return true;
            }
            return false;
        }

        // ---------- the symbols setting ----------

        /// <summary>The listed frameworks the file uses (one at a time) times the listed other symbols it uses, on and off;
        /// combinations with fewer symbols on come first, frameworks in priority order within each.</summary>
        void PlanSetting()
        {
            var used = new HashSet<string>(map.Symbols, StringComparer.Ordinal);
            var fws = new SortedSet<TargetFramework>();
            var families = new List<VersionlessFamily>();
            foreach (var s in settings.Symbols.Distinct(StringComparer.Ordinal))
            {
                var c = SymbolClassifier.Classify(s);
                if (c.Kind == SymbolCategory.Framework && SdkSymbols.Implied(c.Target!.Value).Any(used.Contains)) fws.Add(c.Target!.Value);
                else if (c.Kind == SymbolCategory.Versionless && used.Contains(s)) families.Add(c.Family!.Value);
            }
            foreach (var f in families)
                if (!fws.Any(t => SdkSymbols.InFamily(t, f))) fws.Add(SdkSymbols.NewestOf(f));
            var order = fws.Count == 0 ? [null] : PriorityOrder(fws).Select(t => (TargetFramework?)t).ToList();
            var toggles = decisions.Order(StringComparer.Ordinal).Take(MaxFreeSymbols).ToList();
            foreach (var mask in Masks(toggles.Count))
            {
                if (variants.Count >= MaxSettingCombinations) return;
                foreach (var fw in order)
                {
                    var a = new SortedDictionary<string, bool>(StringComparer.Ordinal);
                    for (int i = 0; i < toggles.Count; i++) a[toggles[i]] = (mask & (1 << i)) != 0;
                    if (!Allowed(fw, a)) continue;
                    variants.Add(new PlannedVariant { Framework = fw, Settings = a, Role = mask == 0 ? VariantRole.Tier : VariantRole.Cover });
                }
            }
        }

        // ---------- shared ----------

        /// <summary>Variants in priority order without the ones equal to the base or to an earlier variant.</summary>
        List<PlannedVariant> Deduplicate()
        {
            var kept = new List<PlannedVariant>();
            var seen = new List<IReadOnlySet<string>>();
            foreach (var v in variants.Select((v, i) => (v, i)).OrderBy(x => Rank(x.v.Role)).ThenBy(x => x.i).Select(x => x.v))
            {
                var set = SymbolSet(v.Framework, v.Settings);
                if (set.Count == 0 || seen.Any(s => s.SetEquals(set))) continue;
                seen.Add(set);
                kept.Add(v);
            }
            return kept;
        }

        static int Rank(VariantRole role) => role switch { VariantRole.Tier => 0, VariantRole.Extra or VariantRole.Fallback => 1, _ => 2 };

        /// <summary>Branches active in the base view or in any variant so far.</summary>
        bool[] Active()
        {
            var active = map.Evaluate(new HashSet<string>());
            foreach (var v in variants)
            {
                var taken = map.Evaluate(SymbolSet(v.Framework, v.Settings));
                for (int i = 0; i < active.Length; i++) active[i] |= taken[i];
            }
            return active;
        }

        HashSet<string> SymbolSet(TargetFramework? fw, IReadOnlyDictionary<string, bool>? a)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (fw is { } t)
            {
                set.UnionWith(SdkSymbols.Implied(t));
                set.Add("TRACE");
            }
            if (a is null) return set;
            foreach (var (k, on) in a)
            {
                if (!on) continue;
                set.Add(k);
                foreach (var (symbol, os) in platformOs) if (os == k) set.Add(symbol);
            }
            return set;
        }

        /// <summary>At most one of DEBUG and RELEASE, at most one platform OS and never with a framework
        /// before .NET 5 (one framework per variant holds by construction).</summary>
        bool Allowed(TargetFramework? fw, IReadOnlyDictionary<string, bool> a)
        {
            int configurations = 0, platforms = 0;
            foreach (var (k, on) in a)
            {
                if (!on) continue;
                if (k is "DEBUG" or "RELEASE") configurations++;
                else if (platformOs.ContainsValue(k)) platforms++;
            }
            return configurations <= 1 && platforms <= 1 && (platforms == 0 || fw is not { } t || t.Family == FrameworkFamily.Net);
        }

        /// <summary>Settings of the free decision symbols on the branch's path (fewest on first) that make the branch taken
        /// with <paramref name="fw"/> and <paramref name="fixedSettings"/>; null when none does.</summary>
        SortedDictionary<string, bool>? Solve(int branch, TargetFramework? fw, IReadOnlyDictionary<string, bool>? fixedSettings, HashSet<string> vars)
        {
            var free = map.Branches[branch].PathSymbols.Select(s => platformOs.GetValueOrDefault(s, s))
                .Where(s => vars.Contains(s) && (fixedSettings is null || !fixedSettings.ContainsKey(s)))
                .Distinct().Order(StringComparer.Ordinal).Take(MaxFreeSymbols).ToList();
            var start = SymbolSet(fw, fixedSettings);
            var expanded = free.Select(f => platformOs.Where(p => p.Value == f).Select(p => p.Key).Append(f).ToArray()).ToArray();
            foreach (var mask in Masks(free.Count))
            {
                var on = new HashSet<string>(start, StringComparer.Ordinal);
                for (int i = 0; i < free.Count; i++)
                    if ((mask & (1 << i)) != 0) on.UnionWith(expanded[i]);
                if (!map.IsTaken(branch, on)) continue;
                var a = new SortedDictionary<string, bool>(StringComparer.Ordinal);
                if (fixedSettings is not null) foreach (var (k, v) in fixedSettings) a[k] = v;
                for (int i = 0; i < free.Count; i++) a[free[i]] = (mask & (1 << i)) != 0;
                if (Allowed(fw, a)) return a;
            }
            return null;
        }

        /// <summary>Inactive branches that no symbol set can activate are listed with the reason; the
        /// rest are counted as unplanned.</summary>
        void ClassifyInactive(VariantPlan plan)
        {
            var active = map.Evaluate(new HashSet<string>());
            foreach (var v in plan.Variants)
            {
                var taken = map.Evaluate(v.Symbols);
                for (int i = 0; i < active.Length; i++) active[i] |= taken[i];
            }
            List<TargetFramework?>? choices = null;
            foreach (var b in map.Branches)
            {
                if (active[b.Index]) continue;
                if (!Satisfiable(b)) { plan.Unreachable.Add(new UnreachableBranch(b.Line, b.Text, "never true")); continue; }
                choices ??= [null, .. frameworks.Concat(SdkSymbols.Known(newestNet)).Distinct().Select(t => (TargetFramework?)t)];
                if (Reachable(b, choices)) plan.UnplannedBranches++;
                else plan.Unreachable.Add(new UnreachableBranch(b.Line, b.Text, "exclusive"));
            }
        }

        /// <summary>True when some set of the path's names (no constraints) takes the branch; paths with more than 16 names
        /// are assumed satisfiable.</summary>
        bool Satisfiable(ConditionalBranch b)
        {
            var names = b.PathSymbols.Where(s => !map.Defined.Contains(s)).Order(StringComparer.Ordinal).ToList();
            if (names.Count > MaxFreeSymbols) return true;
            for (int mask = 0; mask < 1 << names.Count; mask++)
            {
                var on = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < names.Count; i++) if ((mask & (1 << i)) != 0) on.Add(names[i]);
                if (map.IsTaken(b.Index, on)) return true;
            }
            return false;
        }

        /// <summary>True when some framework (or none) and some setting of every switchable symbol takes the branch under the
        /// exclusive groups. Frameworks that agree on the path's framework symbols are tried once.</summary>
        bool Reachable(ConditionalBranch b, List<TargetFramework?> choices)
        {
            var frameworkNames = b.PathSymbols.Where(IsFrameworkSymbol).ToList();
            var tried = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fw in choices)
            {
                var set = SymbolSet(fw, null);
                var key = (fw is { Family: FrameworkFamily.Net } ? "N" : fw is null ? "-" : "F") + string.Concat(frameworkNames.Select(x => set.Contains(x) ? '1' : '0'));
                if (!tried.Add(key)) continue;
                if (Solve(b.Index, fw, null, allDecisions) is not null) return true;
            }
            return false;
        }
    }

    static readonly Dictionary<int, int[]> MaskCache = [];

    /// <summary>Every subset of n symbols as a bit mask, fewest symbols on first, then by value.</summary>
    static int[] Masks(int n)
    {
        lock (MaskCache)
        {
            if (MaskCache.TryGetValue(n, out var masks)) return masks;
            masks = [.. Enumerable.Range(0, 1 << n).OrderBy(x => System.Numerics.BitOperations.PopCount((uint)x)).ThenBy(x => x)];
            MaskCache[n] = masks;
            return masks;
        }
    }
}
