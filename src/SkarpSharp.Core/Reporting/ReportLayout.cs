using System.Globalization;
using SkarpSharp.Findings;
using SkarpSharp.Model;

namespace SkarpSharp.Reporting;

/// <summary>One place of the report: its findings (in report order) and its first line.</summary>
public sealed class PlaceEntry(List<Finding> findings)
{
    public List<Finding> Findings { get; } = findings;
    public Finding First => Findings[0];
    public string Path => First.Path;
    public int Line => Findings.Min(f => f.Line);
}

/// <summary>One file of the report with the places shown and the number left out by the per-file cap.</summary>
public sealed class FileGroup
{
    public required string Path { get; init; }
    public int? Commits { get; init; }
    /// <summary>All eligible places of the file (in report order).</summary>
    public required List<PlaceEntry> Places { get; init; }
    public List<PlaceEntry> Shown { get; } = [];
    public int Hidden => Places.Count - Shown.Count;
    /// <summary>True when <c>--top</c> (the place budget), not the per-file cap, hid some of the file's places.</summary>
    public bool CutByTop { get; set; }
    public int FindingCount => Places.Sum(p => p.Findings.Count);
    /// <summary>Worst value divided by its limit over the file's eligible findings.</summary>
    public double WorstRatio => Places.SelectMany(p => p.Findings).Max(FindingEngine.Ratio);
}

/// <summary>The places chosen for a report view.</summary>
public sealed class ReportView
{
    public List<FileGroup> Files { get; } = [];
    public int TotalPlaces { get; init; }
    public int TotalFiles { get; init; }
    public int ShownPlaces => Files.Sum(f => f.Shown.Count);
    public int ShownFiles => Files.Count(f => f.Shown.Count > 0);
}

/// <summary>
/// Shared pieces of the text, Markdown and GitHub outputs: short place names, <c>path:line</c>, the measure
/// text without the threshold source, the per-file cap, and the "thresholds used" footer.
/// </summary>
public static class ReportLayout
{
    /// <summary>
    /// Chooses the places to show: files in report order, at most <see cref="ReportOptions.PerFile"/> places per
    /// file (0 = no cap), except that a file's warning places are always shown and its notice places fill the
    /// remaining places up to the cap; then places up to <see cref="ReportOptions.Top"/> in total (warnings first when
    /// <c>--top</c> cuts a file). Shown places keep report order. <see cref="ReportOptions.All"/> shows everything.
    /// </summary>
    public static ReportView Select(IReadOnlyList<Finding> eligible, ReportOptions o)
    {
        var entries = Entries(eligible);
        var groups = new List<FileGroup>();
        foreach (var e in entries)
        {
            if (groups.Count == 0 || groups[^1].Path != e.Path)
                groups.Add(new FileGroup { Path = e.Path, Commits = e.First.Commits, Places = [] });
            groups[^1].Places.Add(e);
        }
        var view = new ReportView { TotalPlaces = entries.Count, TotalFiles = groups.Count };
        int perFile = o.All || o.PerFile <= 0 ? int.MaxValue : o.PerFile;
        int budget = o.All ? int.MaxValue : Math.Max(0, o.Top);
        foreach (var g in groups)
        {
            if (budget <= 0) break;
            var shown = Pick(g.Places, perFile, budget);
            g.CutByTop = shown.Count < Pick(g.Places, perFile, int.MaxValue).Count;
            g.Shown.AddRange(shown);
            budget -= shown.Count;
            view.Files.Add(g);
        }
        return view;
    }

    /// <summary>True when a place has a warning finding.</summary>
    public static bool HasWarning(PlaceEntry p) => p.Findings.Exists(f => f.Severity == "warning");

    /// <summary>
    /// The places of one file to show: every warning place (up to <paramref name="budget"/>), then notice places
    /// while the total is under <paramref name="perFile"/> and the budget; the result in report order.
    /// </summary>
    static List<PlaceEntry> Pick(List<PlaceEntry> places, int perFile, int budget)
    {
        int warnings = Math.Min(places.Count(HasWarning), budget);
        int notices = Math.Max(0, Math.Min(perFile, budget) - warnings);
        var picked = new List<PlaceEntry>();
        foreach (var p in places)
        {
            if (HasWarning(p)) { if (warnings-- > 0) picked.Add(p); }
            else if (notices-- > 0) picked.Add(p);
        }
        return picked;
    }

    /// <summary>Consecutive findings of the same place (the findings are in report order, so a place is contiguous).</summary>
    public static List<PlaceEntry> Entries(IReadOnlyList<Finding> findings)
    {
        var entries = new List<PlaceEntry>();
        foreach (var f in findings)
        {
            if (entries.Count > 0 && entries[^1].Path == f.Path && FindingEngine.Place(entries[^1].First) == FindingEngine.Place(f))
                entries[^1].Findings.Add(f);
            else entries.Add(new PlaceEntry([f]));
        }
        return entries;
    }

    /// <summary><c>path:line</c>, the path relative to the analyzed folder with forward slashes (clickable in terminals and editors).</summary>
    public static string PathLine(string path, int line) => $"{path.Replace('\\', '/')}:{line}";

    /// <summary>
    /// The short name of a place: type and member without namespace, parameters or overload ordinal
    /// (<c>MemberCollector.Collect/Add</c>); nested types joined with <c>.</c>; lambdas as <c>/lambdaN</c>; top-level
    /// statements <c>&lt;top-level&gt;</c>; type places <c>type OrderBook</c>; file places <c>(file)</c>. The full id
    /// stays in the JSON.
    /// </summary>
    public static string ShortName(Finding f)
    {
        if (f.Member is null) return "(file)";
        // Namespace places span files (namespace-cycle); a clone group is named after the member of
        // its first copy (the id holds the group's hash).
        if (f.PathlessId) return f.Member ?? f.PlaceKey!;
        if (f.TypePlace) return "type " + ShortType(f.PlaceKey ?? f.Member);
        var key = f.PlaceKey ?? f.Member;
        // Old-style records without a key: a type place is a key without "::" that is not top-level code.
        if (!key.Contains("::", StringComparison.Ordinal) && !key.StartsWith("<top-level>", StringComparison.Ordinal))
            return "type " + ShortType(key);
        return ShortKey(key);
    }

    /// <summary>Short form of an identity key (<c>Ns.Outer+Inner`1::Run#2/lambda1</c> gives <c>Outer.Inner.Run/lambda1</c>).</summary>
    public static string ShortKey(string key)
    {
        if (key.StartsWith("<top-level>", StringComparison.Ordinal)) return key;
        int sep = key.IndexOf("::", StringComparison.Ordinal);
        if (sep < 0) return ShortType(key);
        var type = ShortType(key[..sep]);
        var rest = key[(sep + 2)..];
        // The member segment may carry the overload ordinal (#2); nested segments follow after '/'.
        int slash = rest.IndexOf('/');
        var member = slash < 0 ? rest : rest[..slash];
        var nested = slash < 0 ? "" : rest[slash..];
        int hash = member.LastIndexOf('#');
        if (hash > 0 && member[(hash + 1)..].All(char.IsAsciiDigit)) member = member[..hash];
        member = member switch { ".ctor" => "ctor", ".cctor" => "cctor", _ => member };
        return $"{type}.{member}{nested}";
    }

    /// <summary><c>Ns.Outer+Inner`1</c> gives <c>Outer.Inner</c>: namespace and generic arity dropped, nested types joined with '.'.</summary>
    public static string ShortType(string typeName)
    {
        int plus = typeName.IndexOf('+');
        var head = plus < 0 ? typeName : typeName[..plus];
        var tail = plus < 0 ? "" : typeName[plus..];
        int dot = head.LastIndexOf('.');
        var name = (dot < 0 ? head : head[(dot + 1)..]) + tail;
        var parts = name.Split('+').Select(p => { int tick = p.IndexOf('`'); return tick < 0 ? p : p[..tick]; });
        return string.Join('.', parts);
    }

    /// <summary>The words for a threshold's metric in the measure and footer (<c>WMC</c>, <c>source lines</c>).</summary>
    public static string MetricLabel(string? metric) => metric switch
    {
        null => "",
        "wmc" => "WMC",
        "physicalLines" => "physical lines",
        "sourceLines" => "source lines",
        "booleanOperators" => "boolean operators",
        "constructorParameters" => "constructor parameters",
        "npath" => "NPath",
        "cognitive" => "cognitive complexity",
        "cyclomatic" => "cyclomatic complexity",
        "nesting" => "nesting depth",
        "magicNumbers" => "magic numbers",
        "copiedLines" => "lines in all copies",
        _ => metric,
    };

    /// <summary>
    /// What was measured and the limit it passed (<c>nesting depth 4 (over 3)</c>), without the threshold source, which
    /// the "thresholds used" footer and <c>skarp explain</c> name once per rule. A message with context in
    /// parentheses (<c>WMC 21 (also 62 source lines)</c>) gets the limit right after the value that crossed it.
    /// </summary>
    public static string Measure(Finding f)
    {
        if (f.Threshold is not double t) return f.Message;
        var limit = $"({f.Comparison} {Condition.Num(t)})";
        const string also = " (also ";
        int i = f.Message.IndexOf(also, StringComparison.Ordinal);
        return i < 0 ? $"{f.Message} {limit}" : $"{f.Message[..i]} {limit}{f.Message[i..]}";
    }

    /// <summary>Ratio as "1.4 x limit".</summary>
    public static string RatioText(double ratio) => ratio.ToString("0.0", CultureInfo.InvariantCulture) + " x limit";

    /// <summary>The short place of a baseline id (<c>rule|path|place</c>).</summary>
    public static string RenamedName(string id)
    {
        var parts = id.Split('|');
        if (parts.Length < 3) return id;
        var place = parts[2];
        int tilde = place.LastIndexOf('~');
        if (tilde > 0 && place[(tilde + 1)..].All(char.IsAsciiDigit)) place = place[..tilde];
        return place.Length == 0 ? "(file)" : place.Contains("::", StringComparison.Ordinal) || place.StartsWith("<top-level>", StringComparison.Ordinal)
            ? ShortKey(place) : "type " + ShortType(place);
    }

    /// <summary>One footer row per rule, level and threshold used by the findings shown (the source once per rule).</summary>
    public static List<(string Rule, string Severity, string Limit, string Source)> ThresholdsUsed(IEnumerable<Finding> shown)
    {
        var rows = new List<(string Rule, string Severity, string Limit, string Source)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in shown.SelectMany(BrainRules.Flatten).OrderBy(f => RuleCatalog.Get(f.Rule).Order).ThenBy(f => FindingEngine.SeverityRank(f.Severity)))
        {
            var limit = f.Threshold is double t ? $"{MetricLabel(f.Metric)} {f.Comparison} {Condition.Num(t)}".Trim() : "structural";
            var source = $"{f.Preset}: {f.ThresholdSource}";
            if (seen.Add($"{f.Rule}|{f.Severity}|{limit}|{source}")) rows.Add((f.Rule, f.Severity, limit, source));
        }
        return rows;
    }

    /// <summary>
    /// The analyzed directory relative to the root of its git work tree ("" at the root; null when not in one): GitHub
    /// annotations name files relative to the repository root.
    /// </summary>
    public static string? RepositoryPrefix(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (RepositoryRoot(full) is not { } root) return null;
        var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
        return rel == "." ? "" : rel.TrimEnd('/') + "/";
    }

    /// <summary>The root of the git work tree holding <paramref name="directory"/> (a folder with <c>.git</c>); null when not in one.</summary>
    public static string? RepositoryRoot(string directory)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(directory));
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
