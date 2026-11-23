using System.Globalization;
using SkarpSharp.Findings;
using SkarpSharp.Model;
using SkarpSharp.Reporting;

namespace SkarpSharp.Agent;

/// <summary>
/// Choosing and printing the findings of a saved run without a console, so
/// <c>skarp show</c> and the MCP tool <c>findings</c> share them: one entry per id, a handle prefix, the list order, a
/// <c>--file</c> path relative to the root, the file health breakdowns, and one finding in full to any
/// <see cref="TextWriter"/>.
/// </summary>
public static class ShowFindings
{
    /// <summary>One entry per id (a finding repeated by the data sources once, the first wins).</summary>
    public static List<ShowFinding> Unique(ShowData data)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. data.Entries.Where(e => seen.Add(e.Finding.Id))];
    }

    /// <summary>The entries whose handle starts with <paramref name="prefix"/> (lowercase hexadecimal).</summary>
    public static List<ShowFinding> ByHandle(IEnumerable<ShowFinding> entries, string prefix) =>
        [.. entries.Where(e => (e.Finding.Handle ?? "").StartsWith(prefix, StringComparison.Ordinal))];

    /// <summary>The list order: warnings first, then by path and line (ties keep their order).</summary>
    public static List<ShowFinding> Order(IEnumerable<ShowFinding> list) =>
        [.. list.Select((e, i) => (e, i)).OrderBy(x => FindingEngine.SeverityRank(x.e.Finding.Severity))
            .ThenBy(x => x.e.Finding.Path, StringComparer.Ordinal).ThenBy(x => x.e.Finding.Line).ThenBy(x => x.i).Select(x => x.e)];

    /// <summary>A file argument as a path relative to the root: absolute, relative to the working folder, or already relative to the root.</summary>
    public static string RootRelative(string root, string cwd, string file)
    {
        var full = Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(cwd, file));
        var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
        if (!rel.StartsWith("../", StringComparison.Ordinal) && rel != ".." && !Path.IsPathRooted(rel) && (File.Exists(full) || Path.IsPathRooted(file)))
            return rel;
        var plain = file.Replace('\\', '/');
        while (plain.StartsWith("./", StringComparison.Ordinal)) plain = plain[2..];
        return plain;
    }

    /// <summary>Each file's score and where its points went, from the saved run (the root's score settings).</summary>
    public static List<ShowHealth> Health(ShowData data, IReadOnlyList<string> files, Configuration.SettingsLocations? locations = null)
    {
        var parsed = Configuration.CommandLine.Parse(["analyze", data.Root, "--no-history", "--no-semantic"], locations);
        var settings = parsed.Options?.Score ?? SkarpSharp.Health.ScoreSettings.Default;
        return HealthLines.ForShow(data, files, settings);
    }

    /// <summary>
    /// One finding in full: its file, the stale line when given, its line, id, place, tier and source, the measure and
    /// limit, the baseline state, the last check's word, the members using it, the member's metrics, a namespace cycle
    /// with its cuts, and a composite's parts.
    /// </summary>
    public static void Detail(TextWriter w, ShowData data, ShowFinding e, string? staleLine)
    {
        var f = e.Finding;
        w.WriteLine("Skarp: " + (f.Path.Length > 0 ? f.Path : "(no file)"));
        if (staleLine is not null) w.WriteLine(staleLine);
        w.WriteLine(ShowText.Line(e, f.BaselineState is "new" or "worsened"));
        w.WriteLine($"  id: {f.Id}");
        w.WriteLine($"  place: {AgentFormat.Place(f)}{(f.Member is { } m && m != AgentFormat.Place(f) ? $" ({m})" : "")}");
        w.WriteLine($"  tier: {e.Tier}{(e.TargetFramework is { } t ? $" ({t})" : "")}; data: {e.Source}");
        if (f.Severity != "-")
        {
            w.WriteLine($"  measured: {ReportLayout.Measure(f)}");
            if (f.Preset.Length > 0) w.WriteLine($"  threshold: {f.Severity} level of preset {f.Preset} ({f.ThresholdSource})");
        }
        else w.WriteLine($"  {f.Message}");
        w.WriteLine("  baseline: " + BaselineState(f, data.From));
        Context(w, e);
    }

    /// <summary>What a finding in full adds after its baseline state: the last check's word, the members using it, the
    /// member's metrics, a namespace cycle with its cuts, and a composite's parts.</summary>
    static void Context(TextWriter w, ShowFinding e)
    {
        var f = e.Finding;
        if (e.Change is { } ch) w.WriteLine($"  last check: {ch}");
        if (f.UsedOnlyBy is { Count: > 0 } users) w.WriteLine($"  used only by: {string.Join(", ", users)}");
        if (e.Metrics is { } mr) w.WriteLine("  member metrics: " + Metrics(mr));
        if (e.Component is { } c) Component(w, c);
        // A composite lists its parts, each as its own line (a part's id gives its detail).
        if (f.Parts is { } parts)
        {
            w.WriteLine($"  parts ({parts.Count}):");
            foreach (var p in parts)
            {
                AgentFormat.Restore(p);
                w.WriteLine(TextFormat.Indent + ShowText.Line(new ShowFinding { Finding = p, Tier = "syntax", Source = e.Source }, false, p.Path != f.Path ? p.Path : null));
            }
        }
    }

    static string BaselineState(Finding f, string from) => f.BaselineState switch
    {
        "worsened" when f.Parts is not null => "worsened (a part is new or worse)",
        "worsened" => $"worsened (recorded {(f.BaselineValue is double v ? Condition.Num(v) : "?")})",
        "known" => "known" + (f.BaselineValue is double v2 ? $" (recorded {Condition.Num(v2)})" : ""),
        "new" => "new" + (f.PossiblyRenamedFrom is { } r ? $" (possibly renamed from {r})" : ""),
        _ => from == "baseline" ? "recorded" : "not compared",
    };

    static string Metrics(MemberResult m)
    {
        static string N(long v) => v.ToString(CultureInfo.InvariantCulture);
        var parts = new List<string>
        {
            $"lines {N(m.StartLine)}-{N(m.EndLine)}", $"cyclomatic {N(m.Cyclomatic)}", $"cognitive {N(m.Cognitive)}",
            $"npath {N(m.Npath)}{(m.NpathSaturated ? " (saturated)" : "")}", $"nesting {N(m.Nesting)}", $"statements {N(m.Statements)}",
            $"physical lines {N(m.Lines.Physical)}", $"source lines {N(m.Lines.Source)}",
        };
        if (m.Parameters is int p) parts.Add($"parameters {N(p)}");
        if (m.BooleanOperatorsMax is int b) parts.Add($"boolean operators {N(b)}");
        return string.Join(", ", parts);
    }

    static string Pairs(int n) => n == 1 ? "1 type pair" : $"{n} type pairs";

    /// <summary>A namespace cycle in full: every member, every suggested cut with every example reference (no caps).</summary>
    static void Component(TextWriter w, NamespaceComponent c)
    {
        w.WriteLine($"  namespaces ({c.Size}): {string.Join(", ", c.Members)}");
        if (c.ShortestCycle.Count > 0) w.WriteLine($"  shortest cycle: {string.Join(" -> ", c.ShortestCycle)}");
        foreach (var p in c.MutualPairs) w.WriteLine($"  mutual: {p.From} <-> {p.To} ({p.Weight} and {Pairs(p.OtherWeight)})");
        w.WriteLine($"  suggested cut: {c.Cut.Count} dependenc{(c.Cut.Count == 1 ? "y" : "ies")}, {Pairs(c.CutTypePairs)}{(c.CutComplete ? "" : " (search stopped early: not proven cheapest)")}");
        var deps = c.Dependencies.ToDictionary(d => (d.From, d.To));
        int i = 0;
        foreach (var edge in c.Cut)
        {
            w.WriteLine($"  cut {++i}: {edge.From} -> {edge.To} ({Pairs(edge.Weight)})");
            if (deps.TryGetValue((edge.From, edge.To), out var d))
                foreach (var x in d.Examples) w.WriteLine($"    {x.Path}:{x.Line} {x.FromType} -> {x.ToType}");
        }
    }
}
