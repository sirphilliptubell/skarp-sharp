using System.Text.Json.Nodes;
using SkarpSharp.Commands;
using SkarpSharp.Configuration;
using SkarpSharp.Findings;
using SkarpSharp.Setup;

namespace SkarpSharp.Tests.Setup;

/// <summary>
/// The command builder: the quoting per shell, the choice
/// between a dedicated flag and <c>--setting</c>, the parse-back check on every editable row, the notes, the paths and
/// the flag form <c>skarp setup --command</c>.
/// </summary>
public sealed class CommandBuilderTests : IDisposable
{
    readonly string _base = Path.Combine(Path.GetTempPath(), "skarp-builder-" + Guid.NewGuid().ToString("N"));
    readonly string _repo;

    public CommandBuilderTests()
    {
        _repo = Path.Combine(_base, "repo");
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, true); } catch (IOException) { }
    }

    string RepoFile => Path.Combine(_repo, SettingsFileReader.FileName);

    static JsonObject Doc(params (string Key, string Value)[] sets)
    {
        var doc = new JsonObject();
        foreach (var (k, v) in sets) SettingText.SetPath(doc, k, SettingText.Parse(k, v));
        return doc;
    }

    CommandBuilder Build(string command, JsonObject changes, Dictionary<string, string>? options = null) => CommandBuilder.Build(new CommandRequest
    {
        Command = command, Options = options ?? [], Changes = changes, Cwd = _repo, Locations = SettingsLocations.None,
    });

    string Line(Shell shell, string command, params (string, string)[] sets) => Build(command, Doc(sets)).Line(shell, out _).Line;

    // ---- quoting ------------------------------------------------------------------------------------------------------

    [Test]
    [Arguments("plain-1.2/x:y=z,w+v@u", "plain-1.2/x:y=z,w+v@u", "plain-1.2/x:y=z,w+v@u", "'plain-1.2/x:y=z,w+v@u'")]
    [Arguments("a b", "'a b'", "\"a b\"", "'a b'")]
    [Arguments("it's", "'it'\\''s'", "\"it's\"", "'it''s'")]
    [Arguments("a\u2019b", "'a\u2019b'", "\"a\u2019b\"", "'a\u2019\u2019b'")]
    [Arguments("$HOME", "'$HOME'", "\"$HOME\"", "'$HOME'")]
    [Arguments("a&b|c<d>e^f(g)", "'a&b|c<d>e^f(g)'", "\"a&b|c<d>e^f(g)\"", "'a&b|c<d>e^f(g)'")]
    [Arguments("C:\\x y\\", "'C:\\x y\\'", "\"C:\\x y\\\\\"", null)]
    [Arguments("a\"b", "'a\"b'", "^\"a\\^\"b^\"", null)]
    [Arguments("a%b", "'a%b'", null, "'a%b'")]
    [Arguments("@file", "@file", "@file", "'@file'")]
    [Arguments("src\\x", "'src\\x'", "src\\x", "src\\x")]
    public async Task Quoting_per_shell(string value, string sh, string? cmd, string? powershell)
    {
        await Assert.That(ShellQuote.Sh(value)).IsEqualTo(sh);
        await Assert.That(ShellQuote.Cmd(value)).IsEqualTo(cmd);
        await Assert.That(ShellQuote.PowerShell(value)).IsEqualTo(powershell);
    }

    [Test]
    public async Task Never_an_empty_argument()
    {
        foreach (var shell in new[] { Shell.Sh, Shell.Cmd, Shell.PowerShell })
        {
            var line = Line(shell, "analyze", ("exclude", "[]"), ("history.since", "null"));
            await Assert.That(line).DoesNotContain("\"\"").And.DoesNotContain("''");
        }
    }

    // ---- dedicated flags or --setting ---------------------------------------------------------------------------------

    [Test]
    public async Task Dedicated_flag_when_it_is_exact_else_setting()
    {
        var line = Line(Shell.Sh, "analyze",
            ("preset", "pmd"), ("thresholds.long-method.notice", "60"), ("rules.magic-number", "off"), ("history.window", "6"),
            ("include", "a/**"), ("history.allHistory", "false"), ("history.includeBots", "true"), ("saveLastRun", "true"),
            ("top", "null"), ("gate.on", "long-method:warning"), ("semantic.targets", "all"), ("score.decay", "0.5"),
            ("exclude", "[]"), ("history.botPatterns", "x"));
        // The exact order is registry order within each group: dedicated flags first, then --setting.
        await Assert.That(line).StartsWith("skarp analyze . --preset pmd ");
        foreach (var part in new[] { "--window 6", "--include-bots", "--gate-on long-method:warning", "--all-targets", "--rule magic-number=off",
            "--threshold long-method.notice=60", "--setting 'include=a/**'", "--setting 'exclude=[]'", "--setting top=null",
            "--setting history.allHistory=false", "--setting history.botPatterns=x", "--setting saveLastRun=true", "--setting score.decay=0.5" })
            await Assert.That(line).Contains(part);
        await Assert.That(line.IndexOf("--threshold", StringComparison.Ordinal)).IsLessThan(line.IndexOf("--setting", StringComparison.Ordinal));
    }

    [Test]
    public async Task Append_mode_never_uses_the_replacing_flag()
    {
        var line = Line(Shell.Sh, "analyze", ("gate.on", "long-method:warning"), ("gate.onMode", "append"));
        await Assert.That(line).IsEqualTo("skarp analyze . --setting gate.on=long-method:warning --setting gate.onMode=append");
    }

    [Test]
    public async Task Unchanged_builder_prints_the_bare_command()
    {
        await Assert.That(Line(Shell.PowerShell, "hotspots")).IsEqualTo("skarp hotspots .");
    }

    /// <summary>Every editable row with a value of its kind (and null): the printed form parses back to the same
    /// effective settings as the all-<c>--setting</c> form, so a dedicated flag is never used where it is not exact.</summary>
    [Test]
    public async Task Every_row_round_trips_through_the_parser()
    {
        int built = 0, refused = 0;
        var errors = new List<string>();
        foreach (var row in SettingsRegistry.Editable)
        {
            if (SettingText.DescribesFile(row.Key) || SettingsRegistry.ListOfMode(row.Key) is not null) continue;
            var command = (row.Commands & SettingCommands.Analyze) != 0 ? "analyze" : null;
            if (command is null) continue;
            foreach (var value in Samples(row))
            {
                try { _ = Build(command, Doc((row.Key, value))); built++; }
                catch (SetupException ex) when (!ex.Message.StartsWith("internal error", StringComparison.Ordinal)) { refused++; }
                catch (SetupException ex) { errors.Add($"{row.Key}={value}: {ex.Message}"); }
                catch (SettingTextException) { refused++; }
            }
        }
        await Assert.That(errors).IsEmpty();
        await Assert.That(built).IsGreaterThan(300);
        Console.WriteLine($"{built} rows and values built and checked, {refused} refused as invalid values");
    }

    static IEnumerable<string> Samples(SettingsKey row)
    {
        yield return "null";
        switch (row.Kind)
        {
            case SettingKind.Boolean: yield return "true"; yield return "false"; break;
            case SettingKind.Integer: yield return ((long)(row.Min ?? 0) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture); break;
            case SettingKind.Number:
                yield return row.Rule is not null ? "7" : "0.5";
                if (row.Rule is not null) yield return "off";
                break;
            case SettingKind.Choice: foreach (var a in row.Allowed ?? []) yield return a; break;
            case SettingKind.List:
                yield return "[]";
                yield return row.Key switch
                {
                    "gate.on" => "long-method:warning,*:notice",
                    "semantic.targets" => "net10.0",
                    "symbols" => "DEBUG,TRACE",
                    "history.botPatterns" => "^ci-bot$,x|y",
                    _ => "a/**,b c/**",
                };
                break;
            default:
                if (row.Key == "history.since") yield return "2026-01-01";
                else if (row.Key == "semantic.solution") yield return "src/a b.sln";
                else if (row.Rule is { } rule && row.Part is { } part && Presets.Default.Rules.GetValueOrDefault(rule)?.Parameter(part) is { } p) yield return p;
                break;
        }
    }

    [Test]
    public async Task Threshold_level_with_metric_values_uses_one_flag_per_metric()
    {
        var doc = new JsonObject();
        SettingText.SetPath(doc, "thresholds.large-type.notice.wmc", JsonValue.Create(30));
        SettingText.SetPath(doc, "thresholds.large-type.notice.sourceLines", JsonValue.Create(400));
        var line = Build("analyze", doc).Line(Shell.Sh, out _).Line;
        await Assert.That(line).IsEqualTo("skarp analyze . --threshold large-type.notice.wmc=30 --threshold large-type.notice.sourceLines=400");
    }

    // ---- notes ------------------------------------------------------------------------------------------------------

    [Test]
    public async Task Percent_in_cmd_and_quote_in_powershell_are_left_out_with_a_note()
    {
        var b = Build("analyze", Doc(("exclude", "a%b/**"), ("excludeMode", "append"), ("history.botPatterns", "a\"b"), ("preset", "pmd")));
        var (cmd, cmdNotes) = b.Line(Shell.Cmd, out bool cmdLossy);
        await Assert.That(cmd).IsEqualTo("skarp analyze . --preset pmd --setting ^\"history.botPatterns=a\\^\"b^\"");
        await Assert.That(cmdLossy).IsTrue();
        await Assert.That(cmdNotes).Contains("exclude is left out: cmd expands % even inside quotes; set it in .skarp.json (skarp setup repository) or use bash/sh");
        var (ps, psNotes) = b.Line(Shell.PowerShell, out bool psLossy);
        await Assert.That(ps).IsEqualTo("skarp analyze . --preset pmd --setting 'exclude=a%b/**' --setting excludeMode=append");
        await Assert.That(psLossy).IsTrue();
        await Assert.That(string.Join("\n", psNotes)).Contains("history.botPatterns is left out: Windows PowerShell 5.1 and PowerShell 7 pass a \" to programs differently");
        var (sh, shNotes) = b.Line(Shell.Sh, out bool shLossy);
        await Assert.That((shLossy, shNotes.Count)).IsEqualTo((false, 0));
        await Assert.That(sh).Contains("--setting 'history.botPatterns=a\"b'");
    }

    [Test]
    public async Task Bang_long_line_msys_and_commas_in_list_items_get_notes()
    {
        var bang = Build("analyze", Doc(("exclude", "a!b/**"))).Line(Shell.Cmd, out _);
        await Assert.That(bang.Notes).Contains("if delayed expansion is on, ! in exclude is lost");
        var longLine = Build("analyze", Doc(("exclude", string.Join(",", Enumerable.Range(0, 40).Select(i => $"generated/folder{i}/**"))))).Line(Shell.Sh, out _);
        await Assert.That(longLine.Line.Length).IsGreaterThan(CommandBuilder.LongLine);
        await Assert.That(longLine.Notes).Contains("long command: consider putting these settings in .skarp.json (skarp setup repository)");
        var msys = Build("analyze", Doc(("exclude", "/abs/**"))).Line(Shell.Sh, out _);
        await Assert.That(string.Join("\n", msys.Notes)).Contains("MSYS_NO_PATHCONV=1");
        var doc = new JsonObject { ["history"] = new JsonObject { ["botPatterns"] = new JsonArray("a{1,3}") } };
        var comma = Build("analyze", doc);
        await Assert.That(comma.Notes).Contains("history.botPatterns is left out: its value cannot be written on a command line; set it in .skarp.json (skarp setup repository)");
    }

    // ---- paths --------------------------------------------------------------------------------------------------------

    [Test]
    public async Task Paths_are_relative_with_the_shell_separators()
    {
        var b = Build("analyze", Doc(("semantic.solution", Path.Combine(_repo, "src", "a.sln"))),
            new() { ["directory"] = Path.Combine(_repo, "src"), ["output"] = "out/x.json", ["baseline"] = "base\\b.json" });
        await Assert.That(b.Line(Shell.Sh, out _).Line).IsEqualTo("skarp analyze src --output out/x.json --baseline base/b.json --solution src/a.sln");
        await Assert.That(b.Line(Shell.PowerShell, out _).Line).IsEqualTo("skarp analyze src --output out\\x.json --baseline base\\b.json --solution src\\a.sln");
        await Assert.That(b.Line(Shell.Cmd, out _).Line).IsEqualTo("skarp analyze src --output out\\x.json --baseline base\\b.json --solution src\\a.sln");
    }

    // ---- flag form ----------------------------------------------------------------------------------------------------

    async Task<(int Exit, string Out, string Error)> Setup(params string[] args)
    {
        var o = new StringWriter { NewLine = "\n" };
        var e = new StringWriter { NewLine = "\n" };
        int exit = await SkarpCommand.RunAsync(["setup", .. args], new CommandIO
        {
            Out = o, Error = e, OpenStdout = () => Stream.Null, WorkingDirectory = _repo, SettingsLocations = SettingsLocations.None,
        });
        return (exit, o.ToString(), e.ToString());
    }

    string Scrub(string s) => s.Replace(_repo, "{repo}", StringComparison.OrdinalIgnoreCase);

    [Test]
    [NotInParallel(nameof(SetupMenuTests))] // shares the golden folder whose update strips byte order marks
    public async Task Flag_form_prints_each_shell_and_markdown()
    {
        File.WriteAllText(RepoFile, """{ "version": 1, "preset": "pmd" }""");
        var args = new[] { "--command", "analyze", "--set", "preset=checkstyle", "--set", "exclude=gen/**,obj/**", "--set", "history.window=6",
            "--option", "format=sarif", "--option", "output=out/skarp.sarif" };
        var all = new System.Text.StringBuilder();
        foreach (var shell in new[] { "powershell", "cmd", "sh", "markdown" })
        {
            var (exit, output, error) = await Setup([.. args, "--shell", shell]);
            await Assert.That((exit, error)).IsEqualTo((0, ""));
            all.Append("=== ").Append(shell).Append('\n').Append(Scrub(output));
        }
        await Snapshots.Verify(all.ToString().Replace('\\', '/'), "Setup/frames", null, "txt", "SKARP_UPDATE_GOLDEN");
    }

    [Test]
    public async Task Flag_form_exit_codes()
    {
        await Assert.That((await Setup("--command", "analyze", "--set", "exclude=a%b/**", "--shell", "cmd")).Exit).IsEqualTo(2);
        await Assert.That((await Setup("--command", "analyze", "--set", "exclude=a%b/**", "--shell", "markdown")).Exit).IsEqualTo(0);
        await Assert.That((await Setup("--command", "analyze", "--set", "exclude=a%b/**", "--shell", "sh")).Exit).IsEqualTo(0);
        var bad = await Setup("--command", "check");
        await Assert.That((bad.Exit, bad.Error)).IsEqualTo((2, "skarp setup: unknown command 'check' (analyze, baseline, hotspots or history)\n"));
        var notUsed = await Setup("--command", "history", "--set", "preset=pmd");
        await Assert.That((notUsed.Exit, notUsed.Error)).IsEqualTo((2, "skarp setup: preset: not used by skarp history\n"));
        var option = await Setup("--command", "history", "--option", "gate=on");
        await Assert.That((option.Exit, option.Error)).IsEqualTo((2, "skarp setup: gate: not a run option of skarp history\n"));
        await Assert.That((await Setup("--command", "analyze", "--level", "personal")).Exit).IsEqualTo(2);
        await Assert.That((await Setup("--shell", "cmd")).Exit).IsEqualTo(2);
        await Assert.That((await Setup("--command", "analyze", "--shell", "fish")).Exit).IsEqualTo(2);
        var file = await Setup("--command", "analyze", "--set", "overrides=[]");
        await Assert.That(file.Error).Contains("overrides: describes a file");
    }

    [Test]
    public async Task Gate_option_resolves_as_the_gate()
    {
        var personal = Path.Combine(_base, "personal.json");
        File.WriteAllText(personal, """{ "version": 1, "preset": "pmd" }""");
        var locations = new SettingsLocations { PersonalFile = personal, Variable = _ => null };
        var target = CommandTarget.Open("analyze", _repo, null, locations);
        await Assert.That(SetupRows.View(target.Model, SettingsRegistry.Find("preset")!).Origin).IsEqualTo("from personal");
        target.SetOption("gate", "on");
        await Assert.That(SetupRows.View(target.Model, SettingsRegistry.Find("preset")!).Origin).IsEqualTo("Skarp default");
    }
}
