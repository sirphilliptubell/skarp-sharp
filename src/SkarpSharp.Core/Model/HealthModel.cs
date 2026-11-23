namespace SkarpSharp.Model;

// The code health score in the analysis output. Additive, experimental
// (contracts.md sections 2 and 7). Scores are recomputed from findings; nothing here is read back as input.

/// <summary>One file's code health: a 1 to 10 grade (10 = healthy) lowered by its findings.</summary>
public sealed class FileHealth
{
    /// <summary>The score, 1 to 10, two decimals (display: one decimal).</summary>
    public double Score { get; set; }
    /// <summary><c>healthy</c> (9 and above), <c>problematic</c> (4 to below 9) or <c>unhealthy</c> (below 4).</summary>
    public string Band { get; set; } = "";
    /// <summary><c>syntax</c> (syntax-tier rules only) or <c>full</c> (both tiers).</summary>
    public string Kind { get; set; } = "";
    /// <summary>Points lost before the floor at 1 (ranks files that all score 1).</summary>
    public double Penalty { get; set; }
    /// <summary>The syntax score when <see cref="Kind"/> is <c>full</c> (what <c>skarp check</c> compares with).</summary>
    public double? SyntaxScore { get; set; }
    /// <summary>The score recomputed from the baseline's findings (same kind); absent without a baseline and for files
    /// not in it.</summary>
    public double? BaselineScore { get; set; }
    /// <summary>False for a file the baseline does not hold (not in its <c>files</c> list, else not at its commit); absent
    /// otherwise. With a baseline whose file set is unknown (<c>health.baseline.fileList</c> <c>unknown</c>), a file
    /// without an entry has neither this nor <see cref="BaselineScore"/>: it may be new or have had no findings.</summary>
    public bool? InBaseline { get; set; }
    /// <summary>Where the points went: one entry per rule and severity, most points first.</summary>
    public List<HealthContribution> Contributions { get; set; } = [];
}

/// <summary>The points one rule's findings of one severity took from a file's score.</summary>
public sealed class HealthContribution
{
    public string Rule { get; set; } = "";
    public string Severity { get; set; } = "";
    public int Count { get; set; }
    /// <summary>Points lost (after the decay and the rule's cap), two decimals.</summary>
    public double Points { get; set; }
    /// <summary>True when the rule reached its cap in this file (its points are scaled down to the cap).</summary>
    public bool? Capped { get; set; }
}

/// <summary>The project roll-ups of the code health score.</summary>
public sealed class HealthSummary
{
    /// <summary>The score model (<c>grade</c>).</summary>
    public string Model { get; set; } = "";
    /// <summary><c>syntax</c> or <c>full</c>; every score of the run is of this kind.</summary>
    public string Kind { get; set; } = "";
    /// <summary>Files scored (generated and excluded files are not).</summary>
    public int Files { get; set; }
    /// <summary>Source-line-weighted mean of the file scores (files with no source lines skipped); absent when none.</summary>
    public double? Average { get; set; }
    /// <summary>The same mean over the activity hotspots; absent without git history.</summary>
    public double? Hotspot { get; set; }
    /// <summary>Why <see cref="Hotspot"/> is absent.</summary>
    public string? HotspotNote { get; set; }
    /// <summary>Average and hotspot health recomputed from the baseline's findings; absent without a baseline.</summary>
    public HealthBaselineValues? Baseline { get; set; }
    /// <summary>The score a file must be below to be listed in <see cref="Worst"/> (<c>score.worst.below</c>).</summary>
    public double WorstBelow { get; set; }
    /// <summary>Files scoring below <see cref="WorstBelow"/>, lowest first, at most <c>score.worst.count</c>.</summary>
    public List<HealthWorstFile> Worst { get; set; } = [];
    /// <summary>The same mean per folder (every folder level with scored files).</summary>
    public List<FolderHealth> Folders { get; set; } = [];
}

/// <summary>
/// The comparison with the baseline: both sides cover the same files, those the baseline holds (files created since are
/// left out).
/// </summary>
public sealed class HealthBaselineValues
{
    /// <summary>Files compared (those with a <c>baselineScore</c>).</summary>
    public int Files { get; set; }
    /// <summary>The average of the compared files, recomputed from the baseline's findings.</summary>
    public double? Average { get; set; }
    /// <summary>The average of the same files now.</summary>
    public double? AverageNow { get; set; }
    public double? Hotspot { get; set; }
    public double? HotspotNow { get; set; }
}

public sealed class HealthWorstFile
{
    public string Path { get; set; } = "";
    public double Score { get; set; }
    public string Band { get; set; } = "";
    public double Penalty { get; set; }
    /// <summary>The file's top contributions (at most three).</summary>
    public List<HealthContribution> Contributions { get; set; } = [];
}

public sealed class FolderHealth
{
    /// <summary>Folder relative to the analyzed directory, '/' separators.</summary>
    public string Path { get; set; } = "";
    public double Average { get; set; }
    public int Files { get; set; }
    public long SourceLines { get; set; }
}
