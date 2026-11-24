using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SkarpSharp.Conditionals;

/// <summary>One section of an <c>#if</c> chain: the text after an <c>#if</c>, <c>#elif</c> or <c>#else</c>.</summary>
public sealed class ConditionalBranch
{
    /// <summary>Position among the file's <c>#if</c>/<c>#elif</c>/<c>#else</c> directives in text order.</summary>
    public int Index { get; init; }
    /// <summary>The branch this chain is nested in, or -1 at file level.</summary>
    public int Parent { get; init; } = -1;
    /// <summary>The earlier branches of the same chain (an <c>#elif</c> or <c>#else</c> is taken only when none of them is).</summary>
    public IReadOnlyList<int> Priors { get; init; } = [];
    /// <summary>The condition; null for <c>#else</c>.</summary>
    public ExpressionSyntax? Condition { get; init; }
    /// <summary>Line of the directive (1-based).</summary>
    public int Line { get; init; }
    /// <summary>Line of the next directive of the chain (<c>#elif</c>, <c>#else</c> or <c>#endif</c>; 1-based): the branch's
    /// text is the lines strictly between <see cref="Line"/> and this.</summary>
    public int EndLine { get; internal set; }
    /// <summary>The directive as written, trimmed (for the report of unreachable branches).</summary>
    public string Text { get; init; } = "";
    /// <summary>Names in the condition (for <c>#else</c>: the names of the earlier conditions of the chain).</summary>
    public HashSet<string> Mentions { get; } = new(StringComparer.Ordinal);
    /// <summary>Every name whose value decides whether the branch is taken: its own and its chain's conditions, the
    /// enclosing branches' and, for a name the file <c>#define</c>s under a condition, that condition's names.</summary>
    public HashSet<string> PathSymbols { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The conditional directives of one file and an evaluator of them with Roslyn's semantics: an <c>#if</c> is taken when its enclosing branch is taken and its condition is true; an <c>#elif</c> or
/// <c>#else</c> also needs every earlier condition of its chain false; a <c>#define</c> or <c>#undef</c> applies in text order and only
/// where its enclosing branch is taken. Roslyn's <c>BranchTaken</c> is the oracle (tests, and a debug assertion).
/// </summary>
public sealed class DirectiveMap
{
    readonly List<ConditionalBranch> branches = [];
    /// <summary>Directive events in text order: a branch (Kind 0) or a <c>#define</c> (1) or <c>#undef</c> (2) with its name
    /// and the branch it sits in (-1 at file level).</summary>
    readonly List<(int Kind, int Branch, string? Name)> events = [];

    public IReadOnlyList<ConditionalBranch> Branches => branches;
    /// <summary>Names the file itself <c>#define</c>s or <c>#undef</c>s: not planning symbols.</summary>
    public HashSet<string> Defined { get; } = new(StringComparer.Ordinal);
    /// <summary>The planning symbols: every name in a condition except <c>true</c>, <c>false</c> and the file's own defines.</summary>
    public SortedSet<string> Symbols { get; } = new(StringComparer.Ordinal);
    /// <summary>An <c>#elif</c>, <c>#else</c> or <c>#endif</c> without its <c>#if</c>, or an <c>#if</c> without <c>#endif</c>:
    /// such a file gets no variants.</summary>
    public bool Unbalanced { get; private set; }

    /// <summary>The file's conditional directives, or null when it has no <c>#if</c>.</summary>
    public static DirectiveMap? Collect(SyntaxNode root)
    {
        if (!root.ContainsDirectives) return null;
        var map = new DirectiveMap();
        map.Walk(root, root.SyntaxTree.GetText());
        if (map.branches.Count == 0) return null;
        map.Finish();
        return map;
    }

    void Walk(SyntaxNode root, SourceText text)
    {
        var chains = new Stack<List<int>>();
        int current = -1;
        int LineOf(SyntaxNode d) => text.Lines.GetLineFromPosition(d.SpanStart).LineNumber + 1;
        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: false))
        {
            if (!trivia.IsDirective) continue;
            switch (trivia.GetStructure())
            {
                case IfDirectiveTriviaSyntax d:
                    current = Add(d.Condition, current, [], LineOf(d), d).Index;
                    chains.Push([current]);
                    break;
                case ElifDirectiveTriviaSyntax or ElseDirectiveTriviaSyntax when chains.Count == 0:
                    Unbalanced = true;
                    break;
                case ElifDirectiveTriviaSyntax or ElseDirectiveTriviaSyntax:
                {
                    var d = (DirectiveTriviaSyntax)trivia.GetStructure()!;
                    var chain = chains.Peek();
                    int line = LineOf(d);
                    branches[chain[^1]].EndLine = line;
                    var b = Add((d as ElifDirectiveTriviaSyntax)?.Condition, branches[chain[0]].Parent, [.. chain], line, d);
                    chain.Add(b.Index);
                    current = b.Index;
                    break;
                }
                case EndIfDirectiveTriviaSyntax d:
                {
                    if (chains.Count == 0) { Unbalanced = true; break; }
                    var chain = chains.Pop();
                    branches[chain[^1]].EndLine = LineOf(d);
                    current = branches[chain[0]].Parent;
                    break;
                }
                case DefineDirectiveTriviaSyntax d:
                    Defined.Add(d.Name.ValueText);
                    events.Add((1, current, d.Name.ValueText));
                    break;
                case UndefDirectiveTriviaSyntax d:
                    Defined.Add(d.Name.ValueText);
                    events.Add((2, current, d.Name.ValueText));
                    break;
            }
        }
        if (chains.Count > 0) Unbalanced = true;
    }

    ConditionalBranch Add(ExpressionSyntax? condition, int parent, List<int> priors, int line, SyntaxNode directive)
    {
        var b = new ConditionalBranch { Index = branches.Count, Parent = parent, Priors = priors, Condition = condition, Line = line, Text = directive.ToString().Trim() };
        if (condition is not null)
            foreach (var id in condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                b.Mentions.Add(id.Identifier.ValueText);
        branches.Add(b);
        events.Add((0, b.Index, null));
        return b;
    }

    void Finish()
    {
        foreach (var b in branches)
        {
            if (b.Condition is null) foreach (var p in b.Priors) b.Mentions.UnionWith(branches[p].Mentions);
            for (int k = b.Index; k >= 0; k = branches[k].Parent)
            {
                b.PathSymbols.UnionWith(branches[k].Mentions);
                foreach (var p in branches[k].Priors) b.PathSymbols.UnionWith(branches[p].Mentions);
            }
        }
        // A name #define'd inside a branch depends on that branch's names: add them to the paths that test the name
        // (a few rounds cover defines that depend on other defines).
        for (int round = 0; round < 3; round++)
            foreach (var b in branches)
                foreach (var (kind, inside, name) in events)
                    if (kind != 0 && inside >= 0 && b.PathSymbols.Contains(name!)) b.PathSymbols.UnionWith(branches[inside].PathSymbols);
        foreach (var b in branches)
            foreach (var s in b.Mentions)
                if (!Defined.Contains(s)) Symbols.Add(s);
    }

    /// <summary>Which branches are taken when exactly <paramref name="on"/> is defined (index = branch index).</summary>
    public bool[] Evaluate(IReadOnlySet<string> on)
    {
        var taken = new bool[branches.Count];
        var value = new bool[branches.Count];
        HashSet<string>? defines = Defined.Count == 0 ? null : new HashSet<string>(on, StringComparer.Ordinal);
        IReadOnlySet<string> current = defines ?? on;
        foreach (var (kind, index, name) in events)
        {
            if (kind == 0)
            {
                var b = branches[index];
                value[index] = b.Condition is null || Eval(b.Condition, current);
                bool reached = b.Parent < 0 || taken[b.Parent];
                foreach (var p in b.Priors) reached &= !value[p];
                taken[index] = reached && value[index];
            }
            else if (index < 0 || taken[index])
            {
                if (kind == 1) defines!.Add(name!);
                else defines!.Remove(name!);
            }
        }
        return taken;
    }

    /// <summary>Whether one branch is taken with <paramref name="on"/>: the full evaluation when the file has its own
    /// defines (they apply in text order), else only the branch's enclosing and earlier conditions.</summary>
    public bool IsTaken(int branch, IReadOnlySet<string> on)
    {
        if (Defined.Count > 0) return Evaluate(on)[branch];
        var b = branches[branch];
        if (b.Parent >= 0 && !IsTaken(b.Parent, on)) return false;
        foreach (var p in b.Priors) if (branches[p].Condition is not { } c || Eval(c, on)) return false;
        return b.Condition is null || Eval(b.Condition, on);
    }

    static bool Eval(ExpressionSyntax e, IReadOnlySet<string> on) => e switch
    {
        IdentifierNameSyntax id => on.Contains(id.Identifier.ValueText),
        LiteralExpressionSyntax l => l.IsKind(SyntaxKind.TrueLiteralExpression),
        ParenthesizedExpressionSyntax p => Eval(p.Expression, on),
        PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.LogicalNotExpression) => !Eval(u.Operand, on),
        BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression) => Eval(b.Left, on) && Eval(b.Right, on),
        BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalOrExpression) => Eval(b.Left, on) || Eval(b.Right, on),
        BinaryExpressionSyntax b when b.IsKind(SyntaxKind.EqualsExpression) => Eval(b.Left, on) == Eval(b.Right, on),
        BinaryExpressionSyntax b when b.IsKind(SyntaxKind.NotEqualsExpression) => Eval(b.Left, on) != Eval(b.Right, on),
        _ => false,
    };

    /// <summary>
    /// The branches whose taken state in <paramref name="tree"/> (Roslyn's <c>BranchTaken</c>) differs from this evaluator's
    /// for the same symbols; empty when they agree (the test oracle and debug assertion).
    /// </summary>
    public List<int> Mismatches(SyntaxTree tree, IReadOnlySet<string> on)
    {
        var expected = Evaluate(on);
        var wrong = new List<int>();
        int k = 0;
        foreach (var trivia in tree.GetRoot().DescendantTrivia(descendIntoTrivia: false))
        {
            if (!trivia.IsDirective) continue;
            bool? taken = trivia.GetStructure() switch
            {
                IfDirectiveTriviaSyntax d => d.BranchTaken,
                ElifDirectiveTriviaSyntax d => d.BranchTaken,
                ElseDirectiveTriviaSyntax d => d.BranchTaken,
                _ => null,
            };
            if (taken is not bool t) continue;
            if (k >= expected.Length || expected[k] != t) wrong.Add(k);
            k++;
        }
        for (; k < expected.Length; k++) wrong.Add(k);
        return wrong;
    }
}
