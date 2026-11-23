using System.Text.RegularExpressions;

namespace SkarpSharp.Conditionals;

// The .NET SDK's implicit preprocessor
// symbols as data, and the classifier of a file's condition symbols.

/// <summary>Target framework families in the planner's order: .NET Framework, .NET
/// Standard, .NET Core 1 to 3.1, .NET 5 and later.</summary>
public enum FrameworkFamily { Framework, Standard, CoreApp, Net }

/// <summary>
/// One target framework. <see cref="Index"/> is the position in the family's version list of <see cref="SdkSymbols"/>;
/// for <see cref="FrameworkFamily.Net"/> it is the major version (5 and up, any number: recognized by pattern).
/// </summary>
public readonly record struct TargetFramework(FrameworkFamily Family, int Index) : IComparable<TargetFramework>
{
    public int CompareTo(TargetFramework other) =>
        Family != other.Family ? Family.CompareTo(other.Family) : Index.CompareTo(other.Index);

    /// <summary>The moniker: <c>net48</c>, <c>netstandard2.0</c>, <c>netcoreapp3.1</c>, <c>net8.0</c>.</summary>
    public string Moniker => Family switch
    {
        FrameworkFamily.Framework => "net" + SdkSymbols.FrameworkVersions[Index],
        FrameworkFamily.Standard => "netstandard" + SdkSymbols.StandardVersions[Index].Replace('_', '.'),
        FrameworkFamily.CoreApp => "netcoreapp" + SdkSymbols.CoreAppVersions[Index].Replace('_', '.'),
        _ => $"net{Index}.0",
    };

    /// <summary>The framework's own versioned symbol: <c>NET48</c>, <c>NETSTANDARD2_0</c>, <c>NETCOREAPP3_1</c>, <c>NET8_0</c>.</summary>
    public string ExactSymbol => Family switch
    {
        FrameworkFamily.Framework => "NET" + SdkSymbols.FrameworkVersions[Index],
        FrameworkFamily.Standard => "NETSTANDARD" + SdkSymbols.StandardVersions[Index],
        FrameworkFamily.CoreApp => "NETCOREAPP" + SdkSymbols.CoreAppVersions[Index],
        _ => $"NET{Index}_0",
    };

    public override string ToString() => Moniker;
}

/// <summary>
/// The implicit symbols the .NET SDK defines per target framework (dotnet/sdk 5c977fa): the
/// versionless family symbol, the exact versioned symbol and the <c>_OR_GREATER</c> symbols of every earlier version of
/// the family. Configuration (<c>DEBUG</c>/<c>RELEASE</c>) and <c>TRACE</c> are not part of a framework's set.
/// </summary>
public static class SdkSymbols
{
    /// <summary>.NET Framework versions as the symbols spell them (2.0 to 4.8.1; 3.0 has no moniker of its own).</summary>
    public static readonly string[] FrameworkVersions = ["20", "30", "35", "40", "45", "451", "452", "46", "461", "462", "47", "471", "472", "48", "481"];
    public static readonly string[] StandardVersions = ["1_0", "1_1", "1_2", "1_3", "1_4", "1_5", "1_6", "2_0", "2_1"];
    public static readonly string[] CoreAppVersions = ["1_0", "1_1", "2_0", "2_1", "2_2", "3_0", "3_1"];
    /// <summary>The newest released .NET: the default of the versionless <c>NET</c> and <c>NETCOREAPP</c> and the upper end
    /// of the fallback search (a file naming a newer one extends it).</summary>
    public const int NewestNet = 10;
    /// <summary>The first major version of the <c>NETn_0</c> pattern.</summary>
    public const int FirstNet = 5;

    /// <summary>The symbols the SDK defines for <paramref name="t"/>, without configuration and <c>TRACE</c>.</summary>
    public static IEnumerable<string> Implied(TargetFramework t)
    {
        switch (t.Family)
        {
            case FrameworkFamily.Framework:
                yield return "NETFRAMEWORK";
                yield return t.ExactSymbol;
                for (int i = 0; i <= t.Index; i++) yield return "NET" + FrameworkVersions[i] + "_OR_GREATER";
                break;
            case FrameworkFamily.Standard:
                yield return "NETSTANDARD";
                yield return t.ExactSymbol;
                for (int i = 0; i <= t.Index; i++) yield return "NETSTANDARD" + StandardVersions[i] + "_OR_GREATER";
                break;
            case FrameworkFamily.CoreApp:
                yield return "NETCOREAPP";
                yield return t.ExactSymbol;
                for (int i = 0; i <= t.Index; i++) yield return "NETCOREAPP" + CoreAppVersions[i] + "_OR_GREATER";
                break;
            default:
                yield return "NET";
                yield return "NETCOREAPP";
                yield return t.ExactSymbol;
                for (int n = FirstNet; n <= t.Index; n++) yield return $"NET{n}_0_OR_GREATER";
                foreach (var v in CoreAppVersions) yield return "NETCOREAPP" + v + "_OR_GREATER";
                break;
        }
    }

    /// <summary>True for frameworks with a moniker (every list entry except .NET Framework 3.0).</summary>
    public static bool HasMoniker(TargetFramework t) => t.Family != FrameworkFamily.Framework || FrameworkVersions[t.Index] != "30";

    /// <summary>Every known framework in family order, .NET up to <paramref name="newestNet"/>.</summary>
    public static IEnumerable<TargetFramework> Known(int newestNet = NewestNet)
    {
        for (int i = 0; i < FrameworkVersions.Length; i++)
            if (HasMoniker(new TargetFramework(FrameworkFamily.Framework, i))) yield return new TargetFramework(FrameworkFamily.Framework, i);
        for (int i = 0; i < StandardVersions.Length; i++) yield return new TargetFramework(FrameworkFamily.Standard, i);
        for (int i = 0; i < CoreAppVersions.Length; i++) yield return new TargetFramework(FrameworkFamily.CoreApp, i);
        for (int n = FirstNet; n <= Math.Max(newestNet, NewestNet); n++) yield return new TargetFramework(FrameworkFamily.Net, n);
    }

    /// <summary>The newest framework of a versionless symbol's family.</summary>
    public static TargetFramework NewestOf(VersionlessFamily family) => family switch
    {
        VersionlessFamily.Framework => new TargetFramework(FrameworkFamily.Framework, FrameworkVersions.Length - 1),
        VersionlessFamily.Standard => new TargetFramework(FrameworkFamily.Standard, StandardVersions.Length - 1),
        _ => new TargetFramework(FrameworkFamily.Net, NewestNet),
    };

    /// <summary>True when <paramref name="t"/> defines the versionless symbol of <paramref name="family"/>
    /// (<c>NETCOREAPP</c> is defined by .NET Core and by .NET 5 and later).</summary>
    public static bool InFamily(TargetFramework t, VersionlessFamily family) => family switch
    {
        VersionlessFamily.Framework => t.Family == FrameworkFamily.Framework,
        VersionlessFamily.Standard => t.Family == FrameworkFamily.Standard,
        VersionlessFamily.CoreApp => t.Family is FrameworkFamily.CoreApp or FrameworkFamily.Net,
        _ => t.Family == FrameworkFamily.Net,
    };
}

/// <summary>The family a versionless framework symbol names: <c>NETFRAMEWORK</c>, <c>NETSTANDARD</c>, <c>NETCOREAPP</c>, <c>NET</c>.</summary>
public enum VersionlessFamily { Framework, Standard, CoreApp, Net }

/// <summary>What a condition symbol is to the planner.</summary>
public enum SymbolCategory { Framework, Versionless, Configuration, Platform, Trace, Other }

/// <summary>A classified symbol: a framework symbol's target, a versionless symbol's family, a platform symbol's OS.</summary>
public readonly record struct SymbolClass(SymbolCategory Kind, TargetFramework? Target = null, VersionlessFamily? Family = null, string? Os = null);

/// <summary>
/// Classifies a condition symbol. <c>NETn_0</c> and <c>NETn_0_OR_GREATER</c> are .NET for any n of 5 or more; <c>NETnn</c> (digits only) is .NET Framework from the fixed list, so <c>NET48</c> and <c>NET5_0</c> never collide.
/// A <c>_OR_GREATER</c> symbol targets the lowest framework that defines it (<c>NET30_OR_GREATER</c> gives net35, as
/// 3.0 has no moniker); an exact symbol outside the lists (<c>NET30</c>, <c>NET10</c>) is an other symbol.
/// </summary>
public static partial class SymbolClassifier
{
    [GeneratedRegex(@"^NET(\d+)_0(_OR_GREATER)?$", RegexOptions.CultureInvariant)] private static partial Regex NetPattern();
    [GeneratedRegex(@"^NETCOREAPP(\d_\d)(_OR_GREATER)?$", RegexOptions.CultureInvariant)] private static partial Regex CoreAppPattern();
    [GeneratedRegex(@"^NETSTANDARD(\d_\d)(_OR_GREATER)?$", RegexOptions.CultureInvariant)] private static partial Regex StandardPattern();
    [GeneratedRegex(@"^NET(\d{2,3})(_OR_GREATER)?$", RegexOptions.CultureInvariant)] private static partial Regex FrameworkPattern();
    [GeneratedRegex(@"^(WINDOWS|ANDROID|IOS|MACCATALYST|MACOS|TVOS|BROWSER)(\d+(_\d+)*)?(_OR_GREATER)?$", RegexOptions.CultureInvariant)] private static partial Regex PlatformPattern();

    public static SymbolClass Classify(string symbol)
    {
        switch (symbol)
        {
            case "NETFRAMEWORK": return new(SymbolCategory.Versionless, Family: VersionlessFamily.Framework);
            case "NETSTANDARD": return new(SymbolCategory.Versionless, Family: VersionlessFamily.Standard);
            case "NETCOREAPP": return new(SymbolCategory.Versionless, Family: VersionlessFamily.CoreApp);
            case "NET": return new(SymbolCategory.Versionless, Family: VersionlessFamily.Net);
            case "DEBUG" or "RELEASE": return new(SymbolCategory.Configuration);
            case "TRACE": return new(SymbolCategory.Trace);
        }
        Match m;
        if ((m = NetPattern().Match(symbol)).Success && int.TryParse(m.Groups[1].ValueSpan, out var n) && n >= SdkSymbols.FirstNet)
            return new(SymbolCategory.Framework, new TargetFramework(FrameworkFamily.Net, n));
        if ((m = CoreAppPattern().Match(symbol)).Success && Array.IndexOf(SdkSymbols.CoreAppVersions, m.Groups[1].Value) is var c and >= 0)
            return new(SymbolCategory.Framework, new TargetFramework(FrameworkFamily.CoreApp, c));
        if ((m = StandardPattern().Match(symbol)).Success && Array.IndexOf(SdkSymbols.StandardVersions, m.Groups[1].Value) is var s and >= 0)
            return new(SymbolCategory.Framework, new TargetFramework(FrameworkFamily.Standard, s));
        if ((m = FrameworkPattern().Match(symbol)).Success && Array.IndexOf(SdkSymbols.FrameworkVersions, m.Groups[1].Value) is var f and >= 0)
        {
            var target = new TargetFramework(FrameworkFamily.Framework, f);
            if (SdkSymbols.HasMoniker(target)) return new(SymbolCategory.Framework, target);
            // 3.0 has no exact symbol; its _OR_GREATER is first defined by net35.
            return m.Groups[2].Success ? new(SymbolCategory.Framework, new TargetFramework(FrameworkFamily.Framework, f + 1)) : new(SymbolCategory.Other);
        }
        if ((m = PlatformPattern().Match(symbol)).Success) return new(SymbolCategory.Platform, Os: m.Groups[1].Value);
        return new(SymbolCategory.Other);
    }
}
