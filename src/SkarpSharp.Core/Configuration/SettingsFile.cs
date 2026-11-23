using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkarpSharp.Configuration;

// The settings file .skarp.json: versioned, read with source-generated System.Text.Json
// (AOT safe). Unknown keys and wrong types are errors with the key path (JsonException.Path). Keys are camelCase and
// case-sensitive; comments and trailing commas are allowed.

/// <summary>Root of <c>.skarp.json</c>, version 1. Every key is optional except <c>version</c>.</summary>
public sealed class SettingsFile
{
    /// <summary>Format version; must be 1.</summary>
    public int? Version { get; set; }
    /// <summary>System and personal files only. False skips the file inside a repository (a
    /// repository-level <c>.skarp.json</c> or a git work tree); default true. An error in <c>.skarp.json</c>.</summary>
    public bool? UseInRepositories { get; set; }
    /// <summary><c>.skarp.json</c> only. True marks the repository root: the upward search stops
    /// here (it also stops at the nearest <c>.skarp.json</c> and the git work tree root). An error in machine files.</summary>
    public bool? Root { get; set; }
    /// <summary>Threshold preset id (<c>--preset</c>).</summary>
    public string? Preset { get; set; }
    /// <summary><c>cognitive</c> or <c>cyclomatic</c> (<c>--complex-metric</c>).</summary>
    public string? ComplexMetric { get; set; }
    /// <summary><c>physical</c> or <c>source</c> (<c>--long-method-lines</c>).</summary>
    public string? LongMethodLines { get; set; }
    /// <summary><c>exempt</c> (default) or <c>check</c>: positional record parameters and too-many-parameters.</summary>
    public string? RecordParameters { get; set; }
    /// <summary><c>count</c> (default: counted, not listed in the top list) or <c>list</c>.</summary>
    public string? MagicNumbers { get; set; }
    /// <summary>Rules switched on (true) or off (false) by rule id.</summary>
    public Dictionary<string, bool>? Rules { get; set; }
    /// <summary>
    /// Threshold overrides per rule: <c>{ "long-method": { "notice": 60, "warning": "off" }, "large-type": { "notice":
    /// { "sourceLines": 300 } }, "bumpy-road": { "depth": 3 } }</c>. Same grammar as <c>--threshold</c>.
    /// </summary>
    public Dictionary<string, Dictionary<string, JsonElement>>? Thresholds { get; set; }
    public bool? IncludeTests { get; set; }
    /// <summary>Globs that bring back files the test detection left out.</summary>
    public List<string>? Include { get; set; }
    /// <summary>How this file's <c>include</c> meets the inherited one, <c>replace</c> (default) or <c>append</c>.</summary>
    public string? IncludeMode { get; set; }
    /// <summary>Globs of files to leave out.</summary>
    public List<string>? Exclude { get; set; }
    /// <summary><c>replace</c> (default) or <c>append</c>.</summary>
    public string? ExcludeMode { get; set; }
    /// <summary>Report entries shown.</summary>
    public int? Top { get; set; }
    public HistorySettings? History { get; set; }
    /// <summary>Report layout.</summary>
    public ReportSettings? Report { get; set; }
    /// <summary>The opt-in gate.</summary>
    public GateSettings? Gate { get; set; }
    /// <summary>The semantic tier.</summary>
    public SemanticSettings? Semantic { get; set; }
    /// <summary>The unused-code rules.</summary>
    public UnusedCodeSettings? UnusedCode { get; set; }
    /// <summary>The namespace-cycle rule.</summary>
    public NamespaceCycleSettings? NamespaceCycles { get; set; }
    /// <summary>False stops <c>analyze</c> (and <c>check</c>) from saving results under
    /// <c>.skarp/</c> (<c>--no-save</c>); default true. Experimental.</summary>
    public bool? SaveLastRun { get; set; }
    /// <summary>Finding-line budgets of <c>skarp check</c> and the Claude hook. Experimental.</summary>
    public OutputSettings? Output { get; set; }
    /// <summary>The tracking files of <c>skarp check --track</c>. Experimental.</summary>
    public TrackingSettings? Tracking { get; set; }
    /// <summary>The code health score model, weights and worst-files list. Experimental.</summary>
    public ScoreFileSettings? Score { get; set; }
    /// <summary>Cobertura coverage reports (globs relative to the settings file's folder) that
    /// analyze and baseline read; <c>--coverage</c> replaces them. A glob that matches nothing is a warning.</summary>
    public List<string>? Coverage { get; set; }
    /// <summary><c>replace</c> (default) or <c>append</c>.</summary>
    public string? CoverageMode { get; set; }
    /// <summary>Preprocessor symbols to analyze (<c>["NET462", "NET10_0", "DEBUG"]</c>); per file the
    /// listed symbols it uses are combined (frameworks one at a time, other symbols on and off), replacing the automatic
    /// planning. A listed symbol is never filtered.</summary>
    public List<string>? Symbols { get; set; }
    /// <summary><c>replace</c> (default) or <c>append</c>.</summary>
    public string? SymbolsMode { get; set; }
    /// <summary>The variant caps.</summary>
    public VariantFileSettings? Variants { get; set; }
    /// <summary>Per-path settings, an ordered list of <c>{ files, excludeFiles, tests, rules, thresholds }</c>;
    /// repository level only. Read and checked by the level resolver (<see cref="RuleOverride"/>); kept here as written so
    /// the typed parse accepts the key.</summary>
    public List<JsonElement>? Overrides { get; set; }
}

/// <summary>Variant caps of <c>.skarp.json</c>.</summary>
public sealed class VariantFileSettings
{
    /// <summary>Variants per file besides the base parse for <c>analyze</c>, default 32; 0 analyzes the base view only.</summary>
    public int? Cap { get; set; }
    /// <summary>Variants per file for <c>skarp check</c>, the base parse counted in, default 4 (1: the base view only).</summary>
    public int? CheckCap { get; set; }
}

/// <summary>Code health score settings of <c>.skarp.json</c> (experimental).</summary>
public sealed class ScoreFileSettings
{
    /// <summary>The score model (<c>--score-model</c>); only <c>grade</c> exists.</summary>
    public string? Model { get; set; }
    /// <summary>The k-th finding of a rule in a file costs its weight x decay^(k-1); default 0.7, more than 0 and at most 1.</summary>
    public double? Decay { get; set; }
    /// <summary>Weight overrides by rule id: <c>{ "long-method": { "notice": 0.5, "warning": 1.0, "cap": 3.0 } }</c>.</summary>
    public Dictionary<string, ScoreWeightSettings>? Weights { get; set; }
    public ScoreWorstSettings? Worst { get; set; }
}

/// <summary>One rule's weights (points of the 9 available); unset values keep the built-in table's.</summary>
public sealed class ScoreWeightSettings
{
    public double? Notice { get; set; }
    public double? Warning { get; set; }
    public double? Cap { get; set; }
    /// <summary>This rule's decay (more than 0, at most 1), replacing <c>score.decay</c> for it.</summary>
    public double? Decay { get; set; }
}

/// <summary>The worst-files list: at most <c>count</c> files (default 10) scoring below <c>below</c> (default 7).</summary>
public sealed class ScoreWorstSettings
{
    public int? Count { get; set; }
    public double? Below { get; set; }
}

/// <summary>Text-output settings of <c>.skarp.json</c> (experimental): budgets of finding lines
/// (headings, verdict, About lines and the fold line are free).</summary>
public sealed class OutputSettings
{
    /// <summary>Finding lines of <c>skarp check</c>, default 5 (<c>check --max-lines</c>).</summary>
    public int? CheckMaxLines { get; set; }
    /// <summary>Finding lines of the <c>Stop</c> hook with <c>--gate</c>, default 50 (1 to 1,000).</summary>
    public int? GateMaxLines { get; set; }
}

/// <summary>Tracking settings of <c>.skarp.json</c> (experimental).</summary>
public sealed class TrackingSettings
{
    /// <summary>Tracking files older than this many days are deleted (default 30).</summary>
    public int? RetentionDays { get; set; }
    /// <summary>How many days back an earlier record of the same file, session and agent is looked for (default 14).</summary>
    public int? LookbackDays { get; set; }
}

/// <summary>Namespace-cycle settings of <c>.skarp.json</c> (experimental).</summary>
public sealed class NamespaceCycleSettings
{
    /// <summary>True keeps references to types declared only in generated code (<c>--include-generated-targets</c>);
    /// default false: they are left out and counted.</summary>
    public bool? IncludeGeneratedTargets { get; set; }
}

/// <summary>Unused-code settings of <c>.skarp.json</c>.</summary>
public sealed class UnusedCodeSettings
{
    /// <summary>Code that counts as used: documentation ids (<c>M:Ns.Type.Member</c>, <c>T:Ns.Type</c>) or names
    /// (<c>Ns.Type.Member</c>, <c>Ns.Type</c>), with <c>*</c> (any text) and <c>?</c> (one character).</summary>
    public List<string>? Allow { get; set; }
    /// <summary><c>replace</c> (default) or <c>append</c>.</summary>
    public string? AllowMode { get; set; }
}

/// <summary>Semantic tier settings of <c>.skarp.json</c>.</summary>
public sealed class SemanticSettings
{
    /// <summary>False turns the semantic tier off (<c>--no-semantic</c>); default true: it runs when the inputs allow.</summary>
    public bool? Enabled { get; set; }
    /// <summary>The solution to use (<c>--solution</c>), relative to the settings file's folder; default: the automatic choice.</summary>
    public string? Solution { get; set; }
    /// <summary>False skips source generators (<c>--no-generators</c>); default true.</summary>
    public bool? Generators { get; set; }
    /// <summary>Target frameworks: <c>["highest"]</c> (default), <c>["all"]</c> (<c>--all-targets</c>) or monikers (<c>--target</c>).</summary>
    public List<string>? Targets { get; set; }
    /// <summary><c>replace</c> (default) or <c>append</c>.</summary>
    public string? TargetsMode { get; set; }
}

/// <summary>Report settings of <c>.skarp.json</c>.</summary>
public sealed class ReportSettings
{
    /// <summary>Places shown per file in the default view (<c>--per-file</c>; default 3; 0 = no cap).</summary>
    public int? PerFile { get; set; }
}

/// <summary>Gate settings of <c>.skarp.json</c>.</summary>
public sealed class GateSettings
{
    /// <summary>True turns the gate on for <c>skarp analyze</c> (<c>--gate</c>, <c>--no-gate</c>).</summary>
    public bool? Enabled { get; set; }
    /// <summary>Rule and severity pairs that trip the gate (<c>"*:warning"</c>, <c>"long-method:notice"</c>); default <c>*:warning</c>.</summary>
    public List<string>? On { get; set; }
    /// <summary><c>replace</c> (default) or <c>append</c>.</summary>
    public string? OnMode { get; set; }
    /// <summary>The health limits of the gate.</summary>
    public GateHealthSettings? Health { get; set; }
}

/// <summary>The gate's health limits per file; 0 turns a rule off.</summary>
public sealed class GateHealthSettings
{
    /// <summary>A known file may not lose this much or more (default 1.0; 0 to 9).</summary>
    public double? MaxDrop { get; set; }
    /// <summary>A new file must score at least this (default 6.5; 0 to 10).</summary>
    public double? NewFileMin { get; set; }
    /// <summary>A file whose score fell may not end below this (default 6.5; 0 to 10).</summary>
    public double? Floor { get; set; }
}

/// <summary>History settings of <c>.skarp.json</c>.</summary>
public sealed class HistorySettings
{
    /// <summary>False turns history off for analyze and hotspots (<c>--no-history</c>).</summary>
    public bool? Enabled { get; set; }
    /// <summary>Window length in months ending at HEAD's committer date.</summary>
    public int? Window { get; set; }
    /// <summary>Window start date (committer date); overrides <see cref="Window"/>.</summary>
    public string? Since { get; set; }
    /// <summary>Whole history.</summary>
    public bool? AllHistory { get; set; }
    public bool? IgnoreWhitespace { get; set; }
    public int? BulkFlag { get; set; }
    public int? BulkDrop { get; set; }
    public bool? IncludeBots { get; set; }
    /// <summary>Extra bot patterns (regular expressions), added to the defaults.</summary>
    public List<string>? BotPatterns { get; set; }
    /// <summary><c>replace</c> (default) or <c>append</c>.</summary>
    public string? BotPatternsMode { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true, NumberHandling = JsonNumberHandling.Strict)]
[JsonSerializable(typeof(SettingsFile))]
public sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>A settings file that cannot be used: the file, the key path and what is wrong.</summary>
public sealed class SettingsException(string message) : Exception(message);

public static class SettingsFileReader
{
    public const string FileName = ".skarp.json";
    public const int CurrentVersion = 1;

    /// <summary>
    /// The settings file for <paramref name="directory"/>: <c>.skarp.json</c> in the directory or the nearest parent,
    /// stopping at the root of the git work tree (the folder holding <c>.git</c>). Null when there is none.
    /// </summary>
    public static string? Find(string directory) => FindRepository(directory).File;

    /// <summary>
    /// The repository-level settings file of <paramref name="directory"/> (as <see cref="Find"/>) and, when the
    /// walk stopped at a git work tree root without finding one, that root. The walk stops at the nearest
    /// <c>.skarp.json</c>, so a file with <c>"root": true</c> always ends it; without a folder level, the
    /// nearest file is the repository level whether or not it says so.
    /// </summary>
    public static (string? File, string? GitRoot) FindRepository(string directory)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(directory));
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, FileName);
            if (File.Exists(candidate)) return (candidate, null);
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git"))) return (null, dir.FullName);
            dir = dir.Parent;
        }
        return (null, null);
    }

    public static SettingsFile Read(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SettingsException($"{path}: cannot read: {ex.Message}");
        }
        return Parse(text, path);
    }

    public static SettingsFile Parse(string json, string path = FileName)
    {
        SettingsFile? file;
        try { file = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.SettingsFile); }
        catch (JsonException ex)
        {
            throw new SettingsException($"{path}: {KeyPath(ex.Path)}: {Explain(ex)}");
        }
        if (file is null) throw new SettingsException($"{path}: expected a JSON object");
        if (file.Version is null) throw new SettingsException($"{path}: version: required (\"version\": {CurrentVersion})");
        if (file.Version != CurrentVersion) throw new SettingsException($"{path}: version: {file.Version} is not supported (this skarp reads version {CurrentVersion})");
        return file;
    }

    /// <summary><c>$.history.window</c> as <c>history.window</c>; the root as <c>(root)</c>.</summary>
    public static string KeyPath(string? jsonPath)
    {
        if (string.IsNullOrEmpty(jsonPath) || jsonPath == "$") return "(root)";
        var p = jsonPath.StartsWith("$.", StringComparison.Ordinal) ? jsonPath[2..] : jsonPath.TrimStart('$');
        // Dictionary keys with dashes come as $['long-method']: show them dotted.
        return p.Replace("['", ".", StringComparison.Ordinal).Replace("']", "", StringComparison.Ordinal).TrimStart('.');
    }

    static string Explain(JsonException ex)
    {
        var m = ex.Message;
        if (m.Contains("could not be mapped", StringComparison.Ordinal)) return "unknown key";
        if (m.Contains("could not be converted", StringComparison.Ordinal)) return "wrong type" + TypeHint(m);
        // Syntax errors: keep the reader's message without the path and position suffix (the path is shown already).
        int cut = m.IndexOf(" Path:", StringComparison.Ordinal);
        return cut > 0 ? m[..cut] : m;
    }

    static string TypeHint(string message) =>
        message.Contains("List", StringComparison.Ordinal) ? " (expected a list of strings)"
        : message.Contains("Dictionary", StringComparison.Ordinal) || message.Contains("Settings", StringComparison.Ordinal) ? " (expected an object)"
        : message.Contains("System.Int32", StringComparison.Ordinal) ? " (expected a whole number)"
        : message.Contains("System.Boolean", StringComparison.Ordinal) ? " (expected true or false)"
        : message.Contains("System.Double", StringComparison.Ordinal) ? " (expected a number)"
        : message.Contains("System.String", StringComparison.Ordinal) ? " (expected a string)"
        : "";

    /// <summary>
    /// Rebases a glob written relative to the settings file's folder onto the analyzed directory, which lies
    /// <paramref name="prefix"/> below it (<c>src/lib</c>; empty when it is the same folder). A pattern without a slash or
    /// starting with <c>**/</c> matches at any depth and is kept; <c>src/lib/gen/**</c> becomes <c>gen/**</c>; a pattern
    /// that covers the whole analyzed directory (<c>src/**</c>, <c>src</c>) becomes <c>**</c>; a pattern elsewhere
    /// (<c>tools/**</c>) cannot match and is dropped (null).
    /// </summary>
    public static string? RebaseGlob(string glob, string prefix)
    {
        var p = glob.Replace('\\', '/').Trim();
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        bool anchored = p.StartsWith('/');
        p = p.Trim('/');
        if (prefix.Length == 0) return glob;
        if (!anchored && !p.Contains('/')) return glob;
        var g = p.Split('/');
        var d = prefix.Split('/');
        int i = 0;
        for (; i < d.Length; i++)
        {
            if (i >= g.Length) return "**";              // the pattern names a folder above or at the analyzed directory
            if (g[i] == "**") return string.Join('/', g[i..]);
            if (!SegmentMatches(g[i], d[i])) return null;
        }
        return i >= g.Length ? "**" : string.Join('/', g[i..]);
    }

    static bool SegmentMatches(string pattern, string segment)
    {
        var rx = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", "[^/]*", StringComparison.Ordinal).Replace("\\?", "[^/]", StringComparison.Ordinal) + "$";
        var options = System.Text.RegularExpressions.RegexOptions.CultureInvariant
            | (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? System.Text.RegularExpressions.RegexOptions.IgnoreCase : 0);
        return System.Text.RegularExpressions.Regex.IsMatch(segment, rx, options, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// The threshold overrides of the file in <c>--threshold</c> grammar, each with its key path for errors and origins.
    /// </summary>
    public static List<(string Override, string Key)> ThresholdOverrides(SettingsFile file, string path)
    {
        var list = new List<(string, string)>();
        if (file.Thresholds is null) return list;
        foreach (var (rule, entries) in file.Thresholds)
        {
            if (entries is null) throw new SettingsException($"{path}: thresholds.{rule}: wrong type (expected an object)");
            foreach (var (name, value) in entries)
            {
                var key = $"thresholds.{rule}.{name}";
                switch (value.ValueKind)
                {
                    case JsonValueKind.Number:
                        list.Add(($"{rule}.{name}={value.GetRawText()}", key));
                        break;
                    case JsonValueKind.String:
                        list.Add(($"{rule}.{name}={value.GetString()}", key));
                        break;
                    case JsonValueKind.Object when name is "notice" or "warning":
                        foreach (var metric in value.EnumerateObject())
                        {
                            if (metric.Value.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
                                throw new SettingsException($"{path}: {key}.{metric.Name}: wrong type (expected a number or \"off\")");
                            var v = metric.Value.ValueKind == JsonValueKind.Number ? metric.Value.GetRawText() : metric.Value.GetString();
                            list.Add(($"{rule}.{name}.{metric.Name}={v}", $"{key}.{metric.Name}"));
                        }
                        break;
                    default:
                        throw new SettingsException($"{path}: {key}: wrong type (expected a number, \"off\"" +
                            (name is "notice" or "warning" ? " or an object of metric values)" : " or a string)"));
                }
            }
        }
        return list;
    }
}
