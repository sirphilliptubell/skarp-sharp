using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using SkarpSharp.Agent;
using SkarpSharp.Commands;
using SkarpSharp.Configuration;
using SkarpSharp.Tests.Baselines;

namespace SkarpSharp.Tests.Configuration;

/// <summary>
/// The settings levels (system, personal, repository, environment, command
/// line), the registry, merge rules and list modes, <c>null</c>, <c>useInRepositories</c>, <c>root</c>, the gate, origins,
/// <c>skarp config</c> and the fingerprint note. Every test passes its own locations; none reads the real machine files.
/// </summary>
public sealed class SettingsLevelsTests : IDisposable
{
    readonly string _base = Path.Combine(Path.GetTempPath(), "skarp-levels-" + Guid.NewGuid().ToString("N"));
    readonly string _repo, _machine;
    readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);

    public SettingsLevelsTests()
    {
        _repo = Path.Combine(_base, "repo");
        _machine = Path.Combine(_base, "machine");
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Directory.CreateDirectory(_machine);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, true); } catch (IOException) { }
    }

    string SystemFile => Path.Combine(_machine, "system.json");
    string PersonalFile => Path.Combine(_machine, "personal.json");
    string RepoFile => Path.Combine(_repo, SettingsFileReader.FileName);

    SettingsLocations Locations => new() { SystemFile = SystemFile, PersonalFile = PersonalFile, Variable = n => _env.GetValueOrDefault(n) };

    static void Write(string path, string body) => File.WriteAllText(path, "{ \"version\": 1" + (body.Length > 0 ? ", " + body : "") + " }");

    ParseResult ParseRaw(string directory, params string[] flags) => CommandLine.Parse(["config", directory, .. flags], Locations);

    SkarpOptions Parse(params string[] flags) => ParseIn(_repo, flags);

    SkarpOptions ParseIn(string directory, params string[] flags)
    {
        var r = ParseRaw(directory, flags);
        if (r.Options is null) Assert.Fail(r.Error ?? "no options");
        return r.Options!;
    }

    string Error(params string[] flags)
    {
        var r = ParseRaw(_repo, flags);
        if (r.Options is not null) Assert.Fail("expected an error");
        return r.Error!;
    }

    static EffectiveSetting Get(SkarpOptions o, string key) => o.Settings().Single(s => s.Key == key);

    // ---- registry ---------------------------------------------------------------------------------------------------

    [Test]
    public async Task Every_settings_file_property_has_a_registry_row_and_every_list_a_mode()
    {
        var missing = new List<string>();
        void Walk(Type type, string prefix)
        {
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var key = prefix + char.ToLowerInvariant(p.Name[0]) + p.Name[1..];
                if (SettingsRegistry.Find(key) is null) missing.Add(key);
                var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                if (t.Namespace == typeof(SettingsFile).Namespace && t.IsClass) Walk(t, key + ".");
            }
        }
        Walk(typeof(SettingsFile), "");
        await Assert.That(missing).IsEmpty();
        // Every list has its generated mode key, and every registry row is a real key of the file.
        foreach (var row in SettingsRegistry.All.Where(r => r.Merge == MergeKind.List))
            await Assert.That(SettingsRegistry.Find(row.Key + "Mode")).IsNotNull();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        void Collect(Type type, string prefix)
        {
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var key = prefix + char.ToLowerInvariant(p.Name[0]) + p.Name[1..];
                keys.Add(key);
                var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                if (t.Namespace == typeof(SettingsFile).Namespace && t.IsClass) Collect(t, key + ".");
            }
        }
        Collect(typeof(SettingsFile), "");
        await Assert.That(SettingsRegistry.All.Select(r => r.Key).Where(k => !keys.Contains(k)).ToList()).IsEmpty();
        // skarp config lists only registered keys (entries of rules and the like through their parent).
        await Assert.That(Parse().Settings().Where(s => SettingsRegistry.Governing(s.Key) is null).Select(s => s.Key).ToList()).IsEmpty();
    }

    // ---- each level alone and all together ----------------------------------------------------------------------------

    [Test]
    [Arguments("system")]
    [Arguments("personal")]
    [Arguments("repository")]
    public async Task Each_level_alone_sets_values_with_its_origin(string level)
    {
        var path = level switch { "system" => SystemFile, "personal" => PersonalFile, _ => RepoFile };
        Write(path, "\"top\": 7, \"preset\": \"pmd\"");
        var o = Parse();
        await Assert.That((o.Top, o.Preset)).IsEqualTo((7, "pmd"));
        await Assert.That((Get(o, "top").Origin, Get(o, "top").Path)).IsEqualTo((level, path));
        await Assert.That(Get(o, "history.window").Origin).IsEqualTo("default");
        await Assert.That(o.SettingsFiles.Select(l => l.Level.Name()).ToList()).IsEquivalentTo(new[] { level });
        await Assert.That(o.ConfigPath).IsEqualTo(level == "repository" ? RepoFile : null);
    }

    [Test]
    public async Task All_levels_together_each_key_has_the_expected_winner()
    {
        Write(SystemFile, "\"top\": 10, \"preset\": \"pmd\", \"history\": { \"window\": 6, \"bulkFlag\": 100 }, \"rules\": { \"magic-number\": false, \"empty-catch\": false }");
        Write(PersonalFile, "\"top\": 15, \"history\": { \"window\": 3 }, \"rules\": { \"empty-catch\": true }, \"report\": { \"perFile\": 9 }");
        Write(RepoFile, "\"top\": 20, \"history\": { \"bulkFlag\": 50 }");
        var o = Parse("--window", "2");
        await Assert.That((Get(o, "top").Value, Get(o, "top").Origin)).IsEqualTo(("20", "repository"));
        await Assert.That((Get(o, "preset").Value, Get(o, "preset").Origin)).IsEqualTo(("pmd", "system"));
        await Assert.That((Get(o, "history.window").Value, Get(o, "history.window").Origin)).IsEqualTo(("2", "command line"));
        await Assert.That((Get(o, "history.bulkFlag").Value, Get(o, "history.bulkFlag").Origin)).IsEqualTo(("50", "repository"));
        await Assert.That((Get(o, "report.perFile").Value, Get(o, "report.perFile").Origin)).IsEqualTo(("9", "personal"));
        // Objects merge key by key: magic-number off from system, empty-catch back on from personal.
        await Assert.That((Get(o, "rules.magic-number").Value, Get(o, "rules.magic-number").Origin)).IsEqualTo(("off", "system"));
        await Assert.That((Get(o, "rules.empty-catch").Value, Get(o, "rules.empty-catch").Origin)).IsEqualTo(("on", "personal"));
        // The chain: top 20 overrode 15 (personal) and 10 (system).
        var chain = Get(o, "top").Overridden!;
        await Assert.That(chain.Select(e => (e.Level.Name(), e.Value)).ToList()).IsEquivalentTo(new[] { ("system", (string?)"10"), ("personal", "15") });
        await Assert.That(o.SettingsFiles.Select(l => l.Level).ToList()).IsEquivalentTo(new[] { SettingOrigin.System, SettingOrigin.Personal, SettingOrigin.Repository });
        await Assert.That(o.Levels.Select(l => l.Level).ToList()).IsEquivalentTo(new[]
        {
            SettingOrigin.Default, SettingOrigin.Plugin, SettingOrigin.System, SettingOrigin.Personal, SettingOrigin.Repository, SettingOrigin.CommandLine,
        });
    }

    [Test]
    public async Task Thresholds_and_score_weights_merge_key_by_key()
    {
        Write(PersonalFile, "\"thresholds\": { \"long-method\": { \"notice\": 40, \"warning\": 90 } }, \"score\": { \"weights\": { \"long-method\": { \"notice\": 0.5, \"cap\": 3 } } }");
        Write(RepoFile, "\"thresholds\": { \"long-method\": { \"notice\": 50 } }, \"score\": { \"weights\": { \"long-method\": { \"notice\": 1 } } }");
        var o = Parse();
        await Assert.That(o.Thresholds.Order(StringComparer.Ordinal).ToList()).IsEquivalentTo(new[] { "long-method.notice=50", "long-method.warning=90" });
        await Assert.That((o.OriginText("thresholds.long-method.notice"), o.OriginText("thresholds.long-method.warning"))).IsEqualTo(("repository", "personal"));
        var w = o.Score.Weights["long-method"];
        await Assert.That((w.Notice, w.Cap)).IsEqualTo(((double?)1, (double?)3));
    }

    // ---- lists, modes, null ---------------------------------------------------------------------------------------------

    [Test]
    public async Task List_modes_across_three_levels()
    {
        Write(SystemFile, "\"exclude\": [\"a/**\"], \"history\": { \"botPatterns\": [\"sys\"] }, \"gate\": { \"on\": [\"*:notice\"] }");
        Write(PersonalFile, "\"exclude\": [\"b/**\"], \"excludeMode\": \"append\", \"history\": { \"botPatterns\": [\"me\"] }, \"gate\": { \"onMode\": \"append\", \"on\": [\"long-method:*\"] }");
        Write(RepoFile, "\"exclude\": [\"c/**\"], \"excludeMode\": \"append\", \"history\": { \"botPatternsMode\": \"append\", \"botPatterns\": [\"ci\"] }");
        var o = Parse("--exclude", "d/**");
        await Assert.That(o.Exclude.ToList()).IsEquivalentTo(new[] { "a/**", "b/**", "c/**", "d/**" });
        await Assert.That(Get(o, "exclude").Origin).IsEqualTo("command line");
        // personal replaced the system's bot patterns; the repository appended.
        await Assert.That(o.BotPatterns.ToList()).IsEquivalentTo(new[] { "me", "ci" });
        await Assert.That(o.GateOn.ToList()).IsEquivalentTo(new[] { "*:notice", "long-method:*" });
        // --gate-on keeps replacing the files' set (the documented exception).
        await Assert.That(Parse("--gate-on", "*:*").GateOn.ToList()).IsEquivalentTo(new[] { "*:*" });
        // Lists replace by default.
        Write(RepoFile, "\"exclude\": [\"c/**\"]");
        await Assert.That(Parse().Exclude.ToList()).IsEquivalentTo(new[] { "c/**" });
        // A mode without its list in the same file is an error.
        Write(RepoFile, "\"excludeMode\": \"replace\"");
        await Assert.That(Error()).Contains("excludeMode needs exclude in the same file; to empty an inherited list, write \"exclude\": []");
        Write(RepoFile, "\"semantic\": { \"targetsMode\": \"append\" }");
        await Assert.That(Error()).Contains("semantic.targetsMode needs semantic.targets in the same file");
        // [] empties a list.
        Write(RepoFile, "\"exclude\": []");
        await Assert.That(Parse().Exclude.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Semantic_targets_and_unused_allow_follow_the_modes_too()
    {
        Write(PersonalFile, "\"semantic\": { \"targets\": [\"net8.0\"] }, \"unusedCode\": { \"allow\": [\"Ns.A\"] }");
        Write(RepoFile, "\"semantic\": { \"targets\": [\"net10.0\"], \"targetsMode\": \"append\" }, \"unusedCode\": { \"allow\": [\"Ns.B\"] }");
        var o = Parse();
        await Assert.That(o.Targets.ToList()).IsEquivalentTo(new[] { "net8.0", "net10.0" });
        await Assert.That(o.UnusedAllow.ToList()).IsEquivalentTo(new[] { "Ns.B" });
    }

    [Test]
    public async Task Null_resets_a_key_to_the_default()
    {
        Write(SystemFile, "\"top\": 10, \"history\": { \"window\": 6, \"bulkFlag\": 100 }, \"rules\": { \"magic-number\": false }, \"exclude\": [\"a/**\"]");
        Write(RepoFile, "\"top\": null, \"history\": null, \"rules\": { \"magic-number\": null }, \"exclude\": null");
        var o = Parse();
        await Assert.That((o.Top, o.WindowMonths, o.BulkFlag, o.RuleOn("magic-number"), o.Exclude.Count)).IsEqualTo((20, 12, 300, true, 0));
        await Assert.That(Get(o, "top").Origin).IsEqualTo("repository");
        await Assert.That(Get(o, "top").Overridden!.Single().Value).IsEqualTo("10");
        await Assert.That(HumanReportOrigins(o, "top")).IsEqualTo($"repository {RepoFile} (reset to the default); overrides 10 from system");
    }

    static string HumanReportOrigins(SkarpOptions o, string key) => Reporting.HumanReport.OriginDetail(o, key);

    [Test]
    public async Task Origins_detail_names_the_file_and_the_values_overridden()
    {
        Write(SystemFile, "\"top\": 10");
        Write(PersonalFile, "\"top\": 15");
        var o = Parse();
        await Assert.That(HumanReportOrigins(o, "top")).IsEqualTo($"personal {PersonalFile}; overrides 10 from system");
        await Assert.That(HumanReportOrigins(Parse("--top", "3"), "top")).IsEqualTo("command line; overrides 15 from personal; overrides 10 from system");
        await Assert.That(HumanReportOrigins(o, "history.window")).IsEqualTo("default");
    }

    // ---- repository context, useInRepositories, root ----------------------------------------------------------------

    [Test]
    public async Task UseInRepositories_false_skips_a_machine_file_inside_a_repository_only()
    {
        Write(PersonalFile, "\"useInRepositories\": false, \"top\": 7");
        Write(SystemFile, "\"top\": 5");
        // Inside a git work tree (no .skarp.json): skipped; the system file applies.
        var inside = Parse();
        await Assert.That((inside.Top, Get(inside, "top").Origin)).IsEqualTo((5, "system"));
        await Assert.That(inside.Levels.Single(l => l.Level == SettingOrigin.Personal).Skipped).IsEqualTo("useInRepositories: false");
        // Outside any repository: both apply.
        var outside = Path.Combine(_base, "loose");
        Directory.CreateDirectory(outside);
        var o = ParseIn(outside);
        await Assert.That((o.Top, Get(o, "top").Origin)).IsEqualTo((7, "personal"));
        // A repository-level .skarp.json without git also makes a repository.
        Write(Path.Combine(outside, SettingsFileReader.FileName), "");
        await Assert.That(ParseIn(outside).Top).IsEqualTo(5);
    }

    [Test]
    public async Task Root_true_stops_the_walk_and_fixes_the_check_root()
    {
        Write(RepoFile, "\"top\": 3");
        var sub = Path.Combine(_repo, "src", "lib");
        Directory.CreateDirectory(Path.Combine(sub, "deep"));
        Write(Path.Combine(sub, SettingsFileReader.FileName), "\"root\": true, \"top\": 4");
        var o = ParseIn(Path.Combine(sub, "deep"));
        await Assert.That((o.Top, o.ConfigPath)).IsEqualTo((4, Path.Combine(sub, SettingsFileReader.FileName)));
        var file = Path.Combine(sub, "deep", "A.cs");
        File.WriteAllText(file, "class A {}");
        await Assert.That(FileCheck.RootOf(file)).IsEqualTo(sub);
        // root is an error in machine files; useInRepositories in .skarp.json.
        Write(PersonalFile, "\"root\": true");
        await Assert.That(Error()).IsEqualTo($"skarp: personal settings file {PersonalFile}: root: allowed only in a repository's .skarp.json (not in the personal settings file)");
        File.Delete(PersonalFile);
        Write(RepoFile, "\"useInRepositories\": false");
        await Assert.That(Error()).IsEqualTo($"skarp: settings file {RepoFile}: useInRepositories: allowed only in the system and personal settings files");
    }

    // ---- errors -----------------------------------------------------------------------------------------------------

    [Test]
    public async Task Errors_name_the_level_and_the_path()
    {
        Write(SystemFile, "\"coverage\": [\"c.xml\"]");
        await Assert.That(Error()).IsEqualTo($"skarp: system settings file {SystemFile}: coverage: a path, allowed only in a repository's .skarp.json (not in the system settings file)");
        Write(SystemFile, "\"semantic\": { \"solution\": \"a.sln\" }");
        await Assert.That(Error()).Contains($"system settings file {SystemFile}: semantic.solution: a path");
        Write(SystemFile, "\"gate\": { \"enabled\": true }");
        await Assert.That(Error()).Contains("gate.enabled: the gate is the repository's");
        File.Delete(SystemFile);
        Write(PersonalFile, "\"newerKey\": 1");
        await Assert.That(Error()).IsEqualTo($"skarp: personal settings file {PersonalFile}: newerKey: unknown key");
        Write(PersonalFile, "\"preset\": \"nope\"");
        await Assert.That(Error()).StartsWith($"skarp: personal settings file {PersonalFile}: preset: unknown preset 'nope'");
        Write(PersonalFile, "\"excludeMode\": \"merge\"");
        await Assert.That(Error()).IsEqualTo($"skarp: personal settings file {PersonalFile}: excludeMode: 'merge' is not replace or append");
        File.WriteAllText(PersonalFile, "{ \"version\": 1, ");
        await Assert.That(Error()).StartsWith($"skarp: personal settings file {PersonalFile}:");
        // A skipped file is not read, so its errors do not matter.
        await Assert.That(Parse("--no-user-config").Top).IsEqualTo(20);
    }

    // ---- gate, flags, environment -------------------------------------------------------------------------------------

    [Test]
    public async Task The_gate_skips_the_machine_files_unless_asked()
    {
        Write(SystemFile, "\"preset\": \"pmd\"");
        Write(PersonalFile, "\"top\": 7");
        var gate = Parse("--gate");
        await Assert.That((gate.Preset, gate.Top)).IsEqualTo(("default", 20));
        await Assert.That(gate.Levels.Where(l => l.Level.IsMachine()).Select(l => l.Skipped).Distinct().Single()).IsEqualTo("--gate (--gate-user-config includes it)");
        var asked = Parse("--gate", "--gate-user-config");
        await Assert.That((asked.Preset, asked.Top)).IsEqualTo(("pmd", 7));
        // gate.enabled in .skarp.json turns the gate on, so the machine files are skipped; --no-gate brings them back.
        Write(RepoFile, "\"gate\": { \"enabled\": true }");
        await Assert.That(Parse().Preset).IsEqualTo("default");
        await Assert.That(Parse("--no-gate").Preset).IsEqualTo("pmd");
    }

    [Test]
    public async Task No_user_config_and_the_environment_variables()
    {
        Write(PersonalFile, "\"top\": 7");
        Write(RepoFile, "\"top\": 3, \"preset\": \"pmd\"");
        var other = Path.Combine(_base, "other.json");
        Write(other, "\"preset\": \"doomen\"");
        await Assert.That(Parse("--no-user-config").Levels.Single(l => l.Level == SettingOrigin.Personal).Skipped).IsEqualTo("--no-user-config");
        // SKARP_CONFIG replaces the repository level only; the personal file still applies below it.
        _env[SettingsLocations.ConfigVariable] = other;
        var env = Parse();
        await Assert.That((env.Preset, env.Top, Get(env, "preset").Origin, env.ConfigPath)).IsEqualTo(("doomen", 7, "environment", other));
        // --config wins over the variable.
        var flag = Parse("--config", RepoFile);
        await Assert.That((flag.Preset, Get(flag, "preset").Origin)).IsEqualTo(("pmd", "repository"));
        _env[SettingsLocations.ConfigVariable] = Path.Combine(_base, "missing.json");
        await Assert.That(Error()).Contains("(SKARP_CONFIG)");
        _env.Remove(SettingsLocations.ConfigVariable);
        _env[SettingsLocations.NoUserConfigVariable] = "1";
        var skipped = Parse();
        await Assert.That((skipped.Top, skipped.Levels.Single(l => l.Level == SettingOrigin.Personal).Skipped)).IsEqualTo((3, "SKARP_NO_USER_CONFIG"));
        // --no-config skips every file.
        _env.Remove(SettingsLocations.NoUserConfigVariable);
        var none = Parse("--no-config");
        await Assert.That((none.Top, none.SettingsFiles.Count)).IsEqualTo((20, 0));
    }

    [Test]
    public async Task Machine_globs_are_relative_to_the_repository_root()
    {
        Write(RepoFile, "");
        Write(PersonalFile, "\"exclude\": [\"src/gen/**\", \"tools/**\", \"*.g.cs\"]");
        var src = Path.Combine(_repo, "src");
        Directory.CreateDirectory(src);
        await Assert.That(ParseIn(src).Exclude.ToList()).IsEquivalentTo(new[] { "gen/**", "*.g.cs" });
    }

    // ---- skarp config, end to end ---------------------------------------------------------------------------------------

    (int Exit, string Out, string Err, string Json) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var stdout = new MemoryStream();
        int exit = SkarpCommand.RunAsync(args, new CommandIO { Out = o, Error = e, OpenStdout = () => stdout, SettingsLocations = Locations }).GetAwaiter().GetResult();
        return (exit, o.ToString(), e.ToString(), Encoding.UTF8.GetString(stdout.ToArray()));
    }

    string Scrub(string text) => text.Replace(_base, "<base>", StringComparison.Ordinal).Replace(_base.Replace("\\", "\\\\"), "<base>", StringComparison.Ordinal)
        .Replace('\\', '/').Replace("//", "/").Replace("\r\n", "\n");

    void AllThree()
    {
        Write(SystemFile, "\"top\": 10, \"exclude\": [\"a/**\"], \"rules\": { \"magic-number\": false }");
        Write(PersonalFile, "\"top\": 15, \"excludeMode\": \"append\", \"exclude\": [\"b/**\"], \"history\": { \"window\": 6 }");
        Write(RepoFile, "\"history\": { \"window\": null }, \"thresholds\": { \"long-method\": { \"notice\": 40 } }");
    }

    [Test]
    public async Task Config_text_with_every_level_and_origins()
    {
        AllThree();
        var r = Run("config", _repo, "--origins", "--threshold", "long-method.warning=90");
        await Assert.That(r.Exit).IsEqualTo(0);
        var text = Scrub(r.Out);
        text = string.Join('\n', text.Split('\n').Select(l => l.StartsWith("skarp ", StringComparison.Ordinal) ? "skarp <version> config <base>/repo" : l));
        await Snapshots.Verify(text, Path.Combine("Golden", "expected"), null, "txt", "SKARP_UPDATE_GOLDEN");
    }

    [Test]
    public async Task Config_json_and_the_gate_view()
    {
        AllThree();
        var r = Run("config", _repo, "--format", "json");
        await Assert.That(r.Exit).IsEqualTo(0);
        var node = JsonNode.Parse(r.Json)!;
        node["toolVersion"] = "VERSION";
        await Snapshots.Verify(Scrub(node.ToJsonString(new() { WriteIndented = true })) + "\n", Path.Combine("Golden", "expected"), null, "json", "SKARP_UPDATE_GOLDEN");
        var gate = Run("config", _repo, "--gate");
        await Assert.That(gate.Out).Contains("skipped: --gate (--gate-user-config includes it)");
        await Assert.That(Run("analyze", _repo, "--origins").Err).Contains("--origins applies to the config command only");
    }

    // ---- gate independence, run metadata, fingerprint note ---------------------------------------------------------------

    static readonly string Sample = Path.Combine(FindRepo(), "tests", "SkarpSharp.Tests", "Golden", "sample");

    static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SkarpSharp.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }

    void CopySample()
    {
        foreach (var file in Directory.GetFiles(Sample, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(_repo, Path.GetRelativePath(Sample, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    [Test]
    public async Task Gate_verdict_and_output_are_the_same_with_and_without_machine_files()
    {
        CopySample();
        string[] args = ["analyze", _repo, "--parallelism", "2", "--no-save", "--gate", "--gate-on", "*:notice"];
        var without = Run(args);
        Write(SystemFile, "\"preset\": \"pmd\", \"rules\": { \"deep-nesting\": false }");
        Write(PersonalFile, "\"thresholds\": { \"long-method\": { \"notice\": 5 } }, \"gate\": { \"on\": [\"empty-catch:warning\"] }");
        var with = Run(args);
        await Assert.That(without.Exit).IsEqualTo(3);
        static string Stable(string text) => string.Join('\n', text.Split('\n').Where(l => !l.StartsWith("run:", StringComparison.Ordinal)));
        await Assert.That((with.Exit, Stable(with.Out))).IsEqualTo((without.Exit, Stable(without.Out)));
        // --gate-user-config applies them on purpose: then the verdict may differ.
        await Assert.That(Run([.. args, "--gate-user-config", "--verbose"]).Out).Contains($"personal {PersonalFile}");
    }

    [Test]
    public async Task Run_metadata_lists_the_levels_and_the_fingerprint_note_names_machine_files()
    {
        CopySample();
        Write(PersonalFile, "\"preset\": \"pmd\", \"top\": 5");
        var r = Run("analyze", _repo, "--parallelism", "2", "--no-save", "--format", "json");
        await Assert.That(r.Exit).IsEqualTo(0);
        var run = JsonNode.Parse(r.Json)!["run"]!;
        await Assert.That(run["settingsFile"]).IsNull();
        var files = run["settingsFiles"]!.AsArray().Select(f => (f!["level"]!.GetValue<string>(), f["path"]!.GetValue<string>())).ToList();
        await Assert.That(files).IsEquivalentTo(new[] { ("personal", PersonalFile), ("repository", RepoFile) });
        var notes = JsonNode.Parse(r.Json)!["baseline"]!["notes"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        await Assert.That(notes).Contains($"machine settings that affect findings applied: personal {PersonalFile} (preset); the gate skips them (--gate)");
        var human = Run("analyze", _repo, "--parallelism", "2", "--no-save", "--verbose");
        await Assert.That(human.Out).Contains($"settings:   personal {PersonalFile}; repository {RepoFile}");
        // A machine file that sets no finding key adds no note.
        Write(PersonalFile, "\"top\": 5");
        var quiet = Run("analyze", _repo, "--parallelism", "2", "--no-save", "--format", "json", "--preset", "pmd");
        await Assert.That(quiet.Json).DoesNotContain("machine settings");
    }

    [Test]
    public async Task Check_skips_machine_files_with_gate_and_caches_per_root()
    {
        Write(RepoFile, "");
        Write(PersonalFile, "\"output\": { \"checkMaxLines\": 2 }");
        var file = Path.Combine(_repo, "A.cs");
        File.WriteAllText(file, "class A { void M() { } }");
        var settings = new CheckSettings { Home = Path.Combine(_base, "home"), Now = DateTimeOffset.UtcNow, Locations = Locations, Save = false };
        var outcome = await CheckRun.RunAsync([file], settings);
        await Assert.That(outcome.MaxLines).IsEqualTo(2);
        var gated = await CheckRun.RunAsync([file], new CheckSettings { Home = settings.Home, Now = settings.Now, Locations = Locations, Save = false, SettingsArgs = ["--gate"] });
        await Assert.That(gated.MaxLines).IsEqualTo(5);
    }
}
