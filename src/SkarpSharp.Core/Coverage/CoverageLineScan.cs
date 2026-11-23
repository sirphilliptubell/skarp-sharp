using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using SkarpSharp.Metrics;
using SkarpSharp.Model;

namespace SkarpSharp.Coverage;

/// <summary>Computes a file's <see cref="CoverageLines"/> from its syntax tree and line kinds.</summary>
public static class CoverageLineScan
{
    public static CoverageLines Compute(SyntaxTree tree, LineKind[] lineKinds)
    {
        var text = tree.GetText();
        var root = tree.GetCompilationUnitRoot();
        int n = lineKinds.Length;
        var hasToken = new bool[n];
        var headerOnly = new bool[n];
        Array.Fill(headerOnly, true);
        foreach (var t in root.DescendantTokens())
        {
            if (t.IsKind(SyntaxKind.EndOfFileToken) || t.Span.Length == 0) continue;
            var (a, b) = Lines(text, t.Span, n);
            bool header = IsHeaderToken(t);
            for (int l = a; l <= b; l++) { hasToken[l] = true; headerOnly[l] &= header; }
        }
        var insideStatement = InsideMultiLineStatements(root, text, n);
        var kinds = new CoverageLineKind[n];
        for (int l = 0; l < n; l++)
        {
            kinds[l] = lineKinds[l] == LineKind.Inactive ? CoverageLineKind.Inactive
                : insideStatement[l] ? CoverageLineKind.Code
                : !hasToken[l] || headerOnly[l] ? CoverageLineKind.NonCode
                : CoverageLineKind.Code;
        }
        return new CoverageLines { Kinds = kinds, Excluded = ExcludedSpans(root, text) };
    }

    static (int A, int B) Lines(SourceText text, TextSpan span, int n)
    {
        int a = text.Lines.GetLineFromPosition(span.Start).LineNumber;
        int b = text.Lines.GetLineFromPosition(Math.Max(span.Start, span.End - 1)).LineNumber;
        return (Math.Min(a, n - 1), Math.Min(b, n - 1));
    }

    /// <summary>Lines after the first of a multi-line statement or expression: producers report every line of a
    /// multi-line sequence point (a switch expression body, an array initializer), also lines without a token.</summary>
    static bool[] InsideMultiLineStatements(CompilationUnitSyntax root, SourceText text, int n)
    {
        var inside = new bool[n];
        foreach (var node in root.DescendantNodes())
        {
            if (node is BlockSyntax || node is not (StatementSyntax or EqualsValueClauseSyntax or ArrowExpressionClauseSyntax or ConstructorInitializerSyntax or ExpressionSyntax)) continue;
            var (a, b) = Lines(text, node.Span, n);
            for (int l = a + 1; l < b; l++) inside[l] = true;
        }
        return inside;
    }

    /// <summary>
    /// Tokens that never carry a sequence point on their own line: using and namespace lines, attributes, <c>else</c>,
    /// parameter lists, constraints, base lists, and a method's modifiers, return type and name. Braces are not header
    /// tokens (async and iterator closing braces are coverable), nor constructor headers (the implicit base call), nor
    /// properties (Coverlet reports auto-property lines) or primary-constructor type lines (records).
    /// </summary>
    static bool IsHeaderToken(SyntaxToken t)
    {
        if (t.IsKind(SyntaxKind.ElseKeyword)) return true;
        if (t.Parent?.FirstAncestorOrSelf<ConstructorDeclarationSyntax>() is { Body: { } body, ExpressionBody: null } && !body.Span.Contains(t.Span)) return false;
        for (var n = t.Parent; n is not null; n = n.Parent)
        {
            switch (n)
            {
                case UsingDirectiveSyntax or BaseNamespaceDeclarationSyntax or AttributeListSyntax: return true;
                case ParameterListSyntax { Parent: BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or DelegateDeclarationSyntax }: return true;
                case TypeParameterConstraintClauseSyntax or TypeParameterListSyntax or BaseListSyntax: return true;
                case BlockSyntax or ArrowExpressionClauseSyntax or EqualsValueClauseSyntax or ConstructorInitializerSyntax or AccessorListSyntax: return false;
                case BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or BasePropertyDeclarationSyntax or BaseTypeDeclarationSyntax or DelegateDeclarationSyntax:
                    return !t.IsKind(SyntaxKind.OpenBraceToken) && !t.IsKind(SyntaxKind.CloseBraceToken)
                        && n is not TypeDeclarationSyntax { ParameterList: not null } and not BasePropertyDeclarationSyntax;
            }
        }
        return false;
    }

    /// <summary>Members and types with <c>[ExcludeFromCodeCoverage]</c> (any spelling of the attribute name).</summary>
    static List<(int, int)> ExcludedSpans(CompilationUnitSyntax root, SourceText text)
    {
        var spans = new List<(int, int)>();
        foreach (var node in root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax).OfType<MemberDeclarationSyntax>())
        {
            if (!node.AttributeLists.SelectMany(a => a.Attributes).Any(a => IsExcludeAttribute(a.Name.ToString()))) continue;
            var s = text.Lines.GetLinePositionSpan(node.Span);
            spans.Add((s.Start.Line + 1, s.End.Line + 1));
        }
        return spans;
    }

    static bool IsExcludeAttribute(string name)
    {
        int dot = name.LastIndexOf('.');
        var simple = dot >= 0 ? name[(dot + 1)..] : name;
        return simple is "ExcludeFromCodeCoverage" or "ExcludeFromCodeCoverageAttribute";
    }
}
