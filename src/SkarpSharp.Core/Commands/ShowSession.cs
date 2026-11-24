using System.Text.Json;
using SkarpSharp.Agent;
using SkarpSharp.Findings;

namespace SkarpSharp.Commands;

/// <summary>
/// <c>skarp show --session &lt;id&gt;</c>, the findings in the files a Claude Code
/// session edited, across every agent of the session and every root, each root answered from its own saved run
/// (<c>.skarp/last-run/</c> joined with later <c>skarp check</c> results). Paths of the working folder's root stay
/// relative; others are absolute. A root without a saved run gets a <c>note</c> line with the command that makes one.
/// (Loops and the existing <c>List&lt;ShowFinding&gt;</c> rather than new LINQ instantiations: Native AOT size.)
/// </summary>
public static class ShowSession
{
    /// <summary>The output options of the command line.</summary>
    public sealed class Output(string format, bool all, IReadOnlyList<string> echo, string? target)
    {
        public string Format { get; } = format;
        public bool All { get; } = all;
        public IReadOnlyList<string> Echo { get; } = echo;
        public string? Target { get; } = target;
    }

    /// <summary>One run of the command: its input and output, filters and line style, and what the roots gave: the
    /// findings shown (each with its <see cref="ShowFinding.Root"/>), the lines before them, the roots without a saved
    /// run, the gate set of each root and the notices left out.</summary>
    sealed class Request(CommandIO io, ShowCommand.Filters filters, Output output, LineStyle style, HashSet<string> wanted)
    {
        public CommandIO Io { get; } = io;
        public ShowCommand.Filters Filters { get; } = filters;
        public Output Output { get; } = output;
        public LineStyle Style { get; } = style;
        public HashSet<string> Wanted { get; } = wanted;
        public List<ShowFinding> Rows { get; } = [];
        public List<string> Header { get; } = [];
        public List<string> NoRun { get; } = [];
        public int Notices { get; set; }
        /// <summary>With <c>--new-only</c>, what each finding new in the session says, by root and id.</summary>
        public Dictionary<string, string> Said { get; } = new(StringComparer.Ordinal);

        /// <summary>The path as printed (absolute for another root than the working folder's).</summary>
        public string PathOf(ShowFinding e) => Style.Display(e.Root, e.Finding.Path);
    }

    public static int Run(CommandIO io, string prefix, ShowCommand.Filters filters, Output output)
    {
        var sessions = SessionFiles.Matching(io.HomeFolder, prefix);
        if (sessions.Count != 1)
        {
            io.Error.WriteLine(sessions.Count == 0
                ? $"skarp show: no session starting with {prefix} in {Path.Combine(io.HomeFolder, "tracked")} (skarp check --hook claude writes one per Claude Code session)"
                : $"skarp show: {sessions.Count} sessions start with {prefix}; give more characters:");
            foreach (var (id, files) in sessions) io.Error.WriteLine($"  {id} (started {Path.GetFileName(files.Split('\n')[0])[..10]})");
            return 2;
        }
        foreach (var (session, trackingFiles) in sessions)
        {
            try
            {
                return Show(io, SessionFiles.Load(session, trackingFiles.Split('\n')), filters, output);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                io.Error.WriteLine($"skarp show: {ex.Message}");
                return 1;
            }
        }
        return 2;
    }

    static int Show(CommandIO io, SessionFiles s, ShowCommand.Filters filters, Output output)
    {
        var style = new LineStyle(FileCheck.RootOfFolder(Path.GetFullPath(io.Cwd)), false);
        var wanted = new HashSet<string>(PathComparer);
        foreach (var f in filters.Files) wanted.Add(Path.GetFullPath(Path.IsPathRooted(f) ? f : Path.Combine(io.Cwd, f)));
        var q = new Request(io, filters, output, style, wanted);
        foreach (var root in s.Roots) Collect(q, root);
        q.Rows.Sort((a, b) => Compare(q, a, b));
        if (output.Format == "json") return Json(io, s, q);
        ShowText.Write(io.Out, new ShowList(Count(s, q.Rows.Count), q.Rows, q.PathOf)
        {
            Notes = q.Header, Cap = output.All ? int.MaxValue : ShowCommand.ListCap, Compared = filters.NewOnly,
            Said = e => q.Said.GetValueOrDefault((e.Root ?? "") + "|" + e.Finding.Id),
            More = n => ShowCommand.More(n, output.Echo), Hint = ShowCommand.NoticeHint(filters, q.Notices),
        });
        return 0;
    }

    /// <summary>Warnings first, then by printed path and line.</summary>
    static int Compare(Request q, ShowFinding a, ShowFinding b)
    {
        int c = FindingEngine.SeverityRank(a.Finding.Severity).CompareTo(FindingEngine.SeverityRank(b.Finding.Severity));
        if (c == 0) c = string.CompareOrdinal(q.PathOf(a), q.PathOf(b));
        return c != 0 ? c : a.Finding.Line.CompareTo(b.Finding.Line);
    }

    /// <summary>The verdict line (after <c>Skarp: </c>).</summary>
    static string Count(SessionFiles s, int n)
    {
        int files = 0;
        foreach (var root in s.Roots) files += root.Files.Count;
        return $"{ShowCommand.FindingCount(n)} in the files of session {s.Session} ({files} file{(files == 1 ? "" : "s")}, {s.Roots.Count} folder{(s.Roots.Count == 1 ? "" : "s")})";
    }

    /// <summary>One root: its saved run joined with later checks, the entries in the session's files that match.</summary>
    static void Collect(Request q, SessionRoot sr)
    {
        var (root, files, filters) = (sr.Root, sr.Files, q.Filters);
        var data = Directory.Exists(root) ? Join.FromLastRun(root, q.Output.Target) : null;
        if (data?.Header is null)
        {
            q.NoRun.Add(root);
            q.Header.Add($"{Slash(root)}: no saved run (only skarp check results); skarp analyze {AgentFormat.Arg(Slash(root))} saves one");
        }
        if (data is null) return;
        var known = filters.NewOnly ? SessionFiles.Known(root) : [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var shown = new List<string>();
        foreach (var e in data.Entries)
        {
            if (!seen.Add(e.Finding.Id) || !files.TryGetValue(e.Finding.Path, out var tracked)) continue;
            if (!filters.Matches(e, x => q.Wanted.Count == 0 || q.Wanted.Contains(Path.Combine(root, x.Finding.Path)), x => SessionFiles.IsNew(x, tracked, known))) continue;
            if (!filters.Shows(e)) { q.Notices++; continue; }
            e.Root = root;
            if (filters.NewOnly && SessionFiles.Said(e, tracked, known) is { } said) q.Said[root + "|" + e.Finding.Id] = said;
            q.Rows.Add(e);
            shown.Add(e.Finding.Path);
        }
        if (Join.Stale(data, shown, "skarp analyze " + AgentFormat.Arg(Slash(root))) is { } stale) q.Header.Add(Join.StaleLine(stale, q.Io.Now));
    }

    static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    static string Slash(string path) => path.Replace('\\', '/');

    static int Json(CommandIO io, SessionFiles s, Request q)
    {
        var roots = new List<string>();
        string? main = null;
        foreach (var r in s.Roots)
        {
            roots.Add(r.Root);
            if (main is null && q.Style.Home is { } home && LineStyle.SameRoot(r.Root, home)) main = r.Root;
        }
        main ??= roots.Count > 0 ? roots[0] : "";
        foreach (var e in q.Rows) e.Root = e.Root is { } r && LineStyle.SameRoot(r, main) ? null : e.Root;
        var report = new ShowReport
        {
            SchemaVersion = Join.ShowSchemaVersion, From = "last-run", Root = main, Session = s.Session, Roots = roots,
            NoSavedRun = q.NoRun.Count > 0 ? q.NoRun : null, Findings = q.Rows,
        };
        io.Out.Flush();
        using var stdout = io.OpenStdout();
        JsonSerializer.Serialize(stdout, report, AgentJsonContext.Default.ShowReport);
        stdout.WriteByte((byte)'\n');
        return 0;
    }
}
