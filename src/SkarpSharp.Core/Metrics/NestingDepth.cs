using SkarpSharp.Syntax;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SkarpSharp.Metrics;

/// <summary>
/// Maximum nesting depth, Skarp's own rule set: openers are if (else-if flat,
/// a plain else shares the if level), switch statement, for, foreach, while, do and try (try, catch and finally bodies
/// share one level). The outermost opener is level 1; a member without openers is 0. Lambdas and local functions fold
/// and raise the level of their contents by one (an empty lambda adds nothing).
/// </summary>
public static class NestingDepth
{
    public static int Compute(IReadOnlyList<SyntaxNode> roots)
    {
        int max = 0;
        void Visit(SyntaxNode node, int level)
        {
            switch (node)
            {
                case IfStatementSyntax ifs:
                {
                    int inner = level + 1;
                    max = Math.Max(max, inner);
                    Visit(ifs.Condition, level);
                    Visit(ifs.Statement, inner);
                    var e = ifs.Else;
                    while (e is not null)
                    {
                        if (e.Statement is IfStatementSyntax elseIf)
                        {
                            Visit(elseIf.Condition, level);
                            Visit(elseIf.Statement, inner);
                            e = elseIf.Else;
                        }
                        else
                        {
                            Visit(e.Statement, inner);
                            e = null;
                        }
                    }
                    return;
                }
                case SwitchStatementSyntax s:
                    max = Math.Max(max, level + 1);
                    Visit(s.Expression, level);
                    foreach (var section in s.Sections) foreach (var child in section.ChildNodes()) Visit(child, level + 1);
                    return;
                case ForStatementSyntax or CommonForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax:
                {
                    max = Math.Max(max, level + 1);
                    var body = ((StatementSyntax)node) switch
                    {
                        ForStatementSyntax f => f.Statement,
                        CommonForEachStatementSyntax fe => fe.Statement,
                        WhileStatementSyntax w => w.Statement,
                        DoStatementSyntax d => d.Statement,
                        _ => null!,
                    };
                    foreach (var child in node.ChildNodes())
                        Visit(child, child == body ? level + 1 : level);
                    return;
                }
                case TryStatementSyntax t:
                    max = Math.Max(max, level + 1);
                    Visit(t.Block, level + 1);
                    foreach (var c in t.Catches)
                    {
                        if (c.Filter is not null) Visit(c.Filter, level);
                        Visit(c.Block, level + 1);
                    }
                    if (t.Finally is not null) Visit(t.Finally.Block, level + 1);
                    return;
                case AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax:
                    foreach (var r in MemberBody.NestedRoots(node)) Visit(r, level + 1);
                    return;
                default:
                    foreach (var child in node.ChildNodes()) Visit(child, level);
                    return;
            }
        }
        foreach (var r in roots) Visit(r, 0);
        return max;
    }
}
