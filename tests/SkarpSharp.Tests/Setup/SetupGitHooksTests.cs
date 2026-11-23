using SkarpSharp.Commands;
using SkarpSharp.Configuration;
using SkarpSharp.Install;
using SkarpSharp.Setup;
using SkarpSharp.Tests.History;

namespace SkarpSharp.Tests.Setup;

/// <summary>
/// The "Git hooks" screen of <c>skarp setup</c>, built
/// like the "Claude Code hooks" screen. Golden frames through the hidden <c>--script</c> seam; the level choice row; prefill from an
/// installed entry; apply runs <c>skarp install git-hooks</c> after the menu closes and writes what the plain command
/// writes. Real git in a temporary repository with the machine's git configuration shut out; nothing reads
/// <c>~/.skarp</c> or <c>~/.claude</c>.
/// </summary>
[NotInParallel(nameof(SetupMenuTests))]
public sealed class SetupGitHooksTests : IDisposable
{
    readonly SyntheticRepo _repo = new();
    readonly string _bin = Directory.CreateTempSubdirectory("skarp-setupgit-bin-").FullName;
    readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);

    public SetupGitHooksTests()
    {
        File.WriteAllText(Path.Combine(_bin, OperatingSystem.IsWindows() ? "skarp.exe" : "skarp"), "");
        _env["PATH"] = _bin;
        _env["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
        _repo.Write("src/A.cs", "namespace N;\n");
        _repo.Commit("init", DateTimeOffset.UnixEpoch);
    }

    public void Dispose()
    {
        _repo.Dispose();
        try { Directory.Delete(_bin, true); } catch (IOException) { }
    }

    SettingsLocations Locations => new() { Variable = n => _env.GetValueOrDefault(n) };

    InstallSeams Seams => new()
    {
        Variable = n => _env.GetValueOrDefault(n), UserHome = _bin, GitIgnored = (_, _) => true, Interactive = () => true,
        Answers = new StringReader("y\n"), GitEnvironment = _repo.Environment,
        RunProgram = _ => throw new InvalidOperationException("the menu never runs a program"),
    };

    async Task<(int Exit, string Out, string Error)> Run(params string[] args)
    {
        var o = new StringWriter { NewLine = "\n" };
        var e = new StringWriter { NewLine = "\n" };
        int exit = await SkarpCommand.RunAsync(args, new CommandIO
        {
            Out = o, Error = e, OpenStdout = () => Stream.Null, WorkingDirectory = _repo.Root, SettingsLocations = Locations, Install = Seams,
        });
        return (exit, o.ToString(), e.ToString());
    }

    string Scrub(string s) => s.Replace(_repo.Root, "{repo}", StringComparison.OrdinalIgnoreCase).Replace('\\', '/');

    async Task<string> Script(string keys, params string[] args)
    {
        var script = Path.Combine(_bin, "keys-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        File.WriteAllText(script, keys);
        var (exit, output, error) = await Run(["setup", .. args, "--script", script, "--script-size", "80x24"]);
        if (exit == 2) Assert.Fail(error);
        int raw = output.IndexOf("=== raw\n", StringComparison.Ordinal), summary = output.IndexOf("\n=== summary\n", StringComparison.Ordinal);
        return Scrub(output[..raw] + output[(summary + 1)..]);
    }

    static Task Verify(string text) => Snapshots.Verify(text, "Setup/frames", null, "txt", "SKARP_UPDATE_GOLDEN");

    [Test]
    public async Task Git_hooks_screen_rows_panes_and_markdown()
    {
        // Every row's pane; push on, semantic on, strict on; the program typed; then the command for all shells.
        var keys = "Down Space Down Space Down Down Space Down Enter End Backspace Backspace Backspace Backspace Backspace X Enter Down s End Enter";
        await Verify(await Script(keys, "git"));
    }

    [Test]
    public async Task Level_choice_row_opens_the_screen_and_apply_closes_the_menu_with_the_install_arguments()
    {
        var text = await Script("End Enter Down Space a");
        await Assert.That(text).Contains("Git hooks          warn before a commit makes code worse (skarp install git-hooks)");
        await Assert.That(text).Contains("A git hook that checks the staged C# files before each commit");
        await Assert.That(text).Contains("=== apply\nskarp install git-hooks --pre-push\n");
    }

    [Test]
    public async Task With_a_baseline_the_commit_row_names_the_health_limits_in_force()
    {
        // The limits come from the repository level (a gated hook skips the machine files).
        _repo.Write(".skarp.json", """{ "version": 1, "gate": { "health": { "maxDrop": 0.5, "floor": 0 } } }""");
        _repo.Write(".skarp-baseline.json", "{}");
        var screen = GitHooksScreen.Open(_repo.Root, Seams, Locations);
        await Assert.That(screen.Pane()).Contains("worse than the staged baseline or past a health limit (drop 0.5, new file 6.5, floor off).");
        // Without settings to read (no locations given), the pane says nothing about them.
        await Assert.That(GitHooksScreen.Open(_repo.Root, Seams).Pane()).DoesNotContain("health limit");
    }

    [Test]
    public async Task An_installed_entry_is_read_back_and_applying_it_unchanged_writes_nothing()
    {
        await Run("install", "git-hooks", "--pre-push", "--semantic", "--strict", "--command", "C:/Tools/skarp.exe", "--yes");
        var target = GitHooksTarget.Open(_repo.Root, Seams);
        await Assert.That(target.Loaded).IsEqualTo(new GitHookSpec { Command = "C:/Tools/skarp.exe", CommandGiven = true, PrePush = true, Semantic = true, Strict = true });
        await Assert.That(target.Status).IsEqualTo("installed");
        await Assert.That(target.Changed).IsFalse();
        var config = File.ReadAllBytes(Path.Combine(_repo.Root, ".git", "config"));
        var args = target.Args();
        var (exit, output, _) = await Run([.. args, "--yes"]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Nothing to change");
        await Assert.That(File.ReadAllBytes(Path.Combine(_repo.Root, ".git", "config"))).IsEquivalentTo(config);

        target.TogglePush();
        await Assert.That(target.Status).IsEqualTo("installed, differs");
        await Assert.That(target.Args()).Contains("--no-pre-push");
        target.ToggleRemove();
        await Assert.That(target.Args()).IsEquivalentTo(new[] { "install", "git-hooks", "--remove" });
    }

    [Test]
    public async Task Apply_from_the_menu_runs_the_installer_after_it_closes_and_writes_what_the_plain_command_writes()
    {
        var terminal = new FakeTerminal(80, 24);
        var o = new StringWriter { NewLine = "\n" };
        int exit = await SkarpCommand.RunAsync(["setup", "git"], new CommandIO
        {
            Out = o, Error = TextWriter.Null, OpenStdout = () => Stream.Null, WorkingDirectory = _repo.Root, SettingsLocations = Locations, Install = Seams,
            Keys = new ScriptKeySource(["Down", "Space", "a"], terminal), Terminal = terminal,
        });
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(o.ToString()).StartsWith("Running: skarp install git-hooks --pre-push\nSkarp git hooks, local git config: ");
        var menu = _repo.Git("config", "--local", "--list");
        await Run("install", "git-hooks", "--remove", "--yes");
        await Run("install", "git-hooks", "--pre-push", "--yes");
        await Assert.That(_repo.Git("config", "--local", "--list")).IsEqualTo(menu);

        var (code, _, error) = await Run("setup", "git", "personal");
        await Assert.That(code).IsEqualTo(2);
        await Assert.That(error).Contains("skarp setup git edits Skarp's git hooks and takes no level");
    }

    [Test]
    public async Task The_program_entry_moves_its_cursor_and_Del_puts_a_row_back_to_its_default()
    {
        // Program row: Home, Delete, X, Right Right, Y gives XkaYrp with the cursor after Y; Enter keeps it; Del resets.
        var text = await Script("Down Down Down Down Down Enter Home Delete X Right Right Y Enter Delete Up Space Delete", "git");
        var frames = text.Split("=== frame ");
        await Assert.That(text).Contains(".|program (empty: skarp): XkaY_rp\n");
        await Assert.That(text).Contains("*program            XkaYrp");
        await Assert.That(frames[^2]).Contains(" program            skarp");
        await Assert.That(frames[^2]).Contains("*strict             on");
        await Assert.That(frames[^1]).Contains(" strict             off");
    }
}
