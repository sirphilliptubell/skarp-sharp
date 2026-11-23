namespace SkarpSharp.Coverage;

/// <summary>How a report's file name was resolved (or why it was not).</summary>
public enum PathOutcome { Absolute, Source, Deterministic, Suffix, Unmatched, Ambiguous, Outside }

/// <summary>
/// The path-mapping recipe, against a list of known
/// files relative to a root: separators and case normalized, rooted paths, <c>&lt;sources&gt;</c> plus name, deterministic
/// <c>/_/</c> paths, URLs, then a unique-suffix match of at least two segments (or a unique file name). Ties are
/// ambiguous and never guessed. No file-system access: the caller passes the files it knows.
/// </summary>
public sealed class CoveragePaths
{
    readonly string _root;
    readonly StringComparer _cmp;
    readonly StringComparison _comparison;
    readonly Dictionary<string, string> _files;
    readonly Dictionary<string, List<string>> _byName;

    /// <param name="root">The analyzed directory (absolute).</param>
    /// <param name="files">Known files relative to <paramref name="root"/>, '/' separators.</param>
    /// <param name="ignoreCase">Compare paths without case (default: Windows and macOS).</param>
    public CoveragePaths(string root, IEnumerable<string> files, bool? ignoreCase = null)
    {
        bool ic = ignoreCase ?? (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());
        _cmp = ic ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _comparison = ic ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        _root = Norm(root).TrimEnd('/');
        _files = new Dictionary<string, string>(_cmp);
        _byName = new Dictionary<string, List<string>>(_cmp);
        foreach (var f in files)
        {
            var nf = Norm(f).Trim('/');
            if (!_files.TryAdd(nf, f)) continue;
            var name = nf[(nf.LastIndexOf('/') + 1)..];
            if (!_byName.TryGetValue(name, out var list)) _byName[name] = list = [];
            list.Add(nf);
        }
    }

    public static string Norm(string p) => p.Replace('\\', '/').Trim();

    static bool IsRooted(string f) => (f.Length > 2 && f[1] == ':' && char.IsAsciiLetter(f[0])) || (f.StartsWith('/') && !f.StartsWith("/_/", StringComparison.Ordinal));

    /// <summary>The known file (as given to the constructor) a report file name stands for, and how it was found.</summary>
    public (string? File, PathOutcome Outcome) Resolve(string fileName, IReadOnlyList<string> sources)
    {
        var f = Norm(fileName);
        bool rooted = IsRooted(f);
        if (rooted && Known(Relative(f)) is { } abs) return (abs, PathOutcome.Absolute);
        foreach (var s in sources)
        {
            if (s.Length == 0 || rooted) continue;
            if (Known(Relative(Norm(s).TrimEnd('/') + "/" + f.TrimStart('/'))) is { } src) return (src, PathOutcome.Source);
        }
        if (f.StartsWith("/_/", StringComparison.Ordinal) && Known(f[3..]) is { } det) return (det, PathOutcome.Deterministic);
        if (f.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || f.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || f.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            f = f[(f.IndexOf(':') + 1)..].TrimStart('/');
        // A rooted path inside the root that is not a known file is not guessed elsewhere (the file is gone or not analyzed).
        if (rooted && Relative(f) is not null) return (null, PathOutcome.Unmatched);
        return Suffix(f, rooted);
    }

    (string?, PathOutcome) Suffix(string f, bool rooted)
    {
        var segs = f.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 0 || !_byName.TryGetValue(segs[^1], out var candidates))
            return (null, rooted ? PathOutcome.Outside : PathOutcome.Unmatched);
        int best = 0, ties = 0;
        string? bestFile = null;
        foreach (var c in candidates)
        {
            var cs = c.Split('/');
            int k = 0;
            while (k < cs.Length && k < segs.Length && _cmp.Equals(cs[^(k + 1)], segs[^(k + 1)])) k++;
            if (k > best) { best = k; bestFile = c; ties = 1; }
            else if (k == best) ties++;
        }
        if (ties > 1) return (null, PathOutcome.Ambiguous);
        if (best >= 2 || candidates.Count == 1) return (_files[bestFile!], PathOutcome.Suffix);
        return (null, rooted ? PathOutcome.Outside : PathOutcome.Unmatched);
    }

    /// <summary>The path relative to the root, or null when it lies outside.</summary>
    public string? Relative(string full)
    {
        var n = Norm(full);
        return n.StartsWith(_root + "/", _comparison) ? n[(_root.Length + 1)..] : null;
    }

    string? Known(string? rel) => rel is not null && _files.TryGetValue(rel, out var original) ? original : null;
}
