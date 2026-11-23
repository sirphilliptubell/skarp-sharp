// Pattern: [ExcludeFromCodeCoverage] on a member and on a type.
// Expected: no untested-complex-method finding; coverage status "excluded" for both members.
// Seen: coverage exclusions are documented by the coverage tools, but did not occur in the sampled repositories.
using System.Diagnostics.CodeAnalysis;

namespace Mock.Lib;

public static class DebugViews
{
    [ExcludeFromCodeCoverage]
    public static string Dump(int[] values)
    {
        if (values.Length == 0) return "[]";
        var parts = new List<string>();
        foreach (var v in values)
        {
            if (v < 0) parts.Add("-");
            else if (v == 0) parts.Add("0");
            else if (v > 1000) parts.Add("big");
            else parts.Add(v.ToString());
        }
        return "[" + string.Join(",", parts) + "]";
    }
}

[ExcludeFromCodeCoverage]
public sealed class TraceView
{
    public static string Kind(object? o)
    {
        if (o is null) return "null";
        if (o is string) return "string";
        if (o is int) return "int";
        if (o is Array) return "array";
        if (o is Delegate) return "delegate";
        return "object";
    }
}
