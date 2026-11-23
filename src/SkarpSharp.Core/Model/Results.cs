using System.Text.Json.Serialization;

namespace SkarpSharp.Model;

// Output schema version 2 (finding ids, baseline states, member keys). Property names are camelCase in JSON.

public sealed class AnalysisResult
{
    public int SchemaVersion { get; set; }
    public RunInfo Run { get; set; } = new();
    public Summary Repository { get; set; } = new();
    public List<TypeSummary> Types { get; set; } = [];
    public List<FileResult> Files { get; set; } = [];
    /// <summary>History summary; absent when history did not run.</summary>
    public HistorySummary? History { get; set; }
    /// <summary>Activity hotspots; absent when history did not run.</summary>
    public List<HotspotEntry>? Hotspots { get; set; }
    /// <summary>Findings, all of them, in report order; absent when findings were not computed.</summary>
    public List<Finding>? Findings { get; set; }
    /// <summary>Number of findings per rule and severity.</summary>
    public List<FindingCount>? FindingCounts { get; set; }
    /// <summary>Files left out of the analysis (tests, --exclude), with the reason (<c>--exclude-tests-why</c>).</summary>
    public List<ExcludedFile>? Excluded { get; set; }
    /// <summary>Comparison with the baseline file (absent without one).</summary>
    public BaselineSummary? Baseline { get; set; }
    /// <summary>The gate result (absent when the gate is off).</summary>
    public GateResult? Gate { get; set; }
    /// <summary>Code health roll-ups; absent when findings were not computed.</summary>
    public HealthSummary? Health { get; set; }
    /// <summary>Findings left out by an inline suppression, with the justification; absent when none.</summary>
    public List<SuppressedFinding>? Suppressed { get; set; }
    /// <summary>Composite findings (brain-method, brain-class) over the findings above; absent
    /// when both composite rules are off (the default).</summary>
    public List<CompositeFinding>? Composites { get; set; }
    /// <summary>The coverage reports read with <c>--coverage</c>, their matching and warnings;
    /// absent without a report.</summary>
    public CoverageSummary? Coverage { get; set; }
    /// <summary>The worst-first ranking of <c>--sort</c>; absent without it.</summary>
    public RankingInfo? Ranking { get; set; }
    /// <summary>The semantic tier's results, for the report (they are written to their own file); not serialized.</summary>
    [JsonIgnore] public SemanticTierResults? SemanticTier { get; set; }
    /// <summary><c>.skarp.json</c> files below the analyzed root, outside excluded folders (relative
    /// paths); they are not applied, and the run notes so. Not serialized.</summary>
    [JsonIgnore] public List<string>? NestedSettingsFiles { get; set; }
}

/// <summary>A file left out of the analysis and why (test detection or an --exclude glob).</summary>
public sealed class ExcludedFile
{
    public string Path { get; set; } = "";
    /// <summary><c>test</c> or <c>exclude</c>.</summary>
    public string Kind { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class RunInfo
{
    public string Tool { get; set; } = "skarp";
    public string ToolVersion { get; set; } = "";
    public string Root { get; set; } = "";
    public string CyclomaticPreset { get; set; } = "ours";
    public string ParseOptions { get; set; } = "";
    public bool IncludeGenerated { get; set; }
    public int Parallelism { get; set; }
    public double DurationSeconds { get; set; }
    public long PeakWorkingSetBytes { get; set; }
    public string Runtime { get; set; } = "";
    /// <summary>Git environment, window and policy; absent with --no-history.</summary>
    public GitRunInfo? Git { get; set; }
    /// <summary>Test files are included (<c>--include-tests</c>) or excluded (default).</summary>
    public bool IncludeTests { get; set; }
    /// <summary><c>--include</c> globs (bring back automatically excluded files).</summary>
    public List<string>? IncludeGlobs { get; set; }
    /// <summary><c>--exclude</c> globs.</summary>
    public List<string>? ExcludeGlobs { get; set; }
    /// <summary>Active threshold preset, overrides and the thresholds in effect per rule.</summary>
    public FindingSettings? Findings { get; set; }
    /// <summary>The settings levels whose file applied, lowest first; absent when none applied.</summary>
    public List<SettingsFileInfo>? SettingsFiles { get; set; }
    /// <summary>The semantic tier of this run (status, reason, output files); its results are in their own file.</summary>
    public SemanticTierInfo? Semantic { get; set; }
    /// <summary>The preprocessor symbol variants of this run; absent when no analyzed file has an <c>#if</c>.</summary>
    public RunVariants? Variants { get; set; }
}

public sealed class FileResult
{
    public string Path { get; set; } = "";
    public bool Generated { get; set; }
    public LineCountsDto Lines { get; set; } = new();
    public int TopLevelTypes { get; set; }
    public List<TypeCounts> Types { get; set; } = [];
    public List<MemberResult> Members { get; set; } = [];
    public string? Error { get; set; }
    /// <summary>Per-file history in the window; absent when history did not run.</summary>
    public FileHistory? History { get; set; }
    /// <summary>The file's code health score; absent for generated files and when not scored.</summary>
    public FileHealth? Health { get; set; }
    /// <summary>Test framework namespace imported by a using directive of this file (test detection); not serialized.</summary>
    [JsonIgnore] public string? TestUsing { get; set; }
    /// <summary>True when that using is a <c>global using</c> (applies to the whole project).</summary>
    [JsonIgnore] public bool TestUsingIsGlobal { get; set; }
    /// <summary>The file is test code (kept with --include-tests or an --include glob); clones between test and
    /// production code are never reported; not serialized.</summary>
    [JsonIgnore] public bool TestCode { get; set; }
    /// <summary>The clone token stream (duplicate-code), released after matching; not serialized.</summary>
    [JsonIgnore] public CloneFile? CloneData { get; set; }
    /// <summary>The skarp SuppressMessage attributes of the file; null when none; not serialized.</summary>
    [JsonIgnore] public List<SuppressionAttribute>? Suppressions { get; set; }
    /// <summary>Covered and coverable lines from the coverage reports; absent without <c>--coverage</c>.</summary>
    public CoverageFacts? Coverage { get; set; }
    /// <summary>Line kinds and <c>[ExcludeFromCodeCoverage]</c> spans for the coverage mapping (computed only when a
    /// report is given, released after it); not serialized.</summary>
    [JsonIgnore] public CoverageLines? CoverageLines { get; set; }

    // Preprocessor symbol variants; all absent for a file without #if.

    /// <summary>The file's condition symbols (names in <c>#if</c>/<c>#elif</c> without its own <c>#define</c>s).</summary>
    public List<string>? Symbols { get; set; }
    /// <summary>Other symbols no project file under the repository mentions: they get no variant.</summary>
    public List<string>? FilteredSymbols { get; set; }
    /// <summary>The planned variants (besides the base parse) in priority order, up to the cap.</summary>
    public List<FileVariant>? Variants { get; set; }
    /// <summary>True when the plan was cut at the cap.</summary>
    public bool? VariantsCapped { get; set; }
    /// <summary>Branches no symbol set can activate, with the reason; never findings.</summary>
    public List<UnreachableBranchInfo>? UnreachableBranches { get; set; }
    /// <summary>Branches left inactive although some symbol set activates them (filtered symbol, setting, cap).</summary>
    public int? UnplannedBranches { get; set; }
    /// <summary>The merged line counts (the largest single variant) beside the base view's <see cref="Lines"/>; set only
    /// while writing JSON (<see cref="VariantView"/>).</summary>
    public LineCountsDto? MergedLines { get; set; }
    /// <summary>The base view's line counts when a variant has more source lines (in memory <see cref="Lines"/> holds the
    /// merged ones); not serialized.</summary>
    [JsonIgnore] public LineCountsDto? BaseLines { get; set; }
    /// <summary>The variant whose line counts <see cref="Lines"/> holds; not serialized.</summary>
    [JsonIgnore] public string? LinesVariant { get; set; }
    /// <summary>Per type name, the largest member sums of any single variant (base included); null when the file has no
    /// kept variant (the roll-up then sums the members); not serialized.</summary>
    [JsonIgnore] public Dictionary<string, TypeMemberSums>? TypeSums { get; set; }
    /// <summary>The base view's member sums per type name (with <see cref="TypeSums"/>); not serialized.</summary>
    [JsonIgnore] public Dictionary<string, TypeMemberSums>? BaseTypeSums { get; set; }
    /// <summary>The variant whose member sums are largest, per type name; not serialized.</summary>
    [JsonIgnore] public Dictionary<string, string>? TypeSumsVariant { get; set; }
}

public sealed class MemberResult
{
    public string Id { get; set; } = "";
    /// <summary>Identity key: type and member name without parameters, <c>#n</c> for the n-th member of
    /// the same name in the file (n from 2), lambdas as <c>parent/lambdaN</c>, local functions as <c>parent/name</c>.</summary>
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Parent { get; set; }
    public string Type { get; set; } = "";
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public int Cyclomatic { get; set; }
    public int CyclomaticMicrosoft { get; set; }
    public int Cognitive { get; set; }
    public long Npath { get; set; }
    public int NpathLog2 { get; set; }
    public bool NpathSaturated { get; set; }
    public int Nesting { get; set; }
    public int Statements { get; set; }
    public LineCountsDto Lines { get; set; } = new();

    // Syntax facts for the finding rules. Absent
    // (null) when they do not apply or are empty, so records stay small.

    /// <summary>Declared parameters (methods, constructors, operators, local functions, lambdas, indexers); the receiver
    /// <c>this</c> of an extension method is not counted. Null for members without a parameter list.</summary>
    public int? Parameters { get; set; }
    /// <summary>True when the signature is fixed elsewhere: <c>override</c> or an explicit interface implementation.</summary>
    public bool? InheritedSignature { get; set; }
    /// <summary>Top-level control statements (if, switch, loops) of the body whose conditional or loop nesting reaches
    /// depth 2 or more (bumpy road). <c>try</c>, <c>using</c>, <c>lock</c> and plain blocks are transparent.</summary>
    public List<NestedBlockFact>? NestedBlocks { get; set; }
    /// <summary>Most boolean operators (<c>&amp;&amp;</c>, <c>||</c>, pattern <c>and</c>/<c>or</c>) in one boolean expression of the body.</summary>
    public int? BooleanOperatorsMax { get; set; }
    /// <summary>Line of that expression.</summary>
    public int? BooleanOperatorsLine { get; set; }
    /// <summary>Catch clauses with an empty block.</summary>
    public List<EmptyCatchFact>? EmptyCatches { get; set; }
    /// <summary>Numeric literals of the body outside exempt positions (the value list is the rule's job).</summary>
    public List<NumericLiteralFact>? NumericLiterals { get; set; }

    /// <summary>Covered and coverable lines of the member (its lambdas, local functions, async and iterator
    /// bodies folded in); absent without <c>--coverage</c> and on nested bodies (lambdas, local functions).</summary>
    public CoverageFacts? Coverage { get; set; }

    /// <summary>The variant that gave a worse value than the base view (the first in plan order), or for a
    /// variant-only member the first variant it was seen in (<c>NET8_0+DEBUG</c>); absent otherwise.</summary>
    public string? Variant { get; set; }
    /// <summary>True for a member the base view does not see (numbered after the base members).</summary>
    public bool? OnlyInVariants { get; set; }
    /// <summary>The merged (worst) values beside the base view's primary ones; set only while writing JSON.</summary>
    public MemberValues? Merged { get; set; }
    /// <summary>The base view's values when a variant made one worse (in memory the record holds the merged
    /// values); not serialized.</summary>
    [JsonIgnore] public MemberValues? BaseValues { get; set; }

    /// <summary>A copy with other metric values (the base view of a member with variants).</summary>
    public MemberResult WithValues(MemberValues values)
    {
        var copy = (MemberResult)MemberwiseClone();
        values.ApplyTo(copy);
        copy.BaseValues = null;
        return copy;
    }
}

/// <summary>One top-level control statement of a member body with its conditional or loop nesting depth (itself = 1).</summary>
public sealed class NestedBlockFact
{
    /// <summary>Index of the statement among the top-level statements of the body (consecutive values are adjacent).</summary>
    public int Seq { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public int Depth { get; set; }
}

public sealed class EmptyCatchFact
{
    public int Line { get; set; }
    /// <summary>Caught type as written; null for a bare <c>catch</c>.</summary>
    public string? Caught { get; set; }
    public bool HasComment { get; set; }
    public bool HasFilter { get; set; }
}

public sealed class NumericLiteralFact
{
    public int Line { get; set; }
    /// <summary>Literal text, with a leading minus sign when negated.</summary>
    public string Text { get; set; } = "";
    public double Value { get; set; }
}

/// <summary>Member counts of one type declaration (one part of a partial type).</summary>
public sealed class TypeCounts
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool Partial { get; set; }
    public int Fields { get; set; }
    public int Properties { get; set; }
    public int Methods { get; set; }
    public int Constructors { get; set; }
    public int Events { get; set; }
    public int NestedTypes { get; set; }
    public int PublicMembers { get; set; }
    /// <summary>Line span of the declaration (attributes and nested types included).</summary>
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    /// <summary>Source lines of the declaration span (nested types included).</summary>
    public int SourceLines { get; set; }
    /// <summary>Parameters of a primary constructor (<c>record R(int A)</c>, <c>class C(int a)</c>); null when none.</summary>
    public int? PrimaryConstructorParameters { get; set; }
    /// <summary>The declaration has the <c>static</c> modifier (brain-class exempts static classes); not serialized.</summary>
    [JsonIgnore] public bool Static { get; set; }
    /// <summary>The variant that raised a count (first in plan order), or that holds a variant-only declaration.</summary>
    public string? Variant { get; set; }
    /// <summary>True for a declaration the base view does not see (and whose name it does not declare).</summary>
    public bool? OnlyInVariants { get; set; }
    /// <summary>The merged counts beside the base view's; set only while writing JSON.</summary>
    public TypeValues? Merged { get; set; }
    /// <summary>The base view's counts when a variant raised one; not serialized.</summary>
    [JsonIgnore] public TypeValues? BaseValues { get; set; }

    /// <summary>A copy with other counts (the base view of a declaration with variants).</summary>
    public TypeCounts WithValues(TypeValues values)
    {
        var copy = (TypeCounts)MemberwiseClone();
        values.ApplyTo(copy);
        copy.BaseValues = null;
        return copy;
    }
}

/// <summary>Member counts per type, all partial declarations merged by name.</summary>
public sealed class TypeSummary
{
    public string Name { get; set; } = "";
    /// <summary>Keyword of the first declaration (class, struct, record, interface, enum).</summary>
    public string Kind { get; set; } = "";
    /// <summary>File and line of the first declaration (by path, then line).</summary>
    public string Path { get; set; } = "";
    public int Line { get; set; }
    /// <summary>Source lines over all declarations.</summary>
    public int SourceLines { get; set; }
    public int Declarations { get; set; }
    public int Fields { get; set; }
    public int Properties { get; set; }
    public int Methods { get; set; }
    public int Constructors { get; set; }
    public int Events { get; set; }
    public int NestedTypes { get; set; }
    public int PublicMembers { get; set; }
    public int SumCyclomatic { get; set; }
    public int MaxCyclomatic { get; set; }
    public int SumCognitive { get; set; }
    public int Statements { get; set; }
    /// <summary>A declaration has the <c>static</c> modifier (a static class; brain-class exempts it); not serialized.</summary>
    [JsonIgnore] public bool Static { get; set; }
    /// <summary>The same roll-up over the base view only (set when a declaring file has variants and the base
    /// view declares the type); not serialized.</summary>
    [JsonIgnore] public TypeSummary? BaseView { get; set; }
    /// <summary>No file's base view declares the type; not serialized.</summary>
    [JsonIgnore] public bool OnlyInVariants { get; set; }
    /// <summary>A variant that made the roll-up larger than the base view; not serialized.</summary>
    [JsonIgnore] public string? Variant { get; set; }
    /// <summary><see cref="Path"/> and <see cref="Line"/> come from a declaration only a variant shows (no base
    /// declaration seen yet); not serialized.</summary>
    [JsonIgnore] public bool PlaceFromVariant { get; set; }
}

public sealed class LineCountsDto
{
    public int Physical { get; set; }
    public int Blank { get; set; }
    public int Comment { get; set; }
    public int Mixed { get; set; }
    public int Source { get; set; }
    public int Directive { get; set; }
    public int Inactive { get; set; }

    public void Add(LineCountsDto o)
    {
        Physical += o.Physical; Blank += o.Blank; Comment += o.Comment; Mixed += o.Mixed;
        Source += o.Source; Directive += o.Directive; Inactive += o.Inactive;
    }
}

public sealed class Summary
{
    public int Files { get; set; }
    public int GeneratedFiles { get; set; }
    /// <summary>Files left out as test code and by --exclude globs; they are not in <c>files</c>.</summary>
    public int TestFiles { get; set; }
    public int ExcludedFiles { get; set; }
    public int FilesWithErrors { get; set; }
    public int Members { get; set; }
    public int NestedBodies { get; set; }
    public LineCountsDto Lines { get; set; } = new();
    public List<MetricSummary> Metrics { get; set; } = [];
    /// <summary>Duplicated code; absent when the duplicate-code rules did not run.</summary>
    public DuplicationSummary? Duplication { get; set; }
}

/// <summary>Distribution of one member metric over top-level members (nested bodies excluded).</summary>
public sealed class MetricSummary
{
    public string Metric { get; set; } = "";
    public long Sum { get; set; }
    public long Max { get; set; }
    public double Mean { get; set; }
    public long P90 { get; set; }
    public long WarnThreshold { get; set; }
    public int OverWarn { get; set; }
}

/// <summary>One settings file that applied to a run: its level (<c>system</c>, <c>personal</c>, <c>repository</c>
/// or <c>environment</c>) and absolute path.</summary>
public sealed class SettingsFileInfo
{
    public string Level { get; set; } = "";
    public string Path { get; set; } = "";
}
