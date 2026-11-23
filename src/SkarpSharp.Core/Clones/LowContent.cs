using Microsoft.CodeAnalysis.CSharp;

namespace SkarpSharp.Clones;

/// <summary>
/// The low-content filter of duplicate-code: a group is low-content boilerplate when its copies
/// average fewer than <see cref="MaxTokensPerLine"/> tokens per line and fewer than <see cref="MaxShapeShare"/> of the
/// first copy's lines differ in shape. Constructors that only store injected services (<c>_a = a;</c>, one parameter per
/// line), switch and lookup tables and migration builder chains are copies in form only; real copies carry denser,
/// more varied lines. Lines holding only braces count for neither measure (Allman braces would dilute both).
/// </summary>
public static class LowContent
{
    /// <summary>Tokens per line below which a group can be low-content (from an evaluation: dependency-injection
    /// constructors 3.5 to 4.7, judged real copies 5.05 and up).</summary>
    public const double MaxTokensPerLine = 5.0;
    /// <summary>Share of distinct line shapes below which a group can be low-content (dependency-injection constructors
    /// 0.04 to 0.38, judged real copies 0.50 and up).</summary>
    public const double MaxShapeShare = 0.4;

    static readonly ulong OpenBrace = CloneTokens.KindValue(SyntaxKind.OpenBraceToken);
    static readonly ulong CloseBrace = CloneTokens.KindValue(SyntaxKind.CloseBraceToken);
    static readonly ulong AnyIdentifier = StableHash.Mix(0xC0FFEEUL);

    /// <summary>The groups that are not low-content, in their order.</summary>
    public static List<TokenGroup> Without(CloneCorpus c, List<TokenGroup> groups) => [.. groups.Where(g => !Is(c, g))];

    /// <summary>True when the group's copies average fewer than 5 tokens per line and fewer than 40% of the first
    /// copy's lines have a distinct shape (brace-only lines left out of both).</summary>
    public static bool Is(CloneCorpus c, TokenGroup g)
    {
        double sum = 0;
        double shapeShare = 1;
        foreach (var start in g.Starts)
        {
            var (tokens, lines, shapes) = Measure(c, start, g.Length);
            sum += lines == 0 ? 0 : (double)tokens / lines;
            if (start == g.Starts[0]) shapeShare = lines == 0 ? 0 : (double)shapes / lines;
        }
        return sum / g.Starts.Length < MaxTokensPerLine && shapeShare < MaxShapeShare;
    }

    /// <summary>For one copy: the tokens on lines that hold more than braces, those lines, and their distinct shapes (the
    /// line's normalized tokens with every identifier as one marker).</summary>
    static (int Tokens, int Lines, int Shapes) Measure(CloneCorpus c, int start, int length)
    {
        var shape = new Dictionary<int, ulong>();
        var count = new Dictionary<int, int>();
        var content = new HashSet<int>();
        for (int k = start; k < start + length; k++)
        {
            bool ident = (c.Flags[k] & TokenFlags.Ident) != 0;
            ulong v = ident ? AnyIdentifier : c.Values[k];
            int line = c.Lines[k];
            shape[line] = StableHash.Combine(shape.GetValueOrDefault(line, 7UL), v);
            count[line] = count.GetValueOrDefault(line) + 1;
            if (ident || !IsBrace(v)) content.Add(line);
        }
        int tokens = 0;
        var distinct = new HashSet<ulong>();
        foreach (var line in content)
        {
            tokens += count[line];
            distinct.Add(shape[line]);
        }
        return (tokens, content.Count, distinct.Count);
    }

    static bool IsBrace(ulong value) => value == OpenBrace || value == CloseBrace;
}
