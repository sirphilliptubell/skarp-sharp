using System.Text.Json.Serialization;

namespace SkarpSharp.Model;

// The subset of SARIF 2.1.0 (OASIS) that `skarp analyze --format sarif` writes.
// Property names and order follow the SARIF schema; null properties are left out.
// docs/contracts.md section 9 is the contract.

/// <summary>The SARIF log: one run.</summary>
public sealed class SarifLog
{
    [JsonPropertyName("$schema")] public string Schema { get; set; } = "https://json.schemastore.org/sarif-2.1.0.json";
    public string Version { get; set; } = "2.1.0";
    public List<SarifRun> Runs { get; set; } = [];
}

public sealed class SarifRun
{
    public SarifTool Tool { get; set; } = new();
    /// <summary>SRCROOT: the root the relative result URIs are resolved against (the git work tree, or the analyzed
    /// directory outside one).</summary>
    public Dictionary<string, SarifArtifactLocation>? OriginalUriBaseIds { get; set; }
    public List<SarifResult> Results { get; set; } = [];
    /// <summary>One entry per scored file, with its code health in the property bag; absent when none.</summary>
    public List<SarifArtifact>? Artifacts { get; set; }
    public SarifRunProperties? Properties { get; set; }
}

/// <summary>A file of the run (SARIF artifact object).</summary>
public sealed class SarifArtifact
{
    public SarifArtifactLocation Location { get; set; } = new();
    public SarifArtifactProperties? Properties { get; set; }
}

public sealed class SarifArtifactProperties
{
    /// <summary>The file's code health score; the score produces no results.</summary>
    public SarifHealth? Health { get; set; }
}

public sealed class SarifHealth
{
    public double Score { get; set; }
    public string Band { get; set; } = "";
    public string Kind { get; set; } = "";
}

public sealed class SarifTool
{
    public SarifToolComponent Driver { get; set; } = new();
}

public sealed class SarifToolComponent
{
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public string? SemanticVersion { get; set; }
    public string? InformationUri { get; set; }
    public List<SarifReportingDescriptor> Rules { get; set; } = [];
}

/// <summary>A rule (SARIF reportingDescriptor).</summary>
public sealed class SarifReportingDescriptor
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public SarifText? ShortDescription { get; set; }
    public SarifText? FullDescription { get; set; }
    public SarifMultiformatText? Help { get; set; }
    public SarifConfiguration? DefaultConfiguration { get; set; }
    public SarifRuleProperties? Properties { get; set; }
}

public sealed class SarifText
{
    public string Text { get; set; } = "";
}

public sealed class SarifMultiformatText
{
    public string Text { get; set; } = "";
    public string? Markdown { get; set; }
}

public sealed class SarifConfiguration
{
    /// <summary>False when the rule is switched off in this run's settings (absent when on: the SARIF default is true).</summary>
    public bool? Enabled { get; set; }
    /// <summary><c>warning</c> when the rule has a warning level in the active settings, else <c>note</c>.</summary>
    public string Level { get; set; } = "";
}

public sealed class SarifRuleProperties
{
    /// <summary>GitHub code scanning filters by tags: <c>maintainability</c> and the tier (<c>syntax</c> or <c>semantic</c>).</summary>
    public List<string> Tags { get; set; } = [];
    /// <summary>GitHub code scanning: <c>warning</c> for rules with a warning level, <c>recommendation</c> otherwise.</summary>
    [JsonPropertyName("problem.severity")] public string? ProblemSeverity { get; set; }
    /// <summary>What the rule judges: member, type or file.</summary>
    public string? Unit { get; set; }
}

public sealed class SarifResult
{
    public string RuleId { get; set; } = "";
    public int RuleIndex { get; set; }
    /// <summary><c>warning</c> for skarp warnings, <c>note</c> for notices.</summary>
    public string Level { get; set; } = "";
    public SarifText Message { get; set; } = new();
    public List<SarifLocation> Locations { get; set; } = [];
    /// <summary>The other copies of a clone group (duplicate-code, duplicate-code-near); absent otherwise.</summary>
    public List<SarifLocation>? RelatedLocations { get; set; }
    /// <summary>The finding identity under a versioned key (contracts.md section 9).</summary>
    public Dictionary<string, string> PartialFingerprints { get; set; } = [];
    /// <summary><c>new</c>, <c>unchanged</c> or <c>updated</c> against the baselines; absent when not every result has one.</summary>
    public string? BaselineState { get; set; }
    public SarifResultProperties? Properties { get; set; }
}

public sealed class SarifLocation
{
    public SarifPhysicalLocation PhysicalLocation { get; set; } = new();
    public List<SarifLogicalLocation>? LogicalLocations { get; set; }
    /// <summary>On a composite's related locations, the part finding there (rule, measure and finding id).</summary>
    public SarifText? Message { get; set; }
}

public sealed class SarifPhysicalLocation
{
    public SarifArtifactLocation ArtifactLocation { get; set; } = new();
    public SarifRegion? Region { get; set; }
}

public sealed class SarifArtifactLocation
{
    public string? Uri { get; set; }
    public string? UriBaseId { get; set; }
    public SarifText? Description { get; set; }
}

public sealed class SarifRegion
{
    public int StartLine { get; set; }
    /// <summary>The last line of a clone instance; absent for other findings.</summary>
    public int? EndLine { get; set; }
}

public sealed class SarifLogicalLocation
{
    public string? Name { get; set; }
    public string? FullyQualifiedName { get; set; }
    /// <summary><c>member</c> or <c>type</c>.</summary>
    public string? Kind { get; set; }
}

/// <summary>Finding facts that SARIF has no field for (the JSON output names them the same way).</summary>
public sealed class SarifResultProperties
{
    /// <summary><c>syntax</c> or <c>semantic</c>.</summary>
    public string Tier { get; set; } = "";
    public double? Value { get; set; }
    public string? Metric { get; set; }
    public double? Threshold { get; set; }
    public string? Comparison { get; set; }
    public string? Preset { get; set; }
    public string? ThresholdSource { get; set; }
    public double? BaselineValue { get; set; }
    public string? PossiblyRenamedFrom { get; set; }
    public int? Depth { get; set; }
    /// <summary>The target framework of a semantic finding when the run measured more than one (one file per framework).</summary>
    public string? TargetFramework { get; set; }
}

public sealed class SarifRunProperties
{
    /// <summary>Version of what skarp writes inside SARIF 2.1.0 (contracts.md section 9).</summary>
    public int ProfileVersion { get; set; }
    /// <summary>The semantic tier: <c>ran</c>, <c>off</c> or <c>not evaluated</c>; absent when it was not started.</summary>
    public string? SemanticTier { get; set; }
    /// <summary>Why results carry no baselineState, or which results were left out (GitHub limits); absent when nothing to say.</summary>
    public List<string>? Notes { get; set; }
    /// <summary>Results left out because the run had more than GitHub code scanning accepts per run.</summary>
    public int? ResultsOmitted { get; set; }
}

/// <summary>Source-generated JSON for SARIF (AOT safe).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SarifLog))]
public sealed partial class SarifJsonContext : JsonSerializerContext;
