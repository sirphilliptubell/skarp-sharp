using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using SkarpSharp.Model;

namespace SkarpSharp.Clones;

/// <summary>What the syntax tier collects for duplicate-code detection: nothing, tokens (Type 1/2), or tokens
/// and method units (Type 3 as well).</summary>
public enum CloneMode { None, Tokens, TokensAndUnits }

/// <summary>Token flags of the clone token stream.</summary>
public static class TokenFlags
{
    /// <summary>A run break before this token (file start, an excluded using directive, attribute list or namespace part).</summary>
    public const byte Break = 1;
    /// <summary>First token of a statement, member, accessor, switch section, else, catch, finally or switch arm.</summary>
    public const byte Start = 2;
    /// <summary>Last token of such a node.</summary>
    public const byte End = 4;
    /// <summary>An identifier (Baker-encoded at match time).</summary>
    public const byte Ident = 8;
    /// <summary>First token of a method, accessor, constructor or operator with a body.</summary>
    public const byte MemberFirst = 16;
    /// <summary>Last token of such a member.</summary>
    public const byte MemberLast = 32;
}

/// <summary>Stable 64-bit hashes (no randomized string hashing: values must be the same in every run and build).</summary>
public static class StableHash
{
    public static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    public static ulong Text(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
        return Mix(h);
    }

    public static ulong Combine(ulong h, ulong v) => Mix((h * 0x100000001B3UL) ^ v);
}

/// <summary>Builds the clone token stream and the Type 3 units of one parsed file.</summary>
public static class CloneTokens
{
    /// <summary>Type 3 units need at least this many syntax nodes.</summary>
    public const int MinUnitNodes = 20;

    public static CloneFile Collect(SyntaxTree tree, SourceText text, bool units)
    {
        var root = tree.GetCompilationUnitRoot();
        var b = new Builder(text);
        b.Walk(root);
        return b.Finish(units ? CollectUnits(root, text, b.Positions) : null);
    }

    /// <summary>One pass over the tree: kept tokens with their values, then boundary and body flags from the node spans.</summary>
    sealed class Builder(SourceText text)
    {
        readonly List<ulong> _values = new(text.Length / 5 + 16), _names = new(text.Length / 5 + 16);
        readonly List<uint> _raw = new(text.Length / 5 + 16);
        readonly List<int> _lines = [], _endLines = [];
        readonly List<byte> _flags = [];
        readonly List<TextSpan> _boundaries = [];
        readonly List<(TextSpan Span, string Name)> _bodies = [];
        public List<int> Positions { get; } = [];
        bool _pendingBreak = true;

        public void Walk(CompilationUnitSyntax root)
        {
            foreach (var item in root.DescendantNodesAndTokens(n => !Excluded(n)))
            {
                if (item.IsNode) Node(item.AsNode()!);
                else Token(item.AsToken());
            }
        }

        void Node(SyntaxNode n)
        {
            if (Excluded(n)) { _pendingBreak = true; return; }
            if (IsBoundary(n)) _boundaries.Add(n.Span);
            if (BodyName(n) is { } name) _bodies.Add((n.Span, name));
        }

        void Token(SyntaxToken t)
        {
            if (t.IsKind(SyntaxKind.EndOfFileToken) || t.Span.Length == 0) return;
            if (t.Parent is BaseNamespaceDeclarationSyntax && t.Kind() is SyntaxKind.NamespaceKeyword or SyntaxKind.OpenBraceToken or SyntaxKind.CloseBraceToken or SyntaxKind.SemicolonToken)
            {
                _pendingBreak = true;
                return;
            }
            var (value, name, ident) = Value(t);
            byte f = ident ? TokenFlags.Ident : (byte)0;
            if (_pendingBreak) { f |= TokenFlags.Break; _pendingBreak = false; }
            _values.Add(value);
            _names.Add(name);
            _raw.Add((uint)StableHash.Text(t.Text));
            _lines.Add(text.Lines.IndexOf(t.SpanStart) + 1);
            _endLines.Add(text.Lines.IndexOf(t.Span.End) + 1);
            Positions.Add(t.SpanStart);
            _flags.Add(f);
        }

        public CloneFile Finish(List<CloneUnit>? units)
        {
            var flags = _flags.ToArray();
            foreach (var span in _boundaries)
            {
                var (a, b) = Range(Positions, span);
                if (a > b) continue;
                flags[a] |= TokenFlags.Start;
                flags[b] |= TokenFlags.End;
            }
            var members = new int[flags.Length];
            for (int m = 0; m < _bodies.Count; m++)
            {
                var (a, b) = Range(Positions, _bodies[m].Span);
                if (a > b) continue;
                for (int k = a; k <= b; k++) members[k] = m + 1;
                flags[a] |= TokenFlags.MemberFirst;
                flags[b] |= TokenFlags.MemberLast;
            }
            return new CloneFile
            {
                Values = [.. _values], Raw = [.. _raw], Previous = PreviousOccurrences(flags), Lines = [.. _lines], EndLines = [.. _endLines],
                Flags = flags, Members = members, MemberNames = [.. _bodies.Select(b => b.Name)], Units = units,
            };
        }

        int[] PreviousOccurrences(byte[] flags)
        {
            var previous = new int[flags.Length];
            var last = new Dictionary<ulong, int>();
            for (int k = 0; k < flags.Length; k++)
            {
                previous[k] = -1;
                if ((flags[k] & TokenFlags.Ident) == 0) continue;
                if (last.TryGetValue(_names[k], out var p)) previous[k] = p;
                last[_names[k]] = k;
            }
            return previous;
        }
    }

    /// <summary>The first and last token index inside a span (a &gt; b when none).</summary>
    static (int First, int Last) Range(List<int> positions, TextSpan span) =>
        (LowerBound(positions, span.Start), LowerBound(positions, span.End) - 1);

    static int LowerBound(List<int> a, int v)
    {
        int lo = 0, hi = a.Count;
        while (lo < hi)
        {
            int m = (lo + hi) >> 1;
            if (a[m] < v) lo = m + 1; else hi = m;
        }
        return lo;
    }

    /// <summary>Left out of matching: using directives, extern aliases, attribute lists and namespace names.</summary>
    static bool Excluded(SyntaxNode n) =>
        n is UsingDirectiveSyntax or ExternAliasDirectiveSyntax or AttributeListSyntax
        || n.Parent is BaseNamespaceDeclarationSyntax ns && ns.Name == n;

    static bool IsBoundary(SyntaxNode n) => n is StatementSyntax or MemberDeclarationSyntax or AccessorDeclarationSyntax or SwitchSectionSyntax
        or ElseClauseSyntax or CatchClauseSyntax or FinallyClauseSyntax or SwitchExpressionArmSyntax;

    static readonly ulong IdentifierWord = StableHash.Mix(30000UL);
    static readonly ulong LiteralBase = 20000UL;

    /// <summary>The normalized value: keywords, operators and punctuation by kind; literals by literal kind; identifiers by
    /// name (Baker-encoded later), except <c>var</c>, <c>nameof</c> and <c>dynamic</c>, which stay words.</summary>
    static (ulong Value, ulong Name, bool Ident) Value(SyntaxToken t)
    {
        var k = t.Kind();
        if (k == SyntaxKind.IdentifierToken)
        {
            var v = t.ValueText;
            if (v is "var" or "nameof" or "dynamic") return (IdentifierWord ^ StableHash.Text(v), 0, false);
            return (0, StableHash.Text(v), true);
        }
        if (IsLiteral(k)) return (StableHash.Mix(LiteralBase + (ulong)k), 0, false);
        return (KindValue(k), 0, false);
    }

    /// <summary>The normalized value of a keyword, operator or punctuation token of this kind.</summary>
    public static ulong KindValue(SyntaxKind k) => StableHash.Mix((ulong)k);

    public static bool IsLiteral(SyntaxKind k) => k is SyntaxKind.NumericLiteralToken or SyntaxKind.StringLiteralToken or SyntaxKind.CharacterLiteralToken
        or SyntaxKind.InterpolatedStringTextToken or SyntaxKind.SingleLineRawStringLiteralToken or SyntaxKind.MultiLineRawStringLiteralToken
        or SyntaxKind.Utf8StringLiteralToken or SyntaxKind.Utf8SingleLineRawStringLiteralToken or SyntaxKind.Utf8MultiLineRawStringLiteralToken;

    /// <summary>The short name of a method, constructor, operator or accessor with a body (<c>Type.Method</c>); null otherwise.</summary>
    static string? BodyName(SyntaxNode n) => n switch
    {
        BaseMethodDeclarationSyntax m when m.Body is not null || m.ExpressionBody is not null => TypeOf(m) + "." + MemberName(m),
        AccessorDeclarationSyntax a when a.Body is not null || a.ExpressionBody is not null => TypeOf(a) + "." + AccessorOwner(a) + "." + a.Keyword.ValueText,
        _ => null,
    };

    static string MemberName(BaseMethodDeclarationSyntax m) => m switch
    {
        MethodDeclarationSyntax md => md.Identifier.ValueText,
        ConstructorDeclarationSyntax => "ctor",
        DestructorDeclarationSyntax => "Finalize",
        OperatorDeclarationSyntax op => "operator " + op.OperatorToken.ValueText,
        ConversionOperatorDeclarationSyntax co => "operator " + co.Type,
        _ => m.Kind().ToString(),
    };

    static string AccessorOwner(AccessorDeclarationSyntax a) => a.Parent?.Parent switch
    {
        PropertyDeclarationSyntax p => p.Identifier.ValueText,
        IndexerDeclarationSyntax => "this[]",
        EventDeclarationSyntax e => e.Identifier.ValueText,
        _ => "?",
    };

    /// <summary>The containing type's short name, nested types joined with '.'.</summary>
    static string TypeOf(SyntaxNode n)
    {
        var parts = new List<string>();
        for (var p = n.Parent; p is not null; p = p.Parent)
            if (p is BaseTypeDeclarationSyntax t) parts.Add(t.Identifier.ValueText);
        parts.Reverse();
        return parts.Count == 0 ? "<top-level>" : string.Join('.', parts);
    }

    // ---- Type 3 units ----------------------------------------------------------------------------------------------

    static readonly ulong IdentifierMarker = StableHash.Mix(777777UL);

    static List<CloneUnit> CollectUnits(CompilationUnitSyntax root, SourceText text, List<int> positions)
    {
        var list = new List<CloneUnit>();
        foreach (var node in root.DescendantNodes(n => n is not (BaseMethodDeclarationSyntax or AccessorDeclarationSyntax)))
        {
            if (BodyName(node) is not { } name) continue;
            var fingerprints = new List<ulong>();
            var (_, size) = Fingerprint(node, fingerprints);
            if (size < MinUnitNodes) continue;
            var tokenLines = new HashSet<int>();
            foreach (var t in node.DescendantTokens(n => n is not AttributeListSyntax))
                if (t.Span.Length > 0) tokenLines.Add(text.Lines.IndexOf(t.SpanStart));
            var (first, last) = Range(positions, node.Span);
            list.Add(new CloneUnit
            {
                Name = name, StartLine = text.Lines.IndexOf(node.SpanStart) + 1, EndLine = text.Lines.IndexOf(node.Span.End) + 1,
                Lines = tokenLines.Count, Nodes = size, Fingerprints = [.. fingerprints],
                FirstToken = first <= last ? first : -1, LastToken = first <= last ? last : -1, Forwarding = IsForwarding(node),
            });
        }
        return list;
    }

    /// <summary>A name kept as text in fingerprints: called members, accessed members, qualified names, type arguments
    /// and names in a type-only position; other identifiers become one marker.</summary>
    static bool KeepName(SyntaxToken t)
    {
        if (t.Parent is not SimpleNameSyntax nm) return false;
        switch (nm.Parent)
        {
            case MemberAccessExpressionSyntax ma when ma.Name == nm: return true;
            case MemberBindingExpressionSyntax: return true;
            case InvocationExpressionSyntax inv when inv.Expression == nm: return true;
            case QualifiedNameSyntax: return true;
            case TypeArgumentListSyntax: return true;
        }
        return SyntaxFacts.IsInTypeOnlyContext(nm);
    }

    /// <summary>Hash of a subtree (node kind, children, kept names and keywords); every subtree of 3 or more nodes is one
    /// element of the unit's multiset. Literal text and punctuation are dropped.</summary>
    static (ulong Hash, int Size) Fingerprint(SyntaxNode n, List<ulong> output)
    {
        ulong h = StableHash.Mix(1000UL + (ulong)n.RawKind);
        int size = 1;
        foreach (var child in n.ChildNodesAndTokens())
        {
            if (child.IsNode)
            {
                var c = child.AsNode()!;
                if (c is AttributeListSyntax) continue;
                var (ch, cs) = Fingerprint(c, output);
                h = StableHash.Combine(h, ch);
                size += cs;
                continue;
            }
            var t = child.AsToken();
            var k = t.Kind();
            if (k == SyntaxKind.IdentifierToken) h = StableHash.Combine(h, KeepName(t) ? StableHash.Text(t.ValueText) : IdentifierMarker);
            else if (SyntaxFacts.IsKeywordKind(k)) h = StableHash.Combine(h, StableHash.Mix((ulong)k));
        }
        if (size >= 3) output.Add(h);
        return (h, size);
    }

    /// <summary>
    /// True when the body only forwards: after leading guard statements (an <c>if</c> whose body throws or returns, or a
    /// <c>ThrowIf...</c> call), one statement that returns or evaluates a call, an awaited call or an object creation.
    /// </summary>
    static bool IsForwarding(SyntaxNode member)
    {
        var (block, expression) = member switch
        {
            BaseMethodDeclarationSyntax m => (m.Body, m.ExpressionBody?.Expression),
            AccessorDeclarationSyntax a => (a.Body, a.ExpressionBody?.Expression),
            _ => (null, null),
        };
        if (expression is not null) return IsCall(expression);
        if (block is null) return false;
        var statements = block.Statements.SkipWhile(IsGuard).ToList();
        if (statements.Count != 1) return false;
        return statements[0] switch
        {
            ReturnStatementSyntax { Expression: { } e } => IsCall(e),
            ExpressionStatementSyntax e => IsCall(e.Expression),
            _ => false,
        };
    }

    static bool IsCall(ExpressionSyntax e)
    {
        while (true)
        {
            switch (e)
            {
                case AwaitExpressionSyntax a: e = a.Expression; continue;
                case ParenthesizedExpressionSyntax p: e = p.Expression; continue;
                case CastExpressionSyntax c: e = c.Expression; continue;
                case AssignmentExpressionSyntax asg: e = asg.Right; continue;
                case InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax: return true;
                case ConditionalAccessExpressionSyntax ca: return ca.WhenNotNull is InvocationExpressionSyntax;
                default: return false;
            }
        }
    }

    static bool IsGuard(StatementSyntax s) => s switch
    {
        IfStatementSyntax { Else: null } i => i.Statement is ThrowStatementSyntax or ReturnStatementSyntax
            || i.Statement is BlockSyntax { Statements: [ThrowStatementSyntax or ReturnStatementSyntax] },
        ExpressionStatementSyntax { Expression: InvocationExpressionSyntax inv } => inv.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText.StartsWith("ThrowIf", StringComparison.Ordinal),
            IdentifierNameSyntax id => id.Identifier.ValueText.StartsWith("ThrowIf", StringComparison.Ordinal),
            _ => false,
        },
        _ => false,
    };
}
