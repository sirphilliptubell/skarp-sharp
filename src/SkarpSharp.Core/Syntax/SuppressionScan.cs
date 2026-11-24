using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using SkarpSharp.Model;

namespace SkarpSharp.Syntax;

/// <summary>Reads the skarp suppression attributes of a parsed file.</summary>
public static class SuppressionScan
{
    public const string Category = "skarp";

    static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        "SuppressMessage", "SuppressMessageAttribute", "UnconditionalSuppressMessage", "UnconditionalSuppressMessageAttribute",
    };

    /// <summary>The skarp suppressions of a file; null when there are none. Attributes inside method bodies (local
    /// functions) are not read.</summary>
    public static List<SuppressionAttribute>? Scan(CompilationUnitSyntax root, SourceText text)
    {
        List<SuppressionAttribute>? list = null;
        foreach (var node in root.DescendantNodes(n => n is not (BlockSyntax or ArrowExpressionClauseSyntax or EqualsValueClauseSyntax)))
        {
            if (node is not AttributeSyntax attribute || !IsSuppressMessage(attribute.Name)) continue;
            if (Read(attribute, text) is { } s) (list ??= []).Add(s);
        }
        return list;
    }

    /// <summary><c>SuppressMessage</c>, <c>SuppressMessageAttribute</c> and the <c>Unconditional</c> forms, plain or
    /// qualified with <c>System.Diagnostics.CodeAnalysis</c> (optionally <c>global::</c>).</summary>
    static bool IsSuppressMessage(NameSyntax name)
    {
        switch (name)
        {
            case IdentifierNameSyntax id: return Names.Contains(id.Identifier.ValueText);
            case QualifiedNameSyntax q:
                var left = q.Left.ToString().Replace(" ", "", StringComparison.Ordinal);
                if (left.StartsWith("global::", StringComparison.Ordinal)) left = left["global::".Length..];
                return q.Right is IdentifierNameSyntax r && Names.Contains(r.Identifier.ValueText) && left == "System.Diagnostics.CodeAnalysis";
            case AliasQualifiedNameSyntax a: return Names.Contains(a.Name.Identifier.ValueText) && a.Alias.Identifier.ValueText == "global";
            default: return false;
        }
    }

    /// <summary>The literal arguments of a SuppressMessage attribute (null when absent or not a string literal).</summary>
    sealed class Arguments
    {
        public string? Category, CheckId, Justification, Target, Scope;

        public static Arguments Of(AttributeSyntax attribute)
        {
            var a = new Arguments();
            int positional = 0;
            foreach (var arg in attribute.ArgumentList?.Arguments ?? default)
            {
                var value = Literal(arg.Expression);
                var name = arg.NameEquals?.Name.Identifier.ValueText ?? arg.NameColon?.Name.Identifier.ValueText ?? (positional++ == 0 ? "category" : "checkId");
                switch (name)
                {
                    case "category": a.Category = value; break;
                    case "checkId": a.CheckId = value; break;
                    case "Justification": a.Justification = value ?? ""; break;
                    case "Target": a.Target = value; break;
                    case "Scope": a.Scope = value; break;
                }
            }
            return a;
        }

        /// <summary>The rule id of a skarp suppression (category <c>skarp</c> or <c>skarp.*</c>; the check id up to a ':'); null otherwise.</summary>
        public string? SkarpRule()
        {
            if (Category is null || CheckId is null) return null;
            if (Category != Category0 && !Category.StartsWith(Category0 + ".", StringComparison.Ordinal)) return null;
            int colon = CheckId.IndexOf(':');
            return (colon >= 0 ? CheckId[..colon] : CheckId).Trim();
        }

        const string Category0 = SuppressionScan.Category;
    }

    static SuppressionAttribute? Read(AttributeSyntax attribute, SourceText text)
    {
        var args = Arguments.Of(attribute);
        if (args.SkarpRule() is not { } rule) return null;
        var list = attribute.Parent as AttributeListSyntax;
        int line = text.Lines.IndexOf(attribute.SpanStart) + 1;
        if (list?.Target?.Identifier.Kind() is SyntaxKind.AssemblyKeyword or SyntaxKind.ModuleKeyword)
            return new SuppressionAttribute { Rule = rule, Justification = args.Justification, Line = line, Place = args.Target ?? "assembly", Target = args.Target, Scope = args.Scope, Assembly = true };
        if (list?.Parent is not { } owner || owner is not (MemberDeclarationSyntax or AccessorDeclarationSyntax)) return null;
        return new SuppressionAttribute
        {
            Rule = rule, Justification = args.Justification, Line = line, Place = PlaceName(owner),
            StartLine = text.Lines.IndexOf(owner.SpanStart) + 1, EndLine = text.Lines.IndexOf(owner.Span.End) + 1,
        };
    }

    /// <summary>The text of a string literal (also a parenthesized one); null for anything else (not evaluated).</summary>
    static string? Literal(ExpressionSyntax e) => e switch
    {
        LiteralExpressionSyntax l when l.IsKind(SyntaxKind.StringLiteralExpression) => l.Token.ValueText,
        ParenthesizedExpressionSyntax p => Literal(p.Expression),
        _ => null,
    };

    static string PlaceName(SyntaxNode owner)
    {
        var type = string.Join('.', owner.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.ValueText));
        string? member = owner switch
        {
            BaseTypeDeclarationSyntax => null,
            MethodDeclarationSyntax m => m.Identifier.ValueText,
            ConstructorDeclarationSyntax => "ctor",
            PropertyDeclarationSyntax p => p.Identifier.ValueText,
            EventDeclarationSyntax e => e.Identifier.ValueText,
            IndexerDeclarationSyntax => "this[]",
            BaseFieldDeclarationSyntax f => string.Join(", ", f.Declaration.Variables.Select(v => v.Identifier.ValueText)),
            AccessorDeclarationSyntax a => PlaceName(a.Parent!.Parent!)[(type.Length + 1)..] + "." + a.Keyword.ValueText,
            DelegateDeclarationSyntax d => d.Identifier.ValueText,
            _ => owner.Kind().ToString(),
        };
        return member is null ? "type " + type : type.Length == 0 ? member : type + "." + member;
    }
}
