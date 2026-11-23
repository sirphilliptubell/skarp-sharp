using SkarpSharp.Syntax;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SkarpSharp.Metrics;

/// <summary>
/// Cyclomatic complexity.
/// Default is Skarp's own rule set; <see cref="CyclomaticPreset.Microsoft"/> reproduces Microsoft.CodeAnalysis.Metrics
/// as far as syntax allows (bool &amp; and | are counted only when an operand is visibly boolean; see
/// <see cref="IsEvidentlyBool"/>). Checked against Microsoft.CodeAnalysis.Metrics 5.6.0.
/// Nested bodies fold into the member; their own sub-records start again from base 1.
/// </summary>
public static class Cyclomatic
{
    public static int Compute(IReadOnlyList<SyntaxNode> roots, CyclomaticPreset preset = CyclomaticPreset.Ours)
    {
        int count = 1;
        foreach (var root in roots)
            foreach (var n in root.DescendantNodesAndSelf())
                count += Decisions(n, preset);
        return count;
    }

    static int Decisions(SyntaxNode n, CyclomaticPreset preset)
    {
        bool ms = preset == CyclomaticPreset.Microsoft;
        switch (n)
        {
            case IfStatementSyntax:
            case ForStatementSyntax:
            case CommonForEachStatementSyntax:
            case WhileStatementSyntax:
            case DoStatementSyntax:
            case ConditionalExpressionSyntax:
            case ConditionalAccessExpressionSyntax:
            case CaseSwitchLabelSyntax:
            case CasePatternSwitchLabelSyntax:
                return 1;
            case DefaultSwitchLabelSyntax:
                return ms ? 1 : 0;
            case SwitchExpressionArmSyntax arm:
                return ms || arm.Pattern is DiscardPatternSyntax && arm.WhenClause is null ? 0 : 1;
            case CatchClauseSyntax:
            case WhenClauseSyntax:
            case CatchFilterClauseSyntax:
                return ms ? 0 : 1;
            case BinaryExpressionSyntax b:
                return b.Kind() switch
                {
                    SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression or SyntaxKind.CoalesceExpression => 1,
                    SyntaxKind.BitwiseAndExpression or SyntaxKind.BitwiseOrExpression => ms && IsEvidentlyBool(b) ? 1 : 0,
                    _ => 0,
                };
            case AssignmentExpressionSyntax a when a.IsKind(SyntaxKind.CoalesceAssignmentExpression):
                return ms ? 0 : 1;
            case BinaryPatternSyntax:
                return ms ? 0 : 1;
            default:
                return 0;
        }
    }

    /// <summary>
    /// Syntax-only approximation of Microsoft's "bool-typed non-short-circuit &amp; and |": true when the operator or one
    /// of its operands is visibly boolean (a comparison, !, &amp;&amp;, ||, true/false, an is-test, or another such
    /// &amp;, | or ^). Operands whose type only a compilation knows (bool locals, parameters, calls) are not resolved,
    /// so <c>a &amp; b</c> on two bool parameters stays uncounted.
    /// </summary>
    static bool IsEvidentlyBool(BinaryExpressionSyntax b) => IsBoolOperand(b.Left) || IsBoolOperand(b.Right);

    static bool IsBoolOperand(ExpressionSyntax e)
    {
        e = SyntaxHelpers.StripParens(e);
        return e.Kind() switch
        {
            SyntaxKind.LogicalNotExpression or SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression
                or SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression
                or SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression
                or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression
                or SyntaxKind.TrueLiteralExpression or SyntaxKind.FalseLiteralExpression
                or SyntaxKind.IsExpression or SyntaxKind.IsPatternExpression => true,
            SyntaxKind.BitwiseAndExpression or SyntaxKind.BitwiseOrExpression or SyntaxKind.ExclusiveOrExpression
                => IsEvidentlyBool((BinaryExpressionSyntax)e),
            _ => false,
        };
    }
}

public enum CyclomaticPreset { Ours, Microsoft }
