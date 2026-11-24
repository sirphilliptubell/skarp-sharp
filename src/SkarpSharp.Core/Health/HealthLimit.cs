using System.Globalization;

namespace SkarpSharp.Health;

/// <summary>
/// The gate's three health limits per file (<c>gate.health.*</c>). A value of 0 turns that rule off.
/// </summary>
public sealed class HealthLimits(double maxDrop, double newFileMin, double floor)
{
    public const double DefaultMaxDrop = 1.0, DefaultNewFileMin = 6.5, DefaultFloor = 6.5;
    /// <summary>The largest drop limit (a score runs from 1 to 10) and the largest minimum.</summary>
    public const double MaxDropLimit = 9, MaxScoreLimit = 10;

    public static readonly HealthLimits Default = new(DefaultMaxDrop, DefaultNewFileMin, DefaultFloor);

    /// <summary>A known file may not lose this much or more (shown values); 0: off.</summary>
    public double MaxDrop { get; } = maxDrop;
    /// <summary>A new file must score at least this; 0: off.</summary>
    public double NewFileMin { get; } = newFileMin;
    /// <summary>A file whose score fell may not end below this; 0: off.</summary>
    public double Floor { get; } = floor;

    /// <summary>At least one rule is on.</summary>
    public bool Any => MaxDrop > 0 || NewFileMin > 0 || Floor > 0;

    public HealthLimits With(double? maxDrop = null, double? newFileMin = null, double? floor = null) =>
        new(maxDrop ?? MaxDrop, newFileMin ?? NewFileMin, floor ?? Floor);

    /// <summary>The limits in words for a verdict: <c>a drop under 1.0, new files at least 6.5, no drop below 6.5</c>;
    /// empty when every rule is off.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (MaxDrop > 0) parts.Add($"a drop under {HealthLimit.Number(MaxDrop)}");
        if (NewFileMin > 0) parts.Add($"new files at least {HealthLimit.Number(NewFileMin)}");
        if (Floor > 0) parts.Add($"no drop below {HealthLimit.Number(Floor)}");
        return string.Join(", ", parts);
    }
}

/// <summary>The rule a file broke (JSON <c>rule</c>): <c>drop</c>, <c>floor</c> or <c>newFile</c>.</summary>
public static class HealthRule
{
    public const string Drop = "drop", Floor = "floor", NewFile = "newFile";
}

/// <summary>One file past a health limit: its scores as shown (one decimal; <see cref="Before"/> null for a new file) and the rule.</summary>
public sealed class HealthBreach(string path, double? before, double now, string rule, HealthLimits limits, string? root = null)
{
    public string Path { get; } = path;
    /// <summary>The root <see cref="Path"/> is relative to (checks); null in an analysis.</summary>
    public string? Root { get; } = root;
    public double? Before { get; } = before;
    public double Now { get; } = now;
    public string Rule { get; } = rule;
    /// <summary>The limits it was judged by (the heading names the one it broke).</summary>
    public HealthLimits Limits { get; } = limits;
}

/// <summary>
/// Decides the health rules of the gate in one place, for every surface. Compares the
/// shown one-decimal values, so what blocks is what is printed; a drop equal to the limit blocks.
/// <list type="bullet">
/// <item>a known file whose score fell by <see cref="HealthLimits.MaxDrop"/> or more (<c>drop</c>);</item>
/// <item>a known file whose score fell at all and ends below <see cref="HealthLimits.Floor"/> (<c>floor</c>);</item>
/// <item>a new file below <see cref="HealthLimits.NewFileMin"/> (<c>newFile</c>).</item>
/// </list>
/// A file that did not get worse never blocks, however low it scores.
/// </summary>
public static class HealthLimit
{
    /// <summary>The rule a file breaks, or null. <paramref name="before"/>: the score at the comparison point (null when
    /// unknown: then only a new file is judged); <paramref name="isNew"/>: the comparison point does not hold the file.</summary>
    public static string? Judge(double? before, double now, bool isNew, HealthLimits limits)
    {
        double n = HealthText.Shown(now);
        if (isNew) return limits.NewFileMin > 0 && n < limits.NewFileMin ? HealthRule.NewFile : null;
        if (before is not double b0) return null;
        double drop = Math.Round(HealthText.Shown(b0) - n, 1, MidpointRounding.AwayFromZero);
        if (drop <= 0) return null;
        if (limits.MaxDrop > 0 && drop >= limits.MaxDrop) return HealthRule.Drop;
        if (limits.Floor > 0 && n < limits.Floor) return HealthRule.Floor;
        return null;
    }

    /// <summary>The breach of a file, or null (see <see cref="Judge"/>).</summary>
    public static HealthBreach? Of(string path, double? before, double now, bool isNew, HealthLimits limits, string? root = null) =>
        Judge(before, now, isNew, limits) is { } rule
            ? new HealthBreach(path, isNew ? null : HealthText.Shown(before!.Value), HealthText.Shown(now), rule, limits, root)
            : null;

    /// <summary>
    /// The heading of a blocking file: <c>src/A.cs health 8.3 -&gt; 7.1 (down 1.2, limit under 1.0)</c>,
    /// <c>src/A.cs health 6.8 -&gt; 6.2 (below 6.5 after a drop)</c>, <c>src/A.cs health 5.4 (new file, limit at least 6.5)</c>.
    /// </summary>
    public static string Heading(string path, HealthBreach b) => $"{path} health {Scores(b)} ({Reason(b)})";

    /// <summary><c>8.3 -&gt; 7.1</c>, or <c>5.4</c> for a new file.</summary>
    public static string Scores(HealthBreach b) => b.Before is double before ? $"{HealthText.Score(before)} -> {HealthText.Score(b.Now)}" : HealthText.Score(b.Now);

    /// <summary>What the heading's bracket says.</summary>
    public static string Reason(HealthBreach b) => b.Rule switch
    {
        HealthRule.Drop => $"down {Number(Down(b))}, limit under {Number(b.Limits.MaxDrop)}",
        HealthRule.Floor => $"below {Number(b.Limits.Floor)} after a drop",
        _ => $"new file, limit at least {Number(b.Limits.NewFileMin)}",
    };

    /// <summary>The MCP fact: <c>src/A.cs is down 1.2; the gate stops at 1.0 or more</c>.</summary>
    public static string Fact(string path, HealthBreach b) => b.Rule switch
    {
        HealthRule.Drop => $"{path} is down {Number(Down(b))}; the gate stops at {Number(b.Limits.MaxDrop)} or more",
        HealthRule.Floor => $"{path} fell to {HealthText.Score(b.Now)}; the gate stops a drop that ends below {Number(b.Limits.Floor)}",
        _ => $"{path} is a new file at {HealthText.Score(b.Now)}; the gate stops new files below {Number(b.Limits.NewFileMin)}",
    };

    static double Down(HealthBreach b) => Math.Round((b.Before ?? b.Now) - b.Now, 1, MidpointRounding.AwayFromZero);

    /// <summary><c>health past its limit in 2 files</c> (with <paramref name="where"/>: <c>in 1 staged file</c>).</summary>
    public static string Verdict(int files, string where = "") =>
        $"health past its limit in {files.ToString(CultureInfo.InvariantCulture)}{(where.Length > 0 ? " " + where : "")} {(files == 1 ? "file" : "files")}";

    /// <summary>A limit with one decimal (<c>1.0</c>, <c>6.5</c>).</summary>
    public static string Number(double v) => v.ToString("0.0##", CultureInfo.InvariantCulture);
}
