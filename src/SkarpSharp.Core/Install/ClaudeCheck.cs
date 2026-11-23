using System.Globalization;
using System.Text.Json.Nodes;

namespace SkarpSharp.Install;

/// <summary>
/// <c>skarp install claude --check</c>. Lists every Skarp entry at the three scopes, finds each
/// program, flags shell form and <c>.cmd</c> shims, runs the program once with a synthetic PostToolUse input for a non-C#
/// file (a no-op by the hook contract, so exit 0 is expected), and compares entries across scopes. It cannot test Claude
/// Code itself, and says so on its last line.
/// </summary>
public sealed class ClaudeCheck(TextWriter output, InstallSeams seams, string projectDir)
{
    public const string ClaudeCodeLine = "Claude Code itself can't be tested from here: it runs the hooks after the workspace-trust question and shows a 'hook failed' line when the program is missing.";
    const int PostToolUseTimeout = 30, StopTimeout = 60;

    int _problems, _entries;
    readonly List<string[]> _keys = []; // scope name, event, key

    /// <summary>Check the hook entries (<c>--hooks</c>, or neither option).</summary>
    public bool Hooks { get; init; } = true;
    /// <summary>Check the MCP server entry (<c>--mcp</c>, or neither option).</summary>
    public bool Mcp { get; init; } = true;

    /// <summary>Runs the check; 1 when any problem was found, else 0.</summary>
    public int Run()
    {
        if (Hooks) CheckHooks();
        if (Mcp) CheckMcp();
        if (_problems > 0) output.WriteLine(_problems == 1 ? "1 problem found" : $"{_problems} problems found");
        else if (_entries > 0) output.WriteLine("all good");
        output.WriteLine(ClaudeCodeLine);
        return _problems > 0 ? 1 : 0;
    }

    /// <summary>The MCP entry at each scope (<c>.mcp.json</c>, <c>~/.claude.json</c> local and user) and its
    /// program; Claude Code uses the local entry first, then project, then user.</summary>
    void CheckMcp()
    {
        output.WriteLine("Skarp MCP server for Claude Code:");
        int found = 0;
        foreach (var scope in ClaudeScopes.All)
            if (McpEntry(scope) is { } entry) McpProgram(scope, entry, ++found > 1);
        if (found == 0) output.WriteLine("no Skarp MCP server installed");
    }

    /// <summary>Skarp's MCP entry at <paramref name="scope"/>, or null after the line that says why there is none.</summary>
    JsonObject? McpEntry(ClaudeScope scope)
    {
        var file = ClaudeFile.Read(seams.McpFileOf(scope, projectDir));
        var name = $"{scope.Name()}: {file.Path}";
        if (!file.Existed) { output.WriteLine(name + ": no file"); return null; }
        if (file.Error is not null) { output.WriteLine(name); Problem($"  problem: {file.Error}; Claude Code cannot read this file"); return null; }
        try
        {
            var entry = ClaudeMcp.Find(file.Root, scope, projectDir);
            output.WriteLine(entry is null ? name + ": no Skarp MCP server" : name + (scope == ClaudeScope.Local ? " (this project)" : ""));
            return entry;
        }
        catch (HookShapeException ex) { output.WriteLine(name); Problem($"  problem: {ex.Message}"); return null; }
    }

    /// <summary>The entry and where its program is.</summary>
    void McpProgram(ClaudeScope scope, JsonObject entry, bool shadowed)
    {
        _entries++;
        output.WriteLine($"  {ClaudeMcp.ServerName}: {ClaudeMcp.Describe(entry)}" + (shadowed ? " (not used: an entry at a scope listed above wins)" : ""));
        var command = entry["command"]?.ToString() ?? "";
        var program = ClaudeMcp.ProgramOf(entry);
        bool wrapped = program != command;
        if (wrapped && !seams.IsWindows) { Problem("    problem: runs through cmd.exe, which exists on Windows only"); return; }
        if (seams.Locate(ClaudeMcp.Locatable(program), projectDir) is { } resolved) output.WriteLine($"    program: {resolved}{(wrapped ? " (run through cmd.exe /c)" : "")}");
        else Problem("    problem: " + NotFound(program, "Claude Code shows the server as failed"));
    }

    void CheckHooks()
    {
        output.WriteLine("Skarp hooks for Claude Code:");
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in ClaudeScopes.All)
        {
            var path = seams.FileOf(scope, projectDir);
            var full = Path.GetFullPath(path);
            if (seen.TryGetValue(full, out var same)) { output.WriteLine($"{scope.Name()}: {path}: the same file as {same}"); continue; }
            seen[full] = scope.Name();
            Scope(scope, ClaudeFile.Read(path));
        }
        CompareScopes();
        if (_entries == 0) output.WriteLine("no Skarp hooks installed");
    }

    void Scope(ClaudeScope scope, ClaudeFile file)
    {
        if (!file.Existed) { output.WriteLine($"{scope.Name()}: {file.Path}: no file"); return; }
        if (file.Error is not null) { output.WriteLine($"{scope.Name()}: {file.Path}"); Problem($"  problem: {file.Error}; Claude Code skips this file"); return; }
        var found = ClaudeHooks.Find(file.Root);
        if (found.Count == 0) { output.WriteLine($"{scope.Name()}: {file.Path}: no Skarp hooks"); return; }
        output.WriteLine($"{scope.Name()}: {file.Path}");
        foreach (var f in found)
        {
            _entries++;
            Entry(f);
            _keys.Add([scope.Name(), f.Event, ClaudeHooks.Key(f.Handler, projectDir)]);
        }
    }

    void Problem(string line)
    {
        output.WriteLine(line);
        _problems++;
    }

    /// <summary>Identical entries at two scopes run once (a note); different ones both run (a problem).</summary>
    void CompareScopes()
    {
        foreach (var ev in ClaudeHooks.Events)
        {
            var at = _keys.Where(k => k[1] == ev).ToList();
            var scopes = at.Select(k => k[0]).Distinct().ToList();
            if (scopes.Count < 2) continue;
            var names = string.Join(" and ", scopes);
            if (at.Select(k => k[2]).Distinct().Count() == 1) output.WriteLine($"note: {ev} is installed at {names} scope, identically; Claude Code runs identical hooks once");
            else Problem($"problem: {ev} differs between {names} scope; both run on each event");
        }
    }

    /// <summary>One entry: its program found, a <c>.cmd</c> shim or shell form flagged, one synthetic run.</summary>
    void Entry(FoundHook f)
    {
        var h = f.Handler;
        output.WriteLine($"  {f.Event}: {ClaudeHooks.Describe(f.Event, h, f.Group)}");
        bool shell = ClaudeHooks.IsShellForm(h);
        if (shell) output.WriteLine("    note: shell form (Claude Code runs it through a shell); skarp install claude rewrites it in exec form");
        var (program, args) = ClaudeHooks.Invocation(h)!.Value;
        bool wrapped = ClaudeHooks.IsCmdWrapped(h);
        if (wrapped && !seams.IsWindows) { Problem("    problem: runs through cmd.exe, which exists on Windows only"); return; }
        if (seams.Locate(program, projectDir) is not { } resolved) { Problem("    problem: " + NotFound(program)); return; }
        output.WriteLine($"    program: {resolved}{(wrapped ? " (run through cmd.exe /c)" : "")}");
        if (!shell && !wrapped && seams.IsWindows && InstallSeams.IsScriptShim(resolved))
        {
            Problem("    problem: a .cmd or .bat script: exec form needs a real .exe, so Claude Code cannot start it");
            return;
        }
        int timeout = h["timeout"] is JsonValue t && t.TryGetValue(out int seconds) && seconds > 0 ? seconds : f.Event == ClaudeHooks.Stop ? StopTimeout : PostToolUseTimeout;
        var expanded = args.Select(a => a.Replace(ClaudeHooks.ProjectDirVariable, projectDir, StringComparison.Ordinal));
        var run = seams.Run(new ProgramRun
        {
            Program = wrapped ? seams.Locate(ClaudeHooks.CmdProgram, projectDir) ?? ClaudeHooks.CmdProgram : resolved,
            Input = SyntheticInput(), Directory = projectDir, TimeoutSeconds = timeout,
            Args = wrapped ? ["/c", program, .. expanded] : [.. expanded],
        });
        if (Failure(run, timeout) is { } problem) { Problem("    problem: " + problem); return; }
        output.WriteLine($"    test run: exit 0 in {run.Milliseconds.ToString(CultureInfo.InvariantCulture)} ms (a PostToolUse input for a non-C# file)");
    }

    static string NotFound(string program, string consequence = "Claude Code shows 'hook failed' after each edit")
    {
        bool bare = !program.Contains('/') && !program.Contains('\\');
        var hint = bare && ClaudeHooks.IsSkarpProgram(program) ? " (install Skarp with `dotnet tool install -g Skarp`)" : "";
        return $"{program} not found{(bare ? " on PATH" : "")}: {consequence}{hint}";
    }

    static string? Failure(ProgramResult run, int timeout)
    {
        if (!run.Started) return $"could not start it: {run.Error}";
        if (run.TimedOut) return $"no answer within {timeout} s";
        if (run.ExitCode == 0) return null;
        return $"test run exit {run.ExitCode}{(run.Error.Length > 0 ? ": " + run.Error.Split('\n')[0].Trim() : "")}";
    }

    /// <summary>What Claude Code sends after writing a text file: Skarp ignores non-C# files and exits 0.</summary>
    string SyntheticInput() => new JsonObject
    {
        ["session_id"] = "skarp-install-check",
        ["hook_event_name"] = "PostToolUse",
        ["tool_name"] = "Write",
        ["tool_input"] = new JsonObject { ["file_path"] = Path.Combine(projectDir, "skarp-install-check.txt") },
        ["cwd"] = projectDir,
    }.ToJsonString();
}
