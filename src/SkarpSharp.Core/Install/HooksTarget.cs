using System.Globalization;
using SkarpSharp.Configuration;
using SkarpSharp.Setup;

namespace SkarpSharp.Install;

/// <summary>One scope's settings file as the hooks screen sees it: Skarp's entry read back, or why it cannot be read.</summary>
public sealed class HookScope
{
    public required ClaudeScope Scope { get; init; }
    public required ClaudeFile File { get; init; }
    /// <summary>Skarp's entry, or null (none, or a file Skarp cannot read).</summary>
    public HookEntry? Entry { get; init; }
    /// <summary>Why Skarp cannot edit the file (not strict JSON, a wrong <c>hooks</c> shape), or null.</summary>
    public string? Error { get; init; }

    public static HookScope Read(ClaudeScope scope, string path)
    {
        var file = ClaudeFile.Read(path);
        if (file.Error is not null) return new HookScope { Scope = scope, File = file, Error = file.Error };
        try
        {
            ClaudeHooks.Install(file.Root, new HookSpec()); // the shape check
            return new HookScope { Scope = scope, File = file, Entry = HookEntry.Read(file.Root) };
        }
        catch (HookShapeException ex) { return new HookScope { Scope = scope, File = file, Error = ex.Message }; }
    }

    /// <summary>"not installed", "installed" (installing <paramref name="spec"/> here changes nothing), "installed,
    /// differs", or why Skarp cannot read the file.</summary>
    public string Status(HookSpec spec)
    {
        if (Error is not null) return "cannot be read: " + Error;
        if (Entry is null) return "not installed";
        var (_, changes) = ClaudeHooks.Install(File.Root, spec);
        return changes.All(c => c.Kind == HookChangeKind.Unchanged) ? "installed" : "installed, differs";
    }
}

/// <summary>
/// The "Claude Code hooks" target of <c>skarp setup</c>, a front end to <c>skarp install claude</c>.
/// It reads Skarp's entry at each scope back into the screen's values, says per scope whether applying them would change
/// the file, and builds the <see cref="InstallCall"/> that the menu runs after it closes or prints. Nothing is written here.
/// </summary>
public sealed class HooksTarget
{
    static readonly HookSpec Defaults = new() { Notice = false };

    readonly string _cwd;
    /// <summary>By scope (<see cref="ClaudeScopes.All"/> order); an array, not a dictionary over the enum (binary size).</summary>
    readonly HookScope[] _scopes = new HookScope[ClaudeScopes.All.Length];

    public ClaudeScope Scope { get; private set; }
    /// <summary>The screen's values (<see cref="HookSpec.Notice"/> is true or false).</summary>
    public HookSpec Spec { get; private set; } = Defaults;
    /// <summary>The values read from the scope's entry (the defaults when there is none).</summary>
    public HookSpec Loaded { get; private set; } = Defaults;
    /// <summary>True: the screen removes Skarp's entries at this scope instead.</summary>
    public bool Remove { get; private set; }
    /// <summary>The Skarp settings the hook reads, shown read-only.</summary>
    public HookSettings Settings { get; }
    /// <summary>The screen writes the MCP server entry too (the "MCP server" row).</summary>
    public bool Mcp { get; private set; } = true;
    /// <summary>The MCP row's value read from the scope: on when Skarp's MCP entry is there, or when neither it nor
    /// a hook entry is (an install writes both by default).</summary>
    public bool McpLoaded { get; private set; } = true;
    /// <summary>Skarp's MCP entry per scope (false also for a file Skarp cannot read).</summary>
    readonly bool[] _mcpInstalled = new bool[ClaudeScopes.All.Length];

    HooksTarget(string cwd, InstallSeams seams, SettingsLocations locations)
    {
        _cwd = Path.GetFullPath(cwd);
        var projectDir = InstallArgs.ProjectDir(cwd);
        Settings = new HookSettings(projectDir, locations);
        foreach (var scope in ClaudeScopes.All) _scopes[(int)scope] = HookScope.Read(scope, seams.FileOf(scope, projectDir));
        foreach (var scope in ClaudeScopes.All) _mcpInstalled[(int)scope] = McpInstalled(seams, scope, projectDir);
    }

    /// <summary>Opens the target on the first scope with a Skarp entry (local, project, user), else local.</summary>
    public static HooksTarget Open(string cwd, InstallSeams seams, SettingsLocations locations)
    {
        var t = new HooksTarget(cwd, seams, locations);
        t.Load(Array.Find(t._scopes, s => s.Entry is not null)?.Scope ?? ClaudeScope.Local);
        return t;
    }

    public HookScope Of(ClaudeScope scope) => _scopes[(int)scope];

    /// <summary>The options of the scope's entry the installer does not write (kept on apply).</summary>
    public IReadOnlyList<string> Kept => _scopes[(int)Scope].Entry?.Kept ?? [];

    /// <summary>Chooses a scope and reloads the values from its entry.</summary>
    public void Load(ClaudeScope scope)
    {
        Scope = scope;
        Loaded = _scopes[(int)scope].Entry?.Spec ?? Defaults;
        Spec = Loaded;
        McpLoaded = _mcpInstalled[(int)scope] || _scopes[(int)scope].Entry is null;
        Mcp = McpLoaded;
        Remove = false;
    }

    /// <summary>Skarp's MCP entry at <paramref name="scope"/> is there.</summary>
    public bool McpInstalledAt(ClaudeScope scope) => _mcpInstalled[(int)scope];

    static bool McpInstalled(InstallSeams seams, ClaudeScope scope, string projectDir)
    {
        var file = ClaudeFile.Read(seams.McpFileOf(scope, projectDir));
        if (file.Error is not null) return false;
        try { return ClaudeMcp.Find(file.Root, scope, projectDir) is not null; }
        catch (HookShapeException) { return false; }
    }

    /// <summary>The MCP row.</summary>
    public void ToggleMcp() => Mcp = !Mcp;

    /// <summary>Applying the screen's values at <paramref name="scope"/>: see <see cref="HookScope.Status"/>.</summary>
    public string Status(ClaudeScope scope) => _scopes[(int)scope].Status(Spec);

    /// <summary>The screen differs from the scope's entry (or removes it).</summary>
    public bool Changed => Remove || Spec != Loaded || Mcp != McpLoaded;

    // ---- changes ----------------------------------------------------------------------------------------------------

    /// <summary>The program; empty means plain <c>skarp</c>.</summary>
    public void SetProgram(string program)
    {
        var p = program.Trim();
        if (p.Length == 0) p = "skarp";
        Spec = Spec with { Command = p, CommandGiven = p != "skarp" };
    }

    /// <summary>The one line limit; empty means off.</summary>
    /// <exception cref="SetupException">Not a whole number of at least 1.</exception>
    public void SetMaxLines(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) { Spec = Spec with { MaxLines = null }; return; }
        if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
            throw new SetupException("The line limit is a whole number of at least 1 (empty: off).");
        Spec = Spec with { MaxLines = n };
    }

    /// <summary>The end-of-turn summary; off also turns the semantic check off (it runs at the end of a turn).</summary>
    public void ToggleStop() => Spec = Spec.Stop ? Spec with { Stop = false, Semantic = false } : Spec with { Stop = true };

    /// <summary>The PostCompact entry (the About line comes back after Claude Code compacts the context).</summary>
    public void ToggleCompact() => Spec = Spec with { Compact = !Spec.Compact };

    public void ToggleGate() => Spec = Spec with { Gate = !Spec.Gate };

    /// <summary>The semantic check; on also turns the end-of-turn summary on.</summary>
    public void ToggleSemantic() => Spec = Spec.Semantic ? Spec with { Semantic = false } : Spec with { Semantic = true, Stop = true };

    public void ToggleNotices() => Spec = Spec with { Notice = Spec.Notice != true };

    public void ToggleRemove() => Remove = !Remove;

    /// <summary>Del: the row back to its default (the summary on, the rest off; scope, the per-edit check and the kept
    /// options have none).</summary>
    public void Reset(HookRow row) => Spec = row switch
    {
        HookRow.Program => Spec with { Command = Defaults.Command, CommandGiven = false },
        HookRow.Stop => Spec with { Stop = Defaults.Stop },
        HookRow.Compact => Spec with { Compact = Defaults.Compact },
        HookRow.Gate => Spec with { Gate = Defaults.Gate },
        HookRow.Semantic => Spec with { Semantic = Defaults.Semantic },
        HookRow.Notices => Spec with { Notice = Defaults.Notice },
        HookRow.MaxLines => Spec with { MaxLines = Defaults.MaxLines },
        _ => ResetRemove(row),
    };

    HookSpec ResetRemove(HookRow row)
    {
        if (row == HookRow.Remove) Remove = false;
        if (row == HookRow.Mcp) Mcp = true;
        return Spec;
    }

    // ---- the call ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// What the screen asks the installer for. Notices: <c>--include-notices</c> when on, <c>--no-include-notices</c> only
    /// when off and the entry has them (so the file gets exactly what the screen shows and the printed command stays short).
    /// </summary>
    public InstallCall Call()
    {
        if (Remove) return new InstallCall(Scope, true, new HookSpec());
        var notice = Spec.Notice == true ? true : Loaded.Notice == true ? false : (bool?)null;
        return new InstallCall(Scope, false, Spec with { Notice = notice }, Hooks: true, Mcp: Mcp);
    }

    /// <summary>The arguments of <see cref="Call"/> (after <c>skarp</c>), checked by parsing them back.</summary>
    /// <exception cref="SetupException">They do not parse back to the call (an internal error).</exception>
    public List<string> Args()
    {
        var call = Call();
        var args = call.Args();
        var (back, error) = InstallCall.Parse(args);
        if (back != call) throw new SetupException($"internal error: the install command does not parse back ({error ?? "another request"}); please report it");
        return args;
    }

    /// <summary>The printed command for the shell choice.</summary>
    public CommandBuilder Build() => CommandBuilder.FromArgs(Args(), _cwd);

    /// <summary>The scope's file, shortened as the menu shows paths.</summary>
    public string ShownPath(ClaudeScope scope) => LevelModel.Shorten(_scopes[(int)scope].File.Path, _cwd);
}
