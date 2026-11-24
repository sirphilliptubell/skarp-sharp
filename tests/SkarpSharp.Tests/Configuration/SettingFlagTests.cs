using System.Text;
using SkarpSharp.Commands;
using SkarpSharp.Configuration;
using SkarpSharp.Findings;

namespace SkarpSharp.Tests.Configuration;

/// <summary>
/// The <c>--setting &lt;key&gt;=&lt;value&gt;</c>
/// level (order, <c>null</c>, <c>[]</c>, append, origins, errors, the gate, per-path overrides) and the completeness of
/// the commands per key and the flag classification against the parser.
/// </summary>
public sealed class SettingFlagTests : IDisposable
{
    readonly string _base = Path.Combine(Path.GetTempPath(), "skarp-setting-" + Guid.NewGuid().ToString("N"));
    readonly string _repo;

    public SettingFlagTests()
    {
        _repo = Path.Combine(_base, "repo");
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Directory.CreateDirectory(Path.Combine(_base, "machine"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, true); } catch (IOException) { }
    }

    string PersonalFile => Path.Combine(_base, "machine", "personal.json");
    string RepoFile => Path.Combine(_repo, SettingsFileReader.FileName);
    SettingsLocations Locations => new() { PersonalFile = PersonalFile, Variable = _ => null };

    static void Write(string path, string body) => File.WriteAllText(path, "{ \"version\": 1" + (body.Length > 0 ? ", " + body : "") + " }");

    ParseResult Parse(params string[] args) => CommandLine.Parse(args, Locations);

    SkarpOptions Options(params string[] args) => Parse(args).Options ?? throw new InvalidOperationException(Parse(args).Error);

    [Test]
    public async Task Above_the_files_below_the_dedicated_flags()
    {
        Write(PersonalFile, "\"history\": { \"window\": 24 }, \"preset\": \"pmd\"");
        Write(RepoFile, "\"history\": { \"window\": 12 }");
        var o = Options("config", _repo, "--setting", "history.window=6", "--setting", "preset=checkstyle");
        await Assert.That(o.WindowMonths).IsEqualTo(6);
        await Assert.That(o.Preset).IsEqualTo("checkstyle");
        await Assert.That(o.OriginOf("history.window")).IsEqualTo(SettingOrigin.CommandLine);
        await Assert.That(o.OriginChain("history.window")[^1].Path).IsEqualTo("--setting history.window=6");
        // The dedicated flag wins, whatever the order on the line.
        var flag = Options("config", _repo, "--window", "3", "--setting", "history.window=6");
        await Assert.That(flag.WindowMonths).IsEqualTo(3);
        // Later --setting wins.
        await Assert.That(Options("config", _repo, "--setting", "top=5", "--setting", "top=7").Top).IsEqualTo(7);
    }

    [Test]
    public async Task Null_empty_list_and_append_follow_the_file_rules()
    {
        Write(RepoFile, "\"top\": 5, \"exclude\": [\"a/**\"], \"history\": { \"botPatterns\": [\"x\"] }");
        await Assert.That(Options("config", _repo, "--setting", "top=null").Top).IsEqualTo(20);
        await Assert.That(Options("config", _repo, "--setting", "exclude=[]").Exclude.Count).IsEqualTo(0);
        await Assert.That(Options("config", _repo, "--setting", "exclude=b/**,c/**").Exclude.ToList()).IsEquivalentTo(new[] { "b/**", "c/**" }, CollectionOrdering.Matching);
        var appended = Options("config", _repo, "--setting", "exclude=b/**", "--setting", "excludeMode=append");
        await Assert.That(appended.Exclude.ToList()).IsEquivalentTo(new[] { "a/**", "b/**" }, CollectionOrdering.Matching);
        await Assert.That(appended.OriginChain("exclude")[^1].Mode).IsEqualTo("append");
        // The dedicated append flag adds to the --setting list.
        var both = Options("config", _repo, "--setting", "exclude=b/**", "--exclude", "c/**");
        await Assert.That(both.Exclude.ToList()).IsEquivalentTo(new[] { "b/**", "c/**" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Only_setting_values_without_any_file()
    {
        var o = Options("analyze", _repo, "--setting", "rules.magic-number=off", "--setting", "thresholds.long-method.notice=70");
        await Assert.That(o.RuleOn("magic-number")).IsFalse();
        await Assert.That(o.RuleSettings()["long-method"].Level(Severity.Notice)).IsNotNull();
        await Assert.That(o.OriginText("thresholds.long-method.notice")).IsEqualTo("command line");
    }

    [Test]
    [Arguments("overrides=[]", "--setting overrides: describes a file; set it in .skarp.json")]
    [Arguments("useInRepositories=false", "--setting useInRepositories: describes a file; set it in the system or personal settings file")]
    [Arguments("root=true", "--setting root: describes a file")]
    [Arguments("version=1", "--setting version: describes a file")]
    [Arguments("nothing=1", "--setting nothing: unknown key")]
    [Arguments("history.nothing=1", "--setting history.nothing: unknown key")]
    [Arguments("rules.nothing=off", "--setting rules.nothing: unknown rule")]
    [Arguments("output.checkMaxLines=3", "--setting output.checkMaxLines: not used by skarp analyze")]
    [Arguments("history.window=0", "--setting history.window: 0 is out of range (1 to 1200)")]
    [Arguments("preset=nope", "--setting preset: 'nope' is not default")]
    [Arguments("excludeMode=append", "--setting excludeMode needs --setting exclude=<list> in the same command line")]
    [Arguments("history.botPatterns=(", "--setting: history.botPatterns: '(' is not a valid regular expression")]
    [Arguments("gate.on=nope", "--setting: gate.on:")]
    [Arguments("thresholds.long-method.notice.wmc=3", "--setting: thresholds.long-method")]
    [Arguments("novalue", "--setting novalue: expected <key>=<value>")]
    public async Task Errors_name_the_flag_and_the_key(string setting, string message)
    {
        var r = Parse("analyze", _repo, "--setting", setting);
        await Assert.That(r.ExitCode).IsEqualTo(2);
        await Assert.That(r.Error ?? "").Contains(message);
    }

    [Test]
    public async Task Keys_the_command_does_not_use_are_refused()
    {
        var r = Parse("history", _repo, "--setting", "preset=pmd");
        await Assert.That(r.Error).IsEqualTo("--setting preset: not used by skarp history");
        await Assert.That(Parse("history", _repo, "--setting", "history.window=3").Options!.WindowMonths).IsEqualTo(3);
    }

    [Test]
    public async Task Gate_setting_skips_the_machine_files_like_the_flag()
    {
        Write(PersonalFile, "\"preset\": \"pmd\"");
        Write(RepoFile, "");
        await Assert.That(Options("config", _repo).Preset).IsEqualTo("pmd");
        await Assert.That(Options("config", _repo, "--gate").Preset).IsEqualTo("default");
        var o = Options("config", _repo, "--setting", "gate.enabled=true");
        await Assert.That((o.Preset, o.Gate)).IsEqualTo(("default", true));
        // --setting values still apply to a gated run.
        await Assert.That(Options("config", _repo, "--gate", "--setting", "preset=ndepend").Preset).IsEqualTo("ndepend");
    }

    [Test]
    public async Task Setting_rules_apply_after_the_per_path_overrides()
    {
        Write(RepoFile, "\"overrides\": [ { \"files\": [\"legacy/**\"], \"thresholds\": { \"long-method\": { \"notice\": 200 } } } ]");
        var o = Options("analyze", _repo, "--setting", "thresholds.long-method.notice=50");
        var legacy = o.PathRules().For("legacy/a.cs", false);
        var plain = o.PathRules().For("src/a.cs", false);
        await Assert.That(Text(legacy)).IsEqualTo(Text(plain));
        static string Text(RuleSettings s) => SkarpSharp.Setup.ThresholdLabels.Label(s["long-method"].Level(Severity.Notice));
    }

    [Test]
    public async Task Config_shows_the_flag_text_as_origin()
    {
        Write(RepoFile, "\"history\": { \"window\": 12 }");
        var o = new StringWriter();
        var e = new StringWriter();
        var stdout = new MemoryStream();
        int exit = await SkarpCommand.RunAsync(["config", _repo, "--origins", "--setting", "history.window=6"],
            new CommandIO { Out = o, Error = e, OpenStdout = () => stdout, SettingsLocations = Locations });
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(o.ToString()).Contains("command line --setting history.window=6; overrides 12 from repository");
        stdout.SetLength(0);
        exit = await SkarpCommand.RunAsync(["config", _repo, "--format", "json", "--setting", "history.window=6"],
            new CommandIO { Out = o, Error = e, OpenStdout = () => stdout, SettingsLocations = Locations });
        var json = Encoding.UTF8.GetString(stdout.ToArray());
        await Assert.That(json).Contains("\"origin\": \"command line\",\n      \"path\": \"--setting history.window=6\"".Replace("\n", Environment.NewLine));
    }

    [Test]
    public async Task No_setting_no_change_and_setting_equals_the_dedicated_flag()
    {
        Write(RepoFile, "\"history\": { \"window\": 12 }");
        var plain = Options("analyze", _repo);
        await Assert.That(plain.CommandLineRules).IsNull();
        var viaSetting = Options("analyze", _repo, "--setting", "preset=pmd", "--setting", "rules.magic-number=off", "--setting", "thresholds.long-method.notice=70");
        var viaFlags = Options("analyze", _repo, "--preset", "pmd", "--rule", "magic-number=off", "--threshold", "long-method.notice=70");
        await Assert.That(SkarpSharp.Setup.CommandBuilder.Snapshot(viaSetting)).IsEqualTo(SkarpSharp.Setup.CommandBuilder.Snapshot(viaFlags));
    }

    // ---- completeness -------------------------------------------------------------------------------------

    static readonly string[] Commands = ["analyze", "baseline", "hotspots", "history", "config"];

    /// <summary>For every command and key, <c>--setting key=null</c> is accepted exactly when the row lists the command.</summary>
    [Test]
    public async Task Setting_is_accepted_exactly_when_the_row_lists_the_command()
    {
        int checkedPairs = 0;
        var wrong = new List<string>();
        foreach (var command in Commands)
            foreach (var row in SettingsRegistry.Editable.Concat(SettingsRegistry.All.Where(r => r.Kind == SettingKind.Object)))
            {
                if (SettingText.DescribesFile(row.Key)) continue;
                List<string> args = [command, _repo, "--setting", row.Key + "=null"];
                if (SettingsRegistry.ListOfMode(row.Key) is { } list) args.AddRange(["--setting", list.Key + "=[]"]);
                var r = Parse([.. args]);
                bool listed = (row.Commands & SettingsRegistry.Command(command)) != 0;
                bool accepted = r.Options is not null;
                if (accepted != listed) wrong.Add($"{command} {row.Key}: listed {listed}, accepted {accepted} ({r.Error})");
                checkedPairs++;
            }
        await Assert.That(wrong).IsEmpty();
        await Assert.That(checkedPairs).IsGreaterThan(500);
    }

    [Test]
    public async Task The_keys_that_describe_a_file_are_never_accepted()
    {
        foreach (var command in Commands)
            foreach (var key in new[] { "overrides", "useInRepositories", "root", "version" })
                await Assert.That(Parse(command, _repo, "--setting", key + "=null").Error ?? "").Contains("describes a file");
    }

    [Test]
    public async Task Every_parser_flag_is_classified()
    {
        var names = CommandLine.FlagNames.ToList();
        await Assert.That(names.Where(n => !RunOptions.Flags.ContainsKey(n)).ToList()).IsEmpty();
        await Assert.That(RunOptions.Flags.Keys.Where(n => !names.Contains(n)).ToList()).IsEmpty();
        foreach (var (flag, c) in RunOptions.Flags)
            if (c.Key is { } key) await Assert.That(SettingsRegistry.Find(key)).IsNotNull().Because(flag);
        foreach (var o in RunOptions.All.Where(o => o.Flag.Length > 0))
            await Assert.That(RunOptions.Flags[o.Flag].RunOption).IsTrue().Because(o.Flag);
        // Every key the registry lists for a command is either settable there or shown by config.
        foreach (var row in SettingsRegistry.All)
            if (!SettingText.DescribesFile(row.Key) && row.Commands != SettingCommands.None)
                await Assert.That((row.Commands & SettingCommands.Config) != 0).IsTrue().Because(row.Key);
    }
}
