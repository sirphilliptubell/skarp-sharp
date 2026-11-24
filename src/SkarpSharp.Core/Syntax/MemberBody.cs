using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SkarpSharp.Syntax;

/// <summary>Kinds of member records.</summary>
public enum MemberKind
{
    Method, Constructor, Destructor, Operator, Conversion,
    PropertyGet, PropertySet, PropertyInit, PropertyExpression,
    IndexerGet, IndexerSet, IndexerInit, IndexerExpression,
    EventAdd, EventRemove,
    LocalFunction, Lambda, TopLevel,
}

/// <summary>
/// One analyzable body: a member, accessor, local function, lambda or the top-level statements of a file.
/// <see cref="Roots"/> are the nodes the metrics walk (a block, an expression body, or global statements).
/// </summary>
public sealed class MemberBody
{
    public required string Id { get; init; }
    /// <summary>Simple name used by tests and reports: "M", ".ctor", "P.get", "M/Local", "M/lambda1".</summary>
    public required string Name { get; init; }
    public required MemberKind Kind { get; init; }
    public required string TypeName { get; init; }
    public string? ParentId { get; init; }
    /// <summary>Declaration node (for spans); for top-level statements the first global statement.</summary>
    public required SyntaxNode Declaration { get; init; }
    public required IReadOnlyList<SyntaxNode> Roots { get; init; }
    /// <summary>
    /// Code of the member outside its body: the argument list of a constructor initializer (<c>: base(...)</c>,
    /// <c>: this(...)</c>). Only cyclomatic complexity counts it (constructors include
    /// their initializers; Microsoft.CodeAnalysis.Metrics does the same). Empty for other members.
    /// </summary>
    public IReadOnlyList<SyntaxNode> InitializerRoots { get; init; } = [];
    /// <summary>True when the body is an expression (=> body or expression lambda).</summary>
    public required bool IsExpressionBody { get; init; }
    /// <summary>Identifier that a direct recursive call would use, or null (lambdas, accessors).</summary>
    public string? RecursionName { get; init; }
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    /// <summary>Identity key, set by <see cref="MemberCollector.AssignKeys"/>.</summary>
    public string Key { get; set; } = "";

    /// <summary>True for lambdas, anonymous methods and local functions: bodies nested in another member.</summary>
    public static bool IsNestedBody(SyntaxNode node) =>
        node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax;

    /// <summary>The body nodes of a nested body (lambda or local function).</summary>
    public static IReadOnlyList<SyntaxNode> NestedRoots(SyntaxNode node) => node switch
    {
        AnonymousFunctionExpressionSyntax f => f.Block is { } b ? [b] : f.ExpressionBody is { } e ? [e] : [],
        LocalFunctionStatementSyntax l => l.Body is { } b ? [b] : l.ExpressionBody is { } e ? [e.Expression] : [],
        _ => [],
    };
}

internal static class SyntaxHelpers
{
    public static SyntaxNode SkipParensUp(SyntaxNode node)
    {
        var p = node.Parent;
        while (p is ParenthesizedExpressionSyntax or ParenthesizedPatternSyntax) p = p.Parent;
        return p ?? node;
    }

    public static ExpressionSyntax StripParens(ExpressionSyntax e)
    {
        while (e is ParenthesizedExpressionSyntax p) e = p.Expression;
        return e;
    }

    public static bool IsLogical(SyntaxNode? n) =>
        n is BinaryExpressionSyntax b && (b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.LogicalOrExpression));

    public static bool IsPatternCombinator(SyntaxNode? n) =>
        n is BinaryPatternSyntax b && (b.IsKind(SyntaxKind.AndPattern) || b.IsKind(SyntaxKind.OrPattern));
}
