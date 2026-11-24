using SkarpSharp.Findings;

namespace SkarpSharp.Configuration;

/// <summary>How a key's values from several settings levels combine.</summary>
public enum MergeKind
{
    /// <summary>The higher level's value replaces the lower one's.</summary>
    Scalar,
    /// <summary>Merged key by key, recursively (<c>rules</c>, <c>thresholds</c>, <c>score.weights</c>, every section).</summary>
    Object,
    /// <summary>Follows the sibling <c>&lt;name&gt;Mode</c>: <c>replace</c> (default) or <c>append</c>.</summary>
    List,
    /// <summary><c>overrides</c>, an ordered list held by the repository level only; never merged across
    /// levels and without a generated mode key.</summary>
    Overrides,
}

/// <summary>The settings levels a key may appear in.</summary>
[Flags]
public enum LevelScope
{
    /// <summary>The system and personal files.</summary>
    Machine = 1,
    /// <summary>The repository's <c>.skarp.json</c> (also <c>--config</c> and <c>SKARP_CONFIG</c>).</summary>
    Repository = 2,
    Every = Machine | Repository,
}

/// <summary>The commands that use a settings key. <c>--setting</c> is accepted on a
/// command exactly when the key's row lists it; <see cref="Check"/> marks keys <c>skarp check</c> reads from the files.</summary>
[Flags]
public enum SettingCommands
{
    None = 0,
    Analyze = 1,
    Baseline = 2,
    Hotspots = 4,
    History = 8,
    Config = 16,
    Check = 32,
}

/// <summary>The menu section a key is edited in, in menu order.</summary>
public enum SettingSection { Rules, Thresholds, History, Score, Semantic, Agent, Lists, General }

/// <summary>How a key's value is edited and parsed (<c>skarp setup --set</c>).</summary>
public enum SettingKind { Boolean, Integer, Number, Choice, Text, List, Path, Object }

/// <summary>One settings key.</summary>
/// <param name="Key">Dotted key path (<c>history.window</c>).</param>
/// <param name="Type">The JSON type in words.</param>
/// <param name="Default">skarp's built-in default, as <c>skarp config</c> prints it.</param>
/// <param name="AffectsFindings">The key changes the finding set or its values (the inputs of the settings fingerprint).</param>
/// <param name="IsPath">The value is a path relative to the file (allowed only in <c>.skarp.json</c>).</param>
public sealed record SettingsKey(string Key, string Type, string Default, MergeKind Merge, bool AffectsFindings = false,
    bool IsPath = false, LevelScope Levels = LevelScope.Every)
{
    /// <summary>The generated <c>&lt;name&gt;Mode</c> key of a list, or null.</summary>
    public string? ModeKey => Merge == MergeKind.List ? Key + "Mode" : null;

    /// <summary>The <c>skarp setup</c> section.</summary>
    public SettingSection Section { get; init; } = SettingSection.General;
    /// <summary>How the value is edited and parsed.</summary>
    public SettingKind Kind { get; init; }
    /// <summary>The allowed values of a <see cref="SettingKind.Choice"/>.</summary>
    public IReadOnlyList<string>? Allowed { get; init; }
    /// <summary>The smallest allowed number (exclusive when <see cref="MinExclusive"/>).</summary>
    public double? Min { get; init; }
    public bool MinExclusive { get; init; }
    /// <summary>The largest allowed number.</summary>
    public double? Max { get; init; }
    /// <summary>One line in plain words, shown in the editor's explanation pane.</summary>
    public string Description { get; init; } = "";
    /// <summary>A generated row (<c>rules.&lt;id&gt;</c>, <c>thresholds.*</c>); null for a declared one.</summary>
    public string? Rule { get; init; }
    /// <summary>A threshold level row's level (<c>notice</c> or <c>warning</c>), or a parameter name.</summary>
    public string? Part { get; init; }

    /// <summary>The commands that use this key (<see cref="SettingsRegistry.CommandsOf"/>).</summary>
    public SettingCommands Commands => SettingsRegistry.CommandsOf(Key);

    /// <summary>The range in words (<c>1 to 1200</c>, <c>more than 0, at most 1</c>), or null.</summary>
    public string? RangeText => Min is null && Max is null ? null
        : MinExclusive ? $"more than {Number(Min ?? 0)}" + (Max is { } mx ? $", at most {Number(mx)}" : "")
        : Max is null ? $"{Number(Min ?? 0)} or more"
        : $"{Number(Min ?? 0)} to {Number(Max.Value)}";

    /// <summary>True when <paramref name="v"/> is inside the range.</summary>
    public bool InRange(double v) => (Min is not { } mn || (MinExclusive ? v > mn : v >= mn)) && (Max is not { } mx || v <= mx);

    static string Number(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The one table of settings keys. The level resolver checks which levels allow a key, the merge reads
/// the merge kind, <c>skarp config</c> the defaults and the fingerprint note whether a key affects findings. Every list
/// gets a generated <c>&lt;name&gt;Mode</c> key, so no list can lack one (a test checks every <see cref="SettingsFile"/>
/// Property has a row). Each row also has its <c>skarp setup</c> section, kind, range or allowed
/// values and a one-line description; the rows of <c>rules.&lt;id&gt;</c> and <c>thresholds.*</c> are generated from the
/// rule catalog and the presets (<see cref="Generated"/>), kept apart from <see cref="All"/> so the loader and
/// <c>skarp config</c> see the same table as before.
/// </summary>
public static class SettingsRegistry
{
    const MergeKind S = MergeKind.Scalar, O = MergeKind.Object, L = MergeKind.List, Ov = MergeKind.Overrides;
    const SettingSection Hist = SettingSection.History, Sco = SettingSection.Score, Sem = SettingSection.Semantic,
        Agt = SettingSection.Agent, Lst = SettingSection.Lists, Thr = SettingSection.Thresholds;
    const SettingKind Bool = SettingKind.Boolean, Int = SettingKind.Integer, Num = SettingKind.Number, Choice = SettingKind.Choice,
        Text = SettingKind.Text, List = SettingKind.List, PathKind = SettingKind.Path, Obj = SettingKind.Object;

    /// <summary>The upper end of every count.</summary>
    public const double MaxCount = 100_000;

    static readonly SettingsKey[] Declared =
    [
        new("version", "number", "1", S) { Kind = Int, Min = 1, Max = 1, Description = "Format version of the settings file; always 1." },
        new("useInRepositories", "boolean", "true", S, Levels: LevelScope.Machine) { Kind = Bool, Description = "Apply this file inside repositories too; false keeps it for folders outside any repository." },
        new("root", "boolean", "false", S, Levels: LevelScope.Repository) { Kind = Bool, Description = "Marks the repository root (takes effect with the folder level)." },
        new("preset", "string", "default", S, AffectsFindings: true) { Section = Thr, Kind = Choice, Allowed = [.. Presets.All.Select(p => p.Id)], Description = "The threshold preset: whose published values the rules use." },
        new("complexMetric", "string", "cognitive", S, AffectsFindings: true) { Kind = Choice, Allowed = ["cognitive", "cyclomatic"], Description = "Metric of the complex-method rule: cognitive or cyclomatic complexity." },
        new("longMethodLines", "string", "physical", S, AffectsFindings: true) { Kind = Choice, Allowed = ["physical", "source"], Description = "Line unit of long-method: physical (every line of the declaration) or source (no blank or comment lines)." },
        new("recordParameters", "string", "exempt", S, AffectsFindings: true) { Kind = Choice, Allowed = ["exempt", "check"], Description = "Whether positional record parameters count for too-many-parameters." },
        new("magicNumbers", "string", "count", S) { Kind = Choice, Allowed = ["count", "list"], Description = "Magic numbers in the top list: counted only, or listed." },
        new("rules", "object", "(rule defaults)", O, AffectsFindings: true) { Section = SettingSection.Rules, Kind = Obj, Description = "Rules switched on or off by rule id." },
        new("thresholds", "object", "(preset)", O, AffectsFindings: true) { Section = Thr, Kind = Obj, Description = "Threshold overrides per rule, level and metric." },
        new("includeTests", "boolean", "false", S, AffectsFindings: true) { Kind = Bool, Description = "Analyze test files too (they are detected and left out by default)." },
        new("include", "list of globs", "(none)", L, AffectsFindings: true) { Section = Lst, Kind = List, Description = "Globs that bring back files the test detection left out." },
        new("exclude", "list of globs", "(none)", L, AffectsFindings: true) { Section = Lst, Kind = List, Description = "Globs of files and folders to leave out." },
        new("top", "number", "20", S) { Kind = Int, Min = 0, Max = MaxCount, Description = "Places shown in the report, and rows in the hotspot tables." },
        new("history", "object", "", O) { Section = Hist, Kind = Obj, Description = "Git history settings." },
        new("history.enabled", "boolean", "true", S) { Section = Hist, Kind = Bool, Description = "Read git history (activity, hotspots); off is faster." },
        new("history.window", "number", "12", S) { Section = Hist, Kind = Int, Min = 1, Max = 1200, Description = "Months of history read, ending at the last commit." },
        new("history.since", "date", "(none)", S) { Section = Hist, Kind = Text, Description = "Start date of the history window, such as 2026-01-01 (replaces the window length)." },
        new("history.allHistory", "boolean", "false", S) { Section = Hist, Kind = Bool, Description = "Read the whole history." },
        new("history.ignoreWhitespace", "boolean", "false", S) { Section = Hist, Kind = Bool, Description = "Ignore whitespace changes in the changed-line counts." },
        new("history.bulkFlag", "number", "300", S) { Section = Hist, Kind = Int, Min = 0, Max = MaxCount, Description = "List commits that touch more than this many C# files." },
        new("history.bulkDrop", "number", "(none)", S) { Section = Hist, Kind = Int, Min = 0, Max = MaxCount, Description = "Drop commits that touch more than this many C# files." },
        new("history.includeBots", "boolean", "false", S) { Section = Hist, Kind = Bool, Description = "Count commits by bots (left out by default)." },
        new("history.botPatterns", "list of regular expressions", "(none)", L) { Section = Lst, Kind = List, Description = "Extra bot patterns (regular expressions) on author name or email." },
        new("report", "object", "", O) { Kind = Obj, Description = "Report layout." },
        new("report.perFile", "number", "3", S) { Kind = Int, Min = 0, Max = MaxCount, Description = "Places shown per file before \"+N more\" (0: no cap)." },
        new("gate", "object", "", O) { Kind = Obj, Description = "The opt-in gate." },
        // The gate is the repository's: a machine file cannot switch it on.
        new("gate.enabled", "boolean", "false", S, Levels: LevelScope.Repository) { Kind = Bool, Description = "Turn the gate on: exit code 3 on new or worsened findings against the baseline." },
        new("gate.on", "list of rule:severity pairs", "*:warning", L) { Section = Lst, Kind = List, Description = "Rule and severity pairs that trip the gate (rule:severity, * for any)." },
        // The health limits; part of the gate wherever it runs, independent of gate.on.
        new("gate.health", "object", "", O) { Kind = Obj, Description = "The gate's health limits per file (0 turns a limit off)." },
        new("gate.health.maxDrop", "number", "1.0", S) { Kind = Num, Min = 0, Max = Health.HealthLimits.MaxDropLimit, Description = "The gate stops a file whose health fell by this much or more (against the baseline; in Claude Code, since the session started). 0 is off." },
        new("gate.health.newFileMin", "number", "6.5", S) { Kind = Num, Min = 0, Max = Health.HealthLimits.MaxScoreLimit, Description = "The gate stops a new file whose health is below this. 0 is off." },
        new("gate.health.floor", "number", "6.5", S) { Kind = Num, Min = 0, Max = Health.HealthLimits.MaxScoreLimit, Description = "The gate stops a file whose health fell at all and ends below this; a file that did not get worse never blocks. 0 is off." },
        new("semantic", "object", "", O) { Section = Sem, Kind = Obj, Description = "Semantic tier settings." },
        new("semantic.enabled", "boolean", "true", S) { Section = Sem, Kind = Bool, Description = "Run the semantic tier (a design-time build of the solution) when the inputs allow." },
        new("semantic.solution", "path", "(automatic)", S, IsPath: true, Levels: LevelScope.Repository) { Section = Sem, Kind = PathKind, Description = "The solution to load, relative to this file (default: chosen automatically)." },
        new("semantic.generators", "boolean", "true", S, AffectsFindings: true) { Section = Sem, Kind = Bool, Description = "Run source generators in the semantic tier." },
        new("semantic.targets", "list of target frameworks", "highest", L, AffectsFindings: true) { Section = Lst, Kind = List, Description = "Target frameworks: highest, all, or monikers such as net10.0." },
        new("unusedCode", "object", "", O) { Section = Sem, Kind = Obj, Description = "Unused-code rule settings." },
        new("unusedCode.allow", "list of patterns", "(none)", L, AffectsFindings: true) { Section = Lst, Kind = List, Description = "Code that counts as used: documentation ids or names, with * and ?." },
        new("namespaceCycles", "object", "", O) { Section = Sem, Kind = Obj, Description = "Namespace-cycle rule settings." },
        new("namespaceCycles.includeGeneratedTargets", "boolean", "false", S, AffectsFindings: true) { Section = Sem, Kind = Bool, Description = "Count references to types declared only in generated code." },
        new("saveLastRun", "boolean", "true", S) { Section = Agt, Kind = Bool, Description = "Save each analyze run under .skarp/last-run/ for skarp show." },
        new("output", "object", "", O) { Section = Agt, Kind = Obj, Description = "Finding-line budgets of skarp check and the Claude hook." },
        new("output.checkMaxLines", "number", "5", S) { Section = Agt, Kind = Int, Min = 1, Max = MaxCount, Description = "Finding lines of skarp check (the after-edit hook)." },
        new("output.gateMaxLines", "number", "50", S) { Section = Agt, Kind = Int, Min = 1, Max = SkarpOptions.MaxGateMaxLines, Description = "Finding lines of the end-of-turn summary with --gate (every blocking line first)." },
        new("tracking", "object", "", O) { Section = Agt, Kind = Obj, Description = "Tracking files of skarp check --track." },
        new("tracking.retentionDays", "number", "30", S) { Section = Agt, Kind = Int, Min = 1, Max = MaxCount, Description = "Days tracking files are kept." },
        new("tracking.lookbackDays", "number", "14", S) { Section = Agt, Kind = Int, Min = 0, Max = MaxCount, Description = "Days back an earlier record of the same file and session is looked for." },
        new("score", "object", "", O) { Section = Sco, Kind = Obj, Description = "Code health score settings." },
        new("score.model", "string", "grade", S) { Section = Sco, Kind = Choice, Allowed = [.. Health.ScoreModels.Names], Description = "The score model." },
        new("score.decay", "number", "0.7", S) { Section = Sco, Kind = Num, Min = 0, MinExclusive = true, Max = 1, Description = "Each further finding of a rule in a file costs this share of the one before." },
        new("score.weights", "object", "(table)", O) { Section = Sco, Kind = Obj, Description = "Weight overrides per rule; edit with skarp setup --set score.weights.<rule>.<notice|warning|cap|decay>=<n>." },
        new("score.worst", "object", "", O) { Section = Sco, Kind = Obj, Description = "The worst-files list." },
        new("score.worst.count", "number", "10", S) { Section = Sco, Kind = Int, Min = 0, Max = MaxCount, Description = "Files in the worst-files list." },
        new("score.worst.below", "number", "7", S) { Section = Sco, Kind = Num, Min = 1, Max = 10.5, Description = "Only files scoring below this are listed as worst." },
        new("coverage", "list of globs", "(none)", L, IsPath: true, Levels: LevelScope.Repository) { Section = Lst, Kind = List, Description = "Cobertura coverage reports (globs relative to this file)." },
        new("symbols", "list of preprocessor symbols", "(automatic)", L, AffectsFindings: true) { Section = Lst, Kind = List, Description = "Preprocessor symbols to analyze (replaces the automatic planning)." },
        // Per-path rules and thresholds; edited in the file only (skarp setup shows a read-only row).
        new("overrides", "list of objects", "(none)", Ov, AffectsFindings: true, Levels: LevelScope.Repository) { Section = Lst, Kind = Obj, Description = "Per-path rules and thresholds: each entry selects files (files, excludeFiles, tests) and changes rules and thresholds for them." },
        new("variants", "object", "", O) { Kind = Obj, Description = "Preprocessor variant caps." },
        new("variants.cap", "number", "32", S, AffectsFindings: true) { Kind = Int, Min = 0, Max = MaxCount, Description = "Variants per file besides the base parse for analyze (0: the base view only)." },
        new("variants.checkCap", "number", "4", S, AffectsFindings: true) { Kind = Int, Min = 1, Max = MaxCount, Description = "Variants per file for skarp check, the base parse counted in." },
    ];

    /// <summary>Every key: the declared ones, each list followed by its generated mode key.</summary>
    public static readonly IReadOnlyList<SettingsKey> All = [.. Declared.SelectMany(k => k.ModeKey is { } m
        ? new[] { k, new SettingsKey(m, "replace or append", "replace", S, Levels: k.Levels)
        {
            Section = k.Section, Kind = Choice, Allowed = ["replace", "append"],
            Description = $"How this level's {k.Key} meets the inherited one: replace it, or append to it.",
        } } : [k])];

    static readonly Dictionary<string, SettingsKey> ByKey = All.ToDictionary(k => k.Key, StringComparer.Ordinal);
    static readonly Dictionary<string, SettingsKey> ModeOf = Declared.Where(k => k.ModeKey is not null).ToDictionary(k => k.ModeKey!, StringComparer.Ordinal);

    public static SettingsKey? Find(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>The list a mode key belongs to (<c>history.botPatternsMode</c> to <c>history.botPatterns</c>), or null.</summary>
    public static SettingsKey? ListOfMode(string key) => ModeOf.GetValueOrDefault(key);

    /// <summary>
    /// The row that governs <paramref name="key"/>: its own, else its nearest registered parent (the entries of
    /// <c>rules</c>, <c>thresholds</c> and <c>score.weights</c> are governed by those keys).
    /// </summary>
    public static SettingsKey? Governing(string key)
    {
        for (var k = key; ; )
        {
            if (ByKey.TryGetValue(k, out var row)) return row;
            int dot = k.LastIndexOf('.');
            if (dot < 0) return null;
            k = k[..dot];
        }
    }

    // ---- commands per key ---------------------------------------------------------------------------------

    const SettingCommands An = SettingCommands.Analyze, Ba = SettingCommands.Baseline, Ho = SettingCommands.Hotspots,
        Hi = SettingCommands.History, Co = SettingCommands.Config, Ch = SettingCommands.Check;

    /// <summary>The command of a name (<c>analyze</c>, <c>baseline</c>, <c>hotspots</c>, <c>history</c>, <c>config</c>,
    /// <c>check</c>), or <see cref="SettingCommands.None"/>.</summary>
    public static SettingCommands Command(string name) => name switch
    {
        "analyze" => An, "baseline" => Ba, "hotspots" => Ho, "history" => Hi, "config" => Co, "check" => Ch,
        _ => SettingCommands.None,
    };

    /// <summary>
    /// The commands that read <paramref name="key"/> (a mode key: its list's). The
    /// keys that describe a file (<c>version</c>, <c>useInRepositories</c>, <c>root</c>) list none; <c>config</c> shows
    /// every key a run can set; <c>check</c> is listed where it reads the key from the files (it takes no <c>--setting</c>).
    /// </summary>
    public static SettingCommands CommandsOf(string key)
    {
        if (ListOfMode(key) is { } list) key = list.Key;
        int dot = key.IndexOf('.');
        var head = dot < 0 ? key : key[..dot];
        const SettingCommands findings = An | Ba | Ho | Co | Ch;
        return head switch
        {
            "version" or "useInRepositories" or "root" => SettingCommands.None,
            "overrides" => An | Ba | Ho | Ch,
            "preset" or "complexMetric" or "longMethodLines" or "recordParameters" or "rules" or "thresholds" => findings,
            "includeTests" or "include" or "exclude" => findings | Hi,
            "magicNumbers" or "report" => An | Ho | Co,
            "top" => An | Ba | Ho | Hi | Co,
            "history" => key is "history" or "history.enabled" ? An | Ba | Ho | Co : An | Ba | Ho | Hi | Co,
            // Check reads the health limits from the files (check --gate, the hooks, MCP check).
            "gate" => key.StartsWith("gate.health", StringComparison.Ordinal) ? An | Co | Ch : An | Co,
            "saveLastRun" => An | Co,
            "semantic" or "unusedCode" or "namespaceCycles" => An | Ba | Co,
            "output" => Ch | Co,
            "tracking" => Ch | Co,
            "score" or "coverage" => An | Ba | Ho | Co,
            "symbols" => findings,
            "variants" => key == "variants.checkCap" ? Ch | Co : key == "variants.cap" ? An | Ba | Ho | Co : findings,
            _ => SettingCommands.None,
        };
    }

    // ---- generated rows ----------------------------------------------------------------------------------

    /// <summary>Every rule the settings can name: syntax, semantic, composite and coverage rules.</summary>
    public static IEnumerable<RuleInfo> Rules => RuleCatalog.Every.Concat(RuleCatalog.Composite).Concat(RuleCatalog.Coverage);

    /// <summary>
    /// The generated rows: <c>rules.&lt;id&gt;</c> (on or off), then per rule
    /// <c>thresholds.&lt;rule&gt;.notice</c> and <c>.warning</c> (rules with a metric: a number or <c>off</c>; rules
    /// without one: <c>off</c> only, for the levels they have) and one row per rule parameter.
    /// </summary>
    public static readonly IReadOnlyList<SettingsKey> Generated = Generate();

    static readonly Dictionary<string, SettingsKey> GeneratedByKey = Generated.ToDictionary(k => k.Key, StringComparer.Ordinal);

    /// <summary>The rows <c>skarp setup</c> edits: the declared ones that hold a value (not sections, not
    /// <c>version</c>), then the generated ones.</summary>
    public static readonly IReadOnlyList<SettingsKey> Editable = EditableRows();

    static List<SettingsKey> EditableRows()
    {
        var list = new List<SettingsKey>();
        foreach (var k in All) if (k.Kind != SettingKind.Object && k.Key != "version") list.Add(k);
        list.AddRange(Generated);
        return list;
    }

    /// <summary>
    /// The row of <paramref name="key"/> for the editor: a declared or generated row, or for a threshold metric
    /// (<c>thresholds.large-type.notice.wmc</c>) its level's row; null when none.
    /// </summary>
    public static SettingsKey? Row(string key)
    {
        if (Find(key) is { } row) return row;
        if (GeneratedByKey.TryGetValue(key, out var gen)) return gen;
        if (key.StartsWith("thresholds.", StringComparison.Ordinal) && key.Count(c => c == '.') == 3
            && GeneratedByKey.TryGetValue(key[..key.LastIndexOf('.')], out var level) && level.Part is "notice" or "warning")
            return level;
        return null;
    }

    /// <summary>The range of a threshold metric: counts 1 to 100,000; similarity 0 to 1; activity share a percentage.</summary>
    public static (double Min, double Max) MetricRange(string metric) => metric switch
    {
        "similarity" or "tcc" => (0, 1),
        "activityShare" => (0, 100),
        _ => (1, MaxCount),
    };

    static List<SettingsKey> Generate()
    {
        var rows = new List<SettingsKey>();
        foreach (var rule in Rules)
            rows.Add(new SettingsKey($"rules.{rule.Id}", "boolean", rule.DefaultOff ? "off" : "on", S, AffectsFindings: true)
            {
                Section = SettingSection.Rules, Kind = Bool, Rule = rule.Id, Description = rule.Short,
            });
        foreach (var rule in Rules)
        {
            var composite = Presets.Default.Rules.GetValueOrDefault(rule.Id == RuleIds.ComplexMethod ? $"{rule.Id}/cognitive" : rule.Id);
            if (!RuleCatalog.IsComposite(rule.Id))
                foreach (var level in new[] { "notice", "warning" })
                {
                    bool numeric = rule.Metrics.Count > 0;
                    var defaultLevel = level == "notice" ? composite?.Notice : composite?.Warning;
                    if (!numeric && defaultLevel is null) continue; // a structural rule without this level
                    var range = numeric ? MetricRange(rule.Metrics[0]) : default;
                    rows.Add(new SettingsKey($"thresholds.{rule.Id}.{level}", numeric ? "number, off or an object of metric values" : "off", "(preset)", S, AffectsFindings: true)
                    {
                        Section = Thr, Kind = numeric ? Num : Choice, Allowed = numeric ? null : ["off"],
                        Min = numeric ? range.Min : null, Max = numeric ? range.Max : null, Rule = rule.Id, Part = level,
                        Description = numeric
                            ? $"{rule.Title}: when a {rule.Unit} gets a {level}."
                            : $"{rule.Title}: the {level} level has no number; it is on or off.",
                    });
                }
            foreach (var p in rule.ParameterNames) rows.Add(Parameter(rule, p));
        }
        return rows;
    }

    static SettingsKey Parameter(RuleInfo rule, string name)
    {
        var key = $"thresholds.{rule.Id}.{name}";
        var row = new SettingsKey(key, "parameter", "(preset)", S, AffectsFindings: true) { Section = Thr, Rule = rule.Id, Part = name };
        return (rule.Id, name) switch
        {
            (RuleIds.ActiveComplexFile, "share") => row with { Kind = Num, Min = 0, MinExclusive = true, Max = 100, Description = "Active complex file: the share of recent changes (percent) a file needs." },
            (RuleIds.BumpyRoad, "depth") => row with { Kind = Int, Min = 2, Max = MaxCount, Description = "Bumpy road: the nesting depth at which a block counts as a bump." },
            (RuleIds.MagicNumber, "ignore") => row with { Kind = Text, Description = "Magic numbers: the numbers that never count (comma-separated)." },
            (RuleIds.GodClass, "preset") => row with { Kind = Choice, Allowed = [.. GodClassPresets.Sets.Select(s => s.Id)], Description = "God Class: the published rule set used." },
            (RuleIds.DuplicateCode, "copyLines") => row with { Kind = Int, Min = 1, Max = MaxCount, Description = "Duplicate code: the lines a copy needs before copies are counted." },
            (RuleIds.DuplicateCode, "lowContent") => row with { Kind = Choice, Allowed = ["skip", "report"], Description = "Duplicate code: skip or report clones that are mostly declarations and literals." },
            (RuleIds.BrainMethod, "parts") => row with { Kind = Text, Description = $"Brain method: the member rules that make it (comma-separated: {string.Join(", ", BrainRules.PartRules)})." },
            (RuleIds.BrainMethod, "minParts") => row with { Kind = Text, Description = "Brain method: how many parts must fire at one member (a number, or all)." },
            (RuleIds.BrainClass, "minMethods") => row with { Kind = Int, Min = 0, Max = MaxCount, Description = "Brain class: the methods a type needs." },
            (RuleIds.BrainClass, "preset") => row with { Kind = Choice, Allowed = [BrainRules.OursForm, BrainRules.BookForm], Description = "Brain class: Skarp's form, or the book's with its size legs." },
            _ => row with { Kind = Text, Description = $"{rule.Title}: the {name} parameter." },
        };
    }
}
