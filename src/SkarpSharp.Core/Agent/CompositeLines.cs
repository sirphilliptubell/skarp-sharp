using SkarpSharp.Findings;
using SkarpSharp.Model;

namespace SkarpSharp.Agent;

/// <summary>
/// In <c>skarp check</c> and the <c>Stop</c> hook, the lines of a composite's parts
/// (brain-method, brain-class) become one line for the composite. Its kind follows the parts: new when every part is new,
/// worse when any part is new or worse, better when parts only got better, present (no baseline) when any part is shown.
/// The gate still judges the parts (<see cref="CheckOutcome.PartsTripped"/>).
/// </summary>
public static class CompositeLines
{
    /// <summary>Replaces the part lines of each composite of <paramref name="current"/> with one composite line.
    /// <paramref name="kindOf"/> gives each part's kind before the severity filter (null: unchanged).</summary>
    public static void Apply(CheckOutcome outcome, FileCheckResult current, Func<Finding, LineKind?> kindOf, Severity minSeverity,
        IDictionary<string, string>? changes)
    {
        if (current.Composites.Count == 0) return;
        foreach (var standIn in BrainRules.Collapse(current.Findings, current.Composites).Where(f => f.Parts is not null))
        {
            var parts = BrainRules.Flatten(standIn).ToList();
            var ids = parts.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            var removed = outcome.Lines.Where(l => ids.Contains(l.Id)).ToList();
            outcome.CompositeParts[standIn.Id] = removed;
            if (removed.Exists(l => l.Blocks)) outcome.PartsTripped = true;
            outcome.Lines.RemoveAll(l => ids.Contains(l.Id));
            if (Kind(parts.Select(kindOf).ToList()) is not { } kind) continue;
            if (minSeverity == Severity.Warning && standIn.Severity != "warning") continue;
            outcome.AddSource(standIn);
            var line = outcome.Stamp(AgentFormat.For(standIn, kind)) with { PartsBlock = removed.Exists(l => l.Blocks) };
            outcome.Lines.Add(line);
            outcome.CompositeIds.Add(line.Id);
            if (changes is not null) changes[line.Id] = line.Said;
        }
    }

    /// <summary>The composite's kind from its parts' kinds.</summary>
    public static LineKind? Kind(IReadOnlyList<LineKind?> parts)
    {
        if (parts.Count == 0 || parts.All(k => k is null)) return null;
        if (parts.Any(k => k == LineKind.Present)) return LineKind.Present;
        if (parts.All(k => k == LineKind.New)) return LineKind.New;
        if (parts.Any(k => k is LineKind.New or LineKind.Worse)) return LineKind.Worse;
        return parts.Any(k => k == LineKind.Better) ? LineKind.Better : null;
    }
}
