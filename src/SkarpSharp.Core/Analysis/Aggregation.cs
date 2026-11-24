using SkarpSharp.Model;

namespace SkarpSharp.Analysis;

/// <summary>
/// Repository and type aggregates (count, sum, max, mean, p90, count over the warn threshold of each entry's
/// Recommendation). Only top-level members count; nested bodies are folded into them already.
/// Generated files are excluded unless requested.
/// </summary>
public static class Aggregation
{
    /// <summary>Warn thresholds of the summary: cyclomatic 10, cognitive 15, NPath 200, nesting 3, statements 30 (sources in CREDITS.md).</summary>
    public static readonly (string Metric, long Warn, Func<MemberResult, long> Value)[] Metrics =
    [
        ("cyclomatic", 10, m => m.Cyclomatic),
        ("cognitive", 15, m => m.Cognitive),
        ("npath", 200, m => m.Npath),
        ("nesting", 3, m => m.Nesting),
        ("statements", 30, m => m.Statements),
        ("sourceLines", 0, m => m.Lines.Source),
    ];

    public static void Fill(AnalysisResult result, bool includeGenerated)
    {
        var repo = result.Repository;
        var members = new List<MemberResult>();
        var types = new Dictionary<string, TypeSummary>(StringComparer.Ordinal);
        foreach (var f in result.Files)
        {
            if (f.Error is not null) { repo.FilesWithErrors++; continue; }
            if (f.Generated) repo.GeneratedFiles++;
            if (f.Generated && !includeGenerated) continue;
            repo.Files++;
            repo.Lines.Add(f.Lines);
            foreach (var t in f.Types) AddDeclaration(types, f, t);
            foreach (var m in f.Members)
            {
                if (m.Parent is not null) { repo.NestedBodies++; continue; }
                members.Add(m);
                if (f.TypeSums is null && types.TryGetValue(m.Type, out var s)) AddSums(s, new TypeMemberSums().Add(m.Cyclomatic, m.Cognitive, m.Statements));
            }
            // A file with variants adds, per type, the largest member sums of a single variant (twins under
            // #if/#else are never summed).
            if (f.TypeSums is { } sums)
                foreach (var (name, v) in sums)
                    if (types.TryGetValue(name, out var s)) AddSums(s, v);
        }
        if (result.Files.Any(f => f.TypeSums is not null)) BaseViews(result.Files.Where(f => f.Error is null && (includeGenerated || !f.Generated)), types);
        repo.Members = members.Count;
        foreach (var (metric, warn, value) in Metrics)
        {
            var values = members.Select(value).OrderBy(v => v).ToArray();
            var summary = new MetricSummary { Metric = metric, WarnThreshold = warn };
            if (values.Length > 0)
            {
                long sum = 0;
                foreach (var v in values) sum = SkarpSharp.Metrics.Sat.Add(sum, v);
                summary.Sum = sum;
                summary.Max = values[^1];
                summary.Mean = Math.Round(values.Select(v => (double)v).Average(), 3);
                summary.P90 = values[(int)Math.Ceiling(0.9 * values.Length) - 1];
                summary.OverWarn = warn > 0 ? values.Count(v => v > warn) : 0;
            }
            repo.Metrics.Add(summary);
        }
        result.Types = [.. types.Values.OrderBy(t => t.Name, StringComparer.Ordinal)];
    }

    static void AddDeclaration(Dictionary<string, TypeSummary> types, FileResult f, TypeCounts t)
    {
        // Files come in path order and declarations in source order, so the first one seen is the first declaration.
        // The place is the first declaration the base view sees (identity); a declaration only a
        // variant shows is the place only while no base declaration exists.
        if (!types.TryGetValue(t.Name, out var s)) types[t.Name] = s = new TypeSummary { Name = t.Name, Kind = t.Kind, Path = f.Path, Line = t.StartLine, PlaceFromVariant = t.OnlyInVariants == true };
        else if (s.PlaceFromVariant && t.OnlyInVariants != true) { s.Kind = t.Kind; s.Path = f.Path; s.Line = t.StartLine; s.PlaceFromVariant = false; }
        s.Declarations++; s.SourceLines += t.SourceLines; s.Fields += t.Fields; s.Properties += t.Properties; s.Methods += t.Methods;
        s.Constructors += t.Constructors; s.Events += t.Events; s.NestedTypes += t.NestedTypes; s.PublicMembers += t.PublicMembers;
        s.Static |= t.Static;
    }

    static void AddSums(TypeSummary s, TypeMemberSums v)
    {
        s.SumCyclomatic += v.SumCyclomatic; s.MaxCyclomatic = Math.Max(s.MaxCyclomatic, v.MaxCyclomatic);
        s.SumCognitive += v.SumCognitive; s.Statements += v.Statements;
    }

    /// <summary>
    /// The same roll-up over the base view (base counts and member sums, variant-only declarations and members
    /// left out) for every type a file with variants declares, so a finding seen only in a variant can be told apart
    ///. A type the base view never declares is marked <see cref="TypeSummary.OnlyInVariants"/>.
    /// </summary>
    static void BaseViews(IEnumerable<FileResult> files, Dictionary<string, TypeSummary> types)
    {
        var list = files.ToList();
        var touched = new HashSet<string>(list.Where(f => f.TypeSums is not null).SelectMany(f => f.Types.Select(t => t.Name)), StringComparer.Ordinal);
        var bases = new Dictionary<string, TypeSummary>(StringComparer.Ordinal);
        foreach (var f in list)
        {
            foreach (var t in f.Types)
            {
                if (!touched.Contains(t.Name) || t.OnlyInVariants == true) continue;
                var view = new TypeCounts { Name = t.Name, Kind = t.Kind, StartLine = t.StartLine, Static = t.Static };
                (t.BaseValues ?? TypeValues.Of(t)).ApplyTo(view);
                AddDeclaration(bases, f, view);
            }
            if (f.BaseTypeSums is { } sums)
            {
                foreach (var (name, v) in sums)
                    if (bases.TryGetValue(name, out var s)) AddSums(s, v);
            }
            else
            {
                foreach (var m in f.Members)
                    if (m.Parent is null && touched.Contains(m.Type) && bases.TryGetValue(m.Type, out var s)) AddSums(s, new TypeMemberSums().Add(m.Cyclomatic, m.Cognitive, m.Statements));
            }
        }
        foreach (var name in touched)
        {
            if (!types.TryGetValue(name, out var merged)) continue;
            if (bases.TryGetValue(name, out var b)) merged.BaseView = b;
            else merged.OnlyInVariants = true;
            merged.Variant = list.Select(f => f.TypeSumsVariant?.GetValueOrDefault(name) ?? f.Types.FirstOrDefault(t => t.Name == name && t.Variant is not null)?.Variant)
                .FirstOrDefault(v => v is not null);
        }
    }
}
