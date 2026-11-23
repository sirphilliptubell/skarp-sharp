using SkarpSharp.Syntax;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SkarpSharp.Metrics;

/// <summary>
/// Statement count, Skarp's own rule set: every statement node except
/// blocks, empty statements and label wrappers (the labeled statement inside counts); an expression body counts 1.
/// A local function declaration counts 1; bodies of lambdas and local functions are their own records.
/// </summary>
public static class Statements
{
    public static int Compute(IReadOnlyList<SyntaxNode> roots, bool isExpressionBody)
    {
        if (isExpressionBody) return 1;
        int count = 0;
        foreach (var root in roots)
            foreach (var n in root.DescendantNodesAndSelf(n => !MemberBody.IsNestedBody(n)))
                if (n is StatementSyntax and not (BlockSyntax or EmptyStatementSyntax or LabeledStatementSyntax))
                    count++;
        return count;
    }
}
