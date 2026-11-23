namespace SkarpSharp.Model;

// History additions to the output schema. All history members are nullable on the
// existing classes, so runs without history serialize without them.

/// <summary>Run header part for git (<c>run.git</c>).</summary>
public sealed class GitRunInfo
{
    public string? Version { get; set; }
    public string? RepositoryRoot { get; set; }
    public string? Head { get; set; }
    /// <summary>Window start (ISO 8601 UTC, committer date); null for whole history.</summary>
    public string? WindowStart { get; set; }
    /// <summary>Window end: committer date of HEAD.</summary>
    public string? WindowEnd { get; set; }
    public string WindowBasis { get; set; } = "committer date (git --since); activity dates are author dates";
    public string? Policy { get; set; }
    public string? PolicyDigest { get; set; }
    public List<string> Notices { get; set; } = [];
    /// <summary>Time of the history part (probe, git read, preprocessing, accumulation).</summary>
    public double DurationSeconds { get; set; }
}

/// <summary>Repository-wide history summary (<c>history</c>).</summary>
public sealed class HistorySummary
{
    /// <summary>Non-merge commits git returned in the window (commits that change C# files).</summary>
    public int CommitsRead { get; set; }
    /// <summary>Commits with at least one eligible row.</summary>
    public int CommitsUsed { get; set; }
    /// <summary>Distinct people (lowercase email after mailmap) in used commits.</summary>
    public int People { get; set; }
    /// <summary>Eligible rows and lines of files present at HEAD (equal to the sums over <c>files[].history</c> of the whole repository).</summary>
    public long Rows { get; set; }
    public long Added { get; set; }
    public long Deleted { get; set; }
    /// <summary>Files at HEAD with at least one eligible commit.</summary>
    public int Files { get; set; }
    /// <summary>Every commit reachable from HEAD (merges and non-C# commits included; no pathspec, cheap). Context
    /// only; null when git could not count.</summary>
    public int? CommitsInRepository { get; set; }
    /// <summary>Commits reachable from HEAD that change C# files (same pathspec as the log), when they can be
    /// obtained cheaply: the count the baseline recorded at its commit plus the C# commits since it. Null otherwise
    /// (a notice says why).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("csharpCommitsInRepository")]
    public int? CSharpCommitsInRepository { get; set; }
    /// <summary>How <see cref="CSharpCommitsInRepository"/> was obtained: "baseline", or null when it is not given.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("csharpCommitsSource")]
    public string? CSharpCommitsSource { get; set; }
    /// <summary>The baseline commit (full hash) the C# total counts from.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("csharpCommitsBaselineCommit")]
    public string? CSharpCommitsBaselineCommit { get; set; }
    public DroppedCounts Dropped { get; set; } = new();
    /// <summary>Raw reader counts, before any policy (comparable with a bare git log --numstat with the same C# pathspec).</summary>
    public ReaderCounts Reader { get; set; } = new();
    /// <summary>Commits touching more C# files than the bulk flag, newest first.</summary>
    public List<BulkCommit> FlaggedBulk { get; set; } = [];
}

public sealed class DroppedCounts
{
    /// <summary>Merge commits in the window (whole history with --all-history) that differ in C# files from a parent.</summary>
    public int MergeCommits { get; set; }
    /// <summary>Commits of any kind (merges and non-C# commits included) older than the window: all commits
    /// reachable from HEAD minus those in the window, both counted without the C# pathspec. 0 for whole history.</summary>
    public int OutsideWindowCommits { get; set; }
    public int BotCommits { get; set; }
    public int BulkCommits { get; set; }
    public int ShallowBoundaryCommits { get; set; }
    public long GeneratedRows { get; set; }
    /// <summary>Rows of non-C# files. Git reads only C# changes, so this stays 0 (kept for the schema).</summary>
    public long OtherFileRows { get; set; }
    public long PureMoveRows { get; set; }
    /// <summary>Text rows with 0 added and 0 deleted lines that are not renames (mode changes; whitespace-only under -w never appear).</summary>
    public long NoChangeRows { get; set; }
    /// <summary>Eligible rows of files absent at HEAD (deleted, or renamed on a branch the rename map could not follow).</summary>
    public long NotAtHeadRows { get; set; }
    public int NotAtHeadFiles { get; set; }
    /// <summary>Files at HEAD left out as test code or by --exclude, and their rows. Commit and
    /// people counts still include their commits.</summary>
    public int ExcludedFiles { get; set; }
    public long ExcludedRows { get; set; }
}

public sealed class ReaderCounts
{
    public int Commits { get; set; }
    public long Rows { get; set; }
    public long Added { get; set; }
    public long Deleted { get; set; }
    public long Renames { get; set; }
    public long BinaryRows { get; set; }
    public long Malformed { get; set; }
}

public sealed class BulkCommit
{
    public string Hash { get; set; } = "";
    public int Files { get; set; }
}

/// <summary>Per-file history in the window (<c>files[].history</c>).</summary>
public sealed class FileHistory
{
    public int Commits { get; set; }
    public long Added { get; set; }
    public long Deleted { get; set; }
    /// <summary>Oldest author date of a counted change (ISO 8601 UTC).</summary>
    public string? FirstChange { get; set; }
    /// <summary>Newest author date of a counted change (ISO 8601 UTC).</summary>
    public string? LastChange { get; set; }
    /// <summary>Distinct authors (count only).</summary>
    public int Authors { get; set; }
    /// <summary>Eligible numstat rows of the file (for summary adjustments when files are excluded); not serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public long Rows { get; set; }
}

/// <summary>One row of the activity hotspot table.</summary>
public sealed class HotspotEntry
{
    public string Path { get; set; } = "";
    public int Rank { get; set; }
    public int Commits { get; set; }
    public long Added { get; set; }
    public long Deleted { get; set; }
    public long CognitiveSum { get; set; }
    public long CognitiveMax { get; set; }
    public int SourceLines { get; set; }
    /// <summary>The file has a complex member no test runs (an untested-complex-method finding); absent without
    /// a coverage report or when it has none.</summary>
    public bool? Untested { get; set; }
}

/// <summary>Output of <c>skarp history</c>: history only, no syntax analysis.</summary>
public sealed class HistoryReport
{
    public int SchemaVersion { get; set; }
    public HistoryRunHeader Run { get; set; } = new();
    public HistorySummary? History { get; set; }
    public List<HistoryFileResult> Files { get; set; } = [];
    /// <summary>Files under the directory left out as tests or by --exclude.</summary>
    public List<ExcludedFile>? Excluded { get; set; }
}

public sealed class HistoryRunHeader
{
    public string Tool { get; set; } = "skarp";
    public string ToolVersion { get; set; } = "";
    public string Root { get; set; } = "";
    public double DurationSeconds { get; set; }
    public long PeakWorkingSetBytes { get; set; }
    public string Runtime { get; set; } = "";
    public GitRunInfo? Git { get; set; }
    public bool IncludeTests { get; set; }
    public List<string>? IncludeGlobs { get; set; }
    public List<string>? ExcludeGlobs { get; set; }
    /// <summary>The settings levels whose file applied, lowest first; absent when none applied.</summary>
    public List<SettingsFileInfo>? SettingsFiles { get; set; }
}

public sealed class HistoryFileResult
{
    public string Path { get; set; } = "";
    public FileHistory History { get; set; } = new();
}
