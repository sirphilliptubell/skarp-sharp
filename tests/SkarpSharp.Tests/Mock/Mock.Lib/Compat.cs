// Pattern: a multi-targeted library whose test for this member runs only under the other target framework.
// Expected: untested-complex-method, notice: a documented false finding of single-target reports (collect a report per
// target framework and pass them all).
// Seen: in real multi-targeted libraries (a converter whose test runs under one target framework only).
namespace Mock.Lib;

public static class Compat
{
    public static string Legacy(string? value, bool upper, int width)
    {
        if (value is null) return "";
        var v = value.Trim();
        if (upper) v = v.ToUpperInvariant();
        if (width <= 0) return v;
        if (v.Length > width) return v[..width];
        if (v.Length < width) return v.PadRight(width);
        return v;
    }
}
