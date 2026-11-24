using System.Text.Json;
using System.Text.Json.Nodes;
using NJsonSchema;
using SkarpSharp.Findings;
using SkarpSharp.Model;
using SkarpSharp.Reporting;
using SkarpSharp.Tests.Baselines;

namespace SkarpSharp.Tests.SarifOutput;

/// <summary>
/// <c>skarp analyze --format sarif</c>. The output is validated against the official
/// SARIF 2.1.0 JSON schema (Reporting/sarif-schema-2.1.0.json, vendored unchanged from OASIS; source and notice in
/// sarif-schema-2.1.0.NOTICE.txt) with NJsonSchema (MIT, test only), and its content is checked against the JSON
/// output of the same run. Contract: docs/contracts.md section 9.
/// The golden SARIF of the CLI sample is Golden/expected/SarifTests.Output_is_deterministic_and_matches_the_golden_file.verified.sarif
/// (Verify; SKARP_UPDATE_GOLDEN=1 rewrites it).
/// </summary>
public class SarifTests
{
    static readonly string Repo = FindRepo();
    static readonly string Sample = Path.Combine(Repo, "tests", "SkarpSharp.Tests", "Golden", "sample");
    static readonly Lazy<JsonSchema> Schema = new(() =>
        JsonSchema.FromFileAsync(Path.Combine(AppContext.BaseDirectory, "Reporting", "sarif-schema-2.1.0.json")).GetAwaiter().GetResult());

    static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SkarpSharp.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root (SkarpSharp.slnx) not found");
    }

    static (int Exit, string Sarif, string Err) Sarif(params string[] more)
    {
        var r = BaselineTests.Run(["analyze", Sample, "--parallelism", "2", "--no-save", "--format", "sarif", .. more]);
        return (r.Exit, r.Json, r.Err);
    }

    static string Json(params string[] more) => BaselineTests.Run(["analyze", Sample, "--parallelism", "2", "--no-save", "--format", "json", .. more]).Json;

    internal static async Task AssertValid(string sarif, string what)
    {
        var errors = Schema.Value.Validate(sarif);
        await Assert.That(errors.Count == 0).IsTrue().Because($"{what} is not valid SARIF 2.1.0:\n  " + string.Join("\n  ", errors.Select(e => $"{e.Path}: {e.Kind} {e.Property}")));
    }

    static JsonElement Run(string sarif) => JsonDocument.Parse(sarif).RootElement.GetProperty("runs")[0];

    static string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

    // ---- schema ----

    [Test]
    public async Task Output_with_a_baseline_validates_against_the_official_schema()
    {
        var r = Sarif();
        await Assert.That(r.Exit).IsEqualTo(0);
        await AssertValid(r.Sarif, "sarif with baseline");
    }

    [Test]
    public async Task Output_without_a_baseline_validates_and_has_no_baseline_state()
    {
        var r = Sarif("--no-baseline");
        await AssertValid(r.Sarif, "sarif without baseline");
        await Assert.That(r.Sarif).DoesNotContain("baselineState");
    }

    [Test]
    public async Task The_schema_validator_rejects_an_invalid_level()
    {
        var node = JsonNode.Parse(Sarif().Sarif)!;
        node["runs"]![0]!["results"]![0]!["level"] = "notice";
        await Assert.That(Schema.Value.Validate(node.ToJsonString())).IsNotEmpty();
    }

    // ---- content ----

    [Test]
    public async Task One_run_with_the_driver_and_every_rule_of_the_catalog()
    {
        var run = Run(Sarif().Sarif);
        await Assert.That(JsonDocument.Parse(Sarif().Sarif).RootElement.GetProperty("runs").GetArrayLength()).IsEqualTo(1);
        var driver = run.GetProperty("tool").GetProperty("driver");
        await Assert.That(S(driver, "name")).IsEqualTo("skarp");
        await Assert.That(S(driver, "semanticVersion")).Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$");
        await Assert.That(S(driver, "informationUri")).StartsWith("https://");
        var rules = driver.GetProperty("rules").EnumerateArray().ToList();
        // Brain-method is on by default, so it follows every rule of the catalog (brain-class is off: not listed).
        await Assert.That(rules.Select(r => S(r, "id"))).IsEquivalentTo([.. RuleCatalog.Every.Select(r => r.Id), RuleIds.BrainMethod], CollectionOrdering.Matching);
        foreach (var rule in rules)
        {
            var info = RuleCatalog.Get(S(rule, "id"));
            await Assert.That(S(rule.GetProperty("shortDescription"), "text")).IsEqualTo(info.Short);
            await Assert.That(S(rule.GetProperty("fullDescription"), "text").Length).IsBetween(1, SarifReport.MaxDescriptionLength);
            await Assert.That(S(rule.GetProperty("fullDescription"), "text")).StartsWith("What it measures");
            await Assert.That(S(rule.GetProperty("help"), "text")).IsEqualTo(SarifReport.ExplainText(info));
            await Assert.That(S(rule.GetProperty("help"), "markdown")).IsNotEmpty();
            await Assert.That(new[] { "warning", "note" }).Contains(S(rule.GetProperty("defaultConfiguration"), "level"));
            await Assert.That(rule.GetProperty("properties").GetProperty("tags").EnumerateArray().Select(t => t.GetString())).Contains(info.Tier);
        }
        // Long-method has a warning level in the default preset; large-type is notice only.
        await Assert.That(S(rules.Single(r => S(r, "id") == "long-method").GetProperty("defaultConfiguration"), "level")).IsEqualTo("warning");
        await Assert.That(S(rules.Single(r => S(r, "id") == "large-type").GetProperty("defaultConfiguration"), "level")).IsEqualTo("note");
    }

    [Test]
    public async Task Every_finding_is_a_result_with_level_location_fingerprint_and_baseline_state()
    {
        var run = Run(Sarif().Sarif);
        var findings = JsonDocument.Parse(Json()).RootElement.GetProperty("findings").EnumerateArray().ToList();
        var results = run.GetProperty("results").EnumerateArray().ToList();
        await Assert.That(results.Count).IsEqualTo(findings.Count);
        await Assert.That(findings).Contains(f => S(f, "severity") == "warning");
        await Assert.That(findings).Contains(f => S(f, "severity") == "notice");
        var rules = run.GetProperty("tool").GetProperty("driver").GetProperty("rules");
        var root = run.GetProperty("originalUriBaseIds").GetProperty("SRCROOT");
        await Assert.That(S(root, "uri")).StartsWith("file:///");
        await Assert.That(S(root, "uri")).EndsWith("/");
        for (int i = 0; i < results.Count; i++)
        {
            var (f, r) = (findings[i], results[i]);
            await Assert.That(S(r, "ruleId")).IsEqualTo(S(f, "rule"));
            await Assert.That(S(rules[r.GetProperty("ruleIndex").GetInt32()], "id")).IsEqualTo(S(f, "rule"));
            await Assert.That(S(r, "level")).IsEqualTo(S(f, "severity") == "warning" ? "warning" : "note");
            await Assert.That(S(r.GetProperty("partialFingerprints"), SarifReport.FingerprintKey)).IsEqualTo(S(f, "id"));
            await Assert.That(S(r, "baselineState")).IsEqualTo(SarifReport.BaselineState(S(f, "baselineState")));
            await Assert.That(S(r.GetProperty("message"), "text")).Contains(S(f, "message"));
            var location = r.GetProperty("locations")[0];
            var physical = location.GetProperty("physicalLocation");
            // The sample lies inside this repository: URIs are relative to the git work tree root (GitHub resolves them there).
            await Assert.That(S(physical.GetProperty("artifactLocation"), "uri")).IsEqualTo("tests/SkarpSharp.Tests/Golden/sample/" + S(f, "path"));
            await Assert.That(S(physical.GetProperty("artifactLocation"), "uriBaseId")).IsEqualTo("SRCROOT");
            await Assert.That(physical.GetProperty("region").GetProperty("startLine").GetInt32()).IsEqualTo(f.GetProperty("line").GetInt32());
            if (S(f, "member") is { Length: > 0 } member)
            {
                var logical = location.GetProperty("logicalLocations")[0];
                await Assert.That(S(logical, "fullyQualifiedName")).IsEqualTo(member);
                await Assert.That(S(logical, "kind")).IsEqualTo(member.Contains("::", StringComparison.Ordinal) ? "member" : "type");
            }
            else await Assert.That(location.TryGetProperty("logicalLocations", out _)).IsFalse();
        }
        await Assert.That(run.GetProperty("properties").GetProperty("profileVersion").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task Known_and_worsened_findings_map_to_unchanged_and_updated()
    {
        await Assert.That(SarifReport.BaselineState("new")).IsEqualTo("new");
        await Assert.That(SarifReport.BaselineState("known")).IsEqualTo("unchanged");
        await Assert.That(SarifReport.BaselineState("worsened")).IsEqualTo("updated");
        await Assert.That(SarifReport.BaselineState(null)).IsNull();
        var states = Run(Sarif().Sarif).GetProperty("results").EnumerateArray().Select(r => S(r, "baselineState")).Distinct().ToList();
        await Assert.That(states).Contains("unchanged");
        await Assert.That(states).Contains("new");
    }

    [Test]
    public async Task Output_is_deterministic_and_matches_the_golden_file()
    {
        var a = Sarif().Sarif;
        await Assert.That(Sarif().Sarif).IsEqualTo(a);
        var root = Run(a).GetProperty("originalUriBaseIds").GetProperty("SRCROOT");
        // Through Verify (Golden/expected, Verify's default name); the repository's absolute file URI is scrubbed.
        var uri = S(root, "uri");
        await Snapshots.Verify(a, Path.Combine("Golden", "expected"), null, "sarif", "SKARP_UPDATE_GOLDEN",
            settings => settings.AddScrubber(sb => sb.Replace(uri, "file:///<repository>/")));
    }

    [Test]
    public async Task Output_option_writes_only_the_sarif_file()
    {
        var dir = Directory.CreateTempSubdirectory("skarp-sarif").FullName;
        try
        {
            var file = Path.Combine(dir, "skarp.sarif");
            var r = BaselineTests.Run("analyze", Sample, "--parallelism", "2", "--no-save", "--format", "sarif", "-o", file);
            await Assert.That(r.Exit).IsEqualTo(0);
            await Assert.That(r.Json).IsEqualTo("");
            await Assert.That(r.Out).IsEqualTo("");
            await Assert.That(r.Err).Contains("SARIF result");
            await AssertValid(File.ReadAllText(file), "sarif file");
            await Assert.That(Directory.GetFiles(dir).Select(Path.GetFileName)).IsEquivalentTo(new string?[] { "skarp.sarif" }, CollectionOrdering.Matching);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    [Arguments("baseline")]
    [Arguments("hotspots")]
    [Arguments("history")]
    public async Task Sarif_is_an_analyze_format_only(string command)
    {
        var r = BaselineTests.Run(command, Sample, "--format", "sarif");
        await Assert.That(r.Exit).IsEqualTo(2);
        await Assert.That(r.Err).Contains("--format sarif applies to the analyze command only");
    }

    // ---- builder rules ----

    static Finding F(string rule, string severity, string path, int line, string? member, string? state = null) => new()
    {
        Id = $"{rule}|{path}|{member}", Rule = rule, Severity = severity, Path = path, Line = line, Member = member, Message = "m", BaselineState = state,
    };

    static SarifInput Input(IReadOnlyList<Finding> syntax, bool syntaxBaseline, params SemanticResult[] semantic) => new()
    {
        ToolVersion = "0.1.0.0", SourceRoot = Path.GetTempPath(), PathPrefix = "sub dir/", Settings = RuleSettings.Resolve("default", null, []),
        SyntaxFindings = syntax, SyntaxBaseline = syntaxBaseline, SemanticResults = semantic, SemanticStatus = semantic.Length > 0 ? "ran" : "off",
    };

    [Test]
    public async Task Baseline_state_is_left_out_on_every_result_when_one_tier_has_no_baseline()
    {
        var semantic = new SemanticResult { Findings = [F("high-coupling", "notice", "A.cs", 3, "Ns.A")] };
        var log = SarifReport.Build(Input([F("long-method", "warning", "A.cs", 5, "Ns.A::M", "known")], true, semantic));
        var run = log.Runs[0];
        await Assert.That(run.Results).All(r => r.BaselineState == null);
        await Assert.That(run.Properties!.Notes!).Contains(n => n.StartsWith("baselineState left out", StringComparison.Ordinal));
        await Assert.That(run.Results.Select(r => r.Properties!.Tier)).IsEquivalentTo(["syntax", "semantic"], CollectionOrdering.Matching);
        await Assert.That(run.Results[1].Locations[0].LogicalLocations![0].Kind).IsEqualTo("type");
        await Assert.That(run.Results[1].Locations[0].LogicalLocations![0].Name).IsEqualTo("A");
        await AssertValid(JsonSerializer.Serialize(log, SarifJsonContext.Default.SarifLog), "mixed tiers");
    }

    [Test]
    public async Task Semantic_findings_repeated_per_target_framework_appear_once()
    {
        SemanticResult Tfm(string tfm) => new()
        {
            Run = new SemanticRunInfo { TargetFramework = tfm },
            Findings = [F("unused-member", "notice", "A.cs", 3, "Ns.A::M", "new")],
            Baseline = new BaselineSummary(),
        };
        var log = SarifReport.Build(Input([], false, Tfm("net10.0"), Tfm("net8.0")));
        var result = await Assert.That(log.Runs[0].Results).HasSingleItem();
        await Assert.That(result.Properties!.TargetFramework).IsEqualTo("net10.0");
        await Assert.That(result.BaselineState).IsEqualTo("new");
    }

    [Test]
    public async Task Paths_become_percent_encoded_relative_uris_under_the_prefix()
    {
        var log = SarifReport.Build(Input([F("large-file", "notice", "src/My File#1.cs", 1, null)], false));
        var location = log.Runs[0].Results[0].Locations[0];
        await Assert.That(location.PhysicalLocation.ArtifactLocation.Uri).IsEqualTo("sub%20dir/src/My%20File%231.cs");
        await Assert.That(location.PhysicalLocation.Region!.StartLine).IsEqualTo(1);
        await Assert.That(location.LogicalLocations).IsNull();
        await Assert.That(Uri.IsWellFormedUriString(log.Runs[0].OriginalUriBaseIds!["SRCROOT"].Uri, UriKind.Absolute)).IsTrue();
    }

    [Test]
    public async Task More_results_than_GitHub_accepts_keep_warnings_first_and_say_how_many_were_left_out()
    {
        var findings = Enumerable.Range(0, SarifReport.MaxResultsPerRun + 10)
            .Select(i => F("magic-number", i % 1000 == 999 ? "warning" : "notice", $"F{i}.cs", 1, null)).ToList();
        var run = SarifReport.Build(Input(findings, false)).Runs[0];
        await Assert.That(run.Results.Count).IsEqualTo(SarifReport.MaxResultsPerRun);
        await Assert.That(run.Properties!.ResultsOmitted).IsEqualTo(10);
        await Assert.That(run.Results.Count(r => r.Level == "warning")).IsEqualTo(findings.Count(f => f.Severity == "warning"));
        await Assert.That(run.Properties.Notes!).Contains(n => n.Contains("25000", StringComparison.Ordinal));
        // Report order is kept among the results that stay.
        var ids = run.Results.Select(r => int.Parse(r.Locations[0].PhysicalLocation.ArtifactLocation.Uri!["sub%20dir/F".Length..^3], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        await Assert.That(ids).IsEquivalentTo(ids.Order(), CollectionOrdering.Matching);
    }
}