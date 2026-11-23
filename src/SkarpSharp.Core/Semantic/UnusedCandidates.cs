using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static SkarpSharp.Semantic.CandidateNames;

namespace SkarpSharp.Semantic;

/// <summary>
/// The candidates of the unused-code rules in one measured compilation: private and internal
/// types, and private and internal methods, constructors with parameters, fields, constants, properties and events,
/// declared in files the syntax tier kept (generated files and test projects excluded). Each carries its symbol-level
/// root (<see cref="UnusedRoots"/>) and, when its type has unresolved symbols or invalid code, why it is not evaluated.
/// </summary>
internal static class CandidateCollector
{
    /// <summary>Longest compiler message quoted in a not-evaluated reason.</summary>
    const int MaxMessageLength = 100;

    /// <summary>The candidates of the compilation's kept, non-generated files, in declaration order (file, position).</summary>
    public static List<UnusedCandidate> Collect(CompiledProject compiled, CompilationModels models, ReferenceOptions options,
        Dictionary<INamedTypeSymbol, string> walkNotEvaluated)
    {
        string Rel(string path) => Path.GetRelativePath(options.Root, path).Replace('\\', '/');
        var comp = compiled.Compilation;
        var trees = comp.SyntaxTrees.Where(t => !models.IsGenerated(t) && options.KeptFile(Rel(t.FilePath))).ToList();
        var notEvaluated = new Dictionary<INamedTypeSymbol, string>(walkNotEvaluated, SymbolEqualityComparer.Default);
        AddUnresolved(trees, models, notEvaluated);
        var candidates = new List<UnusedCandidate>();
        var context = new Context(comp, models, [.. trees], Rel, notEvaluated, candidates);
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var tree in trees)
            foreach (var type in DeclaredTypes(tree, models.Model(tree)))
                if (seen.Add(type)) AddType(type, context);
        return candidates;
    }

    sealed record Context(Compilation Compilation, CompilationModels Models, HashSet<SyntaxTree> Kept, Func<string, string> Rel,
        Dictionary<INamedTypeSymbol, string> NotEvaluated, List<UnusedCandidate> Candidates);

    /// <summary>A type whose members are collected: its place name, the ids of its containing types (innermost first),
    /// the same with the type itself first (for its members), why it is not evaluated, and its polyfill root.</summary>
    sealed record Owner(INamedTypeSymbol Type, string TypeName, List<string> Containers, List<string> MemberContainers, string? NotEvaluated, string? Polyfill);

    /// <summary>Types with unresolved symbols (compiler errors of the unresolved kind inside their declaration) are not evaluated.</summary>
    static void AddUnresolved(List<SyntaxTree> trees, CompilationModels models, Dictionary<INamedTypeSymbol, string> notEvaluated)
    {
        var errors = new Diagnostic[trees.Count][];
        Parallel.For(0, trees.Count, i => errors[i] = models.Errors(trees[i]));
        for (int i = 0; i < trees.Count; i++)
            foreach (var d in errors[i])
                if (UnresolvedIn(trees[i], d, models) is { } t)
                    notEvaluated.TryAdd(t, $"unresolved symbols ({d.Id}: {Shorten(d.GetMessage(System.Globalization.CultureInfo.InvariantCulture), MaxMessageLength)})");
    }

    /// <summary>The type declaration an unresolved-symbol error lies in, or null.</summary>
    static INamedTypeSymbol? UnresolvedIn(SyntaxTree tree, Diagnostic d, CompilationModels models)
    {
        if (!TypeMeasurement.UnresolvedCodes.Contains(d.Id)) return null;
        var node = tree.GetRoot().FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true);
        var decl = node.AncestorsAndSelf().FirstOrDefault(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);
        return decl is null ? null : models.Model(tree).GetDeclaredSymbol(decl) as INamedTypeSymbol;
    }

    /// <summary>The types declared in a tree (namespaces and types descended), in source order.</summary>
    static IEnumerable<INamedTypeSymbol> DeclaredTypes(SyntaxTree tree, SemanticModel model)
    {
        foreach (var decl in tree.GetRoot().DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax))
            if (decl is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax && model.GetDeclaredSymbol(decl) is INamedTypeSymbol type)
                yield return type;
    }

    static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";

    /// <summary>The first declaration in a kept, non-generated file (by path, then position), or null.</summary>
    static SyntaxNode? FirstKept(ISymbol symbol, Context c) =>
        symbol.DeclaringSyntaxReferences.Where(r => c.Kept.Contains(r.SyntaxTree)).Select(r => r.GetSyntax())
            .OrderBy(n => n.SyntaxTree.FilePath, StringComparer.Ordinal).ThenBy(n => n.SpanStart).FirstOrDefault();

    static int Line(SyntaxNode node) => node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Line + 1;

    static void AddType(INamedTypeSymbol type, Context c)
    {
        if (!IsSourceType(type) || FirstKept(type, c) is not { } first) return;
        var owner = OwnerOf(type, c);
        AddTypeCandidate(owner, first, c);
        if (type.TypeKind is TypeKind.Interface or TypeKind.Enum or TypeKind.Delegate) return;
        var keys = PlaceKeys(type, owner.TypeName);
        foreach (var m in type.GetMembers())
            if (MemberCandidate(m, owner, keys, c) is { } candidate) c.Candidates.Add(candidate);
    }

    /// <summary>False for implicitly declared types and types (or containing types) marked as generated code.</summary>
    static bool IsSourceType(INamedTypeSymbol type)
    {
        if (type.IsImplicitlyDeclared || type.IsImplicitClass || type.IsScriptClass) return false;
        for (var t = type; t is not null; t = t.ContainingType)
            if (UnusedRoots.IsGeneratedByAttribute(t)) return false;
        return true;
    }

    static Owner OwnerOf(INamedTypeSymbol type, Context c)
    {
        var containers = new List<string>();
        for (var t = type.ContainingType; t is not null; t = t.ContainingType)
            if (SymbolIds.Of(t) is { } cid) containers.Add(cid);
        var memberContainers = new List<string>(containers.Count + 1);
        if (SymbolIds.Of(type) is { } ownId) memberContainers.Add(ownId);
        memberContainers.AddRange(containers);
        c.NotEvaluated.TryGetValue(type, out var notEvaluated);
        return new Owner(type, SymbolFacts.TypeName(type), containers, memberContainers, notEvaluated, Polyfill(type));
    }

    /// <summary>
    /// Types in the System namespaces are shims of .NET types for older targets: the compiler and libraries find some
    /// of them by name (IsExternalInit, FormattableStringFactory, nullable attributes), so none is reported.
    /// </summary>
    static string? Polyfill(INamedTypeSymbol type)
    {
        var ns = SymbolFacts.Namespace(type);
        return ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) ? $"polyfill of a .NET type (namespace {ns})" : null;
    }

    static void AddTypeCandidate(Owner owner, SyntaxNode first, Context c)
    {
        var type = owner.Type;
        if (Access(type) is not { } access || SymbolIds.Of(type) is not { } id) return;
        c.Candidates.Add(new UnusedCandidate
        {
            Id = id, Kind = Kind(type), Accessibility = access, Name = type.Name, Display = SymbolFacts.ShortName(type), FullName = FullName(type, null),
            ExtensionNames = [.. type.GetMembers().OfType<IMethodSymbol>().Where(m => m.IsExtensionMethod).Select(m => m.Name).Distinct(StringComparer.Ordinal)],
            PlaceKey = owner.TypeName, Path = c.Rel(first.SyntaxTree.FilePath), Line = Line(first), IsType = true, Containers = owner.Containers,
            FriendVisible = access == "internal" && Visible(type.ContainingType), Root = TypeRoot(type, c.Compilation) ?? owner.Polyfill, NotEvaluated = owner.NotEvaluated,
        });
    }

    /// <summary>The candidate of a member, or null when the member is not one (kind, accessibility, generated, no place).</summary>
    static UnusedCandidate? MemberCandidate(ISymbol m, Owner owner, Dictionary<ISymbol, string> keys, Context c)
    {
        if (m is INamedTypeSymbol || m.IsImplicitlyDeclared) return null;
        if (MemberKind(m, owner.Type) is not { } kind || Access(m) is not { } access) return null;
        if (FirstKept(m, c) is not { } decl || SymbolIds.Of(m) is not { } id) return null;
        if (!keys.TryGetValue(m, out var key) || UnusedRoots.IsGeneratedByAttribute(m)) return null;
        var type = owner.Type;
        var (name, display, fullName) = MemberNames(m, type);
        var root = UnusedRoots.MemberRoot(m, c.Compilation);
        var unread = UnreadRule.Describe(m, c.Models);
        return new UnusedCandidate
        {
            Id = id, Kind = kind, Accessibility = access, Name = name, Display = display,
            FullName = fullName, PlaceKey = key, Path = c.Rel(decl.SyntaxTree.FilePath), Line = Line(decl),
            Containers = owner.MemberContainers, FriendVisible = access == "internal" && Visible(type), StructField = m is IFieldSymbol { IsStatic: false } && type.TypeKind == TypeKind.Struct,
            Extension = m is IMethodSymbol { IsExtensionMethod: true },
            ValueMember = unread.ValueMember, Initialized = unread.Initialized, UnreadExclusion = unread.Exclusion,
            TypeBound = UnusedRoots.IsTypeBound(m, c.Compilation, root),
            Root = root ?? owner.Polyfill, NotEvaluated = owner.NotEvaluated,
        };
    }

    /// <summary>True when friends can see into the type chain (no private containing type).</summary>
    static bool Visible(INamedTypeSymbol? type)
    {
        for (var t = type; t is not null; t = t.ContainingType)
            if (HiddenFromFriends(t)) return false;
        return true;
    }

    static bool HiddenFromFriends(INamedTypeSymbol t) => t.DeclaredAccessibility is Accessibility.Private or Accessibility.ProtectedAndInternal || t.IsFileLocal;

    /// <summary>The root of a type candidate (null: none).</summary>
    static string? TypeRoot(INamedTypeSymbol type, Compilation compilation) =>
        AttributeTypeRoot(type)
        ?? (UnusedRoots.FrameworkBase(type) is { } scanned ? $"framework-scanned base type or interface {scanned}" : null)
        ?? MemberTypeRoot(type, compilation);

    static string? AttributeTypeRoot(INamedTypeSymbol type)
    {
        if (UnusedRoots.LiveAttribute(type) is { } own) return $"attribute [{own}]";
        for (var t = type.ContainingType; t is not null; t = t.ContainingType)
            if (UnusedRoots.LiveAttribute(t) is { } a) return $"attribute [{a}] on containing type {t.Name}";
        return null;
    }

    /// <summary>A type holding an entry point, an extern member or a member with a live attribute counts as used.</summary>
    static string? MemberTypeRoot(INamedTypeSymbol type, Compilation compilation)
    {
        foreach (var m in type.GetMembers())
            if (m is not INamedTypeSymbol && !m.IsImplicitlyDeclared && HeldRoot(m, compilation) is { } root) return root;
        return null;
    }

    static string? HeldRoot(ISymbol m, Compilation compilation)
    {
        if (m is IMethodSymbol ms && UnusedRoots.IsEntryPoint(ms, compilation)) return $"contains an entry point ({m.Name})";
        if (m.IsExtern) return $"contains an extern member ({m.Name})";
        return UnusedRoots.LiveAttribute(m) is { } a ? $"contains a member with attribute [{a}] ({m.Name})" : null;
    }
}

/// <summary>
/// Which candidates the <c>unread-member</c> rule judges (non-constant fields and
/// auto-properties), their initializer writes, and the members it never reports although they are written and never
/// read.
/// </summary>
internal static class UnreadRule
{
    /// <summary>The unread facts of a member: a value member, its non-constant initializer, its exclusion.</summary>
    public static (bool ValueMember, bool Initialized, string? Exclusion) Describe(ISymbol m, CompilationModels models) =>
        IsValueMember(m) ? (true, HasNonConstantInitializer(m, models), Exclusion(m)) : (false, false, null);

    static bool IsValueMember(ISymbol m) => m switch
    {
        IFieldSymbol f => !f.IsConst,
        IPropertySymbol p => !p.IsIndexer && IsAutoProperty(p),
        _ => false,
    };

    /// <summary>Every declaration is a property with accessors and no body (<c>{ get; set; }</c>, <c>{ get; init; }</c>).</summary>
    static bool IsAutoProperty(IPropertySymbol p) =>
        p.DeclaringSyntaxReferences.Length > 0 && p.DeclaringSyntaxReferences.All(r => r.GetSyntax() is PropertyDeclarationSyntax
        {
            ExpressionBody: null, AccessorList: { } accessors,
        } && accessors.Accessors.All(a => a.Body is null && a.ExpressionBody is null));

    /// <summary>A field or auto-property initializer that is not a constant is a write (Roslyn counts it for fields only).</summary>
    static bool HasNonConstantInitializer(ISymbol m, CompilationModels models)
    {
        foreach (var r in m.DeclaringSyntaxReferences)
        {
            var value = r.GetSyntax() switch
            {
                VariableDeclaratorSyntax v => v.Initializer?.Value,
                PropertyDeclarationSyntax p => p.Initializer?.Value,
                _ => null,
            };
            if (value is not null && !models.Model(value.SyntaxTree).GetConstantValue(value).HasValue) return true;
        }
        return false;
    }

    /// <summary>Why a written-but-never-read member is not reported (only when certain), or null.</summary>
    static string? Exclusion(ISymbol m)
    {
        var owner = m.ContainingType;
        if (!m.IsStatic && owner.IsRecord) return "record member (synthesized Equals and ToString read it)";
        if (!m.IsStatic && owner.TypeKind == TypeKind.Struct) return "struct instance member (value equality and layout read it)";
        if (owner.GetAttributes().Any(a => a.AttributeClass?.Name is "StructLayoutAttribute" or "InlineArrayAttribute")) return "member of a fixed-layout type";
        return TypeExclusion(m is IFieldSymbol f ? f.Type : ((IPropertySymbol)m).Type);
    }

    /// <summary>Values whose reference is kept on purpose: delegates (native callbacks), IDisposable (timers,
    /// subscriptions), GCHandle, and values of any type: <c>object</c>, <c>dynamic</c> and type
    /// parameters (a constrained one by its constraint types).</summary>
    static string? TypeExclusion(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_Object) return "object type (keeps the object alive)";
        if (type.TypeKind == TypeKind.Dynamic) return "dynamic type (keeps the object alive)";
        if (type is ITypeParameterSymbol p) return TypeParameterExclusion(p);
        if (type.TypeKind == TypeKind.Delegate) return "delegate type (keeps the delegate alive)";
        if (type.SpecialType == SpecialType.System_IDisposable || type.AllInterfaces.Any(i => i.SpecialType == SpecialType.System_IDisposable))
            return "IDisposable type (keeps the object alive)";
        return type is { Name: "GCHandle", ContainingNamespace: { } ns } && ns.ToDisplayString() == "System.Runtime.InteropServices" ? "GCHandle type (keeps the handle)" : null;
    }

    /// <summary>A type parameter that can hold any reference (no constraint type, not <c>struct</c> or <c>unmanaged</c>;
    /// <c>class</c>, <c>notnull</c> and <c>new()</c> leave it open) is excluded like <c>object</c>; a constrained one is
    /// excluded when a constraint type is (for example <c>where T : IDisposable</c>).</summary>
    static string? TypeParameterExclusion(ITypeParameterSymbol p)
    {
        if (p.ConstraintTypes.IsEmpty)
            return p.HasValueTypeConstraint || p.HasUnmanagedTypeConstraint ? null : "unconstrained type parameter (keeps the object alive)";
        return p.ConstraintTypes.Select(TypeExclusion).FirstOrDefault(why => why is not null);
    }
}

/// <summary>The kinds, accessibility, display names, allowlist names and finding places of the
/// unused-code candidates.</summary>
internal static class CandidateNames
{
    public static string? Access(ISymbol s) => s switch
    {
        INamedTypeSymbol { IsFileLocal: true } => "file",
        _ => s.DeclaredAccessibility switch { Accessibility.Private => "private", Accessibility.Internal => "internal", _ => null },
    };

    /// <summary>A member's name for name roots (a constructor carries its type's), display name and allowlist name.</summary>
    public static (string Name, string Display, string FullName) MemberNames(ISymbol m, INamedTypeSymbol type) =>
        m is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor
            ? (type.Name, ConstructorDisplay(ctor), FullName(type, "#ctor"))
            : (m.Name, m.Name, FullName(type, m.Name));

    /// <summary><c>Type(int, string)</c>: a constructor's display name with its parameter types.</summary>
    static string ConstructorDisplay(IMethodSymbol ctor) =>
        $"{ctor.ContainingType.Name}({string.Join(", ", ctor.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)))})";

    public static string Kind(INamedTypeSymbol t) => t.TypeKind switch
    {
        TypeKind.Struct => t.IsRecord ? "record struct" : "struct",
        TypeKind.Interface => "interface",
        TypeKind.Enum => "enum",
        TypeKind.Delegate => "delegate",
        _ => t.IsRecord ? "record" : "class",
    };

    /// <summary>The candidate kind of a member, or null when the member kind is never a candidate (IDE0051 filter widened to internal).</summary>
    public static string? MemberKind(ISymbol m, INamedTypeSymbol type) => m switch
    {
        IMethodSymbol { MethodKind: MethodKind.Ordinary } => "method",
        IMethodSymbol { MethodKind: MethodKind.Constructor } ctor when IsCandidateConstructor(ctor, type) => "constructor",
        IFieldSymbol { AssociatedSymbol: null } f => f.IsConst ? "constant" : "field",
        IPropertySymbol => "property",
        IEventSymbol => "event",
        _ => null,
    };

    /// <summary>An instance constructor with parameters, except a record's copy constructor.</summary>
    static bool IsCandidateConstructor(IMethodSymbol ctor, INamedTypeSymbol type)
    {
        if (ctor.IsStatic || ctor.Parameters.Length == 0) return false;
        return !(type.IsRecord && ctor.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(ctor.Parameters[0].Type, type));
    }

    /// <summary>Allowlist name: namespace and types joined with dots, no generic arity, then the member.</summary>
    public static string FullName(INamedTypeSymbol type, string? member)
    {
        var parts = new List<string>();
        for (var t = type; t is not null; t = t.ContainingType) parts.Add(t.Name);
        parts.Reverse();
        var ns = SymbolFacts.Namespace(type);
        var name = (ns.Length == 0 ? "" : ns + ".") + string.Join('.', parts);
        return member is null ? name : name + "." + member;
    }

    /// <summary>
    /// Finding places of the members (like the syntax tier's member keys): <c>Type::Name</c>, constructors
    /// <c>Type::.ctor</c>; a second and later member of the same name in declaration order (path, position) gets <c>#2</c>,
    /// <c>#3</c>. Every source member counts for the ordinal, candidate or not.
    /// </summary>
    public static Dictionary<ISymbol, string> PlaceKeys(INamedTypeSymbol type, string typeName)
    {
        var keys = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        foreach (var g in Located(type).GroupBy(x => x.Name, StringComparer.Ordinal))
        {
            int n = 0;
            foreach (var x in g.OrderBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.Position))
                keys[x.Member] = ++n == 1 ? $"{typeName}::{x.Name}" : $"{typeName}::{x.Name}#{n}";
        }
        return keys;
    }

    /// <summary>The type's source members with their place name and first declaration (path, position).</summary>
    static List<(ISymbol Member, string Name, string Path, int Position)> Located(INamedTypeSymbol type)
    {
        var located = new List<(ISymbol Member, string Name, string Path, int Position)>();
        foreach (var m in type.GetMembers())
        {
            if (m is INamedTypeSymbol || m.IsImplicitlyDeclared || m.DeclaringSyntaxReferences.Length == 0) continue;
            var first = m.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OrderBy(n => n.SyntaxTree.FilePath, StringComparer.Ordinal).ThenBy(n => n.SpanStart).First();
            located.Add((m, PlaceName(m), first.SyntaxTree.FilePath, first.SpanStart));
        }
        return located;
    }

    static string PlaceName(ISymbol m) =>
        m is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } ms ? (ms.IsStatic ? ".cctor" : ".ctor") : m.Name;
}
