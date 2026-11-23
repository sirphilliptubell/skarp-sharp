using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SkarpSharp.Discovery;
using SkarpSharp.Metrics;
using SkarpSharp.Model;
using SkarpSharp.Syntax;

namespace SkarpSharp.Semantic;

/// <summary>What one measured compilation gave: its project row, its types and the types not evaluated.</summary>
public sealed class ProjectMeasurement
{
    public required SemanticProject Project { get; init; }
    public List<SemanticType> Types { get; } = [];
    public List<NotEvaluatedType> NotEvaluated { get; } = [];
}

/// <summary>
/// Measures the types of one compilation: per type (classes, structs, records, interfaces; partial parts merged;
/// nested types separate; enums and delegates not measured) the shared walk (coupling, ATFD, cohesion facts), Microsoft's
/// coupling and WMC. A type whose declaration holds an unresolved-symbol error, or whose walk meets an error type, is
/// "not evaluated" with the reason. Types are measured only when their first declaration is a file
/// the syntax tier kept (tests, exclude globs and generated files follow the same selection).
/// </summary>
public static class TypeMeasurement
{
    /// <summary>Diagnostics that mean a symbol could not be resolved (missing types, members, namespaces, references,
    /// or the generated half of a partial method).</summary>
    public static readonly IReadOnlySet<string> UnresolvedCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "CS0012", "CS0103", "CS0117", "CS0234", "CS0246", "CS0400", "CS0426", "CS0430", "CS0518", "CS1061", "CS1069", "CS7069", "CS8795",
    };

    /// <summary>The shared semantic models of a compiled project (generated trees: generator output, generated file names
    /// and headers).</summary>
    public static CompilationModels Models(CompiledProject compiled) => new(compiled.Compilation,
        t => compiled.GeneratedTrees.Contains(t) || GeneratedCode.IsGeneratedFileName(t.FilePath) || GeneratedCode.HasGeneratedHeader(t));

    public static ProjectMeasurement Measure(CompiledProject compiled, string root, Func<string, bool> keptFile, IReadOnlySet<string> solutionAssemblies,
        bool generators, CompilationModels? shared = null)
    {
        var comp = compiled.Compilation;
        var input = compiled.Input;
        // Semantic models are thread safe: diagnostics and types are processed in parallel, results kept in source order.
        var models = shared ?? Models(compiled);
        SemanticModel Model(SyntaxTree t) => models.Model(t);
        bool IsGenerated(SyntaxTree t) => models.IsGenerated(t);
        string Rel(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
        var row = new SemanticProject
        {
            Path = Rel(input.Record.Project), TargetFramework = input.Record.TargetFramework, AssemblyName = input.AssemblyName, Measured = true,
            Sources = input.SourcePaths.Count, GeneratedSources = input.Generated.Count,
        };
        var result = new ProjectMeasurement { Project = row };
        if (compiled.MissingSources > 0) input.Notes.Add($"{compiled.MissingSources} source files listed by MSBuild do not exist");
        if (input.ArgumentError is not null) input.Notes.Add("compiler arguments: " + input.ArgumentError);

        var measuredTrees = comp.SyntaxTrees.Where(t => !IsGenerated(t) && keptFile(Rel(t.FilePath))).ToList();
        // Errors are counted in the measured files (the files of excluded tests or generated code are never bound).
        var errors = new Diagnostic[measuredTrees.Count][];
        Parallel.For(0, measuredTrees.Count, i => errors[i] = models.Errors(measuredTrees[i]));
        var unresolved = new Dictionary<SyntaxTree, List<Diagnostic>>();
        for (int i = 0; i < measuredTrees.Count; i++)
        {
            row.Errors += errors[i].Length;
            var list = errors[i].Where(d => UnresolvedCodes.Contains(d.Id)).ToList();
            row.Unresolved += list.Count;
            if (list.Count > 0) unresolved[measuredTrees[i]] = list;
        }
        var global = comp.GetDeclarationDiagnostics().Where(d => d.Location == Location.None && d.Severity == DiagnosticSeverity.Error).ToList();
        row.Errors += global.Count;
        var globalUnresolved = global.FirstOrDefault(d => UnresolvedCodes.Contains(d.Id));

        var wmc = Wmc(measuredTrees);
        var walker = new TypeWalker(Model, IsGenerated, solutionAssemblies, comp);
        var microsoft = new MicrosoftCoupling(Model);
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var work = new List<(INamedTypeSymbol Type, List<TypeDeclarationSyntax> Parts, Diagnostic? Unresolved)>();
        foreach (var tree in measuredTrees)
        {
            var model = Model(tree);
            foreach (var decl in tree.GetRoot().DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax).OfType<TypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl) is not INamedTypeSymbol type || !seen.Add(type)) continue;
                var parts = type.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<TypeDeclarationSyntax>().Where(d => !IsGenerated(d.SyntaxTree))
                    .OrderBy(d => d.SyntaxTree.FilePath, StringComparer.Ordinal).ThenBy(d => d.SpanStart).ToList();
                var first = parts[0];
                if (!keptFile(Rel(first.SyntaxTree.FilePath))) continue;
                work.Add((type, parts, globalUnresolved ?? FirstUnresolved(parts, unresolved)));
            }
        }
        var context = new TypeContext(row.Path, walker, microsoft, wmc, Rel, input, generators);
        var measured = new object[work.Count];
        Parallel.For(0, work.Count, i => measured[i] = MeasureType(work[i].Type, work[i].Parts, work[i].Unresolved, context));
        foreach (var m in measured)
            if (m is SemanticType st) result.Types.Add(st); else result.NotEvaluated.Add((NotEvaluatedType)m);
        row.Types = result.Types.Count + result.NotEvaluated.Count;
        row.TypesNotEvaluated = result.NotEvaluated.Count;
        if (input.Notes.Count > 0) row.Notes = [.. input.Notes];
        return result;
    }

    /// <summary>One type: a <see cref="SemanticType"/>, or a <see cref="NotEvaluatedType"/> with the reason.</summary>
    sealed record TypeContext(string Project, TypeWalker Walker, MicrosoftCoupling Microsoft, Dictionary<TypeDeclarationSyntax, int> Wmc,
        Func<string, string> Rel, CompileInput Input, bool Generators);

    static object MeasureType(INamedTypeSymbol type, List<TypeDeclarationSyntax> parts, Diagnostic? unresolved, TypeContext c)
    {
        var (project, walker, microsoft, wmc, rel, input, generators) = c;
        var first = parts[0];
        var name = SymbolFacts.TypeName(type);
        var path = rel(first.SyntaxTree.FilePath);
        int line = first.SyntaxTree.GetLineSpan(first.Span).StartLinePosition.Line + 1;
        var facts = unresolved is null ? walker.Walk(type) : null;
        if (unresolved is not null || facts!.ErrorType is not null)
        {
            return new NotEvaluatedType
            {
                Name = name, Path = path, Line = line,
                Reason = generators ? "unresolved symbols" : "unresolved symbols (source generators not run: --no-generators)",
                Diagnostic = unresolved is null ? facts!.ErrorType : Short(unresolved),
            };
        }
        var ms = microsoft.Compute(type);
        var cohesion = facts.CohesionNotApplicable
            ? new CohesionValue(0, null, null, null, null, null, type.TypeKind == TypeKind.Interface ? "interface" : "static class")
            : Cohesion.Compute(facts.CohesionMembers);
        var foreign = facts.ForeignPerMember;
        return new SemanticType
        {
            Name = name, Kind = Kind(type), Path = path, Line = line, Project = project, TargetFramework = input.Record.TargetFramework,
            Key = SymbolFacts.Key(type),
            Cbo = facts.Coupled.Count(c => !c.Framework), CboAll = facts.Coupled.Count, CboInternal = facts.Coupled.Count(c => c.Internal),
            CboMicrosoft = ms.Count,
            Atfd = foreign.SelectMany(f => f.Where(kv => !kv.Value.Framework).Select(kv => kv.Key)).Distinct().Count(),
            AtfdAll = foreign.SelectMany(f => f.Keys).Distinct().Count(),
            AtfdMemberSum = foreign.Sum(f => f.Count(kv => !kv.Value.Framework)),
            Tcc = cohesion.Tcc, Lcc = cohesion.Lcc, CohesionMethods = cohesion.Methods, CohesionPairs = cohesion.Pairs,
            DirectPairs = cohesion.Direct, IndirectPairs = cohesion.Indirect, CohesionNote = cohesion.Note,
            Wmc = parts.Sum(p => wmc.GetValueOrDefault(p)),
            CoupledTypes = [.. facts.Coupled.Where(c => !c.Framework).Select(c => c.ShortName).Order(StringComparer.Ordinal)],
            ForeignData = foreign.SelectMany(f => f.Where(kv => !kv.Value.Framework).Select(kv => kv.Value.Display)).Distinct().Order(StringComparer.Ordinal).ToList() is { Count: > 0 } fd ? fd : null,
            CoupledKeys = [.. facts.Coupled.Select(c => c.Key)],
        };
    }

    static string Kind(INamedTypeSymbol t) => t.IsRecord ? (t.TypeKind == TypeKind.Struct ? "record struct" : "record")
        : t.TypeKind switch { TypeKind.Struct => "struct", TypeKind.Interface => "interface", _ => "class" };

    static string Short(Diagnostic d)
    {
        var m = d.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        return $"{d.Id}: {(m.Length > 160 ? m[..157] + "..." : m)}";
    }

    /// <summary>The first unresolved-symbol diagnostic inside the type's own declarations (nested types excluded).</summary>
    static Diagnostic? FirstUnresolved(List<TypeDeclarationSyntax> parts, Dictionary<SyntaxTree, List<Diagnostic>> unresolved)
    {
        foreach (var p in parts)
        {
            if (!unresolved.TryGetValue(p.SyntaxTree, out var list)) continue;
            var nested = p.Members.OfType<BaseTypeDeclarationSyntax>().Select(n => n.FullSpan).ToList();
            foreach (var d in list)
            {
                var span = d.Location.SourceSpan;
                if (p.FullSpan.Contains(span) && !nested.Any(n => n.Contains(span))) return d;
            }
        }
        return null;
    }

    /// <summary>WMC per type declaration: the cyclomatic complexity of its top-level member bodies (lambdas
    /// and local functions folded in), as the syntax tier's type summary sums them.</summary>
    static Dictionary<TypeDeclarationSyntax, int> Wmc(List<SyntaxTree> trees)
    {
        var map = new Dictionary<TypeDeclarationSyntax, int>();
        foreach (var tree in trees)
            foreach (var body in MemberCollector.Collect(tree, tree.FilePath))
            {
                if (body.ParentId is not null || body.Kind == MemberKind.TopLevel) continue;
                if (body.Declaration.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault() is not TypeDeclarationSyntax owner) continue;
                IReadOnlyList<SyntaxNode> roots = body.InitializerRoots.Count == 0 ? body.Roots : [.. body.InitializerRoots, .. body.Roots];
                map[owner] = map.GetValueOrDefault(owner) + Cyclomatic.Compute(roots);
            }
        return map;
    }

}
