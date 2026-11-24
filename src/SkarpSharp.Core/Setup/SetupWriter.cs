using System.Globalization;
using System.Text.Json.Nodes;
using SkarpSharp.Configuration;
using SkarpSharp.Findings;

namespace SkarpSharp.Setup;

/// <summary>
/// The one writer of <c>skarp setup</c>, shared by the menu and
/// the flag form. Edits parse by the registry's kind; the candidate is validated by the loader and a full resolve
/// before anything is written; the save keeps a commented original as <c>.bak</c> and writes atomically.
/// </summary>
public static class SetupWriter
{
    public const string FindingsWarning = "changes findings: the next run's settings fingerprint will differ from the baseline (re-record the baseline when you are done)";

    /// <summary>Sets <paramref name="key"/> from its text form (the <c>--set</c> grammar); <c>null</c> writes JSON null.</summary>
    /// <exception cref="SetupException">An unknown key, a key not allowed at this level, or an invalid value.</exception>
    public static void Set(LevelModel m, string key, string text) => SettingsDocument.Set(m.Document.Current, CheckKey(m, key), Parse(key, text.Trim()));

    /// <summary>Removes <paramref name="key"/> at this level (inherit); a list takes its mode key with it.</summary>
    public static void Unset(LevelModel m, string key)
    {
        CheckKey(m, key, unset: true);
        SettingsDocument.Remove(m.Document.Current, key);
        if (SettingsRegistry.Find(key)?.ModeKey is { } mode) SettingsDocument.Remove(m.Document.Current, mode);
    }

    /// <summary>Empties the level to <c>{ "version": 1 }</c>.</summary>
    public static void Reset(LevelModel m) => m.Document.Current = SettingsDocument.Empty();

    static string CheckKey(LevelModel m, string key, bool unset = false)
    {
        if (key.Length == 0 || key.Split('.').Any(p => p.Length == 0)) throw new SetupException($"'{key}' is not a settings key");
        if (key == "version") throw new SetupException("version: always 1; it cannot be changed");
        // Overrides are kept as they are; they are edited in the file itself.
        if (key == "overrides" || key.StartsWith("overrides.", StringComparison.Ordinal) || key.StartsWith("overrides[", StringComparison.Ordinal))
            throw new SetupException(m.IsCommandLine ? "overrides: describes a file; edit the repository's .skarp.json (skarp config lists the entries)"
                : $"overrides: skarp setup does not edit them; edit {m.Path} (skarp config lists the entries)");
        if (SettingsRegistry.Row(key) is null && SettingsRegistry.Governing(key) is not { Kind: SettingKind.Object })
            throw new SetupException($"{key}: unknown key (skarp setup --help lists the forms; skarp config lists the keys)");
        // The command-line level takes the keys of its command (the --setting rules).
        if (m.IsCommandLine)
        {
            if (!unset && CommandLineSettings.KeyError(key, SettingsRegistry.Command(m.Command ?? ""), m.Command ?? "", m.Directory) is { } no)
                throw new SetupException($"{key}: {no}");
            return key;
        }
        if (!unset && SettingsLevels.NotAllowed(key, m.Target) is { } why) throw new SetupException($"{key}: {why}");
        return key;
    }

    /// <summary>A value in the <c>--set</c> grammar, parsed by the key's kind (<see cref="SettingText.Parse"/>,
    /// shared with the <c>--setting</c> flag).</summary>
    /// <exception cref="SetupException">The value does not fit the kind or the range.</exception>
    public static JsonNode? Parse(string key, string text)
    {
        try { return SettingText.Parse(key, text); }
        catch (SettingTextException ex) { throw new SetupException(ex.Message == key + ": empty value" ? $"{key}: empty value (--unset {key} removes it)" : ex.Message); }
    }

    // ---- preview ----------------------------------------------------------------------------------------------------

    /// <summary>The preview: a line naming the file, one line per changed key (<c>key: old → new</c>), then the
    /// warnings (no effect because a level above sets the key, comments removed, findings changed).</summary>
    /// <param name="shortPaths">The menu's form: paths as <see cref="LevelModel.ShownPath"/>.</param>
    public static IReadOnlyList<string> Preview(LevelModel m, bool shortPaths = false)
    {
        var path = shortPaths ? m.ShownPath : m.Path;
        var changed = m.Changed();
        var lines = new List<string>();
        if (changed.Count == 0) { lines.Add($"Nothing to change in {path}."); return lines; }
        lines.Add((m.Document.Existed ? $"{path} ({m.Target.Name()}): " : $"creates {path} ({m.Target.Name()}): ")
            + (changed.Count == 1 ? "1 change" : $"{changed.Count} changes"));
        var before = LevelModel.Thresholds(m.EverythingWith(m.Document.Original));
        var beforeStack = LevelModel.Thresholds(m.StackWith(m.Document.Original));
        var after = LevelModel.Thresholds(m.Everything());
        foreach (var key in changed)
        {
            var old = SettingsDocument.Get(m.Document.Original, key, out bool hadOld);
            var now = SettingsDocument.Get(m.Document.Current, key, out bool hasNew);
            string oldText = hadOld ? Shown(key, old, before, false) : Inherited(m, key, beforeStack);
            string newText = hasNew ? Shown(key, now, after, true) : "(removed)";
            var above = hasNew && m.AboveValue(key) is { } a ? $" (no effect here: {a.Level.Name()} sets {(a.Value is null ? "null" : SettingsDocument.Text(a.Value))})" : "";
            lines.Add($"  {key}: {oldText} → {newText}{above}");
        }
        if (m.Document.Existed && m.Document.Comments > 0)
            lines.Add($"saving removes {m.Document.Comments} comment{(m.Document.Comments == 1 ? "" : "s")}; the original is kept as {path}.bak");
        if (changed.Any(k => (SettingsRegistry.Row(k) ?? SettingsRegistry.Governing(k))?.AffectsFindings == true)) lines.Add(FindingsWarning);
        return lines;
    }

    /// <summary>The words of a value in the preview: thresholds in the "N or more" form with the stored number.</summary>
    static string Shown(string key, JsonNode? value, ThresholdContext ctx, bool writes)
    {
        if (value is null) return "(Skarp default)";
        if (SettingsRegistry.Row(key) is { Rule: { } rule, Part: "notice" or "warning" } row && row.Kind == SettingKind.Number && key == row.Key
            && ThresholdLabels.Basis(rule, row.Part == "warning" ? Severity.Warning : Severity.Notice, ctx) is { AnyOf.Count: > 0 } basis
            && ThresholdLabels.Meaning(value, basis) is { } meaning)
        {
            var label = meaning.Count == 1 && meaning[0].OrMore ? $"{SettingsDocument.N(meaning[0].Value)} or more" : ThresholdLabels.Label(meaning, basis.AllOf);
            return label + (writes ? ThresholdLabels.WritesNote(meaning, value) : "");
        }
        return SettingsDocument.Text(value);
    }

    /// <summary>What a key read before it was set here: <c>(preset: 16 or more)</c> for thresholds, else <c>(inherit)</c>.</summary>
    static string Inherited(LevelModel m, string key, ThresholdContext before)
    {
        if (SettingsRegistry.Row(key) is { Rule: { } rule, Part: "notice" or "warning" } row && key == row.Key)
        {
            var level = before.Active.Rules[rule].Level(row.Part == "warning" ? Severity.Warning : Severity.Notice);
            var from = m.InheritedValue(key) is { } i ? i.Level.Name() : "preset";
            return $"({from}: {ThresholdLabels.ShortLabel(level)})";
        }
        return "(inherit)";
    }

    // ---- validate and save ------------------------------------------------------------------------------------------

    /// <summary>
    /// Checks the candidate <paramref name="text"/> before any write: the loader on this file alone, then a full resolve
    /// with the candidate in place of the target file, as <c>analyze</c> would read it. Returns the error, or null.
    /// </summary>
    public static string? Validate(LevelModel m, string text)
    {
        var locations = m.Locations.WithOverlay(m.Path, text);
        try { SettingsLevels.Check(locations, m.Target, m.Path, m.Directory); }
        catch (LevelException ex) { return ex.Message; }
        var config = m.Target.IsMachine() ? m.ConfigFlag : m.Path;
        List<string> args = ["config", m.Directory, "--no-gate"];
        if (config is not null) args.AddRange(["--config", config]);
        var parsed = CommandLine.Parse(args, locations);
        return parsed.Options is null ? parsed.Error ?? "the settings do not resolve" : null;
    }

    /// <summary>Writes the file: its folder created for the machine levels, a commented original copied to
    /// <c>.bak</c> first, then a temporary file moved over the target.</summary>
    /// <exception cref="SetupException">The file cannot be written (the message names the path).</exception>
    public static void Write(LevelModel m, string text)
    {
        try
        {
            JsonFile.Write(m.Path, text, backup: m.Document.Existed && m.Document.Comments > 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SetupException($"cannot write {m.Path}: {ex.Message}");
        }
    }
}
