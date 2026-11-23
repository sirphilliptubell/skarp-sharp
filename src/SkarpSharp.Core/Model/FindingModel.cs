namespace SkarpSharp.Model;

// Output model: findings. A finding says "worth a look", never "violation".

/// <summary>One finding: a rule judged a member, type or file worth a look.</summary>
public sealed class Finding
{
    /// <summary>
    /// Finding identity v1: <c>rule|path|place</c>, where place is the member key (type name, member name
    /// without parameters, an ordinal for overloads; lambdas and local functions under their parent), the type name, or
    /// empty for file rules. No line numbers. Best effort: renaming or moving a member makes its findings new.
    /// </summary>
    public string Id { get; set; } = "";
    /// <summary>
    /// The short handle of <see cref="Id"/>, the first 8 lowercase hex digits of SHA-256 over the
    /// id's UTF-8 bytes; <c>skarp show &lt;handle&gt;</c> accepts it (or a prefix of 4 or more). Additive in JSON.
    /// </summary>
    public string? Handle { get; set; }
    /// <summary>Stable rule id (for example <c>long-method</c>).</summary>
    public string Rule { get; set; } = "";
    /// <summary><c>notice</c> or <c>warning</c>.</summary>
    public string Severity { get; set; } = "";
    /// <summary>File path relative to the analyzed directory.</summary>
    public string Path { get; set; } = "";
    public int Line { get; set; }
    /// <summary>Member id (member rules), type name (large-type) or null (file rules).</summary>
    public string? Member { get; set; }
    /// <summary>Measured value (null for structural rules such as empty-catch).</summary>
    public double? Value { get; set; }
    /// <summary>Metric the value and threshold are in (for example <c>sourceLines</c>, <c>cognitive</c>).</summary>
    public string? Metric { get; set; }
    /// <summary>Threshold that was passed (null for structural rules).</summary>
    public double? Threshold { get; set; }
    /// <summary><c>over</c> (value &gt; threshold) or <c>at least</c> (value &gt;= threshold), as the source states it.</summary>
    public string? Comparison { get; set; }
    /// <summary>Preset that supplied the threshold (<c>default</c>, a named preset, or <c>custom</c> for an override).</summary>
    public string Preset { get; set; } = "";
    /// <summary>Who published the value (for the composite default: the individual source of this rule's value).</summary>
    public string ThresholdSource { get; set; } = "";
    /// <summary>What was measured, in words (for example "83 source lines").</summary>
    public string Message { get; set; } = "";
    /// <summary>Commits to the file in the history window (the ordering key); null without history.</summary>
    public int? Commits { get; set; }
    /// <summary><c>new</c>, <c>known</c> or <c>worsened</c> against the baseline; null without a baseline.</summary>
    public string? BaselineState { get; set; }
    /// <summary>The value recorded in the baseline (set for known and worsened findings with a value).</summary>
    public double? BaselineValue { get; set; }
    /// <summary>
    /// Id of a fixed baseline entry with the same rule, file and value as this new finding, so the member was
    /// possibly renamed or moved (the finding still counts as new); null otherwise.
    /// </summary>
    public string? PossiblyRenamedFrom { get; set; }
    /// <summary>For code used only by unused code, the places of its unused users (sorted); null otherwise.</summary>
    public List<string>? UsedOnlyBy { get; set; }
    /// <summary>Unused code only: 0 for code nothing references, n for code n steps below it through unused code
    /// (a dead cycle no unreferenced code reaches starts at 1); null for other rules.</summary>
    public int? Depth { get; set; }
    /// <summary>The clone group of duplicate-code and duplicate-code-near findings; null for other rules.</summary>
    public CloneInfo? Clone { get; set; }
    /// <summary>The preprocessor variant the finding's value comes from when it is not the base
    /// view (<c>NET8_0+DEBUG</c>); the message says it too ("with NET8_0 and DEBUG on"). A finding seen only in a variant is
    /// a notice.</summary>
    public string? Variant { get; set; }
    /// <summary>Member key of the place (identity part; null for type and file findings); not serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? PlaceKey { get; set; }
    /// <summary>True for type-level places (large-type, primary-constructor parameters); not serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool TypePlace { get; set; }
    /// <summary>The id leaves the path empty (<c>namespace-cycle||namespace X</c>): the place spans
    /// files, and the path is only where the finding points; not serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool PathlessId { get; set; }
    /// <summary>Set only on the stand-in a report view makes for a composite (brain-method, brain-class): the
    /// findings it replaces, in report order (a brain class holds its large-type finding and its brain methods' stand-ins);
    /// not serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public List<Finding>? Parts { get; set; }
}

/// <summary>Number of findings of one rule.</summary>
public sealed class FindingCount
{
    public string Rule { get; set; } = "";
    public int Warning { get; set; }
    public int Notice { get; set; }
}

/// <summary>Run header part for findings: preset, overrides and the thresholds in effect.</summary>
public sealed class FindingSettings
{
    public string Preset { get; set; } = "";
    public string PresetName { get; set; } = "";
    /// <summary>Metric of the complex-method rule: <c>cognitive</c> (default) or <c>cyclomatic</c>.</summary>
    public string ComplexMetric { get; set; } = "";
    /// <summary>Unit of the long-method line thresholds: <c>physical</c> (default) or <c>source</c>.</summary>
    public string LongMethodLines { get; set; } = "";
    /// <summary><c>exempt</c> (default) or <c>check</c> positional record parameters.</summary>
    public string RecordParameters { get; set; } = "";
    public List<string> Overrides { get; set; } = [];
    public List<RuleSettingInfo> Rules { get; set; } = [];
    public List<string> Notes { get; set; } = [];
    /// <summary>The repository file's <c>overrides</c> entries with the files each matched; null when none.
    /// (<see cref="Overrides"/> is the older list of threshold overrides in <c>--threshold</c> grammar.)</summary>
    public List<PathOverrideInfo>? PathOverrides { get; set; }
    /// <summary>The files any override entry matched; null when there are none.</summary>
    public int? PathOverrideFiles { get; set; }
}

/// <summary>One <c>overrides</c> entry as written, and the number of files of the run it matched.</summary>
public sealed class PathOverrideInfo
{
    /// <summary>The entry's position in the file, from 0.</summary>
    public int Index { get; set; }
    public List<string>? Files { get; set; }
    public List<string>? ExcludeFiles { get; set; }
    public bool? Tests { get; set; }
    public System.Text.Json.JsonElement? Rules { get; set; }
    public System.Text.Json.JsonElement? Thresholds { get; set; }
    /// <summary>Files of the run the entry matched (0: it matches nothing).</summary>
    public int Matched { get; set; }
}

/// <summary>Thresholds in effect for one rule.</summary>
public sealed class RuleSettingInfo
{
    public string Rule { get; set; } = "";
    /// <summary>Preset whose values are used (the composite <c>default</c> when the chosen preset has none for this rule).</summary>
    public string Preset { get; set; } = "";
    public string? Notice { get; set; }
    public string? Warning { get; set; }
    public string? NoticeSource { get; set; }
    public string? WarningSource { get; set; }
    public Dictionary<string, string>? Parameters { get; set; }
    /// <summary>True when the rule is switched off.</summary>
    public bool? Off { get; set; }
}

/// <summary>The baseline comparison of a run.</summary>
public sealed class BaselineSummary
{
    /// <summary>Baseline file (absolute path).</summary>
    public string Path { get; set; } = "";
    public string ToolVersion { get; set; } = "";
    public string Preset { get; set; } = "";
    /// <summary>Findings recorded in the baseline.</summary>
    public int Entries { get; set; }
    public int New { get; set; }
    public int Known { get; set; }
    public int Worsened { get; set; }
    /// <summary>Baseline entries no longer found.</summary>
    public int Fixed { get; set; }
    /// <summary>
    /// True when the effective rule settings differ from those recorded in the baseline (its settings
    /// fingerprint), so new and fixed counts include settings effects; false when equal; null when unknown (a baseline
    /// written before fingerprints).
    /// </summary>
    public bool? SettingsChanged { get; set; }
    /// <summary>Differences in settings between the baseline and this run that change values or findings.</summary>
    public List<string> Notes { get; set; } = [];
    /// <summary>New findings paired with a fixed entry of the same rule, file and value (possibly renamed).</summary>
    public int PossiblyRenamed { get; set; }
    /// <summary>Semantic baseline entries whose type or tier was not evaluated in this run (neither
    /// fixed nor new); absent in the syntax output.</summary>
    public int? NotEvaluated { get; set; }
    /// <summary>How the baseline's commit relates to this run's HEAD; absent when unknown.</summary>
    public string? Commit { get; set; }
}

/// <summary>The result of the opt-in gate; absent when the gate is off.</summary>
public sealed class GateResult
{
    /// <summary>The rule and severity pairs that trip the gate (<c>*:warning</c> by default).</summary>
    public List<string> On { get; set; } = [];
    public bool Tripped { get; set; }
    /// <summary>New findings in the gate set.</summary>
    public int New { get; set; }
    /// <summary>Worsened findings in the gate set.</summary>
    public int Worsened { get; set; }
    /// <summary>New or worsened findings outside the gate set (shown, not tripping).</summary>
    public int Outside { get; set; }
    /// <summary>The health limits and the files past them; absent in the semantic tier's gate.</summary>
    public GateHealth? Health { get; set; }
}

/// <summary>The gate's health limits (0: off) and the files past them, judged on the shown scores.</summary>
public sealed class GateHealth
{
    public double MaxDrop { get; set; }
    public double NewFileMin { get; set; }
    public double Floor { get; set; }
    /// <summary>The files past a limit, the largest drop first.</summary>
    public List<GateHealthFile> Files { get; set; } = [];
    /// <summary>Why a rule could not be judged everywhere (an older baseline without a file list; no semantic baseline);
    /// absent otherwise.</summary>
    public string? Note { get; set; }
}

/// <summary>One file past a health limit: its score at the baseline (absent for a new file) and now, one decimal,
/// and the rule: <c>drop</c>, <c>floor</c> or <c>newFile</c>.</summary>
public sealed class GateHealthFile
{
    public string Path { get; set; } = "";
    public double? Before { get; set; }
    public double Now { get; set; }
    public string Rule { get; set; } = "";
}
