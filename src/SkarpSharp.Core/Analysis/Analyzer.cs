using System.Diagnostics;
using SkarpSharp.Discovery;
using SkarpSharp.Metrics;
using SkarpSharp.Model;
using SkarpSharp.Syntax;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SkarpSharp.Analysis;

public sealed class AnalyzerOptions
{
    public bool IncludeGenerated { get; init; }
    public int Parallelism { get; init; } = Environment.ProcessorCount;
    /// <summary>Analyze test files too (excluded by default).</summary>
    public bool IncludeTests { get; init; }
    /// <summary>Globs that bring back files the automatic test detection left out (<c>--include</c>).</summary>
    public IReadOnlyList<string> Include { get; init; } = [];
    /// <summary>Globs of files to leave out (<c>--exclude</c>); they win over <see cref="Include"/>.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];
    /// <summary>Collect clone tokens (duplicate-code) and method units (duplicate-code-near).</summary>
    public Clones.CloneMode Clones { get; init; }
    /// <summary>Compute the line facts of the coverage mapping (only when a coverage report is given).</summary>
    public bool CoverageLines { get; init; }
    /// <summary>Parse each file with <c>#if</c> in its planned symbol variants; null: the base view only.</summary>
    public VariantSettings? Variants { get; init; } = VariantSettings.Default;
}

/// <summary>
/// Pipeline: discover, parse in parallel, collect members, compute metrics, drop the
/// tree, aggregate. Results are ordered by file path so output does not depend on scheduling.
/// </summary>
public static class Analyzer
{
    /// <summary>Latest language, no preprocessor symbols, no documentation-comment parsing.</summary>
    public static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest, DocumentationMode.None, SourceCodeKind.Regular);

    /// <summary>Output schema version (finding ids and baseline states, member keys, run settings, findings, test exclusion, rule facts).</summary>
    public const int SchemaVersion = 1;

    /// <summary>The tool version from the build (the Git tag through MinVer, without the commit hash), e.g. <c>0.1.0</c>.</summary>
    public static string ToolVersion { get; } = ReadToolVersion();

    static string ReadToolVersion()
    {
        var assembly = typeof(Analyzer).Assembly;
        var info = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        var plus = info?.IndexOf('+') ?? -1;
        return info is { Length: > 0 } ? (plus >= 0 ? info[..plus] : info) : assembly.GetName().Version?.ToString() ?? "";
    }

    public static AnalysisResult AnalyzeDirectory(string root, AnalyzerOptions options)
    {
        var sw = Stopwatch.StartNew();
        root = Path.GetFullPath(root);
        List<Glob> excludeGlobs = [.. options.Exclude.Select(g => new Glob(g))];
        var classifier = new TestDetection.Classifier(root, options.IncludeTests, [.. options.Include.Select(g => new Glob(g))], excludeGlobs);
        // The same walk finds nested settings files (not applied; the run notes them), outside excluded folders.
        var nested = new List<string>();
        var paths = FileDiscovery.Find(root, nested).Where(p => !classifier.ExcludeBeforeParse(p)).ToList();
        nested.RemoveAll(n => excludeGlobs.Any(g => g.Matches(n)));
        var files = new FileResult[paths.Count];
        var variants = options.Variants is { } vs ? VariantOptions.ForRun(root, vs) : null;
        Parallel.For(0, paths.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Parallelism) }, i =>
        {
            var rel = paths[i];
            try
            {
                var text = SourceText.From(File.ReadAllText(Path.Combine(root, rel)));
                files[i] = AnalyzeText(text, rel, options.Clones, options.CoverageLines, variants);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                files[i] = new FileResult { Path = rel, Error = ex.Message };
            }
        });

        var kept = classifier.ExcludeParsed(files);
        var excluded = classifier.SortedExcluded();
        var result = new AnalysisResult
        {
            SchemaVersion = SchemaVersion,
            Files = kept,
            Excluded = excluded.Count > 0 ? excluded : null,
            NestedSettingsFiles = nested.Count > 0 ? nested : null,
            Run = new RunInfo
            {
                ToolVersion = ToolVersion,
                Root = root,
                ParseOptions = $"LanguageVersion={ParseOptions.LanguageVersion}; DocumentationMode={ParseOptions.DocumentationMode}; PreprocessorSymbols=(none)",
                IncludeGenerated = options.IncludeGenerated,
                Parallelism = options.Parallelism,
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
                    + (System.Runtime.JitInfo.GetCompiledMethodCount() > 0 ? " (JIT)" : " (AOT)"),
                IncludeTests = options.IncludeTests,
                IncludeGlobs = options.Include.Count > 0 ? [.. options.Include] : null,
                ExcludeGlobs = options.Exclude.Count > 0 ? [.. options.Exclude] : null,
            },
        };
        if (options.Variants is { } settings && Summarize(kept, settings) is { } runVariants)
        {
            result.Run.Variants = runVariants;
            result.Run.ParseOptions += $"; variants: {(settings.Symbols.Count > 0 ? "symbols setting" : "covering set")}, cap {settings.Cap} per file";
        }
        result.Repository.TestFiles = excluded.Count(e => e.Kind == "test");
        result.Repository.ExcludedFiles = excluded.Count(e => e.Kind == "exclude");
        Aggregation.Fill(result, options.IncludeGenerated);
        result.Run.DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 3);
        result.Run.PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64;
        return result;
    }

    /// <summary>The run's variant summary, or null when no file has an <c>#if</c> (the output then stays as before).</summary>
    public static RunVariants? Summarize(IEnumerable<FileResult> files, VariantSettings settings)
    {
        var withConditionals = files.Where(f => f.Symbols is not null).ToList();
        if (withConditionals.Count == 0) return null;
        return new RunVariants
        {
            Mode = settings.Mode, Cap = settings.Cap, CheckCap = settings.CheckCap,
            Symbols = settings.Symbols.Count > 0 ? [.. settings.Symbols] : null,
            FilesWithConditionals = withConditionals.Count,
            FilesWithVariants = withConditionals.Count(f => f.Variants?.Any(v => v.Discarded != true) == true),
            Variants = withConditionals.Sum(f => f.Variants?.Count(v => v.Discarded != true) ?? 0),
            DiscardedVariants = withConditionals.Sum(f => f.Variants?.Count(v => v.Discarded == true) ?? 0),
            CappedFiles = [.. withConditionals.Where(f => f.VariantsCapped == true).Select(f => f.Path)],
            UnreachableBranches = withConditionals.Sum(f => f.UnreachableBranches?.Count ?? 0),
            UnplannedBranches = withConditionals.Sum(f => f.UnplannedBranches ?? 0),
            FilteredSymbols = withConditionals.Sum(f => f.FilteredSymbols?.Count ?? 0),
        };
    }

    /// <summary>
    /// Test detection and --exclude globs without metrics (for <c>skarp history</c>): the files under <paramref name="root"/>
    /// that an analysis with the same options would leave out. Files outside test projects are parsed for their usings.
    /// </summary>
    public static List<ExcludedFile> FindExcluded(string root, AnalyzerOptions options)
    {
        root = Path.GetFullPath(root);
        var classifier = new TestDetection.Classifier(root, options.IncludeTests,
            [.. options.Include.Select(g => new Glob(g))], [.. options.Exclude.Select(g => new Glob(g))]);
        var paths = FileDiscovery.Find(root).Where(p => !classifier.ExcludeBeforeParse(p)).ToList();
        if (!options.IncludeTests)
        {
            var files = new FileResult[paths.Count];
            Parallel.For(0, paths.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Parallelism) }, i =>
            {
                files[i] = new FileResult { Path = paths[i] };
                try
                {
                    var tree = CSharpSyntaxTree.ParseText(SourceText.From(File.ReadAllText(Path.Combine(root, paths[i]))), ParseOptions, paths[i]);
                    (files[i].TestUsing, files[i].TestUsingIsGlobal) = TestDetection.FindTestUsing(tree.GetCompilationUnitRoot());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
            });
            classifier.ExcludeParsed(files);
        }
        return classifier.SortedExcluded();
    }

    public static FileResult AnalyzeSource(string source, string path = "Test.cs") => AnalyzeText(SourceText.From(source), path);

    /// <param name="variants">Plan, parse and merge the file's preprocessor symbol variants; null: base view only.</param>
    public static FileResult AnalyzeText(SourceText text, string relativePath, Clones.CloneMode clones = Clones.CloneMode.None, bool coverageLines = false,
        VariantOptions? variants = null)
    {
        var tree = CSharpSyntaxTree.ParseText(text, ParseOptions, relativePath);
        var lineKinds = LineClassifier.Classify(tree);
        var file = new FileResult
        {
            Path = relativePath,
            Generated = GeneratedCode.IsGeneratedFileName(relativePath) || GeneratedCode.HasGeneratedHeader(tree),
            Lines = Dto(LineCounts.From(lineKinds)),
        };

        var bodies = MemberCollector.Collect(tree, relativePath);
        foreach (var body in bodies)
            file.Members.Add(Measure(body, lineKinds));

        var root = tree.GetCompilationUnitRoot();
        foreach (var type in root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax).OfType<BaseTypeDeclarationSyntax>())
            file.Types.Add(Count(type, lineKinds));
        file.TopLevelTypes = root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .Count(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);
        (file.TestUsing, file.TestUsingIsGlobal) = TestDetection.FindTestUsing(root);
        // Suppression attributes (every run) and the clone token stream (generated files are never matched).
        file.Suppressions = SuppressionScan.Scan(root, text);
        if (clones != Clones.CloneMode.None && !file.Generated)
            file.CloneData = Clones.CloneTokens.Collect(tree, text, clones == Clones.CloneMode.TokensAndUnits);
        if (coverageLines) file.CoverageLines = Coverage.CoverageLineScan.Compute(tree, lineKinds);
        // Files without conditional directives return before any variant work (their output stays as before).
        if (variants is not null && root.ContainsDirectives)
            VariantMerge.Apply(new VariantMerge.BaseParse(file, text, tree, lineKinds, bodies, relativePath, coverageLines), variants);
        return file;
    }

    internal static MemberResult Measure(MemberBody body, LineKind[] lineKinds)
    {
        var npath = NPath.Compute(body.Roots, body.IsExpressionBody);
        int start = Math.Clamp(body.StartLine - 1, 0, lineKinds.Length), end = Math.Clamp(body.EndLine, start, lineKinds.Length);
        // Cyclomatic complexity also counts code of the member outside its body (constructor initializer arguments).
        IReadOnlyList<SyntaxNode> ccRoots = body.InitializerRoots.Count == 0 ? body.Roots : [.. body.InitializerRoots, .. body.Roots];
        var result = new MemberResult
        {
            Id = body.Id,
            Key = body.Key,
            Name = body.Name,
            Kind = body.Kind.ToString(),
            Parent = body.ParentId,
            Type = body.TypeName,
            StartLine = body.StartLine,
            EndLine = body.EndLine,
            Cyclomatic = Cyclomatic.Compute(ccRoots),
            CyclomaticMicrosoft = Cyclomatic.Compute(ccRoots, CyclomaticPreset.Microsoft),
            Cognitive = Cognitive.Compute(body.Roots, body.RecursionName),
            Npath = npath,
            NpathLog2 = npath <= 1 ? 0 : 63 - (int)long.LeadingZeroCount(npath),
            NpathSaturated = npath == long.MaxValue,
            Nesting = NestingDepth.Compute(body.Roots),
            Statements = Statements.Compute(body.Roots, body.IsExpressionBody),
            Lines = Dto(LineCounts.From(lineKinds.AsSpan(start, end - start))),
        };
        RuleFacts.Fill(body, result);
        return result;
    }

    internal static TypeCounts Count(BaseTypeDeclarationSyntax type, LineKind[] lineKinds)
    {
        var span = type.SyntaxTree.GetLineSpan(type.Span);
        int startLine = span.StartLinePosition.Line + 1, endLine = span.EndLinePosition.Line + 1;
        int start = Math.Clamp(startLine - 1, 0, lineKinds.Length), end = Math.Clamp(endLine, start, lineKinds.Length);
        var counts = new TypeCounts
        {
            Name = type is TypeDeclarationSyntax t ? MemberCollector.TypeName(t) : type.Identifier.ValueText,
            Kind = type switch { TypeDeclarationSyntax td0 => td0.Keyword.ValueText, EnumDeclarationSyntax => "enum", _ => "" },
            Partial = type.Modifiers.Any(SyntaxKind.PartialKeyword),
            Static = type.Modifiers.Any(SyntaxKind.StaticKeyword),
            StartLine = startLine,
            EndLine = endLine,
            SourceLines = LineCounts.From(lineKinds.AsSpan(start, end - start)).Source,
            PrimaryConstructorParameters = type is TypeDeclarationSyntax { ParameterList: { } pl } ? RuleFacts.Count(pl) : null,
        };
        if (type is not TypeDeclarationSyntax td) return counts;
        foreach (var m in td.Members)
        {
            switch (m)
            {
                case FieldDeclarationSyntax f: counts.Fields += f.Declaration.Variables.Count; break;
                case PropertyDeclarationSyntax or IndexerDeclarationSyntax: counts.Properties++; break;
                case MethodDeclarationSyntax or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax: counts.Methods++; break;
                case ConstructorDeclarationSyntax or DestructorDeclarationSyntax: counts.Constructors++; break;
                case EventDeclarationSyntax: counts.Events++; break;
                case EventFieldDeclarationSyntax ef: counts.Events += ef.Declaration.Variables.Count; break;
                case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax: counts.NestedTypes++; break;
            }
            if (m.Modifiers.Any(SyntaxKind.PublicKeyword) || td is InterfaceDeclarationSyntax) counts.PublicMembers++;
        }
        return counts;
    }

    internal static LineCountsDto Dto(LineCounts c) => new()
    {
        Physical = c.Physical, Blank = c.Blank, Comment = c.Comment, Mixed = c.Mixed,
        Source = c.Source, Directive = c.Directive, Inactive = c.Inactive,
    };
}
