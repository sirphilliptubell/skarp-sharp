using Microsoft.CodeAnalysis;

namespace SkarpSharp.Semantic;

/// <summary>
/// Stable cross-compilation symbol ids:
/// <c>{assembly name}|{documentation comment id}</c> of the symbol's definition. Constructed generics map to their
/// original definition, reduced extension methods to the static method, partial methods to the definition part (both
/// parts give the same id anyway). Local functions and lambdas have no id: their doc ids collide (a local function gets
/// its bare name, a lambda an empty one), so their uses are attributed to the enclosing member instead. Ids are only
/// compared, never resolved back to symbols.
/// </summary>
public static class SymbolIds
{
    /// <summary>The definition a reference counts for, or null for local functions, lambdas and other symbols without an id.</summary>
    public static ISymbol? Definition(ISymbol symbol)
    {
        switch (symbol)
        {
            case IMethodSymbol m:
                if (m.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction) return null;
                m = (m.ReducedFrom ?? m).OriginalDefinition;
                return m.PartialDefinitionPart ?? m;
            case IPropertySymbol p:
                p = p.OriginalDefinition;
                return p.PartialDefinitionPart ?? p;
            default:
                return NoId.Contains(symbol.Kind) ? null : symbol.OriginalDefinition;
        }
    }

    /// <summary>Symbol kinds that are never a definition with an id: locals, parameters, labels, range variables,
    /// namespaces, aliases, discards, preprocessing symbols and type parameters.</summary>
    static readonly HashSet<SymbolKind> NoId =
    [
        SymbolKind.Local, SymbolKind.Parameter, SymbolKind.Label, SymbolKind.RangeVariable, SymbolKind.Namespace, SymbolKind.Alias,
        SymbolKind.Discard, SymbolKind.Preprocessing, SymbolKind.TypeParameter,
    ];

    /// <summary>The id of a symbol's definition, or null when it has none.</summary>
    public static string? Of(ISymbol symbol)
    {
        if (Definition(symbol) is not { } def) return null;
        var doc = DocumentationCommentId.CreateDeclarationId(def);
        return doc is null ? null : (def.ContainingAssembly?.Name ?? "") + "|" + doc;
    }
}
