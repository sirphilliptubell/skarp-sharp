namespace SkarpSharp.Model;

// Preprocessor symbol variants in the analysis
// output. All of it is additive and absent for files without #if (no schema bump).

/// <summary>The run's variant summary (<c>run.variants</c>); absent when no analyzed file has an <c>#if</c>.</summary>
public sealed class RunVariants
{
    /// <summary><c>covering</c> (automatic planning) or <c>symbols</c> (the <c>symbols</c> setting).</summary>
    public string Mode { get; set; } = "covering";
    /// <summary>Variants per file besides the base parse (<c>variants.cap</c>).</summary>
    public int Cap { get; set; }
    /// <summary>Variants per file of <c>skarp check</c>, the base parse counted in (<c>variants.checkCap</c>).</summary>
    public int CheckCap { get; set; }
    /// <summary>The <c>symbols</c> setting; absent when automatic.</summary>
    public List<string>? Symbols { get; set; }
    /// <summary>Files with at least one <c>#if</c>.</summary>
    public int FilesWithConditionals { get; set; }
    /// <summary>Files where at least one variant was parsed and kept.</summary>
    public int FilesWithVariants { get; set; }
    /// <summary>Variants parsed and kept, all files.</summary>
    public int Variants { get; set; }
    /// <summary>Variants dropped by the syntax-error guard (more syntax errors than the base).</summary>
    public int DiscardedVariants { get; set; }
    /// <summary>Files whose plan was cut at the cap.</summary>
    public List<string> CappedFiles { get; set; } = [];
    /// <summary>Branches no symbol set can activate (never true, or two of one exclusive group), all files.</summary>
    public int UnreachableBranches { get; set; }
    /// <summary>Branches left inactive because they need a filtered symbol, a symbol the setting leaves off, or a cut variant.</summary>
    public int UnplannedBranches { get; set; }
    /// <summary>Other symbols that no project file mentions (counted once per file).</summary>
    public int FilteredSymbols { get; set; }
}

/// <summary>One planned variant of a file (<c>files[].variants</c>).</summary>
public sealed class FileVariant
{
    /// <summary>Shortest form: the framework's own symbol plus the symbols switched on (<c>NET8_0+DEBUG</c>).</summary>
    public string Name { get; set; } = "";
    /// <summary>The target framework moniker (<c>net8.0</c>); absent for a variant without one.</summary>
    public string? Framework { get; set; }
    /// <summary>Every symbol the variant was parsed with, sorted.</summary>
    public List<string> Symbols { get; set; } = [];
    /// <summary>True when the syntax-error guard dropped it (its members were not merged).</summary>
    public bool? Discarded { get; set; }
}

/// <summary>An <c>#if</c> branch no variant can activate (<c>files[].unreachableBranches</c>), never a finding.</summary>
public sealed class UnreachableBranchInfo
{
    public int Line { get; set; }
    public string Directive { get; set; } = "";
    /// <summary><c>never true</c> or <c>exclusive</c> (two frameworks, <c>DEBUG</c> and <c>RELEASE</c>, or two platforms).</summary>
    public string Reason { get; set; } = "";
}

/// <summary>A member's metric values and rule facts: the merged (worst) values beside the base view's primary ones in
/// JSON (<c>members[].merged</c>), the base view's in memory.</summary>
public sealed class MemberValues
{
    public int Cyclomatic { get; set; }
    public int CyclomaticMicrosoft { get; set; }
    public int Cognitive { get; set; }
    public long Npath { get; set; }
    public int NpathLog2 { get; set; }
    public bool NpathSaturated { get; set; }
    public int Nesting { get; set; }
    public int Statements { get; set; }
    public LineCountsDto Lines { get; set; } = new();
    public int? Parameters { get; set; }
    public List<NestedBlockFact>? NestedBlocks { get; set; }
    public int? BooleanOperatorsMax { get; set; }
    public int? BooleanOperatorsLine { get; set; }
    public List<EmptyCatchFact>? EmptyCatches { get; set; }
    public List<NumericLiteralFact>? NumericLiterals { get; set; }

    public static MemberValues Of(MemberResult m) => new()
    {
        Cyclomatic = m.Cyclomatic, CyclomaticMicrosoft = m.CyclomaticMicrosoft, Cognitive = m.Cognitive, Npath = m.Npath,
        NpathLog2 = m.NpathLog2, NpathSaturated = m.NpathSaturated, Nesting = m.Nesting, Statements = m.Statements, Lines = m.Lines,
        Parameters = m.Parameters, NestedBlocks = m.NestedBlocks, BooleanOperatorsMax = m.BooleanOperatorsMax,
        BooleanOperatorsLine = m.BooleanOperatorsLine, EmptyCatches = m.EmptyCatches, NumericLiterals = m.NumericLiterals,
    };

    public void ApplyTo(MemberResult m)
    {
        m.Cyclomatic = Cyclomatic; m.CyclomaticMicrosoft = CyclomaticMicrosoft; m.Cognitive = Cognitive; m.Npath = Npath;
        m.NpathLog2 = NpathLog2; m.NpathSaturated = NpathSaturated; m.Nesting = Nesting; m.Statements = Statements; m.Lines = Lines;
        m.Parameters = Parameters; m.NestedBlocks = NestedBlocks; m.BooleanOperatorsMax = BooleanOperatorsMax;
        m.BooleanOperatorsLine = BooleanOperatorsLine; m.EmptyCatches = EmptyCatches; m.NumericLiterals = NumericLiterals;
    }
}

/// <summary>A type declaration's counts: merged (largest single variant per count) beside the base view's in JSON
/// (<c>types[].merged</c>), the base view's in memory.</summary>
public sealed class TypeValues
{
    public int Fields { get; set; }
    public int Properties { get; set; }
    public int Methods { get; set; }
    public int Constructors { get; set; }
    public int Events { get; set; }
    public int NestedTypes { get; set; }
    public int PublicMembers { get; set; }
    public int SourceLines { get; set; }
    public int? PrimaryConstructorParameters { get; set; }

    public static TypeValues Of(TypeCounts t) => new()
    {
        Fields = t.Fields, Properties = t.Properties, Methods = t.Methods, Constructors = t.Constructors, Events = t.Events,
        NestedTypes = t.NestedTypes, PublicMembers = t.PublicMembers, SourceLines = t.SourceLines, PrimaryConstructorParameters = t.PrimaryConstructorParameters,
    };

    public void ApplyTo(TypeCounts t)
    {
        t.Fields = Fields; t.Properties = Properties; t.Methods = Methods; t.Constructors = Constructors; t.Events = Events;
        t.NestedTypes = NestedTypes; t.PublicMembers = PublicMembers; t.SourceLines = SourceLines; t.PrimaryConstructorParameters = PrimaryConstructorParameters;
    }
}

/// <summary>Member values of one type in one variant, summed for the type roll-up (WMC and friends).</summary>
public readonly record struct TypeMemberSums(int SumCyclomatic, int MaxCyclomatic, int SumCognitive, int Statements)
{
    public TypeMemberSums Add(int cyclomatic, int cognitive, int statements) =>
        new(SumCyclomatic + cyclomatic, Math.Max(MaxCyclomatic, cyclomatic), SumCognitive + cognitive, Statements + statements);

    public static TypeMemberSums Max(TypeMemberSums a, TypeMemberSums b) =>
        new(Math.Max(a.SumCyclomatic, b.SumCyclomatic), Math.Max(a.MaxCyclomatic, b.MaxCyclomatic), Math.Max(a.SumCognitive, b.SumCognitive), Math.Max(a.Statements, b.Statements));
}

/// <summary>
/// The JSON view of merged values: primary values stay the base view's, with the merged values alongside. In memory every record holds the merged values, which rules, scores and reports read, and keeps
/// the base view's in a <c>BaseValues</c> snapshot. <see cref="ToBase"/> swaps them for writing (primary = base,
/// <c>merged</c> = merged); <see cref="ToMerged"/> swaps back, after writing and after reading a saved run.
/// </summary>
public static class VariantView
{
    public static void ToBase(AnalysisResult result)
    {
        foreach (var f in result.Files)
        {
            if (f.BaseLines is { } lines) { f.MergedLines = f.Lines; f.Lines = lines; f.BaseLines = null; }
            foreach (var m in f.Members)
                if (m.BaseValues is { } b) { m.Merged = MemberValues.Of(m); b.ApplyTo(m); m.BaseValues = null; }
            foreach (var t in f.Types)
                if (t.BaseValues is { } b) { t.Merged = TypeValues.Of(t); b.ApplyTo(t); t.BaseValues = null; }
        }
    }

    public static void ToMerged(AnalysisResult result)
    {
        foreach (var f in result.Files)
        {
            if (f.MergedLines is { } lines) { f.BaseLines = f.Lines; f.Lines = lines; f.MergedLines = null; }
            foreach (var m in f.Members)
                if (m.Merged is { } v) { m.BaseValues = MemberValues.Of(m); v.ApplyTo(m); m.Merged = null; }
            foreach (var t in f.Types)
                if (t.Merged is { } v) { t.BaseValues = TypeValues.Of(t); v.ApplyTo(t); t.Merged = null; }
        }
    }

    /// <summary>Runs <paramref name="write"/> with the JSON view in place and restores the in-memory view after.</summary>
    public static void Writing(AnalysisResult result, Action write)
    {
        ToBase(result);
        try { write(); }
        finally { ToMerged(result); }
    }
}
