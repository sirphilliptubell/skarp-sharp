using SkarpSharp.Syntax;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SkarpSharp.Metrics;

/// <summary>
/// NPath: acyclic paths with early
/// exits, PMD 7.14+ semantics adapted to C#. Saturating 64-bit arithmetic. Nested bodies are separate records and
/// count 1 in their parent.
/// </summary>
public static class NPath
{
    public static long Compute(IReadOnlyList<SyntaxNode> roots, bool isExpressionBody)
    {
        if (isExpressionBody && roots.Count == 1 && roots[0] is ExpressionSyntax e)
            return Math.Max(1, E(e));
        var p = Sequence(roots);
        return Math.Max(1, Sat.Add(Sat.Add(p.N, p.X), Sat.Add(p.B, p.C)));
    }

    /// <summary>n: paths completing normally; x: paths leaving the member; b, c: paths leaving by break, continue.</summary>
    readonly record struct P(long N, long X, long B = 0, long C = 0)
    {
        public static readonly P One = new(1, 0);
    }

    static P Sequence(IEnumerable<SyntaxNode> statements)
    {
        long n = 1, x = 0, b = 0, c = 0;
        foreach (var s in statements)
        {
            var p = Stmt(s);
            x = Sat.Add(x, Sat.Mul(n, p.X));
            b = Sat.Add(b, Sat.Mul(n, p.B));
            c = Sat.Add(c, Sat.Mul(n, p.C));
            n = Sat.Mul(n, p.N);
        }
        return new P(n, x, b, c);
    }

    static P Stmt(SyntaxNode s)
    {
        switch (s)
        {
            case BlockSyntax block:
                return Sequence(block.Statements);
            case IfStatementSyntax ifs:
            {
                var (t, f) = Cond(ifs.Condition);
                var a = Stmt(ifs.Statement);
                var e = ifs.Else is null ? P.One : Stmt(ifs.Else.Statement);
                return new P(
                    Sat.Add(Sat.Mul(t, a.N), Sat.Mul(f, e.N)),
                    Sat.Add(Sat.Mul(t, a.X), Sat.Mul(f, e.X)),
                    Sat.Add(Sat.Mul(t, a.B), Sat.Mul(f, e.B)),
                    Sat.Add(Sat.Mul(t, a.C), Sat.Mul(f, e.C)));
            }
            case WhileStatementSyntax w:
                return Loop(Cond(w.Condition), Stmt(w.Statement));
            case ForStatementSyntax f:
                return Loop(f.Condition is null ? (1, 0) : Cond(f.Condition), Stmt(f.Statement));
            case CommonForEachStatementSyntax fe:
                return Loop((1, 1), Stmt(fe.Statement));
            case DoStatementSyntax d:
            {
                // Copied from PMD's documented behavior: the body runs once; loop-back (condition true) paths are
                // counted as extra completed paths (the least verified rule).
                var (t, f) = Cond(d.Condition);
                var body = Stmt(d.Statement);
                var through = Sat.Add(body.N, body.C);
                return new P(Sat.Add(Sat.Mul(through, Sat.Add(t, f)), body.B), body.X);
            }
            case SwitchStatementSyntax sw:
            {
                long n = 0, x = 0, c = 0;
                bool hasDefault = false;
                foreach (var section in sw.Sections)
                {
                    long labels = 0;
                    foreach (var label in section.Labels)
                    {
                        if (label is DefaultSwitchLabelSyntax) { hasDefault = true; labels++; }
                        else if (label is CasePatternSwitchLabelSyntax cp) labels += Alternatives(cp.Pattern);
                        else labels++;
                    }
                    var p = Sequence(section.Statements);
                    n = Sat.Add(n, Sat.Mul(labels, Sat.Add(p.N, p.B)));
                    x = Sat.Add(x, Sat.Mul(labels, p.X));
                    c = Sat.Add(c, Sat.Mul(labels, p.C));
                }
                if (!hasDefault) n = Sat.Add(n, 1);
                return new P(n, x, 0, c);
            }
            case TryStatementSyntax tr:
            {
                var t = Stmt(tr.Block);
                long n = t.N, x = t.X, b = t.B, c = t.C;
                foreach (var cc in tr.Catches)
                {
                    var p = Stmt(cc.Block);
                    n = Sat.Add(n, p.N); x = Sat.Add(x, p.X); b = Sat.Add(b, p.B); c = Sat.Add(c, p.C);
                }
                if (tr.Finally is null) return new P(n, x, b, c);
                var fin = Stmt(tr.Finally.Block);
                return new P(Sat.Mul(n, fin.N), Sat.Add(Sat.Mul(x, Math.Max(1, fin.N)), fin.X), Sat.Mul(b, fin.N), Sat.Mul(c, fin.N));
            }
            case ReturnStatementSyntax r:
                return new P(0, r.Expression is null ? 1 : E(r.Expression));
            case ThrowStatementSyntax th:
                return new P(0, th.Expression is null ? 1 : E(th.Expression));
            case YieldStatementSyntax y when y.IsKind(SyntaxKind.YieldBreakStatement):
                return new P(0, 1);
            case BreakStatementSyntax:
                return new P(0, 0, 1, 0);
            case ContinueStatementSyntax:
                return new P(0, 0, 0, 1);
            case LocalFunctionStatementSyntax:
                return P.One;
            case LabeledStatementSyntax l:
                return Stmt(l.Statement);
            case UsingStatementSyntax u:
                return Mul(u.Expression is null ? 1 : E(u.Expression), Stmt(u.Statement));
            case LockStatementSyntax lk:
                return Mul(E(lk.Expression), Stmt(lk.Statement));
            case FixedStatementSyntax fx:
                return Stmt(fx.Statement);
            case CheckedStatementSyntax ck:
                return Stmt(ck.Block);
            case UnsafeStatementSyntax us:
                return Stmt(us.Block);
            default:
                // Expression statements, declarations, goto and other simple statements: paths through their expressions.
                return new P(ExprPaths(s), 0);
        }
    }

    static P Mul(long k, P p) => new(Sat.Mul(k, p.N), Sat.Mul(k, p.X), Sat.Mul(k, p.B), Sat.Mul(k, p.C));

    static P Loop((long t, long f) cond, P body) =>
        new(Sat.Add(cond.f, Sat.Mul(cond.t, Sat.Add(body.N, Sat.Add(body.B, body.C)))), Sat.Mul(cond.t, body.X));

    /// <summary>Paths through the expressions directly inside a simple statement.</summary>
    static long ExprPaths(SyntaxNode s)
    {
        long n = 1;
        foreach (var child in s.ChildNodes()) n = Sat.Mul(n, child is ExpressionSyntax e ? E(e) : ExprPaths(child));
        return n;
    }

    /// <summary>Paths through an expression evaluated for its value.</summary>
    static long E(ExpressionSyntax e)
    {
        e = SyntaxHelpers.StripParens(e);
        switch (e)
        {
            case AnonymousFunctionExpressionSyntax:
                return 1;
            case ConditionalExpressionSyntax c:
            {
                var (t, f) = Cond(c.Condition);
                return Sat.Add(Sat.Mul(t, E(c.WhenTrue)), Sat.Mul(f, E(c.WhenFalse)));
            }
            case BinaryExpressionSyntax b when SyntaxHelpers.IsLogical(b):
            {
                var (t, f) = Cond(b);
                return Sat.Add(t, f);
            }
            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.CoalesceExpression):
                return Sat.Add(E(b.Left), E(b.Right));
            case AssignmentExpressionSyntax a when a.IsKind(SyntaxKind.CoalesceAssignmentExpression):
                return Sat.Add(E(a.Left), E(a.Right));
            case ConditionalAccessExpressionSyntax ca:
                return Sat.Add(E(ca.Expression), E(ca.WhenNotNull));
            case SwitchExpressionSyntax sw:
            {
                long n = 0;
                foreach (var arm in sw.Arms)
                {
                    long alt = Alternatives(arm.Pattern);
                    long armPaths = E(arm.Expression);
                    if (arm.WhenClause is not null)
                    {
                        var (t, _) = Cond(arm.WhenClause.Condition);
                        armPaths = Sat.Mul(t, armPaths);
                    }
                    n = Sat.Add(n, Sat.Mul(alt, armPaths));
                }
                return Sat.Mul(E(sw.GoverningExpression), Math.Max(1, n));
            }
            default:
                return ExprPaths(e);
        }
    }

    /// <summary>(paths ending true, paths ending false) through a condition with short-circuit operators.</summary>
    static (long t, long f) Cond(ExpressionSyntax e)
    {
        e = SyntaxHelpers.StripParens(e);
        switch (e)
        {
            case PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.LogicalNotExpression):
            {
                var (t, f) = Cond(u.Operand);
                return (f, t);
            }
            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression):
            {
                var (ta, fa) = Cond(b.Left);
                var (tb, fb) = Cond(b.Right);
                return (Sat.Mul(ta, tb), Sat.Add(fa, Sat.Mul(ta, fb)));
            }
            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalOrExpression):
            {
                var (ta, fa) = Cond(b.Left);
                var (tb, fb) = Cond(b.Right);
                return (Sat.Add(ta, Sat.Mul(fa, tb)), Sat.Mul(fa, fb));
            }
            case IsPatternExpressionSyntax ip:
            {
                var k = E(ip.Expression);
                return (Sat.Mul(k, Alternatives(ip.Pattern)), k);
            }
            default:
            {
                var k = E(e);
                return (k, k);
            }
        }
    }

    /// <summary>Number of `or` alternatives in a pattern (each `or` adds one, matching the label count).</summary>
    static long Alternatives(PatternSyntax p) => p switch
    {
        ParenthesizedPatternSyntax pp => Alternatives(pp.Pattern),
        BinaryPatternSyntax b when b.IsKind(SyntaxKind.OrPattern) => Sat.Add(Alternatives(b.Left), Alternatives(b.Right)),
        _ => 1,
    };
}

/// <summary>Saturating arithmetic on non-negative longs (clamps at long.MaxValue).</summary>
public static class Sat
{
    public static long Add(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;
    public static long Mul(long a, long b) => a == 0 || b == 0 ? 0 : a > long.MaxValue / b ? long.MaxValue : a * b;
}
