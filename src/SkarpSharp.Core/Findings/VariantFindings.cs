using SkarpSharp.Model;
using SkarpSharp.Conditionals;

namespace SkarpSharp.Findings;

/// <summary>
/// Findings of a file with preprocessor variants are compared with the same rules
/// on its base view. A finding the base view does not have exists only in a variant: it becomes a notice and names the
/// variant ("with NET20 on"); a finding whose value a variant made worse names it too, and is never more severe than the
/// base view's.
/// </summary>
public static class VariantFindings
{
    public static bool HasVariants(FileResult f) => f.Variants?.Any(v => v.Discarded != true) == true;

    /// <summary>The file as the base view sees it: base values, without variant-only members and declarations.</summary>
    public static FileResult BaseView(FileResult f) => new()
    {
        Path = f.Path, Generated = f.Generated, Lines = f.BaseLines ?? f.Lines, TopLevelTypes = f.TopLevelTypes, TestCode = f.TestCode,
        Types = [.. f.Types.Where(t => t.OnlyInVariants != true).Select(t => t.BaseValues is { } b ? t.WithValues(b) : t)],
        Members = [.. f.Members.Where(m => m.OnlyInVariants != true).Select(m => m.BaseValues is { } b ? m.WithValues(b) : m)],
    };

    /// <summary>The variant a finding of <paramref name="f"/> comes from: its member's, its type's, or the file's lines'.</summary>
    public static string? LabelOf(FileResult f, Finding x)
    {
        if (x.TypePlace) return f.Types.FirstOrDefault(t => t.Name == x.PlaceKey && t.Variant is not null)?.Variant;
        if (x.PlaceKey is null) return f.LinesVariant;
        return f.Members.FirstOrDefault(m => (m.Key.Length > 0 ? m.Key : m.Id) == x.PlaceKey)?.Variant;
    }

    /// <summary>Marks the findings of <paramref name="merged"/> that <paramref name="baseView"/> lacks or has with another value.</summary>
    public static void Reconcile(List<Finding> merged, List<Finding> baseView, Func<Finding, string?> label)
    {
        var remaining = new Dictionary<(string, string?, int), Queue<Finding>>();
        foreach (var b in baseView)
        {
            var key = Key(b);
            if (!remaining.TryGetValue(key, out var q)) remaining[key] = q = new Queue<Finding>();
            q.Enqueue(b);
        }
        foreach (var x in merged)
        {
            var name = label(x) ?? "another symbol set";
            if (!remaining.TryGetValue(Key(x), out var q) || q.Count == 0)
            {
                x.Severity = "notice";
                x.Variant = label(x);
                x.Message += $" ({PlannedVariant.WithText(name)})";
                continue;
            }
            var b = q.Dequeue();
            if (b.Value == x.Value && b.Severity == x.Severity) continue;
            x.Variant = label(x);
            x.Message += $" (worst {PlannedVariant.WithText(name)})";
            if (x.Severity == "warning" && b.Severity == "notice")
            {
                // The warning exists only in a variant: keep the base view's level, with the variant's value.
                x.Severity = b.Severity; x.Threshold = b.Threshold; x.Comparison = b.Comparison; x.Preset = b.Preset;
                x.ThresholdSource = b.ThresholdSource;
            }
        }
    }

    /// <summary>Rule and place; empty catches also by line (one finding per catch clause, whose lines all variants share).</summary>
    static (string, string?, int) Key(Finding f) => (f.Rule, f.PlaceKey ?? f.Member, f.Rule == RuleIds.EmptyCatch ? f.Line : 0);
}
