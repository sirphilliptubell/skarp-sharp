using System.Text;
using System.Text.Json.Nodes;
using SkarpSharp.Commands;
using SkarpSharp.Configuration;
using SkarpSharp.Install;
using SkarpSharp.Setup;
using SkarpSharp.Tests.Install;

namespace SkarpSharp.Tests.Setup;

/// <summary>
/// The "Claude Code hooks" screen of <c>skarp setup</c>. Golden
/// frames through the hidden <c>--script</c> seam; the parse-back of every printed command; equality of menu apply, the
/// printed command and plain <c>skarp install claude</c> on every fixture; prefill (applying unchanged writes
/// nothing); notices off and kept options. Every machine read goes through <see cref="InstallSeams"/> and
/// <see cref="SettingsLocations"/> into a temp folder: nothing reads the real <c>~/.claude</c> or <c>~/.skarp</c>.
/// </summary>
// The golden updates strip the byte order mark of every file in the folder, so these run one at a time with the other frames.
[NotInParallel(nameof(SetupMenuTests))]
public sealed class SetupHooksTests : IDisposable
{
    const string ProjectExe = "${CLAUDE_PROJECT_DIR}/.skarp/bin/skarp.exe";

    readonly string _base = Path.Combine(Path.GetTempPath(), "skarp-hooks-" + Guid.NewGuid().ToString("N"));
    readonly string _repo, _home;
    readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);

    public SetupHooksTests()
    {
        _repo = Path.Combine(_base, "repo");
        _home = Path.Combine(_repo, "machine", "home");
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Directory.CreateDirectory(_home);
        var bin = Path.Combine(_base, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, OperatingSystem.IsWindows() ? "skarp.exe" : "skarp"), "");
        _env["PATH"] = bin;
        _env["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, true); } catch (IOException) { }
    }

    string Local => Path.Combine(_repo, ".claude", "settings.local.json");
    string User => Path.Combine(_home, ".claude", "settings.json");
    string RepoFile => Path.Combine(_repo, SettingsFileReader.FileName);
    string PersonalFile => Path.Combine(_repo, "machine", "personal.json");

    SettingsLocations Locations => new() { SystemFile = Path.Combine(_repo, "machine", "system.json"), PersonalFile = PersonalFile, Variable = n => _env.GetValueOrDefault(n) };

    InstallSeams Seams => new()
    {
        Variable = n => _env.GetValueOrDefault(n), UserHome = _home, GitIgnored = (_, _) => true,
        // Two answers, one for the hooks file and one for the MCP entry.
        Interactive = () => true, Answers = new StringReader("y\ny\n"),
        RunProgram = _ => throw new InvalidOperationException("the menu never runs a program"),
    };

    async Task<(int Exit, string Out, string Error)> Run(params string[] args)
    {
        var o = new StringWriter { NewLine = "\n" };
        var e = new StringWriter { NewLine = "\n" };
        int exit = await SkarpCommand.RunAsync(args, new CommandIO
        {
            Out = o, Error = e, OpenStdout = () => Stream.Null, WorkingDirectory = _repo, SettingsLocations = Locations, Install = Seams,
        });
        return (exit, o.ToString(), e.ToString());
    }

    static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>Skarp's entries for <paramref name="spec"/>, each handler with <paramref name="extra"/> appended by hand.</summary>
    static string Entry(HookSpec spec, params string[] extra)
    {
        var root = ClaudeHooks.Snippet(spec);
        foreach (var ev in spec.Events)
            foreach (var a in extra) root["hooks"]![ev]![0]!["hooks"]![0]!["args"]!.AsArray().Add((JsonNode?)JsonValue.Create(a));
        return JsonFile.Render(root);
    }

    // ---- golden frames ----------------------------------------------------------------------------------------------

    string Scrub(string s) => s.Replace(_repo, "{repo}", StringComparison.OrdinalIgnoreCase).Replace('\\', '/');

    async Task<string> Script(string keys, params string[] args)
    {
        var script = Path.Combine(_base, "keys-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        File.WriteAllText(script, keys);
        var (exit, output, error) = await Run(["setup", .. args, "--script", script, "--script-size", "80x24"]);
        if (exit == 2) Assert.Fail(error);
        int raw = output.IndexOf("=== raw\n", StringComparison.Ordinal), summary = output.IndexOf("\n=== summary\n", StringComparison.Ordinal);
        return Scrub(output[..raw] + output[(summary + 1)..]);
    }

    static Task Verify(string text) => Snapshots.Verify(text, "Setup/frames", null, "txt", "SKARP_UPDATE_GOLDEN");

    void Settings()
    {
        Write(RepoFile, """{ "version": 1, "output": { "gateMaxLines": 20 }, "gate": { "on": ["*:warning", "long-method:notice"] } }""");
        Write(PersonalFile, """{ "version": 1, "output": { "checkMaxLines": 3 } }""");
    }

    [Test]
    public async Task Hooks_screen_rows_panes_and_markdown()
    {
        Settings();
        Write(Local, Entry(new HookSpec { Gate = true }, "--no-save"));
        // Every row's pane, the gate off, the line limit typed, then the command for all shells.
        var keys = "Down Down Down Down Down Space Down Down Down Enter 8 Enter Down Down s End Enter";
        await Verify(await Script(keys, "claude"));
    }

    [Test]
    public async Task Hooks_screen_scope_list_reloads_and_remove_prints_for_one_shell()
    {
        Write(User, Entry(new HookSpec { Command = ProjectExe, CommandGiven = true, Notice = true, Stop = false }));
        var keys = "Enter Up Enter Enter Down Enter End Enter s Home Enter";
        await Verify(await Script(keys, "claude"));
    }

    [Test]
    public async Task Level_choice_fifth_row_opens_the_hooks_screen_and_apply_closes_the_menu()
    {
        var keys = "Down Down Enter Esc Enter Down Down Down Space a";
        await Verify(await Script(keys));
    }

    // ---- parse-back -------------------------------------------------------------------------------------------------

    /// <summary>Every option combination the screen can produce (the semantic check needs the Stop entry).</summary>
    static IEnumerable<HookSpec> Combos(params string[] programs)
    {
        foreach (var program in programs)
            foreach (var stop in new[] { true, false })
                foreach (var semantic in stop ? new[] { false, true } : new[] { false })
                    foreach (var gate in new[] { false, true })
                        foreach (var notice in new[] { false, true })
                            foreach (var max in new int?[] { null, 7 })
                                // The PostCompact entry, off only beside an end-of-turn summary that is off too.
                                foreach (var compact in stop ? new[] { true } : new[] { true, false })
                                    yield return new HookSpec
                                    {
                                        Command = program, CommandGiven = program != "skarp", Stop = stop, Compact = compact, Semantic = semantic, Gate = gate, Notice = notice, MaxLines = max,
                                    };
    }

    [Test]
    public async Task Every_printed_command_parses_back_to_the_menus_request()
    {
        int count = 0;
        foreach (var scope in ClaudeScopes.All)
            foreach (var spec in Combos("skarp", ProjectExe, "C:/Program Files/Skarp Tools/skarp.exe"))
                foreach (var had in new[] { false, true })
                    foreach (var remove in new[] { false, true })
                    {
                        // As HooksTarget.Call builds it: notices off is written only when the entry had them.
                        var notice = spec.Notice == true ? true : had ? false : (bool?)null;
                        var call = remove ? new InstallCall(scope, true, new HookSpec()) : new InstallCall(scope, false, spec with { Notice = notice });
                        var args = call.Args();
                        var (back, error) = InstallCall.Parse(args);
                        await Assert.That(back).IsEqualTo(call).Because($"{string.Join(" ", args)}: {error}");
                        await Assert.That(args.Any(a => a is "--yes" or "-y" or "--force")).IsFalse();
                        // Every shell prints it whole (no value a shell cannot carry), and the arguments are the call's.
                        var built = CommandBuilder.FromArgs(args, _repo);
                        foreach (var shell in new[] { Shell.PowerShell, Shell.Cmd, Shell.Sh })
                            await Assert.That(built.Arguments(shell).Args).IsEquivalentTo(args, CollectionOrdering.Matching);
                        count++;
                    }
        await Assert.That(count).IsEqualTo(3 * 3 * 32 * 2 * 2);
    }

    [Test]
    public async Task The_printed_command_names_only_non_defaults()
    {
        await Assert.That(string.Join(" ", new InstallCall(ClaudeScope.Local, false, new HookSpec()).Args())).IsEqualTo("install claude");
        var all = new HookSpec { Command = ProjectExe, CommandGiven = true, Stop = true, Semantic = true, Gate = true, MaxLines = 8, Notice = true };
        await Assert.That(string.Join(" ", new InstallCall(ClaudeScope.Project, false, all).Args()))
            .IsEqualTo($"install claude --scope project --command {ProjectExe} --gate --semantic --max-lines 8 --include-notices");
        await Assert.That(string.Join(" ", new InstallCall(ClaudeScope.User, true, new HookSpec()).Args())).IsEqualTo("install claude --remove --scope user");
        var built = CommandBuilder.FromArgs(new InstallCall(ClaudeScope.Local, false, new HookSpec { Command = "C:/a%b/skarp.exe", CommandGiven = true }).Args(), _repo);
        await Assert.That(Scrub(built.Render(Shell.Cmd, out var lossy))).IsEqualTo("REM cmd, run from {repo}\nREM note: the command cannot be written for cmd: C:/a%b/skarp.exe: cmd expands % even inside quotes\n");
        await Assert.That(lossy).IsTrue();
        await Assert.That(Scrub(built.Render(Shell.Sh, out _))).IsEqualTo("# sh, run from {repo}\nskarp install claude --command 'C:/a%b/skarp.exe'\n");
    }

    // ---- equality, prefill, notices ---------------------------------------------------------------------------------

    /// <summary>The target opened on the local file, set to <paramref name="spec"/> with the screen's own changes.</summary>
    HooksTarget Screen(HookSpec spec)
    {
        var t = HooksTarget.Open(_repo, Seams, Locations);
        t.Load(ClaudeScope.Local);
        t.SetProgram(spec.Command);
        if (t.Spec.Stop != spec.Stop) t.ToggleStop();
        if (t.Spec.Compact != spec.Compact) t.ToggleCompact();
        if (t.Spec.Semantic != spec.Semantic) t.ToggleSemantic();
        if (t.Spec.Gate != spec.Gate) t.ToggleGate();
        if (t.Spec.Notice != spec.Notice) t.ToggleNotices();
        t.SetMaxLines(spec.MaxLines?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");
        return t;
    }

    /// <summary>Writes <paramref name="fixture"/> to the local file, runs <paramref name="args"/> and returns the file.</summary>
    async Task<string> After(string fixture, IReadOnlyList<string> args)
    {
        Write(Local, fixture);
        if (File.Exists(Local + ".bak")) File.Delete(Local + ".bak");
        var (exit, _, error) = await Run([.. args]);
        await Assert.That(exit).IsEqualTo(0).Because(error);
        return File.ReadAllText(Local);
    }

    /// <summary>What a person types for <paramref name="spec"/>: every option spelled out, in another order than the menu's.</summary>
    static List<string> Plain(HookSpec spec, bool hadNotices)
    {
        List<string> args = ["install"];
        if (spec.MaxLines is { } n) args.AddRange(["--max-lines", n.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (spec.Notice == true) args.Add("--include-notices");
        else if (hadNotices) args.Add("--no-include-notices");
        if (spec.Gate) args.Add("--gate");
        if (spec.Semantic) args.Add("--semantic");
        if (!spec.Stop) args.Add("--no-stop");
        if (!spec.Compact) args.Add("--no-compact");
        args.Add("claude");
        if (spec.CommandGiven) args.AddRange(["--command", spec.Command]);
        args.AddRange(["--scope", "local"]);
        return args;
    }

    [Test]
    public async Task Menu_apply_the_printed_command_and_plain_install_write_the_same_bytes()
    {
        int compared = 0;
        foreach (var (name, text) in ClaudeHooksTests.Fixtures())
        {
            Write(Local, text);
            bool had = HooksTarget.Open(_repo, Seams, Locations).Of(ClaudeScope.Local).Entry?.Spec.Notice == true;
            foreach (var spec in Combos("skarp", ProjectExe))
            {
                Write(Local, text);
                var target = Screen(spec);
                var menu = await After(text, target.Args());
                var printed = await After(text, target.Build().Arguments(Shell.PowerShell).Args);
                var plain = await After(text, Plain(spec, had));
                await Assert.That(printed).IsEqualTo(menu).Because($"{name}: {string.Join(" ", target.Args())}");
                await Assert.That(plain).IsEqualTo(menu).Because($"{name}: {string.Join(" ", Plain(spec, had))}");
                compared++;
            }
            Write(Local, text);
            var remove = HooksTarget.Open(_repo, Seams, Locations);
            remove.Load(ClaudeScope.Local);
            remove.ToggleRemove();
            await Assert.That(await After(text, remove.Args())).IsEqualTo(await After(text, ["install", "claude", "--remove"])).Because(name + " remove");
        }
        await Assert.That(compared).IsEqualTo(8 * 64);
    }

    [Test]
    public async Task Opening_on_an_entry_and_applying_unchanged_writes_nothing()
    {
        foreach (var (name, text) in ClaudeHooksTests.Fixtures())
            foreach (var spec in Combos("skarp", ProjectExe).Where((_, i) => i % 5 == 0))
            {
                Write(Local, text);
                foreach (var kept in new[] { Array.Empty<string>(), ["--no-save", "--gate-user-config"] })
                {
                    // An entry as Skarp writes it (plus options added by hand), installed into the fixture.
                    var installed = await After(text, new InstallCall(ClaudeScope.Local, false, spec).Args());
                    var root = (JsonObject)JsonFile.Parse(installed, strict: true)!;
                    foreach (var f in ClaudeHooks.Find(root)) foreach (var k in kept) f.Handler["args"]!.AsArray().Add((JsonNode?)JsonValue.Create(k));
                    var withKept = ClaudeFile.FromText(Local, installed).Render(root);
                    Write(Local, withKept);
                    File.Delete(Local + ".bak");
                    var target = HooksTarget.Open(_repo, Seams, Locations);
                    await Assert.That(target.Scope).IsEqualTo(ClaudeScope.Local);
                    await Assert.That((target.Changed, target.Status(ClaudeScope.Local))).IsEqualTo((false, "installed")).Because(name);
                    await Assert.That(target.Kept).IsEquivalentTo(kept, CollectionOrdering.Matching);
                    var (exit, output, _) = await Run([.. target.Args()]);
                    await Assert.That(exit).IsEqualTo(0);
                    await Assert.That(output).Contains("Nothing to change").Because(name);
                    await Assert.That(File.ReadAllText(Local)).IsEqualTo(withKept);
                    await Assert.That(File.Exists(Local + ".bak")).IsFalse();
                }
            }
    }

    [Test]
    public async Task Notices_on_then_off_removes_the_option_and_kept_options_survive_both_routes()
    {
        // Plain command: on (with a hand-added option), a second install without a flag keeps it, --no-include-notices removes it.
        Write(Local, Entry(new HookSpec(), "--no-save"));
        await Run("install", "claude", "--include-notices");
        var args = ClaudeHooks.Find((JsonObject)JsonFile.Parse(File.ReadAllText(Local), strict: true)!).Select(f => string.Join(" ", ClaudeHooks.Invocation(f.Handler)!.Value.Args));
        await Assert.That(args).IsEquivalentTo(new[] { "check --hook claude --include-notices --no-save", "check --hook claude --include-notices --no-save", "check --hook claude --include-notices --no-save" }, CollectionOrdering.Matching);
        await Run("install", "claude");
        await Assert.That(File.ReadAllText(Local)).Contains("--include-notices");
        await Run("install", "claude", "--no-include-notices");
        var plain = File.ReadAllText(Local);
        await Assert.That(plain).DoesNotContain("--include-notices");
        await Assert.That(plain).Contains("--no-save");

        // Menu: the same steps through the screen.
        Write(Local, Entry(new HookSpec(), "--no-save"));
        var on = HooksTarget.Open(_repo, Seams, Locations);
        on.ToggleNotices();
        await Assert.That(string.Join(" ", on.Args())).IsEqualTo("install claude --include-notices");
        await Run([.. on.Args()]);
        var off = HooksTarget.Open(_repo, Seams, Locations);
        await Assert.That((off.Spec.Notice, off.Kept.Count)).IsEqualTo(((bool?)true, 1));
        off.ToggleNotices();
        await Assert.That(string.Join(" ", off.Args())).IsEqualTo("install claude --no-include-notices");
        await Run([.. off.Args()]);
        await Assert.That(File.ReadAllText(Local)).IsEqualTo(plain);
        await Assert.That(HooksTarget.Open(_repo, Seams, Locations).Args()).IsEquivalentTo(new[] { "install", "claude" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Scope_status_and_the_settings_the_hook_reads()
    {
        Settings();
        Write(Local, Entry(new HookSpec()));
        Write(User, Entry(new HookSpec { Gate = true }));
        Write(Path.Combine(_repo, ".claude", "settings.json"), "{ // comment\n}");
        var t = HooksTarget.Open(_repo, Seams, Locations);
        await Assert.That(t.Status(ClaudeScope.Local)).IsEqualTo("installed");
        await Assert.That(t.Status(ClaudeScope.User)).IsEqualTo("installed, differs");
        await Assert.That(t.Status(ClaudeScope.Project)).StartsWith("cannot be read: not strict JSON");
        t.Load(ClaudeScope.User);
        await Assert.That((t.Spec.Gate, t.Status(ClaudeScope.User), t.Status(ClaudeScope.Local))).IsEqualTo((true, "installed", "installed, differs"));
        // Without the gate the personal file counts; with it, the repository level and the defaults.
        string Read(bool gate) => string.Join("; ", t.Settings.Get(gate)!.Select(s => $"{s.Key}={s.Value} ({s.Level.Name()})"));
        const string Limits = "gate.health.maxDrop=1.0 (default); gate.health.newFileMin=6.5 (default); gate.health.floor=6.5 (default); ";
        await Assert.That(Read(false)).IsEqualTo("gate.on=*:warning, long-method:notice (repository); " + Limits + "output.checkMaxLines=3 (personal); output.gateMaxLines=20 (repository)");
        await Assert.That(Read(true)).IsEqualTo("gate.on=*:warning, long-method:notice (repository); " + Limits + "output.checkMaxLines=5 (default); output.gateMaxLines=20 (repository)");
        await Assert.That(t.Settings.HealthLimits()).IsEqualTo("drop 1.0, new file 6.5, floor 6.5");
    }

    [Test]
    public async Task Apply_from_the_menu_runs_the_installer_after_it_closes()
    {
        var terminal = new FakeTerminal(80, 24);
        var o = new StringWriter { NewLine = "\n" };
        int exit = await SkarpCommand.RunAsync(["setup", "claude"], new CommandIO
        {
            Out = o, Error = TextWriter.Null, OpenStdout = () => Stream.Null, WorkingDirectory = _repo, SettingsLocations = Locations, Install = Seams,
            Keys = new ScriptKeySource(["Down", "Down", "Down", "Down", "Down", "Space", "a"], terminal), Terminal = terminal,
        });
        await Assert.That(exit).IsEqualTo(0);
        var text = o.ToString();
        await Assert.That(text).StartsWith("Running: skarp install claude --gate\nSkarp hooks, local scope: ");
        await Assert.That(text).Contains("Write " + Local + "? (y/n) Wrote " + Local);
        var plain = File.ReadAllText(Local);
        File.Delete(Local);
        await Run("install", "claude", "--gate", "--yes");
        await Assert.That(File.ReadAllText(Local)).IsEqualTo(plain);
    }

    [Test]
    public async Task Setup_claude_takes_no_level_and_the_notices_flags_exclude_each_other()
    {
        var (exit, _, error) = await Run("setup", "claude", "personal");
        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(error).Contains("skarp setup claude edits Claude Code's hooks and takes no level");
        var (both, _, bothError) = await Run("install", "claude", "--include-notices", "--no-include-notices");
        await Assert.That((both, bothError.Split('\n')[0])).IsEqualTo((2, "skarp install: --include-notices and --no-include-notices: choose one"));
    }
}
