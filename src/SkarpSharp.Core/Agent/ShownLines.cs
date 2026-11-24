using System.Globalization;

namespace SkarpSharp.Agent;

/// <summary>
/// The <c>Stop</c> summary leaves out what the hook already showed. A finding counts as seen when a message (an edit's or an earlier Stop's) showed it at its current value, so a
/// Stop that only sums what the edits printed says nothing. Each printed line is recorded by its id as
/// <c>&lt;state&gt; &lt;value&gt;</c> (<c>open</c> or <c>fixed</c>; the line's text for a line without a value; a health line:
/// <c>health &lt;score&gt;</c>). A composite is left out when all its part lines are. With <c>--gate</c>, lines
/// that block are never left out (they block again at each Stop).
/// </summary>
public static class ShownLines
{
    /// <summary>The recorded form of a line.</summary>
    public static string Encode(AgentLine line) => line.Kind switch
    {
        LineKind.Health => "health " + Num(line.Value),
        LineKind.Resolved => "fixed " + Num(line.Value),
        // A line without a value (a composite, a structural finding) is seen at the same text: a composite's text holds
        // every part's numbers, the notices among them too.
        _ => "open " + (line.Value is null ? line.Text : Num(line.Value)),
    };

    static string Num(double? v) => v is double d ? d.ToString("R", CultureInfo.InvariantCulture) : "-";

    /// <summary>Adds printed lines (and the parts of each printed composite) to <paramref name="shown"/>.</summary>
    public static void Add(SortedDictionary<string, string> shown, IEnumerable<AgentLine> printed, CheckOutcome outcome)
    {
        foreach (var line in printed)
        {
            shown[line.Id] = Encode(line);
            if (outcome.CompositeParts.TryGetValue(line.Id, out var parts))
                foreach (var part in parts) shown[part.Id] = Encode(part);
        }
    }

    /// <summary>The score last shown for a health line's id (<c>health|&lt;path&gt;</c>), or null.</summary>
    public static double? Score(SortedDictionary<string, string> shown, string id) =>
        shown.TryGetValue(id, out var s) && s.StartsWith("health ", StringComparison.Ordinal)
            && double.TryParse(s.AsSpan(7), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Whether the last line printed for the finding (or file) of <paramref name="line"/> showed its current value.</summary>
    public static bool Seen(SortedDictionary<string, string> shown, AgentLine line) =>
        shown.TryGetValue(line.Id, out var s) && s == Encode(line);

    /// <summary>
    /// Removes the lines of <paramref name="outcome"/> that repeat <paramref name="shown"/>. With
    /// <paramref name="gate"/>, a line that blocks (a composite: one whose parts block) is kept, so the Stop lists
    /// every blocking line each time it blocks.
    /// </summary>
    public static void Drop(CheckOutcome outcome, SortedDictionary<string, string> shown, bool gate)
    {
        outcome.Lines.RemoveAll(l => outcome.CompositeParts.TryGetValue(l.Id, out var parts) && parts.Count > 0
            ? !(gate && (l.PastLimit || parts.Exists(p => p.Blocks || p.PastLimit))) && parts.TrueForAll(p => Seen(shown, p)) && Seen(shown, l)
            : !(gate && (l.Blocks || l.PastLimit)) && Seen(shown, l));
        outcome.Health.RemoveAll(h => Seen(shown, h.Line));
    }
}
