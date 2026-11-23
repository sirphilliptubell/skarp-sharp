using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using SkarpSharp.Findings;
using SkarpSharp.Model;
using SkarpSharp.Semantic;

namespace SkarpSharp.Tests.Semantic;

/// <summary>One in-memory project of an unused-code test: its sources, whether it is a test project, the projects it
/// references, its target frameworks (each compiled with the SDK's framework symbols) and the ones analyzed.</summary>
internal sealed record TestProject(string Name, params string[] Sources)
{
    public bool Test { get; init; }
    public string[] References { get; init; } = [];
    public string[] Frameworks { get; init; } = ["net10.0"];
    /// <summary>Frameworks compiled and walked (null: all of <see cref="Frameworks"/>).</summary>
    public string[]? Analyzed { get; init; }
    public OutputKind Kind { get; init; } = OutputKind.DynamicallyLinkedLibrary;
    /// <summary>File names of the sources that are generated (walked, never reported).</summary>
    public string[] GeneratedFiles { get; init; } = [];
    /// <summary>Paths of the sources relative to the test root (null: <c>Name/File{i}.cs</c>); equal paths in two projects
    /// make a linked (shared) source file.</summary>
    public string[]? Paths { get; init; }
    /// <summary>Analyzer references (paths); with generators off the project is not evaluated.</summary>
    public string[] Analyzers { get; init; } = [];
    /// <summary>Source generators ran.</summary>
    public bool Generators { get; init; } = true;
    /// <summary>Sources compiled only for some frameworks (file name <c>File{i}.cs</c> to frameworks), as a conditional
    /// <c>&lt;Compile Include&gt;</c> gives; other sources are compiled for every framework.</summary>
    public IReadOnlyDictionary<string, string[]> FileFrameworks { get; init; } = new Dictionary<string, string[]>();

    /// <summary>The indexes of the sources compiled for a framework.</summary>
    public int[] SourcesFor(string tfm) =>
        [.. Enumerable.Range(0, Sources.Length).Where(i => !FileFrameworks.TryGetValue($"File{i}.cs", out var only) || only.Contains(tfm))];
}

/// <summary>What an unused-code run gave: the findings (place keys per rule), the analysis and the facts.</summary>
internal sealed class UnusedRun
{
    public required UnusedCodeAnalysis Analysis { get; init; }
    public required List<Finding> Findings { get; init; }
    public required List<ProjectFacts> Facts { get; init; }

    public List<string> Unused => [.. Findings.Where(f => f.Rule is RuleIds.UnusedMember or RuleIds.UnusedType).Select(f => f.PlaceKey!).Order(StringComparer.Ordinal)];
    public List<string> Unread => [.. Findings.Where(f => f.Rule == RuleIds.UnreadMember).Select(f => f.PlaceKey!).Order(StringComparer.Ordinal)];
    public List<string> TestOnly => [.. Findings.Where(f => f.Rule == RuleIds.UsedOnlyByTests).Select(f => f.PlaceKey!).Order(StringComparer.Ordinal)];

    /// <summary>The classification of a candidate by place key: <c>unused</c>, <c>used</c>, <c>used-only-by-tests</c>,
    /// <c>rooted: reason</c>, <c>not evaluated: reason</c> or <c>inside an unused type</c>.</summary>
    public string State(string place) =>
        Analysis.Classified.FirstOrDefault(c => c.Candidate.PlaceKey == place) is { } c ? c.Text
        : throw new InvalidOperationException($"{place} is not a candidate (candidates: {string.Join(", ", Analysis.Classified.Select(x => x.Candidate.PlaceKey))})");
}

/// <summary>
/// Compiles in-memory projects in order (references bind to metadata-only images, as the pipeline does), runs the
/// production reference walk on each compilation and the unused-code analysis over all of them.
/// </summary>
internal static class UnusedTestHost
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "skarp-unused-unit");

    static readonly Lazy<MetadataReference[]> Framework = new(() =>
        [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
            .Select(p => MetadataReference.CreateFromFile(p))]);

    /// <summary>The SDK's preprocessor symbols of a target framework (net8.0: NET, NET8_0, NET5_0_OR_GREATER ... NET8_0_OR_GREATER).</summary>
    public static string[] Symbols(string tfm)
    {
        var version = int.Parse(tfm[3..tfm.IndexOf('.')], System.Globalization.CultureInfo.InvariantCulture);
        return ["NET", $"NET{version}_0", .. Enumerable.Range(5, version - 4).Select(v => $"NET{v}_0_OR_GREATER")];
    }

    public static UnusedRun Run(string source, params string[] allow) => Run([new TestProject("T", source)], allow);

    public static UnusedRun Run(TestProject[] projects, params string[] allow) => Run(projects, allow, "default");

    public static UnusedRun Run(TestProject[] projects, string[] allow, string preset, params string[] overrides)
    {
        var images = new Dictionary<string, MetadataReference>(StringComparer.Ordinal);
        var facts = new List<ProjectFacts>();
        var assemblies = new HashSet<string>(projects.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var frameworks = projects.ToDictionary(p => ProjectPath(p), p => (IReadOnlyList<string>)p.Frameworks, StringComparer.OrdinalIgnoreCase);
        // Every compilation of every framework (analyzed or not), as the design-time build records them.
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var recorded = new List<(string Project, string TargetFramework, IReadOnlyList<string> Sources, bool Analyzed)>();
        foreach (var p in projects)
            foreach (var tfm in p.Frameworks)
            {
                var sources = p.SourcesFor(tfm).Select(i => SourcePath(p, i)).ToList();
                foreach (var i in p.SourcesFor(tfm)) texts[SourcePath(p, i)] = p.Sources[i];
                recorded.Add((ProjectPath(p), tfm, sources, (p.Analyzed ?? p.Frameworks).Contains(tfm)));
            }
        foreach (var p in projects)
        {
            foreach (var tfm in p.Analyzed ?? p.Frameworks)
            {
                var parse = new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.Parse, preprocessorSymbols: Symbols(tfm));
                var included = p.SourcesFor(tfm);
                var trees = included.Select(i => CSharpSyntaxTree.ParseText(p.Sources[i], parse, SourcePath(p, i))).ToList();
                var generated = new HashSet<SyntaxTree>(trees.Where((t, k) => p.GeneratedFiles.Contains($"File{included[k]}.cs")));
                var options = new CSharpCompilationOptions(p.Kind, nullableContextOptions: NullableContextOptions.Enable, allowUnsafe: true);
                var refs = Framework.Value.Concat(p.References.Select(r => images[r])).ToList();
                var comp = CSharpCompilation.Create(p.Name, trees, refs, options);
                var input = new CompileInput
                {
                    Record = new RecordedProject
                    {
                        Project = ProjectPath(p), TargetFramework = tfm, AssemblyName = p.Name, AssetsFile = "", RoslynTargetsPath = "", Arguments = [],
                    },
                    AssemblyName = p.Name, SourcePaths = [.. trees.Select(t => t.FilePath)], References = [], AnalyzerPaths = p.Analyzers, ParseOptions = parse, Options = options,
                    Measure = !p.Test, Walk = true,
                };
                var compiled = new CompiledProject(comp, generated, input, 0);
                facts.Add(ReferenceIndex.Extract(compiled, new ReferenceOptions
                {
                    Root = Root, KeptFile = _ => !p.Test, SolutionAssemblies = assemblies, IsTestProject = p.Test, Generators = p.Generators,
                }));
                using var ms = new MemoryStream();
                var emit = comp.Emit(ms, options: new EmitOptions(metadataOnly: true));
                if (emit.Success) images[p.Name] = MetadataReference.CreateFromImage(ms.ToArray());
                else images[p.Name] = comp.ToMetadataReference();
            }
        }
        var analysis = UnusedCode.Analyze(facts, new UnusedCodeOptions
        {
            ProjectFrameworks = frameworks, Allow = allow, Root = Root,
            UnanalyzedOnlyFiles = UnusedCode.FilesOnlyInUnanalyzedTargets(recorded), ReadText = path => texts.GetValueOrDefault(path),
            UnanalyzedCompiledFiles = UnusedCode.FilesInUnanalyzedTargets(recorded, Root),
        });
        var settings = RuleSettings.Resolve(preset, null, overrides);
        var findings = UnusedCode.Findings(analysis.Verdicts, settings);
        FindingEngine.AssignIds(findings);
        return new UnusedRun { Analysis = analysis, Findings = findings, Facts = facts };
    }

    static string ProjectPath(TestProject p) => Path.Combine(Root, p.Name, p.Name + ".csproj");

    static string SourcePath(TestProject p, int i) => Path.Combine(Root, p.Paths?[i] ?? Path.Combine(p.Name, $"File{i}.cs"));

    /// <summary>The ids recorded as used from code (not tests) by the walk of a single-project compilation.</summary>
    public static ImmutableHashSet<string> UsedIds(this UnusedRun run) => [.. run.Facts.SelectMany(f => f.Edges).Where(e => e.Source != ReferenceEdge.Test).Select(e => e.Target)];
}
