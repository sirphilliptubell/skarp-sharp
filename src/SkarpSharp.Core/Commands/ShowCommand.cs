using System.Text.Json;
using SkarpSharp.Agent;
using SkarpSharp.Findings;
using SkarpSharp.Model;

namespace SkarpSharp.Commands;

/// <summary>
/// <c>skarp show</c> (experimental): slices the saved run (<c>.skarp/last-run/</c>
/// plus later checks) or the baseline without analyzing. Run from anywhere inside the analyzed tree.
/// </summary>
public static class ShowCommand
{
    public const string Usage = """
        skarp show: the findings of the saved run (.skarp/last-run/, written by every skarp analyze, plus later skarp check
        results), or of the baseline, without analyzing again. Run it anywhere inside the analyzed folder.

        Usage:
          skarp show <handle>            one finding in full: message, measure and limit, baseline state, the member's
                                         metrics; namespace-cycle: every member, every suggested cut and its references.
                                         A prefix of 4 or more characters is accepted; several matches are all listed.
          skarp show [filters]           the findings that match, one line each, grouped by file
          skarp show --session <id> [filters]
                                         the findings in the files a Claude Code session edited (its tracking files
                                         in ~/.skarp/tracked/, SKARP_HOME overrides ~/.skarp; every agent of the
                                         session), each folder with its own saved run. A prefix of 4 or more characters
                                         is accepted; an ambiguous one lists the sessions (exit 2).

        Filters and options:
          --file <path>          findings in this file (repeatable; relative to here or to the analyzed folder)
          --rule <rule>          findings of this rule (repeatable)
          --new-only             findings new or worse than in the baseline the run compared with; with --session,
                                 new or worse during the session, as the end-of-turn summary judges them
          --include-notices      notices too (default: warnings only; the last line then says how many notices
                                 the filters left out)
          --from last-run|baseline   the saved run (default) or the baseline files (ids and values only)
          --target <tfm>         the semantic results of this target framework (default: the highest)
          --format text|json     the text format (default; for reading, may change in any release) or JSON
          --all                  list every match (default: the first 50, then a line with the command for the rest)

        A line starts with the finding's level (warning or notice); with --new-only with what changed (new, worse,
        fixed). Paths are relative to the analyzed folder; with --session, files of other folders show their full path.

        Stale data is answered anyway, with a first line saying how many of the files shown changed since and how to
        refresh. Exit codes: 0 done, 1 tool error, 2 usage error (also: unknown handle, no saved run, unknown session).
        """;

    /// <summary>Lines a list shows without --all.</summary>
    public const int ListCap = 50;

    sealed class Args
    {
        public string? Handle;
        public List<string> Files = [];
        public List<string> Rules = [];
        public bool NewOnly, All;
        public Severity MinSeverity = Severity.Warning;
        /// <summary><c>--session</c>, a session id or prefix.</summary>
        public string? Session;
        public string From = "last-run", Format = "text";
        public string? Target;
        /// <summary>The arguments again (without --all), for the fold line.</summary>
        public List<string> Echo = [];
        /// <summary>The <c>--file</c> paths relative to the root (set once the root is known).</summary>
        public List<string> ResolvedFiles = [];
    }

    static string? Parse(IReadOnlyList<string> args, Args a)
    {
        for (int i = 1; i < args.Count; i++)
        {
            var name = args[i];
            string? Value() => i + 1 < args.Count ? args[++i] : null;
            switch (name)
            {
                case "--file": if (Value() is not { Length: > 0 } f) return "Option --file needs a path"; a.Files.Add(f); a.Echo.AddRange(["--file", f]); break;
                case "--rule":
                    if (Value() is not { } r || RuleCatalog.Find(r) is null) return $"Invalid value for --rule (expected a rule id: {string.Join(", ", RuleCatalog.All.Select(x => x.Id))})";
                    a.Rules.Add(r); a.Echo.AddRange(["--rule", r]); break;
                case "--new-only": a.NewOnly = true; a.Echo.Add(name); break;
                case "--all": a.All = true; break;
                case "--include-notices": a.MinSeverity = Severity.Notice; a.Echo.Add(name); break;
                case "--session":
                    if (Value() is not { } sid || !SessionFiles.ValidPrefix(sid)) return $"Option --session needs a session id or a prefix of {SessionFiles.MinPrefix} or more characters (letters, digits, '-', '_', '.')";
                    a.Session = sid; a.Echo.AddRange([name, sid]); break;
                case "--from":
                    var from = Value();
                    if (from is not ("last-run" or "baseline")) return "Invalid value for --from (expected last-run or baseline)";
                    a.From = from; a.Echo.AddRange([name, from]); break;
                case "--format":
                    var fmt = Value();
                    if (fmt is not ("text" or "json")) return "Invalid value for --format (expected text or json)";
                    a.Format = fmt; a.Echo.AddRange([name, fmt]); break;
                case "--target": if (Value() is not { Length: > 0 } t) return "Option --target needs a target framework"; a.Target = t; a.Echo.AddRange([name, t]); break;
                default:
                    if (name.StartsWith('-')) return $"Unknown option: {name}";
                    if (a.Handle is not null) return "show takes at most one handle";
                    var h = name.ToLowerInvariant();
                    if (h.Length < 4 || !h.All(char.IsAsciiHexDigit)) return $"Not a handle: {name} (a handle is 4 to 8 hexadecimal characters, as printed by skarp)";
                    a.Handle = h;
                    break;
            }
        }
        return Combination(a);
    }

    /// <summary>What <c>--session</c> does not combine with.</summary>
    static string? Combination(Args a) => a.Session is null ? null
        : a.Handle is not null ? "Give a handle or --session, not both"
        : a.From == "baseline" ? "--session reads the saved runs; it does not combine with --from baseline" : null;

    public static int Run(IReadOnlyList<string> args, CommandIO io)
    {
        if (args.Count > 1 && args[1] is "-h" or "--help") { io.Out.WriteLine(Usage); return 0; }
        var a = new Args();
        if (Parse(args, a) is { } error)
        {
            io.Error.WriteLine(error);
            io.Error.WriteLine("skarp show --help lists the options.");
            return 2;
        }
        if (a.Session is not null) return ShowSession.Run(io, a.Session, Filter(a), new ShowSession.Output(a.Format, a.All, a.Echo, a.Target));
        if (Root(io, a) is not { } root) return 2;
        try
        {
            var data = a.From == "baseline" ? Join.FromBaseline(root, a.Target) : Join.FromLastRun(root, a.Target);
            return Show(io, a, data, root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            io.Error.WriteLine($"skarp show: {ex.Message}");
            return 1;
        }
    }

    /// <summary>The root the data is read from (the saved run's or the baseline's, from here or the first --file up);
    /// null, after saying why on standard error, when there is none.</summary>
    static string? Root(CommandIO io, Args a)
    {
        var cwd = io.Cwd;
        var start = a.Files.Count > 0 && Path.IsPathRooted(a.Files[0]) ? Path.GetDirectoryName(a.Files[0]) ?? cwd : cwd;
        var root = LastRun.FindRoot(cwd, a.From == "baseline") ?? (start != cwd ? LastRun.FindRoot(start, a.From == "baseline") : null);
        if (root is null)
        {
            io.Error.WriteLine(a.From == "baseline"
                ? $"skarp show: no {Baselines.Baseline.FileName} found from {cwd} up to the git work tree root"
                : $"skarp show: no saved run found from {cwd} up to the git work tree root; skarp analyze <folder> saves one (or use --from baseline)");
            return null;
        }
        if (a.From == "baseline" && !File.Exists(Path.Combine(root, Baselines.Baseline.FileName)))
        {
            io.Error.WriteLine($"skarp show: no {Baselines.Baseline.FileName} in {root}; skarp baseline <folder> records one");
            return null;
        }
        return root;
    }

    /// <summary>The filters of a list, shared with <c>--session</c>.</summary>
    public sealed class Filters(IReadOnlyList<string> files, IReadOnlySet<string> rules, bool newOnly, Severity minSeverity)
    {
        public IReadOnlyList<string> Files { get; } = files;
        public IReadOnlySet<string> Rules { get; } = rules;
        public bool NewOnly { get; } = newOnly;
        public Severity MinSeverity { get; } = minSeverity;

        /// <summary>Every filter but the severity.</summary>
        public bool Matches(ShowFinding e, Func<ShowFinding, bool> inFiles, Func<ShowFinding, bool> isNew) =>
            inFiles(e) && (Rules.Count == 0 || Rules.Contains(e.Finding.Rule)) && (!NewOnly || isNew(e));

        public bool Shows(ShowFinding e) => MinSeverity == Severity.Notice || e.Finding.Severity == "warning";
    }

    static Filters Filter(Args a) => new(a.Files, new HashSet<string>(a.Rules, StringComparer.Ordinal), a.NewOnly, a.MinSeverity);

    /// <summary><c>+M notices in these files: add --include-notices</c> when the filters left notices out.</summary>
    public static string? NoticeHint(Filters filters, int notices) =>
        filters.MinSeverity == Severity.Warning && notices > 0 ? $"+{notices} notice{(notices == 1 ? "" : "s")} in these files: add --include-notices" : null;

    static int Show(CommandIO io, Args a, ShowData data, string root)
    {
        a.ResolvedFiles = [.. a.Files.Select(f => ShowFindings.RootRelative(root, io.Cwd, f))];
        var files = new HashSet<string>(a.ResolvedFiles, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var entries = ShowFindings.Unique(data);
        var refresh = "skarp analyze " + AgentFormat.Arg(Display(root, io.Cwd));
        if (a.Handle is { } handle)
        {
            var matches = ShowFindings.ByHandle(entries, handle);
            if (matches.Count == 0)
            {
                io.Error.WriteLine($"skarp show: no finding with handle {handle} in the {(data.From == "baseline" ? "baseline" : "saved run")} of {root}; skarp show --file <path> lists a file's findings");
                return 2;
            }
            if (matches.Count == 1) return Detail(io, a, data, matches[0], new View(refresh));
            return List(io, a, data, matches, new View(refresh) { Matches = [.. matches.Select(m => m.Finding.Handle!)] });
        }
        var filters = Filter(a);
        var matching = entries.Where(e => filters.Matches(e, x => files.Count == 0 || files.Contains(x.Finding.Path), x => x.Finding.BaselineState is "new" or "worsened")).ToList();
        var list = matching.Where(filters.Shows).ToList();
        return List(io, a, data, list, new View(refresh) { Hint = NoticeHint(filters, matching.Count(e => e.Finding.Severity == "notice")) });
    }

    static string Display(string root, string cwd)
    {
        var rel = Path.GetRelativePath(cwd, root).Replace('\\', '/');
        return rel == "." ? "." : rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? root.Replace('\\', '/') : rel;
    }

    /// <summary>How a list or a detail is shown: the refresh command, the handles a prefix matched (a list of matches)
    /// and the notice hint.</summary>
    sealed class View(string refresh)
    {
        public string Refresh { get; } = refresh;
        public List<string>? Matches { get; init; }
        public string? Hint { get; init; }
    }

    static int List(CommandIO io, Args a, ShowData data, List<ShowFinding> list, View view)
    {
        var (refresh, matches, hint) = (view.Refresh, view.Matches, view.Hint);
        if (matches is not null) hint = null;
        var ordered = ShowFindings.Order(list);
        var stale = Join.Stale(data, ordered.Select(e => e.Finding.Path), refresh);
        var health = Health(a, data);
        if (a.Format == "json")
        {
            WriteJson(io, new ShowReport { SchemaVersion = Join.ShowSchemaVersion, From = data.From, Root = data.Root, Stale = stale, Findings = ordered, Matches = matches, Health = health, SettingsFiles = SettingsFiles(data) });
            return 0;
        }
        var verdict = matches is not null
            ? $"{matches.Count} findings share this handle prefix; give more characters:"
            : $"{FindingCount(ordered.Count)} ({Source(data)})";
        ShowText.Write(io.Out, new ShowList(verdict, ordered, e => e.Finding.Path)
        {
            Notes = stale is null ? [] : [Join.StaleLine(stale, io.Now)], Cap = a.All || matches is not null ? int.MaxValue : ListCap,
            Compared = a.NewOnly, Health = health, More = n => More(n, a.Echo), Hint = hint,
        });
        return 0;
    }

    /// <summary><c>1 finding</c>, <c>12 findings</c>.</summary>
    public static string FindingCount(int n) => $"{n} finding{(n == 1 ? "" : "s")}";

    /// <summary>The fold line of a capped list: the same command with <c>--all</c>.</summary>
    public static string More(int more, IEnumerable<string> echo) => $"+{more} more: skarp show{string.Concat(echo.Select(x => " " + AgentFormat.Arg(x)))} --all";

    /// <summary>With <c>--file</c> on the saved run, each file's score and where its points went.</summary>
    static List<ShowHealth>? Health(Args a, ShowData data)
    {
        if (a.Files.Count == 0 || a.Handle is not null || data.From != "last-run") return null;
        return ShowFindings.Health(data, a.ResolvedFiles);
    }

    static string Source(ShowData data) => data.From == "baseline" ? "from the baseline files"
        : (data.WrittenAt is { } w ? $"saved {w}" + (data.Header?.Commit is { Length: >= 12 } c ? $" at commit {c[..12]}" : "") : "saved run") + MachineLevels(data);

    /// <summary>The saved run's settings levels (last run only).</summary>
    static List<Model.SettingsFileInfo>? SettingsFiles(ShowData data) => data.From == "last-run" ? data.Header?.SettingsFiles : null;

    /// <summary><c>; settings levels: personal, repository</c> when a system or personal file applied to the saved run.</summary>
    static string MachineLevels(ShowData data) =>
        SettingsFiles(data) is { } files && files.Any(f => f.Level is "system" or "personal") ? $"; settings levels: {string.Join(", ", files.Select(f => f.Level))}" : "";

    static void WriteJson(CommandIO io, ShowReport report)
    {
        io.Out.Flush();
        using var stdout = io.OpenStdout();
        JsonSerializer.Serialize(stdout, report, AgentJsonContext.Default.ShowReport);
        stdout.WriteByte((byte)'\n');
    }

    static int Detail(CommandIO io, Args a, ShowData data, ShowFinding e, View view)
    {
        var stale = Join.Stale(data, [e.Finding.Path], view.Refresh);
        if (a.Format == "json")
        {
            WriteJson(io, new ShowReport { SchemaVersion = Join.ShowSchemaVersion, From = data.From, Root = data.Root, Stale = stale, Findings = [e], SettingsFiles = SettingsFiles(data) });
            return 0;
        }
        ShowFindings.Detail(io.Out, data, e, stale is null ? null : Join.StaleLine(stale, io.Now));
        return 0;
    }
}
