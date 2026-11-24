namespace SkarpSharp.Findings;

/// <summary>The selector of one override entry: file globs, excluded globs and Skarp's own test detection,
/// combined with AND. The matchers are delegates so Findings does not depend on Discovery.</summary>
public sealed record RuleScope(Func<string, bool>? Files, Func<string, bool>? ExcludeFiles, bool Tests)
{
    public bool Matches(string path, bool testCode) =>
        (!Tests || testCode) && (Files is null || Files(path)) && (ExcludeFiles is null || !ExcludeFiles(path));
}

/// <summary>
/// The rule settings per path, one resolver per run shared by
/// <c>analyze</c>, <c>baseline</c> and <c>check</c>. A file that no override entry matches gets the repository settings
/// (<see cref="Base"/>) itself. Each distinct set of matching entries resolves once to a <see cref="RuleSettings"/> that
/// shares the base's rule objects for the rules its entries do not name; a second cache maps (path, test status) to its
/// settings. With no overrides every call returns <see cref="Base"/> without matching or caching anything.
/// </summary>
public sealed class PathRules
{
    readonly IReadOnlyList<RuleScope> _scopes;
    readonly Func<int[], RuleSettings> _build;
    readonly Dictionary<string, RuleSettings> _combinations = new(StringComparer.Ordinal);
    readonly Dictionary<(string Path, bool Test), (int[] Matched, RuleSettings Settings)> _byPath = [];
    readonly Lock _lock = new();

    /// <summary>The repository settings (every level and the command line, no override).</summary>
    public RuleSettings Base { get; }
    /// <summary>The number of override entries.</summary>
    public int Count => _scopes.Count;

    /// <param name="build">Resolves the settings of one combination (entry indexes in file order).</param>
    public PathRules(RuleSettings @base, IReadOnlyList<RuleScope> scopes, Func<int[], RuleSettings> build)
    {
        Base = @base;
        _scopes = scopes;
        _build = build;
    }

    /// <summary>A resolver without overrides: <paramref name="settings"/> for every path.</summary>
    public static PathRules Single(RuleSettings settings) => new(settings, [], _ => settings);

    /// <summary>The settings of the file at <paramref name="path"/> (relative to the analyzed directory, '/' separators)
    /// with the given test status. A path the run did not keep (a deleted or excluded file) is asked as production code.</summary>
    public RuleSettings For(string path, bool testCode) => _scopes.Count == 0 ? Base : Resolve(path, testCode).Settings;

    /// <summary>The entries that match the file, in file order (empty with no overrides).</summary>
    public IReadOnlyList<int> Matched(string path, bool testCode) => _scopes.Count == 0 ? [] : Resolve(path, testCode).Matched;

    (int[] Matched, RuleSettings Settings) Resolve(string path, bool testCode)
    {
        lock (_lock)
        {
            if (_byPath.TryGetValue((path, testCode), out var known)) return known;
            List<int>? matched = null;
            for (int i = 0; i < _scopes.Count; i++)
                if (_scopes[i].Matches(path, testCode)) (matched ??= []).Add(i);
            (int[], RuleSettings) entry;
            if (matched is null) entry = ([], Base);
            else
            {
                int[] m = [.. matched];
                entry = (m, CombinationLocked(m));
            }
            _byPath[(path, testCode)] = entry;
            return entry;
        }
    }

    /// <summary>The settings of one combination of entries (indexes in file order), memoized.</summary>
    public RuleSettings Combination(int[] matched)
    {
        if (matched.Length == 0) return Base;
        lock (_lock) return CombinationLocked(matched);
    }

    RuleSettings CombinationLocked(int[] matched)
    {
        var key = string.Join(',', matched);
        if (!_combinations.TryGetValue(key, out var s)) _combinations[key] = s = _build(matched);
        return s;
    }

    /// <summary>The distinct settings resolved so far: the base first, then each combination.</summary>
    public IReadOnlyList<RuleSettings> InUse()
    {
        lock (_lock) return [Base, .. _combinations.Values];
    }

    /// <summary>The number of distinct combinations of entries resolved so far.</summary>
    public int Combinations { get { lock (_lock) return _combinations.Count; } }

    /// <summary>Per entry, how many of <paramref name="files"/> it matches; and how many files any entry matches.</summary>
    public (int[] PerEntry, int Any) MatchCounts(IEnumerable<(string Path, bool TestCode)> files)
    {
        var per = new int[_scopes.Count];
        int any = 0;
        if (_scopes.Count == 0) return (per, 0);
        foreach (var (path, test) in files)
        {
            var m = Resolve(path, test).Matched;
            if (m.Length > 0) any++;
            foreach (var i in m) per[i]++;
        }
        return (per, any);
    }
}
