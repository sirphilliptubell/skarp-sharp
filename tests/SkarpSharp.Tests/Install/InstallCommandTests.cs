using System.Text.Json.Nodes;
using SkarpSharp.Commands;
using SkarpSharp.Install;

namespace SkarpSharp.Tests.Install;

/// <summary>
/// <c>skarp install claude</c> end to end in process, with
/// every machine read behind <see cref="InstallSeams"/>: a temp repository and a temp home, a fake PATH, a fake git ignore
/// check, terminal and program runner. Nothing reads or writes the real <c>~/.claude</c>.
/// </summary>
public sealed class InstallCommandTests : IDisposable
{
    readonly string _base = Path.Combine(Path.GetTempPath(), "skarp-install-" + Guid.NewGuid().ToString("N"));
    readonly string _repo, _home, _bin;
    readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);
    bool? _ignored = true;
    bool? _windows;
    bool _interactive;
    string _answers = "";
    readonly List<ProgramRun> _runs = [];
    Func<ProgramRun, ProgramResult> _runner = _ => new ProgramResult { ExitCode = 0, Milliseconds = 12 };

    public InstallCommandTests()
    {
        _repo = Path.Combine(_base, "repo");
        _home = Path.Combine(_base, "home");
        _bin = Path.Combine(_base, "bin");
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_bin);
        _env["PATH"] = _bin;
        _env["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
        File.WriteAllText(Path.Combine(_bin, OperatingSystem.IsWindows() ? "skarp.exe" : "skarp"), "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, true); } catch (IOException) { }
    }

    string Local => Path.Combine(_repo, ".claude", "settings.local.json");
    string Project => Path.Combine(_repo, ".claude", "settings.json");
    string User => Path.Combine(_home, ".claude", "settings.json");

    InstallSeams Seams => new()
    {
        Variable = n => _env.GetValueOrDefault(n), UserHome = _home, GitIgnored = (_, _) => _ignored, Windows = _windows,
        Interactive = () => _interactive, Answers = new StringReader(_answers),
        RunProgram = r => { _runs.Add(r); return _runner(r); },
    };

    async Task<(int Exit, string Out, string Error)> RunIn(string cwd, params string[] args)
    {
        var o = new StringWriter { NewLine = "\n" };
        var e = new StringWriter { NewLine = "\n" };
        int exit = await SkarpCommand.RunAsync(["install", .. args], new CommandIO
        {
            Out = o, Error = e, OpenStdout = () => Stream.Null, WorkingDirectory = cwd, Install = Seams,
        });
        return (exit, o.ToString(), e.ToString());
    }

    Task<(int Exit, string Out, string Error)> Run(params string[] args) => RunIn(Path.Combine(_repo, "src"), args);

    static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    // ---- scopes and files -------------------------------------------------------------------------------------------

    [Test]
    public async Task Local_scope_is_the_default_at_the_git_root_and_a_new_file_holds_only_the_entries()
    {
        var (exit, output, error) = await Run("claude", "--yes");
        await Assert.That(exit).IsEqualTo(0).Because(error);
        await Assert.That(File.Exists(Local)).IsTrue();
        await Assert.That(File.ReadAllText(Local)).IsEqualTo(JsonFileSnippet());
        await Assert.That(output).Contains($"Skarp hooks, local scope: {Local} (new file)");
        await Assert.That(output).Contains("PostToolUse: add skarp check --hook claude (matcher Edit|Write, timeout 30)");
        await Assert.That(output).Contains("Stop: add skarp check --hook claude (timeout 60)");
        await Assert.That(output).DoesNotContain("warning:");
        await Assert.That(File.Exists(Local + ".bak")).IsFalse();
    }

    static string JsonFileSnippet() => SkarpSharp.Setup.JsonFile.Render(ClaudeHooks.Snippet(new HookSpec()));

    [Test]
    public async Task Outside_git_the_current_folder_is_used()
    {
        var plain = Path.Combine(_base, "plain");
        Directory.CreateDirectory(plain);
        var (exit, _, _) = await RunIn(plain, "claude", "--scope", "project", "--yes");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(plain, ".claude", "settings.json"))).IsTrue();
    }

    [Test]
    public async Task Project_and_user_scopes_and_claude_config_dir()
    {
        await Assert.That((await Run("claude", "--scope", "project", "--yes")).Exit).IsEqualTo(0);
        await Assert.That(File.Exists(Project)).IsTrue();
        await Assert.That((await Run("claude", "--scope", "user", "--yes")).Exit).IsEqualTo(0);
        await Assert.That(File.Exists(User)).IsTrue();
        var config = Path.Combine(_base, "claude-config");
        _env["CLAUDE_CONFIG_DIR"] = config;
        var (exit, output, _) = await Run("claude", "--scope", "user", "--yes", "--no-stop");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(config, "settings.json"))).IsTrue();
        // The other user file (no longer the user scope) is not reported.
        await Assert.That(output).Contains("also installed at project scope");
    }

    [Test]
    public async Task Local_file_not_ignored_by_git_gets_the_warning()
    {
        _ignored = false;
        var (_, output, _) = await Run("claude", "--yes");
        await Assert.That(output).Contains($"warning: {Local} is not ignored by git; add it to .gitignore so it isn't committed");
        _ignored = false;
        var (_, project, _) = await Run("claude", "--scope", "project", "--dry-run");
        await Assert.That(project).DoesNotContain("not ignored by git");
    }

    [Test]
    public async Task An_existing_file_is_backed_up_and_keeps_its_keys_indentation_and_line_endings()
    {
        var original = "{\r\n\t\"permissions\": {\r\n\t\t\"allow\": []\r\n\t}\r\n}\r\n";
        Write(Local, original);
        var (exit, output, _) = await Run("claude", "--yes");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.ReadAllText(Local + ".bak")).IsEqualTo(original);
        var now = File.ReadAllText(Local);
        await Assert.That(now).StartsWith("{\r\n\t\"permissions\": {\r\n\t\t\"allow\": []\r\n\t},\r\n\t\"hooks\": {");
        await Assert.That(now.Replace("\r\n", "").Contains('\n')).IsFalse();
        await Assert.That(output).Contains($"the original is kept as {Local}.bak");
        // Removing restores it byte for byte.
        await Assert.That((await Run("claude", "--remove", "--yes")).Exit).IsEqualTo(0);
        await Assert.That(File.ReadAllText(Local)).IsEqualTo(original);
    }

    [Test]
    [Arguments("{\n  // mine\n  \"model\": \"opus\"\n}\n", "line 2")]
    [Arguments("{\n  \"model\": \"opus\",\n}\n", "line 3")]
    [Arguments("{\n  \"model\": \n", "line 3")]
    [Arguments("[]\n", "not a JSON object")]
    public async Task A_file_that_is_not_strict_json_is_refused_with_the_snippet(string text, string reason)
    {
        Write(Local, text);
        var (exit, _, error) = await Run("claude", "--yes");
        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(error).Contains(reason);
        await Assert.That(error).Contains("Skarp only edits strict JSON");
        await Assert.That(error).Contains("\"command\": \"skarp\"");
        await Assert.That(File.ReadAllText(Local)).IsEqualTo(text);
        await Assert.That(File.Exists(Local + ".bak")).IsFalse();
        await Assert.That((await Run("claude", "--remove", "--yes")).Exit).IsEqualTo(2);
    }

    [Test]
    public async Task Hooks_of_the_wrong_shape_are_refused()
    {
        Write(Local, "{\n  \"hooks\": []\n}\n");
        var (exit, _, error) = await Run("claude", "--yes");
        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(error).Contains("\"hooks\" is not an object");
    }

    // ---- writing ----------------------------------------------------------------------------------------------------

    [Test]
    public async Task Without_a_terminal_and_without_yes_nothing_is_written()
    {
        var (exit, _, error) = await Run("claude");
        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(error).Contains("add --yes");
        await Assert.That(File.Exists(Local)).IsFalse();
    }

    [Test]
    public async Task At_a_terminal_the_answer_decides()
    {
        _interactive = true;
        _answers = "n\n";
        var (no, output, _) = await Run("claude", "--hooks");
        await Assert.That(no).IsEqualTo(2);
        await Assert.That(output).Contains($"Write {Local}? (y/n) Nothing written.");
        await Assert.That(File.Exists(Local)).IsFalse();
        _answers = "y\n";
        await Assert.That((await Run("claude", "--hooks")).Exit).IsEqualTo(0);
        await Assert.That(File.Exists(Local)).IsTrue();
    }

    [Test]
    public async Task Dry_run_and_print_write_nothing()
    {
        var (exit, output, _) = await Run("claude", "--dry-run");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("dry run: nothing written");
        var (pexit, printed, preview) = await Run("claude", "--hooks", "--print");
        await Assert.That(pexit).IsEqualTo(0);
        await Assert.That(printed).IsEqualTo(JsonFileSnippet());
        await Assert.That(preview).Contains("PostToolUse: add");
        await Assert.That(Directory.Exists(Path.Combine(_repo, ".claude"))).IsFalse();
    }

    [Test]
    public async Task A_second_install_says_already_installed_and_writes_nothing()
    {
        await Run("claude", "--yes");
        var stamp = File.GetLastWriteTimeUtc(Local);
        var (exit, output, _) = await Run("claude", "--yes");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("PostToolUse: already installed");
        await Assert.That(output).Contains("Nothing to change");
        await Assert.That(File.Exists(Local + ".bak")).IsFalse();
        await Assert.That(File.GetLastWriteTimeUtc(Local)).IsEqualTo(stamp);
    }

    [Test]
    public async Task Options_update_the_entry_in_place_and_no_stop_removes_stop()
    {
        await Run("claude", "--yes");
        var (exit, output, _) = await Run("claude", "--gate", "--include-notices", "--max-lines", "8", "--no-stop", "--yes");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("    new: skarp check --hook claude --gate --max-lines 8 --include-notices (matcher Edit|Write, timeout 30)");
        await Assert.That(output).Contains("Stop: remove skarp check --hook claude (timeout 60)");
        var root = JsonNode.Parse(File.ReadAllText(Local))!;
        await Assert.That(root["hooks"]!.AsObject().ContainsKey("Stop")).IsFalse();
    }

    // ---- --command and PATH -----------------------------------------------------------------------------------------

    [Test]
    public async Task Skarp_not_on_path_is_a_warning_and_the_file_is_still_written()
    {
        _env["PATH"] = Path.Combine(_base, "nowhere");
        var (exit, output, _) = await Run("claude", "--yes");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("warning: skarp is not on PATH: Claude Code will show 'hook failed' after each edit. Install it with `dotnet tool install -g Skarp`, or use --command <path>");
        await Assert.That(File.Exists(Local)).IsTrue();
    }

    /// <summary>The Native AOT tool on Windows: only a skarp.cmd shim is on PATH (no real skarp.exe).</summary>
    void WindowsWithOnlyAShim()
    {
        _windows = true;
        foreach (var real in new[] { "skarp.exe", "skarp" }) File.Delete(Path.Combine(_bin, real));
        File.WriteAllText(Path.Combine(_bin, "skarp.cmd"), "");
    }

    JsonObject Handler(string settings, string ev = "PostToolUse") =>
        JsonNode.Parse(File.ReadAllText(settings))!["hooks"]![ev]![0]!["hooks"]![0]!.AsObject();

    static string[] Words(JsonNode? args) => [.. args!.AsArray().Select(a => a!.GetValue<string>())];

    [Test]
    public async Task A_cmd_shim_on_windows_is_run_through_cmd_exe_at_local_scope()
    {
        WindowsWithOnlyAShim();
        var (exit, output, error) = await Run("claude", "--yes");
        await Assert.That(exit).IsEqualTo(0).Because(error);
        await Assert.That(output).Contains("note: skarp is a .cmd shim, which exec form cannot start; the entry runs it through cmd.exe /c");
        await Assert.That(output).DoesNotContain("warning:");
        var h = Handler(Local);
        await Assert.That(h["command"]!.GetValue<string>()).IsEqualTo("cmd.exe");
        await Assert.That(Words(h["args"])).IsEquivalentTo(["/c", "skarp", "check", "--hook", "claude"], CollectionOrdering.Matching);
        var mcp = JsonNode.Parse(File.ReadAllText(Path.Combine(_home, ".claude.json")))!["projects"]!.AsObject().First().Value!["mcpServers"]!["skarp"]!;
        await Assert.That(mcp["command"]!.GetValue<string>()).IsEqualTo("cmd.exe");
        await Assert.That(Words(mcp["args"])).IsEquivalentTo(["/c", "skarp", "mcp"], CollectionOrdering.Matching);
        // A second run changes nothing.
        var (_, again, _) = await Run("claude", "--yes");
        await Assert.That(again).Contains("Nothing to change: Skarp's hooks are already installed.");
        await Assert.That(again).Contains("Nothing to change: Skarp's MCP server is already installed.");
    }

    [Test]
    public async Task A_cmd_shim_on_windows_is_run_through_cmd_exe_at_user_scope_too()
    {
        WindowsWithOnlyAShim();
        await Run("claude", "--scope", "user", "--yes");
        await Assert.That(Handler(User)["command"]!.GetValue<string>()).IsEqualTo("cmd.exe");
    }

    [Test]
    public async Task At_project_scope_the_plain_program_is_written_and_the_warning_says_why()
    {
        WindowsWithOnlyAShim();
        var (exit, output, error) = await Run("claude", "--scope", "project", "--yes");
        await Assert.That(exit).IsEqualTo(0).Because(error);
        await Assert.That(Handler(Project)["command"]!.GetValue<string>()).IsEqualTo("skarp");
        await Assert.That(output).Contains("warning: skarp resolves to").And.Contains("The project file is shared with teammates on other systems");
        await Assert.That(output).DoesNotContain("note: skarp is a .cmd shim");
    }

    [Test]
    public async Task A_real_skarp_exe_on_windows_is_started_directly()
    {
        _windows = true;
        File.WriteAllText(Path.Combine(_bin, "skarp.exe"), "");
        File.WriteAllText(Path.Combine(_bin, "skarp.cmd"), "");   // both: the .exe wins, as PATHEXT orders them
        var (_, output, _) = await Run("claude", "--yes");
        await Assert.That(Handler(Local)["command"]!.GetValue<string>()).IsEqualTo("skarp");
        await Assert.That(output).DoesNotContain("cmd.exe");
    }

    [Test]
    public async Task An_explicit_command_that_is_a_shim_is_written_as_given_with_the_warning()
    {
        WindowsWithOnlyAShim();
        var (_, output, _) = await Run("claude", "--command", "skarp", "--dry-run");
        await Assert.That(output).Contains("a .cmd or .bat script: exec form needs a real .exe, so Claude Code cannot start it; use --command with the .exe");
        await Assert.That(output).DoesNotContain("through cmd.exe");
    }

    [Test]
    public async Task Check_runs_a_cmd_wrapped_entry_through_cmd_exe_and_flags_it_off_windows()
    {
        WindowsWithOnlyAShim();
        await Run("claude", "--yes");
        _runs.Clear();
        var (exit, output, _) = await Run("claude", "--check", "--hooks");
        await Assert.That(exit).IsEqualTo(0).Because(output);
        await Assert.That(output).Contains("(run through cmd.exe /c)").And.DoesNotContain("problem:");
        var run = _runs.First(r => r.Args.Contains("check"));
        await Assert.That(run.Program).EndsWith("cmd.exe");
        await Assert.That(run.Args.Take(5)).IsEquivalentTo(["/c", "skarp", "check", "--hook", "claude"], CollectionOrdering.Matching);
        // The same file read on another system: cmd.exe is not there.
        _windows = false;
        var (offExit, offOutput, _) = await Run("claude", "--check", "--hooks");
        await Assert.That(offExit).IsEqualTo(1);
        await Assert.That(offOutput).Contains("problem: runs through cmd.exe, which exists on Windows only");
    }

    [Test]
    public async Task Command_is_resolved_after_project_dir_expansion()
    {
        var exe = Path.Combine(_repo, ".skarp", "bin", "skarp.exe");
        Write(exe, "");
        var (exit, output, _) = await Run("claude", "--command", "${CLAUDE_PROJECT_DIR}/.skarp/bin/skarp.exe", "--yes");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).DoesNotContain("warning:");
        File.Delete(exe);
        var (_, missing, _) = await Run("claude", "--command", "${CLAUDE_PROJECT_DIR}/.skarp/bin/skarp.exe", "--dry-run");
        await Assert.That(missing).Contains($"warning: ${{CLAUDE_PROJECT_DIR}}/.skarp/bin/skarp.exe not found (with ${{CLAUDE_PROJECT_DIR}} = {_repo})");
    }

    [Test]
    [Arguments("${CLAUDE_PROJECT_DIR}/.skarp/bin/skarp.exe", "a .exe runs on Windows only")]
    [Arguments("C:/tools/skarp", "an absolute path exists on this machine only")]
    [Arguments("/opt/skarp/skarp", "an absolute path exists on this machine only")]
    public async Task A_command_that_is_not_portable_is_refused_at_project_scope_unless_forced(string command, string why)
    {
        var (exit, _, error) = await Run("claude", "--scope", "project", "--command", command, "--yes");
        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(error).Contains(why);
        await Assert.That(File.Exists(Project)).IsFalse();
        await Assert.That((await Run("claude", "--scope", "project", "--command", command, "--force", "--yes")).Exit).IsEqualTo(0);
        await Assert.That((await Run("claude", "--scope", "local", "--command", command, "--yes")).Exit).IsEqualTo(0);
    }

    // ---- other scopes -----------------------------------------------------------------------------------------------

    [Test]
    public async Task An_identical_entry_at_another_scope_is_a_note()
    {
        await Run("claude", "--scope", "user", "--yes");
        var (exit, output, _) = await Run("claude", "--yes");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains($"note: also installed at user scope ({User}); Claude Code runs identical hooks once");
    }

    [Test]
    public async Task A_different_entry_at_another_scope_stops_the_install_unless_forced()
    {
        await Run("claude", "--scope", "project", "--gate", "--yes");
        var (exit, _, error) = await Run("claude", "--yes");
        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(error).Contains($"a different Skarp entry is installed at project scope ({Project}): PostToolUse skarp check --hook claude --gate");
        await Assert.That(File.Exists(Local)).IsFalse();
        var (forced, _, note) = await Run("claude", "--yes", "--force");
        await Assert.That(forced).IsEqualTo(0);
        await Assert.That(note).Contains("note: a different Skarp entry");
        await Assert.That(File.Exists(Local)).IsTrue();
    }

    [Test]
    public async Task An_unreadable_file_at_another_scope_is_a_note()
    {
        Write(User, "{ // comment\n}\n");
        var (exit, output, _) = await Run("claude", "--yes");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains($"note: user scope ({User}) is not strict JSON");
    }

    // ---- remove -----------------------------------------------------------------------------------------------------

    [Test]
    public async Task Remove_at_one_scope_lists_what_is_left_elsewhere()
    {
        await Run("claude", "--yes");
        await Run("claude", "--scope", "user", "--yes");
        var (exit, output, _) = await Run("claude", "--remove", "--yes");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("PostToolUse: remove skarp check --hook claude");
        await Assert.That(output).Contains($"note: Skarp entries left at user scope ({User}): PostToolUse, Stop, PostCompact (skarp install claude --remove --scope user)");
        await Assert.That(File.ReadAllText(Local)).IsEqualTo("{}\n");
        await Assert.That(File.Exists(Local + ".bak")).IsTrue();
        var (again, nothing, _) = await Run("claude", "--remove", "--yes");
        await Assert.That(again).IsEqualTo(0);
        await Assert.That(nothing).Contains("Nothing to remove");
    }

    [Test]
    public async Task Remove_with_a_given_command_recognizes_it()
    {
        Write(Local, "{\n  \"hooks\": {\n    \"Stop\": [\n      {\n        \"hooks\": [\n          {\n            \"type\": \"command\",\n            \"command\": \"tools/sk\",\n            \"args\": [\n              \"check\",\n              \"--hook\",\n              \"claude\"\n            ]\n          }\n        ]\n      }\n    ]\n  }\n}\n");
        await Assert.That((await Run("claude", "--remove", "--dry-run")).Out).Contains("Nothing to remove");
        var (exit, output, _) = await Run("claude", "--remove", "--command", "tools/sk", "--yes");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Stop: remove tools/sk check --hook claude");
    }

    // ---- usage ------------------------------------------------------------------------------------------------------

    [Test]
    [Arguments(new[] { "install" }, "name a target")]
    [Arguments(new[] { "install", "cursor" }, "unknown target 'cursor'; targets: claude")]
    [Arguments(new[] { "install", "claude", "--scope", "team" }, "unknown scope 'team'")]
    [Arguments(new[] { "install", "claude", "--check", "--scope", "user" }, "--check takes --hooks or --mcp only")]
    [Arguments(new[] { "install", "claude", "--remove", "--gate" }, "--remove takes --scope, --command, --hooks, --mcp, --dry-run and --yes only (not --gate)")]
    [Arguments(new[] { "install", "claude", "--semantic", "--no-stop" }, "--semantic applies to the Stop entry")]
    [Arguments(new[] { "install", "claude", "--max-lines", "0" }, "--max-lines needs a whole number")]
    [Arguments(new[] { "install", "claude", "--min-severity", "notice" }, "unknown option --min-severity")]
    [Arguments(new[] { "install", "claude", "--bogus" }, "unknown option --bogus")]
    public async Task Usage_errors_exit_2(string[] args, string message)
    {
        var e = new StringWriter();
        int exit = await SkarpCommand.RunAsync(args, new CommandIO { Out = TextWriter.Null, Error = e, OpenStdout = () => Stream.Null, WorkingDirectory = _repo, Install = Seams });
        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(e.ToString()).Contains(message);
    }

    [Test]
    public async Task Help_is_exit_0_and_the_main_help_names_install()
    {
        var (exit, output, _) = await Run("--help");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("skarp install claude --check");
        await Assert.That(SkarpSharp.Configuration.CommandLine.Usage).Contains("skarp install  claude|git-hooks [options]");
    }

    // ---- --check ----------------------------------------------------------------------------------------------------

    [Test]
    public async Task Check_with_nothing_installed_is_exit_0()
    {
        var (exit, output, _) = await Run("claude", "--check", "--hooks");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("no Skarp hooks installed");
        await Assert.That(output.TrimEnd()).EndsWith(ClaudeCheck.ClaudeCodeLine);
    }

    [Test]
    public async Task Check_finds_and_runs_each_entry_with_a_synthetic_input()
    {
        await Run("claude", "--yes");
        var (exit, output, _) = await Run("claude", "--check", "--hooks");
        await Assert.That(exit).IsEqualTo(0).Because(output);
        await Assert.That(output).Contains($"local: {Local}");
        await Assert.That(output).Contains("test run: exit 0 in 12 ms");
        await Assert.That(output).Contains("all good");
        await Assert.That(_runs.Count).IsEqualTo(3);
        var input = JsonNode.Parse(_runs[0].Input)!;
        await Assert.That(input["hook_event_name"]!.GetValue<string>()).IsEqualTo("PostToolUse");
        await Assert.That(input["tool_input"]!["file_path"]!.GetValue<string>()).EndsWith(".txt");
        await Assert.That(_runs[0].Directory).IsEqualTo(_repo);
        await Assert.That(_runs[0].TimeoutSeconds).IsEqualTo(30);
        await Assert.That(_runs[1].TimeoutSeconds).IsEqualTo(60);
        await Assert.That(_runs[2].TimeoutSeconds).IsEqualTo(30);                    // PostCompact
        await Assert.That(output).Contains("  PostCompact: skarp check --hook claude (timeout 30)");
        await Assert.That(string.Join(" ", _runs[0].Args)).IsEqualTo("check --hook claude");
    }

    [Test]
    public async Task Check_reports_a_missing_program_a_failed_run_and_a_timeout()
    {
        await Run("claude", "--yes", "--no-stop", "--no-compact");
        _env["PATH"] = Path.Combine(_base, "nowhere");
        var (missing, output, _) = await Run("claude", "--check", "--hooks");
        await Assert.That(missing).IsEqualTo(1);
        await Assert.That(output).Contains("problem: skarp not found on PATH: Claude Code shows 'hook failed' after each edit");
        await Assert.That(output).Contains("1 problem found");
        _env["PATH"] = _bin;
        _runner = _ => new ProgramResult { ExitCode = 1, Error = "boom\nmore" };
        var (failed, failText, _) = await Run("claude", "--check", "--hooks");
        await Assert.That(failed).IsEqualTo(1);
        await Assert.That(failText).Contains("problem: test run exit 1: boom");
        _runner = _ => new ProgramResult { TimedOut = true };
        await Assert.That((await Run("claude", "--check", "--hooks")).Out).Contains("problem: no answer within 30 s");
        _runner = _ => new ProgramResult { Started = false, Error = "denied" };
        await Assert.That((await Run("claude", "--check", "--hooks")).Out).Contains("problem: could not start it: denied");
    }

    [Test]
    public async Task Check_flags_shell_form_cmd_shims_strict_json_and_differences_across_scopes()
    {
        Write(Local, "{\"hooks\":{\"PostToolUse\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"skarp check --hook claude\"}]}]}}");
        Write(User, "{\n  \"model\": \"opus\",\n}\n");
        await Run("claude", "--scope", "project", "--gate", "--yes", "--force");
        var (exit, output, _) = await Run("claude", "--check", "--hooks");
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(output).Contains("note: shell form");
        await Assert.That(output).Contains("problem: not strict JSON (line 3)");
        await Assert.That(output).Contains("problem: PostToolUse differs between local and project scope; both run on each event");
        await Assert.That(output).Contains("2 problems found");
        if (OperatingSystem.IsWindows())
        {
            File.Delete(Path.Combine(_bin, "skarp.exe"));
            File.WriteAllText(Path.Combine(_bin, "skarp.cmd"), "");
            await Assert.That((await Run("claude", "--check", "--hooks")).Out).Contains("problem: a .cmd or .bat script");
        }
    }

    [Test]
    public async Task Check_notes_identical_entries_across_scopes()
    {
        await Run("claude", "--yes");
        await Run("claude", "--scope", "user", "--yes");
        var (exit, output, _) = await Run("claude", "--check", "--hooks");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("note: PostToolUse is installed at local and user scope, identically");
    }

    /// <summary>One real run: the built <c>skarp</c> (the CLI project's JIT build output, when it was built) through the
    /// default program runner, with the synthetic input. Exit 0 by the hook contract.</summary>
    [Test]
    public async Task Check_runs_the_built_skarp_for_real()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SkarpSharp.slnx"))) root = root.Parent;
        var exe = OperatingSystem.IsWindows() ? "skarp.exe" : "skarp";
        var built = root is null ? null : new[] { "Debug", "Release" }.Select(c => Path.Combine(root.FullName, "src", "SkarpSharp.Cli", "bin", c, "net10.0", exe)).FirstOrDefault(File.Exists);
        if (built is null) { Skip.Test("the CLI project is not built (dotnet build src/SkarpSharp.Cli)"); return; }
        _runner = null!;
        var seams = new InstallSeams { Variable = n => _env.GetValueOrDefault(n), UserHome = _home, GitIgnored = (_, _) => true, Interactive = () => false };
        var o = new StringWriter();
        await SkarpCommand.RunAsync(["install", "claude", "--command", built, "--yes", "--no-stop"], new CommandIO { Out = o, Error = o, OpenStdout = () => Stream.Null, WorkingDirectory = _repo, Install = seams });
        var c = new StringWriter();
        int exit = await SkarpCommand.RunAsync(["install", "claude", "--check"], new CommandIO { Out = c, Error = c, OpenStdout = () => Stream.Null, WorkingDirectory = _repo, Install = seams });
        await Assert.That(exit).IsEqualTo(0).Because(c.ToString());
        await Assert.That(c.ToString()).Contains("test run: exit 0");
    }

    // ---- docs ----------------------------------------------------------------------------------------

    [Test]
    public async Task The_by_hand_page_snippet_equals_print_on_an_empty_folder()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SkarpSharp.slnx"))) root = root.Parent;
        var page = File.ReadAllText(Path.Combine(root!.FullName, "docs", "agents", "claude-code.md")).Replace("\r\n", "\n");
        const string Marker = "<!-- equals: skarp install claude --hooks --print on an empty folder (InstallCommandTests) -->\n```json\n";
        int at = page.IndexOf(Marker, StringComparison.Ordinal);
        await Assert.That(at).IsGreaterThanOrEqualTo(0);
        var start = at + Marker.Length;
        var snippet = page[start..(page.IndexOf("```\n", start, StringComparison.Ordinal))];
        var empty = Path.Combine(_base, "empty");
        Directory.CreateDirectory(empty);
        var (exit, printed, _) = await RunIn(empty, "claude", "--hooks", "--print");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(snippet).IsEqualTo(printed);
    }
}
