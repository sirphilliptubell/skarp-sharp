using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SkarpSharp.Findings;
using SkarpSharp.Model;
using SkarpSharp.Semantic;

namespace SkarpSharp.Tests.Semantic;

/// <summary>Measures C# source in memory (no MSBuild): one compilation against the runtime's assemblies, measured by the
/// production <see cref="TypeMeasurement"/>, with afferent coupling and the semantic findings applied.</summary>
internal static class SemanticTestHost
{
    static readonly string Root = Path.Combine(Path.GetTempPath(), "skarp-semantic-unit");

    static readonly Lazy<MetadataReference[]> References = new(() =>
        [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
            .Select(p => MetadataReference.CreateFromFile(p))]);

    public static SemanticResult Measure(string source, bool generators = true, params string[] overrides)
    {
        var path = Path.Combine(Root, "Test.cs");
        var parse = new CSharpParseOptions(LanguageVersion.Latest);
        var tree = CSharpSyntaxTree.ParseText(source, parse, path);
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable);
        var comp = CSharpCompilation.Create("T", [tree], References.Value, options);
        var input = new CompileInput
        {
            Record = new RecordedProject
            {
                Project = Path.Combine(Root, "T.csproj"), TargetFramework = "net10.0", AssemblyName = "T", AssetsFile = "", RoslynTargetsPath = "", Arguments = [],
            },
            AssemblyName = "T", SourcePaths = [path], References = [], AnalyzerPaths = [], ParseOptions = parse, Options = options, Measure = true,
        };
        var m = TypeMeasurement.Measure(new CompiledProject(comp, new HashSet<SyntaxTree>(), input, 0), Root, _ => true, new HashSet<string> { "T" }, generators);
        var result = new SemanticResult { Projects = [m.Project], Types = m.Types, NotEvaluated = m.NotEvaluated };
        SemanticRunner.Afferent(result.Types);
        SemanticFindings.Apply(result, RuleSettings.Resolve("default", null, overrides), null);
        return result;
    }

    public static SemanticType Type(this SemanticResult r, string name) =>
        r.Types.SingleOrDefault(t => t.Name == name || t.Name.EndsWith("." + name, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"type {name} not measured (types: {string.Join(", ", r.Types.Select(t => t.Name))}; not evaluated: {string.Join(", ", r.NotEvaluated.Select(t => t.Name))})");
}
