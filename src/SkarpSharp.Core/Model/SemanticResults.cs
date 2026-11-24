using System.Text.Json.Serialization;

namespace SkarpSharp.Model;

// Semantic output file: its own schema, versioned from 1. One file per run, or one per target
// framework when some project is analyzed for more than one.

/// <summary>The semantic results of one run (or of one target framework of it).</summary>
public sealed class SemanticResult
{
    public int SchemaVersion { get; set; }
    public SemanticRunInfo Run { get; set; } = new();
    /// <summary>Compilations of the run: measured projects and dependencies compiled only to be referenced.</summary>
    public List<SemanticProject> Projects { get; set; } = [];
    /// <summary>Measured types (classes, structs, records, interfaces), ordered by path, line and name.</summary>
    public List<SemanticType> Types { get; set; } = [];
    /// <summary>Types without semantic numbers: count per reason, then the types.</summary>
    public List<NotEvaluatedCount> NotEvaluatedReasons { get; set; } = [];
    public List<NotEvaluatedType> NotEvaluated { get; set; } = [];
    /// <summary>Findings of the semantic rules (for example high-coupling), in report order.</summary>
    public List<Finding> Findings { get; set; } = [];
    public List<FindingCount> FindingCounts { get; set; } = [];
    /// <summary>Findings left out by an inline suppression in the analyzed files; absent when none.</summary>
    public List<SuppressedFinding>? Suppressed { get; set; }
    /// <summary>Comparison with the semantic baseline file (absent without one).</summary>
    public BaselineSummary? Baseline { get; set; }
    /// <summary>The gate over semantic findings (absent when the gate is off or no semantic baseline exists).</summary>
    public GateResult? Gate { get; set; }
    /// <summary>The unused-code analysis over the whole solution (in the first output file only; absent when its
    /// rules are off).</summary>
    public UnusedCodeSummary? UnusedCode { get; set; }
    /// <summary>Namespace dependencies and their cycles (absent when the namespace-cycle rule is off).</summary>
    public NamespaceSummary? Namespaces { get; set; }
    /// <summary><c>path|place</c> of unused-code candidates not evaluated in this run; not serialized.</summary>
    [JsonIgnore] public IReadOnlySet<string> UnusedNotEvaluatedPlaces { get; set; } = new HashSet<string>();
}

/// <summary>The semantic tier as the report, the gate and <c>skarp baseline</c> see it: status, the reason when it did not
/// run, and one result per output file. A model type, so the model does not depend on the tier's internals.</summary>
public sealed class SemanticTierResults
{
    public required string Status { get; init; }
    public string? Reason { get; init; }
    public required List<SemanticResult> Results { get; init; }
    /// <summary>When the tier did not run: the semantic baseline entries that exist, all not evaluated.</summary>
    public BaselineSummary? NotEvaluatedBaseline { get; init; }
}

/// <summary>The unused-code analysis of a run: candidates and how they were judged.</summary>
public sealed class UnusedCodeSummary
{
    /// <summary>Private and internal members and types declared in measured files (test projects and generated code excluded).</summary>
    public int Candidates { get; set; }
    public int UnusedMembers { get; set; }
    public int UnusedTypes { get; set; }
    /// <summary>Unused members and types whose only users are unused code (included in the two counts above).</summary>
    public int UsedOnlyByUnusedCode { get; set; }
    /// <summary>Fields and auto-properties written but never read by live code.</summary>
    public int UnreadMembers { get; set; }
    /// <summary>Candidates referenced only from test projects (label, never gated).</summary>
    public int UsedOnlyByTests { get; set; }
    /// <summary>Unused or test-only candidates inside a type reported as a whole.</summary>
    public int InsideReportedTypes { get; set; }
    public int Used { get; set; }
    /// <summary>Candidates a root keeps (attributes, overrides, interface implementations, names in strings and so on).</summary>
    public int Rooted { get; set; }
    /// <summary>Candidates that cannot be judged (targets not analyzed, unresolved symbols, invalid code, markup).</summary>
    public int NotEvaluated { get; set; }
    public List<UnusedCodeCount> RootReasons { get; set; } = [];
    public List<UnusedCodeCount> NotEvaluatedReasons { get; set; } = [];
    /// <summary>Every walked project (test projects included) with its counts.</summary>
    public List<UnusedCodeProject> Projects { get; set; } = [];
    public List<string> Notes { get; set; } = [];
}

/// <summary>
/// The namespace graph of a run: nodes are (assembly, namespace), named by the namespace with the
/// assembly in parentheses when the name is declared in more than one assembly; edges are type-to-type references
/// between nodes, weighted by distinct (source type, target type) pairs. Only the cycles are listed in full.
/// </summary>
public sealed class NamespaceSummary
{
    /// <summary>Nodes with at least one dependency.</summary>
    public int Namespaces { get; set; }
    public int Dependencies { get; set; }
    public int TypePairs { get; set; }
    /// <summary>References counted (cross-node, in source files).</summary>
    public int References { get; set; }
    /// <summary>Kept, handwritten, non-test files walked as sources.</summary>
    public int SourceFiles { get; set; }
    /// <summary>Generated files left out as sources.</summary>
    public int GeneratedFilesExcluded { get; set; }
    /// <summary>Test files and files excluded by the file selection, left out as sources (test projects are never sources).</summary>
    public int TestOrExcludedFiles { get; set; }
    /// <summary>Whether references whose target type is declared only in generated code are kept (opt-in) or dropped.</summary>
    public bool GeneratedTargetsKept { get; set; }
    /// <summary>References (and type pairs) to types declared only in generated code: dropped by default, kept with the opt-in.</summary>
    public int GeneratedTargetReferences { get; set; }
    public int GeneratedTargetTypePairs { get; set; }
    /// <summary>Strongly connected components of two or more namespaces, largest first.</summary>
    public List<NamespaceComponent> Components { get; set; } = [];
    public List<string> Notes { get; set; } = [];
}

/// <summary>One namespace cycle (strongly connected component).</summary>
public sealed class NamespaceComponent
{
    /// <summary>The finding place: <c>namespace</c> and the ordinal-first member.</summary>
    public string Place { get; set; } = "";
    public int Size { get; set; }
    public List<string> Assemblies { get; set; } = [];
    public List<string> Members { get; set; } = [];
    public int TypePairs { get; set; }
    /// <summary>One shortest cycle, first node repeated at the end.</summary>
    public List<string> ShortestCycle { get; set; } = [];
    /// <summary>Pairs depending on each other directly, the lighter direction first (cheapest first).</summary>
    public List<NamespaceMutualPair> MutualPairs { get; set; } = [];
    /// <summary>The suggested cut (greedy weighted feedback-edge set; a suggestion, not a minimum), cheapest first.</summary>
    public List<NamespaceEdge> Cut { get; set; } = [];
    public int CutTypePairs { get; set; }
    /// <summary>False when the search stopped at its cap with cycles left.</summary>
    public bool CutComplete { get; set; }
    /// <summary>Every dependency inside the component with up to 5 example references.</summary>
    public List<NamespaceDependency> Dependencies { get; set; } = [];
}

public sealed class NamespaceMutualPair
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public int Weight { get; set; }
    public int OtherWeight { get; set; }
}

public sealed class NamespaceEdge
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public int Weight { get; set; }
}

public sealed class NamespaceDependency
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    /// <summary>Distinct (source type, target type) pairs.</summary>
    public int Weight { get; set; }
    public int References { get; set; }
    /// <summary>The first reference of up to 5 type pairs, in path and line order.</summary>
    public List<NamespaceExample> Examples { get; set; } = [];
}

public sealed class NamespaceExample
{
    public string Path { get; set; } = "";
    public int Line { get; set; }
    public string FromType { get; set; } = "";
    public string ToType { get; set; } = "";
}

public sealed class UnusedCodeCount
{
    public string Reason { get; set; } = "";
    public int Count { get; set; }
}

public sealed class UnusedCodeProject
{
    /// <summary>Project file relative to the root.</summary>
    public string Path { get; set; } = "";
    /// <summary>Target frameworks walked (highest first).</summary>
    public List<string> TargetFrameworks { get; set; } = [];
    /// <summary>Target frameworks of the project that were not analyzed (findings come from the walked ones), or
    /// null when every target was analyzed.</summary>
    public List<string>? TargetFrameworksNotAnalyzed { get; set; }
    /// <summary>A test project: walked as a user, never reported.</summary>
    public bool Test { get; set; }
    public int Candidates { get; set; }
    public int Unused { get; set; }
    /// <summary>Written but never read.</summary>
    public int Unread { get; set; }
    public int UsedOnlyByTests { get; set; }
    public int NotEvaluated { get; set; }
    /// <summary>Why the whole project's candidates are not evaluated, or null.</summary>
    public string? NotEvaluatedReason { get; set; }
}

public sealed class SemanticRunInfo
{
    public string Tool { get; set; } = "skarp";
    public string ToolVersion { get; set; } = "";
    public string Root { get; set; } = "";
    public string Runtime { get; set; } = "";
    /// <summary><c>ran</c>, <c>not evaluated</c> (inputs did not allow it: <see cref="Reason"/>) or <c>off</c>.</summary>
    public string Status { get; set; } = "";
    public string? Reason { get; set; }
    public string? Hint { get; set; }
    /// <summary>Solution used (relative to the root) and why it was chosen.</summary>
    public string? Solution { get; set; }
    public string? SolutionReason { get; set; }
    /// <summary><c>highest</c> (default: the highest target framework per project), <c>all</c>, or the frameworks asked for.</summary>
    public string Targets { get; set; } = "";
    /// <summary>The target framework of this file when the run writes one file per framework; otherwise null.</summary>
    public string? TargetFramework { get; set; }
    /// <summary>Source generators ran (<c>--no-generators</c> turns them off).</summary>
    public bool Generators { get; set; }
    /// <summary>The coupling preset applied by the rule, the framework exclusion of the headline numbers, and notes.</summary>
    public List<string> Notes { get; set; } = [];
    public FindingSettings? Findings { get; set; }
    public SemanticTimings Timings { get; set; } = new();
    public long PeakWorkingSetBytes { get; set; }
}

/// <summary>Wall time per stage (seconds) and the peak working set of the largest child process.</summary>
public sealed class SemanticTimings
{
    public double SolutionSeconds { get; set; }
    public double RestoreSeconds { get; set; }
    public double DesignTimeBuildSeconds { get; set; }
    /// <summary>Wall time the compile stage waited for generator steps (they run in parallel ahead of it).</summary>
    public double GeneratorSeconds { get; set; }
    /// <summary>Sum of the generator compiler processes' times (above the wall time when they overlap).</summary>
    public double GeneratorProcessSeconds { get; set; }
    public double CompileSeconds { get; set; }
    public double MetricsSeconds { get; set; }
    /// <summary>The reference walk of every compilation (test projects included) and the unused-code analysis.</summary>
    public double ReferenceSeconds { get; set; }
    public double TotalSeconds { get; set; }
    public long ChildPeakWorkingSetBytes { get; set; }
}

public sealed class SemanticProject
{
    /// <summary>Project file relative to the root.</summary>
    public string Path { get; set; } = "";
    public string TargetFramework { get; set; } = "";
    public string AssemblyName { get; set; } = "";
    /// <summary>False for a dependency compiled only to be referenced (another target framework, negotiated by MSBuild).</summary>
    public bool Measured { get; set; }
    public int Sources { get; set; }
    public int GeneratedSources { get; set; }
    public int Types { get; set; }
    public int TypesNotEvaluated { get; set; }
    public int Errors { get; set; }
    public int Unresolved { get; set; }
    public List<string>? Notes { get; set; }
}

/// <summary>Type-level design metrics of one type, all partial declarations merged, nested types separate.</summary>
public sealed class SemanticType
{
    /// <summary>Namespace, containing types joined with <c>+</c>, generic arity as <c>`n</c> (as in the syntax output).</summary>
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    /// <summary>File of the first declaration (by path, then line), relative to the root.</summary>
    public string Path { get; set; } = "";
    public int Line { get; set; }
    public string Project { get; set; } = "";
    public string TargetFramework { get; set; } = "";
    /// <summary>Class coupling, headline: distinct other types used, .NET framework types (System.*) excluded.</summary>
    public int Cbo { get; set; }
    /// <summary>Class coupling with framework types counted (keyword types never count).</summary>
    public int CboAll { get; set; }
    /// <summary>Class coupling to types declared in the solution's projects only.</summary>
    public int CboInternal { get; set; }
    /// <summary>Class coupling by Microsoft's rules (Visual Studio code metrics, CA1506).</summary>
    public int CboMicrosoft { get; set; }
    /// <summary>Afferent coupling: other measured types whose coupling set holds this type.</summary>
    public int Ca { get; set; }
    /// <summary>Access to foreign data, class level: distinct foreign fields and properties used by the type as a whole
    /// (atfd.md variant B, the union over members; framework types excluded). God Class uses this value.</summary>
    public int Atfd { get; set; }
    /// <summary>Class-level ATFD with framework types counted.</summary>
    public int AtfdAll { get; set; }
    /// <summary>ATFD per member summed over the members (atfd.md variant A: distinct foreign fields and properties per
    /// member; framework types excluded); a secondary number, not used by a rule.</summary>
    public int AtfdMemberSum { get; set; }
    /// <summary>Tight class cohesion (Bieman-Kang); null when not applicable (fewer than two visible methods, no field use).</summary>
    public double? Tcc { get; set; }
    /// <summary>Loose class cohesion; null when not applicable.</summary>
    public double? Lcc { get; set; }
    /// <summary>Visible methods counted for TCC and LCC (N), and the pairs: possible, directly and indirectly connected.</summary>
    public int CohesionMethods { get; set; }
    public int? CohesionPairs { get; set; }
    public int? DirectPairs { get; set; }
    public int? IndirectPairs { get; set; }
    /// <summary>Why TCC and LCC are not given (null when they are).</summary>
    public string? CohesionNote { get; set; }
    /// <summary>Weighted methods per class: the sum of the members' cyclomatic complexity.</summary>
    public int Wmc { get; set; }
    /// <summary>The types counted by <see cref="Cbo"/> (short names), for explaining a value.</summary>
    public List<string> CoupledTypes { get; set; } = [];
    /// <summary>The foreign fields and properties counted by <see cref="Atfd"/> (<c>Type.Member</c>), distinct over the type.</summary>
    public List<string>? ForeignData { get; set; }
    /// <summary>Assembly and type name (afferent coupling and finding places); not serialized.</summary>
    [JsonIgnore] public string Key { get; set; } = "";
    /// <summary>Keys of every coupled type (framework included), for afferent coupling; not serialized.</summary>
    [JsonIgnore] public List<string> CoupledKeys { get; set; } = [];
}

public sealed class NotEvaluatedCount
{
    public string Reason { get; set; } = "";
    public int Types { get; set; }
}

public sealed class NotEvaluatedType
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public int Line { get; set; }
    public string Reason { get; set; } = "";
    /// <summary>The first diagnostic that caused it (code and message).</summary>
    public string? Diagnostic { get; set; }
}

/// <summary>
/// The one run-header entry of the syntax output about the semantic tier: whether it ran, why not, and the
/// semantic output files written.
/// </summary>
public sealed class SemanticTierInfo
{
    /// <summary><c>ran</c>, <c>not evaluated</c> or <c>off</c>.</summary>
    public string Status { get; set; } = "";
    public string? Reason { get; set; }
    public string? Hint { get; set; }
    public string? Solution { get; set; }
    public List<string>? TargetFrameworks { get; set; }
    public int? Types { get; set; }
    public int? TypesNotEvaluated { get; set; }
    public int? Findings { get; set; }
    public List<string>? Outputs { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SemanticResult))]
public sealed partial class SemanticJsonContext : JsonSerializerContext;
