using System.Globalization;

namespace SkarpSharp.Findings;

public enum Severity { Notice, Warning }

/// <summary>How a value is compared with a threshold, as the source states it.</summary>
public enum Comparison
{
    /// <summary>Strictly greater than the threshold ("over").</summary>
    Over,
    /// <summary>Fires at the threshold itself ("at", "or more").</summary>
    AtLeast,
}

/// <summary>One threshold on one metric.</summary>
public sealed record Condition(string Metric, Comparison Comparison, double Value)
{
    public bool Test(double value) => Comparison == Comparison.Over ? value > Value : value >= Value;

    public string Describe() => $"{Metric} {Word(Comparison)} {Num(Value)}";

    public static string Word(Comparison c) => c == Comparison.Over ? "over" : "at least";

    public static string Num(double v) => v == Math.Floor(v) && Math.Abs(v) < 1e15
        ? ((long)v).ToString(CultureInfo.InvariantCulture)
        : v.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// One severity level of a rule: fires when any condition holds. An empty condition list means a structural rule
/// (empty catch, magic number) whose level is decided by the rule itself. <see cref="Source"/> is shown to users (short:
/// "CodeScene: 70 lines"); <see cref="Evidence"/> is the maintainers' evidence grade and is never shown.
/// </summary>
public sealed record LevelThreshold(IReadOnlyList<Condition> AnyOf, string Source, string Evidence, bool AllOf = false)
{
    /// <summary>The conditions joined with "or", or with "and" for a minimum size whose conditions must all hold
    /// (<see cref="AllOf"/>: duplicate-code's tokens and lines).</summary>
    public string Describe() => AnyOf.Count == 0 ? "structural" : string.Join(AllOf ? " and " : " or ", AnyOf.Select(c => c.Describe()));

    public Condition? Find(string metric) => AnyOf.FirstOrDefault(c => c.Metric == metric);
}

/// <summary>A preset's values for one rule. Blank levels are null (no invented second tier).</summary>
public sealed record RuleThresholds(LevelThreshold? Notice, LevelThreshold? Warning, IReadOnlyDictionary<string, string>? Parameters = null)
{
    public string? Parameter(string name) => Parameters is not null && Parameters.TryGetValue(name, out var v) ? v : null;
}

/// <summary>A named threshold preset credited to its source.</summary>
public sealed class Preset
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    /// <summary>Values per rule key: a rule id, or <c>complex-method/cognitive</c> and <c>complex-method/cyclomatic</c>.</summary>
    public required IReadOnlyDictionary<string, RuleThresholds> Rules { get; init; }
}

/// <summary>The thresholds in effect for one rule after preset choice, fallback and overrides.</summary>
public sealed class ActiveRule
{
    public required string Rule { get; init; }
    /// <summary>Preset whose values are used; <c>default</c> when the chosen preset is blank for this rule.</summary>
    public required string Preset { get; init; }
    public LevelThreshold? Notice { get; set; }
    public LevelThreshold? Warning { get; set; }
    public Dictionary<string, string> Parameters { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Preset id per level after overrides (<c>custom</c> for overridden levels).</summary>
    public string NoticePreset { get; set; } = "";
    public string WarningPreset { get; set; } = "";
    /// <summary>False when the rule is switched off (settings file <c>rules</c>, <c>--rule id=off</c>): both levels are blank.</summary>
    public bool Enabled { get; set; } = true;

    public LevelThreshold? Level(Severity s) => s == Severity.Warning ? Warning : Notice;
    public string LevelPreset(Severity s) => s == Severity.Warning ? WarningPreset : NoticePreset;
}

/// <summary>Everything the rules need to know about thresholds: the active value set per rule.</summary>
public sealed class RuleSettings
{
    public required Preset Preset { get; init; }
    /// <summary><c>cognitive</c> or <c>cyclomatic</c>.</summary>
    public required string ComplexMetric { get; init; }
    public required IReadOnlyDictionary<string, ActiveRule> Rules { get; init; }
    public IReadOnlyList<string> Overrides { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
    /// <summary>Notes about the semantic rules (kept out of the syntax output, which they do not concern).</summary>
    public IReadOnlyList<string> SemanticNotes { get; init; } = [];

    public ActiveRule this[string rule] => Rules[rule];

    /// <summary>Unit of the long-method line thresholds: <c>physical</c> (default) or <c>source</c>.</summary>
    public string LongMethodLines { get; init; } = "physical";

    /// <summary>Positional record parameters are judged by too-many-parameters (default false: exempt).</summary>
    public bool CheckRecordParameters { get; init; }

    /// <summary>Placeholder metric of line-based long-method thresholds in the preset tables, resolved to the chosen unit.</summary>
    public const string LinesMetric = "lines";

    /// <summary>
    /// Resolves the thresholds: the chosen preset's values per rule, the composite default for rules the preset leaves
    /// blank, then the overrides (<c>rule.level=value</c>, <c>rule.level.metric=value</c>, <c>rule.level=off</c>,
    /// <c>rule.parameter=value</c>). complex-method reads cognitive complexity unless <paramref name="complexMetric"/> is
    /// <c>cyclomatic</c>; a preset without a value for the chosen metric uses the composite's.
    /// Line-based long-method thresholds are measured in <paramref name="longMethodLines"/> (<c>physical</c>, the unit
    /// of the Clean-CaDET calibration, or <c>source</c>).
    /// </summary>
    public static RuleSettings Resolve(string presetId, string? complexMetric, IReadOnlyList<string> overrides, string longMethodLines = "physical",
        IReadOnlyCollection<string>? disabledRules = null, bool checkRecordParameters = false) =>
        Resolve(presetId, complexMetric, overrides, longMethodLines, disabledRules, checkRecordParameters, null);

    /// <summary>
    /// The settings of one combination of per-path overrides. Only the rules in
    /// <paramref name="touched"/> are resolved (from the preset, <paramref name="overrides"/> and
    /// <paramref name="disabledRules"/>); every other rule shares <paramref name="shared"/>'s <see cref="ActiveRule"/>,
    /// which is what it would resolve to, so many combinations stay small in memory. The notes and the override list are
    /// the shared settings' (the run header is the repository's).
    /// </summary>
    public static RuleSettings ResolveSharing(RuleSettings shared, IReadOnlySet<string> touched, IReadOnlyList<string> overrides,
        IReadOnlyCollection<string>? disabledRules) =>
        Resolve(shared.Preset.Id, shared.ComplexMetric, overrides, shared.LongMethodLines, disabledRules, shared.CheckRecordParameters, (shared, touched));

    /// <param name="sharing">The settings whose rule objects are shared, and the rules resolved anew.</param>
    static RuleSettings Resolve(string presetId, string? complexMetric, IReadOnlyList<string> overrides, string longMethodLines,
        IReadOnlyCollection<string>? disabledRules, bool checkRecordParameters, (RuleSettings Shared, IReadOnlySet<string> Touched)? sharing)
    {
        var (shared, touched) = sharing is { } x ? (x.Shared, x.Touched) : (null, null);
        var preset = Presets.Find(presetId) ?? throw new ArgumentException($"unknown preset '{presetId}' (known: {string.Join(", ", Presets.All.Select(p => p.Id))})");
        var composite = Presets.Default;
        var notes = new List<string>();
        if (complexMetric is not null and not ("cognitive" or "cyclomatic"))
            throw new ArgumentException($"unknown complex-method metric '{complexMetric}' (cognitive or cyclomatic)");
        if (longMethodLines is not ("physical" or "source"))
            throw new ArgumentException($"unknown long-method line unit '{longMethodLines}' (physical or source)");
        string metric = complexMetric ?? "cognitive";
        string lineMetric = longMethodLines == "source" ? "sourceLines" : "physicalLines";

        var rules = new Dictionary<string, ActiveRule>(StringComparer.Ordinal);
        var semanticNotes = new List<string>();
        bool Shares(string id) => shared is not null && !touched!.Contains(id);
        foreach (var rule in RuleCatalog.Every.Concat(RuleCatalog.Composite).Concat(RuleCatalog.Coverage))
        {
            if (Shares(rule.Id)) { rules[rule.Id] = shared![rule.Id]; continue; }
            var key = rule.Id == RuleIds.ComplexMethod ? $"{rule.Id}/{metric}" : rule.Id;
            bool own = preset.Rules.TryGetValue(key, out var t);
            if (!own) t = composite.Rules[key];
            var active = new ActiveRule
            {
                Rule = rule.Id, Preset = own ? preset.Id : composite.Id, Notice = t!.Notice, Warning = t.Warning,
                NoticePreset = own ? preset.Id : composite.Id, WarningPreset = own ? preset.Id : composite.Id,
            };
            if (rule.Id == RuleIds.LongMethod)
            {
                active.Notice = WithLineUnit(active.Notice, lineMetric);
                active.Warning = WithLineUnit(active.Warning, lineMetric);
            }
            if (t.Parameters is not null) foreach (var (k, v) in t.Parameters) active.Parameters[k] = v;
            if (!own && preset.Id != composite.Id)
                (rule.Tier == "semantic" ? semanticNotes : notes).Add(rule.Id == RuleIds.ComplexMethod
                    ? $"{rule.Id}: preset {preset.Id} has no {metric} threshold, the default (composite) {metric} threshold is used"
                      + (metric == "cognitive" && preset.Rules.ContainsKey($"{rule.Id}/cyclomatic") ? " (--complex-metric cyclomatic reads the preset's cyclomatic value)" : "")
                    : $"{rule.Id}: preset {preset.Id} has no value, the default (composite) is used");
            rules[rule.Id] = active;
        }
        foreach (var o in overrides)
            if (!Shares(RuleOf(o))) ApplyOverride(rules, o, lineMetric);
        foreach (var id in disabledRules ?? [])
        {
            if (Shares(id)) continue;
            if (!rules.TryGetValue(id, out var off)) throw new ArgumentException($"unknown rule '{id}' (known: {string.Join(", ", rules.Keys)})");
            off.Enabled = false;
            off.Notice = null;
            off.Warning = null;
        }
        if (shared is not null)
            return new RuleSettings
            {
                Preset = preset, ComplexMetric = metric, LongMethodLines = longMethodLines, Rules = rules, Overrides = shared.Overrides, Notes = shared.Notes,
                CheckRecordParameters = checkRecordParameters, SemanticNotes = shared.SemanticNotes,
            };
        return new RuleSettings
        {
            Preset = preset, ComplexMetric = metric, LongMethodLines = longMethodLines, Rules = rules, Overrides = [.. overrides], Notes = notes,
            CheckRecordParameters = checkRecordParameters, SemanticNotes = semanticNotes,
        };
    }

    static readonly Lazy<RuleSettings> DefaultSettings = new(() => Resolve("default", null, []));

    /// <summary>
    /// Checks one override in <c>--threshold</c> grammar against the default preset, as <c>Resolve("default", null,
    /// [text])</c> would, resolving only its rule (settings files with many thresholds or override entries
    /// are checked without a full resolve per value).
    /// </summary>
    /// <exception cref="ArgumentException">The override is invalid.</exception>
    public static void Check(string text)
    {
        var rule = RuleOf(text);
        _ = ResolveSharing(DefaultSettings.Value, new HashSet<string>(StringComparer.Ordinal) { rule }, [text], null);
    }

    /// <summary>The rule id of a <c>--threshold</c> text (<c>long-method.notice=60</c>: <c>long-method</c>).</summary>
    static string RuleOf(string text) => text.IndexOf('.') is int dot and > 0 ? text[..dot] : text;

    static LevelThreshold? WithLineUnit(LevelThreshold? level, string lineMetric) =>
        level is null || !level.AnyOf.Any(c => c.Metric == LinesMetric) ? level
            : level with { AnyOf = [.. level.AnyOf.Select(c => c.Metric == LinesMetric ? c with { Metric = lineMetric } : c)] };

    static void ApplyOverride(Dictionary<string, ActiveRule> rules, string text, string lineMetric)
    {
        int eq = text.IndexOf('=');
        if (eq <= 0) throw new ArgumentException($"threshold override '{text}': expected <rule>.<level>=<value>");
        var key = text[..eq].Trim();
        var value = text[(eq + 1)..].Trim();
        var parts = key.Split('.');
        if (parts.Length < 2 || !rules.TryGetValue(parts[0], out var rule))
            throw new ArgumentException($"threshold override '{text}': unknown rule '{parts[0]}' (known: {string.Join(", ", rules.Keys)})");
        if (parts[1] is "notice" or "warning" && RuleCatalog.IsComposite(rule.Rule))
            throw new ArgumentException($"threshold override '{text}': {rule.Rule} is a composite with no threshold of its own (its parts keep theirs); parameters: {string.Join(", ", RuleCatalog.Get(rule.Rule).ParameterNames)}");
        if (parts[1] is "notice" or "warning")
        {
            var sev = parts[1] == "warning" ? Severity.Warning : Severity.Notice;
            var level = rule.Level(sev);
            if (value is "off" or "none")
            {
                if (sev == Severity.Warning) rule.Warning = null; else rule.Notice = null;
            }
            else
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    throw new ArgumentException($"threshold override '{text}': '{value}' is not a number (or 'off')");
                string? metric = parts.Length >= 3 ? string.Join('.', parts.Skip(2)) : null;
                if (metric == LinesMetric && rule.Rule == RuleIds.LongMethod) metric = lineMetric;
                var conditions = level?.AnyOf.ToList() ?? [];
                // A blank level borrows the metric and comparison of the other level of the same rule.
                var template = level ?? rule.Level(sev == Severity.Warning ? Severity.Notice : Severity.Warning);
                if (conditions.Count == 0 && template is { AnyOf.Count: > 0 })
                    conditions = [.. template.AnyOf.Take(metric is null ? 1 : template.AnyOf.Count)];
                if (conditions.Count == 0 && template is not null)
                    throw new ArgumentException($"threshold override '{text}': rule {rule.Rule} is structural and has no numeric threshold");
                if (metric is null)
                {
                    if (conditions.Count == 0) throw new ArgumentException($"threshold override '{text}': rule {rule.Rule} has no threshold to change");
                    conditions = [conditions[0] with { Value = number }, .. conditions.Skip(1)];
                }
                else
                {
                    int i = conditions.FindIndex(c => c.Metric == metric);
                    if (i >= 0) conditions[i] = conditions[i] with { Value = number };
                    else
                    {
                        if (!RuleCatalog.Get(rule.Rule).Metrics.Contains(metric))
                            throw new ArgumentException($"threshold override '{text}': rule {rule.Rule} has no metric '{metric}' (metrics: {string.Join(", ", RuleCatalog.Get(rule.Rule).Metrics)})");
                        conditions.Add(new Condition(metric, Comparison.Over, number));
                    }
                }
                var updated = new LevelThreshold(conditions, "custom (threshold override)", "custom", (level ?? template)?.AllOf ?? false);
                if (sev == Severity.Warning) rule.Warning = updated; else rule.Notice = updated;
            }
            if (sev == Severity.Warning) rule.WarningPreset = "custom"; else rule.NoticePreset = "custom";
            return;
        }
        if (parts.Length == 2 && RuleCatalog.Get(rule.Rule).ParameterNames.Contains(parts[1]))
        {
            rule.Parameters[parts[1]] = value;
            RuleCatalog.ValidateParameter(rule.Rule, parts[1], value);
            return;
        }
        throw new ArgumentException($"threshold override '{text}': expected <rule>.notice, <rule>.warning or a rule parameter ({string.Join(", ", RuleCatalog.Get(rule.Rule).ParameterNames)})");
    }

    /// <summary>The JSON run-header part for these settings (syntax rules only).</summary>
    /// <param name="coverage">A coverage report was given, so the coverage rules are listed too.</param>
    public Model.FindingSettings ToInfo(bool coverage = false) => Info(this, RuleCatalog.WithCoverage(coverage), Notes);

    /// <summary>The semantic output's run-header part (the semantic rules and their notes).</summary>
    public Model.FindingSettings ToSemanticInfo() => Info(this, RuleCatalog.Semantic, SemanticNotes);

    static Model.FindingSettings Info(RuleSettings s, IReadOnlyList<RuleInfo> rules, IReadOnlyList<string> notes) => new()
    {
        Preset = s.Preset.Id,
        PresetName = s.Preset.Name,
        ComplexMetric = s.ComplexMetric,
        LongMethodLines = s.LongMethodLines,
        RecordParameters = s.CheckRecordParameters ? "check" : "exempt",
        Overrides = [.. s.Overrides.Where(o => rules.Any(r => o.StartsWith(r.Id + ".", StringComparison.Ordinal)))],
        Notes = [.. notes],
        Rules = [.. rules.Select(r => s[r.Id]).Select(a => new Model.RuleSettingInfo
        {
            Rule = a.Rule,
            Preset = a.Preset,
            Notice = a.Notice?.Describe(),
            Warning = a.Warning?.Describe(),
            NoticeSource = a.Notice is null ? null : $"{a.NoticePreset}: {a.Notice.Source}",
            WarningSource = a.Warning is null ? null : $"{a.WarningPreset}: {a.Warning.Source}",
            Parameters = a.Parameters.Count == 0 ? null : new Dictionary<string, string>(a.Parameters, StringComparer.Ordinal),
            Off = a.Enabled ? null : true,
        })],
    };
}
