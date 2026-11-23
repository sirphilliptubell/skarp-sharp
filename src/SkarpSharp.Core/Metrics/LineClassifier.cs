using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SkarpSharp.Metrics;

/// <summary>Primary kind of one physical line.</summary>
public enum LineKind : byte { Blank, Comment, Code, Mixed, Directive, Inactive }

/// <summary>Line counts for a file or a line range. <c>Comment</c> counts comment-only plus mixed lines (overlapping).</summary>
public readonly record struct LineCounts(int Physical, int Blank, int Comment, int Mixed, int Source, int Directive, int Inactive)
{
    public static LineCounts From(ReadOnlySpan<LineKind> kinds)
    {
        int blank = 0, commentOnly = 0, code = 0, mixed = 0, directive = 0, inactive = 0;
        foreach (var k in kinds)
        {
            switch (k)
            {
                case LineKind.Blank: blank++; break;
                case LineKind.Comment: commentOnly++; break;
                case LineKind.Code: code++; break;
                case LineKind.Mixed: mixed++; break;
                case LineKind.Directive: directive++; break;
                case LineKind.Inactive: inactive++; break;
            }
        }
        return new LineCounts(kinds.Length, blank, commentOnly + mixed, mixed, code + mixed + directive, directive, inactive);
    }
}

/// <summary>Classifies every physical line of a parsed file from its tokens and trivia, never from raw text.</summary>
public static class LineClassifier
{
    public static LineKind[] Classify(SyntaxTree tree)
    {
        var text = tree.GetText();
        int physical = PhysicalLines(text);
        var kinds = new LineKind[physical];
        if (physical == 0) return kinds;
        var hasCode = new bool[physical];
        var hasComment = new bool[physical];
        var isDirective = new bool[physical];
        var isInactive = new bool[physical];

        int LineOf(int pos) => Math.Min(physical - 1, text.Lines.GetLineFromPosition(pos).LineNumber);
        void Mark(bool[] flags, Microsoft.CodeAnalysis.Text.TextSpan span)
        {
            if (span.Length == 0) return;
            int a = LineOf(span.Start), b = LineOf(span.End - 1);
            for (int i = a; i <= b; i++) flags[i] = true;
        }

        var root = tree.GetRoot();
        foreach (var token in root.DescendantTokens(descendIntoTrivia: false))
        {
            if (!token.IsKind(SyntaxKind.EndOfFileToken)) Mark(hasCode, token.Span);
        }
        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: false))
        {
            switch (trivia.Kind())
            {
                case SyntaxKind.SingleLineCommentTrivia:
                case SyntaxKind.MultiLineCommentTrivia:
                case SyntaxKind.SingleLineDocumentationCommentTrivia:
                case SyntaxKind.MultiLineDocumentationCommentTrivia:
                    Mark(hasComment, trivia.Span);
                    break;
                case SyntaxKind.DisabledTextTrivia:
                    Mark(isInactive, trivia.Span);
                    break;
                default:
                    if (trivia.IsDirective) Mark(isDirective, trivia.Span);
                    break;
            }
        }

        for (int i = 0; i < physical; i++)
        {
            kinds[i] = isInactive[i] ? LineKind.Inactive
                : isDirective[i] ? LineKind.Directive
                : hasCode[i] ? (hasComment[i] ? LineKind.Mixed : LineKind.Code)
                : hasComment[i] ? LineKind.Comment
                : LineKind.Blank;
        }
        return kinds;
    }

    /// <summary>Terminators plus one if the non-empty text does not end with a terminator; an empty text has 0 lines.</summary>
    public static int PhysicalLines(Microsoft.CodeAnalysis.Text.SourceText text)
    {
        if (text.Length == 0) return 0;
        var lines = text.Lines;
        var last = lines[^1];
        return last.Span.Length == 0 && last.Start == text.Length ? lines.Count - 1 : lines.Count;
    }
}
