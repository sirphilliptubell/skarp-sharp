using System.Globalization;
using SkarpSharp.Findings;
using SkarpSharp.Health;

namespace SkarpSharp.Configuration;

/// <summary>
/// The code health score's settings (<c>score.*</c> in <c>.skarp.json</c>) and the flag
/// <c>--score-model</c>, with the usual precedence (default &lt; file &lt; flag). Unknown models and rule ids exit 2.
/// </summary>
public static class ScoreCommandLine
{
    static string KnownModels => string.Join(", ", ScoreModels.Names);

    internal static void ApplyFile(SkarpOptions o, SettingsFile f, string path)
    {
        if (f.Score is not { } s) return;
        var weights = new Dictionary<string, WeightOverride>(StringComparer.Ordinal);
        foreach (var (rule, w) in s.Weights ?? []) weights[rule] = Weight(path, rule, w);
        o.Score = new ScoreSettings
        {
            Model = Model(o, s.Model, path), Decay = Checked(o, "score.decay", s.Decay, o.Score.Decay, d => d > 0 && d <= 1, path, "must be more than 0 and at most 1"),
            Weights = weights,
            WorstCount = (int)Checked(o, "score.worst.count", s.Worst?.Count, o.Score.WorstCount, c => c >= 0, path, "must be 0 or more"),
            WorstBelow = Checked(o, "score.worst.below", s.Worst?.Below, o.Score.WorstBelow, b => b is >= 1 and <= MaxBelow, path, "must be between 1 and 10.5"),
        };
    }

    /// <summary>A threshold above 10 lists every file below a perfect score.</summary>
    const double MaxBelow = 10.5;

    static string Model(SkarpOptions o, string? model, string path)
    {
        if (model is null) return o.Score.Model;
        if (!ScoreModels.IsKnown(model)) throw new SettingsException($"{path}: score.model: unknown model '{model}' (known: {KnownModels})");
        return model;
    }

    /// <summary>A numeric setting: the file's value when valid (marked as from the file), else the current one.</summary>
    static double Checked(SkarpOptions o, string key, double? value, double current, Func<double, bool> valid, string path, string message)
    {
        if (value is not double v) return current;
        if (!valid(v)) throw new SettingsException($"{path}: {key}: {message}");
        return v;
    }

    /// <summary>The points a file can lose (10 down to 1).</summary>
    const double MaxPoints = 9;

    static WeightOverride Weight(string path, string rule, ScoreWeightSettings? w)
    {
        if (RuleCatalog.Find(rule) is null)
            throw new SettingsException($"{path}: score.weights.{rule}: unknown rule (known: {string.Join(", ", RuleCatalog.Every.Select(r => r.Id))})");
        if (RuleCatalog.IsComposite(rule))
            throw new SettingsException($"{path}: score.weights.{rule}: {rule} is a composite and has no weight: the score charges its parts");
        if (w is null) throw new SettingsException($"{path}: score.weights.{rule}: wrong type (expected an object)");
        foreach (var (name, value) in new[] { ("notice", w.Notice), ("warning", w.Warning), ("cap", w.Cap) })
            if (value is double v && v is not (>= 0 and <= MaxPoints))
                throw new SettingsException($"{path}: score.weights.{rule}.{name}: must be between 0 and 9 points");
        if (w.Decay is double d && d is not (> 0 and <= 1))
            throw new SettingsException($"{path}: score.weights.{rule}.decay: must be more than 0 and at most 1");
        return new WeightOverride(w.Notice, w.Warning, w.Cap, w.Decay);
    }

    /// <summary>Applies <c>--score-model</c>; returns an error message, or null (also for other flags).</summary>
    internal static string? ApplyFlag(SkarpOptions o, string name, string? v)
    {
        if (name != "--score-model") return null;
        if (v is null || !ScoreModels.IsKnown(v)) return $"Invalid value for --score-model: '{v}' (known: {KnownModels})";
        o.Score = new ScoreSettings { Model = v, Decay = o.Score.Decay, Weights = o.Score.Weights, WorstCount = o.Score.WorstCount, WorstBelow = o.Score.WorstBelow };
        o.Mark("score.model", SettingOrigin.CommandLine);
        return null;
    }

    /// <summary>The weight overrides as <c>skarp config</c> prints them (<c>long-method notice 0.5 cap 3</c>).</summary>
    internal static string WeightsText(ScoreSettings s)
    {
        if (s.Weights.Count == 0) return "(table)";
        static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        return string.Join("; ", s.Weights.OrderBy(w => w.Key, StringComparer.Ordinal).Select(w =>
            w.Key + (w.Value.Notice is double n ? $" notice {N(n)}" : "") + (w.Value.Warning is double x ? $" warning {N(x)}" : "") + (w.Value.Cap is double c ? $" cap {N(c)}" : "")
            + (w.Value.Decay is double d ? $" decay {N(d)}" : "")));
    }

    internal static string Number(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
