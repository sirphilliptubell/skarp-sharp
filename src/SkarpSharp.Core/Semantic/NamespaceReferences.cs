using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SkarpSharp.Semantic;

/// <summary>
/// One aggregated namespace reference: a type of the source namespace (in the project's own assembly) uses a
/// type of the target namespace of a solution assembly. Types are the outermost types (nested types fold into them);
/// names are metadata names (<c>Cache`2</c>); the global namespace is the empty string.
/// </summary>
public readonly record struct NamespaceReference(string SourceNamespace, string SourceType, string TargetAssembly, string TargetNamespace, string TargetType);

/// <summary>The first place (path and line, in ordinal path order) of a namespace reference, and how many references
/// (distinct syntax positions) it stands for.</summary>
public readonly record struct NamespaceSite(string Path, int Line, int References)
{
    public static bool Before(string path, int line, NamespaceSite other)
    {
        int c = string.CompareOrdinal(path, other.Path);
        return c < 0 || (c == 0 && line < other.Line);
    }
}

/// <summary>Which trees a project gives as namespace sources (counted in <see cref="NamespaceFacts"/>).</summary>
public enum NamespaceSourceKind : byte
{
    Source,
    Generated,
    Excluded,
}

/// <summary>
/// The compact namespace facts of one project (strings only): the cross-node references of its source
/// files, the namespaces it declares, the types declared only in generated code, and the file counts.
/// </summary>
public sealed class NamespaceFacts
{
    public string Assembly { get; init; } = "";
    public Dictionary<NamespaceReference, NamespaceSite> References { get; } = [];
    /// <summary>Namespaces in which the assembly declares a type in source (generated code included).</summary>
    public HashSet<string> DeclaredNamespaces { get; } = new(StringComparer.Ordinal);
    /// <summary>Top-level types whose every declaration is generated code (<see cref="TypeKey"/>).</summary>
    public HashSet<string> GeneratedOnlyTypes { get; } = new(StringComparer.Ordinal);
    /// <summary>Kept, handwritten, non-test files walked as sources.</summary>
    public int SourceFiles { get; set; }
    /// <summary>Generated files, not sources.</summary>
    public int GeneratedFiles { get; set; }
    /// <summary>Test files and files the file selection excluded, not sources.</summary>
    public int ExcludedFiles { get; set; }
    /// <summary>References outside any type (assembly attributes), not counted.</summary>
    public int OutsideTypes { get; set; }

    public static string TypeKey(string ns, string type) => ns + "\u0001" + type;

    /// <summary>The declared namespaces and generated-only types of a compilation's own assembly.</summary>
    public static NamespaceFacts Declared(Compilation compilation, CompilationModels models)
    {
        var facts = new NamespaceFacts { Assembly = compilation.AssemblyName ?? "" };
        var stack = new Stack<INamespaceSymbol>();
        stack.Push(compilation.Assembly.GlobalNamespace);
        while (stack.Count > 0)
        {
            var ns = stack.Pop();
            foreach (var child in ns.GetNamespaceMembers()) stack.Push(child);
            var name = NamespaceUses.NamespaceName(ns);
            foreach (var t in ns.GetTypeMembers())
            {
                if (t.DeclaringSyntaxReferences.Length == 0) continue;
                facts.DeclaredNamespaces.Add(name);
                if (t.DeclaringSyntaxReferences.All(r => models.IsGenerated(r.SyntaxTree))) facts.GeneratedOnlyTypes.Add(TypeKey(name, t.MetadataName));
            }
        }
        return facts;
    }

    /// <summary>Merges one reference: the earliest site in path and line order, references summed.</summary>
    public void Add(NamespaceReference reference, NamespaceSite site)
    {
        if (!References.TryGetValue(reference, out var had))
        {
            References[reference] = site;
            return;
        }
        var first = NamespaceSite.Before(site.Path, site.Line, had) ? site : had;
        References[reference] = first with { References = had.References + site.References };
    }
}

/// <summary>
/// The namespace references of one syntax tree. The reference
/// walk tells it where it is (<see cref="At"/>) and each type definition it reaches (<see cref="Add"/>): the source is
/// the outermost type around the reference (top-level statements: <c>Program</c> in the global namespace), the target
/// the outermost type of the definition, kept when it is declared in a solution assembly and lies in another node
/// (assembly, namespace).
/// </summary>
public sealed class NamespaceUses
{
    readonly NamespaceSourceKind _kind;
    readonly string _assembly = "";
    readonly string _path = "";
    readonly SyntaxTree? _tree;
    readonly bool _topLevel;
    readonly Dictionary<NamespaceReference, (int Position, int Count)> _refs = [];
    /// <summary>References already counted at a syntax position (an attribute binds its type and its constructor: one reference).</summary>
    readonly HashSet<(NamespaceReference, int)> _sites = [];
    readonly Dictionary<INamedTypeSymbol, (string Namespace, string Name)> _names = new(SymbolEqualityComparer.Default);
    int _position;
    int _outside;

    NamespaceUses(NamespaceSourceKind kind) => _kind = kind;

    public NamespaceUses(string assembly, string path, SyntaxTree tree, CompilationUnitSyntax root)
    {
        _kind = NamespaceSourceKind.Source;
        _assembly = assembly;
        _path = path;
        _tree = tree;
        _topLevel = root.Members.Any(m => m is GlobalStatementSyntax);
    }

    /// <summary>A tree that is not a namespace source: only counted.</summary>
    public static NamespaceUses Skipped(NamespaceSourceKind kind) => new(kind);

    /// <summary>True for a source tree (references are recorded).</summary>
    public bool Active => _kind == NamespaceSourceKind.Source;

    public void At(SyntaxNode node) => _position = node.SpanStart;

    /// <summary>True when this tree holds the symbol's first declaration (path, then position); the position moves there.</summary>
    public bool AtFirstDeclaration(ISymbol symbol)
    {
        Location? first = null;
        foreach (var l in symbol.Locations)
        {
            if (!l.IsInSource) continue;
            if (first is null || Earlier(l, first)) first = l;
        }
        if (first is null || first.SourceTree != _tree) return false;
        _position = first.SourceSpan.Start;
        return true;
    }

    static bool Earlier(Location a, Location b)
    {
        int c = string.CompareOrdinal(a.SourceTree!.FilePath, b.SourceTree!.FilePath);
        return c < 0 || (c == 0 && a.SourceSpan.Start < b.SourceSpan.Start);
    }

    public static string NamespaceName(INamespaceSymbol? ns) => ns is null || ns.IsGlobalNamespace ? "" : ns.ToDisplayString();

    static INamedTypeSymbol Outermost(INamedTypeSymbol t)
    {
        while (t.ContainingType is { } outer) t = outer;
        return t.OriginalDefinition;
    }

    (string Namespace, string Name) Name(INamedTypeSymbol t)
    {
        if (!_names.TryGetValue(t, out var n)) _names[t] = n = (NamespaceName(t.ContainingNamespace), t.MetadataName);
        return n;
    }

    /// <summary>A use of a type definition from inside <paramref name="sourceType"/> (null outside any type).</summary>
    public void Add(INamedTypeSymbol definition, INamedTypeSymbol? sourceType, Func<ISymbol, bool> inSolution)
    {
        var target = Outermost(definition);
        if (target.ContainingAssembly is not { } assembly || !inSolution(target)) return;
        (string Namespace, string Name) source;
        if (sourceType is not null) source = Name(Outermost(sourceType));
        else if (_topLevel) source = ("", "Program");
        else
        {
            _outside++;
            return;
        }
        var t = Name(target);
        if (assembly.Name == _assembly && t.Namespace == source.Namespace) return;
        var key = new NamespaceReference(source.Namespace, source.Name, assembly.Name, t.Namespace, t.Name);
        int added = _sites.Add((key, _position)) ? 1 : 0;
        _refs[key] = _refs.TryGetValue(key, out var had) ? (Math.Min(had.Position, _position), had.Count + added) : (_position, 1);
    }

    /// <summary>Adds this tree's counts and references to the project's facts (under the project's lock).</summary>
    public void AddTo(NamespaceFacts facts)
    {
        switch (_kind)
        {
            case NamespaceSourceKind.Generated: facts.GeneratedFiles++; return;
            case NamespaceSourceKind.Excluded: facts.ExcludedFiles++; return;
        }
        facts.SourceFiles++;
        facts.OutsideTypes += _outside;
        foreach (var (key, (position, count)) in _refs)
            facts.Add(key, new NamespaceSite(_path, _tree!.GetLineSpan(new TextSpan(position, 0)).StartLinePosition.Line + 1, count));
    }
}
