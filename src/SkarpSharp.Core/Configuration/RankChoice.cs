using SkarpSharp.Findings;

namespace SkarpSharp.Configuration;

/// <summary>
/// What a worst-first ranking sorts by and what it ranks. <c>health</c> and <c>hotspot</c> rank
/// files; a rule ranks by that rule's own measure, methods for a method rule (or files by their worst method), files for
/// a type or file rule. Shared by <c>skarp analyze --sort</c> and the MCP tool <c>find_worst_code</c>, with the same
/// values, defaults and refusals.
/// </summary>
public sealed record RankChoice(string Sort, string Unit)
{
    public const string Health = "health", Hotspot = "hotspot";
    public const string Auto = "auto", Files = "files", Methods = "methods";

    /// <summary>The rules a ranking can sort by: those with a numeric measure in the syntax tier, in rule order.</summary>
    public static readonly IReadOnlyList<string> SortableRules =
    [
        RuleIds.ComplexMethod, RuleIds.DeepNesting, RuleIds.LongMethod, RuleIds.LargeType, RuleIds.LargeFile,
        RuleIds.TooManyParameters, RuleIds.PathExplosion, RuleIds.BumpyRoad, RuleIds.ComplexCondition,
    ];

    /// <summary>The sortable rules that judge methods (the rest judge types or files).</summary>
    public static bool IsMethodRule(string rule) => rule is not (RuleIds.LargeType or RuleIds.LargeFile);

    /// <summary>The sort values in words, for errors and descriptions.</summary>
    public static string SortValues => $"health, hotspot or a rule: {string.Join(", ", SortableRules)}";

    /// <summary>True for a rule sort.</summary>
    public bool ByRule => Sort is not (Health or Hotspot);

    /// <summary>
    /// The choice for <paramref name="sort"/> and <paramref name="unit"/> (null: the defaults <c>health</c> and
    /// <c>auto</c>), with <c>auto</c> resolved; or the error, which names the valid values or combinations.
    /// </summary>
    public static (RankChoice? Choice, string? Error) Resolve(string? sort, string? unit)
    {
        sort ??= Health;
        unit ??= Auto;
        if ((SortError(sort) ?? UnitError(sort, unit)) is { } error) return (null, error);
        var resolved = unit != Auto ? unit : ByFiles(sort) ? Files : Methods;
        return (new RankChoice(sort, resolved), null);
    }

    static bool ByFiles(string sort) => sort is Health or Hotspot || !IsMethodRule(sort);

    static string? SortError(string sort)
    {
        if (sort is Health or Hotspot || SortableRules.Contains(sort)) return null;
        return RuleCatalog.Find(sort) is null
            ? $"unknown sort '{sort}'; sort takes {SortValues}"
            : $"rule '{sort}' has no measure to sort by; sort takes {SortValues}";
    }

    static string? UnitError(string sort, string unit)
    {
        if (unit is not (Auto or Files or Methods)) return $"unknown unit '{unit}'; unit takes auto, files or methods";
        if (unit != Methods || !ByFiles(sort)) return null;
        return sort is Health or Hotspot
            ? $"unit methods needs a method rule as the sort ({string.Join(", ", SortableRules.Where(IsMethodRule))}); {sort} ranks files"
            : $"unit methods does not fit {sort}, which judges {(sort == RuleIds.LargeType ? "types" : "files")}; use unit files or auto";
    }
}
