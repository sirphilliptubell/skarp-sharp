using SkarpSharp.Baselines;
using SkarpSharp.Findings;
using SkarpSharp.Model;

namespace SkarpSharp.Agent;

/// <summary>One root of a session's files: by root-relative path, every agent's tracking entry of that file.</summary>
public sealed class SessionRoot(string root)
{
    public string Root { get; } = root;
    public Dictionary<string, List<TrackedFile>> Files { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The files a Claude Code session edited, for <c>skarp show --session</c>. They
/// come from the session's tracking files (<c>&lt;home&gt;/tracked/&lt;date&gt;-&lt;session&gt;.json</c>, every agent of
/// the session, every day the session ran), grouped by root. A finding counts as new in the session when the
/// <c>Stop</c> summary would report it: new or worse than the file's session-start state (or the baseline).
/// (Loops rather than LINQ over the new types: each generic instantiation costs Native AOT size.)
/// </summary>
public sealed class SessionFiles
{
    /// <summary>The shortest session prefix accepted.</summary>
    public const int MinPrefix = 4;

    public required string Session { get; init; }
    /// <summary>The roots in ordinal order.</summary>
    public required List<SessionRoot> Roots { get; init; }

    /// <summary>Whether <paramref name="prefix"/> can name a session (4 or more of the characters a session file name keeps).</summary>
    public static bool ValidPrefix(string prefix) =>
        prefix.Length >= MinPrefix && prefix.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>The sessions whose id starts with <paramref name="prefix"/>, keyed by session id; each value holds the
    /// session's tracking files, oldest day first, joined by a line feed (a string map keeps the AOT size down).</summary>
    public static SortedDictionary<string, string> Matching(string home, string prefix)
    {
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var folder = Path.Combine(home, "tracked");
        if (!Directory.Exists(folder)) return found;
        var files = Directory.GetFiles(folder, "*.json");
        Array.Sort(files, StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (TrackingStore.SessionOf(Path.GetFileName(file)) is not { } session || !session.StartsWith(prefix, StringComparison.Ordinal)) continue;
            found[session] = found.TryGetValue(session, out var list) ? list + "\n" + file : file;
        }
        return found;
    }

    /// <summary>The session's files from its tracking files (a later day's entry of the same agent and file wins).</summary>
    public static SessionFiles Load(string session, IEnumerable<string> trackingFiles)
    {
        // By agent and file ("<agent>\n<path>"): a later day's entry replaces an earlier one.
        var byKey = new SortedDictionary<string, TrackedFile>(StringComparer.Ordinal);
        foreach (var file in trackingFiles)
            if (TrackingStore.Read(file) is { } state) Collect(state, byKey);
        var roots = new List<SessionRoot>();
        foreach (var entry in byKey.Values)
        {
            if (entry.Root.Length == 0 || entry.Path.Length == 0) continue;
            var root = roots.Find(r => LineStyle.SameRoot(r.Root, entry.Root));
            if (root is null) roots.Add(root = new SessionRoot(entry.Root));
            if (!root.Files.TryGetValue(entry.Path, out var list)) root.Files[entry.Path] = list = [];
            list.Add(entry);
        }
        roots.Sort((a, b) => string.CompareOrdinal(a.Root, b.Root));
        return new SessionFiles { Session = session, Roots = roots };
    }

    static void Collect(TrackingState state, SortedDictionary<string, TrackedFile> byKey)
    {
        foreach (var (agent, a) in state.Agents)
            foreach (var (key, entry) in a.Files) byKey[agent + "\n" + key] = entry;
    }

    /// <summary>The ids and values of a root's baseline (a finding the baseline holds at that value or more is known).</summary>
    public static Dictionary<string, double?> Known(string root)
    {
        var known = new Dictionary<string, double?>(StringComparer.Ordinal);
        var path = Path.Combine(root, Baseline.FileName);
        if (File.Exists(path))
            foreach (var e in Baseline.Read(path).Findings) known[e.Id] = e.Value;
        return known;
    }

    /// <summary>
    /// Whether a finding of the session's file is new in the session: a finding a check can judge is judged as the
    /// <c>Stop</c> summary judges it, against every agent's session-start state; any other (semantic, history and clone
    /// findings, composites, and a saved run's <c>large-type</c>, which may be of a partial type) by its state against
    /// the baseline.
    /// </summary>
    public static bool IsNew(ShowFinding e, List<TrackedFile>? tracked, Dictionary<string, double?> known) =>
        e.Change is not ("resolved" or "removed") && (Checkable(e, tracked) ? Against(e, tracked!, known) is not null : e.Finding.BaselineState is "new" or "worsened");

    /// <summary>What a finding new or worse in the session says (<c>worse: physical lines 81 -&gt; 109 (limit 70)</c>,
    /// as the <c>Stop</c> summary would), or null (not new in the session, or not judged against it).</summary>
    public static string? Said(ShowFinding e, List<TrackedFile>? tracked, Dictionary<string, double?> known) =>
        e.Change is not ("resolved" or "removed") && Checkable(e, tracked) ? Against(e, tracked!, known)?.Said : null;

    /// <summary>A finding a check can judge.</summary>
    static bool Checkable(ShowFinding e, List<TrackedFile>? tracked) =>
        tracked is not null && e.Tier == "syntax" && e.Finding.Parts is null && !FileCheck.LeftOut.Contains(e.Finding.Rule)
            && (e.Finding.Rule != RuleIds.LargeType || e.Source == "check");

    static AgentLine? Against(ShowFinding e, List<TrackedFile> tracked, Dictionary<string, double?> known)
    {
        var origins = new List<SortedDictionary<string, double?>>();
        foreach (var t in tracked) origins.Add(t.Origin ?? CheckRun.OpenValues(t.Findings));
        return CheckRun.AgainstOrigin(e.Finding, origins, known);
    }
}
