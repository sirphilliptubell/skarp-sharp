namespace SkarpSharp.Model;

// Output model (additive, no schema bump): the clone block of duplicate-code and duplicate-code-near findings, the
// Repository's duplication figure and the findings left out by inline suppression.

/// <summary>The clone group of a <c>duplicate-code</c> or <c>duplicate-code-near</c> finding (one finding per group).</summary>
public sealed class CloneInfo
{
    /// <summary><c>exact</c> (Type 1), <c>renamed</c> (Type 2) or <c>near-miss</c> (Type 3).</summary>
    public string Kind { get; set; } = "";
    /// <summary>Tokens per instance (Type 1/2; the instances have equal normalized token sequences); null for near-miss.</summary>
    public int? Tokens { get; set; }
    /// <summary>Distinct lines holding tokens of the instances.</summary>
    public int DuplicatedLines { get; set; }
    /// <summary>Every instance, ordinal path order then line (the first is where the finding points).</summary>
    public List<CloneInstance> Instances { get; set; } = [];
    /// <summary>Near-miss only: the similar pairs that joined the group (instance indexes, multiset Jaccard similarity).</summary>
    public List<ClonePair>? Pairs { get; set; }
}

public sealed class CloneInstance
{
    public string Path { get; set; } = "";
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    /// <summary>Lines from the first to the last token of the instance.</summary>
    public int Lines { get; set; }
    /// <summary>Tokens of the instance (Type 1/2) or of the method (near-miss).</summary>
    public int? Tokens { get; set; }
    /// <summary>The method, accessor, constructor or operator holding the instance (<c>Type.Member</c>); null outside bodies.</summary>
    public string? Member { get; set; }
}

public sealed class ClonePair
{
    public int A { get; set; }
    public int B { get; set; }
    public double Similarity { get; set; }
}

/// <summary>Duplicated code of the run; absent when the duplicate-code rules did not run.</summary>
public sealed class DuplicationSummary
{
    /// <summary>duplicate-code groups reported (after suppression).</summary>
    public int Groups { get; set; }
    /// <summary>duplicate-code-near groups reported; null when that rule is off.</summary>
    public int? NearGroups { get; set; }
    /// <summary>Instances of the duplicate-code groups.</summary>
    public int Instances { get; set; }
    /// <summary>Distinct source lines inside duplicate-code instances.</summary>
    public int DuplicatedLines { get; set; }
    /// <summary><see cref="DuplicatedLines"/> divided by the source lines of the analyzed files (4 decimals).</summary>
    public double Share { get; set; }
}

/// <summary>A finding left out by an inline suppression.</summary>
public sealed class SuppressedFinding
{
    public string Id { get; set; } = "";
    public string Rule { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Path { get; set; } = "";
    public int Line { get; set; }
    public string Message { get; set; } = "";
    public string Justification { get; set; } = "";
    /// <summary>Where the suppression is (<c>path:line</c> of the attribute).</summary>
    public string SuppressedBy { get; set; } = "";
}

/// <summary>
/// One <c>[SuppressMessage("skarp", "&lt;rule&gt;", Justification = "...")]</c> (or <c>UnconditionalSuppressMessage</c>) of a
/// file, read from the syntax tree.
/// </summary>
public sealed class SuppressionAttribute
{
    public required string Rule { get; init; }
    /// <summary>Null or blank: the suppression does not count (a <c>suppression-without-justification</c> notice instead).</summary>
    public string? Justification { get; init; }
    /// <summary>The attribute's own line.</summary>
    public required int Line { get; init; }
    /// <summary>Lines of the member or type it is on; 0 for an assembly-level attribute.</summary>
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    /// <summary>The member or type the attribute is on (<c>Type.Member</c>, <c>type Type</c>), or <c>assembly</c>.</summary>
    public required string Place { get; init; }
    /// <summary>Assembly-level only: <c>Target</c> and <c>Scope</c>.</summary>
    public string? Target { get; init; }
    public string? Scope { get; init; }
    public bool Assembly { get; init; }

    public bool Justified => !string.IsNullOrWhiteSpace(Justification);
}
