using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace SkarpSharp.Semantic;

/// <summary>One type used by a measured type, with the tags that decide which coupling counts include it.</summary>
public sealed record CoupledType(string Key, string ShortName, bool Framework, bool Internal);

/// <summary>A foreign field or property met by the ATFD walk: display name (<c>Type.Member</c>) and framework flag.</summary>
public readonly record struct ForeignAttribute(string Display, bool Framework);

/// <summary>What the shared semantic walk found for one type (no symbols kept).</summary>
public sealed class TypeFacts
{
    public List<CoupledType> Coupled { get; } = [];
    /// <summary>ATFD per member: distinct foreign fields and properties, keyed by assembly, full type name and member
    /// (so equal short names of different types stay apart), with the display name and the framework flag.</summary>
    public List<Dictionary<string, ForeignAttribute>> ForeignPerMember { get; } = [];
    public List<CohesionMember> CohesionMembers { get; } = [];
    /// <summary>True when the type is an interface or static class (TCC and LCC not applicable).</summary>
    public bool CohesionNotApplicable { get; set; }
    /// <summary>Where an unresolved (error) type was met, or null; such types are not evaluated.</summary>
    public string? ErrorType { get; set; }
}

/// <summary>
/// The shared semantic walk of one type: one descent over the operation trees of every non-generated
/// declaration part (lambdas, local functions and accessors folded into their member; nested types excluded), plus the
/// declared signatures, collecting the coupled types (Skarp's own counting rules), the foreign data per member
/// (ATFD) and the state use and same-object calls per method (TCC and LCC).
/// </summary>
public sealed class TypeWalker(Func<SyntaxTree, SemanticModel> models, Func<SyntaxTree, bool> isGenerated, IReadOnlySet<string> solutionAssemblies,
    Compilation compilation)
{
    sealed class State(INamedTypeSymbol self, TypeFacts facts, CohesionIndex cohesion)
    {
        public INamedTypeSymbol Self { get; } = self;
        public TypeFacts Facts { get; } = facts;
        public CohesionIndex Cohesion { get; } = cohesion;
        public Dictionary<string, CoupledType> Coupled { get; } = new(StringComparer.Ordinal);
        /// <summary>The operation being visited (to locate an error type).</summary>
        public IOperation? Current { get; set; }
    }

    static string ErrorAt(INamedTypeSymbol type, IOperation? op)
    {
        if (op is null) return $"unresolved type '{type.Name}' in a declaration";
        var text = op.Syntax.ToString().ReplaceLineEndings(" ");
        int line = op.Syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        return $"unresolved type '{type.Name}' at line {line}: {(text.Length > 80 ? text[..77] + "..." : text)}";
    }

    public TypeFacts Walk(INamedTypeSymbol type)
    {
        var facts = new TypeFacts();
        var s = new State(type, facts, new CohesionIndex(type, compilation));
        facts.CohesionNotApplicable = type.TypeKind == TypeKind.Interface || type.IsStatic;
        var declarations = type.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<TypeDeclarationSyntax>()
            .Where(d => !isGenerated(d.SyntaxTree)).ToList();
        AddDeclared(s, declarations);
        // Operation trees: one ATFD and cohesion unit per member declaration, plus one for the type's own attributes,
        // base-list arguments and primary-constructor parameter defaults.
        var typeLevel = new Dictionary<string, ForeignAttribute>(StringComparer.Ordinal);
        foreach (var d in declarations)
        {
            var model = models(d.SyntaxTree);
            foreach (var child in d.ChildNodes())
            {
                if (child is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax) continue;
                if (child is MemberDeclarationSyntax member)
                {
                    var foreign = new Dictionary<string, ForeignAttribute>(StringComparer.Ordinal);
                    var unit = s.Cohesion.Unit(member, model);
                    foreach (var root in Roots(member, model)) Visit(root, s, foreign, unit);
                    facts.ForeignPerMember.Add(foreign);
                }
                else
                    foreach (var root in Roots(child, model)) Visit(root, s, typeLevel, null);
            }
        }
        facts.ForeignPerMember.Add(typeLevel);
        facts.Coupled.AddRange(s.Coupled.Values.OrderBy(c => c.Key, StringComparer.Ordinal));
        facts.CohesionMembers.AddRange(s.Cohesion.Members);
        return facts;
    }

    /// <summary>Base list, type parameter constraints and the declared signatures of the members.</summary>
    void AddDeclared(State s, List<TypeDeclarationSyntax> declarations)
    {
        foreach (var d in declarations)
            if (d.BaseList is { } bl)
                foreach (var bt in bl.Types) AddCoupled(s, models(d.SyntaxTree).GetTypeInfo(bt.Type).Type);
        foreach (var tp in s.Self.TypeParameters)
            foreach (var c in tp.ConstraintTypes) AddCoupled(s, c);
        foreach (var member in s.Self.GetMembers())
        {
            if (member is INamedTypeSymbol || member.IsImplicitlyDeclared || member.DeclaringSyntaxReferences.All(r => isGenerated(r.SyntaxTree))) continue;
            switch (member)
            {
                case IMethodSymbol m:
                    foreach (var p in m.Parameters) AddCoupled(s, p.Type);
                    if (!m.ReturnsVoid) AddCoupled(s, m.ReturnType);
                    foreach (var tp in m.TypeParameters) foreach (var c in tp.ConstraintTypes) AddCoupled(s, c);
                    break;
                case IPropertySymbol p:
                    AddCoupled(s, p.Type);
                    foreach (var x in p.Parameters) AddCoupled(s, x.Type);
                    break;
                case IFieldSymbol f: AddCoupled(s, f.Type); break;
                case IEventSymbol e: AddCoupled(s, e.Type); break;
            }
        }
    }

    /// <summary>The operation roots below a syntax node: the first node on each path that has an operation.</summary>
    static IEnumerable<IOperation> Roots(SyntaxNode node, SemanticModel model)
    {
        var stack = new Stack<SyntaxNode>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax && !ReferenceEquals(n, node)) continue;
            if (model.GetOperation(n) is { Parent: null } op)
            {
                yield return op;
                // A member's body operation does not hold its attributes or parameter default values.
                foreach (var c in n.ChildNodes())
                    if (c is AttributeListSyntax or BaseParameterListSyntax) stack.Push(c);
                continue;
            }
            foreach (var c in n.ChildNodes()) stack.Push(c);
        }
    }

    void Visit(IOperation root, State s, Dictionary<string, ForeignAttribute> foreign, CohesionMember? unit)
    {
        var stack = new Stack<IOperation>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var op = stack.Pop();
            if (op is INameOfOperation) continue; // nameof(T) names a type without using it
            s.Current = op;
            AddCoupled(s, op.Type);
            AddOperationTypes(s, op);
            Foreign(op, s.Self, foreign);
            if (unit is not null) s.Cohesion.Record(op, unit);
            foreach (var child in op.ChildOperations) stack.Push(child);
        }
    }

    void AddOperationTypes(State s, IOperation op)
    {
        switch (op)
        {
            case IMemberReferenceOperation { Member.IsStatic: true } mr: AddCoupled(s, mr.Member.ContainingType); break;
            case IInvocationOperation inv:
                if (inv.TargetMethod.IsStatic || inv.TargetMethod.IsExtensionMethod) AddCoupled(s, inv.TargetMethod.ContainingType);
                foreach (var ta in inv.TargetMethod.TypeArguments) AddCoupled(s, ta);
                break;
            case IIsTypeOperation it: AddCoupled(s, it.TypeOperand); break;
            case ITypeOfOperation to: AddCoupled(s, to.TypeOperand); break;
            case ISizeOfOperation so: AddCoupled(s, so.TypeOperand); break;
            case IDeclarationPatternOperation dp: AddCoupled(s, dp.MatchedType); break;
            case ITypePatternOperation tp: AddCoupled(s, tp.MatchedType); break;
            case IRecursivePatternOperation rp: AddCoupled(s, rp.MatchedType); break;
            case ICatchClauseOperation cc: AddCoupled(s, cc.ExceptionType); break;
            case IVariableDeclaratorOperation vd: AddCoupled(s, vd.Symbol.Type); break;
        }
    }

    /// <summary>Skarp's own coupling rules: unwrap arrays, pointers, nullable and tuples; definition plus type arguments; drop keyword
    /// types, type parameters, dynamic, anonymous types, the type itself, its containing types and its own nested types.</summary>
    void AddCoupled(State s, ITypeSymbol? t)
    {
        switch (t)
        {
            case IArrayTypeSymbol a: AddCoupled(s, a.ElementType); return;
            case IPointerTypeSymbol p: AddCoupled(s, p.PointedAtType); return;
            case INamedTypeSymbol n:
                if (n.TypeKind == TypeKind.Error) { s.Facts.ErrorType ??= ErrorAt(n, s.Current); return; }
                if (n.IsAnonymousType) return;
                if (n.IsTupleType)
                {
                    foreach (var e in n.TupleElements) AddCoupled(s, e.Type);
                    return;
                }
                if (n.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T && !n.IsUnboundGenericType)
                {
                    AddCoupled(s, n.TypeArguments[0]);
                    return;
                }
                // typeof(List<>): the arguments of an unbound generic are placeholder error types, not unresolved symbols.
                if (n.IsGenericType && !n.IsUnboundGenericType) foreach (var arg in n.TypeArguments) AddCoupled(s, arg);
                var def = n.OriginalDefinition;
                if (SymbolFacts.IsKeywordType(def) || SymbolFacts.IsWithin(s.Self, def) || SymbolFacts.IsWithin(def, s.Self)) return;
                var key = SymbolFacts.Key(def);
                if (!s.Coupled.ContainsKey(key))
                    s.Coupled[key] = new CoupledType(key, SymbolFacts.ShortName(def), SymbolFacts.IsFramework(def),
                        def.ContainingAssembly is { } asm && solutionAssemblies.Contains(asm.Name));
                return;
            default: return; // null, type parameters, dynamic, function pointers
        }
    }

    /// <summary>ATFD: fields and properties (indexers, constants and enum members excluded) of types outside the own
    /// hierarchy (the type, its base types and interfaces, types derived from it, containing and nested types).</summary>
    static void Foreign(IOperation op, INamedTypeSymbol self, Dictionary<string, ForeignAttribute> foreign)
    {
        ISymbol? member = op switch
        {
            IFieldReferenceOperation f when !f.Field.IsConst => f.Field,
            IPropertyReferenceOperation p when !p.Property.IsIndexer => p.Property,
            _ => null,
        };
        if (member?.ContainingType?.OriginalDefinition is not { } owner || owner.TypeKind == TypeKind.Error) return;
        if (SymbolFacts.IsWithin(self, owner) || SymbolFacts.IsWithin(owner, self) || SymbolFacts.DerivesFrom(self, owner)
            || SymbolFacts.DerivesFrom(owner, self) || self.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, owner)))
            return;
        foreign[SymbolFacts.Key(owner) + "." + member.Name] = new ForeignAttribute(SymbolFacts.ShortName(owner) + "." + member.Name, SymbolFacts.IsFramework(owner));
    }
}

/// <summary>
/// The state and the method-like members of one type for TCC and LCC: state = instance fields,
/// field-like events and auto-properties of the type and the accessible ones of its base classes, and the
/// parameters of the type's primary constructor (C# 12), which a member that uses them captures as hidden
/// fields; method-like members =
/// instance methods with a body, properties and indexers with bodies and events with accessors (constructors,
/// operators, static and abstract members excluded); visible = public, protected, internal or protected internal, or an
/// explicit interface implementation.
/// </summary>
public sealed class CohesionIndex
{
    readonly Dictionary<ISymbol, int> _state = new(SymbolEqualityComparer.Default);
    readonly Dictionary<ISymbol, int> _methods = new(SymbolEqualityComparer.Default);
    public List<CohesionMember> Members { get; } = [];

    public CohesionIndex(INamedTypeSymbol type, Compilation compilation)
    {
        if (type.TypeKind == TypeKind.Interface || type.IsStatic) return;
        AddState(type, null);
        foreach (var p in PrimaryConstructorParameters(type)) _state.TryAdd(p, _state.Count);
        for (var b = type.BaseType; b is not null && b.SpecialType != SpecialType.System_Object; b = b.BaseType)
            AddState(b.OriginalDefinition, m => compilation.IsSymbolAccessibleWithin(m, type));
        foreach (var m in type.GetMembers())
        {
            if (m.IsStatic || m.IsAbstract || m.IsImplicitlyDeclared) continue;
            bool add = m switch
            {
                IMethodSymbol ms => ms.MethodKind is MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation
                    && !(ms.IsPartialDefinition && ms.PartialImplementationPart is null) && !ms.IsExtern,
                IPropertySymbol p => !SymbolFacts.IsAutoProperty(p) && !p.IsExtern,
                IEventSymbol e => e.AddMethod is { IsImplicitlyDeclared: false },
                _ => false,
            };
            if (!add) continue;
            _methods[m] = Members.Count;
            Members.Add(new CohesionMember { Name = m.Name, Visible = IsVisible(m) });
        }
    }

    static bool IsVisible(ISymbol m) =>
        m.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.Internal or Accessibility.ProtectedOrInternal
        || m switch
        {
            IMethodSymbol ms => ms.ExplicitInterfaceImplementations.Length > 0,
            IPropertySymbol p => p.ExplicitInterfaceImplementations.Length > 0,
            IEventSymbol e => e.ExplicitInterfaceImplementations.Length > 0,
            _ => false,
        };

    void AddState(INamedTypeSymbol type, Func<ISymbol, bool>? accessible)
    {
        foreach (var m in type.GetMembers())
        {
            if (m.IsStatic || (accessible is not null && !accessible(m))) continue;
            bool state = m switch
            {
                IFieldSymbol f => !f.IsConst && !f.IsImplicitlyDeclared,
                IPropertySymbol p => SymbolFacts.IsAutoProperty(p),
                IEventSymbol e => e.AddMethod is null or { IsImplicitlyDeclared: true },
                _ => false,
            };
            if (state) _state.TryAdd(m, _state.Count);
        }
    }

    /// <summary>The parameters of the type's primary constructor (<c>class C(int a)</c>), declared on a type declaration;
    /// empty when it has none. A use inside a method-like member captures the parameter as state.</summary>
    public static IEnumerable<IParameterSymbol> PrimaryConstructorParameters(INamedTypeSymbol type) =>
        type.InstanceConstructors.Where(c => !c.IsImplicitlyDeclared && c.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax))
            .SelectMany(c => c.Parameters);

    /// <summary>The cohesion unit of a member declaration (null for constructors, fields and other members).</summary>
    public CohesionMember? Unit(MemberDeclarationSyntax declaration, SemanticModel model)
    {
        var symbol = model.GetDeclaredSymbol(declaration);
        if (symbol is IMethodSymbol m) symbol = m.PartialDefinitionPart ?? m;
        return symbol is not null && _methods.TryGetValue(symbol.OriginalDefinition, out var i) ? Members[i] : null;
    }

    /// <summary>Records state use and same-object calls of one operation inside a method-like member.</summary>
    public void Record(IOperation op, CohesionMember unit)
    {
        (ISymbol? target, IOperation? instance) = op switch
        {
            IFieldReferenceOperation f => ((ISymbol?)f.Field, f.Instance),
            IPropertyReferenceOperation p => (p.Property, p.Instance),
            IEventReferenceOperation e => (e.Event, e.Instance),
            IInvocationOperation i => (i.TargetMethod.PartialDefinitionPart ?? i.TargetMethod, i.Instance),
            _ => ((ISymbol?)null, (IOperation?)null),
        };
        // A captured primary-constructor parameter is state of the type, like a field.
        if (op is IParameterReferenceOperation pr && _state.TryGetValue(pr.Parameter, out var captured)) { unit.Fields.Add(captured); return; }
        if (target is null || instance is not IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance }) return;
        var def = target.OriginalDefinition;
        if (_state.TryGetValue(def, out var s)) unit.Fields.Add(s);
        else if (_methods.TryGetValue(def, out var c)) unit.Calls.Add(c);
    }
}
