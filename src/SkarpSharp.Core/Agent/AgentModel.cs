using System.Text.Json.Serialization;
using SkarpSharp.Model;

namespace SkarpSharp.Agent;

// The documents of the agent loop. All source-generated (Native AOT).

/// <summary>
/// <c>.skarp/last-run/run.json</c>: what the saved run holds and how to tell whether it is
/// stale. Version 1 (contracts.md section 2).
/// </summary>
public sealed class LastRunHeader
{
    public int Version { get; set; }
    /// <summary>UTC time the run was saved (ISO 8601, seconds).</summary>
    public string WrittenAt { get; set; } = "";
    /// <summary>HEAD (full hash) when the run was saved; absent outside git or without commits.</summary>
    public string? Commit { get; set; }
    /// <summary>True when a <c>.cs</c> file under the analyzed directory had uncommitted changes; absent outside git.</summary>
    public bool? Dirty { get; set; }
    /// <summary>The syntax rule settings fingerprint of the run (the baseline's <c>settingsFingerprint</c>).</summary>
    public string SettingsFingerprint { get; set; } = "";
    public string ToolVersion { get; set; } = "";
    /// <summary>The analyzed directory (absolute, machine-specific).</summary>
    public string Root { get; set; } = "";
    /// <summary>The settings levels whose file applied (level and absolute path), lowest first; absent when none.</summary>
    public List<Model.SettingsFileInfo>? SettingsFiles { get; set; }
    /// <summary>The solution the run was asked to use (<c>--solution</c>, <c>semantic.solution</c> or the MCP
    /// tool <c>check</c>'s), relative to the root with '/' separators; absent when the semantic tier chooses one itself.
    /// A semantic run is reused only for the same solution.</summary>
    public string? Solution { get; set; }
    /// <summary>File names of the saved documents: <c>analysis.json</c>, then <c>semantic[.tfm].json</c>.</summary>
    public List<string> Documents { get; set; } = [];
    /// <summary>SHA-256 (lowercase hex) of every analyzed file's bytes, by path relative to the root.</summary>
    public SortedDictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// <c>.skarp/checked/&lt;key&gt;.json</c>: the result of the last <c>skarp check</c> of one file, so <c>skarp show</c> can
/// answer for findings a check printed that the saved run does not hold yet. Internal format.
/// </summary>
public sealed class CheckRecord
{
    public int Version { get; set; }
    public string WrittenAt { get; set; } = "";
    /// <summary>The file, relative to the root.</summary>
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    /// <summary>Every finding of the file in this check (both severities), in report order.</summary>
    public List<Finding> Findings { get; set; } = [];
    /// <summary>Findings the check reported as resolved.</summary>
    public List<ResolvedFinding> Resolved { get; set; } = [];
    /// <summary>What the check said about each finding it printed (<c>worse</c>, <c>new</c>), by id.</summary>
    public SortedDictionary<string, string> Changes { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Types declared <c>partial</c> in the file (their <c>large-type</c> stays with the saved run).</summary>
    public List<string> PartialTypes { get; set; } = [];
    /// <summary>The members that have findings (their metrics, for <c>show</c>).</summary>
    public List<MemberResult> Members { get; set; } = [];
}

/// <summary>A finding a check reported as resolved (no longer over its threshold).</summary>
public sealed class ResolvedFinding
{
    public string Id { get; set; } = "";
    public string Handle { get; set; } = "";
    public string Rule { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Path { get; set; } = "";
    public int Line { get; set; }
    public string? Metric { get; set; }
    public double? Value { get; set; }
    public double? Threshold { get; set; }
    /// <summary>True when the finding's member or type no longer exists in the file (deleted or renamed).</summary>
    public bool? Removed { get; set; }
}

/// <summary>
/// One tracking file <c>~/.skarp/tracked/&lt;yyyy-mm-dd&gt;-&lt;session&gt;.json</c>: per agent, per absolute
/// file path, what the last check of that file found. Version 1.
/// </summary>
public sealed class TrackingState
{
    public int Version { get; set; }
    /// <summary>The session id (<c>nosession</c> without one).</summary>
    public string Session { get; set; } = "";
    /// <summary>UTC time the file was created (the session start as skarp saw it).</summary>
    public string Started { get; set; } = "";
    /// <summary>By agent: <c>main</c> or the subagent's id.</summary>
    public SortedDictionary<string, TrackedAgent> Agents { get; set; } = new(StringComparer.Ordinal);
    /// <summary>The last <c>Stop</c> of the session: file hashes then (absolute path to SHA-256).</summary>
    public TrackedStop? Stop { get; set; }
    /// <summary>What the edit checks printed since the last <c>Stop</c>, so the <c>Stop</c> summary
    /// leaves out what they already said; each <c>Stop</c> clears it. By line id (a finding id, or <c>health|&lt;path&gt;</c>
    /// for a health line): <c>&lt;kind&gt; &lt;value&gt; &lt;from&gt;</c> of the last line printed (<see cref="ShownLines"/>). Absent
    /// when nothing was printed.</summary>
    public SortedDictionary<string, string>? Shown { get; set; }
    /// <summary>How many times Claude Code compacted the session's context (each <c>PostCompact</c> hook raises
    /// it); an agent whose About line predates the count gets it again.</summary>
    public int Compactions { get; set; }
}

public sealed class TrackedAgent
{
    /// <summary>The subagent's type (Claude Code <c>agent_type</c>); absent for the main agent.</summary>
    public string? AgentType { get; set; }
    /// <summary><see cref="TrackingState.Compactions"/> when the hook last printed its About line to this agent;
    /// absent before the first.</summary>
    public int? AboutCompactions { get; set; }
    /// <summary>The messages printed to this agent since (and with) its last About line.</summary>
    public int SinceAbout { get; set; }
    /// <summary>By absolute file path.</summary>
    public SortedDictionary<string, TrackedFile> Files { get; set; } = new(StringComparer.Ordinal);
}

public sealed class TrackedFile
{
    public long Size { get; set; }
    /// <summary>Last write time of the file (UTC, ISO 8601).</summary>
    public string Modified { get; set; } = "";
    public string Sha256 { get; set; } = "";
    /// <summary>When the file was last checked (UTC, ISO 8601).</summary>
    public string Checked { get; set; } = "";
    /// <summary>What the first check compared with: <c>head</c>, <c>untracked</c> (not in git HEAD), <c>outside-git</c>,
    /// <c>lookback</c> (an earlier tracking file); later checks compare with this entry.</summary>
    public string ComparedWith { get; set; } = "";
    /// <summary>The analyzed root (absolute) and the path relative to it.</summary>
    public string Root { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>Lines printed for this file so far, and how many findings were reported resolved.</summary>
    public int Reported { get; set; }
    public int ResolvedCount { get; set; }
    /// <summary>By finding id.</summary>
    public SortedDictionary<string, TrackedFinding> Findings { get; set; } = new(StringComparer.Ordinal);
    /// <summary>The file's findings at the start of the session (what the first check compared with: git
    /// HEAD, an earlier day's record, or none), by id with their values; the <c>Stop</c> hook reports what got worse or
    /// new against it. Carried unchanged by later checks.</summary>
    public SortedDictionary<string, double?>? Origin { get; set; }
    /// <summary>The severity of each <see cref="Origin"/> finding, so the <c>Stop</c> hook can
    /// recompute the file's session-start health score. Absent in files written by earlier versions.</summary>
    public SortedDictionary<string, string>? OriginSeverities { get; set; }
    /// <summary>The file's score (as shown, one decimal) the hook last showed this agent, or the score its first
    /// check compared with; a message with only health in it needs a move of 0.5 from it.</summary>
    public double? HealthShown { get; set; }
}

public sealed class TrackedFinding
{
    public string Rule { get; set; } = "";
    public string Severity { get; set; } = "";
    public string? Metric { get; set; }
    public double? Value { get; set; }
    public double? Threshold { get; set; }
    public int Line { get; set; }
    /// <summary><c>open</c> or <c>resolved</c>.</summary>
    public string State { get; set; } = "";
    /// <summary>Times this finding was printed (new, worse or resolved).</summary>
    public int Reported { get; set; }
}

public sealed class TrackedStop
{
    public string At { get; set; } = "";
    public SortedDictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);
}

/// <summary><c>skarp show --format json</c>: the findings asked for, version 1 (contracts.md section 2).</summary>
public sealed class ShowReport
{
    public int SchemaVersion { get; set; }
    /// <summary><c>last-run</c> or <c>baseline</c>.</summary>
    public string From { get; set; } = "";
    /// <summary>The root the data belongs to (absolute, machine-specific).</summary>
    public string Root { get; set; } = "";
    /// <summary>Set when the data is older than the files shown.</summary>
    public ShowStale? Stale { get; set; }
    public List<ShowFinding> Findings { get; set; } = [];
    /// <summary>Handles a prefix matched (several matches: listed, nothing chosen).</summary>
    public List<string>? Matches { get; set; }
    /// <summary>With <c>--file</c>, each file's code health recomputed from the findings shown.</summary>
    public List<ShowHealth>? Health { get; set; }
    /// <summary>The saved run's settings levels that applied (from <c>run.json</c>); absent for the baseline or none.</summary>
    public List<Model.SettingsFileInfo>? SettingsFiles { get; set; }
    /// <summary>With <c>--session</c>, the session id.</summary>
    public string? Session { get; set; }
    /// <summary>With <c>--session</c>, every root of the session's files (<see cref="Root"/> is the first, or the working folder's).</summary>
    public List<string>? Roots { get; set; }
    /// <summary>With <c>--session</c>, the roots without a saved run (only <c>skarp check</c> results were read).</summary>
    public List<string>? NoSavedRun { get; set; }
}

/// <summary>One file's code health in <c>skarp show --file</c>.</summary>
public sealed class ShowHealth
{
    public string Path { get; set; } = "";
    public FileHealth Health { get; set; } = new();
}

public sealed class ShowStale
{
    public string WrittenAt { get; set; } = "";
    public string? Commit { get; set; }
    public bool? Dirty { get; set; }
    public int FilesShown { get; set; }
    public int FilesChanged { get; set; }
    public string Refresh { get; set; } = "";
}

public sealed class ShowFinding
{
    public Finding Finding { get; set; } = new();
    /// <summary><c>syntax</c> or <c>semantic</c>.</summary>
    public string Tier { get; set; } = "";
    /// <summary>The target framework of a semantic finding (absent with one target).</summary>
    public string? TargetFramework { get; set; }
    /// <summary>Where the data comes from: <c>last-run</c>, <c>check</c> (a later <c>skarp check</c> of the file) or <c>baseline</c>.</summary>
    public string Source { get; set; } = "";
    /// <summary>What the last check said about it (<c>new</c>, <c>worse ...</c>, <c>resolved</c>, <c>removed</c> (the member or type is gone)).</summary>
    public string? Change { get; set; }
    /// <summary>Member findings: the member's metrics.</summary>
    public MemberResult? Metrics { get; set; }
    /// <summary><c>namespace-cycle</c>: the whole component (members, every cut, dependencies with example references).</summary>
    public NamespaceComponent? Component { get; set; }
    /// <summary>A composite (brain-method, brain-class): the ids of its part findings.</summary>
    public List<string>? Parts { get; set; }
    /// <summary>With <c>--session</c>, the root the finding's path is relative to, when it is not the report's <c>root</c>.</summary>
    public string? Root { get; set; }
}

/// <summary>The stdin of a Claude Code hook (the fields skarp reads; others are ignored).</summary>
public sealed class ClaudeHookInput
{
    [JsonPropertyName("session_id")] public string? SessionId { get; set; }
    [JsonPropertyName("agent_id")] public string? AgentId { get; set; }
    [JsonPropertyName("agent_type")] public string? AgentType { get; set; }
    [JsonPropertyName("hook_event_name")] public string? HookEventName { get; set; }
    [JsonPropertyName("tool_name")] public string? ToolName { get; set; }
    [JsonPropertyName("tool_input")] public ClaudeToolInput? ToolInput { get; set; }
    [JsonPropertyName("stop_hook_active")] public bool? StopHookActive { get; set; }
    [JsonPropertyName("cwd")] public string? Cwd { get; set; }
}

public sealed class ClaudeToolInput
{
    [JsonPropertyName("file_path")] public string? FilePath { get; set; }
}

/// <summary>What skarp answers a Claude Code hook with (stdout, exit 0).</summary>
public sealed class ClaudeHookOutput
{
    [JsonPropertyName("decision")] public string? Decision { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("hookSpecificOutput")] public ClaudeHookSpecificOutput? HookSpecificOutput { get; set; }
}

public sealed class ClaudeHookSpecificOutput
{
    [JsonPropertyName("hookEventName")] public string HookEventName { get; set; } = "";
    [JsonPropertyName("additionalContext")] public string AdditionalContext { get; set; } = "";
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(LastRunHeader))]
[JsonSerializable(typeof(CheckRecord))]
[JsonSerializable(typeof(TrackingState))]
[JsonSerializable(typeof(ShowReport))]
public sealed partial class AgentJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ClaudeHookInput))]
[JsonSerializable(typeof(ClaudeHookOutput))]
public sealed partial class HookJsonContext : JsonSerializerContext;
