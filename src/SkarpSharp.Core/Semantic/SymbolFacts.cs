using System.Text;
using Microsoft.CodeAnalysis;

namespace SkarpSharp.Semantic;

/// <summary>Names and classifications of type symbols shared by the semantic metrics.</summary>
public static class SymbolFacts
{
    /// <summary>Namespace, containing types joined with <c>+</c>, generic arity as <c>`n</c> (the syntax output's type names).</summary>
    public static string TypeName(INamedTypeSymbol type)
    {
        var parts = new List<string>();
        for (var t = type; t is not null; t = t.ContainingType)
            parts.Add(t.Arity > 0 ? $"{t.Name}`{t.Arity}" : t.Name);
        parts.Reverse();
        var ns = Namespace(type);
        var name = string.Join("+", parts);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }

    /// <summary>The namespace of the outermost containing type ("" for the global namespace).</summary>
    public static string Namespace(INamedTypeSymbol type)
    {
        var outer = type;
        while (outer.ContainingType is not null) outer = outer.ContainingType;
        var sb = new StringBuilder();
        for (var ns = outer.ContainingNamespace; ns is not null && !ns.IsGlobalNamespace; ns = ns.ContainingNamespace)
            sb.Insert(0, sb.Length == 0 ? ns.Name : ns.Name + ".");
        return sb.ToString();
    }

    /// <summary>A short display name for reports: <c>Outer.Inner&lt;T&gt;</c> without namespace.</summary>
    public static string ShortName(INamedTypeSymbol type) => type.ToDisplayString(ShortFormat);

    static readonly SymbolDisplayFormat ShortFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>Stable key of a type across compilations of one run: assembly name and type name.</summary>
    public static string Key(INamedTypeSymbol type) => (type.ContainingAssembly?.Name ?? "") + "|" + TypeName(type);

    /// <summary>A .NET framework type is one in the namespace <c>System</c> or below it.</summary>
    public static bool IsFramework(INamedTypeSymbol type)
    {
        var ns = Namespace(type);
        return ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal);
    }

    /// <summary>C# keyword types (bool, int, string, object, decimal, void, nint, ...): never counted as coupling.</summary>
    public static bool IsKeywordType(ITypeSymbol type) => type.SpecialType is
        SpecialType.System_Object or SpecialType.System_Boolean or SpecialType.System_Char or SpecialType.System_SByte
        or SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32
        or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Decimal
        or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_String or SpecialType.System_IntPtr
        or SpecialType.System_UIntPtr or SpecialType.System_Void;

    /// <summary>True when <paramref name="type"/> is <paramref name="outer"/> or nested (at any depth) in it.</summary>
    public static bool IsWithin(INamedTypeSymbol type, INamedTypeSymbol outer)
    {
        for (var t = type; t is not null; t = t.ContainingType)
            if (SymbolEqualityComparer.Default.Equals(t, outer)) return true;
        return false;
    }

    /// <summary>True when <paramref name="type"/> derives (through base classes) from <paramref name="ancestor"/>.</summary>
    public static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol ancestor)
    {
        for (var b = type.BaseType; b is not null; b = b.BaseType)
            if (SymbolEqualityComparer.Default.Equals(b.OriginalDefinition, ancestor)) return true;
        return false;
    }

    public static bool HasAttribute(ISymbol symbol, string fullName) =>
        symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == fullName);

    /// <summary>
    /// An auto-property counts as a field (state). Source: every accessor without a body (not abstract,
    /// not in an interface), or a positional record parameter. Metadata: the getter carries [CompilerGenerated].
    /// </summary>
    public static bool IsAutoProperty(IPropertySymbol p)
    {
        if (p.IsAbstract || p.IsIndexer || p.ContainingType.TypeKind == TypeKind.Interface) return false;
        if (p.DeclaringSyntaxReferences.Length == 0)
            return p.GetMethod is { } g && HasAttribute(g, "System.Runtime.CompilerServices.CompilerGeneratedAttribute");
        foreach (var r in p.DeclaringSyntaxReferences)
        {
            var node = r.GetSyntax();
            if (node is Microsoft.CodeAnalysis.CSharp.Syntax.ParameterSyntax) return true;
            if (node is not Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax pd) return false;
            if (pd.ExpressionBody is not null || pd.AccessorList is null) return false;
            if (pd.Modifiers.Any(m => m.ValueText is "extern" or "partial")) return false;
            if (pd.AccessorList.Accessors.Any(a => a.Body is not null || a.ExpressionBody is not null)) return false;
        }
        return true;
    }
}
