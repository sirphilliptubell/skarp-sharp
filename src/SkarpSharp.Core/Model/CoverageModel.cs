namespace SkarpSharp.Model;

// Coverage facts read from Cobertura reports the user produced (skarp never runs tests). Every
// property is absent without --coverage, so the output is unchanged then. Counts are sums of lines, never averaged
// percentages; a file, member or project the reports say nothing about reads "no data", never 0%.

/// <summary>Covered and coverable lines of a member, file or project.</summary>
public sealed class CoverageFacts
{
    /// <summary><c>data</c> (the reports have coverable lines here), <c>no data</c> (none: not the same as 0%) or
    /// <c>excluded</c> (<c>[ExcludeFromCodeCoverage]</c>, generated code).</summary>
    public string Status { get; set; } = "";
    /// <summary>Distinct report lines in the span (both counts present only with status <c>data</c>).</summary>
    public int? CoverableLines { get; set; }
    /// <summary>Of those, the lines any report covers (union over reports).</summary>
    public int? CoveredLines { get; set; }
    /// <summary>Members: the span holds code inactive in every analyzed variant (an <c>#if</c> branch no planned symbol set
    /// takes); such a member is never reported by untested-complex-method.</summary>
    public bool? InactiveRegions { get; set; }

    public const string Data = "data", NoData = "no data", Excluded = "excluded";
}

/// <summary>The top-level <c>coverage</c> block: the reports read, how their files matched, totals and warnings.</summary>
public sealed class CoverageSummary
{
    public List<CoverageReportInfo> Reports { get; set; } = [];
    /// <summary>Distinct report files (over all reports) matched to an analyzed file.</summary>
    public int MatchedFiles { get; set; }
    /// <summary>Report files that match no file of the analyzed directory (deleted, or the report is from elsewhere).</summary>
    public int UnmatchedFiles { get; set; }
    /// <summary>Report files whose name fits several files equally well (never guessed).</summary>
    public int AmbiguousFiles { get; set; }
    /// <summary>Report files that exist but are not analyzed: tests, excluded, generated (also generator output under obj/).</summary>
    public int NotAnalyzedFiles { get; set; }
    /// <summary>Report files outside the analyzed directory.</summary>
    public int OutsideFiles { get; set; }
    /// <summary>Analyzed (non-generated) files with coverable lines in the reports, and without.</summary>
    public int FilesWithData { get; set; }
    public int FilesWithoutData { get; set; }
    /// <summary>Sums over the analyzed files with data.</summary>
    public int CoverableLines { get; set; }
    public int CoveredLines { get; set; }
    /// <summary>Covered report lines on code inactive in every analyzed variant (<c>#if</c>), which skarp does not analyze.</summary>
    public int InactiveCoveredLines { get; set; }
    /// <summary>True when a staleness warning (covered lines on non-code lines, or files gone) was raised.</summary>
    public bool Stale { get; set; }
    public List<ProjectCoverage> Projects { get; set; } = [];
    public List<CoverageWarning> Warnings { get; set; } = [];
}

public sealed class CoverageReportInfo
{
    /// <summary>The report file as given (absolute).</summary>
    public string Path { get; set; } = "";
    /// <summary>The report's <c>timestamp</c> as UTC (ISO 8601); absent when the report has none.</summary>
    public string? Timestamp { get; set; }
    /// <summary><c>coverlet</c>, <c>microsoft</c>, <c>reportgenerator</c> or <c>unknown</c>.</summary>
    public string Producer { get; set; } = "";
    /// <summary>Distinct file names in the report, and how many were matched, unmatched or ambiguous.</summary>
    public int Files { get; set; }
    public int Matched { get; set; }
    public int Unmatched { get; set; }
    public int Ambiguous { get; set; }
}

/// <summary>A project (the nearest *.csproj of its files) with its files' sums.</summary>
public sealed class ProjectCoverage
{
    /// <summary>The project file relative to the analyzed directory; <c>(none)</c> for files outside every project.</summary>
    public string Path { get; set; } = "";
    /// <summary><c>data</c> or <c>no data</c> (no file of the project has coverable lines in the reports).</summary>
    public string Status { get; set; } = "";
    public int Files { get; set; }
    public int FilesWithData { get; set; }
    public int? CoverableLines { get; set; }
    public int? CoveredLines { get; set; }
}

public sealed class CoverageWarning
{
    /// <summary><c>stale</c> (covered lines on non-code lines), <c>newer-files</c> (weaker: files modified after the newest
    /// report), <c>missing-files</c>, <c>beyond-end</c>, <c>inactive-code</c>, <c>unmatched</c>, <c>ambiguous</c>,
    /// <c>no-data</c> (a report without lines), <c>no-match</c> (a settings glob that matched no file).</summary>
    public string Kind { get; set; } = "";
    public string Message { get; set; } = "";
    /// <summary>The files concerned (at most 20; <see cref="Count"/> gives the total).</summary>
    public List<string>? Files { get; set; }
    public int? Count { get; set; }
}
