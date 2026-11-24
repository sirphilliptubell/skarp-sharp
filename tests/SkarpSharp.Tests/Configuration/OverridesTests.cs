using System.Text;
using System.Text.Json.Nodes;
using SkarpSharp.Agent;
using SkarpSharp.Analysis;
using SkarpSharp.Baselines;
using SkarpSharp.Commands;
using SkarpSharp.Configuration;
using SkarpSharp.Discovery;
using SkarpSharp.Findings;
using SkarpSharp.Health;
using SkarpSharp.Model;
using SkarpSharp.Semantic;

namespace SkarpSharp.Tests.Configuration;

/// <summary>
/// Per-path settings, <c>overrides</c>. The merge table,
/// errors, and that <c>check</c> equals the full run
/// with overrides active on this repository.
/// </summary>
[NotInParallel(nameof(OverridesTests))]
public sealed class OverridesTests : IDisposable
{
    readonly string _repo = Path.Combine(Path.GetTempPath(), "skarp-overrides-" + Guid.NewGuid().ToString("N"));

    public OverridesTests() => Directory.CreateDirectory(Path.Combine(_repo, ".git"));

    public void Dispose()
    {
        try { Directory.Delete(_repo, true); } catch (IOException) { }
    }

    static readonly SettingsLocations None = SettingsLocations.None;

    string RepoFile => Path.Combine(_repo, SettingsFileReader.FileName);

    SkarpOptions Parse(string settings, params string[] flags)
    {
        File.WriteAllText(RepoFile, settings);
        var r = CommandLine.Parse(["analyze", _repo, .. flags], None);
        if (r.Options is null) Assert.Fail(r.Error ?? "no options");
        return r.Options!;
    }

    string Error(string settings, SettingsLocations? locations = null)
    {
        File.WriteAllText(RepoFile, settings);
        var r = CommandLine.Parse(["analyze", _repo], locations ?? None);
        if (r.Options is not null) Assert.Fail("expected an error for " + settings);
        return r.Error!;
    }

    static string Entries(string entries) => $$"""{ "version": 1, "overrides": [ {{entries}} ] }""";

    const string Table = """
        {
          "version": 1,
          "rules": { "magic-number": false },
          "thresholds": { "long-method": { "notice": 50 }, "complex-method": { "notice": 12 } },
          "overrides": [
            { "files": ["src/**"], "thresholds": { "long-method": { "notice": 80, "warning": 150 } } },
            { "files": ["src/gen/**"], "thresholds": { "long-method": { "notice": 100 } }, "rules": { "magic-number": true } },
            { "files": ["src/gen/old/**"], "rules": { "magic-number": false }, "thresholds": { "complex-method": null } },
            { "tests": true, "rules": { "deep-nesting": false } },
            { "files": ["src/**"], "excludeFiles": ["src/gen/**"], "thresholds": { "large-file": { "notice": 999 } } }
          ]
        }
        """;

    static double? Notice(RuleSettings s, string rule) => s[rule].Notice?.AnyOf[0].Value;
    static double? Warning(RuleSettings s, string rule) => s[rule].Warning?.AnyOf[0].Value;

    // ---- order, null, command line ----------------------------------------------------------------------------------

    [Test]
    public async Task Merge_table_order_null_reset_and_the_command_line_on_top()
    {
        var rules = Parse(Table, "--threshold", "long-method.warning=200").PathRules();
        var preset = RuleSettings.Resolve("default", null, []);

        // No match: the repository settings object itself, the command line on top.
        var other = rules.For("other/A.cs", false);
        await Assert.That(ReferenceEquals(other, rules.Base)).IsTrue();
        await Assert.That(Notice(other, RuleIds.LongMethod)).IsEqualTo(50);
        await Assert.That(Warning(other, RuleIds.LongMethod)).IsEqualTo(200);
        await Assert.That(other[RuleIds.MagicNumber].Enabled).IsFalse();

        // One entry: its notice wins over the repository's; its warning loses to the command line.
        var src = rules.For("src/A.cs", false);
        await Assert.That(Notice(src, RuleIds.LongMethod)).IsEqualTo(80);
        await Assert.That(Warning(src, RuleIds.LongMethod)).IsEqualTo(200);
        await Assert.That(Notice(src, RuleIds.ComplexMethod)).IsEqualTo(12);
        await Assert.That(Notice(src, RuleIds.LargeFile)).IsEqualTo(999);
        await Assert.That(ReferenceEquals(src, rules.For("src/B.cs", false))).IsTrue();
        // Rules no entry names share the repository settings' objects (memory).
        await Assert.That(ReferenceEquals(src[RuleIds.DeepNesting], rules.Base[RuleIds.DeepNesting])).IsTrue();
        await Assert.That(ReferenceEquals(src[RuleIds.LongMethod], rules.Base[RuleIds.LongMethod])).IsFalse();

        // Overlapping: the later entry wins key by key; a rule off at repository level is on again.
        var gen = rules.For("src/gen/A.cs", false);
        await Assert.That(Notice(gen, RuleIds.LongMethod)).IsEqualTo(100);
        await Assert.That(Warning(gen, RuleIds.LongMethod)).IsEqualTo(200);
        await Assert.That(gen[RuleIds.MagicNumber].Enabled).IsTrue();
        await Assert.That(Notice(gen, RuleIds.LargeFile)).IsEqualTo(Notice(preset, RuleIds.LargeFile)); // excludeFiles

        // Off again deeper; null resets complex-method to the preset, not to the repository's 12.
        var old = rules.For("src/gen/old/A.cs", false);
        await Assert.That(old[RuleIds.MagicNumber].Enabled).IsFalse();
        await Assert.That(Notice(old, RuleIds.LongMethod)).IsEqualTo(100);
        await Assert.That(Notice(old, RuleIds.ComplexMethod)).IsEqualTo(Notice(preset, RuleIds.ComplexMethod));

        // --rule beats every entry.
        var off = Parse(Table, "--rule", "magic-number=off").PathRules();
        await Assert.That(off.For("src/gen/A.cs", false)[RuleIds.MagicNumber].Enabled).IsFalse();
        await Assert.That(rules.Combinations).IsEqualTo(3);
    }

    [Test]
    public async Task Selectors_files_excludeFiles_tests_and_both()
    {
        var rules = Parse(Entries("""
            { "files": ["src/**"], "rules": { "magic-number": false } },
            { "files": ["src/**"], "excludeFiles": ["src/Keep/**", "**/*.g.cs"], "rules": { "empty-catch": false } },
            { "tests": true, "rules": { "deep-nesting": false } },
            { "files": ["src/Legacy/**"], "tests": true, "rules": { "long-method": false } }
            """)).PathRules();
        await Assert.That(rules.Matched("src/A.cs", false).ToList()).IsEquivalentTo(new[] { 0, 1 });
        await Assert.That(rules.Matched("src/Keep/A.cs", false).ToList()).IsEquivalentTo(new[] { 0 });
        await Assert.That(rules.Matched("src/x/B.g.cs", false).ToList()).IsEquivalentTo(new[] { 0 });
        await Assert.That(rules.Matched("other/T.cs", true).ToList()).IsEquivalentTo(new[] { 2 });
        await Assert.That(rules.Matched("src/Legacy/T.cs", true).ToList()).IsEquivalentTo(new[] { 0, 1, 2, 3 });
        await Assert.That(rules.Matched("src/Legacy/P.cs", false).ToList()).IsEquivalentTo(new[] { 0, 1 });
        await Assert.That(rules.Matched("other/P.cs", false).Count).IsEqualTo(0);
        await Assert.That(rules.For("src/Legacy/T.cs", true)[RuleIds.LongMethod].Enabled).IsFalse();
        await Assert.That(rules.For("src/Legacy/P.cs", false)[RuleIds.LongMethod].Enabled).IsTrue();
    }

    [Test]
    public async Task Globs_are_rebased_when_a_subfolder_is_analyzed_and_a_dropped_entry_matches_nothing()
    {
        File.WriteAllText(RepoFile, Entries("""
            { "files": ["src/Legacy/**"], "rules": { "magic-number": false } },
            { "files": ["tools/**"], "rules": { "empty-catch": false } }
            """));
        var src = Path.Combine(_repo, "src");
        Directory.CreateDirectory(src);
        var o = CommandLine.Parse(["analyze", src], None).Options!;
        await Assert.That(o.RuleOverrides[0].RebasedFiles!.ToList()).IsEquivalentTo(new[] { "Legacy/**" });
        await Assert.That(o.RuleOverrides[1].MatchesNothing).IsTrue();
        await Assert.That(o.PathRules().Matched("Legacy/A.cs", false).ToList()).IsEquivalentTo(new[] { 0 });
        await Assert.That(o.PathRules().Matched("tools/A.cs", false).Count).IsEqualTo(0);
    }

    // ---- errors -----------------------------------------------------------------------------------------------------

    [Test]
    public async Task Each_error_names_the_entry_the_key_and_the_remedy()
    {
        (string Settings, string Expected)[] cases =
        [
            (Entries("""{ "files": ["a/**"], "preset": "pmd" }"""), "overrides[0].preset: not allowed in an override"),
            (Entries("""{ "files": ["a/**"], "rules": { "duplicate-code": false } }"""), "overrides[0].rules.duplicate-code: duplicate-code matches across files and has one setting per run; use exclude to leave files out"),
            (Entries("""{ "files": ["a/**"], "thresholds": { "duplicate-code-near": { "notice": 0.9 } } }"""), "overrides[0].thresholds.duplicate-code-near: duplicate-code-near matches across files"),
            (Entries("""{ "files": ["a/**"], "thresholds": { "namespace-cycle": { "warning": "off" } } }"""), "overrides[0].thresholds.namespace-cycle: namespace-cycle matches across files"),
            (Entries("""{ "files": ["a/**"], "thresholds": { "active-complex-file": { "share": 10 } } }"""), "overrides[0].thresholds.active-complex-file: active-complex-file ranks files repository-wide; an override can only switch it off"),
            (Entries("""{ "files": ["a/**"], "rules": { "active-complex-file": true } }"""), "overrides[0].rules.active-complex-file: active-complex-file ranks files repository-wide"),
            (Entries("""{ "rules": { "long-method": false } }"""), "overrides[0]: needs files or \"tests\": true"),
            (Entries("""{ "excludeFiles": ["a/**"], "rules": { "long-method": false } }"""), "needs files or \"tests\": true (excludeFiles only narrows a selection)"),
            (Entries("""{ "files": [], "rules": { "long-method": false } }"""), "overrides[0].files: empty list"),
            (Entries("""{ "files": [""], "rules": { "long-method": false } }"""), "overrides[0].files: empty glob"),
            (Entries("""{ "files": "a/**" }"""), "overrides[0].files: wrong type (expected a list of globs)"),
            (Entries("""{ "tests": false, "rules": { "long-method": false } }"""), "overrides[0].tests: only true is allowed"),
            (Entries("""{ "files": ["a/**"] }, { "tests": "yes" }"""), "overrides[1].tests: wrong type (expected true)"),
            (Entries("""{ "files": ["a/**"], "thresholds": { "long-method": { "notice": "x" } } }"""), "overrides[0].thresholds.long-method.notice"),
            (Entries("""{ "files": ["a/**"], "rules": { "no-such-rule": false } }"""), "overrides[0].rules.no-such-rule: unknown rule"),
            (Entries("""{ "files": ["a/**"], "rules": { "long-method": "off" } }"""), "overrides[0].rules.long-method: wrong type"),
            (Entries("""{ "files": ["a/**"], "rules": null }"""), "overrides[0].rules: null is not allowed here"),
            (Entries("""{ "files": ["a/**"], "thresholds": { "brain-method": { "notice": 3 } } }"""), "overrides[0].thresholds.brain-method.notice"),
            (Entries("\"src/**\""), "overrides[0]: wrong type (expected an object)"),
            ("""{ "version": 1, "overrides": { "files": ["a/**"] } }""", "overrides: wrong type (expected a list of objects)"),
        ];
        foreach (var (settings, expected) in cases)
            await Assert.That(Error(settings)).Contains(expected);
        // A threshold that is valid on its own but not under the run's settings names the entry (checked after the flags).
        File.WriteAllText(RepoFile, Entries("""{ "files": ["a/**"], "thresholds": { "long-method": { "notice": { "noSuchMetric": 3 } } } }"""));
        await Assert.That(CommandLine.Parse(["analyze", _repo], None).Error ?? "").Contains("overrides[0]");
    }

    [Test]
    public async Task Overrides_only_at_the_repository_level()
    {
        var personal = Path.Combine(_repo, "personal.json");
        File.WriteAllText(personal, Entries("""{ "files": ["a/**"], "rules": { "long-method": false } }"""));
        var error = Error("""{ "version": 1 }""", new SettingsLocations { PersonalFile = personal, Variable = _ => null });
        await Assert.That(error).Contains("personal settings file");
        await Assert.That(error).Contains("overrides: allowed only in a repository's .skarp.json (not in the personal settings file)");
        // --config (and SKARP_CONFIG) is the repository level: allowed there.
        var other = Path.Combine(_repo, "other.json");
        File.WriteAllText(other, Entries("""{ "files": ["a/**"], "rules": { "long-method": false } }"""));
        var r = CommandLine.Parse(["analyze", _repo, "--config", other], None);
        await Assert.That(r.Options?.RuleOverrides.Count).IsEqualTo(1);
        await Assert.That(SettingsRegistry.Find("overrides")!.Merge).IsEqualTo(MergeKind.Overrides);
        await Assert.That(SettingsRegistry.Find("overridesMode")).IsNull();
    }

    // ---- no overrides, fingerprints ---------------------------------------------------------------------------------

    [Test]
    public async Task No_overrides_gives_the_one_settings_object_and_the_same_fingerprints()
    {
        foreach (var settings in (string[])["""{ "version": 1, "thresholds": { "long-method": { "notice": 50 } } }""", """{ "version": 1, "thresholds": { "long-method": { "notice": 50 } }, "overrides": [] }"""])
        {
            var o = Parse(settings);
            var rules = o.PathRules();
            await Assert.That(rules.Count).IsEqualTo(0);
            await Assert.That(ReferenceEquals(rules.For("src/A.cs", true), rules.Base)).IsTrue();
            await Assert.That(rules.Combinations).IsEqualTo(0);
        }
        var info = Parse("""{ "version": 1 }""").RuleSettings().ToInfo();
        await Assert.That(Baseline.SettingsFingerprint(info, false, false, [], [], null, null)).IsEqualTo(Baseline.SettingsFingerprint(info, false, false, [], [], null));
        await Assert.That(SemanticBaseline.Fingerprint(info, true, "highest", false, null)).IsEqualTo(SemanticBaseline.Fingerprint(info, true, "highest"));
    }

    [Test]
    public async Task Fingerprint_lines_count_an_entry_by_its_effect_and_its_selectors()
    {
        string Lines(string entry, bool semantic = false)
        {
            var o = Parse(Entries(entry));
            return string.Join("\n", RuleOverrides.FingerprintLines(o.RuleOverrides, o.PathRules(), semantic));
        }
        var a = Lines("""{ "files": ["src/**"], "thresholds": { "long-method": { "notice": 60 } } }""");
        await Assert.That(Lines("""{ "files": ["src/**"], "thresholds": { "long-method": { "notice": 60.0 } } }""")).IsEqualTo(a);
        await Assert.That(Lines("""{ "files": ["lib/**"], "thresholds": { "long-method": { "notice": 60 } } }""")).IsNotEqualTo(a);
        await Assert.That(Lines("""{ "files": ["src/**"], "thresholds": { "long-method": { "notice": 61 } } }""")).IsNotEqualTo(a);
        await Assert.That(a).Contains("rule long-method notice=");
        // The semantic fingerprint gets the entry's semantic rules (and every entry's selectors).
        var s = Lines("""{ "files": ["src/**"], "thresholds": { "high-coupling": { "notice": 5 } } }""", semantic: true);
        await Assert.That(s).Contains("rule high-coupling notice=");
    }

    // ---- check equals the full run ----------------------------------------------------------------------------------

    static readonly string Repo = FindRepo();

    static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SkarpSharp.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root (SkarpSharp.slnx) not found");
    }

    /// <summary>Settings for this repository (eq-self): folders, tests: true, excludeFiles, a null reset, the
    /// composites on, a composite parameter.</summary>
    const string SelfSettings = """
        {
          "version": 1,
          "includeTests": true,
          "exclude": ["tools/bench/**", "tests/SkarpSharp.Tests/Golden/**", "tests/SkarpSharp.Tests/Mock/**"],
          "overrides": [
            { "files": ["src/SkarpSharp.Core/Findings/**"], "thresholds": { "long-method": { "notice": 20 }, "complex-method": { "notice": 5 } }, "rules": { "magic-number": false } },
            { "tests": true, "thresholds": { "long-method": { "notice": 200, "warning": 400 } }, "rules": { "deep-nesting": false, "too-many-parameters": false } },
            { "files": ["**/*Rules*.cs", "src/SkarpSharp.Core/Semantic/**"], "excludeFiles": ["**/Clone*.cs"], "thresholds": { "large-type": { "notice": { "sourceLines": 50 } } }, "rules": { "brain-method": true, "brain-class": true } },
            { "files": ["src/SkarpSharp.Core/Semantic/**"], "thresholds": { "large-file": { "notice": 100 }, "too-many-parameters": null, "brain-method": { "minParts": "2" } } }
          ]
        }
        """;

    [Test]
    public async Task Check_equals_the_full_run_with_overrides_on_this_repository() => await AssertEqual(Repo, SelfSettings);

    /// <summary>Case c: 60 generated selectors, three per entry, each entry lowering one threshold.</summary>
    internal static string CaseC()
    {
        var globs = GlobEquivalenceTests.SelectorGlobs;
        string[] rules = [RuleIds.LongMethod, RuleIds.ComplexMethod, RuleIds.DeepNesting, RuleIds.TooManyParameters, RuleIds.LargeFile];
        var entries = new List<string>();
        for (int k = 0; k * 3 + 2 < globs.Count && k < 20; k++)
        {
            // Every third entry also switches magic-number, on and off in turn.
            var magic = k % 3 == 0 ? $$""", "rules": { "magic-number": {{(k / 3 % 2 == 0 ? "true" : "false")}} }""" : "";
            entries.Add($$"""{ "files": [{{string.Join(", ", globs.Skip(k * 3).Take(3).Select(g => $"\"{g}\""))}}], "thresholds": { "{{rules[k % rules.Length]}}": { "notice": {{10 + k}} } }{{magic}} }""");
        }
        return $$"""{ "version": 1, "includeTests": true, "overrides": [ {{string.Join(",\n", entries)}} ] }""";
    }

    sealed record Full(AnalysisResult Result, HashSet<string> MultiOrPartial, PathRules Rules);

    static string Key(Finding f) => $"{f.Id} {f.Severity} {f.Value} {f.Threshold} {f.Line} {f.Message}";

    static string Key(CompositeFinding c) => $"{c.Id} {c.Severity} {c.Line} {c.Message} {string.Join(",", c.Parts)}";

    /// <summary>For every .cs file under <paramref name="repository"/>, a check (a fresh resolver, as a check process has)
    /// equals the full run of its root filtered to the file. The root's settings are replaced by
    /// <paramref name="settings"/> (an overlay; nothing is written), nested roots keep their own.</summary>
    static async Task AssertEqual(string repository, string settings)
    {
        var rootFile = Path.Combine(Path.GetFullPath(repository), SettingsFileReader.FileName);
        var locations = SettingsLocations.None.WithOverlay(rootFile, settings);
        var fulls = new Dictionary<string, Full>(StringComparer.OrdinalIgnoreCase);
        var mismatches = new List<string>();
        int files = 0, compared = 0, composites = 0, overridden = 0, capped = 0;
        foreach (var rel in FileDiscovery.Find(repository))
        {
            var full = Path.GetFullPath(Path.Combine(repository, rel));
            var root = FileCheck.RootOf(full);
            string[] config = string.Equals(root, Path.GetFullPath(repository), StringComparison.OrdinalIgnoreCase) ? ["--config", rootFile] : [];
            SkarpOptions Options() => CommandLine.Parse(["analyze", root, "--no-history", "--no-semantic", .. config], locations).Options!;
            var options = Options();
            if (!fulls.TryGetValue(root, out var run))
            {
                var rules = options.PathRules();
                var result = Analyzer.AnalyzeDirectory(root, options.AnalyzerOptions());
                FindingEngine.Apply(result, rules, options.IncludeGenerated);
                var multi = new HashSet<string>(result.Types.Where(t => t.Declarations > 1).Select(t => t.Name), StringComparer.Ordinal);
                multi.UnionWith(result.Files.SelectMany(f => f.Types).Where(t => t.Partial).Select(t => t.Name));
                fulls[root] = run = new Full(result, multi, rules);
            }
            bool Exception(Finding f) => FileCheck.LeftOut.Contains(f.Rule) || (f.Rule == RuleIds.LargeType && run.MultiOrPartial.Contains(f.Member ?? ""));
            var path = FileCheck.Relative(root, full);
            if (run.Result.Files.FirstOrDefault(f => f.Path == path)?.Variants is { } planned && planned.Count > options.Variants.CheckCap - 1) { capped++; continue; }
            var fresh = Options();
            var check = FileCheck.Analyze(fresh, fresh.PathRules(), root, path, FileCheck.Decode(File.ReadAllBytes(full)));
            var kept = run.Result.Files.FirstOrDefault(f => f.Path == path);
            if (kept is not null && !ReferenceEquals(run.Rules.For(path, kept.TestCode), run.Rules.Base)) overridden++;
            var expected = (run.Result.Findings ?? []).Where(f => f.Path == path && !Exception(f)).Select(Key).Order(StringComparer.Ordinal).ToList();
            var actual = check.Findings.Where(f => !Exception(f)).Select(Key).Order(StringComparer.Ordinal).ToList();
            files++;
            compared += expected.Count;
            if (!expected.SequenceEqual(actual))
                mismatches.Add($"{root} {path}:\n  only full run: {string.Join("\n    ", expected.Except(actual))}\n  only check: {string.Join("\n    ", actual.Except(expected))}");
            var expectedComposites = (run.Result.Composites ?? []).Where(c => c.Path == path && !(c.Rule == RuleIds.BrainClass && run.MultiOrPartial.Contains(c.Member ?? "")))
                .Select(Key).Order(StringComparer.Ordinal).ToList();
            var actualComposites = check.Composites.Select(Key).Order(StringComparer.Ordinal).ToList();
            composites += expectedComposites.Count;
            if (!expectedComposites.SequenceEqual(actualComposites))
                mismatches.Add($"{root} {path} composites:\n  only full run: {string.Join("\n    ", expectedComposites.Except(actualComposites))}\n  only check: {string.Join("\n    ", actualComposites.Except(expectedComposites))}");
        }
        var combos = string.Join(", ", fulls.Select(f => $"{Path.GetFileName(f.Key)}: {f.Value.Rules.Count} overrides, {f.Value.Rules.Combinations} combinations"));
        Console.WriteLine($"OVERRIDES-EQ {repository}: {files} files ({overridden} under an override), {fulls.Count} roots, {compared} findings equal, {composites} composites equal, {capped} over the variant cap; {combos}; {mismatches.Count} mismatches");
        await Assert.That(overridden > 0).IsTrue().Because("no file was under an override");
        await Assert.That(mismatches.Count == 0).IsTrue().Because($"{mismatches.Count} of {files} files differ:\n" + string.Join("\n", mismatches.Take(20)));
    }

    // ---- semantic per-type rules and the walk plan ------------------------------------------------------------------

    [Test]
    public async Task Semantic_per_type_rules_take_the_settings_of_the_types_file()
    {
        var o = Parse(Entries("""
            { "files": ["src/Legacy/**"], "thresholds": { "high-coupling": { "notice": 100, "warning": 200 } } },
            { "files": ["src/Core/**"], "thresholds": { "high-coupling": { "notice": 3 } } }
            """));
        var rules = o.PathRules();
        SemanticType Type(string name, string path) => new() { Name = name, Kind = "class", Path = path, Line = 1, Cbo = 25, CboAll = 30, CboMicrosoft = 25 };
        var result = new SemanticResult { Types = [Type("Legacy", "src/Legacy/A.cs"), Type("Core", "src/Core/B.cs"), Type("Other", "src/Other/C.cs")] };
        SemanticFindings.Apply(result, rules.Base, null, null, p => rules.For(p, false));
        var coupling = result.Findings.Where(f => f.Rule == RuleIds.HighCoupling).ToDictionary(f => f.Member!, f => f.Severity);
        await Assert.That(coupling.ContainsKey("Legacy")).IsFalse();
        await Assert.That(coupling["Core"]).IsEqualTo("notice");
        await Assert.That(coupling["Other"]).IsEqualTo("notice");
        // The same type with the repository settings everywhere (no resolver) is judged as before.
        var plain = new SemanticResult { Types = [Type("Legacy", "src/Legacy/A.cs")] };
        SemanticFindings.Apply(plain, rules.Base, null);
        await Assert.That(plain.Findings.Any(f => f.Rule == RuleIds.HighCoupling)).IsTrue();
    }

    [Test]
    public async Task The_unused_code_walk_runs_when_any_settings_in_use_switch_an_unused_rule_on()
    {
        var o = Parse("""{ "version": 1, "rules": { "unused-member": false, "unused-type": false, "used-only-by-tests": false, "unread-member": false }, "overrides": [ { "files": ["src/Core/**"], "rules": { "unused-member": true } } ] }""");
        var rules = o.PathRules();
        MeasureContext Context() => new(_repo, new SemanticOptions(), _ => true, rules.Base, "test", null) { SettingsInUse = rules.InUse() };
        await Assert.That(SemanticRunner.WalkPlan.For(Context(), []).Unused).IsFalse();      // no file under the entry yet
        _ = rules.For("src/Core/A.cs", false);
        await Assert.That(SemanticRunner.WalkPlan.For(Context(), []).Unused).IsTrue();
        await Assert.That(SemanticRunner.WalkPlan.For(new MeasureContext(_repo, new SemanticOptions(), _ => true, rules.Base, "test", null), []).Unused).IsFalse();
    }

    // ---- baselines --------------------------------------------------------------------------------------------------

    [Test]
    public async Task Baseline_entries_are_re_judged_by_the_settings_of_their_path_a_deleted_file_by_its_globs_as_production_code()
    {
        var o = Parse("""
            { "version": 1, "thresholds": { "long-method": { "notice": 50, "warning": 100 } },
              "overrides": [ { "files": ["src/Legacy/**"], "thresholds": { "long-method": { "warning": 300 } } },
                             { "tests": true, "thresholds": { "long-method": { "warning": 400 } } } ] }
            """);
        var rules = o.PathRules();
        var result = new AnalysisResult { Files = [new FileResult { Path = "src/T.cs", TestCode = true }] };
        var rulesOf = SkarpCommand.RulesOf(rules, result)!;
        BaselineEntry Entry(string path) => new() { Id = $"{RuleIds.LongMethod}|{path}|M()", Value = 150 };
        var byPath = HealthBaseline.ByPath([Entry("src/Legacy/Gone.cs"), Entry("src/Other.cs"), Entry("src/T.cs"), Entry("src/Gone.Tests.cs")], new Dictionary<string, Finding>(), rules.Base, rulesOf);
        await Assert.That(byPath["src/Legacy/Gone.cs"][0].Severity).IsEqualTo("notice");   // deleted, matched by its glob
        await Assert.That(byPath["src/Other.cs"][0].Severity).IsEqualTo("warning");        // repository settings
        await Assert.That(byPath["src/T.cs"][0].Severity).IsEqualTo("notice");             // test code in this run
        await Assert.That(byPath["src/Gone.Tests.cs"][0].Severity).IsEqualTo("warning");   // deleted: production code
        // Pathless entries (clone groups, namespace cycles) keep the repository settings.
        await Assert.That(SkarpCommand.RulesOf(Parse("""{ "version": 1 }""").PathRules(), result)).IsNull();
    }

    // ---- end to end: analyze, config, setup, nested files -----------------------------------------------------------

    (int Exit, string Out, string Err, string Json) Run(params string[] args)
    {
        var o = new StringWriter { NewLine = "\n" };
        var e = new StringWriter { NewLine = "\n" };
        var stdout = new MemoryStream();
        int exit = SkarpCommand.RunAsync(args, new CommandIO { Out = o, Error = e, OpenStdout = () => stdout, SettingsLocations = None, WorkingDirectory = _repo })
            .GetAwaiter().GetResult();
        return (exit, o.ToString(), e.ToString(), Encoding.UTF8.GetString(stdout.ToArray()));
    }

    string Scrub(string text) => text.Replace(_repo, "<repo>", StringComparison.Ordinal).Replace(_repo.Replace("\\", "\\\\"), "<repo>", StringComparison.Ordinal)
        .Replace('\\', '/').Replace("//", "/").Replace("\r\n", "\n");

    const string Sample = """
        {
          "version": 1,
          "rules": { "magic-number": false },
          "thresholds": { "long-method": { "notice": 12 } },
          "overrides": [
            { "files": ["src/Legacy/**"], "excludeFiles": ["src/Legacy/Keep/**"], "rules": { "magic-number": true }, "thresholds": { "long-method": { "notice": 100 } } },
            { "files": ["**/*.Gen.cs"], "rules": { "long-method": false, "complex-method": false } },
            { "tests": true, "thresholds": { "complex-method": { "notice": 25 } } },
            { "files": ["tools/**"], "rules": { "empty-catch": false } }
          ]
        }
        """;

    const string LongMethod = """
        namespace N;
        public class C
        {
            public int M(int x)
            {
                int y = 0;
                y += x * 3;
                y += x * 4;
                y += x * 5;
                y += x * 6;
                y += x * 7;
                y += x * 8;
                y += x * 9;
                y += x * 10;
                y += x * 11;
                y += x * 12;
                return y;
            }
        }
        """;

    void WriteSample()
    {
        File.WriteAllText(RepoFile, Sample);
        foreach (var (rel, text) in new[] { ("src/Legacy/Old.cs", LongMethod), ("src/Legacy/Keep/Kept.cs", LongMethod), ("src/New.cs", LongMethod), ("src/Api.Gen.cs", LongMethod) })
        {
            var path = Path.Combine(_repo, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text.Replace("class C", "class C" + Path.GetFileNameWithoutExtension(rel).Replace(".", "")));
        }
    }

    [Test]
    public async Task Analyze_judges_each_file_by_its_overrides_and_reports_them()
    {
        WriteSample();
        var r = Run("analyze", _repo, "--no-history", "--no-semantic", "--no-save", "--format", "json");
        await Assert.That(r.Exit).IsEqualTo(0);
        var json = JsonNode.Parse(r.Json)!;
        var longMethod = json["findings"]!.AsArray().Where(f => (string?)f!["rule"] == RuleIds.LongMethod).Select(f => (string)f!["path"]!).Order(StringComparer.Ordinal).ToList();
        await Assert.That(longMethod).IsEquivalentTo(new[] { "src/Legacy/Keep/Kept.cs", "src/New.cs" });
        var po = json["run"]!["findings"]!["pathOverrides"]!.AsArray();
        await Assert.That(po.Count).IsEqualTo(4);
        await Assert.That((int)po[0]!["matched"]!).IsEqualTo(1);
        await Assert.That((int)po[1]!["matched"]!).IsEqualTo(1);
        await Assert.That((int)po[3]!["matched"]!).IsEqualTo(0);
        await Assert.That(po[0]!["excludeFiles"]![0]!.GetValue<string>()).IsEqualTo("src/Legacy/Keep/**");
        await Assert.That((int)json["run"]!["findings"]!["pathOverrideFiles"]!).IsEqualTo(2);
        var notes = json["run"]!["findings"]!["notes"]!.AsArray().Select(n => (string)n!).ToList();
        await Assert.That(notes).Contains("override 3 selects test code, but tests are not analyzed (includeTests is off)");
        var human = Run("analyze", _repo, "--no-history", "--no-semantic", "--no-save", "--verbose");
        await Assert.That(human.Out).Contains("overrides:  2 apply (2 files matched); 2 match nothing (skarp config lists them)");
    }

    [Test]
    public async Task Config_lists_the_overrides_and_shows_one_file_with_origins()
    {
        WriteSample();
        var text = Run("config", _repo, "--file", "src/Legacy/Old.cs", "--threshold", "complex-method.warning=40");
        await Assert.That(text.Exit).IsEqualTo(0);
        var lines = Scrub(text.Out).Split('\n');
        var shown = string.Join('\n', lines.SkipWhile(l => !l.StartsWith("Overrides (", StringComparison.Ordinal))) + "\n";
        await Snapshots.Verify(shown, Path.Combine("Golden", "expected"), null, "txt", "SKARP_UPDATE_GOLDEN");
        var json = JsonNode.Parse(Run("config", _repo, "--format", "json", "--file", "src/New.cs").Json)!;
        var view = new JsonObject { ["overrides"] = json["overrides"]!.DeepClone(), ["file"] = json["file"]!.DeepClone() };
        await Snapshots.Verify(view.ToJsonString(new() { WriteIndented = true }) + "\n", Path.Combine("Golden", "expected"), "OverridesTests.Config_json_file_view", "json", "SKARP_UPDATE_GOLDEN");
        await Assert.That(Run("config", _repo, "--file", "src/Nope.cs").Err).Contains("--file: file not found");
        await Assert.That(Run("analyze", _repo, "--file", "src/New.cs").Err).Contains("--file applies to the config command only");
    }

    [Test]
    public async Task Setup_keeps_overrides_shows_them_read_only_and_refuses_to_set_them()
    {
        WriteSample();
        var before = JsonNode.Parse(File.ReadAllText(RepoFile))!["overrides"]!.ToJsonString();
        var set = Run("setup", "--level", "repository", "--set", "top=7");
        await Assert.That(set.Exit).IsEqualTo(0).Because(set.Err);
        var after = JsonNode.Parse(File.ReadAllText(RepoFile))!;
        await Assert.That((int)after["top"]!).IsEqualTo(7);
        await Assert.That(after["overrides"]!.ToJsonString()).IsEqualTo(before);
        var refused = Run("setup", "--level", "repository", "--set", "overrides=[]");
        await Assert.That(refused.Exit).IsEqualTo(2);
        await Assert.That(refused.Err).Contains("overrides: skarp setup does not edit them; edit");
        await Assert.That(refused.Err).Contains(SettingsFileReader.FileName);
        var m = SetupCommand.Open(SettingOrigin.Repository, _repo, null, None);
        var row = SkarpSharp.Setup.SetupRows.Rows(m, SettingSection.Lists).Single(r => r.Key == "overrides");
        await Assert.That(SkarpSharp.Setup.SetupRows.View(m, row).Value).IsEqualTo("4 entries (edit the file)");
    }

    [Test]
    public async Task A_nested_settings_file_is_noted_not_applied()
    {
        File.WriteAllText(RepoFile, """{ "version": 1, "exclude": ["skipped/**"] }""");
        foreach (var dir in (string[])["sub", "skipped", Path.Combine("sub", "obj")])
        {
            Directory.CreateDirectory(Path.Combine(_repo, dir));
            File.WriteAllText(Path.Combine(_repo, dir, SettingsFileReader.FileName), """{ "version": 1, "rules": { "long-method": false } }""");
        }
        File.WriteAllText(Path.Combine(_repo, "sub", "A.cs"), LongMethod.Replace("notice", ""));
        var r = Run("analyze", _repo, "--no-history", "--no-semantic", "--no-save", "--format", "json");
        var notes = JsonNode.Parse(r.Json)!["run"]!["findings"]!["notes"]!.AsArray().Select(n => Scrub((string)n!)).ToList();
        await Assert.That(notes).Contains($"found sub/.skarp.json: not applied; use overrides in <repo>/{SettingsFileReader.FileName}");
        await Assert.That(notes.Count(n => n.StartsWith("found ", StringComparison.Ordinal))).IsEqualTo(1);
    }
}
