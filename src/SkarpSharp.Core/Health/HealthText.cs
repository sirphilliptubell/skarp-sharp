using System.Globalization;
using SkarpSharp.Model;

namespace SkarpSharp.Health;

/// <summary>How scores are rounded and written: one decimal shown, two kept in JSON, half away from zero.</summary>
public static class HealthText
{
    /// <summary>
    /// One decimal, rounded half away from zero (<c>7.8</c>, <c>10.0</c>), from the two-decimal value JSON keeps, so a
    /// score shows the same whether it was just computed or read back from a saved run.
    /// </summary>
    public static string Score(double score) => Shown(score).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>The value <see cref="Score"/> shows.</summary>
    public static double Shown(double score) => Math.Round(Json(score), 1, MidpointRounding.AwayFromZero);

    /// <summary>Two decimals for JSON.</summary>
    public static double Json(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>A signed one-decimal change (<c>-0.4</c>, <c>+1.0</c>).</summary>
    public static string Delta(double delta)
    {
        var d = Math.Round(delta, 1, MidpointRounding.AwayFromZero);
        return (d >= 0 ? "+" : "") + d.ToString("0.0", CultureInfo.InvariantCulture);
    }

    /// <summary>One contribution: <c>complex-method 2 warnings -1.7 (capped)</c>.</summary>
    public static string Contribution(HealthContribution c) =>
        $"{c.Rule} {c.Count} {c.Severity}{(c.Count == 1 ? "" : "s")} -{Math.Round(c.Points, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture)}{(c.Capped == true ? " (capped)" : "")}";

    /// <summary>The top contributions joined with commas (at most <paramref name="max"/>; "no findings" when none).</summary>
    public static string Contributions(IEnumerable<HealthContribution> contributions, int max = 3)
    {
        var list = contributions.Take(max).Select(Contribution).ToList();
        return list.Count == 0 ? "no findings" : string.Join(", ", list);
    }
}
