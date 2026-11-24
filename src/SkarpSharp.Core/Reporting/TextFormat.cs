using System.Text;
using SkarpSharp.Findings;
using SkarpSharp.Health;
using SkarpSharp.Model;

namespace SkarpSharp.Reporting;

/// <summary>
/// One finding line of the text format (indented): <c>  worse line 18 Orders.Ship long-method: physical lines 81 -&gt; 97
/// (limit 70) [cdceba26]</c>. <see cref="File"/> goes before <c>line</c> where the heading is not the file
/// (<c>--group rule</c>); <see cref="Line"/> 0 leaves <c>line</c> out; <see cref="Notice"/> adds <c>(notice)</c> after the
/// rule (with a comparison only); <see cref="Limit"/> and <see cref="Id"/> are left out when null.
/// </summary>
public sealed class TextLine(string lead, string rule, string numbers)
{
    public string Lead { get; } = lead;
    public string Rule { get; } = rule;
    public string Numbers { get; } = numbers;
    public string? File { get; init; }
    public int Line { get; init; }
    public string Place { get; init; } = "";
    public bool Notice { get; init; }
    public string? Limit { get; init; }
    public string? Id { get; init; }

    public string Render()
    {
        var sb = new StringBuilder(TextFormat.Indent).Append(Lead);
        if (File is { Length: > 0 }) sb.Append(' ').Append(File);
        if (Line > 0) sb.Append(" line ").Append(Line.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (Place.Length > 0) sb.Append(' ').Append(Place);
        sb.Append(' ').Append(Rule);
        if (Notice) sb.Append(" (notice)");
        sb.Append(": ").Append(Numbers);
        if (Limit is not null) sb.Append(" (limit ").Append(Limit).Append(')');
        if (Id is { Length: > 0 }) sb.Append(" [").Append(Id).Append(']');
        return sb.ToString();
    }
}

/// <summary>
/// The text format: the one renderer of findings as text, used by <c>skarp analyze</c>,
/// <c>skarp check</c> (plain and in the Claude hook), <c>skarp show</c> and the git commit and push reports. One line per
/// finding, <c>&lt;lead&gt; line &lt;n&gt; &lt;place&gt; &lt;rule&gt;[ (notice)]: &lt;numbers&gt;[ (limit &lt;x&gt;)] [&lt;id&gt;]</c>,
/// under a file heading <c>&lt;path&gt; health &lt;old&gt; -&gt; &lt;new&gt;</c>. The text is for reading; its layout and wording
/// may change in any release (tools read JSON).
/// </summary>
public static class TextFormat
{
    /// <summary>The scale note, on the first health mention of a report only.</summary>
    public const string Scale = " (10 = healthy)";

    /// <summary>The indent of a finding line under its heading.</summary>
    public const string Indent = "  ";

    /// <summary>The lead word without a comparison: the level (<c>warning</c> or <c>notice</c>); <c>recorded</c> for a
    /// baseline entry no saved finding matches (no level known).</summary>
    public static string Level(string severity) => severity switch { "warning" => "warning", "notice" => "notice", _ => "recorded" };


    /// <summary>The limit of a finding: <c>70</c> for "over" thresholds, <c>at least 8</c> for "at least" ones; null for
    /// a structural finding, a composite or a clone group (their numbers say it).</summary>
    public static string? Limit(Finding f) =>
        f.Threshold is double t && f.Parts is null && f.Clone is null ? (f.Comparison == "at least" ? "at least " : "") + Condition.Num(t) : null;

    /// <summary>
    /// What a finding measured: <c>cognitive complexity 16</c>; a composite its parts on one line (<c>long (99 physical
    /// lines), complex (...)</c>); a clone group, and a finding without a metric, its message.
    /// </summary>
    public static string Numbers(Finding f) =>
        f.Parts is null && f.Clone is null && f.Value is double v && f.Metric is { Length: > 0 } m ? $"{ReportLayout.MetricLabel(m)} {Condition.Num(v)}" : f.Message;

    /// <summary>A change of a value: <c>physical lines 81 -&gt; 97</c> (a composite or clone group: its message).</summary>
    public static string Change(Finding f, double? old) =>
        old is double o && f.Parts is null && f.Clone is null && f.Value is double v && f.Metric is { Length: > 0 } m
            ? $"{ReportLayout.MetricLabel(m)} {Condition.Num(o)} -> {Condition.Num(v)}" : Numbers(f);

    /// <summary>A finding that dropped below its limit: <c>physical lines 75, now within the limit</c>; one whose member
    /// or type is gone: <c>method removed</c> (<c>type removed</c>, <c>removed</c>).</summary>
    public static string Fixed(string? metric, double? value, bool removed, string place)
    {
        if (removed) return place.StartsWith("type ", StringComparison.Ordinal) ? "type removed" : place == "(file)" ? "removed" : "method removed";
        return value is double v && metric is { Length: > 0 } m ? $"{ReportLayout.MetricLabel(m)} {Condition.Num(v)}, now within the limit" : "now within the limit";
    }

    /// <summary>The lead word of a finding read against a baseline (<c>new</c>, <c>worse</c>) when <paramref name="compared"/>,
    /// else (and for known findings) its level.</summary>
    public static string Lead(Finding f, bool compared) => !compared ? Level(f.Severity) : f.BaselineState switch
    {
        "new" => "new",
        "worsened" => "worse",
        _ => Level(f.Severity),
    };

    /// <summary>A finding's line as <c>analyze</c> and <c>show</c> print it: the lead from its baseline state when
    /// <paramref name="compared"/>, the old value for a worse one.</summary>
    public static string FindingLine(Finding f, bool compared, string? file = null)
    {
        var lead = Lead(f, compared);
        bool changed = lead is "new" or "worse";
        var numbers = lead == "worse" ? Change(f, f.BaselineValue) : Numbers(f);
        if (f.PossiblyRenamedFrom is { } from) numbers += $" (possibly renamed from {ReportLayout.RenamedName(from)})";
        return new TextLine(lead, f.Rule, numbers)
        {
            File = file, Line = f.Line, Place = ReportLayout.ShortName(f), Notice = changed && f.Severity == "notice", Limit = Limit(f), Id = f.Handle,
        }.Render();
    }

    /// <summary>What a finding measured with its limit, as a line prints it (<c>physical lines 22 -&gt; 25 (limit 20)</c>);
    /// the old value for a worse one when <paramref name="compared"/>.</summary>
    public static string Measured(Finding f, bool compared)
    {
        var numbers = compared && f.BaselineState == "worsened" ? Change(f, f.BaselineValue) : Numbers(f);
        return Limit(f) is { } limit ? $"{numbers} (limit {limit})" : numbers;
    }

    /// <summary>A finding's line without its place and line number (a table that has them in other cells):
    /// <c>worse long-method: physical lines 22 -&gt; 25 (limit 20) [6c1f0a2b]</c>.</summary>
    public static string Compact(Finding f, bool compared)
    {
        var lead = Lead(f, compared);
        return new TextLine(lead, f.Rule, lead == "worse" ? Change(f, f.BaselineValue) : Numbers(f))
        {
            Notice = lead is "new" or "worse" && f.Severity == "notice", Limit = Limit(f), Id = f.Handle,
        }.Render()[Indent.Length..];
    }

    /// <summary>
    /// A file heading: <c>src/Orders.cs health 8.8 -&gt; 6.7</c> when the shown score changed, <c>health 9.3</c> when it
    /// did not or nothing is compared, <c>health 7.4 (new file)</c> for a file new to git, only the path without a score;
    /// <see cref="Scale"/> after the first score of the report (<paramref name="scaleShown"/>).
    /// </summary>
    public static string Heading(string path, double? before, double? now, ref bool scaleShown, bool newFile = false)
    {
        if (now is not double n) return path;
        var text = before is double b && HealthText.Shown(b) != HealthText.Shown(n)
            ? $"{path} health {HealthText.Score(b)} -> {HealthText.Score(n)}"
            : $"{path} health {HealthText.Score(n)}";
        bool scale = !scaleShown;
        scaleShown = true;
        return newFile ? $"{text} (new file{(scale ? "; 10 = healthy" : "")})" : scale ? text + Scale : text;
    }

    /// <summary>The per-file fold line: <c>  +2 more in this file</c>, with what lists them.</summary>
    public static string MoreInFile(int n, string? how = null) => $"{Indent}+{n} more in this file{(how is null ? "" : " (" + how + ")")}";

    /// <summary>The closing lines: <c>About a problem: `skarp show &lt;id&gt;` eg: `skarp show 4cca475f`</c>
    /// (when an id was shown and <c>skarp show</c> can resolve it) and <c>About a rule: `skarp explain long-method`</c>
    /// (several rules: <c>About the rules: ...</c>; more than <see cref="RulesNamed"/>: the placeholder with an example).</summary>
    public static List<string> About(string? firstId, IReadOnlyList<string> rules)
    {
        var lines = new List<string>();
        if (firstId is not null) lines.Add($"About a problem: `skarp show <id>` eg: `skarp show {firstId}`");
        if (rules.Count == 1) lines.Add($"About a rule: `skarp explain {rules[0]}`");
        else if (rules.Count > RulesNamed) lines.Add($"About the rules: `skarp explain <rule>` eg: `skarp explain {rules[0]}`");
        else if (rules.Count > 1)
        {
            var sb = new StringBuilder("About the rules: ");
            for (int i = 0; i < rules.Count; i++) sb.Append(i == 0 ? "" : ", ").Append("`skarp explain ").Append(rules[i]).Append('`');
            lines.Add(sb.ToString());
        }
        return lines;
    }

    /// <summary>The most rules the About line names one by one.</summary>
    public const int RulesNamed = 3;

    /// <summary>Adds <paramref name="rule"/> to <paramref name="rules"/> once (print order).</summary>
    public static void Note(List<string> rules, string rule)
    {
        if (!rules.Contains(rule)) rules.Add(rule);
    }

    /// <summary><c>1 file</c>, <c>2 files</c>.</summary>
    public static string Count(int n, string noun) => $"{n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} {noun}{(n == 1 ? "" : "s")}";
}
