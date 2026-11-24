using SkarpSharp.Configuration;
using SkarpSharp.Findings;
using SkarpSharp.Reporting;

namespace SkarpSharp.Tests.Configuration;

/// <summary>The options model: precedence default &lt; file &lt; flag for every setting, settings file validation, discovery.</summary>
public sealed class OptionsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "skarp-options-" + Guid.NewGuid().ToString("N"));

    public OptionsTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, ".git")); // stops the settings-file search here
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string Config(string json)
    {
        File.WriteAllText(Path.Combine(_dir, SettingsFileReader.FileName), json);
        return _dir;
    }

    // Helpers that return a value check synchronously (Assert.Fail throws), so their callers stay unchanged.
    static SkarpOptions Parse(params string[] args)
    {
        var r = CommandLine.Parse(args);
        if (r.Options is null) Assert.Fail(r.Error ?? "no options");
        return r.Options!;
    }

    static string Error(params string[] args)
    {
        var r = CommandLine.Parse(args);
        if (r.Options is not null) Assert.Fail("expected a usage error, got options");
        if (r.ExitCode != 2) Assert.Fail($"expected exit code 2, got {r.ExitCode}");
        return r.Error!;
    }

    // Each setting: the file fragment, the flag arguments, and the value read back for default, file and flag.
    // Rows are Funcs, so each test gets its own flag array (TUnit0046).
    public static IEnumerable<Func<(string, string, string[], string, string, string)>> Settings() =>
    [
        () => ("preset", "\"preset\": \"pmd\"", ["--preset", "doomen"], "default", "pmd", "doomen"),
        () => ("complexMetric", "\"complexMetric\": \"cyclomatic\"", ["--complex-metric", "cognitive"], "cognitive", "cyclomatic", "cognitive"),
        () => ("longMethodLines", "\"longMethodLines\": \"source\"", ["--long-method-lines", "physical"], "physical", "source", "physical"),
        () => ("recordParameters", "\"recordParameters\": \"check\"", ["--record-parameters", "exempt"], "exempt", "check", "exempt"),
        () => ("magicNumbers", "\"magicNumbers\": \"list\"", ["--magic-numbers", "count"], "count", "list", "count"),
        () => ("rules.magic-number", "\"rules\": { \"magic-number\": false }", ["--rule", "magic-number=on"], "on", "off", "on"),
        () => ("includeTests", "\"includeTests\": true", ["--no-include-tests"], "false", "true", "false"),
        () => ("top", "\"top\": 5", ["--top", "7"], "20", "5", "7"),
        () => ("history.enabled", "\"history\": { \"enabled\": false }", ["--history"], "true", "false", "true"),
        () => ("history.window", "\"history\": { \"window\": 6 }", ["--window", "3"], "12", "6", "3"),
        () => ("history.since", "\"history\": { \"since\": \"2025-01-01\" }", ["--since", "2024-02-03"], "(none)", "2025-01-01T00:00:00+00:00", "2024-02-03T00:00:00+00:00"),
        () => ("history.allHistory", "\"history\": { \"allHistory\": true }", ["--all-history"], "false", "true", "true"),
        () => ("history.ignoreWhitespace", "\"history\": { \"ignoreWhitespace\": true }", ["--no-ignore-whitespace"], "false", "true", "false"),
        () => ("history.bulkFlag", "\"history\": { \"bulkFlag\": 100 }", ["--bulk-flag", "50"], "300", "100", "50"),
        () => ("history.bulkDrop", "\"history\": { \"bulkDrop\": 1000 }", ["--bulk-drop", "500"], "(none)", "1000", "500"),
        () => ("history.includeBots", "\"history\": { \"includeBots\": true }", ["--no-include-bots"], "false", "true", "false"),
        () => ("include", "\"include\": [\"a/**\"]", ["--include", "b/**"], "(none)", "a/**", "a/**, b/**"),
        () => ("exclude", "\"exclude\": [\"tools/bench/**\"]", ["--exclude", "x.cs"], "(none)", "tools/bench/**", "tools/bench/**, x.cs"),
        () => ("history.botPatterns", "\"history\": { \"botPatterns\": [\"ci-user\"] }", ["--bot-pattern", "build"], "(none)", "ci-user", "ci-user, build"),
        // The per-file cap and the gate; --gate-on replaces the file's set.
        () => ("report.perFile", "\"report\": { \"perFile\": 5 }", ["--per-file", "2"], "3", "5", "2"),
        () => ("gate.enabled", "\"gate\": { \"enabled\": true }", ["--no-gate"], "false", "true", "false"),
        () => ("gate.on", "\"gate\": { \"on\": [\"long-method:notice\", \"*:warning\"] }", ["--gate-on", "*:*"], "*:warning", "long-method:notice, *:warning", "*:*"),
        // The health limits, layered like the other gate settings; 0 is shown as off.
        () => ("gate.health.maxDrop", "\"gate\": { \"health\": { \"maxDrop\": 0.5 } }", ["--gate-health-drop", "0"], "1.0", "0.5", "0 (off)"),
        () => ("gate.health.newFileMin", "\"gate\": { \"health\": { \"newFileMin\": 7 } }", ["--gate-health-new-min", "5.5"], "6.5", "7.0", "5.5"),
        () => ("gate.health.floor", "\"gate\": { \"health\": { \"floor\": 0 } }", ["--gate-health-floor", "8"], "6.5", "0 (off)", "8.0"),
        // The semantic tier (semantic.solution, an absolute path, is checked in SemanticInputTests).
        () => ("semantic.enabled", "\"semantic\": { \"enabled\": false }", ["--semantic"], "true", "false", "true"),
        () => ("semantic.generators", "\"semantic\": { \"generators\": false }", ["--generators"], "true", "false", "true"),
        () => ("semantic.targets", "\"semantic\": { \"targets\": [\"all\"] }", ["--target", "net8.0"], "highest", "all", "net8.0"),
        () => ("namespaceCycles.includeGeneratedTargets", "\"namespaceCycles\": { \"includeGeneratedTargets\": true }", ["--include-generated-targets"], "false", "true", "true"),
        // The saved run (output.* and tracking.* have no analyze flag).
        () => ("saveLastRun", "\"saveLastRun\": false", ["--no-save"], "true", "false", "false"),
        () => ("score.model", "\"score\": { \"model\": \"grade\" }", ["--score-model", "grade"], "grade", "grade", "grade"),
    ];

    [Test]
    public async Task Agent_loop_settings_without_analyze_flags_come_from_the_file()
    {
        var o = Parse("analyze", Config("""{ "version": 1, "output": { "checkMaxLines": 3, "gateMaxLines": 40 }, "tracking": { "retentionDays": 7, "lookbackDays": 0 } }"""));
        await Assert.That((o.CheckMaxLines, o.GateMaxLines, o.TrackingRetentionDays, o.TrackingLookbackDays)).IsEqualTo((3, 40, 7, 0));
        var d = Parse("analyze", _dir, "--no-config");
        await Assert.That((d.CheckMaxLines, d.GateMaxLines, d.TrackingRetentionDays, d.TrackingLookbackDays)).IsEqualTo((5, 50, 30, 14));
        await Assert.That(Error("analyze", Config("""{ "version": 1, "output": { "checkMaxLines": 0 } }"""))).Contains("output.checkMaxLines: must be 1 or more");
        await Assert.That(Error("analyze", Config("""{ "version": 1, "tracking": { "retentionDays": 0 } }"""))).Contains("tracking.retentionDays: must be 1 or more");
        await Assert.That(Error("hotspots", _dir, "--no-config", "--format", "markdown")).Contains("--format markdown applies to the analyze command only");
        // The agent.* keys and the agent and human formats are gone, with no aliases.
        await Assert.That(Error("analyze", Config("""{ "version": 1, "agent": { "checkMaxLines": 3 } }"""))).Contains("unknown key");
        await Assert.That(Error("analyze", _dir, "--no-config", "--format", "human")).Contains("Invalid value for --format").And.Contains("text, json, markdown, github or sarif");
        await Assert.That(Error("analyze", _dir, "--no-config", "--format", "agent")).Contains("Invalid value for --format");
        await Assert.That(Error("analyze", _dir, "--no-config", "--max-lines", "5")).Contains("--max-lines");
    }

    [Test]
    public async Task Variant_settings_come_from_the_file()
    {
        // Symbols and the caps have no flags; they enter the settings fingerprint.
        static EffectiveSetting Get(SkarpOptions o, string key) => o.Settings().Single(s => s.Key == key);
        var d = Parse("analyze", _dir, "--no-config");
        await Assert.That((Get(d, "symbols").Value, Get(d, "variants.cap").Value, Get(d, "variants.checkCap").Value, Get(d, "variants.cap").Origin))
            .IsEqualTo(("(automatic)", "32", "4", "default"));
        var o = Parse("analyze", Config("""{ "version": 1, "symbols": ["NET462", "NET10_0", "DEBUG", "NET462"], "variants": { "cap": 8, "checkCap": 2 } }"""));
        await Assert.That(o.Variants.Symbols).IsEquivalentTo(["NET462", "NET10_0", "DEBUG"], CollectionOrdering.Matching);
        await Assert.That((o.Variants.Cap, o.Variants.CheckCap, o.Variants.Mode, o.AnalyzerOptions().Variants)).IsEqualTo((8, 2, "symbols", o.Variants));
        await Assert.That((Get(o, "symbols").Value, Get(o, "symbols").Origin, Get(o, "variants.checkCap").Origin)).IsEqualTo(("NET462, NET10_0, DEBUG", "repository", "repository"));
        await Assert.That(Error("analyze", Config("""{ "version": 1, "variants": { "cap": -1 } }"""))).Contains("variants.cap: must be 0 or more");
        await Assert.That(Error("analyze", Config("""{ "version": 1, "variants": { "checkCap": 0 } }"""))).Contains("variants.checkCap: must be 1 or more");
        await Assert.That(Error("analyze", Config("""{ "version": 1, "symbols": ["NET 8"] }"""))).Contains("symbols: 'NET 8' is not a preprocessor symbol");
        await Assert.That(Error("analyze", Config("""{ "version": 1, "variants": { "max": 3 } }"""))).Contains("variants.max: unknown key");
        var settings = d.RuleSettings().ToInfo(false);
        await Assert.That(SkarpSharp.Baselines.Baseline.SettingsFingerprint(settings, false, false, [], [], d.Variants.FingerprintText))
            .IsNotEqualTo(SkarpSharp.Baselines.Baseline.SettingsFingerprint(settings, false, false, [], [], o.Variants.FingerprintText));
    }

    [Test]
    public async Task Gate_settings_are_validated()
    {
        await Assert.That(Error("analyze", _dir, "--no-config", "--gate-on", "nope:warning")).Contains("unknown rule 'nope'");
        await Assert.That(Error("analyze", _dir, "--no-config", "--gate-on", "*:error")).Contains("severity 'error' is not notice, warning or *");
        await Assert.That(Error("analyze", _dir, "--no-config", "--gate-on", "long-method")).Contains("is not <rule>:<severity>");
        var dir = Config("{ \"version\": 1, \"gate\": { \"on\": [\"long-method:big\"] } }");
        await Assert.That(Error("analyze", dir)).Contains("gate.on: 'long-method:big'");
        await Assert.That(Error("analyze", Config("{ \"version\": 1, \"report\": { \"perFile\": -1 } }"))).Contains("report.perFile: must be 0 or more");
        await Assert.That(Error("analyze", Config("{ \"version\": 1, \"gate\": { \"unknown\": 1 } }"))).Contains("gate.unknown: unknown key");
        await Assert.That(Error("hotspots", _dir, "--no-config", "--format", "markdown")).Contains("--format markdown applies to the analyze command only");
        await Assert.That(Error("analyze", _dir, "--no-config", "--unit", "files")).Contains("--unit needs --sort");
        await Assert.That(Error("hotspots", _dir, "--no-config", "--sort", "health")).Contains("--sort applies to the analyze command only");
        await Assert.That(Error("analyze", _dir, "--no-config", "--sort", "health", "--format", "sarif")).Contains("--sort works with --format text or json, not sarif");
        var o = Parse("analyze", _dir, "--no-config", "--gate", "--gate-on", "*:notice", "--gate-on", "empty-catch:*", "--sort", "long-method", "--per-file", "0");
        await Assert.That((o.Gate, o.Sort, o.PerFile)).IsEqualTo((true, "long-method", 0));
        await Assert.That(o.GateOn).IsEquivalentTo(["*:notice", "empty-catch:*"], CollectionOrdering.Matching);
        await Assert.That(o.ReportOptions().PerFile).IsEqualTo(0);
    }

    [Test]
    public async Task Health_limits_out_of_range_are_rejected()
    {
        // Values from 0 to 10, a drop limit from 0 to 9; the error names the range.
        await Assert.That(Error("analyze", Config("""{ "version": 1, "gate": { "health": { "maxDrop": 9.5 } } }""")))
            .Contains("gate.health.maxDrop: must be from 0 to 9 (0 turns the limit off)");
        await Assert.That(Error("analyze", Config("""{ "version": 1, "gate": { "health": { "newFileMin": -1 } } }""")))
            .Contains("gate.health.newFileMin: must be from 0 to 10");
        await Assert.That(Error("analyze", Config("""{ "version": 1, "gate": { "health": { "floor": 11 } } }""")))
            .Contains("gate.health.floor: must be from 0 to 10");
        await Assert.That(Error("analyze", Config("""{ "version": 1, "gate": { "health": { "min": 7 } } }"""))).Contains("gate.health.min: unknown key");
        await Assert.That(Error("analyze", _dir, "--no-config", "--gate-health-drop", "10")).Contains("Invalid value for --gate-health-drop: '10' (expected a number from 0 to 9");
        await Assert.That(Error("analyze", _dir, "--no-config", "--gate-health-floor", "-1")).Contains("Invalid value for --gate-health-floor");
        await Assert.That(Error("analyze", _dir, "--no-config", "--gate-health-new-min", "x")).Contains("Invalid value for --gate-health-new-min");
        var o = Parse("analyze", _dir, "--no-config", "--gate-health-drop", "0.1", "--gate-health-new-min", "0", "--gate-health-floor", "10");
        await Assert.That((o.HealthLimits.MaxDrop, o.HealthLimits.NewFileMin, o.HealthLimits.Floor)).IsEqualTo((0.1, 0.0, 10.0));
    }

    [Test]
    [MethodDataSource(nameof(Settings))]
    public async Task Precedence_is_default_then_file_then_flag(string key, string fragment, string[] flags, string byDefault, string byFile, string byFlag)
    {
        static EffectiveSetting Get(SkarpOptions o, string key) => o.Settings().Single(s => s.Key == key);

        var d = Get(Parse("analyze", _dir, "--no-config"), key);
        await Assert.That((d.Value, d.Origin)).IsEqualTo((byDefault, "default"));

        var dir = Config($"{{ \"version\": 1, {fragment} }}");
        var f = Get(Parse("analyze", dir), key);
        await Assert.That((f.Value, f.Origin)).IsEqualTo((byFile, "repository"));

        var fl = Get(Parse(["analyze", dir, .. flags]), key);
        await Assert.That((fl.Value, fl.Origin)).IsEqualTo((byFlag, "command line"));
        // The old "file + flag" of lists is now the origin chain: the flag over the repository's value.
        await Assert.That(fl.Overridden!.Select(e => e.Level).ToList()).Contains(SettingOrigin.Repository);
    }

    [Test]
    public async Task Settings_list_covers_every_file_key()
    {
        var keys = Parse("analyze", _dir, "--no-config").Settings().Select(s => s.Key).ToList();
        foreach (var rule in RuleCatalog.All) await Assert.That(keys).Contains($"rules.{rule.Id}");
        // Every key of the theory exists, and every listed key that is not a rule switch is in the theory.
        var tested = Settings().Select(row => row().Item1).ToHashSet();
        foreach (var k in keys.Where(k => !k.StartsWith("rules.", StringComparison.Ordinal) && k is not ("semantic.solution" or "unusedCode.allow" or "output.checkMaxLines" or "output.gateMaxLines" or "tracking.retentionDays" or "tracking.lookbackDays"
            or "score.decay" or "score.weights" or "score.worst.count" or "score.worst.below" or "symbols" or "variants.cap" or "variants.checkCap")))
            await Assert.That(tested).Contains(k);
        foreach (var rule in RuleCatalog.Semantic) await Assert.That(keys).Contains($"rules.{rule.Id}");
    }

    [Test]
    public async Task Unused_code_allowlist_comes_from_the_settings_file_only()
    {
        static EffectiveSetting Get(SkarpOptions o) => o.Settings().Single(s => s.Key == "unusedCode.allow");
        await Assert.That((Get(Parse("analyze", _dir, "--no-config")).Value, Get(Parse("analyze", _dir, "--no-config")).Origin)).IsEqualTo(("(none)", "default"));
        var o = Parse("analyze", Config("""{ "version": 1, "unusedCode": { "allow": ["M:Ns.C.A", " Ns.Keep* "] } }"""));
        await Assert.That(o.UnusedAllow).IsEquivalentTo(["M:Ns.C.A", "Ns.Keep*"], CollectionOrdering.Matching);
        await Assert.That((Get(o).Value, Get(o).Origin)).IsEqualTo(("M:Ns.C.A, Ns.Keep*", "repository"));
        await Assert.That(Error("analyze", Config("""{ "version": 1, "unusedCode": { "allow": [""] } }"""))).Contains("unusedCode.allow: empty pattern");
        await Assert.That(Error("analyze", Config("""{ "version": 1, "unusedCode": { "nope": 1 } }"""))).Contains("unusedCode.nope: unknown key");
    }

    [Test]
    public async Task Thresholds_from_the_file_then_flags_and_their_origins()
    {
        var dir = Config("""
            {
              "version": 1,
              // comments are allowed
              "thresholds": {
                "long-method": { "notice": 60, "warning": "off" },
                "large-type": { "notice": { "sourceLines": 300 } },
                "bumpy-road": { "depth": 3 },
              }
            }
            """);
        var o = Parse("analyze", dir, "--threshold", "long-method.notice=80");
        await Assert.That(o.Thresholds).IsEquivalentTo(["long-method.notice=60", "long-method.warning=off", "large-type.notice.sourceLines=300", "bumpy-road.depth=3", "long-method.notice=80"], CollectionOrdering.Matching);
        var rules = o.RuleSettings();
        await Assert.That(rules[RuleIds.LongMethod].Notice!.AnyOf[0].Value).IsEqualTo(80);     // flag wins
        await Assert.That(rules[RuleIds.LongMethod].Warning).IsNull();                          // file
        await Assert.That(rules[RuleIds.LargeType].Notice!.Find("sourceLines")!.Value).IsEqualTo(300);
        await Assert.That(rules[RuleIds.BumpyRoad].Parameters["depth"]).IsEqualTo("3");
        await Assert.That(o.OriginText("thresholds.long-method.notice")).IsEqualTo("command line");
        await Assert.That(o.OriginText("thresholds.long-method.warning")).IsEqualTo("repository");
        await Assert.That(o.OriginText("thresholds.large-type.notice.sourceLines")).IsEqualTo("repository");

        var text = new StringWriter();
        Reporting.HumanReport.WriteConfig(text, o, rules, "t");
        var config = text.ToString();
        await Assert.That(config).Contains("repository    " + Path.Combine(dir, ".skarp.json") + " (applied)");
        await Assert.That(config).Matches(@"long-method\s+notice\s+physicalLines over 80\s+command line");
        await Assert.That(config).Matches(@"long-method\s+warning\s+blank\s+repository");
        await Assert.That(config).Matches(@"complex-method\s+notice\s+cognitive over 15\s+preset default");
    }

    [Test]
    public async Task A_rule_switched_off_has_no_levels_and_produces_no_findings()
    {
        var o = Parse("analyze", Config("""{ "version": 1, "rules": { "complex-method": false } }"""));
        var rules = o.RuleSettings();
        await Assert.That(rules[RuleIds.ComplexMethod].Enabled).IsFalse();
        var code = "class C { void M(bool b) { " + string.Concat(Enumerable.Repeat("if (b) { } ", 20)) + "} }";
        var result = new Model.AnalysisResult { Files = [Analysis.Analyzer.AnalyzeSource(code)] };
        Analysis.Aggregation.Fill(result, false);
        FindingEngine.Apply(result, rules, false);
        await Assert.That(result.Findings!).DoesNotContain(f => f.Rule == RuleIds.ComplexMethod);
        await Assert.That(result.Run.Findings!.Rules).Contains(r => r.Rule == RuleIds.ComplexMethod && r.Off == true);
    }

    [Test]
    [Arguments("""{ "version": 1, "preset": "default", "colour": "red" }""", ".skarp.json: colour: unknown key")]
    [Arguments("""{ "version": 1, "history": { "window": "twelve" } }""", ".skarp.json: history.window: wrong type (expected a whole number)")]
    [Arguments("""{ "version": 1, "history": { "windowz": 3 } }""", ".skarp.json: history.windowz: unknown key")]
    [Arguments("""{ "version": 1, "top": true }""", ".skarp.json: top: wrong type (expected a whole number)")]
    [Arguments("""{ "version": 1, "includeTests": "yes" }""", ".skarp.json: includeTests: wrong type (expected true or false)")]
    [Arguments("""{ "version": 1, "exclude": "tools" }""", ".skarp.json: exclude: wrong type (expected a list of strings)")]
    [Arguments("""{ "preset": "default" }""", ".skarp.json: version: required")]
    [Arguments("""{ "version": 2 }""", ".skarp.json: version: 2 is not supported")]
    [Arguments("""{ "version": 1, "preset": "nope" }""", ".skarp.json: preset: unknown preset 'nope'")]
    [Arguments("""{ "version": 1, "rules": { "long-methods": false } }""", ".skarp.json: rules.long-methods: unknown rule")]
    [Arguments("""{ "version": 1, "thresholds": { "long-method": { "notice": "big" } } }""", ".skarp.json: thresholds.long-method.notice: threshold override")]
    [Arguments("""{ "version": 1, "thresholds": { "long-method": { "notice": true } } }""", ".skarp.json: thresholds.long-method.notice: wrong type")]
    [Arguments("""{ "version": 1, "thresholds": { "nope": { "notice": 3 } } }""", ".skarp.json: thresholds.nope: unknown rule")]
    [Arguments("""{ "version": 1, "history": { "since": "yesterday-ish" } }""", ".skarp.json: history.since: 'yesterday-ish' is not a date")]
    [Arguments("""{ "version": 1, "history": { "botPatterns": ["("] } }""", ".skarp.json: history.botPatterns: '(' is not a valid regular expression")]
    [Arguments("""{ "version": 1, "complexMetric": "npath" }""", ".skarp.json: complexMetric: 'npath' is not cognitive or cyclomatic")]
    [Arguments("""{ "version": 1, """, ".skarp.json: ")]
    public async Task Settings_file_errors_name_the_key(string json, string expected)
    {
        var error = Error("analyze", Config(json));
        await Assert.That(error).StartsWith("skarp: settings file ");
        await Assert.That(error).Contains(expected);
    }

    [Test]
    public async Task Settings_file_is_found_in_a_parent_up_to_the_git_root_and_can_be_ignored_or_replaced()
    {
        var sub = Path.Combine(_dir, "src", "lib");
        Directory.CreateDirectory(sub);
        Config("""{ "version": 1, "top": 3 }""");
        await Assert.That(Parse("analyze", sub).Top).IsEqualTo(3);
        await Assert.That(Parse("analyze", sub).ConfigPath).IsEqualTo(Path.Combine(_dir, ".skarp.json"));
        await Assert.That(Parse("analyze", sub, "--no-config").Top).IsEqualTo(20);
        var other = Path.Combine(_dir, "other.json");
        File.WriteAllText(other, """{ "version": 1, "top": 9 }""");
        await Assert.That(Parse("analyze", sub, "--config", other).Top).IsEqualTo(9);
        await Assert.That(Error("analyze", sub, "--config", Path.Combine(_dir, "missing.json"))).Contains("Settings file not found");

        // Globs of the file are relative to its folder and rebased onto the analyzed subdirectory.
        Config("""{ "version": 1, "exclude": ["src/lib/gen/**", "tools/**", "*.g.cs"], "include": ["src/**"] }""");
        var rebased = Parse("analyze", sub);
        await Assert.That(rebased.Exclude).IsEquivalentTo(["gen/**", "*.g.cs"], CollectionOrdering.Matching);
        await Assert.That(rebased.Include).IsEquivalentTo(["**"], CollectionOrdering.Matching);

        // A .skarp.json above the git root is not used.
        var repo = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        await Assert.That(Parse("analyze", repo).ConfigPath).IsNull();
    }

    [Test]
    [Arguments("tools/bench/**", "", "tools/bench/**")]
    [Arguments("tools/bench/**", "src", null)]
    [Arguments("src/lib/gen/**", "src/lib", "gen/**")]
    [Arguments("src/**", "src/lib", "**")]
    [Arguments("/src/lib", "src/lib", "**")]
    [Arguments("**/Generated/**", "src", "**/Generated/**")]
    [Arguments("src/**/Generated/**", "src/lib", "**/Generated/**")]
    [Arguments("*.g.cs", "src", "*.g.cs")]
    [Arguments("s*/lib/x.cs", "src/lib", "x.cs")]
    public async Task Settings_globs_are_rebased_onto_the_analyzed_directory(string glob, string prefix, string? expected)
    {
        await Assert.That(SettingsFileReader.RebaseGlob(glob, prefix)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(new[] { "analyze", "." , "--top", "x" }, "Invalid value for --top")]
    [Arguments(new[] { "analyze", ".", "--frobnicate" }, "Unknown option: --frobnicate")]
    [Arguments(new[] { "analyze", ".", "--preset" }, "Option --preset needs a value")]
    [Arguments(new[] { "history", ".", "--preset", "pmd" }, "does not apply to the history command")]
    [Arguments(new[] { "analyze", ".", "--rule", "magic-number=maybe" }, "Invalid value for --rule")]
    [Arguments(new[] { "analyze", ".", "--threshold", "long-method.notice=big" }, "is not a number")]
    [Arguments(new[] { "analyze" }, "missing <directory>")]
    [Arguments(new[] { "frob", "." }, "Unknown command: frob")]
    public async Task Flag_errors_are_usage_errors(string[] args, string expected)
    {
        await Assert.That(Error(args.Length >= 2 ? [args[0], args[1], "--no-config", .. args[2..]] : args)).Contains(expected);
    }

    [Test]
    public async Task Defaults_per_command_and_derived_option_objects()
    {
        var a = Parse("analyze", _dir, "--no-config");
        await Assert.That((a.Detail, a.History, a.Top, a.Format)).IsEqualTo(("members", true, 20, "text"));
        await Assert.That(Parse("hotspots", _dir, "--no-config").Detail).IsEqualTo("files");
        var o = Parse("analyze", _dir, "--no-config", "--include-tests", "--exclude", "x/**", "-w", "--bulk-drop", "9", "--new-only", "--magic-numbers", "list", "--parallelism", "3");
        await Assert.That(o.AnalyzerOptions().IncludeTests).IsTrue();
        await Assert.That(o.AnalyzerOptions().Exclude).IsEquivalentTo(["x/**"], CollectionOrdering.Matching);
        await Assert.That(o.AnalyzerOptions().Parallelism).IsEqualTo(3);
        await Assert.That(o.HistoryOptions().IgnoreWhitespace).IsTrue();
        await Assert.That(o.HistoryOptions().BulkDrop).IsEqualTo(9);
        await Assert.That(o.ReportOptions().NewOnly).IsTrue();
        await Assert.That(o.ReportOptions().ListMagicNumbers).IsTrue();
        await Assert.That(o.BaselineFile()).IsEqualTo(Path.Combine(_dir, ".skarp-baseline.json"));
        await Assert.That(CommandLine.Parse(["--help"]).ShowUsage).IsTrue();
        await Assert.That(CommandLine.Parse(["explain", "long-method"]).Explain).IsTrue();
    }
}
