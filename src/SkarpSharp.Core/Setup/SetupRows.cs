using System.Text.Json.Nodes;
using SkarpSharp.Configuration;
using SkarpSharp.Findings;

namespace SkarpSharp.Setup;

/// <summary>What choosing an option does: set a value (JSON <c>null</c> included), remove the key here, or open the entry line.</summary>
public enum OptionAction { Set, Remove, Custom }

/// <summary>One line of a picker.</summary>
/// <param name="Write">The value written for <see cref="OptionAction.Set"/> (null writes JSON <c>null</c>).</param>
/// <param name="Marker">For the current value: <c>(set here)</c>, <c>(from your preset)</c>, <c>(from personal)</c>.</param>
public sealed class SetupOption(string label, OptionAction action, JsonNode? write, string pane, string? marker = null)
{
    public string Label { get; } = label;
    public OptionAction Action { get; } = action;
    public JsonNode? Write { get; } = write;
    public string Pane { get; } = pane;
    public string? Marker { get; } = marker;

    /// <summary>This option with another label, pane or marker.</summary>
    public SetupOption With(string? label = null, string? pane = null, string? marker = null) =>
        new(label ?? Label, Action, Write, pane ?? Pane, marker ?? Marker);

    public bool IsCurrent => Marker is not null;
}

/// <summary>A row as the editor shows it: setting, value, origin and the override from above.</summary>
public sealed class RowView(SettingsKey row, string name, string value, string origin, string? above, bool setHere)
{
    public SettingsKey Row { get; } = row;
    public string Name { get; } = name;
    public string Value { get; } = value;
    public string Origin { get; } = origin;
    public string? Above { get; } = above;
    public bool SetHere { get; } = setHere;
}

/// <summary>
/// The rows of each section at a level and the options of each picker, from the registry,
/// the rule catalog and the presets.
/// </summary>
public static class SetupRows
{
    public const string Inherit = "inherit (remove here)";
    public const string SkarpDefault = "use Skarp default";
    public const string Custom = "custom…";

    /// <summary>The sections in menu order.</summary>
    public static readonly IReadOnlyList<SettingSection> Sections =
    [
        SettingSection.Rules, SettingSection.Thresholds, SettingSection.History, SettingSection.Score,
        SettingSection.Semantic, SettingSection.Agent, SettingSection.Lists, SettingSection.General,
    ];

    /// <summary>A section's name in the menu (no enum formatting, which costs size under Native AOT).</summary>
    public static string SectionName(SettingSection s) => s switch
    {
        SettingSection.Rules => "Rules", SettingSection.Thresholds => "Thresholds", SettingSection.History => "History",
        SettingSection.Score => "Score", SettingSection.Semantic => "Semantic", SettingSection.Agent => "Agent",
        SettingSection.Lists => "Lists", _ => "General",
    };

    /// <summary>The rows of a section at the model's level: keys the level may hold; <c>root</c> never; a list's mode
    /// only when this level sets the list.</summary>
    public static IReadOnlyList<SettingsKey> Rows(LevelModel m, SettingSection section)
    {
        if (m.IsCommandLine) return CommandRows(m, section);
        var scope = m.IsMachine ? LevelScope.Machine : LevelScope.Repository;
        var rows = new List<SettingsKey>();
        foreach (var r in SettingsRegistry.Editable)
        {
            if (r.Section != section || (r.Levels & scope) == 0 || r.Key == "root") continue;
            if (SettingsRegistry.ListOfMode(r.Key) is { } list && !SettingsDocument.Has(m.Document.Current, list.Key)) continue;
            if (r.Key == "preset") rows.Insert(0, r); // the Thresholds section starts with the preset
            else rows.Add(r);
        }
        // The repository file's overrides, read-only (edited in the file), when it holds some.
        if (section == SettingSection.Lists && !m.IsMachine && SettingsDocument.Has(m.Document.Current, "overrides"))
            rows.Add(SettingsRegistry.Find("overrides")!);
        return rows;
    }

    /// <summary>
    /// The rows of a section for <c>skarp setup command</c>: the keys its command uses, without the keys that
    /// describe a file and without <c>gate.enabled</c> (the gate is a run option); a list's mode only when the list is
    /// changed; <c>overrides</c> read-only when the repository file holds some.
    /// </summary>
    static IReadOnlyList<SettingsKey> CommandRows(LevelModel m, SettingSection section)
    {
        var command = SettingsRegistry.Command(m.Command ?? "");
        var rows = new List<SettingsKey>();
        foreach (var r in SettingsRegistry.Editable)
        {
            if (r.Section != section || SettingText.DescribesFile(r.Key) || r.Key == "gate.enabled" || (r.Commands & command) == 0) continue;
            if (SettingsRegistry.ListOfMode(r.Key) is { } list && !SettingsDocument.Has(m.Document.Current, list.Key)) continue;
            if (r.Key == "preset") rows.Insert(0, r);
            else rows.Add(r);
        }
        if (section == SettingSection.Lists && SettingsDocument.Has(m.Inherited, "overrides") && (SettingsRegistry.CommandsOf("overrides") & command) != 0)
            rows.Add(SettingsRegistry.Find("overrides")!);
        return rows;
    }

    /// <summary>The value of the read-only <c>overrides</c> row: <c>2 entries (edit the file)</c>.</summary>
    public static string OverridesText(LevelModel m)
    {
        int n = SettingsDocument.Get(m.IsCommandLine ? m.Inherited : m.Document.Current, "overrides", out _) is JsonArray a ? a.Count : 0;
        return $"{n} entr{(n == 1 ? "y" : "ies")} (edit the file)";
    }

    /// <summary>Keys set at this level now, per section (unknown keys count under General).</summary>
    public static int SetHere(LevelModel m, SettingSection section)
    {
        int n = 0;
        foreach (var k in SettingsDocument.Leaves(m.Document.Current))
            if (k != "version" && (SettingsRegistry.Row(k) ?? SettingsRegistry.Governing(k))?.Section == section) n++;
        return n;
    }

    public static string Name(SettingsKey row) =>
        row.Key.StartsWith("rules.", StringComparison.Ordinal) ? row.Key["rules.".Length..]
        : row.Key.StartsWith("thresholds.", StringComparison.Ordinal) ? row.Key["thresholds.".Length..]
        : row.Key;

    /// <summary>The origin of a key at the level: <c>set here</c>, <c>from personal</c> or <c>Skarp default</c>.</summary>
    static string Origin(LevelModel m, string key, out bool here)
    {
        var value = m.Here(key, out here);
        if (here) return value is null ? "set here (Skarp default)" : "set here";
        return m.InheritedValue(key) is { } inherited ? "from " + inherited.Level.Name() : "Skarp default";
    }

    public static RowView View(LevelModel m, SettingsKey row, ThresholdContext? stack = null)
    {
        var origin = Origin(m, row.Key, out bool here);
        string value;
        if (row.Rule is { } rule && row.Part is { } part)
        {
            stack ??= LevelModel.Thresholds(m.Stack());
            var active = stack.Active.Rules[rule];
            value = part is "notice" or "warning" ? ThresholdLabels.Label(active.Level(part == "warning" ? Severity.Warning : Severity.Notice))
                : active.Parameters.GetValueOrDefault(part) ?? "(none)";
            var from = part is "notice" or "warning" ? active.LevelPreset(part == "warning" ? Severity.Warning : Severity.Notice) : active.Preset;
            if (origin == "Skarp default" && from is not ("default" or "custom")) origin = "preset " + from;
        }
        else if (row.Rule is { } r)
        {
            var on = Effective(m, row.Key);
            value = on is JsonValue v && v.TryGetValue<bool>(out var b) ? (b ? "on" : "off") : (RuleCatalog.Get(r).DefaultOff ? "off" : "on");
        }
        else if (row.Merge == MergeKind.Overrides) value = OverridesText(m);
        else
        {
            var effective = Effective(m, row.Key);
            value = effective is null ? row.Default : SettingsDocument.Text(effective);
            // A health limit of 0 is off.
            if (row.Key.StartsWith("gate.health.", StringComparison.Ordinal) && value == "0") value = "off";
        }
        var above = m.AboveValue(row.Key) is { } a ? $"({a.Level.Name()} overrides: {(a.Value is null ? "Skarp default" : SettingsDocument.Text(a.Value))})" : null;
        return new RowView(row, Name(row), value, origin, above, here);
    }

    /// <summary>The value this level produces for a key (its own, else inherited); null for skarp's default.</summary>
    public static JsonNode? Effective(LevelModel m, string key)
    {
        var here = m.Here(key, out bool found);
        return found ? here : m.InheritedValue(key)?.Value;
    }

    static string? Marker(LevelModel m, string key, string fromPreset = "(from your preset)")
    {
        _ = m.Here(key, out bool here);
        return here ? "(set here)" : m.InheritedValue(key) is { } i ? $"(from {i.Level.Name()})" : fromPreset;
    }

    // ---- pickers ----------------------------------------------------------------------------------------------------

    /// <summary>The picker of a row; empty for rows edited without one (booleans, numbers, text, lists).</summary>
    public static IReadOnlyList<SetupOption> Options(LevelModel m, SettingsKey row)
    {
        if (row.Key == "preset") return PresetOptions(m);
        if (row.Rule is { } rule && row.Part is "notice" or "warning")
            return LevelOptions(m, row, rule, row.Part == "warning" ? Severity.Warning : Severity.Notice);
        if (row.Rule is { } r && row.Part is { } parameter) return ParameterOptions(m, row, r, parameter);
        if (row.Kind == SettingKind.Choice) return ChoiceOptions(m, row);
        return [];
    }

    static IReadOnlyList<SetupOption> Tail(SettingsKey row, bool custom, bool skarpDefault = true)
    {
        var list = new List<SetupOption> { new(Inherit, OptionAction.Remove, null, $"Removes {row.Key} from this level: the value comes from the levels below.") };
        if (skarpDefault) list.Add(new(SkarpDefault, OptionAction.Set, null, $"Writes null: {row.Key} takes Skarp's built-in value, whatever the levels below set."));
        if (custom) list.Add(new(Custom, OptionAction.Custom, null, $"Type a value{(row.RangeText is { } range ? $" ({range})" : "")}."));
        return list;
    }

    static IReadOnlyList<SetupOption> ChoiceOptions(LevelModel m, SettingsKey row)
    {
        var current = Effective(m, row.Key) is JsonValue v && v.TryGetValue<string>(out var s) ? s : row.Default;
        var marker = Marker(m, row.Key, "(Skarp default)");
        var list = new List<SetupOption>();
        foreach (var a in row.Allowed!) list.Add(new(a, OptionAction.Set, JsonValue.Create(a), row.Description, a == current ? marker : null));
        list.AddRange(Tail(row, false));
        return list;
    }

    static IReadOnlyList<SetupOption> PresetOptions(LevelModel m)
    {
        var row = SettingsRegistry.Find("preset")!;
        var ctx = LevelModel.Thresholds(m.Stack());
        var marker = Marker(m, "preset", "(Skarp default)");
        var list = new List<SetupOption>();
        foreach (var p in Presets.All)
            list.Add(new($"{p.Id}  {p.Name}", OptionAction.Set, JsonValue.Create(p.Id),
                $"{p.Description} Choosing it changes {ChangedRows(ctx, p.Id)} threshold rows.", p.Id == ctx.Preset ? marker : null));
        list.AddRange(Tail(row, false));
        return list;
    }

    /// <summary>How many threshold rows (rule and level) read differently under another preset.</summary>
    public static int ChangedRows(ThresholdContext ctx, string preset)
    {
        var other = RuleSettings.Resolve(preset, ctx.ComplexMetric == "cognitive" ? null : ctx.ComplexMetric, [], ctx.LongMethodLines);
        int n = 0;
        foreach (var (id, rule) in ctx.Base.Rules)
            foreach (var sev in new[] { Severity.Notice, Severity.Warning })
                if (ThresholdLabels.Label(rule.Level(sev)) != ThresholdLabels.Label(other.Rules[id].Level(sev))) n++;
        return n;
    }

    static IReadOnlyList<SetupOption> ParameterOptions(LevelModel m, SettingsKey row, string rule, string name)
    {
        var ctx = LevelModel.Thresholds(m.Stack());
        var current = ctx.Active.Rules[rule].Parameters.GetValueOrDefault(name);
        var marker = Marker(m, row.Key);
        var values = new List<string>();
        var vendors = new List<string>();
        foreach (var p in Presets.All)
            if (p.Rules.TryGetValue(rule, out var t) && t.Parameter(name) is { } v)
            {
                int i = values.IndexOf(v);
                if (i >= 0) vendors[i] += ", " + ThresholdLabels.VendorName(p.Id);
                else { values.Add(v); vendors.Add(ThresholdLabels.VendorName(p.Id)); }
            }
        foreach (var a in row.Allowed ?? []) if (!values.Contains(a)) { values.Add(a); vendors.Add(""); }
        var list = new List<SetupOption>();
        for (int i = 0; i < values.Count; i++)
            list.Add(new(vendors[i].Length > 0 ? $"{values[i]}  ({vendors[i]})" : values[i], OptionAction.Set,
                ParameterValue(values[i]), row.Description, values[i] == current ? marker : null));
        list.AddRange(Tail(row, row.Kind != SettingKind.Choice));
        if (current is not null && !AnyCurrent(list)) list[^1] = list[^1].With(label: $"{list[^1].Label} (now {current})", marker: marker);
        return list;
    }

    /// <summary>A parameter value as written: a number when it is one, else text.</summary>
    public static JsonNode ParameterValue(string value) => SettingText.ParameterValue(value);

    static IReadOnlyList<SetupOption> LevelOptions(LevelModel m, SettingsKey row, string rule, Severity severity)
    {
        var all = LevelModel.Thresholds(m.Everything());
        var stack = LevelModel.Thresholds(m.Stack());
        var basis = ThresholdLabels.Basis(rule, severity, all);
        var marker = Marker(m, row.Key);
        var here = m.Here(row.Key, out bool setHere);
        var active = stack.Active.Rules[rule].Level(severity);
        var info = RuleCatalog.Get(rule);

        if (info.Metrics.Count == 0)
        {
            bool off = setHere ? here is JsonValue v && v.TryGetValue<string>(out var s) && s is "off" or "none" : active is null;
            return
            [
                new("on (Skarp default)", OptionAction.Set, null, $"{info.Title}: the {row.Part} level is on; it has no number.", off ? null : marker),
                new("off", OptionAction.Set, JsonValue.Create("off"), $"{info.Title}: no findings at the {row.Part} level.", off ? marker : null),
                .. Tail(row, false, skarpDefault: false),
            ];
        }

        var sets = ThresholdLabels.VendorSets(rule, severity, all);
        var offered = new List<VendorSet>();
        var other = new List<VendorSet>();
        foreach (var s in sets) (basis is not null && ThresholdLabels.Offered(s, basis) ? offered : other).Add(s);
        var notOffered = NotOffered(other, basis);
        var list = new List<SetupOption>();
        if (sets.Count == 0)
        {
            list.Add(new("off (Skarp default)", OptionAction.Set, null, $"No vendor publishes a value for the {row.Part} level of {rule}; Skarp leaves it off.",
                active is null ? marker : null));
            list.AddRange(Tail(row, true, skarpDefault: false));
        }
        else
        {
            var currentMeaning = setHere ? ThresholdLabels.Meaning(here, basis) : active is null ? null : ThresholdLabels.Normalized(active);
            bool isOff = setHere ? here is JsonValue v && v.TryGetValue<string>(out var s) && s is "off" or "none" : active is null;
            bool isNull = setHere && here is null;
            foreach (var set in offered)
            {
                var write = ThresholdLabels.Write(set.Conditions, basis!);
                bool partial = ThresholdLabels.Partial(set, basis!);
                var meaning = partial ? ThresholdLabels.Completed(set, basis!) : set.Conditions;
                bool current = !isOff && !isNull && currentMeaning is not null && ThresholdLabels.Same(currentMeaning, meaning);
                string keeps = partial ? ThresholdLabels.KeepsNote(set, basis!) : "";
                string partialPane = partial ? $" Only {ThresholdLabels.Label(set.Conditions, false)} is written{keeps}." : "";
                list.Add(new($"{ThresholdLabels.Label(set.Conditions, set.AllOf)}{keeps}  ({ThresholdLabels.Vendors(set)})",
                    OptionAction.Set, write, ThresholdLabels.Pane(set, row.Key) + partialPane + notOffered, current ? marker : null));
            }
            list.Add(new("off", OptionAction.Set, JsonValue.Create("off"), $"No {row.Part} findings for {rule}.", isOff ? marker : null));
            list.AddRange(Tail(row, true));
            if (isNull) list[^2] = list[^2].With(marker: "(set here)");
            if (!AnyCurrent(list) && currentMeaning is not null)
                list[^1] = list[^1].With(label: $"{Custom} (now {ThresholdLabels.Label(currentMeaning, basis?.AllOf ?? false)})", marker: marker);
        }
        list[^1] = list[^1].With(pane: CustomPane(rule, basis) + notOffered);
        return list;
    }

    static bool AnyCurrent(List<SetupOption> list)
    {
        foreach (var o in list) if (o.IsCurrent) return true;
        return false;
    }

    static string NotOffered(List<VendorSet> sets, LevelThreshold? basis)
    {
        if (sets.Count == 0 || basis is null) return "";
        var names = new List<string>();
        foreach (var c in basis.AnyOf) if (!names.Contains(ThresholdLabels.MetricName(c.Metric))) names.Add(ThresholdLabels.MetricName(c.Metric));
        var labels = new List<string>();
        foreach (var s in sets) labels.Add($"{ThresholdLabels.Label(s.Conditions, s.AllOf)} ({ThresholdLabels.Vendors(s)})");
        return " Not offered while this level measures " + string.Join(" and ", names) + ": " + string.Join("; ", labels) + ".";
    }

    static string CustomPane(string rule, LevelThreshold? basis) => basis is not { AnyOf.Count: > 0 } ? $"Type a number for {rule}."
        : basis.AnyOf.Count == 1 ? $"Type N for \"N or more {ThresholdLabels.MetricName(basis.AnyOf[0].Metric)}\"."
        : $"Type metric=N pairs for \"N or more\", comma-separated ({ThresholdLabels.MetricKeys(basis)}).";

    /// <summary>
    /// The custom entry of a threshold level in "N or more" terms (<c>10</c>, or <c>sourceLines=301, wmc=21</c>), as the
    /// value to write over the basis level; null with <paramref name="error"/> when it cannot be read or is out of range.
    /// </summary>
    public static JsonNode? CustomThreshold(LevelModel m, string rule, Severity severity, string text, out string? error)
    {
        error = null;
        var basis = ThresholdLabels.Basis(rule, severity, LevelModel.Thresholds(m.Everything()));
        if (basis is not { AnyOf.Count: > 0 }) { error = $"{rule} has no number at this level"; return null; }
        var wanted = new List<NormalCondition>();
        var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) { error = "Type a number, or Esc to go back."; return null; }
        foreach (var part in parts)
        {
            int eq = part.IndexOf('=');
            var metric = eq > 0 ? part[..eq].Trim() : basis.AnyOf[0].Metric;
            var number = eq > 0 ? part[(eq + 1)..].Trim() : part;
            if (basis.Find(metric) is null) { error = $"'{metric}' is not a metric of this level ({ThresholdLabels.MetricKeys(basis)})"; return null; }
            if (!double.TryParse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            { error = $"'{number}' is not a number"; return null; }
            var (min, max) = SettingsRegistry.MetricRange(metric);
            if (v < min || v > max) { error = $"{SettingsDocument.N(v)} is out of range: {metric} takes {SettingsDocument.N(min)} to {SettingsDocument.N(max)}"; return null; }
            wanted.Add(new(metric, v, true));
        }
        // Metrics left out keep their current value.
        var full = new List<NormalCondition>();
        foreach (var c in basis.AnyOf) full.Add(ThresholdLabels.FindMetric(wanted, c.Metric) ?? ThresholdLabels.Normalize(c));
        return ThresholdLabels.Write(full, basis);
    }
}
