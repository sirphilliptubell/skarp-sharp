using SkarpSharp.Health;
using SkarpSharp.Model;
using SkarpSharp.Reporting;

namespace SkarpSharp.Agent;

/// <summary>A <c>skarp show</c> list to print: the verdict, notes, the rows in selection order, the cap, how
/// paths print, whether the rows are compared (<c>--new-only</c>), the <c>--file</c> health breakdowns and the tail.</summary>
public sealed class ShowList(string verdict, List<ShowFinding> rows, Func<ShowFinding, string> pathOf)
{
    public string Verdict { get; } = verdict;
    public List<ShowFinding> Rows { get; } = rows;
    public Func<ShowFinding, string> PathOf { get; } = pathOf;
    public List<string> Notes { get; init; } = [];
    public int Cap { get; init; } = int.MaxValue;
    public bool Compared { get; init; }
    public IReadOnlyList<ShowHealth>? Health { get; init; }
    /// <summary>The fold line of a capped list (null: none).</summary>
    public Func<int, string>? More { get; init; }
    /// <summary>The notice hint, or null.</summary>
    public string? Hint { get; init; }
    /// <summary>With <see cref="Compared"/>: what a row says in place of the last check's words (<c>show --session</c>
    /// judges the session), or null.</summary>
    public Func<ShowFinding, string?>? Said { get; init; }
    /// <summary>The closing lines in place of the hint and the About lines, from the first id shown and the
    /// rules shown (the MCP tool names its tools, never a command); null: the hint and the About lines.</summary>
    public Func<string?, IReadOnlyList<string>, IEnumerable<string>>? Footer { get; init; }
}

/// <summary>
/// <c>skarp show</c> in the text format: the list grouped by file (one heading per
/// file, findings by line, a blank line between files), and the finding line that starts <c>show &lt;id&gt;</c>.
/// </summary>
public static class ShowText
{
    /// <summary>
    /// An entry's line: <c>fixed</c> for what a later check reported resolved; with <paramref name="compared"/>, the
    /// change (the last check's word, else the baseline's: <c>new</c>, <c>worse</c>); else the level. A baseline entry no
    /// saved finding matches leads with <c>recorded</c>.
    /// </summary>
    public static string Line(ShowFinding e, bool compared, string? file = null, string? said = null)
    {
        var f = e.Finding;
        AgentFormat.Restore(f);
        var place = ReportLayout.ShortName(f);
        if (e.Change is "resolved" or "removed" || (e.Change?.StartsWith("fixed:", StringComparison.Ordinal) ?? false))
            return new TextLine("fixed", f.Rule, TextFormat.Fixed(f.Metric, f.Value, e.Change == "removed", place))
            {
                File = file, Line = e.Change == "removed" ? 0 : f.Line, Place = place, Notice = f.Severity == "notice", Id = f.Handle,
            }.Render();
        if (f.Severity == "-")
            return new TextLine("recorded", f.Rule, f.Value is double v ? "value " + Findings.Condition.Num(v) : "in the baseline") { File = file, Place = place, Id = f.Handle }.Render();
        said ??= e.Source == "check" ? e.Change : null;
        if (compared && said is not null && said.IndexOf(": ", StringComparison.Ordinal) is var colon and > 0)
            return new TextLine(said[..colon], f.Rule, said[(colon + 2)..])
            {
                File = file, Line = f.Line, Place = place, Notice = f.Severity == "notice", Id = f.Handle,
            }.Render();
        return TextFormat.FindingLine(f, compared, file);
    }

    /// <summary>Prints the list.</summary>
    public static void Write(TextWriter w, ShowList list)
    {
        w.WriteLine("Skarp: " + list.Verdict);
        foreach (var n in list.Notes) w.WriteLine(n);
        int shown = Math.Min(list.Cap, list.Rows.Count);
        var files = new List<string>();
        var byFile = new Dictionary<string, List<ShowFinding>>(StringComparer.Ordinal);
        for (int i = 0; i < shown; i++)
        {
            var path = list.PathOf(list.Rows[i]);
            if (!byFile.TryGetValue(path, out var rows)) { byFile[path] = rows = []; files.Add(path); }
            rows.Add(list.Rows[i]);
        }
        foreach (var h in list.Health ?? []) if (!byFile.ContainsKey(h.Path)) { byFile[h.Path] = []; files.Add(h.Path); }
        files.Sort(string.CompareOrdinal);
        bool scale = false;
        var rules = new List<string>();
        string? firstId = null;
        foreach (var path in files)
        {
            w.WriteLine();
            var health = Find(list.Health, path);
            w.WriteLine(TextFormat.Heading(path, null, health?.Health.Score, ref scale));
            if (health is not null) w.WriteLine(Breakdown(health));
            var rows = byFile[path];
            rows.Sort((a, b) => a.Finding.Line.CompareTo(b.Finding.Line));
            foreach (var e in rows)
            {
                w.WriteLine(Line(e, list.Compared, null, list.Said?.Invoke(e)));
                TextFormat.Note(rules, e.Finding.Rule);
                firstId ??= e.Finding.Handle;
            }
        }
        if (list.Rows.Count > shown && list.More is { } more) w.WriteLine(more(list.Rows.Count - shown));
        foreach (var line in Closing(list, firstId, rules)) w.WriteLine(line);
    }

    /// <summary>The closing lines: the list's own, else the hint and the About lines.</summary>
    static IEnumerable<string> Closing(ShowList list, string? firstId, List<string> rules) =>
        list.Footer is { } footer ? footer(firstId, rules)
        : list.Hint is { } hint ? [hint, .. TextFormat.About(firstId, rules)]
        : TextFormat.About(firstId, rules);

    static ShowHealth? Find(IReadOnlyList<ShowHealth>? health, string path)
    {
        foreach (var h in health ?? []) if (h.Path == path) return h;
        return null;
    }

    /// <summary>Where a file's points went: <c>  points (problematic, syntax score): empty-catch 1 warning
    /// -0.8, ...</c> (every contribution).</summary>
    public static string Breakdown(ShowHealth h) =>
        $"{TextFormat.Indent}points ({h.Health.Band}, {h.Health.Kind} score): {HealthText.Contributions(h.Health.Contributions, int.MaxValue)}";
}
