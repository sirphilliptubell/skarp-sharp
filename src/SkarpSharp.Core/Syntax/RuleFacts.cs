using SkarpSharp.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SkarpSharp.Syntax;

/// <summary>
/// Syntax facts for the finding rules:
/// parameter count, nested blocks for bumpy road, boolean operators per expression, empty catch clauses and numeric
/// literals. Computed per member body in the existing per-file pass (no extra parse); facts cover the member's own code,
/// nested lambdas and local functions are their own records. Facts are neutral: thresholds and value lists are
/// applied by the rules.
/// </summary>
public static class RuleFacts
{
    /// <summary>Nested blocks are recorded from this depth on (the smallest bump depth a preset may use).</summary>
    public const int MinBlockDepth = 2;

    public static void Fill(MemberBody body, MemberResult r)
    {
        var tree = body.Declaration.SyntaxTree;
        int Line(int position) => tree.GetLineSpan(new Microsoft.CodeAnalysis.Text.TextSpan(position, 0)).StartLinePosition.Line + 1;

        (r.Parameters, r.InheritedSignature) = ParameterFacts(body);

        if (!body.IsExpressionBody) r.NestedBlocks = NestedBlocks(body.Roots, Line);

        int maxOps = 0, maxLine = 0;
        List<EmptyCatchFact>? catches = null;
        List<NumericLiteralFact>? literals = null;
        foreach (var root in body.Roots)
        {
            if (MemberBody.IsNestedBody(root)) continue; // a lambda whose body is another lambda: that one is its own record
            foreach (var n in root.DescendantNodesAndSelf(d => !MemberBody.IsNestedBody(d)))
            {
                if (MemberBody.IsNestedBody(n)) continue;
                switch (n)
                {
                    case CatchClauseSyntax c when c.Block.Statements.Count == 0:
                        (catches ??= []).Add(new EmptyCatchFact
                        {
                            Line = Line(c.SpanStart),
                            Caught = c.Declaration?.Type.ToString(),
                            HasComment = HasComment(c.Block),
                            HasFilter = c.Filter is not null,
                        });
                        break;
                    case LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.NumericLiteralExpression):
                        if (NumericLiteral(lit) is { } fact) { fact.Line = Line(lit.SpanStart); (literals ??= []).Add(fact); }
                        break;
                    default:
                        if (BranchCondition(n) is { } cond)
                        {
                            int ops = CountBooleanOperators(cond);
                            if (ops > maxOps) { maxOps = ops; maxLine = Line(cond.SpanStart); }
                        }
                        break;
                }
            }
        }
        if (maxOps > 0) { r.BooleanOperatorsMax = maxOps; r.BooleanOperatorsLine = maxLine; }
        r.EmptyCatches = catches;
        r.NumericLiterals = literals;
    }

    // ---- Parameters -------------------------------------------------------------------------------------------------

    static (int?, bool?) ParameterFacts(MemberBody body)
    {
        var d = body.Declaration;
        int? count = d switch
        {
            BaseMethodDeclarationSyntax m => Count(m.ParameterList),
            LocalFunctionStatementSyntax lf => Count(lf.ParameterList),
            ParenthesizedLambdaExpressionSyntax pl => pl.ParameterList.Parameters.Count,
            SimpleLambdaExpressionSyntax => 1,
            AnonymousMethodExpressionSyntax am => am.ParameterList?.Parameters.Count ?? 0,
            AccessorDeclarationSyntax { Parent.Parent: IndexerDeclarationSyntax ix } => Count(ix.ParameterList),
            IndexerDeclarationSyntax ix => Count(ix.ParameterList),
            _ => null,
        };
        var owner = d is AccessorDeclarationSyntax { Parent.Parent: BasePropertyDeclarationSyntax bp } ? bp : d as MemberDeclarationSyntax;
        bool inherited = owner is not null && (owner.Modifiers.Any(SyntaxKind.OverrideKeyword) || owner switch
        {
            MethodDeclarationSyntax m => m.ExplicitInterfaceSpecifier is not null,
            BasePropertyDeclarationSyntax p => p.ExplicitInterfaceSpecifier is not null,
            _ => false,
        });
        return (count, inherited ? true : null);
    }

    /// <summary>Declared parameters; the receiver <c>this</c> of an extension method is not counted.</summary>
    public static int Count(BaseParameterListSyntax list)
    {
        int n = list.Parameters.Count;
        if (n > 0 && list.Parameters[0].Modifiers.Any(SyntaxKind.ThisKeyword)) n--;
        return n;
    }

    // ---- Bumpy road: nested blocks ----------------------------------------------------------------------------------

    static List<NestedBlockFact>? NestedBlocks(IReadOnlyList<SyntaxNode> roots, Func<int, int> line)
    {
        List<NestedBlockFact>? list = null;
        int seq = 0;
        void Scan(StatementSyntax s)
        {
            switch (s)
            {
                // Transparent wrappers: their statements count as top-level statements of the body.
                case BlockSyntax b: foreach (var x in b.Statements) Scan(x); return;
                case TryStatementSyntax t:
                    Scan(t.Block);
                    foreach (var c in t.Catches) Scan(c.Block);
                    if (t.Finally is not null) Scan(t.Finally.Block);
                    return;
                case UsingStatementSyntax u: Scan(u.Statement); return;
                case LockStatementSyntax l: Scan(l.Statement); return;
                case FixedStatementSyntax f: Scan(f.Statement); return;
                case CheckedStatementSyntax ch: Scan(ch.Block); return;
                case UnsafeStatementSyntax us: Scan(us.Block); return;
                case LabeledStatementSyntax lb: Scan(lb.Statement); return;
                case IfStatementSyntax or SwitchStatementSyntax or ForStatementSyntax or CommonForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax:
                    int depth = ControlDepth(s, 0);
                    if (depth >= MinBlockDepth)
                        (list ??= []).Add(new NestedBlockFact { Seq = seq, StartLine = line(s.SpanStart), EndLine = line(Math.Max(s.SpanStart, s.Span.End - 1)), Depth = depth });
                    seq++;
                    return;
                default:
                    seq++;
                    return;
            }
        }
        foreach (var r in roots)
        {
            if (r is StatementSyntax st) Scan(st);
        }
        return list;
    }

    /// <summary>
    /// Deepest conditional or loop nesting inside a statement, the statement itself at <paramref name="level"/> + 1:
    /// if (else-if flat), switch and loops open a level; try, using, lock and blocks do not; nested bodies are skipped.
    /// </summary>
    static int ControlDepth(SyntaxNode node, int level)
    {
        int max = level;
        void Visit(SyntaxNode n, int lv)
        {
            if (MemberBody.IsNestedBody(n)) return;
            switch (n)
            {
                case IfStatementSyntax ifs:
                {
                    int inner = lv + 1;
                    max = Math.Max(max, inner);
                    Visit(ifs.Statement, inner);
                    var e = ifs.Else;
                    while (e is not null)
                    {
                        if (e.Statement is IfStatementSyntax elseIf) { Visit(elseIf.Statement, inner); e = elseIf.Else; }
                        else { Visit(e.Statement, inner); e = null; }
                    }
                    return;
                }
                case SwitchStatementSyntax sw:
                    max = Math.Max(max, lv + 1);
                    foreach (var section in sw.Sections) foreach (var st in section.Statements) Visit(st, lv + 1);
                    return;
                case ForStatementSyntax f: max = Math.Max(max, lv + 1); Visit(f.Statement, lv + 1); return;
                case CommonForEachStatementSyntax fe: max = Math.Max(max, lv + 1); Visit(fe.Statement, lv + 1); return;
                case WhileStatementSyntax w: max = Math.Max(max, lv + 1); Visit(w.Statement, lv + 1); return;
                case DoStatementSyntax d: max = Math.Max(max, lv + 1); Visit(d.Statement, lv + 1); return;
                case StatementSyntax:
                    foreach (var c in n.ChildNodes()) if (c is StatementSyntax or CatchClauseSyntax or FinallyClauseSyntax or SwitchSectionSyntax) Visit(c, lv);
                    return;
                case CatchClauseSyntax cc: Visit(cc.Block, lv); return;
                case FinallyClauseSyntax fc: Visit(fc.Block, lv); return;
                default: return;
            }
        }
        Visit(node, level);
        return max;
    }

    // ---- Complex condition: boolean operators per expression --------------------------------------------------------

    /// <summary>
    /// The branch condition a node carries: the condition of if, while, do, for and ?:, and the when clause of a
    /// switch-statement case, a switch-expression arm or a catch clause. Other boolean expressions (assignments, returns,
    /// arguments, lambda bodies) are not conditions for this rule.
    /// </summary>
    public static ExpressionSyntax? BranchCondition(SyntaxNode n) => n switch
    {
        IfStatementSyntax s => s.Condition,
        WhileStatementSyntax s => s.Condition,
        DoStatementSyntax s => s.Condition,
        ForStatementSyntax s => s.Condition,
        ConditionalExpressionSyntax c => c.Condition,
        WhenClauseSyntax w => w.Condition,
        CatchFilterClauseSyntax f => f.FilterExpression,
        _ => null,
    };

    /// <summary>
    /// Counts &amp;&amp; and || in a condition, across parentheses and negations, plus the and/or combinators of
    /// patterns in is-expressions (not counted: <c>not</c>, patterns of case labels and switch arms, sub-expressions in
    /// arguments or lambdas).
    /// </summary>
    public static int CountBooleanOperators(ExpressionSyntax e)
    {
        int count = 0;
        void Visit(SyntaxNode n)
        {
            switch (n)
            {
                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.LogicalOrExpression):
                    count++; Visit(b.Left); Visit(b.Right); return;
                case PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.LogicalNotExpression): Visit(u.Operand); return;
                case ParenthesizedExpressionSyntax p: Visit(p.Expression); return;
                case IsPatternExpressionSyntax ip: VisitPattern(ip.Pattern); return;
            }
        }
        void VisitPattern(PatternSyntax p)
        {
            switch (p)
            {
                case BinaryPatternSyntax bp: count++; VisitPattern(bp.Left); VisitPattern(bp.Right); return;
                case ParenthesizedPatternSyntax pp: VisitPattern(pp.Pattern); return;
                case UnaryPatternSyntax up: VisitPattern(up.Pattern); return;
            }
        }
        Visit(e);
        return count;
    }

    // ---- Empty catch ------------------------------------------------------------------------------------------------

    static bool HasComment(BlockSyntax block)
    {
        int start = block.OpenBraceToken.Span.End, end = block.CloseBraceToken.SpanStart;
        foreach (var t in block.OpenBraceToken.TrailingTrivia.Concat(block.CloseBraceToken.LeadingTrivia))
            if (t.SpanStart >= start && t.Span.End <= end && t.Kind() is SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia
                or SyntaxKind.SingleLineDocumentationCommentTrivia or SyntaxKind.MultiLineDocumentationCommentTrivia)
                return true;
        return false;
    }

    // ---- Magic numbers ----------------------------------------------------------------------------------------------

    static NumericLiteralFact? NumericLiteral(LiteralExpressionSyntax lit)
    {
        SyntaxNode node = lit;
        bool negative = false;
        if (node.Parent is PrefixUnaryExpressionSyntax neg && neg.IsKind(SyntaxKind.UnaryMinusExpression)) { negative = true; node = neg; }
        else if (node.Parent is PrefixUnaryExpressionSyntax plus && plus.IsKind(SyntaxKind.UnaryPlusExpression)) node = plus;

        double value = ToDouble(lit.Token.Value);
        if (!double.IsFinite(value)) return null; // malformed or overflowing literal (a compile error anyway)
        if (negative) value = -value;

        // Exempt positions (our C# adaptation of Checkstyle's constant definitions and Doomen's AV1515).
        for (var p = node.Parent; p is not null; p = p.Parent)
        {
            switch (p)
            {
                case LocalDeclarationStatementSyntax ld when ld.IsConst: return null;
                case AttributeArgumentSyntax: return null;
                case EqualsValueClauseSyntax { Parent: ParameterSyntax }: return null;
                case BracketedArgumentListSyntax: return null;
                case ArrayRankSpecifierSyntax: return null; // array size: new T[n], stackalloc T[n]
                case StatementSyntax or MemberDeclarationSyntax or AnonymousFunctionExpressionSyntax: goto done;
            }
        }
        done:
        var parent = node.Parent;
        while (parent is ParenthesizedExpressionSyntax) parent = parent.Parent;
        if (parent is BinaryExpressionSyntax bin && (bin.IsKind(SyntaxKind.MultiplyExpression) || bin.IsKind(SyntaxKind.DivideExpression)) && IsPowerOfTen(value))
            return null;
        return new NumericLiteralFact { Text = (negative ? "-" : "") + lit.Token.Text, Value = value };
    }

    static bool IsPowerOfTen(double v)
    {
        if (v < 10) return false;
        while (v >= 10 && v % 10 == 0) v /= 10;
        return v == 1;
    }

    static double ToDouble(object? v) => v switch
    {
        int i => i, uint u => u, long l => l, ulong ul => ul, float f => f, double d => d, decimal m => (double)m,
        short s => s, ushort us => us, byte b => b, sbyte sb => sb,
        _ => double.NaN,
    };
}
