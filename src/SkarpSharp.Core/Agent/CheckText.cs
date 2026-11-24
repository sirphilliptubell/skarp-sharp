using SkarpSharp.Findings;
using SkarpSharp.Health;
using SkarpSharp.Reporting;

namespace SkarpSharp.Agent;

/// <summary>How a check prints: the budget of finding lines, paths and the gate, the short session id the fold
/// line names (null: it names files); whether the text is the Claude hook's.</summary>
public sealed class CheckPrint(int max, LineStyle style, string? session)
{
    public int Max { get; } = max;
    public LineStyle Style { get; } = style;
    public string? Session { get; } = session;
    /// <summary>The fold line in place of the session's or the files' (<c>check --staged</c> saves nothing).</summary>
    public Func<IReadOnlyList<AgentLine>, string>? More { get; init; }
    /// <summary>The Claude hook's text (<c>additionalContext</c>, <c>reason</c>): one <c>About Skarp:</c> line
    /// that holds the scale, in place of the About lines.</summary>
    public bool Hook { get; init; }
    /// <summary>The Claude hook's About line and duplicate-code note are due on this message
    /// (<see cref="AboutRhythm"/>); ignored outside the hook, which prints its About lines every time.</summary>
    public bool About { get; init; }
    /// <summary>Only the headings, finding lines and fold line (the MCP tool <c>check</c> writes its own first
    /// and closing lines): no verdict, notes, hints or About lines, and no <c>Skarp: </c> before the first heading.</summary>
    public bool Bare { get; init; }

    /// <summary>The same printing with the About line due.</summary>
    public CheckPrint WithAbout() => new(Max, Style, Session) { More = More, Hook = Hook, About = true, Bare = Bare };
}

/// <summary>
/// <c>skarp check</c>'s output in the text format, plain and in the Claude hook. Which
/// findings and health changes are shown: the budget keeps the first finding lines in
/// selection order (blocking ones first with <c>--gate</c>); only finding lines count. They print grouped by file under
/// the file's health (largest drop first), by line. Every message starts with <c>Skarp</c>; an edit's message has no
/// blank lines, the Stop's has one between files. (Loops over the existing types rather than new LINQ: Native AOT size.)
/// </summary>
public static class CheckText
{
    /// <summary>The Claude hook's About line (on the rhythm of <see cref="AboutRhythm"/>).</summary>
    public const string AboutSkarp =
        "About Skarp: code health notes after each C# edit (first against the last commit, then against the previous edit; health 10 = healthy). " +
        "About a problem: `skarp show <id>`; why a rule matters and how to fix it: `skarp explain <rule>`.";

    /// <summary>The one instruction of a gated Stop: what makes it pass.</summary>
    public const string StopWayOut = "Fix them, or tell the user why they should stay. Skarp asks once per turn.";

    /// <summary>One file of the output: its printed path, its health change or score, and its finding lines.</summary>
    sealed class Group(string path)
    {
        public string Path { get; } = path;
        public AgentLine? Health { get; set; }
        public double? Now { get; set; }
        public List<AgentLine> Lines { get; } = [];
        /// <summary>The file is past a health limit (its heading says which).</summary>
        public HealthBreach? Breach { get; set; }
        public double Delta => Health is { Value: double n } h ? n - (h.From ?? 10) : Breach is { } b ? b.Now - (b.Before ?? 10) : 0;
    }

    /// <summary>The output lines; <paramref name="printed"/> gets the finding lines printed (not those folded
    /// into <c>+N more</c>) and the health changes (every one is printed, as a heading).</summary>
    public static List<string> Lines(CheckOutcome outcome, CheckPrint print, out List<AgentLine> printed)
    {
        printed = [];
        if (outcome.Lines.Count == 0 && outcome.Health.Count == 0 && outcome.Breaches.Count == 0) return [];
        var style = print.Style;
        var findings = AgentFormat.Order(outcome.Lines);
        var ordered = new List<AgentLine>();
        // Blocking lines first, then the lines of files past a health limit.
        for (int rank = 0; rank < 3; rank++) foreach (var l in findings) if (Rank(l, style) == rank) ordered.Add(l);
        int max = Math.Max(1, print.Max);
        int keep = Math.Min(max, ordered.Count);
        var shown = ordered.GetRange(0, keep);
        var folded = ordered.GetRange(keep, ordered.Count - keep);
        printed.AddRange(shown);
        foreach (var (h, _) in outcome.Health) printed.Add(h);

        bool stop = outcome.EndOfTurn, tripped = style.Gate && outcome.GateTripped;
        bool bare = print.Bare;
        List<string> lines = bare ? [] : Preamble(outcome, print, ordered, shown, tripped);

        bool scale = print.Hook; // the hook's About line holds the scale
        var rules = new List<string>();
        string? firstId = null;
        foreach (var g in Groups(outcome, style, shown))
        {
            if (stop) lines.Add("");
            Add(lines, g.Breach is { } b && !bare ? LimitHeading(g.Path, b, ref scale) : TextFormat.Heading(g.Path, g.Health?.From ?? g.Breach?.Before, g.Health?.Value ?? g.Now ?? g.Breach?.Now, ref scale, g.Health?.NewFile == true), bare);
            g.Lines.Sort((a, b) => a.Line.CompareTo(b.Line));
            foreach (var l in g.Lines)
            {
                lines.Add(l.Render());
                TextFormat.Note(rules, l.Rule);
                firstId ??= l.Handle;
            }
        }
        if (folded.Count > 0) lines.Add(Fold(print, folded));
        if (stop && tripped) { lines.Add(""); lines.Add(StopWayOut); }
        if (max > 1 && !bare)
        {
            if (print.Hook) { if (print.About) lines.Add(AboutSkarp); }
            else lines.AddRange(TextFormat.About(firstId, rules));
        }
        return lines;
    }

    /// <summary>The lines before the first heading: the verdict, the notes and the duplicate-code hint.</summary>
    static List<string> Preamble(CheckOutcome outcome, CheckPrint print, List<AgentLine> ordered, List<AgentLine> shown, bool tripped)
    {
        var lines = new List<string>();
        if (Verdict(outcome, print, ordered, tripped) is { } verdict) lines.Add(verdict);
        foreach (var note in outcome.Header) Add(lines, note, false);
        if (AgentFormat.NeedsCloneHint(shown) && (!print.Hook || print.About)) Add(lines, print.Hook ? DuplicationText.ClaudeHint : DuplicationText.AgentHint, false);
        return lines;
    }

    /// <summary>A line, with <c>Skarp: </c> before it when it is the first (not in <paramref name="bare"/> text).</summary>
    static void Add(List<string> lines, string line, bool bare) => lines.Add(lines.Count == 0 && !bare ? "Skarp: " + line : line);

    /// <summary>
    /// The verdict line: a gated Stop that blocks (<c>Skarp did not end the turn: ...</c>), any other Stop (what it
    /// compares with), an edit whose lines block; null for an edit check that does not (its first heading starts with
    /// <c>Skarp: </c>).
    /// </summary>
    static string? Verdict(CheckOutcome outcome, CheckPrint print, List<AgentLine> ordered, bool tripped)
    {
        if (outcome.EndOfTurn && !tripped) return "Skarp, end of turn: not shown after the edits (compared with the start of the session)";
        if (!tripped) return null;
        int n = 0;
        foreach (var l in ordered) if (l.Blocks) n++;
        // The gate judges composite parts: when only they trip it, every new or worse line counts.
        if (n == 0 && (outcome.PartsTripped || outcome.Breaches.Count == 0)) foreach (var l in ordered) if (l.Kind is LineKind.New or LineKind.Worse && !l.PastLimit) n++;
        var problems = TextFormat.Count(n, "problem");
        // The files past a health limit, joined to the findings with a semicolon.
        int files = outcome.Breaches.Count;
        var health = HealthLimit.Verdict(files);
        if (outcome.EndOfTurn)
            return n == 0 ? $"Skarp did not end the turn: {health} this session edited."
                : $"Skarp did not end the turn: {problems} {(n == 1 ? "is" : "are")} new or worse in files this session edited{(files > 0 ? "; " + health : "")}.";
        if (print.Hook)
            return n == 0 ? $"Skarp: this edit took {health}, which blocks the end of the turn."
                : $"Skarp: this edit added {problems} that {(n == 1 ? "blocks" : "block")} the end of the turn{(files > 0 ? "; " + health : "")}.";
        return n == 0 ? $"Skarp: gate failed: {health}." : $"Skarp: gate failed: {problems} new or worse{(files > 0 ? "; " + health : "")}.";
    }

    /// <summary>The order lines are kept within the budget: blocking lines with the gate (0), then the lines of a
    /// file past its health limit (1), then the others (2).</summary>
    static int Rank(AgentLine l, LineStyle style) => style.Gate && l.Blocks ? 0 : l.PastLimit ? 1 : 2;

    /// <summary>The heading of a file past its health limit, with the scale note when it is the first score.</summary>
    static string LimitHeading(string path, HealthBreach b, ref bool scale)
    {
        var text = HealthLimit.Heading(path, b);
        if (!scale) text += TextFormat.Scale;
        scale = true;
        return text;
    }

    /// <summary>The files of the shown lines and of the health changes, the largest drop first, then the lowest score, then
    /// by path.</summary>
    static List<Group> Groups(CheckOutcome outcome, LineStyle style, List<AgentLine> shown)
    {
        var groups = new List<Group>();
        foreach (var (h, _) in outcome.Health) Of(groups, style.PathOf(h)).Health = h;
        foreach (var l in shown) Of(groups, style.PathOf(l)).Lines.Add(l);
        foreach (var b in outcome.Breaches) Of(groups, style.Display(b.Root, b.Path)).Breach = b;
        foreach (var s in outcome.Scores)
        {
            var path = style.Display(s.Root, s.Path);
            foreach (var g in groups) if (g.Path == path && g.Health is null) g.Now = s.Now;
        }
        groups.Sort((a, b) =>
        {
            int c = a.Delta.CompareTo(b.Delta);
            if (c == 0) c = (a.Health?.Value ?? a.Now ?? 10).CompareTo(b.Health?.Value ?? b.Now ?? 10);
            return c != 0 ? c : string.CompareOrdinal(a.Path, b.Path);
        });
        return groups;
    }

    static Group Of(List<Group> groups, string path)
    {
        foreach (var g in groups) if (g.Path == path) return g;
        var n = new Group(path);
        groups.Add(n);
        return n;
    }

    /// <summary>The fold line: the given one, <c>skarp show --session</c> with a session, else the files.</summary>
    static string Fold(CheckPrint print, List<AgentLine> folded)
    {
        if (print.More is { } more) return more(folded);
        if (print.Session is { } session) return AgentFormat.SessionMore(folded, session);
        var paths = new List<string>();
        foreach (var l in folded) paths.Add(print.Style.PathOf(l));
        return AgentFormat.More(folded.Count, AgentFormat.ShowFiles(paths));
    }
}
