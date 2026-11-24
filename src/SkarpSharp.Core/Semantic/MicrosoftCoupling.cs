using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace SkarpSharp.Semantic;

/// <summary>
/// Class coupling by Microsoft's rules (the Visual Studio code metrics engine, Microsoft.CodeAnalysis.Metrics, CA1506).
/// Reimplemented from the engine's documented behavior and checked against Metrics.exe 5.6.0 on worked examples
/// (tests/SkarpSharp.Tests/Oracle/microsoft-coupling-examples.json):
/// a type's set is the union of a walk over its declarations (member declarations skipped) and its members' sets; a
/// member's set holds its walk plus parameter and return, field, property or event types. The walk asks the semantic
/// model for the type of every syntax node it visits and, at the first operation root below a node, adds the type of
/// every operation and the containing type of static member references and of static or extension calls. Named types
/// enter by their original definition with their type arguments; arrays, pointers, type parameters, dynamic, anonymous
/// types, the special types in <see cref="IsIgnored"/> and types marked compiler-generated or generated code do not. The
/// measured type and its containing types are removed.
/// </summary>
public sealed class MicrosoftCoupling(Func<SyntaxTree, SemanticModel> models)
{
    /// <summary>The coupled types of <paramref name="type"/> by Microsoft's rules.</summary>
    public HashSet<INamedTypeSymbol> Compute(INamedTypeSymbol type)
    {
        var set = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        Walk(type, set);
        foreach (var member in Members(type)) set.UnionWith(Member(member));
        RemoveContaining(type, set);
        return set;
    }

    /// <summary>Members measured by the engine: nested types, implicitly declared members and accessors are not.</summary>
    static IEnumerable<ISymbol> Members(INamedTypeSymbol type)
    {
        foreach (var m in type.GetMembers())
        {
            if (m is INamedTypeSymbol || m.IsImplicitlyDeclared) continue;
            if (m is IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise })
                continue;
            yield return m;
        }
    }

    HashSet<INamedTypeSymbol> Member(ISymbol member)
    {
        var set = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        Walk(member, set);
        switch (member)
        {
            case IMethodSymbol m:
                foreach (var p in m.Parameters) Add(set, p.Type);
                if (!m.ReturnsVoid) Add(set, m.ReturnType);
                break;
            case IPropertySymbol p:
                foreach (var a in new[] { p.GetMethod, p.SetMethod })
                    if (a is { IsImplicitlyDeclared: false }) set.UnionWith(Member(a));
                foreach (var x in p.Parameters) Add(set, x.Type);
                Add(set, p.Type);
                break;
            case IFieldSymbol f:
                Add(set, f.Type);
                break;
            case IEventSymbol e:
                foreach (var a in new[] { e.AddMethod, e.RemoveMethod })
                    if (a is { IsImplicitlyDeclared: false }) set.UnionWith(Member(a));
                Add(set, e.Type);
                break;
        }
        RemoveContaining(member, set);
        return set;
    }

    static void RemoveContaining(ISymbol symbol, HashSet<INamedTypeSymbol> set)
    {
        for (var t = symbol as INamedTypeSymbol ?? symbol.ContainingType; t is not null; t = t.ContainingType) set.Remove(t);
    }

    /// <summary>The declaration walk of one symbol over all its declarations.</summary>
    void Walk(ISymbol symbol, HashSet<INamedTypeSymbol> set)
    {
        var attributes = symbol.GetAttributes();
        if (symbol is IMethodSymbol ms) attributes = attributes.AddRange(ms.GetReturnTypeAttributes());
        var parameters = symbol switch { IMethodSymbol m => m.Parameters, IPropertySymbol p => p.Parameters, _ => [] };
        var applicable = new HashSet<SyntaxNode>();
        var queue = new Queue<SyntaxNode>();
        foreach (var declaration in symbol.DeclaringSyntaxReferences)
        {
            var top = Topmost(declaration.GetSyntax(), symbol);
            queue.Enqueue(top);
            foreach (var p in parameters)
                if (p.DeclaringSyntaxReferences.FirstOrDefault() is { } pr) queue.Enqueue(pr.GetSyntax());
            foreach (var a in attributes)
                if (a.ApplicationSyntaxReference is { } ar && ar.SyntaxTree == declaration.SyntaxTree && applicable.Add(ar.GetSyntax()))
                    queue.Enqueue(ar.GetSyntax());
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                var model = models(node.SyntaxTree);
                if (!ReferenceEquals(node, top) && model.GetDeclaredSymbol(node) is { } declared
                    && !SymbolEqualityComparer.Default.Equals(declared, symbol) && declared.Kind != SymbolKind.Parameter)
                    continue; // member declarations are measured as members
                Add(set, model.GetTypeInfo(node).Type);
                var operation = model.GetOperation(node);
                if (operation is { Parent: null })
                {
                    // Attributes not applied to this symbol (on a parameter, or reached through a field declaration at type
                    // level) give their class through the type above, but their arguments are not walked.
                    if (operation.Kind is OperationKind.None or OperationKind.Attribute && !applicable.Contains(node)) continue;
                    foreach (var op in operation.DescendantsAndSelf()) AddOperation(set, op);
                }
                else
                    foreach (var child in node.ChildNodes()) queue.Enqueue(child);
            }
        }
    }

    static void AddOperation(HashSet<INamedTypeSymbol> set, IOperation op)
    {
        Add(set, op.Type);
        if (op is IMemberReferenceOperation { Member.IsStatic: true } mr) Add(set, mr.Member.ContainingType);
        else if (op is IInvocationOperation inv && (inv.TargetMethod.IsStatic || inv.TargetMethod.IsExtensionMethod)) Add(set, inv.TargetMethod.ContainingType);
    }

    SyntaxNode Topmost(SyntaxNode node, ISymbol symbol)
    {
        var model = models(node.SyntaxTree);
        while (node.Parent is { } parent && SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(parent), symbol)) node = parent;
        return node;
    }

    static void Add(HashSet<INamedTypeSymbol> set, ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named || IsIgnored(named)) return;
        if (named.IsGenericType)
            foreach (var arg in named.TypeArguments) Add(set, arg);
        set.Add(named.OriginalDefinition);
    }

    /// <summary>Microsoft's ignore list: these special types, anonymous types, and types marked as generated.</summary>
    public static bool IsIgnored(INamedTypeSymbol t)
    {
        switch (t.SpecialType)
        {
            case SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_Char or SpecialType.System_Double
                or SpecialType.System_Int16 or SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_UInt16
                or SpecialType.System_UInt32 or SpecialType.System_UInt64 or SpecialType.System_IntPtr or SpecialType.System_UIntPtr
                or SpecialType.System_SByte or SpecialType.System_Single or SpecialType.System_String or SpecialType.System_Object
                or SpecialType.System_ValueType or SpecialType.System_Void:
                return true;
        }
        if (t.IsAnonymousType) return true;
        return SymbolFacts.HasAttribute(t, "System.Runtime.CompilerServices.CompilerGeneratedAttribute")
            || SymbolFacts.HasAttribute(t, "System.CodeDom.Compiler.GeneratedCodeAttribute");
    }
}
