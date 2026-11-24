using System.Globalization;
using SkarpSharp.Findings;
using SkarpSharp.Model;

namespace SkarpSharp.Semantic;

/// <summary>
/// Marinescu's God Class detection strategy, WMC very high AND ATFD over few AND TCC below one third
/// (the threshold values are from Lanza and Marinescu, taken from secondary sources). TCC is compared as an exact
/// fraction (3 * direct pairs &lt; pairs). Notice level only. A type
/// whose TCC is not defined gets no finding; types not evaluated have no metrics at all.
/// </summary>
public static class GodClassRule
{
    /// <summary>Judges one type; null when the rule does not fire or does not apply.</summary>
    public static Finding? Judge(SemanticType t, ActiveRule rule)
    {
        if (rule.Notice is null || t.Kind is "interface") return null;
        var set = GodClassPresets.Find(rule.Parameters.GetValueOrDefault("preset", GodClassPresets.Sets[0].Id)) ?? GodClassPresets.Sets[0];
        if (t.CohesionPairs is not int pairs || t.DirectPairs is not int direct || pairs == 0) return null;
        bool wmc = set.WmcStrict ? t.Wmc > set.Wmc : t.Wmc >= set.Wmc;
        if (!wmc || t.Atfd <= set.Atfd || direct * 3 >= pairs) return null;
        string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        return new Finding
        {
            Rule = RuleIds.GodClass, Severity = "notice", Path = t.Path, Line = t.Line, Member = t.Name,
            Value = t.Wmc, Metric = "wmc", Threshold = set.Wmc, Comparison = set.WmcStrict ? "over" : "at least",
            Preset = rule.NoticePreset, ThresholdSource = $"{set.Id}: {set.Source}",
            Message = $"WMC {t.Wmc}, ATFD {t.Atfd}, TCC {direct}/{pairs} = {N((double)direct / pairs)} (God Class {set.Id}: {set.Describe()})",
            PlaceKey = t.Name, TypePlace = true,
        };
    }
}
