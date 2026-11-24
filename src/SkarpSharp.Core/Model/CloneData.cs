namespace SkarpSharp.Model;

// The per-file input of the duplicate-code rules (built by Clones.CloneTokens, read by the matchers).

/// <summary>
/// The clone token stream of one file: Roslyn tokens without trivia from the syntax tier's parse,
/// with using directives, extern aliases, attribute lists and namespace declarations left out as run breaks. Small
/// arrays per token; released after matching.
/// </summary>
public sealed class CloneFile
{
    /// <summary>Normalized token value (identifiers 0: their Baker code depends on the window).</summary>
    public required ulong[] Values { get; init; }
    /// <summary>Hash of the token text (tells exact copies from renamed ones).</summary>
    public required uint[] Raw { get; init; }
    /// <summary>File-local index of the previous identifier with the same name; -1 when none.</summary>
    public required int[] Previous { get; init; }
    /// <summary>1-based first and last line of the token.</summary>
    public required int[] Lines { get; init; }
    public required int[] EndLines { get; init; }
    public required byte[] Flags { get; init; }
    /// <summary>1-based ordinal of the method, accessor, constructor or operator body holding the token; 0 outside bodies.</summary>
    public required int[] Members { get; init; }
    /// <summary>Short names of the bodies (<c>Type.Method</c>), by ordinal - 1.</summary>
    public required string[] MemberNames { get; init; }
    /// <summary>Type 3 units (methods with a body); null unless collected.</summary>
    public List<CloneUnit>? Units { get; init; }

    public int Count => Values.Length;
}

/// <summary>One Type 3 unit: a method, constructor, operator or accessor with a body.</summary>
public sealed class CloneUnit
{
    public required string Name { get; init; }
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
    /// <summary>Lines holding tokens of the unit.</summary>
    public required int Lines { get; init; }
    public required int Nodes { get; init; }
    /// <summary>Normalized subtree fingerprints (a multiset).</summary>
    public required ulong[] Fingerprints { get; init; }
    /// <summary>File-local token range of the unit in <see cref="CloneFile"/> (-1 when it has none).</summary>
    public required int FirstToken { get; init; }
    public required int LastToken { get; init; }
    /// <summary>The body only forwards a call (after optional guard statements): skipped by Type 3.</summary>
    public required bool Forwarding { get; init; }
}
