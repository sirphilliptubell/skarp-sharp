using System.Text.Json;
using System.Text.Json.Nodes;
using SkarpSharp.Tests.Baselines;

namespace SkarpSharp.Tests;

/// <summary>
/// End-to-end CLI golden tests: the commands run in process on the fixed input Golden/sample (its own
/// .skarp.json and .skarp-baseline.json, history off) and their human and JSON output is compared with
/// Golden/expected. Run-time details (durations, memory, runtime name, absolute paths) are scrubbed.
/// After an intended output change: set SKARP_UPDATE_GOLDEN=1, run the tests once, review the diff of Golden/expected.
/// Compared through Verify.TUnit (<see cref="Snapshots"/>); the expected output of each test is
/// Golden/expected/CliGoldenTests.{test}.verified.{ext}.
/// </summary>
public class CliGoldenTests
{
    static readonly string Repo = FindRepo();
    static readonly string Sample = Path.Combine(Repo, "tests", "SkarpSharp.Tests", "Golden", "sample");

    static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SkarpSharp.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root (SkarpSharp.slnx) not found");
    }

    /// <summary>Scrubs the human, Markdown and annotation outputs: the run line (duration, memory, runtime) goes, the sample's
    /// absolute path becomes &lt;root&gt;, and paths on the command and &lt;root&gt; lines use forward slashes. Verify
    /// normalizes line endings to \n.</summary>
    static void Normalize(VerifySettings settings)
    {
        settings.AddScrubber(sb =>
        {
            var text = sb.ToString().Replace(Sample, "<root>", StringComparison.Ordinal).Replace(Sample.Replace('\\', '/'), "<root>", StringComparison.Ordinal);
            var lines = text.Split('\n').Where(l => !l.StartsWith("run:        ", StringComparison.Ordinal))
                .Select(l => l.StartsWith("skarp ", StringComparison.Ordinal) || l.Contains("<root>", StringComparison.Ordinal) ? l.Replace('\\', '/') : l);
            sb.Clear().Append(string.Join('\n', lines));
        });
    }

    static string NormalizeJson(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var run = root["run"]!.AsObject();
        foreach (var k in new[] { "durationSeconds", "peakWorkingSetBytes", "runtime" }) run.Remove(k);
        run["root"] = "<root>";
        foreach (var f in run["settingsFiles"]?.AsArray() ?? []) f!["path"] = "<root>/.skarp.json";
        if (root["baseline"] is JsonObject b) b["path"] = "<root>/.skarp-baseline.json";
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    /// <summary>Scrubs the JSON output: the run's duration, memory and runtime go; the root, settings and baseline paths
    /// become &lt;root&gt;; the document is written indented with \n line ends and a final newline.</summary>
    static void NormalizeJson(VerifySettings settings) =>
        settings.AddScrubber(sb => { var json = NormalizeJson(sb.ToString()); sb.Clear().Append(json); });

    /// <summary>Compares the test's output with Golden/expected/CliGoldenTests.{test}.verified.{extension}.</summary>
    static Task Golden(string extension, string actual, Action<VerifySettings>? scrub = null) =>
        Snapshots.Verify(actual, Path.Combine("Golden", "expected"), null, extension, "SKARP_UPDATE_GOLDEN", scrub);

    static (int Exit, string Out, string Err, string Json) Run(params string[] args) =>
        // Analyze saves .skarp/last-run/ by default; not in the source tree (the saved run is tested in Agent/).
        BaselineTests.Run([args[0], Sample, "--parallelism", "2", .. (args[0] == "analyze" ? new[] { "--no-save" } : []), .. args[1..]]);

    [Test]
    public async Task Analyze_human_report()
    {
        var r = Run("analyze");
        await Assert.That(r.Exit).IsEqualTo(0);
        await Assert.That(r.Err).IsEqualTo("");
        await Golden("txt", r.Out, Normalize);
    }

    /// <summary>--verbose adds the run header, the health block, each rule's sentence and the
    /// tables to the short text report.</summary>
    [Test]
    public async Task Analyze_verbose()
    {
        var r = Run("analyze", "--verbose");
        await Assert.That(r.Exit).IsEqualTo(0);
        await Assert.That(r.Err).IsEqualTo("");
        await Golden("txt", r.Out, Normalize);
    }

    [Test]
    public async Task Analyze_new_only()
    {
        var r = Run("analyze", "--new-only");
        await Assert.That(r.Exit).IsEqualTo(0);
        await Golden("txt", r.Out, Normalize);
    }

    [Test]
    public async Task Analyze_by_rule_with_everything_listed_and_no_baseline()
    {
        var r = Run("analyze", "--group", "rule", "--all", "--no-baseline", "--magic-numbers", "list");
        await Assert.That(r.Exit).IsEqualTo(0);
        await Golden("txt", r.Out, Normalize);
    }

    [Test]
    public async Task Analyze_json_schema_1()
    {
        var r = Run("analyze", "--format", "json");
        await Assert.That(r.Exit).IsEqualTo(0);
        await Assert.That(r.Err).StartsWith("skarp analyze: 2 files");
        await Assert.That(NormalizeJson(r.Json)).Contains("\"schemaVersion\": 1");
        await Golden("json", r.Json, NormalizeJson);
    }

    [Test]
    public async Task Analyze_with_gate_tripped_and_per_file_cap()
    {
        var r = Run("analyze", "--gate", "--gate-on", "*:notice", "--per-file", "2");
        await Assert.That(r.Exit).IsEqualTo(3);
        await Golden("txt", r.Out, Normalize);
    }

    [Test]
    public async Task Analyze_sort_health()
    {
        var r = Run("analyze", "--sort", "health", "--top", "2");
        await Assert.That(r.Exit).IsEqualTo(0);
        await Golden("txt", r.Out, Normalize);
    }

    [Test]
    public async Task Analyze_sort_long_method()
    {
        var r = Run("analyze", "--sort", "long-method", "--top", "3");
        await Assert.That(r.Exit).IsEqualTo(0);
        await Golden("txt", r.Out, Normalize);
    }

    [Test]
    public async Task Analyze_markdown_summary()
    {
        var r = Run("analyze", "--format", "markdown", "--gate");
        await Assert.That(r.Exit).IsEqualTo(0);
        await Golden("md", r.Out, Normalize);
    }

    [Test]
    public async Task Analyze_github_annotations()
    {
        var r = Run("analyze", "--format", "github", "--gate", "--gate-on", "*:*");
        await Assert.That(r.Exit).IsEqualTo(3);
        await Golden("txt", r.Out, Normalize);
    }

    [Test]
    public async Task Analyze_github_annotations_without_a_baseline_annotate_warnings()
    {
        // Warnings only (within the limit), and the log suggests committing a baseline.
        var r = Run("analyze", "--format", "github", "--no-baseline");
        await Assert.That(r.Exit).IsEqualTo(0);
        await Assert.That(r.Out).DoesNotContain("::notice");
        await Assert.That(r.Out).Contains("::warning ");
        await Assert.That(r.Out).Contains("commit .skarp-baseline.json");
        await Golden("txt", r.Out, Normalize);
    }

    [Test]
    public async Task Analyze_markdown_without_a_baseline_suggests_one()
    {
        var r = Run("analyze", "--format", "markdown", "--no-baseline");
        await Assert.That(r.Exit).IsEqualTo(0);
        await Assert.That(r.Out).Contains("commit `.skarp-baseline.json`");
        await Golden("md", r.Out, Normalize);
    }

    [Test]
    public async Task Config_shows_settings_and_origins()
    {
        var r = Run("config", "--top", "5", "--threshold", "deep-nesting.notice=5");
        await Assert.That(r.Exit).IsEqualTo(0);
        await Golden("txt", r.Out, Normalize);
    }

    [Test]
    public async Task Baseline_writes_the_expected_file()
    {
        var file = Path.Combine(Path.GetTempPath(), "skarp-golden-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var r = Run("baseline", "--baseline", file);
            await Assert.That(r.Exit).IsEqualTo(0);
            await Assert.That(r.Out).Contains("recorded 12 findings");
            await Golden("json", File.ReadAllText(file));
        }
        finally { File.Delete(file); }
    }

    [Test]
    public async Task Usage_errors_and_explain()
    {
        var bad = BaselineTests.Run("analyze", Sample, "--frobnicate");
        await Assert.That(bad.Exit).IsEqualTo(2);
        await Assert.That(bad.Err).StartsWith("Unknown option: --frobnicate");
        await Assert.That(bad.Err).Contains("Usage:");
        var help = BaselineTests.Run("--help");
        await Assert.That(help.Exit).IsEqualTo(0);
        await Assert.That(help.Out).Contains("skarp baseline <directory>");
        var explain = BaselineTests.Run("explain", "magic-number");
        await Assert.That(explain.Exit).IsEqualTo(0);
        await Assert.That(explain.Out).StartsWith("magic-number: ");
        await Assert.That(BaselineTests.Run("explain", "nope").Exit).IsEqualTo(2);
        var missing = BaselineTests.Run("analyze", Path.Combine(Sample, "missing"));
        await Assert.That((missing.Exit, missing.Err.StartsWith("Directory not found", StringComparison.Ordinal))).IsEqualTo((2, true));
        var badConfig = BaselineTests.Run("analyze", Sample, "--config", Path.Combine(Sample, "Sample.csproj"));
        await Assert.That(badConfig.Exit).IsEqualTo(2);
        await Assert.That(badConfig.Err).Contains("Sample.csproj: (root): ");
    }
}
