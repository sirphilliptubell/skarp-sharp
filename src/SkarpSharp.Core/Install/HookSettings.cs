using SkarpSharp.Configuration;

namespace SkarpSharp.Install;

/// <summary>A setting the hook reads from Skarp's settings files: its value and the level it comes from.</summary>
public sealed record HookSetting(string Key, string Value, SettingOrigin Level);

/// <summary>
/// The Skarp settings the hooks screen shows read-only, the gate set and the two line limits,
/// as the hook resolves them for the repository: with the gate the system and personal files are skipped (as
/// <c>check --gate</c> does). Each resolution is made once.
/// </summary>
public sealed class HookSettings(string projectDir, SettingsLocations locations)
{
    public static readonly string[] Keys = ["gate.on", "gate.health.maxDrop", "gate.health.newFileMin", "gate.health.floor", "output.checkMaxLines", "output.gateMaxLines"];

    /// <summary>The health limits in force with the gate (<c>gate.health.*</c>), in words (<c>drop 1.0,
    /// new file 6.5, floor 6.5</c>; 0 reads off); null when the settings cannot be read.</summary>
    public string? HealthLimits()
    {
        if (Find(true, "gate.health.maxDrop") is not { } drop || Find(true, "gate.health.newFileMin") is not { } min || Find(true, "gate.health.floor") is not { } floor) return null;
        static string V(HookSetting s) => s.Value.Replace(" (off)", "", StringComparison.Ordinal) == "0" ? "off" : s.Value;
        return $"drop {V(drop)}, new file {V(min)}, floor {V(floor)}";
    }

    sealed class Resolved
    {
        public List<HookSetting>? List;
        public string? Error;
    }

    Resolved? _plain, _gated;

    /// <summary>The settings with or without the gate; null when they cannot be read (<see cref="Error"/> says why).</summary>
    public IReadOnlyList<HookSetting>? Get(bool gate) => Resolve(gate).List;

    public string? Error(bool gate) => Resolve(gate).Error;

    /// <summary>One setting, or null.</summary>
    public HookSetting? Find(bool gate, string key) => Resolve(gate).List?.Find(s => s.Key == key);

    Resolved Resolve(bool gate) => gate ? _gated ??= Read(true) : _plain ??= Read(false);

    Resolved Read(bool gate)
    {
        List<string> args = ["analyze", projectDir];
        if (gate) args.Add("--gate");
        var parsed = CommandLine.Parse(args, locations);
        var r = new Resolved { Error = parsed.Error };
        if (parsed.Options is not { } o) return r;
        r.List = [];
        foreach (var s in o.Settings())
            if (Keys.Contains(s.Key)) r.List.Add(new HookSetting(s.Key, s.Value, s.Level));
        return r;
    }
}
