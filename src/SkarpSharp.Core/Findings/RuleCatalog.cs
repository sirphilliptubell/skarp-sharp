using System.Globalization;

namespace SkarpSharp.Findings;

public static class RuleIds
{
    public const string ComplexMethod = "complex-method";
    public const string DeepNesting = "deep-nesting";
    public const string LongMethod = "long-method";
    public const string LargeType = "large-type";
    public const string LargeFile = "large-file";
    public const string TooManyParameters = "too-many-parameters";
    public const string PathExplosion = "path-explosion";
    public const string ActiveComplexFile = "active-complex-file";
    public const string BumpyRoad = "bumpy-road";
    public const string ComplexCondition = "complex-condition";
    public const string EmptyCatch = "empty-catch";
    public const string MagicNumber = "magic-number";
    /// <summary>The first semantic-tier rule (class coupling).</summary>
    public const string HighCoupling = "high-coupling";
    /// <summary>Marinescu's God Class detection strategy over WMC, ATFD and TCC.</summary>
    public const string GodClass = "god-class";
    /// <summary>A private or internal member that nothing in the solution uses.</summary>
    public const string UnusedMember = "unused-member";
    /// <summary>A private or internal type that nothing in the solution uses.</summary>
    public const string UnusedType = "unused-type";
    /// <summary>The label for production code referenced only from test projects (never gated).</summary>
    public const string UsedOnlyByTests = "used-only-by-tests";
    /// <summary>A private or internal field or auto-property written but never read.</summary>
    public const string UnreadMember = "unread-member";
    /// <summary>Namespaces that depend on each other in a cycle (one finding per strongly connected component).</summary>
    public const string NamespaceCycle = "namespace-cycle";
    /// <summary>Copied code, exact or with renamed identifiers and changed literals (Type 1/2), one finding per clone group.</summary>
    public const string DuplicateCode = "duplicate-code";
    /// <summary>Near-miss copies of whole methods (Type 3); on by default, notice, score weight 0.</summary>
    public const string DuplicateCodeNear = "duplicate-code-near";
    /// <summary>A skarp suppression attribute without a justification (it does not count).</summary>
    public const string SuppressionWithoutJustification = "suppression-without-justification";
    /// <summary>A member with all of long-method, complex-method and deep-nesting, as one
    /// composite finding over those parts; on by default.</summary>
    public const string BrainMethod = "brain-method";
    /// <summary>A large type with a brain method and many methods, as one composite finding; experimental, off by default.</summary>
    public const string BrainClass = "brain-class";
    /// <summary>A complex member (cyclomatic 6 or more) no test runs, from coverage reports
    /// the user gives (<c>--coverage</c>); not evaluated without one.</summary>
    public const string UntestedComplexMethod = "untested-complex-method";
}

/// <summary>Static description of one rule: id, order, unit, the metrics its thresholds may use, and the two-level explanation.</summary>
public sealed class RuleInfo
{
    public required string Id { get; init; }
    /// <summary>Rule number (also the tie-break order of findings).</summary>
    public required int Order { get; init; }
    public required string Title { get; init; }
    /// <summary>member, type or file.</summary>
    public required string Unit { get; init; }
    public required IReadOnlyList<string> Metrics { get; init; }
    public IReadOnlyList<string> ParameterNames { get; init; } = [];
    /// <summary>One line, plain words (the short level of the two-level explanations).</summary>
    public required string Short { get; init; }
    /// <summary><c>syntax</c> (judged on the syntax output) or <c>semantic</c> (needs the semantic tier; its
    /// findings go to the semantic output file and baseline).</summary>
    public string Tier { get; init; } = "syntax";
    /// <summary>The rule is off unless switched on (<c>--rule id=on</c>, settings <c>rules</c>); experimental rules.</summary>
    public bool DefaultOff { get; init; }
    /// <summary>What it measures, why it matters, how to improve; thresholds are appended from the preset table.</summary>
    public required string Long { get; init; }
}

/// <summary>The rules: ids, titles and explanation texts (large type and large file are separate rules).</summary>
public static class RuleCatalog
{
    public static readonly IReadOnlyList<RuleInfo> All =
    [
        new()
        {
            Id = RuleIds.ComplexMethod, Order = 1, Title = "Complex method", Unit = "member",
            Metrics = ["cognitive", "cyclomatic", "cyclomaticMicrosoft"],
            Short = "Much branching logic in one member makes it hard to follow and to test.",
            Long = """
                What it measures: the control-flow complexity of one member. By default cognitive complexity, which adds one
                for each break in the linear flow (if, loops, switch, catch, jumps, runs of && and ||) and more for each level
                of nesting it sits in, whatever the preset: a preset without a cognitive threshold uses the default's cognitive
                threshold, credited to its source. Only `--complex-metric cyclomatic` on skarp analyze or baseline (or
                "complexMetric": "cyclomatic" in .skarp.json) reads cyclomatic complexity: the number of independent paths (1
                plus each decision point). Lambdas and local functions are folded into the member they sit in.

                Why it matters: every extra decision is a case a reader must keep in mind and a test must cover. Members with
                high complexity are where misunderstandings and missed cases tend to collect.

                How to improve: extract well-named methods for separate steps or branches, replace flag arguments and long
                if/else chains with early returns, guard clauses, a lookup table or polymorphism, and move deeply nested blocks
                into their own members.
                """,
        },
        new()
        {
            Id = RuleIds.DeepNesting, Order = 2, Title = "Deep nesting", Unit = "member",
            Metrics = ["nesting"],
            Short = "Code nested many levels deep forces the reader to keep every enclosing condition in mind.",
            Long = """
                What it measures: the deepest nesting of control structures in the member (if, switch, loops, try), the outermost
                one being level 1. An else-if chain does not deepen; a lambda or local function adds one level for its body.

                Why it matters: each level is a condition the reader has to remember to understand the innermost line. Deeply
                nested code is also hard to change without breaking one of the enclosing cases.

                How to improve: return early (guard clauses) instead of wrapping the rest of the body in an if, invert conditions,
                use continue in loops, and extract the inner block into a method with a name that says what it does.

                Note: tools count levels differently (some count only if statements, some count IL blocks), so a borrowed number
                comes with its own counting; skarp applies it to its own count (outermost construct = 1).
                """,
        },
        new()
        {
            Id = RuleIds.LongMethod, Order = 3, Title = "Long method", Unit = "member",
            Metrics = ["sourceLines", "statements", "physicalLines"],
            Short = "A long member usually does several things; it is harder to read, name, test and reuse.",
            Long = """
                What it measures: the size of one member, by default its physical lines (every line of the declaration span,
                signature, blank and comment lines included: the unit of the Clean-CaDET labels the default threshold was
                measured on). `--long-method-lines source` on skarp analyze or baseline (or "longMethodLines": "source" in
                .skarp.json) counts only lines with code instead; the choice applies to every line threshold of the rule. Some
                presets count statements. Lambdas and local functions count as lines of the member they sit in; their own
                records are not judged by this rule.

                Why it matters: a long member tends to mix several responsibilities and levels of abstraction. It is harder to
                give it a precise name, to test its parts separately and to change one part without touching the others.

                How to improve: look for blocks that already carry a comment or a blank line around them; those are often
                methods waiting to be extracted. Split setup, work and reporting; move a large literal table or object initializer
                into data or its own member.
                """,
        },
        new()
        {
            Id = RuleIds.LargeType, Order = 4, Title = "Large type", Unit = "type",
            Metrics = ["sourceLines", "wmc", "methods", "fields", "statements"],
            Short = "A large class or struct often holds several responsibilities that could live apart.",
            Long = """
                What it measures: the size of a class, struct, record or interface, all partial declarations merged: source lines
                of the declarations (nested types included), WMC (the sum of the members' cyclomatic complexity), methods
                (methods, operators and constructors) and fields, depending on the preset. Enums and delegates are not judged.
                The finding points at the first declaration.

                Why it matters: big types attract more change and more people, and they make it hard to see which members belong
                together. They often hide a second or third concept.

                How to improve: group members by the fields they use; a group that shares fields among itself but not with the
                rest is a candidate for its own type. Move helper logic to the types whose data it works on.

                Note: the default threshold (Clean-CaDET, 100 lines or WMC 20) was measured against human labels and is
                deliberately sensitive, hence a notice only.
                """,
        },
        new()
        {
            Id = RuleIds.LargeFile, Order = 5, Title = "Large file", Unit = "file",
            Metrics = ["sourceLines", "physicalLines"],
            Short = "A very large source file is hard to navigate, review and merge.",
            Long = """
                What it measures: source lines of the whole file (some presets: physical lines). Each type in the file is judged
                separately by large-type.

                Why it matters: large files collect unrelated changes, which makes reviews and merges harder and hides structure.

                How to improve: one top-level type per file, and split a large type (see large-type) or move nested helper types
                into their own files.
                """,
        },
        new()
        {
            Id = RuleIds.TooManyParameters, Order = 6, Title = "Too many parameters", Unit = "member",
            Metrics = ["parameters", "constructorParameters"],
            Short = "Many parameters make calls hard to read and suggest the member does too much or misses a type.",
            Long = """
                What it measures: declared parameters of a method, constructor, operator, local function or indexer, and of a
                primary constructor (record R(int A, ...), class C(...)). The receiver of an extension method (this) is not counted;
                params and optional parameters count as one each. Overrides and explicit interface implementations are skipped
                (their signature is decided elsewhere); lambdas are skipped. Some presets allow constructors one more parameter
                (dependency injection).

                Why it matters: long parameter lists are easy to call with arguments in the wrong order, and they often travel
                together through several layers, a sign of a missing concept.

                How to improve: introduce a parameter object or record for values that belong together, split the member by
                responsibility, or pass a collaborator instead of its parts. Wide positional records are often fine as data
                carriers; consider whether the record mixes concerns.
                """,
        },
        new()
        {
            Id = RuleIds.PathExplosion, Order = 7, Title = "Path explosion (NPath)", Unit = "member",
            Metrics = ["npath"],
            Short = "The number of distinct paths through this member is so large that it cannot be tested path by path.",
            Long = """
                What it measures: NPath, the number of acyclic execution paths. Sequential decisions multiply (two independent
                if statements give 4 paths, ten give 1,024), nested ones add. Lambdas and local functions are their own records.

                Why it matters: unlike cyclomatic complexity, NPath grows exponentially with independent decisions in sequence, so
                it shows members whose combinations of conditions cannot all be reasoned about or tested.

                How to improve: extract independent decision blocks into their own methods (each then has a small NPath), replace
                long chains of independent if statements with data-driven code, and avoid boolean flags that switch parts of the body.
                """,
        },
        new()
        {
            Id = RuleIds.ActiveComplexFile, Order = 8, Title = "Active complex file", Unit = "file",
            Metrics = ["activityShare"], ParameterNames = ["share"],
            Short = "This file changed often recently and contains code other rules flag, so effort spent here pays back first.",
            Long = """
                What it measures: files in the top share (default 4 percent) of changed files by commits in the history window
                (12 months by default), which also have at least one complexity-type finding (rules complex-method, deep-nesting,
                long-method, large-type, large-file, too-many-parameters, path-explosion, bumpy-road, complex-condition). It is the
                activity hotspot table (skarp hotspots) as a finding. Needs git history; skipped with a note without it.

                Why it matters: code that is both complex and frequently changed costs effort on every change. It does not say
                the file is risky or buggy: it says it changed often and contains code the other rules flag.

                How to improve: start improvements here, in small steps along with the planned changes; look at the file's other
                findings first.
                """,
        },
        new()
        {
            Id = RuleIds.BumpyRoad, Order = 9, Title = "Bumpy road", Unit = "member",
            Metrics = ["bumps"], ParameterNames = ["depth"],
            Short = "Several separate blocks of nested logic in one member: each bump is a missing abstraction.",
            Long = """
                What it measures: the top-level statements of the member body (try, using, lock and plain blocks are looked
                through) are scanned for if, switch and loop statements whose nested conditional or loop logic reaches the bump
                depth (default 2, the statement itself being level 1). Adjacent such statements form one chunk; the number of
                separate chunks is the number of bumps. The idea is CodeScene's Bumpy Road smell; the exact detection is ours.

                Why it matters: every bump asks the reader to build a mental model of another nested block, and the blocks often
                share state through local variables, which makes them hard to change one at a time.

                How to improve: extract each bump into a well-named method (Extract Method); often the bumps are steps of an
                algorithm that read better as a sequence of calls.
                """,
        },
        new()
        {
            Id = RuleIds.ComplexCondition, Order = 10, Title = "Complex condition", Unit = "member",
            Metrics = ["booleanOperators"],
            Short = "A boolean expression with many && and || operators is hard to read and easy to get wrong.",
            Long = """
                What it measures: the number of boolean operators in one branch condition: the condition of if, while, do, for
                and ?:, and the when clause of a switch case, a switch-expression arm or a catch clause. Other boolean
                expressions (assignments, returns, arguments, lambda bodies) are not counted, so a condition extracted into a
                named variable is not flagged. Counted: && and ||, and the pattern combinators and and or inside an is-pattern
                (not is not counted), across parentheses and negations; each operator counts, also in a run of the same operator.
                Patterns of case labels and switch arms are value lists, not conditions. The member reports its worst condition.

                Why it matters: each operator doubles the combinations a reader must check, and mixed && and || without clear
                grouping is a frequent source of logic errors.

                How to improve: give the parts names (local variables or small methods such as IsEligible(order)), split one
                condition into early returns, or move the decision to the type that owns the data.
                """,
        },
        new()
        {
            Id = RuleIds.EmptyCatch, Order = 11, Title = "Empty catch block", Unit = "member",
            Metrics = [],
            Short = "An empty catch silently swallows errors, so failures go unnoticed.",
            Long = """
                What it measures: catch clauses whose block has no statement. Severity (Skarp's own design; no source defines levels):
                warning for an empty block without any comment that catches everything (bare catch, catch (Exception) or
                catch (System.Exception)) without a when filter; notice when a comment documents the intent, when a specific
                exception type is caught, or when a filter narrows it. catch (OperationCanceledException) { } is ordinary in
                cancellation code and stays a notice.

                Why it matters: swallowed exceptions hide real failures and make problems surface far from their cause.

                How to improve: catch the specific exception you expect, log or handle it, or let it propagate. When ignoring is
                really right, say why in a comment so the next reader does not have to guess.
                """,
        },
        new()
        {
            Id = RuleIds.MagicNumber, Order = 12, Title = "Magic numbers", Unit = "member",
            Metrics = [], ParameterNames = ["ignore"],
            Short = "Unexplained numeric literals hide their meaning; a named constant says why the value is what it is.",
            Long = """
                What it measures: numeric literals in member bodies, one finding per member with the count. Not counted: values
                in the ignore list (default -1, 0, 1, 2), local const declarations, attribute arguments, default parameter values,
                element-access arguments such as [3], array sizes (new byte[4096], stackalloc char[256]), and multiplication or
                division by a power of ten (unit conversions such as * 1000). Field, property and enum initializers are not scanned (they name the value already). Notice level only.

                Why it matters: a bare 3600 or 0.075 tells the reader nothing about what it stands for, and the same value
                copied in several places drifts apart when one copy changes.

                How to improve: introduce a named constant, an enum member or a configuration value, and use it everywhere the
                value means the same thing.
                """,
        },
        new()
        {
            Id = RuleIds.DuplicateCode, Order = 20, Title = "Duplicate code", Unit = "clone group",
            Metrics = ["tokens", "lines", "copies"], ParameterNames = ["copyLines", "lowContent"],
            Short = "The same code, exact or with renamed identifiers and changed literals, appears in several places; a fix in one copy is easily missed in the others.",
            Long = DuplicationText.TokenRule,
        },
        new()
        {
            Id = RuleIds.DuplicateCodeNear, Order = 21, Title = "Near-duplicate methods", Unit = "clone group",
            Metrics = ["similarity", "lines"],
            Short = "Whole methods that are copies with small edits (statements added, removed or changed); notice only, score weight 0.",
            Long = DuplicationText.NearRule,
        },
        new()
        {
            Id = RuleIds.SuppressionWithoutJustification, Order = 22, Title = "Suppression without justification", Unit = "member",
            Metrics = [],
            Short = "A [SuppressMessage(\"skarp\", ...)] without a Justification does not count: say why the finding is acceptable.",
            Long = DuplicationText.Suppression,
        },
    ];

    /// <summary>Rules of the semantic tier (their findings live in the semantic output file and baseline).</summary>
    public static readonly IReadOnlyList<RuleInfo> Semantic =
    [
        new()
        {
            Id = RuleIds.HighCoupling, Order = 13, Title = "High class coupling", Unit = "type", Tier = "semantic",
            Metrics = ["cbo", "cboAll", "cboMicrosoft"],
            Short = "This type uses many other types, so changes in any of them can ripple into it and it is hard to reuse alone.",
            Long = """
                What it measures: class coupling (CBO, efferent coupling) of a class, struct, record or interface, all partial
                declarations merged: the number of distinct other types it uses in its base list, signatures, fields,
                properties, attributes and code (local variables, object creation, casts, static and extension calls, generic
                arguments, patterns and catch types). Arrays, nullable values and tuples count their element types; keyword
                types (int, string, object, decimal, ...), the type itself, its containing and its own nested types do not.
                By default .NET framework types (namespace System and below) are not counted; cboAll counts them, and
                the microsoft preset uses Microsoft's own counting (Visual Studio code metrics, CA1506). Nested types are
                measured on their own; enums and delegates are not judged. Needs the semantic tier (a compilation): types
                touching unresolved symbols are not evaluated.

                Why it matters: every type used is a reason to change and a dependency to understand, test and mock. Types
                that use many others sit in the middle of many change paths and are hard to reuse or replace.

                How to improve: move behaviour next to the data it uses, split the type by responsibility (groups of members
                that use different collaborators), pass a smaller abstraction instead of several concrete types, or introduce
                a facade for a group of collaborators that are always used together.
                """,
        },
        new()
        {
            Id = RuleIds.GodClass, Order = 14, Title = "God Class", Unit = "type", Tier = "semantic",
            Metrics = ["wmc", "atfd", "tcc"], ParameterNames = ["preset"],
            Short = "A complex class that uses other classes' data and has low cohesion: it likely centralizes too much of a subsystem.",
            Long = """
                What it measures: Marinescu's God Class detection strategy (Lanza and Marinescu, Object-Oriented Metrics in
                Practice, 2006): all three must hold: WMC (the sum of the members' cyclomatic complexity) is very high, ATFD
                (access to foreign data: the distinct fields and properties of other types the class uses as a whole, framework types excluded) is
                above "few", and TCC (tight class cohesion, Bieman-Kang: the share of visible method pairs that use common
                state) is below one third, compared as an exact fraction. Rule sets (--threshold god-class.preset=<id> on skarp
                analyze or baseline, or thresholds.god-class.preset in .skarp.json): lanza-marinescu-2006 (default): WMC at
                least 47, ATFD over 5, TCC below 1/3; iplasma: WMC over 20, ATFD over 4, TCC below 1/3; atfd-2: WMC at least 47,
                ATFD over 2, TCC below 1/3. The values are Java statistics, not a C# calibration. Classes, structs and records
                only; a type whose TCC is not defined (fewer than two visible methods, no state use) or that was not evaluated
                gets no finding.
                Notice level only. Needs the semantic tier.

                Why it matters: such a class tends to do the work of several, reaching into the data of smaller classes
                instead of letting them do their part. It is where changes collide and where defects were found more often in
                studies of Java systems.

                How to improve: find groups of methods that use the same fields (low TCC means there are several) and extract
                them into their own classes; move logic that mostly works on another class's data into that class (the foreign
                data ATFD lists in the semantic JSON names the candidates).
                """,
        },
        new()
        {
            Id = RuleIds.UnusedMember, Order = 15, Title = "Unused member", Unit = "member", Tier = "semantic",
            Metrics = [],
            Short = "A private or internal member that nothing in the solution uses: it can most likely be deleted.",
            Long = UnusedCodeText.Member,
        },
        new()
        {
            Id = RuleIds.UnusedType, Order = 16, Title = "Unused type", Unit = "type", Tier = "semantic",
            Metrics = [],
            Short = "A private or internal type that nothing in the solution uses: it can most likely be deleted with its members.",
            Long = UnusedCodeText.Type,
        },
        new()
        {
            Id = RuleIds.UsedOnlyByTests, Order = 17, Title = "Used only by tests", Unit = "member", Tier = "semantic",
            Metrics = [],
            Short = "Production code that only test projects use: a label, never gated; the code may be dead apart from its tests.",
            Long = UnusedCodeText.TestOnly,
        },
        new()
        {
            Id = RuleIds.UnreadMember, Order = 18, Title = "Unread member", Unit = "member", Tier = "semantic",
            Metrics = [],
            Short = "A private or internal field or auto-property that is written but never read: check its writes for side effects, then remove it.",
            Long = UnusedCodeText.Unread,
        },
        new()
        {
            Id = RuleIds.NamespaceCycle, Order = 19, Title = "Namespace cycle", Unit = "namespace", Tier = "semantic",
            Metrics = [],
            Short = "Namespaces that depend on each other in a cycle cannot be understood, tested or moved apart; cut the cheapest dependency first.",
            Long = """
                What it measures: the namespace dependency graph of the solution's non-test projects: a namespace depends on
                another when a type declared in it uses a type declared in the other (base types, signatures, attributes,
                members called or read, object creation, casts, patterns, typeof and nameof; using directives and doc-comment
                crefs do not count). Nodes are (assembly, namespace): a namespace name declared in several assemblies is one node
                per assembly and is named with the assembly in parentheses; parent and child namespaces are separate nodes.
                Sources are the kept, handwritten, non-test files; references to framework and package types do not count, nor
                (by default) references to types declared only in generated code (--include-generated-targets on skarp analyze
                or baseline, or "namespaceCycles": { "includeGeneratedTargets": true } in .skarp.json, keeps them; experimental). The weight of a dependency is its number of distinct (using type, used type) pairs. One finding
                per strongly connected component of two or more namespaces (Tarjan's algorithm), named after its ordinal-first
                namespace; its value is the number of namespaces (a growing cycle is worsened). The message leads with the
                cheapest cuts: a suggested set of dependencies whose removal breaks every cycle (greedy: repeatedly the lightest
                dependency of the shortest remaining cycle; a suggestion, not a guaranteed minimum), then the members and one
                shortest cycle; the finding points at the first reference of the cheapest cut. The semantic JSON lists, per
                component, the mutual pairs (lighter direction first), every dependency with up to 5 example references
                (path:line, using type, used type) and the cut. Needs the semantic tier. Warning level.

                Why it matters: namespaces in a cycle form one unit: none can be understood, tested, reused or moved to its own
                assembly without the others, and a change in one can ripple around the cycle (the Acyclic Dependencies
                Principle, Robert C. Martin; levelization, John Lakos). Tools such as NDepend, Structure101 and Lattix report
                them for the same reason. Large cycles usually hide a few light back references.

                How to improve: start with the cheapest cut: open its example references; usually a constant, a helper, an
                interface or a type in the wrong namespace makes a lower-level namespace depend on a higher one. Move that type,
                invert the dependency with an interface or event owned by the lower namespace, or pass the value in instead of
                looking it up. Re-run skarp: the component shrinks or disappears.
                """,
        },
    ];

    /// <summary>Every rule: the syntax rules, then the semantic ones.</summary>
    public static readonly IReadOnlyList<RuleInfo> Every = [.. All, .. Semantic];

    /// <summary>
    /// The composite rules, which group findings of other rules and make none of their
    /// own (no threshold, score weight or gate entry). Kept out of <see cref="All"/> and <see cref="Every"/>, so with
    /// them off (the default) every output, count table and settings listing stays as it was; <see cref="Find"/> and
    /// <see cref="Get"/> know them.
    /// </summary>
    public static readonly IReadOnlyList<RuleInfo> Composite =
    [
        new()
        {
            Id = RuleIds.BrainMethod, Order = 23, Title = "Brain method (composite)", Unit = "member",
            Metrics = [], ParameterNames = ["parts", "minParts"],
            Short = "A member that is long, complex and deeply nested at once, reported as one finding listing those parts; on by default.",
            Long = BrainText.Method,
        },
        new()
        {
            Id = RuleIds.BrainClass, Order = 24, Title = "Brain class (experimental composite)", Unit = "type", DefaultOff = true,
            Metrics = [], ParameterNames = ["minMethods", "preset"],
            Short = "A large non-static type with at least one brain method and many methods, reported as one finding listing its parts; experimental, off by default.",
            Long = BrainText.Class,
        },
    ];

    /// <summary>
    /// Rules that need a coverage report. Kept out of <see cref="All"/> and <see cref="Every"/>
    /// like the composites, so a run without <c>--coverage</c> lists, counts and writes exactly what it did before; a run
    /// with a report adds them (<see cref="WithCoverage"/>).
    /// </summary>
    public static readonly IReadOnlyList<RuleInfo> Coverage =
    [
        new()
        {
            Id = RuleIds.UntestedComplexMethod, Order = 25, Title = "Untested complex method", Unit = "member",
            Metrics = ["cyclomatic"],
            Short = "A complex member (cyclomatic 6 or more) that no test runs, from the coverage report you give; not evaluated without one.",
            Long = CoverageText.Rule,
        },
    ];

    /// <summary>The syntax rules of a run: <see cref="All"/>, plus the coverage rules when a report was given.</summary>
    public static IReadOnlyList<RuleInfo> WithCoverage(bool coverage) => coverage ? [.. All, .. Coverage] : All;

    /// <summary>True for a rule that needs a coverage report.</summary>
    public static bool IsCoverage(string id) => id is RuleIds.UntestedComplexMethod;

    /// <summary>True for a composite rule (brain-method, brain-class).</summary>
    public static bool IsComposite(string id) => id is RuleIds.BrainMethod or RuleIds.BrainClass;

    static readonly Dictionary<string, RuleInfo> ById = Every.Concat(Composite).Concat(Coverage).ToDictionary(r => r.Id, StringComparer.Ordinal);

    public static RuleInfo Get(string id) => ById.TryGetValue(id, out var r) ? r : throw new ArgumentException($"unknown rule '{id}'");

    public static RuleInfo? Find(string id) => ById.GetValueOrDefault(id);

    /// <summary>Every rule <see cref="Find"/> knows, in the order <c>skarp explain</c> lists them.</summary>
    public static IEnumerable<RuleInfo> Known => Every.Concat(Composite).Concat(Coverage);

    /// <summary>
    /// The paragraph of a rule's long text that starts with <c>How to improve:</c>, up to the next blank line or
    /// the next line that starts a labelled section (<c>How it is reported:</c>), with <c>\n</c> line endings; null when
    /// the rule has none (a test keeps every rule with one).
    /// </summary>
    public static string? HowToImprove(RuleInfo rule)
    {
        var text = rule.Long.ReplaceLineEndings("\n");
        int start = text.IndexOf("How to improve:", StringComparison.Ordinal);
        if (start < 0) return null;
        int end = text.IndexOf("\n\n", start, StringComparison.Ordinal);
        var paragraph = end < 0 ? text[start..] : text[start..end];
        var lines = paragraph.Split('\n');
        int keep = 1;
        while (keep < lines.Length && !SectionLabel(lines[keep])) keep++;
        return string.Join("\n", lines, 0, keep).TrimEnd();
    }

    /// <summary>A line that starts a labelled section of a rule text: a capital, a few words, a colon and a space.</summary>
    static bool SectionLabel(string line)
    {
        int colon = line.IndexOf(": ", StringComparison.Ordinal);
        return colon is > 2 and < 40 && char.IsUpper(line[0]) && line[..colon].All(c => char.IsLetter(c) || c == ' ');
    }

    public static void ValidateParameter(string rule, string name, string value)
    {
        bool ok = (rule, name) switch
        {
            (RuleIds.ActiveComplexFile, "share") => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0 && s <= 100,
            (RuleIds.BumpyRoad, "depth") => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d >= 2,
            (RuleIds.MagicNumber, "ignore") => ParseNumberList(value) is not null,
            (RuleIds.GodClass, "preset") => GodClassPresets.Find(value) is not null,
            (RuleIds.DuplicateCode, "copyLines") => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var cl) && cl >= 1,
            (RuleIds.DuplicateCode, "lowContent") => value is "skip" or "report",
            (RuleIds.BrainMethod, "parts") => BrainRules.ParseParts(value) is not null,
            (RuleIds.BrainMethod, "minParts") => value == "all" || (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var mp) && mp >= 1),
            (RuleIds.BrainClass, "minMethods") => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var mm) && mm >= 0,
            (RuleIds.BrainClass, "preset") => value is BrainRules.OursForm or BrainRules.BookForm,
            _ => false,
        };
        if (!ok)
            throw new ArgumentException($"invalid value '{value}' for {rule}.{name}" + (name switch
            {
                "share" => " (a percentage over 0 and at most 100)",
                "depth" => " (an integer, at least 2: nested blocks are recorded from depth 2)",
                "ignore" => " (comma-separated numbers)",
                "preset" when rule == RuleIds.BrainClass => $" ({BrainRules.OursForm} or {BrainRules.BookForm})",
                "preset" => $" (one of {string.Join(", ", GodClassPresets.Sets.Select(s => s.Id))})",
                "parts" => $" (comma-separated member rules: {string.Join(", ", BrainRules.PartRules)})",
                "minParts" => " (an integer, at least 1, or all)",
                "minMethods" => " (an integer, at least 0)",
                "copyLines" => " (an integer, at least 1)",
                "lowContent" => " (skip or report)",
                _ => "",
            }));
    }

    public static double[]? ParseNumberList(string value)
    {
        if (value.Trim().Length == 0) return [];
        var list = new List<double>();
        foreach (var part in value.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
            list.Add(v);
        }
        return [.. list];
    }
}
