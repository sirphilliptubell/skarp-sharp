using SkarpSharp.Syntax;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SkarpSharp.Metrics;

/// <summary>
/// Cognitive complexity, Skarp's own rule set.
/// Structural constructs add 1 + nesting level; else-if and else add 1; goto adds 1; each run of like boolean
/// operators adds 1 (pattern and/or form their own runs); direct recursion adds 1 once (syntactic: a call by the
/// member's own name; overloads are not told apart without symbols). Nested bodies fold and raise the level.
/// </summary>
public static class Cognitive
{
    public static int Compute(IReadOnlyList<SyntaxNode> roots, string? recursionName = null)
    {
        var w = new Walker(recursionName);
        foreach (var r in roots) w.Visit(r, 0);
        return w.Score + (w.Recursive ? 1 : 0);
    }

    sealed class Walker(string? recursionName)
    {
        public int Score;
        public bool Recursive;

        public void Visit(SyntaxNode node, int level)
        {
            switch (node)
            {
                case IfStatementSyntax ifs:
                    VisitIfChain(ifs, level);
                    return;
                case ConditionalExpressionSyntax c:
                    Score += 1 + level;
                    Visit(c.Condition, level);
                    Visit(c.WhenTrue, level + 1);
                    Visit(c.WhenFalse, level + 1);
                    return;
                case SwitchStatementSyntax s:
                    Score += 1 + level;
                    Visit(s.Expression, level);
                    foreach (var section in s.Sections) VisitChildren(section, level + 1);
                    return;
                case SwitchExpressionSyntax se:
                    Score += 1 + level;
                    Visit(se.GoverningExpression, level);
                    foreach (var arm in se.Arms) VisitChildren(arm, level + 1);
                    return;
                case ForStatementSyntax f:
                    Score += 1 + level;
                    if (f.Declaration is not null) Visit(f.Declaration, level);
                    foreach (var i in f.Initializers) Visit(i, level);
                    if (f.Condition is not null) Visit(f.Condition, level);
                    foreach (var i in f.Incrementors) Visit(i, level);
                    Visit(f.Statement, level + 1);
                    return;
                case CommonForEachStatementSyntax fe:
                    Score += 1 + level;
                    Visit(fe.Expression, level);
                    Visit(fe.Statement, level + 1);
                    return;
                case WhileStatementSyntax wh:
                    Score += 1 + level;
                    Visit(wh.Condition, level);
                    Visit(wh.Statement, level + 1);
                    return;
                case DoStatementSyntax d:
                    Score += 1 + level;
                    Visit(d.Statement, level + 1);
                    Visit(d.Condition, level);
                    return;
                case CatchClauseSyntax cc:
                    Score += 1 + level;
                    if (cc.Filter is not null) Visit(cc.Filter, level);
                    Visit(cc.Block, level + 1);
                    return;
                case GotoStatementSyntax:
                    Score += 1;
                    VisitChildren(node, level);
                    return;
                case AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax:
                    foreach (var r in MemberBody.NestedRoots(node)) Visit(r, level + 1);
                    return;
                case BinaryExpressionSyntax b when SyntaxHelpers.IsLogical(b) && !SyntaxHelpers.IsLogical(SyntaxHelpers.SkipParensUp(b)):
                    Score += Runs(b);
                    foreach (var leaf in LogicalLeaves(b)) Visit(leaf, level);
                    return;
                case BinaryPatternSyntax p when SyntaxHelpers.IsPatternCombinator(p) && !SyntaxHelpers.IsPatternCombinator(SyntaxHelpers.SkipParensUp(p)):
                    Score += Runs(p);
                    foreach (var leaf in PatternLeaves(p)) Visit(leaf, level);
                    return;
                case InvocationExpressionSyntax inv when recursionName is not null && IsSelfCall(inv, recursionName):
                    Recursive = true;
                    VisitChildren(node, level);
                    return;
                default:
                    VisitChildren(node, level);
                    return;
            }
        }

        void VisitChildren(SyntaxNode node, int level)
        {
            foreach (var child in node.ChildNodes()) Visit(child, level);
        }

        void VisitIfChain(IfStatementSyntax ifs, int level)
        {
            Score += 1 + level;
            Visit(ifs.Condition, level);
            Visit(ifs.Statement, level + 1);
            var e = ifs.Else;
            while (e is not null)
            {
                Score += 1; // else-if and else: hybrid increment, no nesting increment
                if (e.Statement is IfStatementSyntax elseIf)
                {
                    Visit(elseIf.Condition, level);
                    Visit(elseIf.Statement, level + 1);
                    e = elseIf.Else;
                }
                else
                {
                    Visit(e.Statement, level + 1);
                    e = null;
                }
            }
        }

        static bool IsSelfCall(InvocationExpressionSyntax inv, string name) => inv.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText == name,
            GenericNameSyntax g => g.Identifier.ValueText == name,
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } ma => ma.Name.Identifier.ValueText == name,
            _ => false,
        };
    }

    /// <summary>Runs of equal operators in source order; parentheses are transparent, '!' starts a new sequence.</summary>
    static int Runs(SyntaxNode root)
    {
        var ops = new List<SyntaxKind>();
        void Collect(SyntaxNode n)
        {
            switch (n)
            {
                case ParenthesizedExpressionSyntax pe: Collect(pe.Expression); break;
                case ParenthesizedPatternSyntax pp: Collect(pp.Pattern); break;
                case BinaryExpressionSyntax b when SyntaxHelpers.IsLogical(b):
                    Collect(b.Left); ops.Add(b.Kind()); Collect(b.Right); break;
                case BinaryPatternSyntax p when SyntaxHelpers.IsPatternCombinator(p):
                    Collect(p.Left); ops.Add(p.Kind()); Collect(p.Right); break;
            }
        }
        Collect(root);
        int runs = 0;
        for (int i = 0; i < ops.Count; i++) if (i == 0 || ops[i] != ops[i - 1]) runs++;
        return runs;
    }

    static IEnumerable<SyntaxNode> LogicalLeaves(SyntaxNode n) => n switch
    {
        ParenthesizedExpressionSyntax pe => LogicalLeaves(pe.Expression),
        BinaryExpressionSyntax b when SyntaxHelpers.IsLogical(b) => LogicalLeaves(b.Left).Concat(LogicalLeaves(b.Right)),
        _ => [n],
    };

    static IEnumerable<SyntaxNode> PatternLeaves(SyntaxNode n) => n switch
    {
        ParenthesizedPatternSyntax pp => PatternLeaves(pp.Pattern),
        BinaryPatternSyntax p when SyntaxHelpers.IsPatternCombinator(p) => PatternLeaves(p.Left).Concat(PatternLeaves(p.Right)),
        _ => [n],
    };
}
