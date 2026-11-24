using System.Diagnostics;

namespace SkarpSharp.Install;

/// <summary>Claude Code's writable settings files (its own names).</summary>
public enum ClaudeScope { Local, Project, User }

/// <summary>The scopes in Claude Code's order of precedence (local first) and their names.</summary>
public static class ClaudeScopes
{
    public static readonly ClaudeScope[] All = [ClaudeScope.Local, ClaudeScope.Project, ClaudeScope.User];

    public static string Name(this ClaudeScope scope) => scope switch { ClaudeScope.Local => "local", ClaudeScope.Project => "project", _ => "user" };

    public static ClaudeScope? Parse(string name) => name switch
    {
        "local" => ClaudeScope.Local,
        "project" => ClaudeScope.Project,
        "user" => ClaudeScope.User,
        _ => null,
    };
}

/// <summary>One run of a hook program by <c>--check</c>.</summary>
public sealed class ProgramRun
{
    public required string Program { get; init; }
    public required IReadOnlyList<string> Args { get; init; }
    public required string Input { get; init; }
    /// <summary>The working folder and <c>CLAUDE_PROJECT_DIR</c>.</summary>
    public required string Directory { get; init; }
    public required int TimeoutSeconds { get; init; }
}

/// <summary>What a <see cref="ProgramRun"/> did: started or not, exit code, time, standard error.</summary>
public sealed class ProgramResult
{
    public bool Started { get; init; } = true;
    public bool TimedOut { get; init; }
    public int ExitCode { get; init; }
    public long Milliseconds { get; init; }
    public string Error { get; init; } = "";
}

/// <summary>
/// What <c>skarp install</c> reads from the machine, replaceable in tests: environment variables
/// (<c>CLAUDE_CONFIG_DIR</c>, <c>PATH</c>, <c>PATHEXT</c>), the home folder, the operating system, the git ignore check,
/// the terminal and its answers, and running a program. Tests never touch the real <c>~/.claude</c>.
/// </summary>
public sealed class InstallSeams
{
    public Func<string, string?> Variable { get; init; } = Environment.GetEnvironmentVariable;
    /// <summary>The home folder (<c>~</c>); default the user profile.</summary>
    public string? UserHome { get; init; }
    public bool? Windows { get; init; }
    /// <summary>Is the file ignored by git? True, false, or null when git cannot tell (no git, outside a repository).</summary>
    public Func<string, string, bool?>? GitIgnored { get; init; }
    /// <summary>True: standard input and output are a terminal, so Skarp may ask before writing.</summary>
    public Func<bool>? Interactive { get; init; }
    /// <summary>Where the answer to "Write ...? (y/n)" is read (default the console).</summary>
    public TextReader? Answers { get; init; }
    public Func<ProgramRun, ProgramResult>? RunProgram { get; init; }

    internal string Home => UserHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    internal bool IsWindows => Windows ?? OperatingSystem.IsWindows();
    internal bool? Ignored(string root, string file) => (GitIgnored ?? ProgramRunner.GitIgnored)(root, file);
    internal bool IsInteractive => Interactive?.Invoke() ?? !(Console.IsInputRedirected || Console.IsOutputRedirected);
    internal TextReader AnswerReader => Answers ?? Console.In;
    internal ProgramResult Run(ProgramRun r) => (RunProgram ?? ProgramRunner.Run)(r);

    /// <summary>Variables for the git processes of <c>install git-hooks</c> (tests isolate git from the
    /// machine's configuration); null: inherited only.</summary>
    public IReadOnlyDictionary<string, string?>? GitEnvironment { get; init; }

    /// <summary>The settings file of a scope: <c>.claude/settings.local.json</c> or <c>.claude/settings.json</c> in the
    /// project folder, or <c>~/.claude/settings.json</c> (<c>CLAUDE_CONFIG_DIR</c> replaces <c>~/.claude</c>).</summary>
    public string FileOf(ClaudeScope scope, string projectDir) => scope switch
    {
        ClaudeScope.Local => Path.Combine(projectDir, ".claude", "settings.local.json"),
        ClaudeScope.Project => Path.Combine(projectDir, ".claude", "settings.json"),
        _ => Path.GetFullPath(Path.Combine(Variable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : Path.Combine(Home, ".claude"), "settings.json")),
    };

    /// <summary>The file of an MCP server entry: <c>.mcp.json</c> in the project folder (project scope), else
    /// Claude Code's own <c>~/.claude.json</c> (<c>CLAUDE_CONFIG_DIR</c> replaces <c>~</c>), which holds the user entries
    /// at its top and the local ones per project.</summary>
    public string McpFileOf(ClaudeScope scope, string projectDir) => scope == ClaudeScope.Project
        ? Path.Combine(projectDir, ".mcp.json")
        : Path.GetFullPath(Path.Combine(Variable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : Home, ".claude.json"));

    /// <summary>
    /// Where Claude Code would find <paramref name="command"/>: <c>${CLAUDE_PROJECT_DIR}</c> expanded; a path (with a
    /// folder) relative to the project folder; a bare name on <c>PATH</c>. On Windows a name without an extension is
    /// tried with each <c>PATHEXT</c> extension. Null when not found.
    /// </summary>
    public string? Locate(string command, string projectDir)
    {
        var c = command.Replace(ClaudeHooks.ProjectDirVariable, projectDir, StringComparison.Ordinal);
        if (c.Length == 0) return null;
        bool hasFolder = c.Contains('/') || c.Contains('\\') || Path.IsPathRooted(c);
        if (hasFolder) return Existing(Path.GetFullPath(Path.IsPathRooted(c) ? c : Path.Combine(projectDir, c)));
        var path = Variable("PATH") ?? "";
        foreach (var raw in path.Split(IsWindows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = raw.Trim().Trim('"');
            if (dir.Length == 0) continue;
            string candidate;
            try { candidate = Path.Combine(dir, c); }
            catch (ArgumentException) { continue; }
            if (Existing(candidate) is { } found) return found;
        }
        return null;
    }

    string? Existing(string path)
    {
        if (!IsWindows) return File.Exists(path) ? path : null;
        var exts = (Variable("PATHEXT") is { Length: > 0 } pe ? pe : ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (Path.HasExtension(path) && File.Exists(path)) return path;
        foreach (var e in exts) if (File.Exists(path + e.ToLowerInvariant())) return path + e.ToLowerInvariant();
        return null;
    }

    /// <summary>
    /// True when Skarp writes its entry as <c>cmd.exe /c skarp ...</c>: Windows, no <c>--command</c>, and plain
    /// <c>skarp</c> resolves to a <c>.cmd</c> shim (the Native AOT tool package), which exec form cannot start. Never at
    /// project scope: that file is committed and teammates may be on another system.
    /// </summary>
    public bool UsesCmdWrapper(string command, bool commandGiven, ClaudeScope scope, string projectDir) =>
        IsWindows && !commandGiven && scope != ClaudeScope.Project && Locate(command, projectDir) is { } found && IsScriptShim(found);

    /// <summary>A program Windows cannot start without a shell (exec form needs a real <c>.exe</c>).</summary>
    public static bool IsScriptShim(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The default program runner and git ignore check of <see cref="InstallSeams"/>.</summary>
static class ProgramRunner
{
    const int GitTimeoutSeconds = 10;

    /// <summary><c>git check-ignore -q -- &lt;file&gt;</c> in <paramref name="root"/>: exit 0 ignored, 1 not ignored.</summary>
    public static bool? GitIgnored(string root, string file)
    {
        var r = Run(new ProgramRun { Program = "git", Args = ["-C", root, "check-ignore", "-q", "--", file], Input = "", Directory = root, TimeoutSeconds = GitTimeoutSeconds });
        if (!r.Started || r.TimedOut) return null;
        return r.ExitCode switch { 0 => true, 1 => false, _ => null };
    }

    /// <summary>Starts the program without a shell (<c>CLAUDE_PROJECT_DIR</c> set, as Claude Code does), writes the
    /// input, waits up to the timeout (then kills it).</summary>
    public static ProgramResult Run(ProgramRun r)
    {
        var psi = new ProcessStartInfo(r.Program)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = r.Directory, CreateNoWindow = true,
        };
        foreach (var a in r.Args) psi.ArgumentList.Add(a);
        psi.Environment["CLAUDE_PROJECT_DIR"] = r.Directory;
        var sw = Stopwatch.StartNew();
        Process process;
        try { process = Process.Start(psi) ?? throw new InvalidOperationException("not started"); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new ProgramResult { Started = false, Error = ex.Message };
        }
        using (process)
        {
            _ = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            Feed(process, r.Input);
            if (!process.WaitForExit(r.TimeoutSeconds * 1000))
            {
                Kill(process);
                return new ProgramResult { TimedOut = true, Milliseconds = sw.ElapsedMilliseconds };
            }
            process.WaitForExit();
            return new ProgramResult { ExitCode = process.ExitCode, Milliseconds = sw.ElapsedMilliseconds, Error = error.Result.Trim() };
        }
    }

    static void Feed(Process process, string input)
    {
        try
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The program exited without reading its input; its exit code says what happened.
        }
    }

    static void Kill(Process process)
    {
        try { process.Kill(); }
        catch (InvalidOperationException)
        {
            // It exited in the meantime.
        }
    }
}
