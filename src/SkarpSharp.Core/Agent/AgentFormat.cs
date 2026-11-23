using SkarpSharp.Findings;
using SkarpSharp.Model;
using SkarpSharp.Reporting;

namespace SkarpSharp.Agent;

/// <summary>What a line says about its finding (the order is the selection order: worse first).</summary>
public enum LineKind { Health = -1, Worse = 0, New = 1, Better = 2, Present = 3, Resolved = 4 }

/// <summary>
/// One finding of <c>skarp check</c> and the hooks, or (<see cref="LineKind.Health"/>) a file's health change.
/// <see cref="Text"/> is what the finding measured as the text format prints it (<c>physical lines 81 -&gt; 97</c>);
/// <see cref="Place"/> and <see cref="Limit"/> complete the line (<see cref="TextFormat"/>).
/// </summary>
public sealed record AgentLine(string Severity, string Path, int Line, string Rule, string Handle, string Text, LineKind Kind, string Id)
{
    /// <summary>The finding's value the line states (a health line: the new score as shown); null when none.</summary>
    public double? Value { get; init; }
    /// <summary>The earlier value the line was built with (a health line: the earlier score as shown, null for a
    /// file new to git).</summary>
    public double? From { get; init; }
    /// <summary>The earlier value the line states: a worse or better line's; null for the others.</summary>
    public double? Stated => Kind is LineKind.Worse or LineKind.Better ? From : null;
    /// <summary>A resolved line whose member or type is gone.</summary>
    public bool Removed { get; init; }
    /// <summary>The root <see cref="Path"/> is relative to (absolute); null when not known.</summary>
    public string? Root { get; init; }
    /// <summary>A warning in the gate set (<c>gate.on</c> of its root's settings).</summary>
    public bool Gated { get; init; }
    /// <summary>A composite's line: whether its parts trip the gate (null for other lines).</summary>
    public bool? PartsBlock { get; init; }
    /// <summary>A line that trips <c>--gate</c>: a gate-set warning that is new or worse (a composite: and its
    /// parts trip the gate, which judges them).</summary>
    public bool Blocks => Gated && Kind is LineKind.Worse or LineKind.New && PartsBlock != false;
    /// <summary>The short place (<c>Orders.Ship</c>, <c>type Orders</c>, <c>(file)</c>).</summary>
    public string Place { get; init; } = "";
    /// <summary>The limit as the line prints it (<c>70</c>, <c>at least 8</c>); null for none.</summary>
    public string? Limit { get; init; }
    /// <summary>A new or worse line of a file past its health limit: listed whatever its severity, kept
    /// at the Stop like a blocking line.</summary>
    public bool PastLimit { get; init; }
    /// <summary>A health line of a file new to git.</summary>
    public bool NewFile { get; init; }

    /// <summary>A file's health line: its score now and before (as shown; null before for a file new to git).</summary>
    public static AgentLine Health(string path, double? before, double now) =>
        new("health", path, 0, "", "", "", LineKind.Health, "health|" + path) { Value = now, From = before, NewFile = before is null };

    /// <summary>What the line says about its finding, as the tracking state records it (<c>worse</c>, <c>new</c>,
    /// <c>better</c>, <c>resolved</c>, <c>removed</c>, <c>present</c>).</summary>
    public string Change => Kind switch
    {
        LineKind.Worse => "worse", LineKind.New => "new", LineKind.Better => "better", LineKind.Present => "present",
        LineKind.Resolved => Removed ? "removed" : "resolved", _ => "health",
    };

    /// <summary>The lead word: the change (<c>worse</c>, <c>new</c>, <c>better</c>, <c>fixed</c>), or, for a
    /// finding simply present (no comparison), its level.</summary>
    public string Lead => Kind switch
    {
        LineKind.Worse => "worse", LineKind.New => "new", LineKind.Better => "better", LineKind.Resolved => "fixed",
        _ => TextFormat.Level(Severity),
    };

    /// <summary>What the check said, as <c>skarp show</c> repeats it (<c>worse: physical lines 81 -&gt; 97 (limit 70)</c>).</summary>
    public string Said => $"{Lead}: {Text}{(Limit is null ? "" : $" (limit {Limit})")}";

    /// <summary>The line in the text format, <paramref name="path"/> before <c>line</c> when given.</summary>
    public string Render(string? path = null) => new TextLine(Lead, Rule, Text)
    {
        File = path, Line = Kind == LineKind.Resolved && Removed ? 0 : Line, Place = Place,
        Notice = Kind != LineKind.Present && Severity == "notice", Limit = Limit, Id = Handle,
    }.Render();
}

/// <summary>
/// The findings of <c>skarp check</c> as lines (the text format, <see cref="TextFormat"/>): what each says, the
/// print order, the fold line and the duplicate-code note.
/// </summary>
public static class AgentFormat
{
    /// <summary>The short place of a finding (<c>Parser.Parse</c>, <c>type Parser</c>, <c>(file)</c>).</summary>
    public static string Place(Finding f)
    {
        Restore(f);
        return ReportLayout.ShortName(f);
    }

    /// <summary>
    /// Fields a finding read back from JSON lacks (they are not serialized): the place key, whether it is a type place and
    /// whether the id is path-less, recovered from the id (<c>rule|path|place[~n]</c>); and the handle for older files.
    /// </summary>
    public static void Restore(Finding f)
    {
        f.Handle ??= FindingEngine.HandleOf(f.Id);
        if (f.PlaceKey is not null || f.Id.Length == 0) return;
        var parts = f.Id.Split('|', 3);
        if (parts.Length < 3) return;
        var place = parts[2];
        int tilde = place.LastIndexOf('~');
        if (tilde > 0 && place[(tilde + 1)..].All(char.IsAsciiDigit)) place = place[..tilde];
        if (place.Length == 0) return;
        f.PathlessId = parts[1].Length == 0;
        f.PlaceKey = place;
        f.TypePlace = !f.PathlessId && !place.Contains("::", StringComparison.Ordinal) && !place.StartsWith("<top-level>", StringComparison.Ordinal);
    }

    /// <summary>A line for a finding that is new, worse, better or simply present (no comparison).</summary>
    public static AgentLine For(Finding f, LineKind kind, double? old = null)
    {
        Restore(f);
        var text = kind is LineKind.Worse or LineKind.Better ? TextFormat.Change(f, old) : TextFormat.Numbers(f);
        return new AgentLine(f.Severity, f.Path, f.Line, f.Rule, f.Handle!, text, kind, f.Id)
        {
            Value = f.Value, From = old, Place = ReportLayout.ShortName(f), Limit = TextFormat.Limit(f),
        };
    }

    /// <summary>A line for a finding that dropped below its threshold (or whose place went away).</summary>
    public static AgentLine Resolved(ResolvedFinding r)
    {
        var f = new Finding { Id = r.Id, Rule = r.Rule, Path = r.Path };
        Restore(f);
        f.Member = f.PlaceKey; // the place's name; null for file findings
        var place = ReportLayout.ShortName(f);
        return new AgentLine(r.Severity, r.Path, r.Line, r.Rule, f.Handle!, TextFormat.Fixed(r.Metric, r.Value, r.Removed == true, place), LineKind.Resolved, r.Id)
        {
            Value = r.Value, Removed = r.Removed == true, Place = place,
        };
    }

    /// <summary>The selection order: worse, new, better, present, resolved; warnings before notices within each; else the
    /// given order. (The budget keeps the first lines; the text prints them grouped by file, by line.)</summary>
    public static List<AgentLine> Order(IEnumerable<AgentLine> lines) =>
        [.. lines.Select((l, i) => (l, i)).OrderBy(x => (int)x.l.Kind).ThenBy(x => FindingEngine.SeverityRank(x.l.Severity)).ThenBy(x => x.i).Select(x => x.l)];

    /// <summary>The plain fold line <c>+N more: &lt;command&gt;</c>.</summary>
    public static string More(int count, string command) => $"+{count} more: {command}";

    /// <summary>A shell-safe argument: plain when it holds only safe characters, else in double quotes.</summary>
    public static string Arg(string s) =>
        s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '-' or '_' or ':' or '+' or '=' or ',') ? s : "\"" + s.Replace("\"", "'", StringComparison.Ordinal) + "\"";

    /// <summary>Whether a duplicate-code or duplicate-code-near line is among the lines (the
    /// duplicate-code note is then printed once).</summary>
    public static bool NeedsCloneHint(IEnumerable<AgentLine> lines) =>
        lines.Any(l => l.Rule is RuleIds.DuplicateCode or RuleIds.DuplicateCodeNear);

    /// <summary>The most file names a fold line lists.</summary>
    public const int MaxFoldFiles = 3;

    /// <summary><c>skarp show --file a --file b</c> for the files of the folded lines (in first-seen order); plain
    /// <c>skarp show</c> when they span more than <see cref="MaxFoldFiles"/> files.</summary>
    public static string ShowFiles(IEnumerable<string> paths)
    {
        var files = paths.Distinct(StringComparer.Ordinal).ToList();
        return files.Count > MaxFoldFiles ? "skarp show" : "skarp show" + string.Concat(files.Select(p => " --file " + Arg(p)));
    }

    /// <summary>The hook's fold line, <c>+N more: skarp show --session &lt;id&gt;
    /// --new-only</c>; <paramref name="session"/> is the short session id.</summary>
    public static string SessionMore(IReadOnlyList<AgentLine> folded, string session) =>
        More(folded.Count, $"skarp show --session {Arg(session)} --new-only");

    /// <summary>The characters of a session id the fold line prints (<c>skarp show --session</c> takes a prefix).</summary>
    public const int ShortSession = 8;

    /// <summary>The short form of a session id.</summary>
    public static string Short(string session) => session.Length > ShortSession ? session[..ShortSession] : session;
}

/// <summary>
/// Where lines are printed: the path relative to its root when that root is <paramref name="home"/> (the
/// project of the working folder or of the hook input's <c>cwd</c>), else absolute with <c>/</c>; and whether the run
/// gates (<c>--gate</c>).
/// </summary>
public sealed class LineStyle(string? home, bool gate)
{
    /// <summary>The root paths stay relative to (null: every path stays relative).</summary>
    public string? Home { get; } = home;
    /// <summary><c>--gate</c>: blocking lines are selected first and named in the verdict.</summary>
    public bool Gate { get; } = gate;

    public string PathOf(AgentLine l) => Display(l.Root, l.Path);

    /// <summary>A root-relative path as printed: unchanged under <see cref="Home"/>, else absolute.</summary>
    public string Display(string? root, string path)
    {
        if (root is null || Home is null) return path;
        return SameRoot(root, Home) ? path : System.IO.Path.Combine(root, path).Replace('\\', '/');
    }

    public static bool SameRoot(string a, string b) =>
        string.Equals(System.IO.Path.TrimEndingDirectorySeparator(a), System.IO.Path.TrimEndingDirectorySeparator(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
