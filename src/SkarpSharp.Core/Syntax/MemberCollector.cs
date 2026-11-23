using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SkarpSharp.Syntax;

/// <summary>
/// Finds every analyzable body in a syntax tree and gives it a stable syntactic id.
/// Nested bodies (lambdas, anonymous methods, local functions) become their own records with a parent id.
/// </summary>
public static class MemberCollector
{
    public static List<MemberBody> Collect(SyntaxTree tree, string relativePath)
    {
        var result = new Collector(tree).Run(relativePath);
        AssignKeys(result);
        return result;
    }

    /// <summary>One collection: the tree's text, the records in collection order and the ids used so far.</summary>
    sealed class Collector(SyntaxTree tree)
    {
        readonly CompilationUnitSyntax root = tree.GetCompilationUnitRoot();
        readonly SourceText text = tree.GetText();
        readonly List<MemberBody> result = [];
        readonly Dictionary<string, int> ids = new(StringComparer.Ordinal);

        public List<MemberBody> Run(string relativePath)
        {
            CollectTopLevel(relativePath);
            CollectMembers();
            CollectInitializerLambdas();
            return result;
        }

        string Unique(string id)
        {
            if (ids.TryGetValue(id, out var n)) { ids[id] = n + 1; return $"{id}#{n + 1}"; }
            ids[id] = 1;
            return id;
        }

        int Line(int position) => text.Lines.GetLineFromPosition(position).LineNumber + 1;

        int EndLine(SyntaxNode decl) => Line(Math.Max(decl.SpanStart, decl.Span.End - 1));

        /// <summary>Top-level statements: one record per file.</summary>
        void CollectTopLevel(string relativePath)
        {
            var globals = root.Members.OfType<GlobalStatementSyntax>().ToList();
            if (globals.Count == 0) return;
            var roots = globals.Select(g => (SyntaxNode)g.Statement).ToList();
            var tl = new MemberBody
            {
                Id = Unique($"{relativePath}::<top-level>"), Name = "<top-level>", Kind = MemberKind.TopLevel, TypeName = "<top-level>",
                Declaration = globals[0], Roots = roots, IsExpressionBody = false, RecursionName = null,
                StartLine = Line(globals[0].SpanStart), EndLine = Line(globals[^1].Span.End - 1),
            };
            result.Add(tl);
            foreach (var r in roots)
            {
                if (r is LocalFunctionStatementSyntax lf) AddLocalFunction(lf, tl);
                else AddNested(r, tl);
            }
        }

        /// <summary>Members with a body, in declaration order, in every type of the file.</summary>
        void CollectMembers()
        {
            foreach (var member in root.DescendantNodes(n => n is not MemberDeclarationSyntax || n is BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax || n is CompilationUnitSyntax).OfType<MemberDeclarationSyntax>())
            {
                if (member is BaseNamespaceDeclarationSyntax or GlobalStatementSyntax) continue;
                if (member.Parent is not TypeDeclarationSyntax type) continue;
                CollectMember(member, TypeName(type));
            }
        }

        void CollectMember(MemberDeclarationSyntax member, string typeName)
        {
            switch (member)
            {
                case MethodDeclarationSyntax m when HasBody(m):
                    Add(m, m.Identifier.ValueText, MemberKind.Method, typeName, Roots(m), m.Body is null, m.Identifier.ValueText,
                        $"{m.Identifier.ValueText}{Arity(m.TypeParameterList)}({Params(m.ParameterList)})");
                    break;
                case ConstructorDeclarationSyntax c when HasBody(c):
                    Add(c, ".ctor", MemberKind.Constructor, typeName, Roots(c), c.Body is null, null,
                        $"{(c.Modifiers.Any(SyntaxKind.StaticKeyword) ? ".cctor" : ".ctor")}({Params(c.ParameterList)})",
                        initializerRoots: c.Initializer is { } ci ? [ci.ArgumentList] : null);
                    break;
                case DestructorDeclarationSyntax d when HasBody(d):
                    Add(d, "~", MemberKind.Destructor, typeName, Roots(d), d.Body is null, null, "Finalize()");
                    break;
                case OperatorDeclarationSyntax o when HasBody(o):
                    Add(o, $"operator {o.OperatorToken.ValueText}", MemberKind.Operator, typeName, Roots(o), o.Body is null, null,
                        $"operator {o.OperatorToken.ValueText}({Params(o.ParameterList)})");
                    break;
                case ConversionOperatorDeclarationSyntax co when HasBody(co):
                    Add(co, $"{co.ImplicitOrExplicitKeyword.ValueText} operator {Compact(co.Type)}", MemberKind.Conversion, typeName,
                        Roots(co), co.Body is null, null,
                        $"{co.ImplicitOrExplicitKeyword.ValueText} operator {Compact(co.Type)}({Params(co.ParameterList)})");
                    break;
                case PropertyDeclarationSyntax p:
                    AddProperty(p, p.Identifier.ValueText, p.ExpressionBody, p.AccessorList, typeName, false);
                    break;
                case IndexerDeclarationSyntax ix:
                    AddProperty(ix, $"this[{Params(ix.ParameterList)}]", ix.ExpressionBody, ix.AccessorList, typeName, true);
                    break;
                case EventDeclarationSyntax ev when ev.AccessorList is not null:
                    AddEventAccessors(ev, ev.AccessorList, typeName);
                    break;
            }
        }

        void AddEventAccessors(EventDeclarationSyntax ev, AccessorListSyntax accessors, string typeName)
        {
            foreach (var a in accessors.Accessors)
            {
                if (!HasBody(a)) continue;
                var add = a.IsKind(SyntaxKind.AddAccessorDeclaration);
                var suffix = add ? "add" : "remove";
                Add(a, $"{ev.Identifier.ValueText}.{suffix}", add ? MemberKind.EventAdd : MemberKind.EventRemove, typeName,
                    Roots(a), a.Body is null, null, $"{ev.Identifier.ValueText}.{suffix}");
            }
        }

        void AddProperty(MemberDeclarationSyntax decl, string name, ArrowExpressionClauseSyntax? exprBody, AccessorListSyntax? accessors, string typeName, bool indexer)
        {
            if (exprBody is not null)
            {
                Add(decl, $"{name}.get", indexer ? MemberKind.IndexerExpression : MemberKind.PropertyExpression, typeName,
                    [exprBody.Expression], true, null, $"{name}.get");
                return;
            }
            if (accessors is null) return;
            foreach (var a in accessors.Accessors)
            {
                if (!HasBody(a)) continue;
                var (suffix, kind) = AccessorKind(a, indexer);
                Add(a, $"{name}.{suffix}", kind, typeName, Roots(a), a.Body is null, null, $"{name}.{suffix}");
            }
        }

        static (string Suffix, MemberKind Kind) AccessorKind(AccessorDeclarationSyntax a, bool indexer) => a.Kind() switch
        {
            SyntaxKind.GetAccessorDeclaration => ("get", indexer ? MemberKind.IndexerGet : MemberKind.PropertyGet),
            SyntaxKind.InitAccessorDeclaration => ("init", indexer ? MemberKind.IndexerInit : MemberKind.PropertyInit),
            _ => ("set", indexer ? MemberKind.IndexerSet : MemberKind.PropertySet),
        };

        /// <summary>
        /// Lambdas outside member bodies: field, event-field and property initializers. (Constructor initializers are
        /// handled in Add; lambdas in primary-constructor base arguments are not recorded yet.)
        /// </summary>
        void CollectInitializerLambdas()
        {
            foreach (var type in root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax).OfType<TypeDeclarationSyntax>())
            {
                var typeName = TypeName(type);
                foreach (var m in type.Members)
                    foreach (var (owner, init) in Initializers(m)) AddInitializerLambdas(m, typeName, owner, init);
            }
        }

        /// <summary>The nested bodies of one initializer hang under a pseudo parent <c>Type::owner</c> that is not a record.</summary>
        void AddInitializerLambdas(MemberDeclarationSyntax m, string typeName, string owner, SyntaxNode init)
        {
            var pseudo = new MemberBody
            {
                Id = $"{typeName}::{owner}", Name = owner, Kind = MemberKind.Lambda, TypeName = typeName,
                Declaration = m, Roots = [], IsExpressionBody = false,
            };
            foreach (var node in DirectNested(init)) AddNestedWithPseudoParent(node, pseudo);
        }

        static IEnumerable<(string Owner, SyntaxNode Init)> Initializers(MemberDeclarationSyntax m) => m switch
        {
            BaseFieldDeclarationSyntax fd => fd.Declaration.Variables.Where(v => v.Initializer is not null).Select(v => (v.Identifier.ValueText, (SyntaxNode)v.Initializer!)),
            PropertyDeclarationSyntax { Initializer: { } pi } p => [(p.Identifier.ValueText, (SyntaxNode)pi)],
            _ => [],
        };

        void AddNestedWithPseudoParent(SyntaxNode node, MemberBody pseudo)
        {
            int index = result.Count(r => r.ParentId == pseudo.Id) + 1;
            var pos = text.Lines.GetLinePosition(node.SpanStart);
            var roots = MemberBody.NestedRoots(node);
            var isLocal = node is LocalFunctionStatementSyntax;
            var child = Child(node, isLocal ? $"{pseudo.Name}/{((LocalFunctionStatementSyntax)node).Identifier.ValueText}" : $"{pseudo.Name}/lambda{index}",
                isLocal ? MemberKind.LocalFunction : MemberKind.Lambda, pseudo, roots,
                node is AnonymousFunctionExpressionSyntax af ? af.Block is null : ((LocalFunctionStatementSyntax)node).Body is null,
                null, $"{pseudo.Id}/lambda@{pos.Line + 1}:{pos.Character + 1}");
            foreach (var r in roots) AddNested(r, child);
        }

        void Add(SyntaxNode decl, string name, MemberKind kind, string typeName, IReadOnlyList<SyntaxNode> roots, bool exprBody, string? recursionName, string memberText, IReadOnlyList<SyntaxNode>? initializerRoots = null)
        {
            var body = new MemberBody
            {
                Id = Unique($"{typeName}::{memberText}"), Name = name, Kind = kind, TypeName = typeName, ParentId = null, Declaration = decl,
                Roots = roots, InitializerRoots = initializerRoots ?? [], IsExpressionBody = exprBody, RecursionName = recursionName,
                StartLine = Line(decl.SpanStart), EndLine = EndLine(decl),
            };
            result.Add(body);
            int lambdas = 0;
            foreach (var r in roots) lambdas = AddNested(r, body);
            // Lambdas in a constructor initializer (': base(x => ...)', ': this(...)') are nested bodies of the constructor
            // like those in its body. They are numbered after the body's lambdas, so body lambda names do not shift.
            if (initializerRoots is not null)
                foreach (var r in initializerRoots) lambdas = AddNested(r, body, lambdas);
        }

        /// <summary>
        /// Nested bodies directly inside 'scope' (not inside another nested body) become children of 'parent'.
        /// Lambda names continue from 'lambdaIndex'; returns the last index used.
        /// </summary>
        int AddNested(SyntaxNode scope, MemberBody parent, int lambdaIndex = 0)
        {
            foreach (var node in DirectNested(scope))
            {
                if (node is LocalFunctionStatementSyntax lf) AddLocalFunction(lf, parent);
                else AddLambda(node, parent, ++lambdaIndex);
            }
            return lambdaIndex;
        }

        void AddLocalFunction(LocalFunctionStatementSyntax lf, MemberBody parent)
        {
            var roots = MemberBody.NestedRoots(lf);
            var child = Child(lf, $"{parent.Name}/{lf.Identifier.ValueText}", MemberKind.LocalFunction, parent,
                roots, lf.Body is null, lf.Identifier.ValueText, $"{parent.Id}/{lf.Identifier.ValueText}");
            foreach (var r in roots) AddNested(r, child);
        }

        void AddLambda(SyntaxNode node, MemberBody parent, int lambdaIndex)
        {
            var pos = text.Lines.GetLinePosition(node.SpanStart);
            var roots = MemberBody.NestedRoots(node);
            var child = Child(node, $"{parent.Name}/lambda{lambdaIndex}", MemberKind.Lambda, parent,
                roots, ((AnonymousFunctionExpressionSyntax)node).Block is null, null,
                $"{parent.Id}/lambda@{pos.Line + 1}:{pos.Character + 1}");
            foreach (var r in roots) AddNested(r, child);
        }

        MemberBody Child(SyntaxNode decl, string name, MemberKind kind, MemberBody parent, IReadOnlyList<SyntaxNode> roots, bool exprBody, string? recursionName, string id)
        {
            var body = new MemberBody
            {
                Id = Unique(id), Name = name, Kind = kind, TypeName = parent.TypeName, ParentId = parent.Id,
                Declaration = decl, Roots = roots, IsExpressionBody = exprBody, RecursionName = recursionName,
                StartLine = Line(decl.SpanStart), EndLine = EndLine(decl),
            };
            result.Add(body);
            return body;
        }
    }

    /// <summary>
    /// Finding identity v1: a key per record without parameter text or line numbers.
    /// Members: <c>Type::Name</c> (constructors <c>.ctor</c> or <c>.cctor</c>, indexers <c>this[]</c>, accessors
    /// <c>P.get</c>), the n-th member of the same key in the file (overloads in declaration order) gets <c>#n</c> from n = 2.
    /// Nested bodies: the parent's key plus <c>/lambdaN</c> (N-th lambda of that parent) or <c>/name</c> (local function).
    /// Lambdas of field and property initializers hang under <c>Type::field</c>. Top-level statements: <c>&lt;top-level&gt;</c>.
    /// Records are in collection order (declaration order, parents before their nested bodies).
    /// </summary>
    internal static void AssignKeys(List<MemberBody> bodies)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var lambdas = new Dictionary<string, int>(StringComparer.Ordinal);
        var keyById = new Dictionary<string, string>(StringComparer.Ordinal);
        string Unique(string key)
        {
            int n = seen.GetValueOrDefault(key) + 1;
            seen[key] = n;
            return n == 1 ? key : $"{key}#{n}";
        }
        foreach (var b in bodies)
        {
            string key;
            if (b.ParentId is null)
                key = b.Kind == MemberKind.TopLevel ? "<top-level>" : $"{b.TypeName}::{BaseName(b)}";
            else
            {
                // A parent that is not a record (field or property initializer) is named by its id, Type::field.
                var parent = keyById.GetValueOrDefault(b.ParentId, b.ParentId);
                if (b.Kind == MemberKind.Lambda)
                {
                    int n = lambdas.GetValueOrDefault(parent) + 1;
                    lambdas[parent] = n;
                    key = $"{parent}/lambda{n}";
                }
                else key = $"{parent}/{b.Name[(b.Name.LastIndexOf('/') + 1)..]}";
            }
            b.Key = Unique(key);
            keyById[b.Id] = b.Key;
        }
    }

    static string BaseName(MemberBody b)
    {
        if (b.Kind == MemberKind.Constructor) return b.Id.Contains("::.cctor(", StringComparison.Ordinal) ? ".cctor" : ".ctor";
        if (b.Kind is MemberKind.IndexerGet or MemberKind.IndexerSet or MemberKind.IndexerInit or MemberKind.IndexerExpression)
        {
            int end = b.Name.LastIndexOf("].", StringComparison.Ordinal);
            return end < 0 ? b.Name : "this[]" + b.Name[(end + 1)..];
        }
        return b.Name;
    }

    /// <summary>Nested bodies directly inside scope, not inside another nested body.</summary>
    internal static IEnumerable<SyntaxNode> DirectNested(SyntaxNode scope)
    {
        if (MemberBody.IsNestedBody(scope)) { yield return scope; yield break; }
        foreach (var n in scope.DescendantNodes(n => n == scope || !MemberBody.IsNestedBody(n)))
            if (MemberBody.IsNestedBody(n)) yield return n;
    }

    static bool HasBody(BaseMethodDeclarationSyntax m) => m.Body is not null || m.ExpressionBody is not null;
    static bool HasBody(AccessorDeclarationSyntax a) => a.Body is not null || a.ExpressionBody is not null;

    static IReadOnlyList<SyntaxNode> Roots(BaseMethodDeclarationSyntax m) =>
        m.Body is not null ? [m.Body] : m.ExpressionBody is not null ? [m.ExpressionBody.Expression] : [];

    static IReadOnlyList<SyntaxNode> Roots(AccessorDeclarationSyntax a) =>
        a.Body is not null ? [a.Body] : a.ExpressionBody is not null ? [a.ExpressionBody.Expression] : [];

    internal static string TypeName(TypeDeclarationSyntax type)
    {
        var parts = new List<string>();
        SyntaxNode? n = type;
        string? ns = null;
        while (n is not null)
        {
            switch (n)
            {
                case TypeDeclarationSyntax t: parts.Add($"{t.Identifier.ValueText}{Arity(t.TypeParameterList)}"); break;
                case BaseNamespaceDeclarationSyntax nd: ns = ns is null ? nd.Name.ToString() : $"{nd.Name}.{ns}"; break;
            }
            n = n.Parent;
        }
        parts.Reverse();
        var typePart = string.Join("+", parts);
        return ns is null ? typePart : $"{ns}.{typePart}";
    }

    static string Arity(TypeParameterListSyntax? list) => list is null || list.Parameters.Count == 0 ? "" : $"`{list.Parameters.Count}";

    static string Params(BaseParameterListSyntax list) =>
        string.Join(",", list.Parameters.Select(p => p.Type is null ? "" : Compact(p.Type)));

    static string Compact(SyntaxNode node)
    {
        var sb = new StringBuilder();
        foreach (var t in node.DescendantTokens()) sb.Append(t.ValueText);
        return sb.ToString();
    }
}
