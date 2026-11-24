using System.Globalization;
using SkarpSharp.Analysis;
using SkarpSharp.Findings;
using SkarpSharp.History;

namespace SkarpSharp.Configuration;

/// <summary>One effective setting with its origin level (for <c>skarp config</c>, with the file and the values it overrode).</summary>
/// <param name="Origin">The origin level's name (<see cref="SettingOrigins.Name"/>).</param>
/// <param name="Path">The file of the origin level, or null (defaults, command line).</param>
/// <param name="Overridden">The values lower levels set, lowest first (the origin chain).</param>
public sealed record EffectiveSetting(string Key, string Value, string Origin, SettingOrigin Level = SettingOrigin.Default,
    string? Path = null, IReadOnlyList<OriginEvent>? Overridden = null);

/// <summary>
/// The one options object of a run: built from the defaults, then <c>.skarp.json</c>, then
/// command-line flags, by <see cref="CommandLine.Parse"/>. Read-only for callers; every setting that can come from the
/// settings file remembers its origin (<see cref="Settings"/>).
/// </summary>
public sealed class SkarpOptions
{
    public string Command { get; internal set; } = "";
    public string Directory { get; internal set; } = "";

    // Output
    public string Format { get; internal set; } = "text";
    public string? Output { get; internal set; }
    public int Top { get; internal set; } = Hotspots.DefaultTop;
    public bool All { get; internal set; }
    public Severity MinSeverity { get; internal set; } = Severity.Notice;
    /// <summary>True when <c>--min-severity</c> was given (the push hook's report defaults to warnings).</summary>
    public bool MinSeverityGiven { get; internal set; }
    public string Group { get; internal set; } = "file";
    public string Detail { get; internal set; } = "members";
    public bool ExcludeTestsWhy { get; internal set; }
    /// <summary>List the findings left out by inline suppression in the human report.</summary>
    public bool ShowSuppressed { get; internal set; }
    /// <summary><c>--verbose</c>, the text report's run details, health block, rule sentences and tables.</summary>
    public bool Verbose { get; internal set; }
    /// <summary>Places shown per file in the default view (0 = no cap; <c>--all</c> lifts it).</summary>
    public int PerFile { get; internal set; } = DefaultPerFile;
    /// <summary>Places shown per file by default.</summary>
    public const int DefaultPerFile = 3;
    /// <summary><c>--sort</c>, a worst-first ranking (health, hotspot or a rule id) instead of the top places; null without.</summary>
    public string? Sort { get; internal set; }
    /// <summary><c>--unit</c> of the ranking (auto, files or methods); null without.</summary>
    public string? Unit { get; internal set; }

    // Gate
    /// <summary>The gate is on (<c>--gate</c>, setting <c>gate.enabled</c>); applies to <c>analyze</c>.</summary>
    public bool Gate { get; internal set; }
    /// <summary>Rule and severity pairs that trip the gate; empty means <see cref="Findings.Gate.DefaultPair"/>.</summary>
    public IReadOnlyList<string> GateOn => _gateOn;
    /// <summary>The gate's health limits (<c>gate.health.*</c>, <c>--gate-health-*</c>).</summary>
    public Health.HealthLimits HealthLimits { get; internal set; } = Health.HealthLimits.Default;

    // Baseline
    /// <summary>Baseline file given with <c>--baseline</c>; null means <c>.skarp-baseline.json</c> in the analyzed directory.</summary>
    public string? BaselinePath { get; internal set; }
    public bool NoBaseline { get; internal set; }
    public bool NewOnly { get; internal set; }

    // Findings
    public string Preset { get; internal set; } = "default";
    /// <summary>Null means the default (cognitive).</summary>
    public string? ComplexMetric { get; internal set; }
    public string LongMethodLines { get; internal set; } = "physical";
    /// <summary>Threshold overrides in <c>--threshold</c> grammar: the file's first, then the flags' (later wins).</summary>
    public IReadOnlyList<string> Thresholds => _thresholds;
    /// <summary>Rules switched off (settings file <c>rules</c>, <c>--rule id=off</c>).</summary>
    public IReadOnlyCollection<string> DisabledRules => [.. RuleCatalog.Every.Concat(RuleCatalog.Composite).Concat(RuleCatalog.Coverage).Select(r => r.Id).Where(id => !RuleOn(id)).Order(StringComparer.Ordinal)];

    /// <summary>A rule is on unless switched off; a default-off rule (experimental) only when switched on.</summary>
    public bool RuleOn(string id) => _rules.TryGetValue(id, out var on) ? on : RuleCatalog.Find(id)?.DefaultOff != true;
    /// <summary>Positional record parameters are judged by too-many-parameters (default: exempt).</summary>
    public bool CheckRecordParameters { get; internal set; }
    /// <summary>Magic-number findings are listed in the top list (default: counted only).</summary>
    public bool ListMagicNumbers { get; internal set; }

    // Files
    public bool IncludeGenerated { get; internal set; }
    public bool IncludeTests { get; internal set; }
    public IReadOnlyList<string> Include => _include;
    public IReadOnlyList<string> Exclude => _exclude;
    public int Parallelism { get; internal set; } = Environment.ProcessorCount;

    // History
    public bool History { get; internal set; } = true;
    public DateTimeOffset? Since { get; internal set; }
    public int WindowMonths { get; internal set; } = 12;
    public bool AllHistory { get; internal set; }
    public bool IgnoreWhitespace { get; internal set; }
    public int BulkFlag { get; internal set; } = HistoryPolicy.DefaultBulkFlag;
    public int? BulkDrop { get; internal set; }
    public bool IncludeBots { get; internal set; }
    public IReadOnlyList<string> BotPatterns => _botPatterns;

    /// <summary>The repository-level settings file that was applied (<c>.skarp.json</c>, <c>--config</c>, <c>SKARP_CONFIG</c>), or null.</summary>
    public string? ConfigPath { get; internal set; }
    /// <summary>Every settings level, lowest first, with its file and whether it applied or why not.</summary>
    public IReadOnlyList<SettingsSource> Levels { get; internal set; } = [];
    /// <summary>The levels whose file applied, lowest first (<c>run.settingsFiles</c>).</summary>
    public IReadOnlyList<SettingsSource> SettingsFiles => [.. Levels.Where(l => l.Applied && l.Path is not null)];
    /// <summary><c>skarp config --origins</c>: also print the values each setting overrode.</summary>
    public bool Origins { get; internal set; }
    /// <summary><c>skarp config --file &lt;path&gt;</c>: show one file's effective rules and thresholds.</summary>
    public string? ConfigFile { get; internal set; }

    // Semantic tier
    /// <summary>The semantic tier runs when the inputs allow (<c>--no-semantic</c>, setting <c>semantic.enabled</c>).</summary>
    public bool Semantic { get; internal set; } = true;
    /// <summary><c>--restore</c>: <c>dotnet restore</c> the solution first (implies the semantic tier).</summary>
    public bool Restore { get; internal set; }
    /// <summary>Solution for the semantic tier (absolute), or null for the automatic choice.</summary>
    public string? Solution { get; internal set; }
    /// <summary>Run source generators (<c>--no-generators</c>, setting <c>semantic.generators</c>).</summary>
    public bool Generators { get; internal set; } = true;
    /// <summary>Target frameworks: empty for the highest per project, <c>all</c>, or monikers.</summary>
    public IReadOnlyList<string> Targets => _targets;
    /// <summary>Semantic output file (<c>--semantic-output</c>); without it the file goes next to <c>--output</c>.</summary>
    public string? SemanticOutput { get; internal set; }
    /// <summary><c>unusedCode.allow</c>, patterns of code that counts as used.</summary>
    public IReadOnlyList<string> UnusedAllow => _unusedAllow;
    /// <summary><c>namespaceCycles.includeGeneratedTargets</c>, <c>--include-generated-targets</c>.</summary>
    public bool IncludeGeneratedTargets { get; internal set; }

    // Agent loop (experimental)
    /// <summary><c>analyze</c> saves its results in <c>.skarp/last-run/</c> (<c>--no-save</c>, setting <c>saveLastRun</c>).</summary>
    public bool SaveLastRun { get; internal set; } = true;
    /// <summary>Finding lines of the Claude hook's end-of-turn summary without <c>--gate</c> (a constant since
    /// <c>agent.maxLines</c> went; <c>check --max-lines</c> still overrides it).</summary>
    public const int StopMaxLines = 15;
    /// <summary>Finding lines of <c>skarp check</c> (setting <c>output.checkMaxLines</c>, <c>check --max-lines</c>).</summary>
    public int CheckMaxLines { get; internal set; } = DefaultCheckMaxLines;
    public const int DefaultCheckMaxLines = 5;
    /// <summary>Finding lines of the <c>Stop</c> summary with <c>--gate</c> (setting <c>output.gateMaxLines</c>).</summary>
    public int GateMaxLines { get; internal set; } = DefaultGateMaxLines;
    public const int DefaultGateMaxLines = 50;
    /// <summary>The largest <c>output.gateMaxLines</c>.</summary>
    public const int MaxGateMaxLines = 1000;
    /// <summary>Tracking files older than this are deleted (setting <c>tracking.retentionDays</c>).</summary>
    public int TrackingRetentionDays { get; internal set; } = 30;
    /// <summary>Days an earlier tracking record is looked for (setting <c>tracking.lookbackDays</c>).</summary>
    public int TrackingLookbackDays { get; internal set; } = 14;

    // Code health score (experimental)
    /// <summary>The score model, decay, weight overrides and worst-files list (setting <c>score</c>, <c>--score-model</c>).</summary>
    public Health.ScoreSettings Score { get; internal set; } = Health.ScoreSettings.Default;

    // Coverage
    /// <summary>Report files given with <c>--coverage</c> (absolute; they replace the settings file's globs).</summary>
    public IReadOnlyList<string> CoverageFiles => _coverageFiles;
    /// <summary>The settings file's <c>coverage</c> globs, relative to <see cref="CoverageGlobBase"/>.</summary>
    public IReadOnlyList<string> CoverageGlobs => _coverageGlobs;
    /// <summary>The folder the settings file's coverage globs are relative to (the settings file's folder).</summary>
    public string? CoverageGlobBase { get; internal set; }
    /// <summary><c>--strict-coverage</c>: a stale coverage report fails the run (exit code 2) instead of a warning.</summary>
    public bool StrictCoverage { get; internal set; }
    /// <summary>True when a coverage report is given (flag or settings).</summary>
    public bool HasCoverage => _coverageFiles.Count > 0 || _coverageGlobs.Count > 0;

    // Preprocessor symbol variants
    /// <summary>The <c>symbols</c> setting, the variant caps (<c>variants.cap</c>, <c>variants.checkCap</c>).</summary>
    public VariantSettings Variants { get; internal set; } = VariantSettings.Default;

    // Per-path settings
    /// <summary>The repository file's <c>overrides</c>, in file order; empty when none.</summary>
    public IReadOnlyList<RuleOverride> RuleOverrides { get; internal set; } = [];
    /// <summary>The merged <c>rules</c> and <c>thresholds</c> before any override (the tree each combination patches).</summary>
    internal System.Text.Json.Nodes.JsonObject? RulesTree { get; set; }
    /// <summary>The <c>rules</c> and <c>thresholds</c> of <c>--setting</c> (they apply after the overrides).</summary>
    internal System.Text.Json.Nodes.JsonObject? CommandLineRules { get; set; }
    /// <summary>The file that holds the overrides (errors name it).</summary>
    internal string? OverridesPath { get; set; }
    PathRules? _pathRules;

    /// <summary>
    /// The per-path rule settings of this run (one resolver, built on first use): the repository settings
    /// (<see cref="RuleSettings()"/>) for every file no override matches; with no overrides, nothing else.
    /// </summary>
    public PathRules PathRules() => _pathRules ??= BuildPathRules();

    PathRules BuildPathRules()
    {
        var @base = RuleSettings();
        if (RuleOverrides.Count == 0) return Findings.PathRules.Single(@base);
        static Func<string, bool>? Matcher(IReadOnlyList<string>? globs)
        {
            if (globs is null) return null;
            var list = new Discovery.Glob[globs.Count];
            for (int i = 0; i < list.Length; i++) list[i] = new Discovery.Glob(globs[i]);
            return p =>
            {
                foreach (var g in list) if (g.Matches(p)) return true;
                return false;
            };
        }
        var scopes = new List<RuleScope>(RuleOverrides.Count);
        foreach (var r in RuleOverrides) scopes.Add(new RuleScope(Matcher(r.RebasedFiles), Matcher(r.RebasedExcludeFiles), r.Tests));
        return new PathRules(@base, scopes, matched => Combination(@base, matched));
    }

    /// <summary>
    /// The settings of one combination of matching entries: the merged rules and thresholds, each entry's patch in file
    /// order through the settings merge (later wins key by key, <c>null</c> resets to the Skarp default), then the command
    /// line. Rules the entries do not name share the repository settings' objects.
    /// </summary>
    RuleSettings Combination(RuleSettings @base, int[] matched)
    {
        var path = OverridesPath ?? SettingsFileReader.FileName;
        var tree = (System.Text.Json.Nodes.JsonObject?)RulesTree?.DeepClone() ?? [];
        var events = new List<OriginEvent>();
        var touched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var i in matched)
        {
            SettingsLevels.Merge(tree, RuleOverrides[i].Patch, "", SettingOrigin.Repository, path, events);
            touched.UnionWith(RuleOverrides[i].RulesNamed);
        }
        if (CommandLineRules is { } cl) SettingsLevels.Merge(tree, cl, "", SettingOrigin.CommandLine, null, events);
        tree["version"] = SettingsFileReader.CurrentVersion;
        var f = SettingsFileReader.Parse(tree.ToJsonString(), path);
        var thresholds = new List<string>();
        foreach (var (text, _) in SettingsFileReader.ThresholdOverrides(f, path)) thresholds.Add(text);
        thresholds.AddRange(_cliThresholds);
        var disabled = new List<string>();
        foreach (var id in touched)
        {
            bool on = _cliRules.TryGetValue(id, out var flag) ? flag
                : f.Rules is not null && f.Rules.TryGetValue(id, out var set) ? set
                : RuleCatalog.Find(id)?.DefaultOff != true;
            if (!on) disabled.Add(id);
        }
        return Findings.RuleSettings.ResolveSharing(@base, touched, thresholds, disabled);
    }

    /// <summary>The levels of the run and their origin events, including the repository file's overrides.</summary>
    internal void Keep(LayeredSettings layered)
    {
        Levels = layered.Levels;
        _events.AddRange(layered.Events);
        ConfigPath = layered.RepositoryFile;
        RuleOverrides = layered.Overrides;
        RulesTree = layered.RulesTree;
        OverridesPath = layered.MergedPath;
        CommandLineRules = layered.CommandLineRules;
    }

    /// <summary>Checks the thresholds once, after the files and the flags combined, and also each override entry's
    /// thresholds under this run's preset, complexity metric and line unit (the repository settings are resolved once,
    /// for the run).</summary>
    /// <exception cref="ArgumentException">The message names the entry.</exception>
    internal void ValidateRuleSettings()
    {
        if (RuleOverrides.Count == 0) { _ = RuleSettings(); return; }
        var @base = PathRules().Base;
        foreach (var r in RuleOverrides)
        {
            try { _ = Findings.RuleSettings.ResolveSharing(@base, r.RulesNamed, r.ThresholdTexts, null); }
            catch (ArgumentException ex) { throw new ArgumentException($"{OverridesPath}: overrides[{r.Index}].thresholds: {ex.Message}", ex); }
        }
    }

    internal readonly List<string> _coverageFiles = [];
    internal readonly List<string> _coverageGlobs = [];
    internal readonly List<string> _thresholds = [];
    /// <summary>The command line's <c>--threshold</c> and <c>--rule</c> flags (applied on top of the overrides).</summary>
    internal readonly List<string> _cliThresholds = [];
    internal readonly Dictionary<string, bool> _cliRules = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, bool> _rules = new(StringComparer.Ordinal);
    internal readonly List<string> _include = [];
    internal readonly List<string> _exclude = [];
    internal readonly List<string> _botPatterns = [];
    internal readonly List<string> _gateOn = [];
    internal readonly List<string> _targets = [];
    internal readonly List<string> _unusedAllow = [];
    /// <summary>Every value a level set, in the order applied (the merge's events, then the flags').</summary>
    internal readonly List<OriginEvent> _events = [];

    /// <summary>Records that a flag set <paramref name="key"/> (the files' values are recorded by the merge).</summary>
    internal void Mark(string key, SettingOrigin origin, string? value = null) => _events.Add(new(key, origin, null, value ?? ""));

    /// <summary>The events of <paramref name="key"/>: its own, its parents' (a reset) and its entries' (<c>score.weights.x.notice</c>).</summary>
    IEnumerable<OriginEvent> EventsOf(string key) => _events.Where(e => e.Key == key
        || (e.Key.Length > key.Length && e.Key[key.Length] == '.' && e.Key.StartsWith(key, StringComparison.Ordinal))
        || (key.Length > e.Key.Length && key[e.Key.Length] == '.' && key.StartsWith(e.Key, StringComparison.Ordinal)));

    /// <summary>The level that set the effective value of <paramref name="key"/> (the last one applied).</summary>
    public SettingOrigin OriginOf(string key) => EventsOf(key).LastOrDefault()?.Level ?? SettingOrigin.Default;

    /// <summary>Origin text: <c>default</c>, <c>system</c>, <c>personal</c>, <c>repository</c>, <c>environment</c> or <c>command line</c>.</summary>
    public string OriginText(string key) => OriginOf(key).Name();

    /// <summary>The values set for <paramref name="key"/>, lowest level first; the last one is the effective value's origin.</summary>
    public IReadOnlyList<OriginEvent> OriginChain(string key) => [.. EventsOf(key)];

    /// <summary>
    /// The system and personal files that set a key affecting findings that still shapes this run's
    /// value (it won, or a later level appended to it), with those keys. Empty in a gate run.
    /// </summary>
    public IReadOnlyList<(SettingsSource Level, IReadOnlyList<string> Keys)> MachineFindingKeys()
    {
        var byLevel = new Dictionary<SettingOrigin, SortedSet<string>>();
        foreach (var key in _events.Select(e => e.Key).Distinct(StringComparer.Ordinal))
        {
            if (SettingsRegistry.Governing(key) is not { AffectsFindings: true }) continue;
            var chain = _events.Where(e => e.Key == key).ToList();
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                if (chain[i].Level.IsMachine() && chain[i].Value is not null)
                {
                    if (!byLevel.TryGetValue(chain[i].Level, out var set)) byLevel[chain[i].Level] = set = new(StringComparer.Ordinal);
                    set.Add(key);
                }
                if (chain[i].Mode != "append") break;
            }
        }
        return [.. Levels.Where(l => l.Applied && byLevel.ContainsKey(l.Level)).Select(l => (l, (IReadOnlyList<string>)[.. byLevel[l.Level]]))];
    }

    /// <summary>The thresholds in effect (preset, fallback, overrides, rules switched off).</summary>
    public RuleSettings RuleSettings() =>
        Findings.RuleSettings.Resolve(Preset, ComplexMetric, Thresholds, LongMethodLines, DisabledRules, CheckRecordParameters);

    /// <param name="coverageLines">Compute the coverage line facts (only the run that reads the reports).</param>
    public AnalyzerOptions AnalyzerOptions(bool coverageLines = false) => new()
    {
        IncludeGenerated = IncludeGenerated, Parallelism = Parallelism, IncludeTests = IncludeTests, Include = Include, Exclude = Exclude,
        Clones = RuleOn(RuleIds.DuplicateCodeNear) ? Clones.CloneMode.TokensAndUnits
            : RuleOn(RuleIds.DuplicateCode) ? Clones.CloneMode.Tokens : Clones.CloneMode.None,
        CoverageLines = coverageLines, Variants = Variants,
    };

    /// <summary>
    /// The coverage reports of this run, or null without any: the <c>--coverage</c> files, else the settings
    /// file's globs expanded under its folder (a glob that matches nothing is a warning, never an error).
    /// </summary>
    public Coverage.CoverageInputs? CoverageInputs()
    {
        if (_coverageFiles.Count > 0) return new() { Reports = [.. _coverageFiles] };
        if (_coverageGlobs.Count == 0) return null;
        var (files, warnings) = Coverage.CoverageGlobs.Expand(CoverageGlobBase ?? System.IO.Directory.GetCurrentDirectory(), _coverageGlobs);
        return new() { Reports = files, Warnings = warnings };
    }

    public HistoryOptions HistoryOptions() => new()
    {
        Since = Since, WindowMonths = WindowMonths, AllHistory = AllHistory, IgnoreWhitespace = IgnoreWhitespace,
        BulkFlag = BulkFlag, BulkDrop = BulkDrop, IncludeBots = IncludeBots, ExtraBotPatterns = BotPatterns,
    };

    public SkarpSharp.Semantic.SemanticOptions SemanticOptions() => new()
    {
        Enabled = Semantic && Command is "analyze" or "baseline", Restore = Restore, Solution = Solution, Generators = Generators,
        Targets = Targets.Count == 1 && Targets[0] == "highest" ? [] : Targets,
    };

    /// <summary>The baseline file of this run: <c>--baseline</c>, else <c>.skarp-baseline.json</c> in the analyzed directory.</summary>
    public string BaselineFile() => BaselinePath ?? Path.Combine(Path.GetFullPath(Directory), Baselines.Baseline.FileName);

    /// <summary>Every setting the settings file can hold, with its effective value and origin, in file order.</summary>
    public IReadOnlyList<EffectiveSetting> Settings()
    {
        static string B(bool b) => b ? "true" : "false";
        static string L(IReadOnlyList<string> l) => l.Count == 0 ? "(none)" : string.Join(", ", l);
        var list = new List<EffectiveSetting>();
        void Add(string key, string value)
        {
            var chain = OriginChain(key);
            var origin = chain.Count > 0 ? chain[^1] : null;
            list.Add(new EffectiveSetting(key, value, OriginText(key), origin?.Level ?? SettingOrigin.Default, origin?.Path, chain.Count > 1 ? [.. chain.Take(chain.Count - 1)] : []));
        }
        Add("preset", Preset);
        Add("complexMetric", ComplexMetric ?? "cognitive");
        Add("longMethodLines", LongMethodLines);
        Add("recordParameters", CheckRecordParameters ? "check" : "exempt");
        Add("magicNumbers", ListMagicNumbers ? "list" : "count");
        foreach (var rule in RuleCatalog.Every)
            Add($"rules.{rule.Id}", RuleOn(rule.Id) ? "on" : "off");
        // The composite rules are listed once set (file or flag), so the default listing stays as it was.
        foreach (var rule in RuleCatalog.Composite.Where(r => _rules.ContainsKey(r.Id)))
            Add($"rules.{rule.Id}", RuleOn(rule.Id) ? "on" : "off");
        Add("includeTests", B(IncludeTests));
        Add("include", L(Include));
        Add("exclude", L(Exclude));
        Add("top", Top.ToString(CultureInfo.InvariantCulture));
        Add("history.enabled", B(History));
        Add("history.window", WindowMonths.ToString(CultureInfo.InvariantCulture));
        Add("history.since", Since?.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) ?? "(none)");
        Add("history.allHistory", B(AllHistory));
        Add("history.ignoreWhitespace", B(IgnoreWhitespace));
        Add("history.bulkFlag", BulkFlag.ToString(CultureInfo.InvariantCulture));
        Add("history.bulkDrop", BulkDrop?.ToString(CultureInfo.InvariantCulture) ?? "(none)");
        Add("history.includeBots", B(IncludeBots));
        Add("history.botPatterns", L(BotPatterns));
        Add("report.perFile", PerFile.ToString(CultureInfo.InvariantCulture));
        Add("gate.enabled", B(Gate));
        Add("gate.on", GateOn.Count == 0 ? Findings.Gate.DefaultPair : string.Join(", ", GateOn));
        static string Limit(double v) => v == 0 ? "0 (off)" : Health.HealthLimit.Number(v);
        Add("gate.health.maxDrop", Limit(HealthLimits.MaxDrop));
        Add("gate.health.newFileMin", Limit(HealthLimits.NewFileMin));
        Add("gate.health.floor", Limit(HealthLimits.Floor));
        Add("semantic.enabled", B(Semantic));
        Add("semantic.solution", Solution ?? "(automatic)");
        Add("semantic.generators", B(Generators));
        Add("semantic.targets", Targets.Count == 0 ? "highest" : string.Join(", ", Targets));
        Add("unusedCode.allow", L(UnusedAllow));
        Add("namespaceCycles.includeGeneratedTargets", B(IncludeGeneratedTargets));
        Add("saveLastRun", B(SaveLastRun));
        Add("output.checkMaxLines", CheckMaxLines.ToString(CultureInfo.InvariantCulture));
        Add("output.gateMaxLines", GateMaxLines.ToString(CultureInfo.InvariantCulture));
        Add("tracking.retentionDays", TrackingRetentionDays.ToString(CultureInfo.InvariantCulture));
        Add("tracking.lookbackDays", TrackingLookbackDays.ToString(CultureInfo.InvariantCulture));
        Add("score.model", Score.Model);
        Add("score.decay", ScoreCommandLine.Number(Score.Decay));
        Add("score.weights", ScoreCommandLine.WeightsText(Score));
        Add("score.worst.count", Score.WorstCount.ToString(CultureInfo.InvariantCulture));
        Add("score.worst.below", ScoreCommandLine.Number(Score.WorstBelow));
        // Listed once set, so the default listing stays as it was.
        if (_coverageGlobs.Count > 0 || _coverageFiles.Count > 0)
            Add("coverage", _coverageFiles.Count > 0 ? L(_coverageFiles) : L(_coverageGlobs));
        // The preprocessor symbol variants.
        Add("symbols", Variants.Symbols.Count == 0 ? "(automatic)" : string.Join(", ", Variants.Symbols));
        Add("variants.cap", Variants.Cap.ToString(CultureInfo.InvariantCulture));
        Add("variants.checkCap", Variants.CheckCap.ToString(CultureInfo.InvariantCulture));
        return list;
    }
}
