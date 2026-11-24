namespace SkarpSharp.Model;

// The per-file input of the coverage mapping (built by Coverage.CoverageLineScan while the file is parsed, read by
// Coverage.CoverageEngine, released after the mapping). Here rather than in Coverage, so the model does not depend on it.

/// <summary>What a report line on a source line can mean.</summary>
public enum CoverageLineKind : byte
{
    /// <summary>A line a producer may report: a statement, an expression, a brace or a constructor header.</summary>
    Code,
    /// <summary>A line no producer reports in a fresh report: blank, comment, directive or header-only (using, namespace,
    /// attributes, else, a method's signature), and not inside a multi-line statement. A covered line here means the
    /// report was made from other text (the staleness signal).</summary>
    NonCode,
    /// <summary>Code inactive in every analyzed variant (an <c>#if</c> branch no planned symbol set takes).</summary>
    Inactive,
}

/// <summary>
/// The per-file syntax facts the coverage mapping needs, computed while the file is parsed (only when a coverage report
/// is given): a kind per line and the line spans of members and types marked <c>[ExcludeFromCodeCoverage]</c>.
/// </summary>
public sealed class CoverageLines
{
    /// <summary>Kind of each line, index 0 = line 1.</summary>
    public required CoverageLineKind[] Kinds { get; init; }
    /// <summary>Line spans (1-based, inclusive) of declarations marked <c>[ExcludeFromCodeCoverage]</c>.</summary>
    public required IReadOnlyList<(int Start, int End)> Excluded { get; init; }

    public int Count => Kinds.Length;

    public CoverageLineKind this[int line] => Kinds[line - 1];

    public bool IsExcluded(int line)
    {
        foreach (var (s, e) in Excluded) if (s <= line && line <= e) return true;
        return false;
    }

    public bool HasInactive(int start, int end)
    {
        for (int l = Math.Max(1, start); l <= Math.Min(end, Kinds.Length); l++) if (Kinds[l - 1] == CoverageLineKind.Inactive) return true;
        return false;
    }
}
