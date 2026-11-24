using System.Text;
using System.Text.RegularExpressions;

namespace SkarpSharp.Discovery;

/// <summary>
/// A path glob for <c>include</c>, <c>exclude</c> and the selectors of <c>overrides</c>: <c>*</c> (within a segment),
/// <c>?</c>, <c>**</c> (any segments). Matched against paths relative to the analyzed directory with '/' separators. A
/// pattern matches a file when it matches the path or one of its folders; a pattern without '/' may match at any depth
/// (<c>Migrations</c>, <c>*.g.cs</c>). Case-insensitive on Windows and macOS.
/// </summary>
/// <remarks>
/// The common shapes are matched without a regular expression (a literal
/// folder <c>src/Legacy/**</c>, an exact path or folder, one segment at any depth <c>**/*.cs</c> or <c>*.g.cs</c>, and
/// segment patterns such as <c>dir/**/name</c> or <c>src/*/Linq/**</c>); the regular expression, which defines the
/// meaning, is built only for the other shapes (<c>**</c> inside a segment). An equivalence test keeps both in step.
/// </remarks>
public sealed class Glob
{
    enum Shape { Regex, UnderFolder, PathOrFolder, AnySegment, Segments }

    static readonly bool IgnoreCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    static readonly StringComparison Cmp = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    readonly string _source;
    Regex? _regex;
    readonly Shape _shape;
    /// <summary>The folder or path (UnderFolder, PathOrFolder), the segment pattern (AnySegment), or the literal folders
    /// a path must start with (Segments).</summary>
    readonly string _literal = "";
    readonly string[] _segments = [];

    public string Pattern { get; }

    public Glob(string pattern)
    {
        Pattern = pattern;
        var p = pattern.Replace('\\', '/').Trim();
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        bool anchored = p.StartsWith('/');
        p = p.Trim('/');
        _source = RegexSource(p, anchored);
        static bool Wild(string s) => s.Contains('*') || s.Contains('?');
        // Case folding of non-ASCII letters is the regular expression's (sharp s, the Kelvin sign): those patterns use it.
        if (IgnoreCase && !System.Text.Ascii.IsValid(p)) _shape = Shape.Regex;
        else if (anchored || p.Contains('/'))
        {
            if (p.EndsWith("/**", StringComparison.Ordinal) && !Wild(p[..^3])) { _shape = Shape.UnderFolder; _literal = p[..^3]; }
            else if (!Wild(p)) { _shape = Shape.PathOrFolder; _literal = p; }
            else if (p.StartsWith("**/", StringComparison.Ordinal) && !p[3..].Contains('/') && !p[3..].Contains("**", StringComparison.Ordinal))
            {
                _shape = Shape.AnySegment;
                _literal = p[3..];
            }
            else if (p.Split('/') is var segments && segments.All(s => s == "**" || !s.Contains("**", StringComparison.Ordinal)))
            {
                _shape = Shape.Segments;
                _segments = segments;
                var sb = new StringBuilder();
                foreach (var s in segments)
                {
                    if (Wild(s)) break;
                    sb.Append(s).Append('/');
                }
                _literal = sb.ToString();
            }
        }
        else if (!p.Contains("**", StringComparison.Ordinal)) { _shape = Shape.AnySegment; _literal = p; }
    }

    /// <summary>The regular expression that defines the pattern's meaning.</summary>
    static string RegexSource(string p, bool anchored)
    {
        var sb = new StringBuilder("^");
        if (!anchored && !p.Contains('/')) sb.Append("(?:.*/)?");
        for (int i = 0; i < p.Length; i++)
        {
            char c = p[i];
            if (c == '*' && i + 1 < p.Length && p[i + 1] == '*')
            {
                bool slash = i + 2 < p.Length && p[i + 2] == '/';
                sb.Append(slash ? "(?:.*/)?" : ".*");
                i += slash ? 2 : 1;
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append("(?:/.*)?$");
        return sb.ToString();
    }

    Regex Expression => _regex ??= new Regex(_source, RegexOptions.CultureInvariant | (IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromSeconds(1));

    /// <summary>The regular expression's answer (the equivalence test compares it with <see cref="Matches"/>).</summary>
    internal bool MatchesByRegex(string relativePath) => Expression.IsMatch(relativePath.Replace('\\', '/'));

    /// <summary>True when the regular expression is needed (no fast shape covers the pattern).</summary>
    internal bool UsesRegex => _shape == Shape.Regex;

    public bool Matches(string relativePath)
    {
        var path = relativePath.Contains('\\') ? relativePath.Replace('\\', '/') : relativePath;
        // A non-ASCII path is folded by the regular expression too (the fast paths fold ASCII letters only).
        if (IgnoreCase && _shape != Shape.Regex && !System.Text.Ascii.IsValid(path)) return Expression.IsMatch(path);
        switch (_shape)
        {
            case Shape.UnderFolder:
                return path.Length > _literal.Length && path[_literal.Length] == '/' && path.StartsWith(_literal, Cmp);
            case Shape.PathOrFolder:
                return path.StartsWith(_literal, Cmp) && (path.Length == _literal.Length || path[_literal.Length] == '/');
            case Shape.AnySegment:
                for (int start = 0; start <= path.Length;)
                {
                    int end = path.IndexOf('/', start);
                    if (end < 0) end = path.Length;
                    if (Segment(_literal, path.AsSpan(start, end - start))) return true;
                    start = end + 1;
                }
                return false;
            case Shape.Segments:
                return path.StartsWith(_literal, Cmp) && MatchSegments(0, path.Split('/'), 0);
            default:
                return Expression.IsMatch(path);
        }
    }

    /// <summary>Pattern segments from <paramref name="pi"/> against path segments from <paramref name="si"/>. The pattern
    /// may end at a folder of the path (the regex's <c>(?:/.*)?$</c>); a trailing <c>**</c> needs one more segment (it may be
    /// empty), an inner one takes zero or more.</summary>
    bool MatchSegments(int pi, string[] path, int si)
    {
        if (pi == _segments.Length) return true;
        if (_segments[pi] == "**")
        {
            if (pi == _segments.Length - 1) return si < path.Length;
            for (int k = si; k <= path.Length; k++) if (MatchSegments(pi + 1, path, k)) return true;
            return false;
        }
        return si < path.Length && Segment(_segments[pi], path[si]) && MatchSegments(pi + 1, path, si + 1);
    }

    /// <summary>One segment against a pattern of literals, <c>*</c> and <c>?</c> (no '/').</summary>
    static bool Segment(string pattern, ReadOnlySpan<char> s)
    {
        int p = 0, i = 0, star = -1, mark = 0;
        while (i < s.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || Same(pattern[p], s[i]))) { p++; i++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = i; }
            else if (star >= 0) { p = star + 1; i = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    static bool Same(char a, char b) => a == b || (IgnoreCase && char.IsAsciiLetter(a) && (a | 0x20) == (b | 0x20));
}
