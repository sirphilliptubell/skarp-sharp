using System.Text.Json.Nodes;
using SkarpSharp.Commands;
using SkarpSharp.Configuration;
using SkarpSharp.Findings;
using SkarpSharp.Setup;

namespace SkarpSharp.Tests.Setup;

/// <summary>
/// Tests of the registry fields, the per-level model, the
/// writer and flag form of <c>skarp setup</c>, and the threshold labels. Every test uses its own folders; none reads the
/// real machine files.
/// </summary>
public sealed class SetupTests : IDisposable
{
    readonly string _base = Path.Combine(Path.GetTempPath(), "skarp-setup-" + Guid.NewGuid().ToString("N"));
    readonly string _repo;
    readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);

    public SetupTests()
    {
        _repo = Path.Combine(_base, "repo");
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Directory.CreateDirectory(Path.Combine(_base, "machine"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, true); } catch (IOException) { }
    }

    string SystemFile => Path.Combine(_base, "machine", "system.json");
    string Personal => Path.Combine(_base, "machine", "personal.json");
    string RepoFile => Path.Combine(_repo, SettingsFileReader.FileName);

    SettingsLocations Locations => new() { SystemFile = SystemFile, PersonalFile = Personal, Variable = n => _env.GetValueOrDefault(n) };

    Task<(int Exit, string Out, string Error)> RunIn(string cwd, params string[] args) => RunWith(cwd, Locations, args);

    static async Task<(int Exit, string Out, string Error)> RunWith(string cwd, SettingsLocations locations, params string[] args)
    {
        var o = new StringWriter { NewLine = "\n" };
        var e = new StringWriter { NewLine = "\n" };
        int exit = await SkarpCommand.RunAsync(["setup", .. args], new CommandIO
        {
            Out = o, Error = e, OpenStdout = () => Stream.Null, WorkingDirectory = cwd, SettingsLocations = locations,
        });
        return (exit, o.ToString(), e.ToString());
    }

    Task<(int Exit, string Out, string Error)> Run(params string[] args) => RunIn(_repo, args);

    LevelModel Open(SettingOrigin level) => SetupCommand.Open(level, _repo, null, Locations);

    // ---- registry -------------------------------------------------------------------------------------------

    [Test]
    public async Task Every_editable_row_has_a_description_and_every_numeric_row_a_range()
    {
        var rows = SettingsRegistry.Editable.ToList();
        await Assert.That(rows.Where(r => r.Description.Length == 0).Select(r => r.Key).ToList()).IsEmpty();
        await Assert.That(rows.Where(r => r.Kind is SettingKind.Integer or SettingKind.Number && (r.Min is null || r.Max is null)).Select(r => r.Key).ToList()).IsEmpty();
        await Assert.That(rows.Where(r => r.Kind == SettingKind.Choice && r.Allowed is not { Count: > 0 }).Select(r => r.Key).ToList()).IsEmpty();
        // Generated rows: one per rule, a threshold level per numeric rule and level, one per parameter.
        await Assert.That(rows.Count(r => r.Key.StartsWith("rules.", StringComparison.Ordinal))).IsEqualTo(SettingsRegistry.Rules.Count());
        await Assert.That(SettingsRegistry.Row("thresholds.large-type.notice.wmc")?.Key).IsEqualTo("thresholds.large-type.notice");
        await Assert.That(SettingsRegistry.Row("thresholds.duplicate-code.copyLines")?.Kind).IsEqualTo(SettingKind.Integer);
        await Assert.That(SettingsRegistry.Row("thresholds.brain-method.notice")).IsNull(); // a composite has no threshold
    }

    static string Sample(string list) => list switch { "gate.on" => "*:warning", "semantic.targets" => "net10.0", _ => "x" };

    static SettingOrigin LevelFor(SettingsKey row) => (row.Levels & LevelScope.Repository) != 0 ? SettingOrigin.Repository : SettingOrigin.Personal;

    [Test]
    public async Task Every_choice_value_is_accepted_by_the_loader_and_a_value_outside_is_rejected()
    {
        var failures = new List<string>();
        foreach (var row in SettingsRegistry.Editable.Where(r => r.Kind == SettingKind.Choice))
        {
            var level = LevelFor(row);
            var list = SettingsRegistry.ListOfMode(row.Key);
            foreach (var value in row.Allowed!)
            {
                string[] args = ["--level", level.Name(), .. list is null ? Array.Empty<string>() : ["--set", $"{list.Key}={Sample(list.Key)}"], "--set", $"{row.Key}={value}", "--dry-run"];
                var (exit, _, error) = await Run(args);
                if (exit != 0) failures.Add($"{row.Key}={value}: {error}");
            }
            // Outside the allowed values: the writer and the loader both reject it.
            var (outExit, _, _) = await Run("--level", level.Name(), "--set", $"{row.Key}=not-a-value", "--dry-run");
            if (outExit != 2) failures.Add($"{row.Key}=not-a-value: writer accepted it");
            var file = new JsonObject { ["version"] = 1 };
            if (list is not null) SettingsDocument.Set(file, list.Key, new JsonArray(Sample(list.Key)));
            SettingsDocument.Set(file, row.Key, "not-a-value");
            var path = level == SettingOrigin.Repository ? RepoFile : Personal;
            File.WriteAllText(path, file.ToJsonString());
            if (CommandLine.Parse(["config", _repo], Locations).Options is not null) failures.Add($"{row.Key}=not-a-value: loader accepted it");
            File.Delete(path);
        }
        await Assert.That(failures).IsEmpty();
    }

    // ---- the per-level model --------------------------------------------------------------------------------

    [Test]
    public async Task The_model_shows_inherited_values_with_their_level_and_overrides_from_above()
    {
        File.WriteAllText(SystemFile, """{ "version": 1, "history": { "window": 24 }, "top": 5 }""");
        File.WriteAllText(Personal, """{ "version": 1, "history": { "window": 6 }, "exclude": ["p/**"] }""");
        File.WriteAllText(RepoFile, """{ "version": 1, "history": { "window": 3 }, "gate": { "enabled": true } }""");
        var m = Open(SettingOrigin.Personal);
        await Assert.That(m.Below.Select(b => b.Level).ToList()).IsEquivalentTo(new[] { SettingOrigin.System });
        await Assert.That(m.Above.Select(b => b.Level).ToList()).IsEquivalentTo(new[] { SettingOrigin.Repository });
        await Assert.That(m.InheritedValue("history.window")?.Level).IsEqualTo(SettingOrigin.System);
        await Assert.That(m.AboveValue("history.window")?.Value?.ToJsonString()).IsEqualTo("3");
        var view = SetupRows.View(m, SettingsRegistry.Find("history.window")!);
        await Assert.That((view.Value, view.Origin, view.Above)).IsEqualTo(("6", "set here", "(repository overrides: 3)"));
        var top = SetupRows.View(m, SettingsRegistry.Find("top")!);
        await Assert.That((top.Value, top.Origin)).IsEqualTo(("5", "from system"));
        // Not as the gate: gate.enabled in the repository file does not hide the machine files from the editor.
        var repo = Open(SettingOrigin.Repository);
        await Assert.That(repo.Below.Select(b => b.Level).ToList()).IsEquivalentTo(new[] { SettingOrigin.System, SettingOrigin.Personal });
        await Assert.That(repo.Above).IsEmpty();
        // A broken target can still be opened (the target's content is left out of the resolve).
        File.WriteAllText(Personal, """{ "version": 1, "preset": "nope" }""");
        await Assert.That(Open(SettingOrigin.Personal).Here("preset", out _)?.ToString()).IsEqualTo("nope");
    }

    // ---- writer and flag form -------------------------------------------------------------------------------

    [Test]
    public async Task Key_order_is_kept_and_new_keys_go_at_the_end_of_their_object()
    {
        File.WriteAllText(RepoFile, "{\n  \"version\": 1,\n  \"top\": 5,\n  \"history\": { \"window\": 3, \"bulkFlag\": 10 },\n  \"exclude\": [\"a/**\"]\n}\n");
        var (exit, _, error) = await Run("--level", "repository", "--set", "history.window=6", "--set", "history.includeBots=true", "--set", "thresholds.large-type.notice.wmc=30");
        await Assert.That(exit).IsEqualTo(0).Because(error);
        await Assert.That(File.ReadAllText(RepoFile)).IsEqualTo("""
            {
              "version": 1,
              "top": 5,
              "history": {
                "window": 6,
                "bulkFlag": 10,
                "includeBots": true
              },
              "exclude": [
                "a/**"
              ],
              "thresholds": {
                "large-type": {
                  "notice": {
                    "wmc": 30
                  }
                }
              }
            }

            """.Replace("\r\n", "\n"));
    }

    [Test]
    public async Task The_three_health_limits_are_edited_saved_and_reloaded()
    {
        // Three rows next to the gate, 0 shown as off; the existing write path saves them.
        var general = SetupRows.Rows(Open(SettingOrigin.Repository), SettingSection.General).Select(r => r.Key).ToList();
        await Assert.That(general).Contains("gate.health.maxDrop").And.Contains("gate.health.newFileMin").And.Contains("gate.health.floor");
        var (exit, _, error) = await Run("--level", "repository", "--set", "gate.health.maxDrop=0.5", "--set", "gate.health.newFileMin=7", "--set", "gate.health.floor=0");
        await Assert.That(exit).IsEqualTo(0).Because(error);
        await Assert.That(File.ReadAllText(RepoFile)).Contains("\"health\": {\n      \"maxDrop\": 0.5,\n      \"newFileMin\": 7,\n      \"floor\": 0\n    }");
        var m = Open(SettingOrigin.Repository);
        string Value(string key) => SetupRows.View(m, SettingsRegistry.Find(key)!).Value;
        await Assert.That((Value("gate.health.maxDrop"), Value("gate.health.newFileMin"), Value("gate.health.floor"))).IsEqualTo(("0.5", "7", "off"));
        var (rangeExit, _, rangeError) = await Run("--level", "repository", "--set", "gate.health.maxDrop=9.5");
        await Assert.That(rangeExit).IsEqualTo(2);
        await Assert.That(rangeError).Contains("gate.health.maxDrop").And.Contains("0 to 9");
        // Back to the defaults.
        (exit, _, error) = await Run("--level", "repository", "--unset", "gate.health.maxDrop", "--unset", "gate.health.newFileMin", "--unset", "gate.health.floor");
        await Assert.That(exit).IsEqualTo(0).Because(error);
        m = Open(SettingOrigin.Repository);
        await Assert.That((Value("gate.health.maxDrop"), Value("gate.health.newFileMin"), Value("gate.health.floor"))).IsEqualTo(("1.0", "6.5", "6.5"));
    }

    [Test]
    public async Task Comments_are_warned_about_and_the_original_is_kept_as_bak()
    {
        var original = "{\n  // mine\n  \"version\": 1, /* two */\n  \"top\": 5,\n}\n";
        File.WriteAllText(RepoFile, original);
        var (exit, output, _) = await Run("--level", "repository", "--set", "top=7");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains($"saving removes 2 comments; the original is kept as {RepoFile}.bak");
        await Assert.That(File.ReadAllText(RepoFile + ".bak")).IsEqualTo(original);
        await Assert.That(File.ReadAllText(RepoFile)).IsEqualTo("{\n  \"version\": 1,\n  \"top\": 7\n}\n");
        // Without comments there is no .bak.
        File.Delete(RepoFile + ".bak");
        _ = await Run("--level", "repository", "--set", "top=8");
        await Assert.That(File.Exists(RepoFile + ".bak")).IsFalse();
    }

    [Test]
    public async Task Null_unset_and_reset()
    {
        File.WriteAllText(Personal, """{ "version": 1, "preset": "pmd", "exclude": ["a/**"], "excludeMode": "append", "top": 3 }""");
        var (exit, output, error) = await Run("--level", "personal", "--set", "preset=null", "--unset", "exclude", "--unset", "top");
        await Assert.That(exit).IsEqualTo(0).Because(error);
        await Assert.That(output).Contains("  preset: pmd → (Skarp default)");
        await Assert.That(output).Contains("  exclude: a/** → (removed)");
        await Assert.That(output).Contains("  excludeMode: append → (removed)");
        await Assert.That(output).Contains(SetupWriter.FindingsWarning);
        await Assert.That(File.ReadAllText(Personal).Replace(" ", "")).IsEqualTo("{\n\"version\":1,\n\"preset\":null\n}\n");
        (exit, _, _) = await Run("--level", "personal", "--reset", "--set", "top=4");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.ReadAllText(Personal)).IsEqualTo("{\n  \"version\": 1,\n  \"top\": 4\n}\n");
    }

    [Test]
    public async Task Crlf_and_tab_indentation_are_kept()
    {
        File.WriteAllText(RepoFile, "{\r\n\t\"version\": 1,\r\n\t\"top\": 5\r\n}");
        var (exit, _, _) = await Run("--level", "repository", "--set", "history.window=6");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.ReadAllText(RepoFile)).IsEqualTo("{\r\n\t\"version\": 1,\r\n\t\"top\": 5,\r\n\t\"history\": {\r\n\t\t\"window\": 6\r\n\t}\r\n}\r\n");
    }

    [Test]
    public async Task Invalid_values_and_a_mode_without_its_list_write_nothing()
    {
        File.WriteAllText(RepoFile, """{ "version": 1 }""");
        foreach (var set in new[] { "history.window=2000", "preset=nope", "rules.no-such-rule=false", "thresholds.long-method.notice=abc", "excludeMode=append", "history.since=yesterday-ish", "useInRepositories=false" })
        {
            var (exit, _, error) = await Run("--level", "repository", "--set", set);
            await Assert.That(exit).IsEqualTo(2).Because(set);
            await Assert.That(error).Contains("nothing written").Because(set);
        }
        var (_, _, modeError) = await Run("--level", "repository", "--set", "excludeMode=append");
        await Assert.That(modeError).Contains("excludeMode needs exclude in the same file");
        var (_, _, rangeError) = await Run("--level", "repository", "--set", "history.window=2000");
        await Assert.That(rangeError).Contains("history.window: 2000 is out of range (1 to 1200)");
        var (_, _, levelError) = await Run("--level", "repository", "--set", "useInRepositories=false");
        await Assert.That(levelError).Contains("useInRepositories: allowed only in the system and personal settings files");
        await Assert.That(File.ReadAllText(RepoFile)).IsEqualTo("""{ "version": 1 }""");
    }

    [Test]
    public async Task A_new_repository_file_is_created_at_the_git_root()
    {
        var sub = Path.Combine(_repo, "src", "lib");
        Directory.CreateDirectory(sub);
        var (exit, output, error) = await RunIn(sub, "--level", "repository", "--set", "top=9");
        await Assert.That(exit).IsEqualTo(0).Because(error);
        await Assert.That(output).StartsWith($"creates {RepoFile} (repository): 1 change");
        await Assert.That(File.ReadAllText(RepoFile)).IsEqualTo("{\n  \"version\": 1,\n  \"top\": 9\n}\n");
        // Outside git: in the current folder.
        var outside = Path.Combine(_base, "plain");
        Directory.CreateDirectory(outside);
        (exit, _, _) = await RunIn(outside, "--level", "repository", "--set", "top=9");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(outside, SettingsFileReader.FileName))).IsTrue();
    }

    [Test]
    public async Task A_failed_write_exits_2_and_print_still_works()
    {
        var blocker = Path.Combine(_base, "blocker");
        File.WriteAllText(blocker, "a file, not a folder");
        var personal = Path.Combine(blocker, "settings.json");
        var locations = new SettingsLocations { SystemFile = SystemFile, PersonalFile = personal, Variable = _ => null };
        var (exit, _, error) = await RunWith(_repo, locations, "--level", "personal", "--set", "top=4");
        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(error).Contains($"cannot write {personal}");
        await Assert.That(error).Contains("--print");
        var (printExit, output, preview) = await RunWith(_repo, locations, "--level", "personal", "--set", "top=4", "--print");
        await Assert.That(printExit).IsEqualTo(0);
        await Assert.That(output).IsEqualTo("{\n  \"version\": 1,\n  \"top\": 4\n}\n");
        await Assert.That(preview).Contains("top: (inherit) → 4");
    }

    [Test]
    public async Task Dry_run_and_print_write_nothing()
    {
        var (exit, output, _) = await Run("--level", "personal", "--set", "top=4", "--dry-run");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("dry run: nothing written");
        (exit, output, _) = await Run("--level", "personal", "--set", "top=4", "--print");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("\"top\": 4");
        await Assert.That(File.Exists(Personal)).IsFalse();
        // Nothing to change: exit 0, no file.
        (exit, output, _) = await Run("--level", "personal", "--unset", "top");
        await Assert.That((exit, output.Trim())).IsEqualTo((0, $"Nothing to change in {Personal}."));
        await Assert.That(File.Exists(Personal)).IsFalse();
    }

    [Test]
    public async Task Usage_errors_exit_2()
    {
        foreach (var args in new[] { new[] { "--set", "top=4" }, ["--level", "folder"], ["--bogus"], ["personal", "--level", "system"], ["--level", "personal", "--set", "top"] })
        {
            var (exit, _, error) = await Run(args);
            await Assert.That(exit).IsEqualTo(2).Because(string.Join(' ', args));
            await Assert.That(error).StartsWith("skarp setup:");
        }
        var (helpExit, help, _) = await Run("--help");
        await Assert.That(helpExit).IsEqualTo(0);
        await Assert.That(help).Contains("skarp setup --level <level>");
    }

    [Test]
    public async Task Preview_warns_when_a_level_above_sets_the_key()
    {
        File.WriteAllText(RepoFile, """{ "version": 1, "history": { "window": 3 } }""");
        var (_, output, _) = await Run("--level", "personal", "--set", "history.window=8", "--dry-run");
        await Assert.That(output).Contains("  history.window: (inherit) → 8 (no effect here: repository sets 3)");
    }

    // ---- comparison conversion -----------------------------------------------------------------------------

    [Test]
    public async Task A_choice_is_written_in_the_comparison_of_the_level_it_overrides()
    {
        // Default preset: cognitive "over 15", so "10 or more" writes 9.
        var (_, over, _) = await Run("--level", "personal", "--set", "thresholds.complex-method.notice=9", "--dry-run");
        await Assert.That(over).Contains("  thresholds.complex-method.notice: (preset: 16 or more) → 10 or more (writes 9)");
        var basis = ThresholdLabels.Basis(RuleIds.ComplexMethod, Severity.Notice, LevelModel.Thresholds([]))!;
        await Assert.That(ThresholdLabels.Write([new("cognitive", 10, true)], basis).ToJsonString()).IsEqualTo("9");
        // PMD: cognitive "at least 15", so "10 or more" writes 10.
        File.WriteAllText(Personal, """{ "version": 1, "preset": "pmd" }""");
        var (_, atLeast, _) = await Run("--level", "repository", "--set", "thresholds.complex-method.notice=10", "--dry-run");
        await Assert.That(atLeast).Contains("  thresholds.complex-method.notice: (preset: 15 or more) → 10 or more");
        await Assert.That(atLeast).DoesNotContain("(writes");
        var pmd = ThresholdLabels.Basis(RuleIds.ComplexMethod, Severity.Notice, LevelModel.Thresholds(new JsonObject { ["preset"] = "pmd" }))!;
        await Assert.That(ThresholdLabels.Write([new("cognitive", 10, true)], pmd).ToJsonString()).IsEqualTo("10");
        // Fractional metrics keep their value.
        var near = ThresholdLabels.Basis(RuleIds.DuplicateCodeNear, Severity.Notice, LevelModel.Thresholds([]))!;
        await Assert.That(ThresholdLabels.Label([.. near.AnyOf.Select(ThresholdLabels.Normalize)], near.AllOf)).IsEqualTo("0.75 or more similarity and 10 or more lines together");
    }

    // ---- threshold labels -----------------------------------------------------------------------------------

    IReadOnlyList<string> Labels(string key, string? personal = null)
    {
        if (personal is not null) File.WriteAllText(Personal, "{ \"version\": 1, " + personal + " }");
        var m = Open(SettingOrigin.Repository);
        return [.. SetupRows.Options(m, SettingsRegistry.Row(key)!).Select(o => o.Label + (o.Marker is { } mk ? " • " + mk : ""))];
    }

    [Test]
    public async Task Complex_method_labels_cyclomatic_and_cognitive()
    {
        await Assert.That(Labels("thresholds.complex-method.notice", "\"complexMetric\": \"cyclomatic\"")).IsEquivalentTo(new[]
        {
            "9 or more cyclomatic  (CodeScene)",
            "10 or more cyclomatic  (PMD [Java])",
            "11 or more cyclomatic  (Clean-CaDET, Checkstyle [Java], Alves [Java])",
            "16 or more cyclomatic  (Skarp default, ReportGenerator, NDepend) • (from your preset)",
            "off", SetupRows.Inherit, SetupRows.SkarpDefault, SetupRows.Custom,
        }, CollectionOrdering.Matching);
        await Assert.That(Labels("thresholds.complex-method.warning")).IsEquivalentTo(new[]
        {
            "20 or more cyclomatic  (Alves [Java])",
            "31 or more cyclomatic  (Skarp default, NDepend) • (from your preset)",
            "100 or more cyclomatic  (CodeScene)",
            "off", SetupRows.Inherit, SetupRows.SkarpDefault, SetupRows.Custom,
        }, CollectionOrdering.Matching);
        // Microsoft counts cyclomatic its own way: named in the pane only.
        var m = Open(SettingOrigin.Repository);
        var custom = SetupRows.Options(m, SettingsRegistry.Row("thresholds.complex-method.notice")!)[^1];
        await Assert.That(custom.Pane).Contains("26 or more cyclomatic (Microsoft counting) (Microsoft)");
        // Cognitive (the default metric); its warning level is published by no vendor.
        File.Delete(Personal);
        await Assert.That(Labels("thresholds.complex-method.notice")).IsEquivalentTo(new[]
        {
            "15 or more cognitive  (PMD [Java])",
            "16 or more cognitive  (Skarp default) • (from your preset)",
            "off", SetupRows.Inherit, SetupRows.SkarpDefault, SetupRows.Custom,
        }, CollectionOrdering.Matching);
        await Assert.That(Labels("thresholds.complex-method.warning")).IsEquivalentTo(new[]
        {
            "off (Skarp default) • (from your preset)", SetupRows.Inherit, SetupRows.Custom,
        }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Single_metric_vendor_values_are_offered_on_an_any_of_level()
    {
        // A vendor's parameters-only limit is offered over Skarp's parameters-or-constructor level;
        // choosing it writes only the parameters number and keeps the constructor condition.
        var labels = Labels("thresholds.too-many-parameters.notice");
        await Assert.That(labels).IsEquivalentTo(new[]
        {
            "4 or more parameters; keeps 6 or more constructor parameters  (Doomen)",
            "5 or more parameters, or 6 or more constructor parameters  (Skarp default, CodeScene) • (from your preset)",
            "6 or more parameters; keeps 6 or more constructor parameters  (NDepend)",
            "8 or more parameters; keeps 6 or more constructor parameters  (Checkstyle [Java])",
            "10 or more parameters; keeps 6 or more constructor parameters  (PMD [Java])",
            "off", SetupRows.Inherit, SetupRows.SkarpDefault, SetupRows.Custom,
        }, CollectionOrdering.Matching);
        var m = Open(SettingOrigin.Repository);
        var ndepend = SetupRows.Options(m, SettingsRegistry.Row("thresholds.too-many-parameters.notice")!)[2];
        await Assert.That(ndepend.Write!.ToJsonString()).IsEqualTo("{\"parameters\":5}");
        // Once written (here at the personal level, viewed from the repository level), the choice is marked as current.
        await Assert.That(Labels("thresholds.too-many-parameters.notice", "\"thresholds\": { \"too-many-parameters\": { \"notice\": { \"parameters\": 5 } } }")[2])
            .IsEqualTo("6 or more parameters; keeps 6 or more constructor parameters  (NDepend) • (from personal)");
    }

    [Test]
    public async Task Long_method_labels_follow_the_measured_unit()
    {
        // Physical lines (default): only line-based sets; statement sets are named in the pane.
        await Assert.That(Labels("thresholds.long-method.notice")).IsEquivalentTo(new[]
        {
            "37 or more all lines  (Alves [Java])",
            "51 or more all lines  (Skarp default, Clean-CaDET) • (from your preset)",
            "71 or more all lines  (CodeScene)",
            "151 or more all lines  (Checkstyle [Java])",
            "off", SetupRows.Inherit, SetupRows.SkarpDefault, SetupRows.Custom,
        }, CollectionOrdering.Matching);
        var m = Open(SettingOrigin.Repository);
        var custom = SetupRows.Options(m, SettingsRegistry.Row("thresholds.long-method.notice")!)[^1];
        await Assert.That(custom.Pane).Contains("Not offered while this level measures all lines: 16 or more statements (Doomen); 21 or more statements (NDepend); 60 or more statements (PMD [Java])");
        // Statements (preset doomen): the statement sets.
        await Assert.That(Labels("thresholds.long-method.notice", "\"preset\": \"doomen\"")).IsEquivalentTo(new[]
        {
            "16 or more statements  (Doomen) • (from your preset)",
            "21 or more statements  (NDepend)",
            "60 or more statements  (PMD [Java])",
            "off", SetupRows.Inherit, SetupRows.SkarpDefault, SetupRows.Custom,
        }, CollectionOrdering.Matching);
        // Source lines.
        await Assert.That(Labels("thresholds.long-method.warning", "\"longMethodLines\": \"source\"")[0]).IsEqualTo("64 or more source lines  (Alves [Java])");
    }

    [Test]
    public async Task Large_type_any_of_and_duplicate_code_all_of()
    {
        await Assert.That(Labels("thresholds.large-type.notice")).IsEquivalentTo(new[]
        {
            "101 or more source lines, or 21 or more WMC  (Skarp default, Clean-CaDET) • (from your preset)",
            "off", SetupRows.Inherit, SetupRows.SkarpDefault, SetupRows.Custom,
        }, CollectionOrdering.Matching);
        var m = Open(SettingOrigin.Repository);
        var row = SettingsRegistry.Row("thresholds.large-type.notice")!;
        var choice = SetupRows.Options(m, row)[0];
        await Assert.That(choice.Write!.ToJsonString()).IsEqualTo("""{"sourceLines":100,"wmc":20}""");
        await Assert.That(choice.Pane).Contains("21 or more methods, or 16 or more fields (NDepend)");
        await Assert.That(Labels("thresholds.large-type.notice", "\"preset\": \"ndepend\"")[0]).IsEqualTo("21 or more methods, or 16 or more fields  (NDepend) • (from your preset)");
        File.Delete(Personal);
        await Assert.That(Labels("thresholds.duplicate-code.notice")).IsEquivalentTo(new[]
        {
            "50 or more tokens and 5 or more lines together  (jscpd)",
            "100 or more tokens and 10 or more lines together  (Skarp default) • (from your preset)",
            "off", SetupRows.Inherit, SetupRows.SkarpDefault, SetupRows.Custom,
        }, CollectionOrdering.Matching);
        await Assert.That(Labels("thresholds.duplicate-code.warning")[0]).IsEqualTo("50 or more lines, or 3 or more copies  (Skarp default, jscpd) • (from your preset)");
        var dup = SetupRows.Options(Open(SettingOrigin.Repository), SettingsRegistry.Row("thresholds.duplicate-code.notice")!)[0];
        await Assert.That(dup.Write!.ToJsonString()).IsEqualTo("""{"tokens":50,"lines":5}""");
    }

    [Test]
    public async Task Structural_rules_parameters_and_the_preset_row()
    {
        await Assert.That(Labels("thresholds.empty-catch.notice")).IsEquivalentTo(new[]
        {
            "on (Skarp default) • (from your preset)", "off", SetupRows.Inherit,
        }, CollectionOrdering.Matching);
        await Assert.That(SettingsRegistry.Row("thresholds.namespace-cycle.notice")).IsNull(); // warning only
        await Assert.That(Labels("thresholds.bumpy-road.depth")).IsEquivalentTo(new[]
        {
            "2  (Skarp default, CodeScene) • (from your preset)", SetupRows.Inherit, SetupRows.SkarpDefault, SetupRows.Custom,
        }, CollectionOrdering.Matching);
        await Assert.That(Labels("thresholds.duplicate-code.lowContent")).IsEquivalentTo(new[]
        {
            "skip  (Skarp default, jscpd) • (from your preset)", "report", SetupRows.Inherit, SetupRows.SkarpDefault,
        }, CollectionOrdering.Matching);
        var preset = Labels("preset");
        await Assert.That(preset[0]).IsEqualTo("default  default (composite) • (Skarp default)");
        await Assert.That(preset.Count).IsEqualTo(Presets.All.Count + 2);
        var m = Open(SettingOrigin.Repository);
        var pmd = SetupRows.Options(m, SettingsRegistry.Find("preset")!).Single(o => o.Label.StartsWith("pmd", StringComparison.Ordinal));
        await Assert.That(pmd.Pane).Contains($"Choosing it changes {SetupRows.ChangedRows(LevelModel.Thresholds(m.Stack()), "pmd")} threshold rows.");
        await Assert.That(SetupRows.ChangedRows(LevelModel.Thresholds(m.Stack()), "default")).IsEqualTo(0);
    }

    [Test]
    public async Task The_current_value_is_marked_set_here()
    {
        File.WriteAllText(RepoFile, """{ "version": 1, "thresholds": { "complex-method": { "notice": 14 } } }""");
        await Assert.That(Labels("thresholds.complex-method.notice")).Contains("15 or more cognitive  (PMD [Java]) • (set here)");
        File.WriteAllText(RepoFile, """{ "version": 1, "thresholds": { "complex-method": { "notice": 20 } } }""");
        await Assert.That(Labels("thresholds.complex-method.notice")[^1]).IsEqualTo("custom… (now 21 or more cognitive) • (set here)");
        File.WriteAllText(RepoFile, """{ "version": 1, "thresholds": { "complex-method": { "notice": "off" } } }""");
        await Assert.That(Labels("thresholds.complex-method.notice")).Contains("off • (set here)");
        var m = Open(SettingOrigin.Repository);
        var custom = SetupRows.CustomThreshold(m, RuleIds.ComplexMethod, Severity.Notice, "10", out _);
        await Assert.That(custom!.ToJsonString()).IsEqualTo("9");
        _ = SetupRows.CustomThreshold(m, RuleIds.ComplexMethod, Severity.Notice, "0", out var error);
        await Assert.That(error).IsEqualTo("0 is out of range: cognitive takes 1 to 100000");
    }
}
