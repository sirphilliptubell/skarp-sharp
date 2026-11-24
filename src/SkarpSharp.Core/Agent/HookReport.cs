using System.Globalization;
using SkarpSharp.Findings;
using SkarpSharp.Health;
using SkarpSharp.Model;
using SkarpSharp.Reporting;

namespace SkarpSharp.Agent;

/// <summary>One finding of the hook report: where, which rule, new or worse, whether it blocks, what
/// it measured as the text format prints it (<c>physical lines 81 -&gt; 97</c>), its limit and id. A class, not a record:
/// Native AOT size (no generated equality members).</summary>
public sealed class HookItem(string path, int line, string place, string rule, LineKind kind, bool blocks, string numbers)
{
    public string Path { get; } = path;
    public int Line { get; } = line;
    public string Place { get; } = place;
    public string Rule { get; } = rule;
    public LineKind Kind { get; } = kind;
    public bool Blocks { get; } = blocks;
    public string Numbers { get; } = numbers;
    /// <summary>The limit as printed (<c>70</c>, <c>at least 8</c>); null for none.</summary>
    public string? Limit { get; init; }
    /// <summary>A notice (the line says <c>(notice)</c>).</summary>
    public bool Notice { get; init; }
    /// <summary>A new or worse finding of a file past its health limit (listed under it, notices too).</summary>
    public bool PastLimit { get; init; }
    /// <summary>The finding's id (handle), printed at the end of its line; null: none.</summary>
    public string? Handle { get; init; }

    /// <summary>The item's line in the text format.</summary>
    public string Render() => new TextLine(Kind == LineKind.Worse ? "worse" : "new", Rule, Numbers)
    {
        Line = Line, Place = Place, Notice = Notice, Limit = Limit, Id = Handle,
    }.Render();

    /// <summary>The item of a check or summary line.</summary>
    public static HookItem From(string path, AgentLine line, bool blocks) => new(path, line.Line, line.Place, line.Rule, line.Kind, blocks, line.Text)
    {
        Limit = line.Limit, Notice = line.Severity == "notice", Handle = line.Handle, PastLimit = line.PastLimit,
    };
}

/// <summary>One file's code health in the hook report: before (null: no earlier score) and now (null: unknown);
/// in a check's outcome also the root the path is relative to.</summary>
public sealed class HookHealth(string path, double? before, double? now, string? root = null)
{
    public string? Root { get; } = root;
    public string Path { get; } = path;
    public double? Before { get; } = before;
    public double? Now { get; } = now;
}

/// <summary>What the hook report is built from.</summary>
public sealed class HookReportData
{
    /// <summary><c>commit</c> or <c>push</c>.</summary>
    public string What { get; init; } = "commit";
    public List<HookItem> Items { get; } = [];
    public List<HookHealth> Files { get; } = [];
    /// <summary>The files past a health limit (paths as printed).</summary>
    public List<HealthBreach> Breaches { get; } = [];
    /// <summary>The push hook's project health (<c>Project health 7.0 over 2 files (was 10.0)</c>); null on commit.</summary>
    public string? Project { get; init; }
    /// <summary>The project's shown average moved against the baseline (a passing push says so).</summary>
    public bool ProjectMoved { get; init; }
    /// <summary><c>skarp show</c> resolves the ids (the push hook's saved run), so the report names it.</summary>
    public bool ShowIds { get; init; }
    /// <summary>Claude runs git (<c>CLAUDECODE=1</c>), so the report offers Claude's ways out.</summary>
    public bool Claude { get; set; }
}

/// <summary>
/// The report the commit hook (<c>check --staged --gate</c>) and the push hook (<c>analyze --gate
/// --push-check</c>) print for a person: a verdict with counts first, the findings grouped by file under the file's
/// health (worst drop first), the ways out last. One line per finding in the text format
/// (<see cref="TextFormat"/>), the id at its end. Built from structured data. A passing check prints at most one line.
/// No colour, no wrapping; the caller writes it to standard error.
/// </summary>
public static class HookReport
{
    /// <summary>The report of a blocked commit or push. Loops rather than LINQ over the report's own types: Native AOT
    /// size (each generic instantiation over a new type costs about 2 KB).</summary>
    public static List<string> Blocked(HookReportData data)
    {
        var (blocking, all) = Blocking(data);
        var listed = Listed(data, blocking);
        var files = Files(data, listed);
        var lines = new List<string> { Verdict(data.What, blocking, data.Breaches.Count) };
        if (data.Project is { } project) lines.Add(project);
        var rules = new List<string>();
        string? firstId = null;
        bool scaleShown = false;
        files.Sort((a, b) => Drop(b, data).CompareTo(Drop(a, data)) is var c && c != 0 ? c : string.CompareOrdinal(a, b));
        foreach (var path in files)
        {
            lines.Add("");
            lines.Add(Heading(path, data, ref scaleShown));
            firstId = Group(lines, rules, firstId, listed, path);
            int more = 0;
            if (!all) foreach (var i in data.Items) if (i.Path == path && !listed.Contains(i)) more++;
            if (more > 0) lines.Add(TextFormat.MoreInFile(more));
        }
        lines.Add("");
        lines.AddRange(WaysOut(data.What, listed.Count + data.Breaches.Count > 1, data.Claude));
        lines.AddRange(TextFormat.About(data.ShowIds ? firstId : null, rules));
        return lines;
    }

    /// <summary>The findings that block; when the gate tripped on something no line shows as blocking (it judges composite
    /// parts) and no file is past a health limit, every new or worse one (<c>All</c>).</summary>
    static (List<HookItem> Blocking, bool All) Blocking(HookReportData data)
    {
        var blocking = new List<HookItem>();
        foreach (var i in data.Items) if (i.Blocks) blocking.Add(i);
        bool all = blocking.Count == 0 && data.Breaches.Count == 0;
        if (all) blocking.AddRange(data.Items);
        return (blocking, all);
    }

    /// <summary>The blocking findings and the new or worse findings of files past a health limit,
    /// notices too.</summary>
    static List<HookItem> Listed(HookReportData data, List<HookItem> blocking)
    {
        var listed = new List<HookItem>(blocking);
        foreach (var i in data.Items) if (i.PastLimit && !listed.Contains(i)) listed.Add(i);
        return listed;
    }

    /// <summary>The files of the listed findings and the files past a health limit.</summary>
    static List<string> Files(HookReportData data, List<HookItem> listed)
    {
        var files = new List<string>();
        foreach (var i in listed) if (!files.Contains(i.Path)) files.Add(i.Path);
        foreach (var b in data.Breaches) if (!files.Contains(b.Path)) files.Add(b.Path);
        return files;
    }

    /// <summary>A file's heading: the limit it is past (<c>src/A.cs health 8.3 -&gt; 7.1 (down 1.2, limit under
    /// 1.0)</c>), else its health.</summary>
    static string Heading(string path, HookReportData data, ref bool scaleShown)
    {
        HealthBreach? breach = null;
        foreach (var b in data.Breaches) if (b.Path == path) breach = b;
        if (breach is null)
        {
            var health = Find(data.Files, path);
            return TextFormat.Heading(path, health?.Before, health?.Now, ref scaleShown);
        }
        var text = HealthLimit.Heading(path, breach);
        if (!scaleShown) text += TextFormat.Scale;
        scaleShown = true;
        return text;
    }

    /// <summary>One file's blocking findings by line; their rules added to <paramref name="rules"/>; returns the first id
    /// shown in the report.</summary>
    static string? Group(List<string> lines, List<string> rules, string? firstId, List<HookItem> blocking, string path)
    {
        var shown = new List<HookItem>();
        foreach (var i in blocking) if (i.Path == path) shown.Add(i);
        shown.Sort((a, b) => a.Line.CompareTo(b.Line));
        foreach (var item in shown)
        {
            lines.Add(item.Render());
            TextFormat.Note(rules, item.Rule);
            firstId ??= item.Handle;
        }
        return firstId;
    }

    /// <summary><c>Skarp stopped this commit: 3 problems (2 new, 1 worse) in 2 staged files.</c></summary>
    static string Verdict(string what, List<HookItem> blocking, int limited)
    {
        // The files past a health limit, joined with a semicolon.
        var health = limited == 0 ? "" : HealthLimit.Verdict(limited, what == "commit" ? "staged" : "");
        if (blocking.Count == 0) return $"Skarp stopped this {what}: {health}.";
        int n = blocking.Count, worse = 0;
        var files = new List<string>();
        foreach (var i in blocking)
        {
            if (i.Kind == LineKind.Worse) worse++;
            if (!files.Contains(i.Path)) files.Add(i.Path);
        }
        int added = n - worse;
        var problems = worse == 0 ? $"{n} new {Plural(n, "problem")}"
            : added == 0 ? $"{n} worse {Plural(n, "problem")}"
            : $"{n} problems ({added} new, {worse} worse)";
        var where = what == "commit" ? $"{files.Count} staged {Plural(files.Count, "file")}" : $"{files.Count} {Plural(files.Count, "file")}";
        return $"Skarp stopped this {what}: {problems} in {where}{(health.Length > 0 ? "; " + health : "")}.";
    }

    static HookHealth? Find(List<HookHealth> health, string path)
    {
        foreach (var h in health) if (h.Path == path) return h;
        return null;
    }

    static string Plural(int n, string word) => n == 1 ? word : word + "s";

    /// <summary>A file's shown health drop (no score: no drop); the files print with the largest drop first, then by path.</summary>
    static double Drop(string path, HookReportData data)
    {
        foreach (var b in data.Breaches) if (b.Path == path) return (b.Before ?? HealthEngine.BaselineDefault) - b.Now;
        return Find(data.Files, path) is { Before: double b0, Now: double n } ? HealthText.Shown(b0) - HealthText.Shown(n) : 0;
    }

    /// <summary>The three numbered ways out; "them" for <paramref name="several"/> problems. For Claude (<paramref name="claude"/>) the second and third become one, which leaves the decision to
    /// the user and names no way around the check.</summary>
    public static List<string> WaysOut(string what, bool several, bool claude)
    {
        var lines = new List<string>
        {
            "What you can do:",
            what == "commit" ? $"  1. Fix {(several ? "them" : "it")}, then stage and commit again." : $"  1. Fix {(several ? "them" : "it")}, commit the fix, and push again.",
        };
        if (claude) lines.Add(ClaudeWayOut);
        else
        {
            lines.Add($"  2. Accept {(several ? "them" : "it")} as known:   `skarp baseline . && git add .skarp-baseline.json`   then {(what == "commit" ? "commit" : "commit and push")}");
            lines.Add($"  3. Skip the check once:  `git {what} --no-verify`");
        }
        return lines;
    }

    /// <summary>Claude's second way out of a blocked commit or push.</summary>
    public const string ClaudeWayOut = "  2. Or stop and tell the user which problems should stay and why; the user decides.";

    /// <summary>Whether the environment says Claude Code runs this process (<c>CLAUDECODE=1</c>).</summary>
    public static bool UnderClaude(Configuration.SettingsLocations? locations) =>
        (locations ?? Configuration.SettingsLocations.Default).Variable("CLAUDECODE") == "1";

    /// <summary>
    /// A passing commit: nothing, or one line when a staged file's shown health changed
    /// (<c>Skarp: commit ok. src/Orders.cs health 7.2 -> 8.1 (better)</c>; several files: <c>2 files healthier, 1 less healthy</c>).
    /// A passing push: one line when the project's average moved.
    /// </summary>
    public static string? Passed(HookReportData data)
    {
        if (data.What == "push") return data.ProjectMoved && data.Project is { } p ? "Skarp: push ok. " + p : null;
        HookHealth? first = null;
        int better = 0, worse = 0;
        foreach (var f in data.Files)
        {
            if (f.Before is not double b || f.Now is not double n || HealthText.Shown(b) == HealthText.Shown(n)) continue;
            first ??= f;
            if (n > b) better++; else worse++;
        }
        if (first is null) return null;
        if (better + worse == 1)
            return $"Skarp: commit ok. {first.Path} health {HealthText.Score(first.Before!.Value)} -> {HealthText.Score(first.Now!.Value)} ({(better == 1 ? "better" : "worse")})";
        var parts = new List<string>();
        if (better > 0) parts.Add($"{better} {Plural(better, "file")} healthier");
        if (worse > 0) parts.Add(better > 0 ? $"{worse} less healthy" : $"{worse} {Plural(worse, "file")} less healthy");
        return $"Skarp: commit ok. {string.Join(", ", parts)}.";
    }

    // ---- sources ----------------------------------------------------------------------------------------------------

    /// <summary>The commit hook's data: the check's new and worse lines, and each staged file's score. Nothing is saved,
    /// so the report does not name <c>skarp show</c>.</summary>
    public static HookReportData FromCheck(CheckOutcome outcome, LineStyle style)
    {
        var data = new HookReportData { What = "commit" };
        foreach (var line in outcome.Lines)
        {
            if (line.Kind is not (LineKind.New or LineKind.Worse)) continue;
            bool blocks = line.Blocks || (outcome.CompositeIds.Contains(line.Id) && line.PartsBlock == true);
            data.Items.Add(HookItem.From(style.PathOf(line), line, blocks));
        }
        foreach (var s in outcome.Scores) data.Files.Add(new HookHealth(style.Display(s.Root, s.Path), s.Before, s.Now));
        foreach (var b in outcome.Breaches) data.Breaches.Add(new HealthBreach(style.Display(b.Root, b.Path), b.Before, b.Now, b.Rule, b.Limits));
        return data;
    }

    /// <summary>The push hook's data from the analysis: the summary's new and worse findings, each file's score against
    /// the baseline, and the project's; <paramref name="saved"/>: the run was saved, so <c>skarp show</c> resolves the ids.</summary>
    public static HookReportData FromAnalysis(AnalysisResult result, Severity minSeverity, IReadOnlyList<string>? gateOn, bool saved)
    {
        var h = result.Health;
        var data = new HookReportData { What = "push", Project = Project(h), ProjectMoved = Moved(h), ShowIds = saved };
        // The files past a health limit list their notices too.
        var limited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in AnalysisLimits.Breaches(result.Gate?.Health)) { data.Breaches.Add(b); limited.Add(b.Path); }
        foreach (var line in AgentSummary.Entries(result, limited.Count > 0 ? Severity.Notice : minSeverity, gateOn))
        {
            if (line.Kind is not (LineKind.New or LineKind.Worse)) continue;
            bool past = limited.Contains(line.Path);
            if (!past && minSeverity == Severity.Warning && line.Severity != "warning") continue;
            bool blocks = line.Blocks || line.PartsBlock == true;
            data.Items.Add(HookItem.From(line.Path, line with { PastLimit = past }, blocks));
        }
        foreach (var file in result.Files)
            if (file.Health is { } fh) data.Files.Add(new HookHealth(file.Path, fh.BaselineScore, fh.Score));
        return data;
    }

    /// <summary><c>Project health 7.0 over 2 files (was 10.0)</c>; null without an average.</summary>
    static string? Project(HealthSummary? h)
    {
        if (h?.Average is not double a) return null;
        var was = h.Baseline?.Average is double b ? $" (was {HealthText.Score(b)})" : "";
        return $"Project health {HealthText.Score(a)} over {h.Files.ToString("N0", CultureInfo.InvariantCulture)} {Plural(h.Files, "file")}{was}";
    }

    static bool Moved(HealthSummary? h) =>
        h?.Baseline is { AverageNow: double n, Average: double o } && HealthText.Shown(n) != HealthText.Shown(o);
}
