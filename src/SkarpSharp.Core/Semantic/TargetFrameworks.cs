using System.Globalization;

namespace SkarpSharp.Semantic;

/// <summary>
/// Target framework order for the default "highest target framework per project": .NET 5 and later and
/// .NET Core above .NET Standard above .NET Framework; within a family the higher version first; a framework without a
/// platform suffix (<c>net10.0</c>) before one with it (<c>net10.0-windows</c>). Unknown monikers sort last.
/// </summary>
public static class TargetFrameworks
{
    /// <summary>Sort key: (family, major, minor, plain); larger is higher.</summary>
    public static (int Family, int Major, int Minor, int Plain) Key(string tfm)
    {
        var t = tfm.Trim().ToLowerInvariant();
        int dash = t.IndexOf('-');
        var b = dash >= 0 ? t[..dash] : t;
        int plain = dash >= 0 ? 0 : 1;
        if (b.StartsWith("netstandard", StringComparison.Ordinal)) { var (m, n) = Version(b[11..]); return (1, m, n, plain); }
        if (b.StartsWith("netcoreapp", StringComparison.Ordinal)) { var (m, n) = Version(b[10..]); return (2, m, n, plain); }
        if (b.StartsWith("net", StringComparison.Ordinal) && b.Contains('.')) { var (m, n) = Version(b[3..]); return (2, m, n, plain); }
        if (b.StartsWith("net", StringComparison.Ordinal) && b.Length >= 5 && b[3..].All(char.IsAsciiDigit))
        {
            var digits = b[3..];
            return (0, digits[0] - '0', int.Parse(digits[1..].PadRight(2, '0'), CultureInfo.InvariantCulture), plain);
        }
        return (-1, 0, 0, plain);
    }

    static (int, int) Version(string s)
    {
        var parts = s.Split('.');
        int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major);
        int minor = 0;
        if (parts.Length > 1) int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor);
        return (major, minor);
    }

    public static string Highest(IEnumerable<string> tfms) => tfms.OrderByDescending(Key).ThenBy(t => t, StringComparer.Ordinal).First();

    /// <summary>Highest first.</summary>
    public static IEnumerable<string> Ordered(IEnumerable<string> tfms) => tfms.OrderByDescending(Key).ThenBy(t => t, StringComparer.Ordinal);
}
