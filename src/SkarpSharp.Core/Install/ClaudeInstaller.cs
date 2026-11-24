using System.Text.Json.Nodes;
using SkarpSharp.Setup;

namespace SkarpSharp.Install;

/// <summary>What one <c>skarp install claude</c> run writes, and how.</summary>
public sealed class InstallRequest
{
    public ClaudeScope Scope { get; init; } = ClaudeScope.Local;
    public required HookSpec Spec { get; init; }
    public bool Force { get; init; }
    public bool DryRun { get; init; }
    public bool Print { get; init; }
    public bool Yes { get; init; }
    /// <summary>The hook entries (<c>--hooks</c>, or neither option).</summary>
    public bool Hooks { get; init; } = true;
    /// <summary>The MCP server entry (<c>--mcp</c>, or neither option).</summary>
    public bool Mcp { get; init; } = true;
}

/// <summary>
/// <c>skarp install claude</c> and <c>--remove</c> on one scope's file. Reads it
/// (strict JSON only), merges or removes Skarp's entries, looks at the other two scopes, prints the preview and warnings,
/// asks at a terminal, then writes a <c>.bak</c> and the file atomically. Exit codes as <see cref="Commands.InstallCommand"/>.
/// </summary>
public sealed class ClaudeInstaller(InstallRequest request, TextWriter output, TextWriter error, InstallSeams seams, string projectDir)
{
    const int Refused = 2;
    readonly HookSpec _spec = request.Spec with { ViaCmd = seams.UsesCmdWrapper(request.Spec.Command, request.Spec.CommandGiven, request.Scope, projectDir) };
    readonly string _path = seams.FileOf(request.Scope, projectDir);
    string? Given => _spec.CommandGiven ? _spec.Command : null;
    string ScopeName => request.Scope.Name();

    // ---- install ----------------------------------------------------------------------------------------------------

    /// <summary>The hooks and the MCP entry, as the request asks (neither option: both), hooks first; the higher
    /// exit code.</summary>
    public int Install() => Both(InstallHooks, () => new McpInstaller(request, output, error, seams, projectDir).Install());

    /// <summary><c>--remove</c> of the hooks and the MCP entry, as the request asks.</summary>
    public int Remove() => Both(RemoveHooks, () => new McpInstaller(request, output, error, seams, projectDir).Remove());

    int Both(Func<int> hooks, Func<int> mcp)
    {
        int exit = request.Hooks ? hooks() : 0;
        if (!request.Mcp || exit == Refused) return exit;
        if (request.Hooks) output.WriteLine();
        return Math.Max(exit, mcp());
    }

    int InstallHooks()
    {
        if (PortabilityError(request) is { } why) { error.WriteLine(why); return Refused; }
        var file = ClaudeFile.Read(_path);
        if (file.Error is not null) return Refuse(file, file.Error);
        JsonObject result;
        List<HookChange> changes;
        try { (result, changes) = ClaudeHooks.Install(file.Root, _spec); }
        catch (HookShapeException ex) { return Refuse(file, ex.Message); }

        var preview = request.Print ? error : output;
        preview.WriteLine($"Skarp hooks, {ScopeName} scope: {_path}{(file.Existed ? "" : " (new file)")}");
        foreach (var c in changes) foreach (var line in ChangeLines(c)) preview.WriteLine(line);
        var conflicts = OtherScopes(preview);
        if (_spec.ViaCmd)
            preview.WriteLine($"note: {_spec.Command} is a .cmd shim, which exec form cannot start; the entry runs it through cmd.exe /c (a real skarp.exe on PATH, as the Skarp.any package installs, is started directly)");
        foreach (var w in Warnings()) preview.WriteLine("warning: " + w);
        if (conflicts.Count > 0 && !Conflicts(conflicts)) return Refused;
        if (!changes.Any(c => c.Kind is HookChangeKind.Add or HookChangeKind.Update or HookChangeKind.Remove))
        {
            preview.WriteLine("Nothing to change: Skarp's hooks are already installed.");
            if (request.Print) output.Write(file.Text);
            return 0;
        }
        var text = file.Render(result);
        if (request.Print) { output.Write(text); return 0; }
        if (!Save(text, file.Existed)) return Refused;
        if (!request.DryRun) output.WriteLine("Claude Code picks up hook changes without a restart; /hooks lists what it loaded. skarp install claude --check tests the program.");
        return 0;
    }

    /// <summary>At project scope, a <c>--command</c> teammates cannot run is refused unless <c>--force</c>.</summary>
    internal static string? PortabilityError(InstallRequest request)
    {
        var spec = request.Spec;
        if (request.Scope != ClaudeScope.Project || !spec.CommandGiven || request.Force || NotPortable(spec.Command) is not { } why) return null;
        return $"skarp install: --command {spec.Command} is not portable at project scope: {why}. The project files are committed, so " +
            "teammates' Claude Code would run it and fail to start it. Use plain skarp (each teammate installs " +
            "Skarp), install at local scope, or add --force to write it anyway; nothing written";
    }

    /// <summary>Why <paramref name="command"/> breaks on teammates' machines, or null.</summary>
    static string? NotPortable(string command)
    {
        bool drive = command.Length >= 2 && char.IsAsciiLetter(command[0]) && command[1] == ':';
        if (drive || command.StartsWith('/') || command.StartsWith('\\') || command.StartsWith('~')) return "an absolute path exists on this machine only";
        return command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "a .exe runs on Windows only" : null;
    }

    /// <summary>Notes for the other scopes (identical entries, unreadable files); returns the different Skarp entries.</summary>
    List<string> OtherScopes(TextWriter preview)
    {
        var want = _spec.Events.ToDictionary(ev => ev, ev => ClaudeHooks.Key(_spec.Handler(ev), projectDir));
        var conflicts = new List<string>();
        foreach (var o in Others())
        {
            var (scope, other, found) = (o.Scope, o.File, o.Found);
            if (other.Error is not null) { preview.WriteLine($"note: {scope.Name()} scope ({other.Path}) is {other.Error}; Claude Code skips that file"); continue; }
            var mine = found.Where(f => want.ContainsKey(f.Event)).ToList();
            var different = mine.Where(f => ClaudeHooks.Key(f.Handler, projectDir) != want[f.Event]).ToList();
            if (mine.Count > 0 && different.Count == 0) preview.WriteLine($"note: also installed at {scope.Name()} scope ({other.Path}); Claude Code runs identical hooks once");
            conflicts.AddRange(different.Select(f =>
                $"a different Skarp entry is installed at {scope.Name()} scope ({other.Path}): {f.Event} {ClaudeHooks.Describe(f.Event, f.Handler, f.Group)}; both would run"));
        }
        return conflicts;
    }

    /// <summary>Reports the conflicts; true when <c>--force</c> lets the install go on.</summary>
    bool Conflicts(List<string> conflicts)
    {
        foreach (var c in conflicts) error.WriteLine((request.Force ? "note: " : "skarp install: ") + c);
        if (request.Force) return true;
        error.WriteLine("Remove it first (skarp install claude --remove --scope <scope>), or add --force to install both; nothing written");
        return false;
    }

    /// <summary>The other scopes' files with their Skarp handlers (a file that is the target itself is skipped).</summary>
    List<OtherScope> Others()
    {
        var list = new List<OtherScope>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(_path) };
        foreach (var scope in ClaudeScopes.All)
        {
            var path = seams.FileOf(scope, projectDir);
            if (scope == request.Scope || !seen.Add(Path.GetFullPath(path))) continue;
            var file = ClaudeFile.Read(path);
            if (file.Existed) list.Add(new OtherScope { Scope = scope, File = file, Found = file.Error is null ? ClaudeHooks.Find(file.Root, Given) : [] });
        }
        return list;
    }

    sealed class OtherScope
    {
        public ClaudeScope Scope { get; init; }
        public required ClaudeFile File { get; init; }
        public required List<FoundHook> Found { get; init; }
    }

    /// <summary>The gitignore warning (local scope) and where the program resolves (not found, a .cmd or .bat shim).</summary>
    List<string> Warnings()
    {
        var list = new List<string>();
        bool inGit = Reporting.ReportLayout.RepositoryRoot(projectDir) is not null;
        if (request.Scope == ClaudeScope.Local && inGit && seams.Ignored(projectDir, _path) == false)
            list.Add($"{_path} is not ignored by git; add it to .gitignore so it isn't committed");
        var found = seams.Locate(_spec.Command, projectDir);
        if (found is null) list.Add(NotFound());
        else if (seams.IsWindows && !_spec.ViaCmd && InstallSeams.IsScriptShim(found))
            list.Add(!_spec.CommandGiven && request.Scope == ClaudeScope.Project
                ? $"{_spec.Command} resolves to {found}, a .cmd or .bat script: exec form needs a real .exe, so Claude Code cannot start it. The project file is shared with teammates on other systems, so Skarp writes plain {_spec.Command} here; install at local or user scope, where Skarp writes a cmd.exe /c entry that works with the shim, or put a real skarp.exe on PATH (the Skarp.any package)"
                : $"{_spec.Command} resolves to {found}, a .cmd or .bat script: exec form needs a real .exe, so Claude Code cannot start it; use --command with the .exe");
        return list;
    }

    string NotFound()
    {
        if (!_spec.CommandGiven)
            return "skarp is not on PATH: Claude Code will show 'hook failed' after each edit. Install it with `dotnet tool install -g Skarp`, or use --command <path>";
        var where = _spec.Command.Contains(ClaudeHooks.ProjectDirVariable, StringComparison.Ordinal) ? $" (with ${{CLAUDE_PROJECT_DIR}} = {projectDir})" : " on PATH";
        return $"{_spec.Command} not found{where}: Claude Code will show 'hook failed' after each edit";
    }

    /// <summary>A refused file: the reason, then the entries to add by hand.</summary>
    int Refuse(ClaudeFile file, string reason)
    {
        error.WriteLine($"skarp install: {file.Path}: {reason}; Skarp only edits strict JSON (no comments, no trailing commas), " +
            "and Claude Code itself skips a file that isn't. Nothing written.");
        error.WriteLine("Fix the file, or add these entries to its \"hooks\" by hand:");
        error.Write(JsonFile.Render(ClaudeHooks.Snippet(_spec)));
        return Refused;
    }

    // ---- remove -----------------------------------------------------------------------------------------------------

    int RemoveHooks()
    {
        var file = ClaudeFile.Read(_path);
        if (file.Error is not null)
        {
            error.WriteLine($"skarp install: {_path}: {file.Error}; Skarp does not edit it (Claude Code skips it too). Remove Skarp's entries by hand; nothing written");
            return Refused;
        }
        JsonObject result;
        List<HookChange> changes;
        try { (result, changes) = ClaudeHooks.Remove(file.Root, Given); }
        catch (HookShapeException ex) { error.WriteLine($"skarp install: {_path}: {ex.Message}; nothing written"); return Refused; }
        output.WriteLine($"Skarp hooks, {ScopeName} scope: {_path}{(file.Existed ? "" : " (no file)")}");
        foreach (var c in changes) foreach (var line in ChangeLines(c)) output.WriteLine(line);
        foreach (var o in Others().Where(o => o.Found.Count > 0))
            output.WriteLine($"note: Skarp entries left at {o.Scope.Name()} scope ({o.File.Path}): {string.Join(", ", o.Found.Select(f => f.Event).Distinct())} (skarp install claude --remove --scope {o.Scope.Name()})");
        if (changes.Count == 0) { output.WriteLine("Nothing to remove: no Skarp hooks in this file."); return 0; }
        return Save(file.Render(result), backup: true) ? 0 : Refused;
    }

    // ---- shared -----------------------------------------------------------------------------------------------------

    static IEnumerable<string> ChangeLines(HookChange c)
    {
        string Now() => ClaudeHooks.Describe(c.Event, c.New!, c.NewGroup);
        string Was() => ClaudeHooks.Describe(c.Event, c.Old!, c.OldGroup);
        return c.Kind switch
        {
            HookChangeKind.Add => [$"  {c.Event}: add {Now()}"],
            HookChangeKind.Unchanged => [$"  {c.Event}: already installed: {Now()}"],
            HookChangeKind.Remove => [$"  {c.Event}: remove {Was()}"],
            HookChangeKind.Update => [$"  {c.Event}: update", $"    old: {Was()}", $"    new: {Now()}"],
            _ => [],
        };
    }

    /// <summary><c>--dry-run</c> stops here; otherwise asks (unless <c>--yes</c>) and writes. False: nothing written by refusal or failure.</summary>
    bool Save(string text, bool backup)
    {
        if (request.DryRun) { output.WriteLine("dry run: nothing written"); return true; }
        if (!seams.Confirm($"Write {_path}?", request.Yes, output, error)) return false;
        try { JsonFile.Write(_path, text, backup); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"skarp install: cannot write {_path}: {ex.Message}; --print writes the new content to standard output instead");
            return false;
        }
        output.WriteLine($"Wrote {_path}" + (backup && File.Exists(_path + ".bak") ? $"; the original is kept as {_path}.bak" : ""));
        return true;
    }

}
