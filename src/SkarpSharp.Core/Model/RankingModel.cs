namespace SkarpSharp.Model;

/// <summary>
/// A worst-first ranking (<c>skarp analyze --sort</c>, and the MCP tool <c>find_worst_code</c>) in the JSON
/// document: the sort and unit used, the paging and the entries. Absent without <c>--sort</c>.
/// </summary>
public sealed class RankingInfo
{
    /// <summary><c>health</c>, <c>hotspot</c> or a rule id: the sort used (health when hotspot had no git history).</summary>
    public string Sort { get; set; } = "";
    /// <summary>The sort asked for, when it differs from <see cref="Sort"/> (hotspot without history); absent otherwise.</summary>
    public string? Requested { get; set; }
    /// <summary><c>files</c> or <c>methods</c>.</summary>
    public string Unit { get; set; } = "";
    /// <summary>The metric of a rule sort (for example <c>physicalLines</c>); absent for health and hotspot.</summary>
    public string? Metric { get; set; }
    /// <summary>Everything that ranks: files with findings (health, hotspot), or files or methods with a measure (rule).</summary>
    public int Total { get; set; }
    public int Offset { get; set; }
    /// <summary>The entries listed, worst first.</summary>
    public List<RankingEntry> Entries { get; set; } = [];
}

/// <summary>One entry of a <see cref="RankingInfo"/>: a file, or a method with its line.</summary>
public sealed class RankingEntry
{
    public int Rank { get; set; }
    public string Path { get; set; } = "";
    /// <summary>A method entry, or a file entry of a method rule (its worst method): the member's start line.</summary>
    public int? Line { get; set; }
    /// <summary>The member's short name (method entries, and the worst method of a file entry of a method rule).</summary>
    public string? Member { get; set; }
    /// <summary>The file's health score (two decimals) and band.</summary>
    public double? Score { get; set; }
    public string? Band { get; set; }
    /// <summary>Hotspot sort: commits in the history window.</summary>
    public int? Commits { get; set; }
    /// <summary>Rule sort: the measured value and its limit in words (<c>70</c>, <c>at least 8</c>).</summary>
    public double? Value { get; set; }
    public string? Limit { get; set; }
    /// <summary>Findings of the file (or method) in all.</summary>
    public int Findings { get; set; }
}
