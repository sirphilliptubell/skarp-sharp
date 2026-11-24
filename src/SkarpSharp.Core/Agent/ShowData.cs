using System.Globalization;
using SkarpSharp.Baselines;
using SkarpSharp.Findings;
using SkarpSharp.Model;

namespace SkarpSharp.Agent;

/// <summary>The findings <c>skarp show</c> answers from, joined from the saved run (or the baseline) and later checks.</summary>
public sealed class ShowData
{
    public required string Root { get; init; }
    /// <summary><c>last-run</c> or <c>baseline</c>.</summary>
    public required string From { get; init; }
    public LastRunHeader? Header { get; init; }
    public List<ShowFinding> Entries { get; } = [];
    /// <summary>The SHA-256 of each file as the data saw it (for the stale warning).</summary>
    public Dictionary<string, string> Hashes { get; } = new(StringComparer.Ordinal);
    /// <summary>The newest time any of the data was written (the saved run or a later check).</summary>
    public string? WrittenAt { get; set; }
    /// <summary>Source lines of each analyzed file of the saved run (the score model's size input).</summary>
    public Dictionary<string, int> SourceLines { get; } = new(StringComparer.Ordinal);
    /// <summary>The saved run holds the semantic tier's results (scores are then of kind <c>full</c>).</summary>
    public bool SemanticRan { get; set; }
}

/// <summary>
/// The joining rule of <c>skarp show</c>: the analysis document is primary; the semantic documents add
/// their findings, from the highest target framework unless <c>--target</c> names another; a later
/// <c>skarp check</c> of a file replaces that file's syntax findings (except <c>active-complex-file</c> and the
/// <c>large-type</c> of partial types, which a check cannot judge) and adds what it reported resolved.
/// </summary>
public static class Join
{
    /// <summary>Schema version of <c>skarp show --format json</c> (contracts.md section 2).</summary>
    public const int ShowSchemaVersion = 1;

    /// <summary>The semantic document of the highest target framework, or the one named; null when there is none.</summary>
    public static SemanticResult? Highest(IReadOnlyList<SemanticResult> results, string? target = null)
    {
        if (target is not null) return results.FirstOrDefault(r => string.Equals(r.Run.TargetFramework, target, StringComparison.OrdinalIgnoreCase));
        return results.OrderByDescending(r => r.Run.TargetFramework is { } t ? Semantic.TargetFrameworks.Key(t) : default).FirstOrDefault();
    }

    public static ShowData FromLastRun(string root, string? target)
    {
        var saved = LastRun.Load(root);
        var records = CheckRecords.ReadAll(root)
            .Where(r => saved is null || string.CompareOrdinal(r.WrittenAt, saved.Header.WrittenAt) >= 0).ToList();
        var data = new ShowData { Root = root, From = "last-run", Header = saved?.Header, WrittenAt = saved?.Header.WrittenAt };
        if (saved is not null)
            foreach (var (path, hash) in saved.Header.Files) data.Hashes[path] = hash;
        var byPath = records.ToDictionary(r => r.Path, StringComparer.Ordinal);

        if (saved?.Analysis is { } analysis)
        {
            foreach (var file in analysis.Files) data.SourceLines[file.Path] = file.Lines.Source;
            var members = MemberIndex(analysis.Files.Select(f => (f.Path, (IEnumerable<MemberResult>)f.Members)));
            foreach (var f in analysis.Findings ?? [])
            {
                if (byPath.TryGetValue(f.Path, out var rec)
                    && !(f.Rule == RuleIds.ActiveComplexFile || (f.Rule == RuleIds.LargeType && rec.PartialTypes.Contains(f.Member ?? ""))))
                    continue;
                data.Entries.Add(Entry(f, "syntax", null, "last-run", members));
            }
            // The composites (brain-method, brain-class) of files no later check replaced, with their parts.
            if (analysis.Composites is { } composites)
                foreach (var c in BrainRules.StandIns(analysis.Findings ?? [], [.. composites.Where(c => !byPath.ContainsKey(c.Path))]))
                {
                    AgentFormat.Restore(c);
                    data.Entries.Add(new ShowFinding { Finding = c, Tier = "syntax", Source = "last-run", Parts = [.. c.Parts!.Select(p => p.Id)] });
                }
        }
        foreach (var rec in records)
        {
            data.Hashes[rec.Path] = rec.Sha256;
            if (data.WrittenAt is null || string.CompareOrdinal(rec.WrittenAt, data.WrittenAt) > 0) data.WrittenAt = rec.WrittenAt;
            var members = MemberIndex([(rec.Path, rec.Members)]);
            foreach (var f in rec.Findings)
            {
                var e = Entry(f, "syntax", null, "check", members);
                e.Change = rec.Changes.GetValueOrDefault(f.Id);
                data.Entries.Add(e);
            }
            foreach (var r in rec.Resolved)
            {
                var f = new Finding
                {
                    Id = r.Id, Handle = r.Handle, Rule = r.Rule, Severity = r.Severity, Path = r.Path, Line = r.Line, Metric = r.Metric,
                    Value = r.Value, Threshold = r.Threshold,
                    Message = r.Removed == true ? "resolved: the member or type was removed" : "resolved: no longer over its threshold",
                };
                data.Entries.Add(new ShowFinding { Finding = f, Tier = "syntax", Source = "check", Change = r.Removed == true ? "removed" : "resolved" });
            }
        }
        if (saved is not null && Highest(saved.Semantic, target) is { } sem)
        {
            data.SemanticRan = true;
            var components = (sem.Namespaces?.Components ?? []).ToDictionary(c => c.Place, StringComparer.Ordinal);
            foreach (var f in sem.Findings)
            {
                AgentFormat.Restore(f);
                data.Entries.Add(new ShowFinding
                {
                    Finding = f, Tier = "semantic", TargetFramework = sem.Run.TargetFramework, Source = "last-run",
                    Component = f.Rule == RuleIds.NamespaceCycle && f.Member is { } m ? components.GetValueOrDefault(m) : null,
                });
            }
        }
        return data;
    }

    static Dictionary<(string, string), MemberResult> MemberIndex(IEnumerable<(string Path, IEnumerable<MemberResult> Members)> files)
    {
        var index = new Dictionary<(string, string), MemberResult>();
        foreach (var (path, ms) in files)
            foreach (var m in ms) index.TryAdd((path, m.Id), m);
        return index;
    }

    static ShowFinding Entry(Finding f, string tier, string? tfm, string source, Dictionary<(string, string), MemberResult> members)
    {
        AgentFormat.Restore(f);
        return new ShowFinding
        {
            Finding = f, Tier = tier, TargetFramework = tfm, Source = source,
            Metrics = !f.TypePlace && !f.PathlessId && f.Member is { } m ? members.GetValueOrDefault((f.Path, m)) : null,
        };
    }

    /// <summary>
    /// <c>--from baseline</c> (part B5): the ids and values of <c>.skarp-baseline.json</c> and the semantic baseline (the
    /// highest target's file, or <c>--target</c>), without analyzing. Severity, line and message come from the saved run
    /// when it holds the same id; otherwise severity is <c>-</c> and the line 0.
    /// </summary>
    public static ShowData FromBaseline(string root, string? target)
    {
        var data = new ShowData { Root = root, From = "baseline" };
        ShowData? saved = null;
        try { saved = FromLastRun(root, target); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException) { }
        var known = (saved?.Entries ?? []).GroupBy(e => e.Finding.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var syntax = Path.Combine(root, Baseline.FileName);
        if (File.Exists(syntax)) Add(data, Baseline.Read(syntax), "syntax", null, known);
        var semantic = SemanticBaselineFiles(root)
            .Select(p => (Path: p, File: SemanticBaseline.Read(p)))
            .ToList();
        var chosen = target is not null
            ? semantic.FirstOrDefault(s => string.Equals(s.File.TargetFramework, target, StringComparison.OrdinalIgnoreCase))
            : semantic.OrderByDescending(s => s.File.TargetFramework is { } t ? Semantic.TargetFrameworks.Key(t) : default).FirstOrDefault();
        if (chosen.File is not null) Add(data, chosen.File, "semantic", chosen.File.TargetFramework, known);
        data.WrittenAt = null;
        return data;
    }

    static IEnumerable<string> SemanticBaselineFiles(string root)
    {
        var plain = SemanticBaseline.PathFor(Path.Combine(root, Baseline.FileName), null);
        var stem = Path.GetFileName(plain)[..^".json".Length];
        return Directory.GetFiles(root, stem + "*.json")
            .Where(f => f == plain || Path.GetFileName(f).StartsWith(stem + ".", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
    }

    static void Add(ShowData data, BaselineFile file, string tier, string? tfm, Dictionary<string, ShowFinding> known)
    {
        foreach (var e in file.Findings)
        {
            var parts = e.Id.Split('|', 3);
            var k = known.GetValueOrDefault(e.Id);
            var f = new Finding
            {
                Id = e.Id, Handle = FindingEngine.HandleOf(e.Id), Rule = parts[0], Path = k?.Finding.Path ?? (parts.Length > 1 ? parts[1] : ""),
                Severity = k?.Finding.Severity ?? "-", Line = k?.Finding.Line ?? 0, Value = e.Value, Metric = k?.Finding.Metric,
                Threshold = k?.Finding.Threshold, Comparison = k?.Finding.Comparison, Member = k?.Finding.Member,
                Message = "recorded in the baseline" + (e.Value is double v ? " with value " + Condition.Num(v) : ""),
            };
            AgentFormat.Restore(f);
            data.Entries.Add(new ShowFinding { Finding = f, Tier = tier, TargetFramework = tfm, Source = "baseline", Metrics = k?.Metrics, Component = k?.Component });
        }
    }

    /// <summary>
    /// The stale warning: how many of the files shown changed since the data was written (per-file
    /// SHA-256); null when none did. Never re-runs and never refuses.
    /// </summary>
    public static ShowStale? Stale(ShowData data, IEnumerable<string> paths, string refresh)
    {
        if (data.From != "last-run") return null;
        var shown = paths.Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        int changed = 0;
        foreach (var p in shown)
        {
            var current = Paths.FileSha256(Path.Combine(data.Root, p));
            if (current is null || data.Hashes.GetValueOrDefault(p) != current) changed++;
        }
        if (changed == 0) return null;
        return new ShowStale
        {
            WrittenAt = data.WrittenAt ?? "", Commit = data.Header?.Commit, Dirty = data.Header?.Dirty,
            FilesShown = shown.Count, FilesChanged = changed, Refresh = refresh,
        };
    }

    /// <summary>The stale warning as one agent-format line.</summary>
    public static string StaleLine(ShowStale s, DateTimeOffset now)
    {
        var age = Paths.ParseUtc(s.WrittenAt) is { } t ? Age(now - t) + " ago" : "age unknown";
        var commit = s.Commit is { Length: >= 12 } c ? $", commit {c[..12]}{(s.Dirty == true ? " with uncommitted changes" : "")}" : "";
        return $"stale {s.FilesChanged} of {s.FilesShown} file{(s.FilesShown == 1 ? "" : "s")} shown changed since the data was saved ({s.WrittenAt}, {age}{commit}); refresh: {s.Refresh}";
    }

    /// <summary>An age in words: <c>under a minute</c>, <c>14 min</c>, <c>3 h</c>, <c>5 days</c>.</summary>
    public static string Age(TimeSpan d) =>
        d.TotalMinutes < 1 ? "under a minute"
        : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min"
        : d.TotalDays < 2 ? $"{(int)d.TotalHours} h"
        : $"{((int)d.TotalDays).ToString(CultureInfo.InvariantCulture)} days";
}
