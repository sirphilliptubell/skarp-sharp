namespace SkarpSharp.Findings;

/// <summary>
/// Named threshold presets and the composite default. Values are facts with their source and evidence level; blanks stay blank (the composite is used for
/// a rule a preset leaves blank, and the report says so). Static data, AOT friendly.
/// </summary>
public static class Presets
{
    static Condition Over(string metric, double v) => new(metric, Comparison.Over, v);
    static Condition AtLeast(string metric, double v) => new(metric, Comparison.AtLeast, v);
    static LevelThreshold L(string source, string evidence, params Condition[] anyOf) => new(anyOf, source, evidence);
    /// <summary>A level whose conditions must all hold (a minimum size).</summary>
    static LevelThreshold Both(string source, string evidence, params Condition[] allOf) => new(allOf, source, evidence, AllOf: true);
    static RuleThresholds R(LevelThreshold? notice, LevelThreshold? warning = null, Dictionary<string, string>? parameters = null) => new(notice, warning, parameters);

    /// <summary>Line-based long-method thresholds: measured in the unit chosen with --long-method-lines.</summary>
    const string Lines = RuleSettings.LinesMetric;

    const string UnusedSource = "Skarp's own: certain unused code only, after Roslyn IDE0051 widened to internal members and types";
    const string UnusedEvidence = "E1 (IDE0051 filter); design ours";

    const string CodeSceneSrc = "CodeScene defaults";

    /// <summary>Composite default: per rule the best supported value.</summary>
    public static readonly Preset Default = new()
    {
        Id = "default",
        Name = "default (composite)",
        Description = "Composite of the best supported published value per rule (values measured against labelled C# code first, then C#-specific sources, then published authority, then fit with Skarp's counting). Each value names its own source.",
        Rules = new Dictionary<string, RuleThresholds>(StringComparer.Ordinal)
        {
            ["complex-method/cognitive"] = R(L("Sonar S3776, PMD, detekt and Lizard: 15", "E4; E1 via research", Over("cognitive", 15))),
            ["complex-method/cyclomatic"] = R(
                L("ReportGenerator and NDepend: 15", "E1 via research; E3", Over("cyclomatic", 15)),
                L("NDepend: 30, extremely complex", "E3", Over("cyclomatic", 30))),
            ["deep-nesting"] = R(L("CodeScene: depth 4", "E3 value, E4 reading", Over("nesting", 3))),
            ["long-method"] = R(
                L("Clean-CaDET: 50 lines, measured on 2,574 C# methods", "E1 via research", Over(Lines, 50)),
                L("CodeScene: 70 lines", "E3", Over(Lines, 70))),
            ["large-type"] = R(L("Clean-CaDET: 100 lines or WMC 20, measured on 920 C# classes", "E1 via research",
                Over("sourceLines", 100), Over("wmc", 20))),
            ["large-file"] = R(
                L("CodeScene: 1000 lines", "E3", Over("sourceLines", 1000)),
                L("CodeScene: 5000 lines, alert", "E3", Over("sourceLines", 5000))),
            ["too-many-parameters"] = R(
                L("CodeScene: 4 arguments, 5 for constructors", "E3", Over("parameters", 4), Over("constructorParameters", 5)),
                L("NDepend: 8 or more", "E3", AtLeast("parameters", 8))),
            ["path-explosion"] = R(L("Nejmeh 1988, ReportGenerator, Checkstyle and PMD: 200", "E2; E1 via research", Over("npath", 200))),
            ["active-complex-file"] = R(L("CodeScene: 2 to 4 percent, Tornhill: 1 to 3 percent; Skarp uses 4", "E4",
                AtLeast("activityShare", 4)), null, new() { ["share"] = "4" }),
            ["bumpy-road"] = R(L("CodeScene: 2 bumps at depth 2", "E3", AtLeast("bumps", 2)), null, new() { ["depth"] = "2" }),
            ["complex-condition"] = R(L("CodeScene: 2; Designite: 3 boolean sub-expressions", "E1 via research (Java); E3", AtLeast("booleanOperators", 2))),
            ["empty-catch"] = R(
                L("Skarp's own, after Checkstyle EmptyCatchBlock and Doomen AV1210", "E3; design is ours"),
                L("Skarp's own, after Checkstyle EmptyCatchBlock and Doomen AV1210", "E3; design is ours")),
            ["magic-number"] = R(L("Checkstyle MagicNumber: ignores -1, 0, 1, 2; positions after Doomen AV1515", "E3"),
                null, new() { ["ignore"] = "-1,0,1,2" }),
            ["high-coupling"] = R(L("Checkstyle ClassFanOutComplexity and PMD CouplingBetweenObjects: 20", "E3",
                Over("cbo", 20))),
            ["god-class"] = R(L("Lanza and Marinescu 2006: Java values, via PMD GodClassRule; the rule set is chosen by the preset parameter",
                "E4 secondhand"), null, new() { ["preset"] = "lanza-marinescu-2006" }),
            ["unused-member"] = R(L(UnusedSource, UnusedEvidence)),
            ["unused-type"] = R(L(UnusedSource, UnusedEvidence)),
            ["used-only-by-tests"] = R(L("Skarp's own, after NDepend's \"code used only by tests\" rule; a label, never gated", "E3; design ours")),
            ["namespace-cycle"] = R(null, L("Skarp's own: every cycle, after NDepend ND1400/ND1401 and Structure101's 0% tangle default", "E3; design ours")),
            ["unread-member"] = R(L("Skarp's own: certain written-but-never-read members only, after Roslyn IDE0052 widened to internal members", "E1 (IDE0052 value usage); design ours")),
            ["duplicate-code"] = R(
                Both("SonarQube: 100 tokens over at least 10 lines; PMD CPD: 100 tokens", "E3",
                    AtLeast("tokens", 100), AtLeast("lines", 10)),
                L("Skarp's own: a copy of 50 or more lines, or 3 or more copies of copyLines (20) or more lines", "E5 design ours",
                    AtLeast("lines", 50), AtLeast("copies", 3)),
                new() { ["copyLines"] = "20", ["lowContent"] = "skip" }),
            ["duplicate-code-near"] = R(Both("Skarp's own, after CodeScene's method similarity: 0.75 similar and 10 lines", "E4 (single rater)",
                AtLeast("similarity", 0.75), AtLeast("lines", 10))),
            ["suppression-without-justification"] = R(L("Skarp's own: a suppression must say why", "design ours")),
            ["brain-method"] = R(L("Skarp's own, after CodeScene's Brain Method: all part findings at one member, each at its own threshold", "E5 design ours; precision 36 to 57%; on by default because CodeScene ships it on for C#"),
                null, new() { ["parts"] = "long-method,complex-method,deep-nesting", ["minParts"] = "all" }),
            ["brain-class"] = R(L("Skarp's own: large-type, a brain method and at least minMethods methods; preset lanza-marinescu-2006 adds the book's size legs", "E5 design ours (precision 44 to 67%, opt-in)"),
                null, new() { ["minMethods"] = "10", ["preset"] = "ours" }),
            ["untested-complex-method"] = R(L("Skarp's own: cyclomatic 6 or more with no covered line, CRAP's zero-coverage cut (Savoia and Evans 2007: CRAP over 30 at 0% coverage from cyclomatic 6)",
                "E5 design ours (factual precision 97%, usefulness 63 to 69%)", AtLeast("cyclomatic", 6))),
        },
    };

    public static readonly IReadOnlyList<Preset> All =
    [
        Default,
        new()
        {
            Id = "codescene", Name = "CodeScene C# defaults",
            Description = "CodeScene's published C# default thresholds. Counting rules and units are not published by the vendor; Skarp's counting is used.",
            Rules = new Dictionary<string, RuleThresholds>(StringComparer.Ordinal)
            {
                ["complex-method/cyclomatic"] = R(L(CodeSceneSrc + ": 9", "E3", AtLeast("cyclomatic", 9)), L(CodeSceneSrc + ": 100", "E3", AtLeast("cyclomatic", 100))),
                ["deep-nesting"] = R(L(CodeSceneSrc + ": 4", "E3", AtLeast("nesting", 4))),
                ["long-method"] = R(L(CodeSceneSrc + ": 70", "E3", Over(Lines, 70)), L(CodeSceneSrc + ": 500", "E3", Over(Lines, 500))),
                ["large-file"] = R(L(CodeSceneSrc + ": 1000", "E3", Over("sourceLines", 1000)), L(CodeSceneSrc + ": 5000", "E3", Over("sourceLines", 5000))),
                ["too-many-parameters"] = R(L(CodeSceneSrc + ": 4, constructors 5", "E3", Over("parameters", 4), Over("constructorParameters", 5))),
                ["bumpy-road"] = R(L(CodeSceneSrc + ": 2 bumps at depth 2", "E3", AtLeast("bumps", 2)), null, new() { ["depth"] = "2" }),
                ["complex-condition"] = R(L(CodeSceneSrc + ": 2", "E3", AtLeast("booleanOperators", 2))),
            },
        },
        new()
        {
            Id = "clean-cadet", Name = "Clean-CaDET (University of Novi Sad)",
            Description = "Rules measured against human labels on C# code (Clean-CaDET ML-code-smell-CSharp notebooks; datasets on Zenodo, CC BY 4.0).",
            Rules = new Dictionary<string, RuleThresholds>(StringComparer.Ordinal)
            {
                ["complex-method/cyclomatic"] = R(L("Clean-CaDET: 10, Liu 2012 rule", "E1 via research", Over("cyclomatic", 10))),
                ["long-method"] = R(L("Clean-CaDET: 50 lines", "E1 via research", Over(Lines, 50))),
                ["large-type"] = R(L("Clean-CaDET: 100 lines or WMC 20, Liu 2012 rule", "E1 via research", Over("sourceLines", 100), Over("wmc", 20))),
            },
        },
        new()
        {
            Id = "doomen", Name = "Doomen C# coding guidelines",
            Description = "Dennis Doomen's C# coding guidelines (AV rules); values from the guideline text.",
            Rules = new Dictionary<string, RuleThresholds>(StringComparer.Ordinal)
            {
                ["long-method"] = R(L("Doomen AV1500: 15 statements", "E3", Over("statements", 15))),
                ["too-many-parameters"] = R(L("Doomen AV1561: 3 parameters", "E3", Over("parameters", 3))),
            },
        },
        new()
        {
            Id = "reportgenerator", Name = "ReportGenerator",
            Description = "Risk-hotspot thresholds of ReportGenerator (Apache-2.0 .NET coverage report tool).",
            Rules = new Dictionary<string, RuleThresholds>(StringComparer.Ordinal)
            {
                ["complex-method/cyclomatic"] = R(L("ReportGenerator: 15", "E1 via research", Over("cyclomatic", 15))),
                ["path-explosion"] = R(L("ReportGenerator: 200", "E1 via research", Over("npath", 200))),
            },
        },
        new()
        {
            Id = "ndepend", Name = "NDepend CQLinq defaults",
            Description = "NDepend default rule and metric thresholds. NDepend counts on IL (nesting, logical lines); our syntax counts approximate them (logical lines as statements).",
            Rules = new Dictionary<string, RuleThresholds>(StringComparer.Ordinal)
            {
                ["complex-method/cyclomatic"] = R(L("NDepend: 15, hard to maintain", "E3", Over("cyclomatic", 15)), L("NDepend: 30, extremely complex", "E3", Over("cyclomatic", 30))),
                ["deep-nesting"] = R(L("NDepend: depth 4, counted on IL", "E3", Over("nesting", 4)), L("NDepend: depth 8, counted on IL", "E3", Over("nesting", 8))),
                ["long-method"] = R(L("NDepend: 20 logical lines, as statements", "E3", Over("statements", 20)), L("NDepend: 40 logical lines, as statements", "E3", Over("statements", 40))),
                ["large-type"] = R(L("NDepend: 20 methods or 15 fields", "E3", Over("methods", 20), Over("fields", 15))),
                ["too-many-parameters"] = R(L("NDepend: over 5 is painful", "E3", Over("parameters", 5)), L("NDepend: 8 or more", "E3", AtLeast("parameters", 8))),
                ["high-coupling"] = R(L("NDepend TypeCe: 50, third-party types counted", "E3", Over("cboAll", 50))),
            },
        },
        new()
        {
            Id = "pmd", Name = "PMD (Java rule defaults)",
            Description = "Default properties of PMD's design rules (Java). Java counting differs in places; values applied to our C# counts.",
            Rules = new Dictionary<string, RuleThresholds>(StringComparer.Ordinal)
            {
                ["complex-method/cyclomatic"] = R(L("PMD CyclomaticComplexity: 10", "E3", AtLeast("cyclomatic", 10))),
                ["complex-method/cognitive"] = R(L("PMD CognitiveComplexity: 15", "E1 via research", AtLeast("cognitive", 15))),
                ["deep-nesting"] = R(L("PMD AvoidDeeplyNestedIfStmts: 3, if statements only", "E3", AtLeast("nesting", 3))),
                ["long-method"] = R(L("PMD NcssCount: 60 statements", "E3", AtLeast("statements", 60))),
                ["large-type"] = R(L("PMD: 10 methods, 15 fields or 1500 statements", "E3", Over("methods", 10), Over("fields", 15), AtLeast("statements", 1500))),
                ["path-explosion"] = R(L("PMD NPathComplexity: 200", "E1 via research", AtLeast("npath", 200))),
                ["too-many-parameters"] = R(L("PMD ExcessiveParameterList: 10", "E3", AtLeast("parameters", 10))),
                ["high-coupling"] = R(L("PMD CouplingBetweenObjects: 20", "E3", Over("cbo", 20))),
            },
        },
        new()
        {
            Id = "checkstyle", Name = "Checkstyle (Java check defaults)",
            Description = "Default properties of Checkstyle's checks (Java). Line counts follow Checkstyle (physical lines).",
            Rules = new Dictionary<string, RuleThresholds>(StringComparer.Ordinal)
            {
                ["complex-method/cyclomatic"] = R(L("Checkstyle CyclomaticComplexity: 10", "E3", Over("cyclomatic", 10))),
                ["deep-nesting"] = R(L("Checkstyle NestedIfDepth: 1, outermost 0, if statements only", "E3", Over("nesting", 2))),
                ["long-method"] = R(L("Checkstyle MethodLength: 150 lines", "E3", Over(Lines, 150))),
                ["large-type"] = R(L("Checkstyle MethodCount: 100; JavaNCSS: 1500 statements", "E3", Over("methods", 100), Over("statements", 1500))),
                ["large-file"] = R(L("Checkstyle FileLength: 2000 lines", "E3", Over("physicalLines", 2000))),
                ["too-many-parameters"] = R(L("Checkstyle ParameterNumber: 7", "E3", Over("parameters", 7))),
                ["path-explosion"] = R(L("Checkstyle NPathComplexity: 200", "E3", Over("npath", 200))),
                ["complex-condition"] = R(L("Checkstyle BooleanExpressionComplexity: 3", "E3", Over("booleanOperators", 3))),
                ["magic-number"] = R(L("Checkstyle MagicNumber: ignores -1, 0, 1, 2", "E3"), null, new() { ["ignore"] = "-1,0,1,2" }),
                ["high-coupling"] = R(L("Checkstyle ClassFanOutComplexity: 20", "E3", Over("cbo", 20))),
            },
        },
        new()
        {
            Id = "microsoft", Name = "Microsoft code analysis (CA1502)",
            Description = "Microsoft's CA1502 'avoid excessive complexity' (opt-in rule), on Microsoft's cyclomatic counting.",
            Rules = new Dictionary<string, RuleThresholds>(StringComparer.Ordinal)
            {
                ["complex-method/cyclomatic"] = R(L("Microsoft CA1502: 25, Microsoft counting", "E1", Over("cyclomaticMicrosoft", 25))),
                ["high-coupling"] = R(L("Microsoft CA1506: 95, Microsoft counting", "E1", Over("cboMicrosoft", 95))),
            },
        },
        new()
        {
            Id = "jscpd", Name = "jscpd defaults (duplication)",
            Description = "jscpd's default minimum clone size (50 tokens and 5 lines, the clone-benchmark standard); stricter than the default. Other rules use the default.",
            Rules = new Dictionary<string, RuleThresholds>(StringComparer.Ordinal)
            {
                ["duplicate-code"] = R(Both("jscpd: 50 tokens and 5 lines", "E3", AtLeast("tokens", 50), AtLeast("lines", 5)),
                    L("Skarp's own: a copy of 50 or more lines, or 3 or more copies of copyLines (20) or more lines", "E5 design ours",
                        AtLeast("lines", 50), AtLeast("copies", 3)),
                    new() { ["copyLines"] = "20", ["lowContent"] = "skip" }),
            },
        },
        new()
        {
            Id = "alves-percentile", Name = "Yamashita et al. 2016 benchmark percentiles (Java; Alves et al. method)",
            Description = "Benchmark-derived risk bands (80th and 90th percentile, upper end of the published ranges) from Java open-source systems; values from published research, not tool defaults.",
            Rules = new Dictionary<string, RuleThresholds>(StringComparer.Ordinal)
            {
                ["complex-method/cyclomatic"] = R(L("Yamashita et al.: 9-10, 80th percentile", "E2", Over("cyclomatic", 10)), L("Yamashita et al.: 16-19, 90th percentile", "E2", Over("cyclomatic", 19))),
                ["long-method"] = R(L("Chowdhury et al.: 36 lines, 80th percentile", "E2", Over(Lines, 36)), L("Chowdhury et al.: 63 lines, 90th percentile", "E2", Over(Lines, 63))),
                ["large-type"] = R(L("Yamashita et al.: 25-28 methods, 80th percentile", "E2", Over("methods", 28)), L("Yamashita et al.: 42-47 methods, 90th percentile", "E2", Over("methods", 47))),
                ["large-file"] = R(L("Yamashita et al.: 466-553 lines, 80th percentile", "E2", Over("sourceLines", 553)), L("Yamashita et al.: 847-975 lines, 90th percentile", "E2", Over("sourceLines", 975))),
            },
        },
    ];

    public static Preset? Find(string id) => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
