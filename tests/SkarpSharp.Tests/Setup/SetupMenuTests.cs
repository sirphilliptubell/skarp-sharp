using System.Text;
using SkarpSharp.Commands;
using SkarpSharp.Configuration;
using SkarpSharp.Setup;

namespace SkarpSharp.Tests.Setup;

/// <summary>
/// Golden frames of the <c>skarp setup</c> menu. Scripted keys run
/// on a fake 80x24 terminal (one run at 50x16) through the hidden <c>--script</c> seam, the same path a Native AOT
/// build is compared on; the frames are logical lines (Setup/frames/*.verified.txt). Paths are shown relative to the
/// test repository, so the frames do not depend on the temp folder.
/// </summary>
// The golden updates strip the byte order mark of every file in the folder, so the tests run one at a time.
[NotInParallel(nameof(SetupMenuTests))]
public sealed class SetupMenuTests : IDisposable
{
    readonly string _base = Path.Combine(Path.GetTempPath(), "skarp-menu-" + Guid.NewGuid().ToString("N"));
    readonly string _repo;
    readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);

    public SetupMenuTests()
    {
        _repo = Path.Combine(_base, "repo");
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Directory.CreateDirectory(Path.Combine(_repo, "machine"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, true); } catch (IOException) { }
    }

    string SystemFile => Path.Combine(_repo, "machine", "system.json");
    string PersonalFile => Path.Combine(_repo, "machine", "personal.json");
    string RepoFile => Path.Combine(_repo, SettingsFileReader.FileName);

    SettingsLocations Locations => new() { SystemFile = SystemFile, PersonalFile = PersonalFile, Variable = n => _env.GetValueOrDefault(n) };

    /// <summary>Runs the menu on a script: the exit code, the frames (snapshots) and the raw bytes.</summary>
    async Task<(int Exit, string Frames, string Raw, string Summary)> Script(string keys, string size = "80x24", params string[] args)
    {
        var script = Path.Combine(_base, "keys-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        File.WriteAllText(script, keys);
        var o = new StringWriter { NewLine = "\n" };
        var e = new StringWriter { NewLine = "\n" };
        int exit = await SkarpCommand.RunAsync(["setup", .. args, "--script", script, "--script-size", size], new CommandIO
        {
            Out = o, Error = e, OpenStdout = () => Stream.Null, WorkingDirectory = _repo, SettingsLocations = Locations,
        });
        var text = o.ToString();
        if (exit == 2) Assert.Fail(e.ToString());
        int raw = text.IndexOf("=== raw\n", StringComparison.Ordinal), summary = text.IndexOf("\n=== summary\n", StringComparison.Ordinal);
        int print = text.IndexOf("=== print\n", summary, StringComparison.Ordinal);
        _print = print < 0 ? null : Scrub(text[(print + 10)..]);
        return (exit, Scrub(text[..raw]), text[(raw + 8)..summary], Scrub(text[(summary + 13)..(print < 0 ? text.Length : print)].Trim()));
    }

    /// <summary>What the menu printed after it closed (the built command), or null.</summary>
    string? _print;

    string Scrub(string s) => s.Replace(_repo, "{repo}", StringComparison.OrdinalIgnoreCase).Replace('\\', '/');

    /// <summary>Only the frames drawn after the keys <paramref name="after"/> lists (all when null).</summary>
    static string Pick(string frames, params string[] after)
    {
        var sb = new StringBuilder();
        foreach (var frame in frames.Split("=== frame ", StringSplitOptions.RemoveEmptyEntries))
        {
            var head = frame[..frame.IndexOf('\n')];
            if (after.Length == 0 || after.Any(a => head.EndsWith(" after " + a, StringComparison.Ordinal))) sb.Append("=== frame ").Append(frame);
        }
        return sb.ToString();
    }

    static Task Verify(string text) => Snapshots.Verify(text, "Setup/frames", null, "txt", "SKARP_UPDATE_GOLDEN");

    [Test]
    public async Task Level_choice()
    {
        File.WriteAllText(PersonalFile, """{ "version": 1 }""");
        var (exit, frames, _, summary) = await Script("Up Down Down Enter Esc Esc");
        await Assert.That((exit, summary)).IsEqualTo((0, "Nothing changed."));
        await Verify(frames);
    }

    [Test]
    public async Task Each_section()
    {
        File.WriteAllText(SystemFile, """{ "version": 1, "history": { "window": 24 } }""");
        File.WriteAllText(PersonalFile, """{ "version": 1, "rules": { "magic-number": false }, "exclude": ["gen/**"], "output": { "checkMaxLines": 9 } }""");
        File.WriteAllText(RepoFile, """{ "version": 1, "history": { "window": 3 } }""");
        var keys = string.Join(' ', Enumerable.Range(0, 8).Select(i => string.Concat(Enumerable.Repeat("Down ", i)) + "Enter Esc Home"));
        var (exit, frames, _, _) = await Script(keys + " Esc", "80x24", "personal");
        await Assert.That(exit).IsEqualTo(0);
        await Verify(Pick(frames, "(start)", "Enter"));
    }

    [Test]
    public async Task Threshold_pickers_complex_method_and_large_type()
    {
        File.WriteAllText(PersonalFile, """{ "version": 1, "complexMetric": "cyclomatic" }""");
        var (exit, frames, _, _) = await Script("Down Enter Down Enter Down Esc Down Down Down Down Down Down Enter Esc Esc Esc", "80x24", "repository");
        await Assert.That(exit).IsEqualTo(0);
        await Verify(Pick(frames, "Enter", "Down"));
    }

    [Test]
    public async Task Out_of_range_entry()
    {
        var (exit, frames, _, summary) = await Script("Down Down Enter Down Enter 2000 Enter Backspace Backspace Backspace Backspace 6 Enter Esc Esc y", "80x24", "personal");
        await Assert.That((exit, summary)).IsEqualTo((0, "Quit; nothing saved."));
        await Verify(Pick(frames, "Enter", "Esc", "y"));
    }

    [Test]
    public async Task List_edit_with_mode_toggle_and_the_set_here_filter()
    {
        var keys = "Down Down Down Down Down Down Enter Down Enter a/**,b/** Enter Down Enter f s n Esc Esc y";
        var (exit, frames, _, _) = await Script(keys, "80x24", "repository");
        await Assert.That(exit).IsEqualTo(0);
        await Verify(Pick(frames, "Enter", "f", "s"));
    }

    [Test]
    public async Task Preview_with_comments_and_findings_warnings_then_save()
    {
        File.WriteAllText(PersonalFile, "{\n  // my settings\n  \"version\": 1,\n  \"history\": { \"window\": 6 }\n}\n");
        File.WriteAllText(RepoFile, """{ "version": 1, "rules": { "complex-method": true } }""");
        var (exit, frames, _, summary) = await Script("Enter Enter Esc s y", "80x24", "personal");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(summary).IsEqualTo("Saved {repo}/machine/personal.json (1 change); the original is kept as {repo}/machine/personal.json.bak.");
        await Assert.That(File.Exists(PersonalFile + ".bak")).IsTrue();
        await Assert.That(File.ReadAllText(PersonalFile)).IsEqualTo("{\n  \"version\": 1,\n  \"history\": {\n    \"window\": 6\n  },\n  \"rules\": {\n    \"complex-method\": false\n  }\n}\n");
        await Verify(Pick(frames, "s"));
    }

    [Test]
    public async Task Quit_without_saving_asks_first()
    {
        var (exit, frames, _, summary) = await Script("Enter Enter Esc Esc n Esc y", "80x24", "personal");
        await Assert.That((exit, summary)).IsEqualTo((0, "Quit; nothing saved."));
        await Assert.That(File.Exists(PersonalFile)).IsFalse();
        await Verify(Pick(frames, "Esc", "n"));
    }

    [Test]
    public async Task Ctrl_c_quits_at_once_with_130()
    {
        var (exit, frames, raw, summary) = await Script("Enter Enter Ctrl+C", "80x24", "personal");
        await Assert.That((exit, summary)).IsEqualTo((130, "Cancelled (Ctrl+C); nothing saved."));
        await Assert.That(File.Exists(PersonalFile)).IsFalse();
        await Assert.That(raw).EndsWith("\u001b[0m\u001b[?25h\n"); // attributes reset and the cursor shown again
        await Verify(Pick(frames, "Enter"));
    }

    [Test]
    public async Task Small_window_50x16_and_too_small()
    {
        var (exit, frames, _, _) = await Script("Enter Down Enter Resize:39x16 Down Resize:50x16 Esc Esc Esc", "50x16");
        await Assert.That(exit).IsEqualTo(0);
        await Verify(frames);
    }

    [Test]
    public async Task No_color_draws_no_sgr_codes()
    {
        var (_, coloured, colouredRaw, _) = await Script("Esc", "80x24", "personal");
        await Assert.That(colouredRaw).Contains("\u001b[7m");
        _env["NO_COLOR"] = "1";
        var (_, frames, raw, _) = await Script("Esc", "80x24", "personal");
        await Assert.That(frames).IsEqualTo(coloured); // the logical frames are the same
        foreach (var sgr in new[] { "\u001b[0m", "\u001b[1m", "\u001b[2m", "\u001b[7m", "\u001b[31m" }) await Assert.That(raw).DoesNotContain(sgr);
        _env.Remove("NO_COLOR");
        _env["TERM"] = "dumb";
        var (_, _, dumb, _) = await Script("Esc", "80x24", "personal");
        await Assert.That(dumb).IsEqualTo(raw);
        await Verify(Scrub(raw).Replace("\u001b", "<ESC>", StringComparison.Ordinal).Replace("\r", "<CR>", StringComparison.Ordinal));
    }

    [Test]
    public async Task A_resize_moves_up_by_the_rows_the_last_frame_takes_after_reflow()
    {
        var model = SetupCommand.Open(SettingOrigin.Personal, _repo, null, Locations);
        var terminal = new FakeTerminal(80, 24);
        var frames = new List<Frame>();
        var menu = new SetupMenu(new SetupMenuStart([], 0, model, _ => model),
            new MenuConsole(new ScriptKeySource(["Resize:50x24", "Down", "Esc"], terminal), terminal, onFrame: frames.Add));
        await Assert.That(menu.Run()).IsEqualTo(0);
        // The first frame at width 80: lines cut at 79, the highlighted one padded to 79; each takes ceil(n / 50) rows at 50.
        var lengths = frames[0].Lines.Select(l => Math.Min(l.Text.Length, 79) is var n && l.Style == LineStyle.Highlight ? 79 : n).ToList();
        int rows = FrameWriter.RowsAfterReflow(lengths, 50);
        await Assert.That(rows).IsGreaterThan(frames[0].Lines.Count);
        await Assert.That(terminal.MoveUps[0]).IsEqualTo(rows - 1);
        // A plain redraw moves up by the line count.
        await Assert.That(terminal.MoveUps[1]).IsEqualTo(frames[1].Lines.Count - 1);
    }

    // ---- the command line target -------------------------------------------------------------------------

    [Test]
    public async Task Command_line_from_the_level_choice_run_options_and_markdown()
    {
        File.WriteAllText(RepoFile, """{ "version": 1, "history": { "window": 12 } }""");
        var keys = "Down Enter Enter Enter Down Enter Down Down Down Down Enter Esc Down Down Down Enter Down Enter 6 Enter Esc s Home Down Down Down Enter";
        var (exit, frames, _, summary) = await Script(keys);
        await Assert.That((exit, summary)).IsEqualTo((0, ""));
        await Verify(Pick(frames, "(start)", "Enter", "Home", "6") + "=== print\n" + _print);
    }

    [Test]
    public async Task Command_line_history_shows_only_its_sections_and_prints_for_one_shell()
    {
        var keys = "Down Down Down Enter Down Enter Down Down Enter f Esc s Home Down Enter";
        var (exit, frames, _, _) = await Script(keys, "80x24", "command");
        await Assert.That(exit).IsEqualTo(0);
        await Verify(Pick(frames, "(start)", "Enter", "f", "Home") + "=== print\n" + _print);
    }

    [Test]
    public async Task Command_line_esc_asks_before_quitting_with_changes()
    {
        var (exit, _, _, summary) = await Script("Enter Enter Down Enter Down Enter Esc Esc y", "80x24", "command");
        await Assert.That((exit, summary, _print)).IsEqualTo((0, "Quit; nothing printed.", (string?)null));
    }

    [Test]
    public async Task The_io_seams_drive_the_menu_and_no_terminal_exits_2()
    {
        var o = new StringWriter { NewLine = "\n" };
        var terminal = new FakeTerminal(80, 24);
        int exit = await SkarpCommand.RunAsync(["setup", "personal"], new CommandIO
        {
            Out = o, Error = TextWriter.Null, OpenStdout = () => Stream.Null, WorkingDirectory = _repo, SettingsLocations = Locations,
            Keys = new ScriptKeySource(["Enter", "Ctrl+C"], terminal), Terminal = terminal,
        });
        await Assert.That((exit, o.ToString())).IsEqualTo((130, "Cancelled (Ctrl+C); nothing saved.\n"));
        await Assert.That(terminal.Output.ToString()).StartsWith("\u001b[?25l");
        if (!Console.IsInputRedirected && !Console.IsOutputRedirected) return; // an interactive test host: nothing to check
        var e = new StringWriter();
        exit = await SkarpCommand.RunAsync(["setup", "personal"], new CommandIO
        {
            Out = TextWriter.Null, Error = e, OpenStdout = () => Stream.Null, WorkingDirectory = _repo, SettingsLocations = Locations,
        });
        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(e.ToString()).Contains("Use the flag form: skarp setup --level <level> --set <key>=<value>");
    }
}
