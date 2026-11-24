using System.Globalization;
using SkarpSharp.Configuration;

namespace SkarpSharp.Install;

/// <summary>
/// The words of the hooks screen: one line per row, the scope list, and the pane per row. The panes show the
/// Skarp settings the hook reads, with the gate as the screen has it: a gated hook skips the system and
/// personal files, so its values come from the repository level or the defaults.
/// </summary>
public sealed class HooksText(HooksTarget target)
{
    const int NameWidth = 21, ScopeWidth = 8;

    /// <summary>The line of a row: a <c>*</c> when it differs from the entry, the name and the value.</summary>
    public string Line(HookRow row)
    {
        var (name, value, changed) = Cells(row);
        if (row == HookRow.Remove) return $"{(changed ? '*' : ' ')}Remove Skarp's hooks at this scope{(target.Remove ? ": yes" : "")}";
        return $"{(changed ? '*' : ' ')}{name.PadRight(NameWidth)} {value}";
    }

    (string Name, string Value, bool Changed) Cells(HookRow row)
    {
        var s = target.Spec;
        var l = target.Loaded;
        return row switch
        {
            HookRow.Scope => ("scope", $"{target.Scope.Name()}  {target.Status(target.Scope)}", false),
            HookRow.Program => ("program", s.Command, s.Command != l.Command),
            HookRow.PerEdit => ("per-edit check", "on (always)", false),
            HookRow.Stop => ("end-of-turn summary", OnOff(s.Stop), s.Stop != l.Stop),
            HookRow.Compact => ("after compaction", OnOff(s.Compact), s.Compact != l.Compact),
            HookRow.Gate => ("gate", OnOff(s.Gate), s.Gate != l.Gate),
            HookRow.Semantic => ("semantic check", OnOff(s.Semantic), s.Semantic != l.Semantic),
            HookRow.Notices => ("include notices", OnOff(s.Notice == true), s.Notice != l.Notice),
            HookRow.MaxLines => ("one line limit", s.MaxLines?.ToString(CultureInfo.InvariantCulture) ?? "off (advanced)", s.MaxLines != l.MaxLines),
            HookRow.Mcp => ("MCP server", $"{OnOff(target.Mcp)}  {(target.McpInstalledAt(target.Scope) ? "installed" : "not installed")}", target.Mcp != target.McpLoaded),
            HookRow.Kept => ("other options (kept)", string.Join(" ", target.Kept), false),
            _ => ("", "", target.Remove),
        };
    }

    static string OnOff(bool on) => on ? "on" : "off";

    public List<string> ScopeLines()
    {
        var lines = new List<string>();
        foreach (var s in ClaudeScopes.All) lines.Add($"{s.Name().PadRight(ScopeWidth)} {target.ShownPath(s)}  {target.Status(s)}");
        return lines;
    }

    public string Pane(HookRow row) => row switch
    {
        HookRow.Scope => ScopePane(target.Scope),
        HookRow.Program => "The program Claude Code runs (--command): skarp found on PATH, or a path such as ${CLAUDE_PROJECT_DIR}/.skarp/bin/skarp.exe. At project scope a path or .exe is refused unless --force, which the menu never adds.",
        HookRow.PerEdit => $"Always on: after each Edit or Write the hook checks the file and prints up to {Limit("output.checkMaxLines")}.{From()}",
        HookRow.Stop => $"At the end of each turn (Stop): a summary of the turn's files, up to {StopLimit()}, with the gate {Limit("output.gateMaxLines")}. Off writes --no-stop.{From()}",
        HookRow.Compact => "After Claude Code compacts the context (PostCompact): the hook only notes it, so its next message repeats the About line that says what Skarp's notes are. Prints nothing. Off writes --no-compact.",
        HookRow.Gate => GatePane(),
        HookRow.Semantic => "--semantic, end of turn only: also runs the semantic tier (a design-time build of the solution; slow). Turning it on turns the end-of-turn summary on.",
        HookRow.Notices => "--include-notices: also print notices (default: warnings only; notices are tracked either way). Off removes it from the entry.",
        HookRow.MaxLines => $"Advanced (--max-lines): one limit of finding lines for all three, over output.checkMaxLines ({Value("output.checkMaxLines")}), the end-of-turn {SkarpOptions.StopMaxLines} and output.gateMaxLines ({Value("output.gateMaxLines")}). Usually better: set those in skarp setup, Agent section.",
        HookRow.Mcp => "Skarp's MCP server (skarp mcp, experimental): Claude Code can ask it for the least healthy code and what a rule means. On: applying writes its entry (.mcp.json at project scope, ~/.claude.json otherwise; the same program). Off: applying leaves an existing entry as it is; skarp install claude --remove --mcp removes it.",
        HookRow.Kept => "Options in the entry that this screen does not set; applying keeps them as they are, after Skarp's own.",
        _ => "Removes Skarp's entries at this scope, and the groups and events they leave empty (skarp install claude --remove). Enter switches it; a applies, s prints.",
    };

    public string ScopePane(ClaudeScope scope)
    {
        var what = scope switch
        {
            ClaudeScope.Local => "Only for you, in this repository (git should ignore the file).",
            ClaudeScope.Project => "Committed: runs for everyone who clones the repository.",
            _ => "Every project on this machine.",
        };
        var statuses = new List<string>();
        foreach (var s in ClaudeScopes.All) statuses.Add($"{s.Name()} {target.Status(s)}");
        return $"{what} Applying here: {string.Join(", ", statuses)}. skarp install claude --check tests the program (not run from here).";
    }

    string GatePane()
    {
        var gateOn = Setting("gate.on") is { } g ? $"{g.Value}, {g.Level.Name()}" : "unknown";
        // And the health limits (gate.health.*), judged since the session started.
        var limits = target.Settings.HealthLimits() is { } h ? $" and files past a health limit ({h})" : "";
        return $"--gate: new or worse findings in gate.on ({gateOn}){limits} block the agent. A gated hook reads its settings from the repository level only (skarp setup repository).";
    }

    /// <summary>With the gate on, the note that the values come from the repository level.</summary>
    string From() => target.Spec.Gate ? " With the gate on, these limits come from the repository level or the defaults." : "";

    HookSetting? Setting(string key) => target.Settings.Find(target.Spec.Gate, key);

    string Value(string key) => Setting(key)?.Value ?? "?";

    /// <summary>The end-of-turn summary's limit without the gate: "15 finding lines", or the one line limit when it is set.</summary>
    string StopLimit() => target.Spec.MaxLines is { } n ? $"{n} finding lines (the one line limit)" : $"{SkarpOptions.StopMaxLines} finding lines";

    /// <summary>"5 finding lines (output.checkMaxLines, default)", or the one line limit when it is set.</summary>
    string Limit(string key)
    {
        if (target.Spec.MaxLines is { } n) return $"{n} finding lines (the one line limit)";
        return Setting(key) is { } s ? $"{s.Value} finding lines ({key}, {s.Level.Name()})" : $"? finding lines ({key}: {target.Settings.Error(target.Spec.Gate) ?? "unknown"})";
    }
}
