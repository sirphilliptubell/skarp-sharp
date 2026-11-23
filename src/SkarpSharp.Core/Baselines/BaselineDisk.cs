using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkarpSharp.Model;

namespace SkarpSharp.Baselines;

/// <summary>
/// The shape of a baseline file on disk (both <c>.skarp-baseline.json</c> and <c>.skarp-baseline.semantic.json</c>):
/// a header, then the findings grouped under the path of their file. <see cref="Baseline.Parse"/> maps it to the
/// in-memory <see cref="BaselineFile"/> (finding id <c>rule|path|place</c>), so nothing else works with this shape.
/// </summary>
public sealed class BaselineDisk
{
    public int Version { get; set; }
    /// <summary>What kind of file this is: <see cref="Baseline.TypeName"/> or <see cref="SemanticBaseline.TypeName"/>.</summary>
    public string? Type { get; set; }
    public string? ToolVersion { get; set; }
    public string? Preset { get; set; }
    public string? ComplexMetric { get; set; }
    public string? LongMethodLines { get; set; }
    public string? SettingsFingerprint { get; set; }
    public string? Commit { get; set; }
    public int? CsharpCommits { get; set; }
    public string? Solution { get; set; }
    public string? Targets { get; set; }
    public string? TargetFramework { get; set; }
    /// <summary>The findings under the path of their file (relative, '/' separators). A syntax baseline lists every
    /// analyzed file, an empty array meaning "analyzed, no findings"; a semantic baseline lists files with findings.</summary>
    public Dictionary<string, List<DiskFinding>>? Files { get; set; }
    /// <summary>Findings that belong to no single file (clone groups).</summary>
    public List<DiskFinding>? Repository { get; set; }
}

/// <summary>One recorded finding inside its file group: the rule, the place within the file (a type or a member key),
/// the measured value, and for clone groups the severity and the files of the instances.</summary>
public sealed class DiskFinding
{
    public string Rule { get; set; } = "";
    public string? Place { get; set; }
    public double? Value { get; set; }
    public string? Severity { get; set; }
    public List<string>? Files { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(BaselineDisk))]
public sealed partial class BaselineJsonContext : JsonSerializerContext;

/// <summary>Writing and reading the grouped layout, shared by the syntax and the semantic baseline.</summary>
internal static class BaselineLayout
{
    public static string Str(string s) => "\"" + JsonEncodedText.Encode(s, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString() + "\"";

    static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Appends <c>"files"</c> (every path in <paramref name="filePaths"/> and every path with a finding, sorted, each
    /// with its findings sorted by rule then place, one finding per line, so merges conflict only where the same
    /// findings changed) and, when there are findings without a file, <c>"repository"</c>. Ends the object.
    /// </summary>
    public static void AppendBody(StringBuilder sb, IEnumerable<Finding> findings, IEnumerable<string>? filePaths)
    {
        var byPath = new SortedDictionary<string, List<(string Rule, string Place, Finding F)>>(StringComparer.Ordinal);
        foreach (var p in filePaths ?? []) if (p.Length > 0) byPath.TryAdd(p, []);
        var repository = new List<(string Rule, string Place, Finding F)>();
        foreach (var f in findings)
        {
            // The id (rule|path|place) is the identity; a '|' inside a path moves into the place and joins back on reading.
            var parts = f.Id.Split('|', 3);
            var (rule, path, place) = (parts[0], parts.Length > 1 ? parts[1] : "", parts.Length > 2 ? parts[2] : "");
            if (path.Length == 0) { repository.Add((rule, place, f)); continue; }
            if (!byPath.TryGetValue(path, out var list)) byPath[path] = list = [];
            list.Add((rule, place, f));
        }
        sb.Append("  \"files\": {");
        bool first = true;
        foreach (var (path, list) in byPath)
        {
            sb.Append(first ? "\n" : ",\n");
            first = false;
            sb.Append("    ").Append(Str(path)).Append(':');
            AppendList(sb, list, "    ");
        }
        sb.Append(first ? "}" : "\n  }");
        if (repository.Count > 0)
        {
            sb.Append(",\n  \"repository\":");
            AppendList(sb, repository, "  ");
        }
        sb.Append("\n}\n");
    }

    static void AppendList(StringBuilder sb, List<(string Rule, string Place, Finding F)> list, string indent)
    {
        if (list.Count == 0) { sb.Append(" []"); return; }
        sb.Append(" [");
        var sorted = list.OrderBy(e => e.Rule, StringComparer.Ordinal).ThenBy(e => e.Place, StringComparer.Ordinal).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            var (rule, place, f) = sorted[i];
            sb.Append(i == 0 ? "\n" : ",\n").Append(indent).Append("  { \"rule\": ").Append(Str(rule));
            if (place.Length > 0) sb.Append(", \"place\": ").Append(Str(place));
            if (f.Value is double v) sb.Append(", \"value\": ").Append(Num(v));
            if (f.Clone is { } c)
            {
                sb.Append(", \"severity\": ").Append(Str(f.Severity)).Append(", \"files\": [");
                sb.AppendJoin(", ", c.Instances.Select(x => x.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(Str)).Append(']');
            }
            sb.Append(" }");
        }
        sb.Append('\n').Append(indent).Append(']');
    }

    /// <summary>Deserializes and checks a baseline file of the <paramref name="expectedType"/>, then maps it to the in-memory form.</summary>
    public static BaselineFile Parse(string json, string path, string expectedType)
    {
        BaselineDisk? d;
        try { d = JsonSerializer.Deserialize(json, BaselineJsonContext.Default.BaselineDisk); }
        catch (JsonException ex) { throw new InvalidDataException($"baseline {path}: not a baseline file ({ex.Message})"); }
        if (d is null) throw new InvalidDataException($"baseline {path}: not a baseline file");
        if (d.Type is null)
            throw new InvalidDataException($"baseline {path}: no \"type\" property, so it is not a Skarp baseline file; write it again with skarp baseline");
        if (d.Type != expectedType)
            throw new InvalidDataException($"baseline {path}: this is a {d.Type} file, not a {expectedType} file" +
                (expectedType == Baseline.TypeName ? " (the semantic baseline is read next to the baseline automatically)" : ""));
        if (d.Version != Baseline.CurrentVersion)
            throw new InvalidDataException($"baseline {path}: version {d.Version} is not supported (this skarp reads version {Baseline.CurrentVersion}); write it again with skarp baseline");
        var file = new BaselineFile
        {
            Version = d.Version, Type = d.Type, ToolVersion = d.ToolVersion ?? "", Preset = d.Preset ?? "", ComplexMetric = d.ComplexMetric ?? "",
            LongMethodLines = d.LongMethodLines ?? "", SettingsFingerprint = d.SettingsFingerprint ?? "", Commit = d.Commit,
            CsharpCommits = d.CsharpCommits, Solution = d.Solution, Targets = d.Targets, TargetFramework = d.TargetFramework,
        };
        if (expectedType == Baseline.TypeName) file.Files = [.. (d.Files ?? []).Keys.Order(StringComparer.Ordinal)];
        foreach (var (filePath, list) in d.Files ?? [])
            foreach (var e in list) file.Findings.Add(ToEntry(e, filePath));
        foreach (var e in d.Repository ?? []) file.Findings.Add(ToEntry(e, ""));
        return file;
    }

    static BaselineEntry ToEntry(DiskFinding e, string filePath) =>
        new() { Id = $"{e.Rule}|{filePath}|{e.Place}", Value = e.Value, Severity = e.Severity, Files = e.Files };
}
