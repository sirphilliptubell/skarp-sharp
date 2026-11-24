using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using SkarpSharp.Metrics;
using SkarpSharp.Model;
using SkarpSharp.Conditionals;
using SkarpSharp.Syntax;

namespace SkarpSharp.Analysis;

/// <summary>The variant settings of a run (<c>symbols</c>, <c>variants.cap</c>, <c>variants.checkCap</c>).</summary>
public sealed class VariantSettings
{
    public const int DefaultCap = 32;
    public const int DefaultCheckCap = 4;

    /// <summary>The <c>symbols</c> setting; empty: automatic planning.</summary>
    public IReadOnlyList<string> Symbols { get; init; } = [];
    /// <summary>Variants per file besides the base parse (<c>analyze</c>).</summary>
    public int Cap { get; init; } = DefaultCap;
    /// <summary>Variants per file of <c>skarp check</c>, the base parse counted in.</summary>
    public int CheckCap { get; init; } = DefaultCheckCap;

    public static readonly VariantSettings Default = new();

    public string Mode => Symbols.Count > 0 ? "symbols" : "covering";

    /// <summary>The settings in the words the settings fingerprint hashes.</summary>
    public string FingerprintText => $"{Mode} cap={Cap} checkCap={CheckCap} symbols={string.Join('|', Symbols.Order(StringComparer.Ordinal))}";
}

/// <summary>How one file's variants are planned and parsed.</summary>
public sealed class VariantOptions
{
    public VariantSettings Settings { get; init; } = VariantSettings.Default;
    /// <summary>Variants besides the base parse.</summary>
    public int Cap { get; init; } = VariantSettings.DefaultCap;
    /// <summary>The project-file symbols (read on first use); null: no filter.</summary>
    public Lazy<IReadOnlySet<string>>? ProjectSymbols { get; init; }
    /// <summary>Parse a file's variants in parallel (<c>check</c>: one file).</summary>
    public bool Parallel { get; init; }

    /// <summary>The options of a full run under <paramref name="root"/>.</summary>
    public static VariantOptions ForRun(string root, VariantSettings settings) => new()
    {
        Settings = settings, Cap = settings.Cap,
        ProjectSymbols = new Lazy<IReadOnlySet<string>>(() => Conditionals.ProjectSymbols.For(root), LazyThreadSafetyMode.ExecutionAndPublication),
    };

    /// <summary>The options of <c>skarp check</c>: the base counts as one of the check cap, variants parsed in parallel.</summary>
    public static VariantOptions ForCheck(string root, VariantSettings settings) => new()
    {
        Settings = settings, Cap = Math.Max(0, settings.CheckCap - 1), Parallel = true,
        ProjectSymbols = new Lazy<IReadOnlySet<string>>(() => Conditionals.ProjectSymbols.For(root), LazyThreadSafetyMode.ExecutionAndPublication),
    };
}

/// <summary>
/// Parses a file's planned symbol variants over its one <see cref="SourceText"/> and merges them
/// into the base view's record. Each variant is a full <c>CSharpSyntaxTree.ParseText</c> with its symbols (never
/// <c>WithRootAndOptions</c>, whose <c>BranchTaken</c> stays stale). Members are matched on (identifier position, kind,
/// name without the lambda ordinal); each metric keeps its worst value; type and file
/// sizes take the largest single variant; variant-only members are numbered after the base members in position
/// order. Variant trees are dropped after their facts are taken.
/// </summary>
public static partial class VariantMerge
{
    /// <summary>The match key of a body: identifier position, kind and name with lambda ordinals removed.</summary>
    public static string MatchKey(MemberBody b) => $"{Anchor(b)}|{b.Kind}|{LambdaOrdinal().Replace(b.Name, "/lambda")}";

    [GeneratedRegex(@"/lambda\d+")] private static partial Regex LambdaOrdinal();
    [GeneratedRegex(@"#\d+$")] private static partial Regex Ordinal();

    /// <summary>The identifier token of a declaration (attributes and modifiers may sit inside an <c>#if</c>); top-level
    /// statements are one record per file.</summary>
    public static int Anchor(MemberBody b) => b.Kind == MemberKind.TopLevel ? 0 : b.Declaration switch
    {
        MethodDeclarationSyntax m => m.Identifier.SpanStart,
        ConstructorDeclarationSyntax c => c.Identifier.SpanStart,
        DestructorDeclarationSyntax d => d.Identifier.SpanStart,
        OperatorDeclarationSyntax o => o.OperatorToken.SpanStart,
        ConversionOperatorDeclarationSyntax co => co.Type.SpanStart,
        PropertyDeclarationSyntax p => p.Identifier.SpanStart,
        IndexerDeclarationSyntax i => i.ThisKeyword.SpanStart,
        EventDeclarationSyntax e => e.Identifier.SpanStart,
        AccessorDeclarationSyntax a => a.Keyword.SpanStart,
        LocalFunctionStatementSyntax l => l.Identifier.SpanStart,
        _ => b.Declaration.SpanStart,
    };

    static int Anchor(BaseTypeDeclarationSyntax t) => t.Identifier.SpanStart;

    static IEnumerable<BaseTypeDeclarationSyntax> TypeDeclarations(SyntaxTree tree) =>
        tree.GetCompilationUnitRoot().DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax).OfType<BaseTypeDeclarationSyntax>();

    static int ErrorCount(SyntaxTree tree) =>
        !tree.GetRoot().ContainsDiagnostics ? 0 : tree.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);

    /// <summary>The base parse of a file and its record, as <see cref="Analyzer.AnalyzeText"/> made them.</summary>
    internal sealed record BaseParse(FileResult File, SourceText Text, SyntaxTree Tree, LineKind[] Kinds, List<MemberBody> Bodies, string Path, bool CoverageLines);

    internal static void Apply(BaseParse b, VariantOptions options)
    {
        var (file, text, baseTree, path) = (b.File, b.Text, b.Tree, b.Path);
        var map = DirectiveMap.Collect(baseTree.GetRoot());
        if (map is null) return;
        var plan = VariantPlanner.Plan(map, new PlannerSettings { Symbols = options.Settings.Symbols, ProjectSymbols = options.ProjectSymbols?.Value }, options.Cap);
        Describe(file, plan);
        if (plan.Variants.Count == 0) return;

        var trees = new SyntaxTree[plan.Variants.Count];
        var errors = new int[trees.Length];
        void ParseOne(int i)
        {
            trees[i] = CSharpSyntaxTree.ParseText(text, Analyzer.ParseOptions.WithPreprocessorSymbols(plan.Variants[i].Symbols), path);
            errors[i] = ErrorCount(trees[i]);
        }
        if (options.Parallel && trees.Length > 1) System.Threading.Tasks.Parallel.For(0, trees.Length, ParseOne);
        else for (int i = 0; i < trees.Length; i++) ParseOne(i);

        // Section 2 item 9: a variant with more syntax errors than the base is an unreal mix; it is not merged.
        int baseErrors = ErrorCount(baseTree);
        var kept = new List<(PlannedVariant Variant, SyntaxTree Tree)>();
        for (int i = 0; i < trees.Length; i++)
        {
            if (errors[i] > baseErrors) { file.Variants![i].Discarded = true; continue; }
            Debug.Assert(map.Mismatches(trees[i], plan.Variants[i].Symbols).Count == 0, $"{path}: the directive evaluator disagrees with BranchTaken for {plan.Variants[i].Name}");
            kept.Add((plan.Variants[i], trees[i]));
        }
        if (kept.Count > 0) new Merger(b, map).Run(kept);
    }

    static void Describe(FileResult file, VariantPlan plan)
    {
        file.Symbols = [.. plan.Symbols];
        file.FilteredSymbols = plan.FilteredSymbols.Count > 0 ? [.. plan.FilteredSymbols] : null;
        file.Variants = plan.Variants.Count > 0
            ? [.. plan.Variants.Select(v => new FileVariant { Name = v.Name, Framework = v.Framework?.Moniker, Symbols = [.. v.Symbols.Order(StringComparer.Ordinal)] })]
            : null;
        file.VariantsCapped = plan.Capped ? true : null;
        file.UnreachableBranches = plan.Unreachable.Count > 0
            ? [.. plan.Unreachable.Select(u => new UnreachableBranchInfo { Line = u.Line, Directive = u.Directive, Reason = u.Reason })]
            : null;
        file.UnplannedBranches = plan.UnplannedBranches > 0 ? plan.UnplannedBranches : null;
    }

    /// <summary>A variant-only member until it is numbered.</summary>
    sealed record Extra(MemberResult Result, MemberBody Body, string? ParentKey, int Anchor);

    sealed class Merger(BaseParse parse, DirectiveMap map)
    {
        readonly FileResult file = parse.File;
        readonly LineKind[] baseKinds = parse.Kinds;
        readonly List<MemberBody> baseBodies = parse.Bodies;
        readonly Dictionary<string, MemberResult> byKey = new(StringComparer.Ordinal);
        readonly Dictionary<string, Extra> extras = new(StringComparer.Ordinal);
        readonly Dictionary<int, TypeCounts> typesByAnchor = [];
        readonly Dictionary<string, TypeCounts> typesByName = new(StringComparer.Ordinal);
        readonly Dictionary<string, TypeMemberSums> sums = new(StringComparer.Ordinal);
        readonly Dictionary<string, string> sumsVariant = new(StringComparer.Ordinal);
        Dictionary<string, TypeMemberSums> baseSums = [];
        CoverageLineKind[]? coverage;
        List<(int, int)>? excluded;

        public void Run(List<(PlannedVariant Variant, SyntaxTree Tree)> kept)
        {
            for (int i = 0; i < baseBodies.Count; i++) byKey.TryAdd(MatchKey(baseBodies[i]), file.Members[i]);
            baseSums = Sums(file.Members.Select(m => (m, (MemberValues?)null)));
            foreach (var (k, v) in baseSums) sums[k] = v;
            IndexBaseTypes();
            if (parse.CoverageLines && file.CoverageLines is { } cl) { coverage = (CoverageLineKind[])cl.Kinds.Clone(); excluded = [.. cl.Excluded]; }
            var baseTaken = map.Evaluate(new HashSet<string>());
            foreach (var (variant, tree) in kept) Merge(variant, tree, baseTaken);
            Number();
            file.TypeSums = sums;
            file.BaseTypeSums = baseSums;
            file.TypeSumsVariant = sumsVariant;
            if (coverage is not null) file.CoverageLines = new CoverageLines { Kinds = coverage, Excluded = excluded! };
        }

        /// <summary>Base declarations by identifier position and by name (the same walk as <see cref="Analyzer.AnalyzeText"/>).</summary>
        void IndexBaseTypes()
        {
            int i = 0;
            foreach (var decl in TypeDeclarations(parse.Tree))
            {
                if (i >= file.Types.Count) break;
                typesByAnchor[Anchor(decl)] = file.Types[i];
                typesByName.TryAdd(file.Types[i].Name, file.Types[i]);
                i++;
            }
        }

        void Merge(PlannedVariant variant, SyntaxTree tree, bool[] baseTaken)
        {
            var label = variant.Name;
            var kinds = ClassifyChanged(tree, baseKinds, map, baseTaken, map.Evaluate(variant.Symbols));
            MergeLines(kinds, label);
            if (coverage is not null) MergeCoverage(Coverage.CoverageLineScan.Compute(tree, kinds));

            var bodies = MemberCollector.Collect(tree, parse.Path);
            var keyOfId = bodies.ToDictionary(b => b.Id, MatchKey, StringComparer.Ordinal);
            // Lines whose kind differs from the base: a member whose span has none is token for token the base member.
            var diff = new int[kinds.Length + 1];
            for (int i = 0; i < kinds.Length; i++) diff[i + 1] = diff[i] + (i < baseKinds.Length && kinds[i] == baseKinds[i] ? 0 : 1);
            var values = new List<(MemberResult, MemberValues?)>();
            foreach (var b in bodies)
            {
                var key = keyOfId[b.Id];
                int s = Math.Clamp(b.StartLine - 1, 0, kinds.Length), e = Math.Clamp(b.EndLine, s, kinds.Length);
                byKey.TryGetValue(key, out var baseMember);
                if (baseMember is not null && diff[e] == diff[s])
                {
                    values.Add((baseMember, baseMember.BaseValues ?? MemberValues.Of(baseMember)));
                    continue;
                }
                var r = Analyzer.Measure(b, kinds);
                values.Add((r, null));
                if (baseMember is not null) Worsen(baseMember, r, label, snapshot: true);
                else if (extras.TryGetValue(key, out var extra)) Worsen(extra.Result, r, label, snapshot: false);
                else
                {
                    r.Variant = label;
                    r.OnlyInVariants = true;
                    extras[key] = new Extra(r, b, b.ParentId is { } pid && keyOfId.TryGetValue(pid, out var pk) ? pk : null, Anchor(b));
                }
            }
            foreach (var (type, v) in Sums(values))
            {
                var max = sums.TryGetValue(type, out var old) ? TypeMemberSums.Max(old, v) : v;
                if (max != old) { sums[type] = max; sumsVariant.TryAdd(type, label); }
            }
            MergeTypes(tree, kinds, label);
        }

        /// <summary>Member sums per type name over top-level members, from the given values (or the record's own).</summary>
        static Dictionary<string, TypeMemberSums> Sums(IEnumerable<(MemberResult Member, MemberValues? Values)> members)
        {
            var result = new Dictionary<string, TypeMemberSums>(StringComparer.Ordinal);
            foreach (var (m, v) in members)
            {
                if (m.Parent is not null) continue;
                result[m.Type] = result.GetValueOrDefault(m.Type).Add(v?.Cyclomatic ?? m.Cyclomatic, v?.Cognitive ?? m.Cognitive, v?.Statements ?? m.Statements);
            }
            return result;
        }

        void MergeLines(LineKind[] kinds, string label)
        {
            var counts = LineCounts.From(kinds);
            if (counts.Source <= file.Lines.Source) return;
            file.BaseLines ??= file.Lines;
            file.Lines = Analyzer.Dto(counts);
            file.LinesVariant = label;
        }

        /// <summary>Section 2 item 12: a line is inactive only if inactive in every kept variant; code wins over non-code.</summary>
        void MergeCoverage(CoverageLines variant)
        {
            for (int i = 0; i < coverage!.Length && i < variant.Kinds.Length; i++)
            {
                if (coverage[i] == CoverageLineKind.Inactive && variant.Kinds[i] != CoverageLineKind.Inactive) coverage[i] = variant.Kinds[i];
                else if (coverage[i] == CoverageLineKind.NonCode && variant.Kinds[i] == CoverageLineKind.Code) coverage[i] = CoverageLineKind.Code;
            }
            foreach (var span in variant.Excluded) if (!excluded!.Contains(span)) excluded.Add(span);
        }

        /// <summary>Type declarations: the same declaration (identifier position) or a twin of the same name keeps the larger
        /// count per field; a name the base view never declares is a variant-only declaration (twins are never summed).</summary>
        void MergeTypes(SyntaxTree tree, LineKind[] kinds, string label)
        {
            foreach (var decl in TypeDeclarations(tree))
            {
                var counts = Analyzer.Count(decl, kinds);
                int anchor = Anchor(decl);
                if (!typesByAnchor.TryGetValue(anchor, out var target) && !typesByName.TryGetValue(counts.Name, out target))
                {
                    counts.Variant = label;
                    counts.OnlyInVariants = true;
                    file.Types.Add(counts);
                    typesByName[counts.Name] = counts;
                    typesByAnchor[anchor] = counts;
                    continue;
                }
                typesByAnchor.TryAdd(anchor, target);
                RaiseCounts(target, counts, label);
            }
        }

        static void RaiseCounts(TypeCounts t, TypeCounts v, string label)
        {
            bool raises = v.Fields > t.Fields || v.Properties > t.Properties || v.Methods > t.Methods || v.Constructors > t.Constructors
                || v.Events > t.Events || v.NestedTypes > t.NestedTypes || v.PublicMembers > t.PublicMembers || v.SourceLines > t.SourceLines
                || (v.PrimaryConstructorParameters ?? 0) > (t.PrimaryConstructorParameters ?? 0);
            if (!raises) return;
            if (t.OnlyInVariants != true) t.BaseValues ??= TypeValues.Of(t);
            t.Fields = Math.Max(t.Fields, v.Fields); t.Properties = Math.Max(t.Properties, v.Properties); t.Methods = Math.Max(t.Methods, v.Methods);
            t.Constructors = Math.Max(t.Constructors, v.Constructors); t.Events = Math.Max(t.Events, v.Events);
            t.NestedTypes = Math.Max(t.NestedTypes, v.NestedTypes); t.PublicMembers = Math.Max(t.PublicMembers, v.PublicMembers);
            t.SourceLines = Math.Max(t.SourceLines, v.SourceLines);
            if ((v.PrimaryConstructorParameters ?? 0) > (t.PrimaryConstructorParameters ?? 0)) t.PrimaryConstructorParameters = v.PrimaryConstructorParameters;
            t.Variant ??= label;
        }

        /// <summary>Variant-only members get keys and ids after the base members' (ordinals continue), in
        /// position order, parents before their nested bodies.</summary>
        void Number()
        {
            var keyCount = new Dictionary<string, int>(StringComparer.Ordinal);
            var idCount = new Dictionary<string, int>(StringComparer.Ordinal);
            var lambdas = new Dictionary<string, int>(StringComparer.Ordinal);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in file.Members)
            {
                var stem = Ordinal().Replace(m.Key, "");
                keyCount[stem] = keyCount.GetValueOrDefault(stem) + 1;
                var idStem = Ordinal().Replace(m.Id, "");
                idCount[idStem] = idCount.GetValueOrDefault(idStem) + 1;
                ids.Add(m.Id);
                if (m.Kind == nameof(MemberKind.Lambda) && m.Key.LastIndexOf("/lambda", StringComparison.Ordinal) is var at and > 0)
                    lambdas[m.Key[..at]] = lambdas.GetValueOrDefault(m.Key[..at]) + 1;
            }
            var resolved = new Dictionary<string, MemberResult>(byKey, StringComparer.Ordinal);
            var resolvedId = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, x) in extras.OrderBy(e => e.Value.Anchor).ThenBy(e => e.Value.Body.ParentId is null ? 0 : 1).ThenBy(e => e.Key, StringComparer.Ordinal))
            {
                var r = x.Result;
                var b = x.Body;
                MemberResult? parent = x.ParentKey is not null && resolved.TryGetValue(x.ParentKey, out var p) ? p : null;
                // A parent that is not a record (a field or property initializer) is named by its id, as the base view does.
                string? parentKey = parent?.Key ?? b.ParentId;
                string stem;
                if (parentKey is not null && b.Kind == MemberKind.Lambda)
                {
                    int n = lambdas.GetValueOrDefault(parentKey) + 1;
                    lambdas[parentKey] = n;
                    stem = $"{parentKey}/lambda{n}";
                }
                else if (parentKey is not null) stem = $"{parentKey}/{b.Name[(b.Name.LastIndexOf('/') + 1)..]}";
                else stem = Ordinal().Replace(b.Key, "");
                int c = keyCount.GetValueOrDefault(stem) + 1;
                keyCount[stem] = c;
                r.Key = c == 1 ? stem : $"{stem}#{c}";

                var idStem = Ordinal().Replace(parent is not null && b.ParentId is { } pid ? parent.Id + b.Id[pid.Length..] : b.Id, "");
                int k = idCount.GetValueOrDefault(idStem) + 1;
                string id = k == 1 ? idStem : $"{idStem}#{k}";
                while (!ids.Add(id)) id = $"{idStem}#{++k}";
                idCount[idStem] = k;
                r.Id = id;
                r.Parent = parent?.Id ?? b.ParentId;
                resolved[key] = r;
                file.Members.Add(r);
            }
        }
    }

    /// <summary>Section 2 item 10: the worse value per metric (higher complexity, length, nesting, parameters); facts
    /// listed per occurrence (empty catches, numeric literals) are united. Snapshots the base values on the first change.</summary>
    static void Worsen(MemberResult t, MemberResult v, string label, bool snapshot)
    {
        bool lines = v.Lines.Source > t.Lines.Source;
        bool boolean = (v.BooleanOperatorsMax ?? 0) > (t.BooleanOperatorsMax ?? 0);
        bool blocks = BlockWeight(v.NestedBlocks) > BlockWeight(t.NestedBlocks);
        var catches = (v.EmptyCatches ?? []).Where(e => !(t.EmptyCatches ?? []).Any(x => x.Line == e.Line)).ToList();
        var literals = (v.NumericLiterals ?? []).Where(e => !(t.NumericLiterals ?? []).Any(x => x.Line == e.Line && x.Text == e.Text)).ToList();
        bool worse = v.Cyclomatic > t.Cyclomatic || v.CyclomaticMicrosoft > t.CyclomaticMicrosoft || v.Cognitive > t.Cognitive || v.Npath > t.Npath
            || v.Nesting > t.Nesting || v.Statements > t.Statements || (v.Parameters ?? 0) > (t.Parameters ?? 0) || lines || boolean || blocks
            || catches.Count > 0 || literals.Count > 0;
        if (!worse) return;
        if (snapshot) t.BaseValues ??= MemberValues.Of(t);
        t.Cyclomatic = Math.Max(t.Cyclomatic, v.Cyclomatic);
        t.CyclomaticMicrosoft = Math.Max(t.CyclomaticMicrosoft, v.CyclomaticMicrosoft);
        t.Cognitive = Math.Max(t.Cognitive, v.Cognitive);
        if (v.Npath > t.Npath) { t.Npath = v.Npath; t.NpathLog2 = v.NpathLog2; t.NpathSaturated = v.NpathSaturated; }
        t.Nesting = Math.Max(t.Nesting, v.Nesting);
        t.Statements = Math.Max(t.Statements, v.Statements);
        if ((v.Parameters ?? 0) > (t.Parameters ?? 0)) t.Parameters = v.Parameters;
        if (lines) t.Lines = v.Lines;
        if (boolean) { t.BooleanOperatorsMax = v.BooleanOperatorsMax; t.BooleanOperatorsLine = v.BooleanOperatorsLine; }
        if (blocks) t.NestedBlocks = v.NestedBlocks;
        // Appended after the base view's, so the ordinals of the base view's empty-catch findings do not move.
        if (catches.Count > 0) t.EmptyCatches = [.. t.EmptyCatches ?? [], .. catches.OrderBy(e => e.Line)];
        if (literals.Count > 0) t.NumericLiterals = [.. (t.NumericLiterals ?? []).Concat(literals).OrderBy(e => e.Line)];
        t.Variant ??= label;
    }

    /// <summary>Bumpy road facts compare by the number of nested blocks, then their total depth.</summary>
    static long BlockWeight(List<NestedBlockFact>? blocks) => blocks is null ? 0 : blocks.Count * 10_000L + blocks.Sum(x => x.Depth);

    /// <summary>
    /// A variant's line kinds: the base kinds, classified again only inside branches whose taken state differs from the
    /// base (every other line is token for token the base's). Equal to a full <see cref="LineClassifier.Classify"/> of the
    /// variant tree (tested).
    /// </summary>
    public static LineKind[] ClassifyChanged(SyntaxTree tree, LineKind[] baseKinds, DirectiveMap map, bool[] baseTaken, bool[] taken)
    {
        var kinds = (LineKind[])baseKinds.Clone();
        var text = tree.GetText();
        var root = tree.GetRoot();
        int n = kinds.Length;
        var ranges = new List<(int A, int B)>();
        foreach (var b in map.Branches)
        {
            if (baseTaken[b.Index] == taken[b.Index]) continue;
            int a = b.Line + 1, e = b.EndLine - 1;
            if (e < a) continue;
            if (ranges.Count > 0 && ranges[^1].A <= a && e <= ranges[^1].B) continue;
            ranges.Add((a, e));
        }
        foreach (var (a, e) in ranges)
        {
            int a0 = Math.Clamp(a - 1, 0, n), e0 = Math.Clamp(e, a0, Math.Min(n, text.Lines.Count));
            if (e0 <= a0) continue;
            var span = TextSpan.FromBounds(text.Lines[a0].Start, text.Lines[e0 - 1].EndIncludingLineBreak);
            int len = e0 - a0;
            var code = new bool[len];
            var comment = new bool[len];
            var directive = new bool[len];
            var inactive = new bool[len];
            void Mark(bool[] flags, TextSpan s)
            {
                if (s.Length == 0) return;
                int x = text.Lines.GetLineFromPosition(s.Start).LineNumber, y = text.Lines.GetLineFromPosition(s.End - 1).LineNumber;
                for (int i = Math.Max(x, a0); i <= Math.Min(y, e0 - 1); i++) flags[i - a0] = true;
            }
            foreach (var t in root.DescendantTokens(span, descendIntoTrivia: false))
                if (!t.IsKind(SyntaxKind.EndOfFileToken) && t.Span.IntersectsWith(span)) Mark(code, t.Span);
            foreach (var tr in root.DescendantTrivia(span, descendIntoTrivia: false))
            {
                if (!tr.Span.IntersectsWith(span)) continue;
                switch (tr.Kind())
                {
                    case SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia
                        or SyntaxKind.SingleLineDocumentationCommentTrivia or SyntaxKind.MultiLineDocumentationCommentTrivia:
                        Mark(comment, tr.Span);
                        break;
                    case SyntaxKind.DisabledTextTrivia: Mark(inactive, tr.Span); break;
                    default: if (tr.IsDirective) Mark(directive, tr.Span); break;
                }
            }
            for (int i = 0; i < len; i++)
                kinds[a0 + i] = inactive[i] ? LineKind.Inactive : directive[i] ? LineKind.Directive
                    : code[i] ? (comment[i] ? LineKind.Mixed : LineKind.Code) : comment[i] ? LineKind.Comment : LineKind.Blank;
        }
        return kinds;
    }
}
