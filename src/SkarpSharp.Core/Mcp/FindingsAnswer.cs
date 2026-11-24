using System.Globalization;
using ModelContextProtocol;
using SkarpSharp.Agent;
using SkarpSharp.Commands;
using SkarpSharp.Findings;

namespace SkarpSharp.Mcp;

/// <summary>
/// The text of a <c>findings</c> answer on the saved run's data: one finding in full
/// (as <c>skarp show &lt;id&gt;</c>, then the rule's "How to improve" paragraph), the matches of an id prefix, or one page of
/// the findings that match, with the stale line and closing lines that name the tools, never a command.
/// </summary>
internal sealed class FindingsAnswer(McpHost host, ShowData data)
{
    const string Refresh = "find_worst_code";
    readonly List<ShowFinding> _entries = ShowFindings.Unique(data);
    readonly StringWriter _w = new(CultureInfo.InvariantCulture) { NewLine = "\n" };

    public override string ToString() => _w.ToString();

    /// <summary>One finding in full, or the matches of a prefix.</summary>
    /// <exception cref="McpException">No finding has the id.</exception>
    public FindingsAnswer Find(string id)
    {
        var matches = ShowFindings.ByHandle(_entries, id);
        if (matches.Count == 0)
            throw new McpException($"no finding with id {id} in the saved run of {data.Root} (saved {Saved()}); findings file=<path> lists a file's findings");
        if (matches.Count > 1)
        {
            Write(ShowFindings.Order(matches), new Page($"{matches.Count} findings share the id prefix {id}; give more characters:"), Footer([], null));
            return this;
        }
        var e = matches[0];
        var stale = Join.Stale(data, [e.Finding.Path], Refresh);
        ShowFindings.Detail(_w, data, e, stale is null ? null : Join.StaleLine(stale, host.Now));
        if (RuleCatalog.Find(e.Finding.Rule) is not { } rule) return this;
        if (FindingsTool.HowToImprove(rule) is { } how)
        {
            _w.WriteLine();
            _w.WriteLine(how);
        }
        _w.WriteLine($"More about the rule: explain rule={rule.Id}");
        return this;
    }

    /// <summary>The findings that match the request's filters, one page, grouped by file.</summary>
    public FindingsAnswer List(FindingsRequest q)
    {
        List<string> files = q.File is null ? [] : [ShowFindings.RootRelative(data.Root, host.Cwd, q.File)];
        var matching = Matching(q, files);
        var ordered = ShowFindings.Order(matching.Where(e => q.Notices || e.Finding.Severity == "warning"));
        var page = ordered.Skip(q.Offset).Take(q.Limit).ToList();
        var parts = new Page(Verdict(q.Offset, page.Count, ordered.Count))
        {
            Compared = q.NewOnly,
            Health = files.Count > 0 && data.Header is not null ? ShowFindings.Health(data, files, host.SettingsLocations) : null,
        };
        if (q.NewOnly && !data.Entries.Exists(e => e.Finding.BaselineState is not null))
            parts.Notes.Add("The saved run was not compared with a baseline, so nothing is marked new or worse.");
        var tail = new List<string>();
        if (q.Offset + page.Count < ordered.Count) tail.Add($"next: offset={q.Offset + page.Count}");
        int notices = q.Notices ? 0 : matching.Count(e => e.Finding.Severity == "notice");
        if (notices > 0) tail.Add($"+{N(notices)} notice{(notices == 1 ? "" : "s")} left out: include_notices=true");
        Write(page, parts, Footer(tail, q.Rule));
        return this;
    }

    /// <summary>The entries that match file, rule and new_only, of either level.</summary>
    List<ShowFinding> Matching(FindingsRequest q, List<string> files)
    {
        var inFiles = new HashSet<string>(files, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var filters = new ShowCommand.Filters(files, new HashSet<string>(q.Rule is null ? [] : [q.Rule], StringComparer.Ordinal), q.NewOnly, Severity.Notice);
        return [.. _entries.Where(e => filters.Matches(e, x => inFiles.Count == 0 || inFiles.Contains(x.Finding.Path), x => x.Finding.BaselineState is "new" or "worsened"))];
    }

    /// <summary><c>findings 1 to 30 of 212 in the saved run (...)</c>, or why the page is empty.</summary>
    string Verdict(int offset, int shown, int total) =>
        shown > 0 ? $"findings {N(offset + 1)} to {N(offset + shown)} of {N(total)} in the saved run ({SavedLine()})"
        : total > 0 ? $"no findings at offset {N(offset)}: {N(total)} match in the saved run ({SavedLine()})"
        : $"no findings match in the saved run ({SavedLine()})";

    /// <summary>A list's first line, notes, whether rows are compared, and the file health breakdowns.</summary>
    sealed record Page(string Verdict)
    {
        public List<string> Notes { get; } = [];
        public bool Compared { get; init; }
        public List<ShowHealth>? Health { get; init; }
    }

    void Write(List<ShowFinding> rows, Page page, Func<string?, IReadOnlyList<string>, IEnumerable<string>> footer)
    {
        var stale = Join.Stale(data, rows.Select(e => e.Finding.Path), Refresh);
        if (stale is not null) page.Notes.Insert(0, Join.StaleLine(stale, host.Now));
        ShowText.Write(_w, new ShowList(page.Verdict, rows, e => e.Finding.Path)
        {
            Notes = page.Notes, Compared = page.Compared, Health = page.Health, Footer = footer,
        });
    }

    /// <summary>The closing lines: <paramref name="tail"/> (the next page, the notices left out), then the tools to call
    /// about a finding and about a rule (the filtered rule, else the first shown).</summary>
    static Func<string?, IReadOnlyList<string>, IEnumerable<string>> Footer(List<string> tail, string? rule) => (firstId, rules) =>
    {
        var lines = new List<string>(tail);
        if (firstId is not null) lines.Add($"About a finding: findings id={firstId}");
        if ((rule ?? rules.FirstOrDefault()) is { } about) lines.Add($"About a rule and how to fix it: explain rule={about}");
        return lines;
    };

    static string N(int v) => v.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>When the saved run was written (else the newest per-file record).</summary>
    string Saved() => data.Header?.WrittenAt ?? data.WrittenAt ?? "time unknown";

    /// <summary><c>saved 2026-11-20T10:12:03Z, 14 min ago; syntax and semantic tiers</c>.</summary>
    string SavedLine()
    {
        var when = Saved();
        var age = Paths.ParseUtc(when) is { } t ? $", {Join.Age(host.Now - t)} ago" : "";
        var what = data.Header is null ? "per-edit records only, syntax tier" : data.SemanticRan ? "syntax and semantic tiers" : "syntax tier";
        return $"saved {when}{age}; {what}";
    }
}
