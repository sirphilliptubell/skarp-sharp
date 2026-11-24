using System.Globalization;
using System.Text;
using System.Text.Json;
using SkarpSharp.Coverage;
using SkarpSharp.Semantic;
using SkarpSharp.Tests.Baselines;

namespace SkarpSharp.Tests.Coverage;

/// <summary>
/// The mock solution (tests/SkarpSharp.Tests/Mock, our own pattern code; each
/// pattern file names the finding it must raise) is copied to a temporary folder, built, and its TUnit tests run with
/// Microsoft code coverage (Cobertura); skarp then reads the report. The findings and coverage facts are compared with
/// Verify snapshots (Coverage/MockSolutionTests.*.verified.txt): a fresh report, and a stale copy (a file edited after the
/// report) that must show the staleness warning. Slow (about 15 s); skipped with the reason when the SDK cannot build or
/// test the mock (no SDK, no packages), unless SKARP_MOCK_REQUIRED=1 (then a broken environment fails).
/// </summary>
[Category("slow")]
[NotInParallel(nameof(MockSolutionTests))]
public sealed class MockSolutionTests
{
    sealed record Built(string Root, string Report, string? Failure);

    static readonly Lazy<Built> Mock = new(Build);

    static string Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SkarpSharp.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "SkarpSharp.Tests", "Mock");
    }

    static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
        {
            var name = Path.GetFileName(f);
            if (name.EndsWith(".fixture", StringComparison.Ordinal)) name = name[..^".fixture".Length];
            File.Copy(f, Path.Combine(to, name));
        }
        foreach (var d in Directory.GetDirectories(from))
            if (Path.GetFileName(d) is not ("bin" or "obj" or "TestResults")) Copy(d, Path.Combine(to, Path.GetFileName(d)));
    }

    static Built Build()
    {
        var root = Path.Combine(Path.GetTempPath(), "skarp-mock-" + Guid.NewGuid().ToString("N")[..8]);
        Copy(Source(), root);
        var results = Path.Combine(root, "TestResults");
        var run = DotnetRunner.RunAsync(["test", "--solution", "Mock.slnx", "--coverage", "--coverage-output-format", "cobertura",
            "--coverage-output", "mock.cobertura.xml", "--results-directory", results], root).GetAwaiter().GetResult();
        var report = Path.Combine(results, "mock.cobertura.xml");
        var failure = run.Succeeded && File.Exists(report) ? null : "the mock solution could not be built or tested with coverage: " + run.ErrorSummary();
        // The sources are older than the report, as after a build (the copy kept no times worth relying on).
        if (failure is null)
            foreach (var f in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddHours(-1));
        return new Built(root, report, failure);
    }

    static Built Ready()
    {
        var m = Mock.Value;
        if (m.Failure is not null)
        {
            if (Environment.GetEnvironmentVariable("SKARP_MOCK_REQUIRED") == "1") throw new InvalidOperationException(m.Failure);
            Skip.Test(m.Failure);
        }
        return m;
    }

    static (int Exit, string Err, JsonDocument Json) Analyze(Built m, params string[] extra)
    {
        var r = BaselineTests.Run(["analyze", m.Root, "--no-save", "--no-history", "--no-semantic", "--no-config", "--format", "json", "--coverage", m.Report, .. extra]);
        // --strict-coverage stops before any output: an empty document then.
        return (r.Exit, r.Err, JsonDocument.Parse(r.Json.Length > 0 ? r.Json : "{}"));
    }

    static string Facts(JsonElement c) =>
        c.GetProperty("status").GetString() + (c.TryGetProperty("coveredLines", out var cov) ? $" {cov.GetInt32()} of {c.GetProperty("coverableLines").GetInt32()}" : "")
        + (c.TryGetProperty("inactiveRegions", out _) ? ", inactive regions" : "");

    /// <summary>The run as text for the snapshot: the coverage block (report paths and times left out), every file's and
    /// member's facts, the untested-complex-method findings and the warnings.</summary>
    static string Describe(JsonDocument json)
    {
        var sb = new StringBuilder();
        var c = json.RootElement.GetProperty("coverage");
        int I(string name) => c.GetProperty(name).GetInt32();
        sb.Append(CultureInfo.InvariantCulture, $"reports: {string.Join(", ", c.GetProperty("reports").EnumerateArray().Select(r => r.GetProperty("producer").GetString()))}\n");
        sb.Append(CultureInfo.InvariantCulture, $"files: {I("matchedFiles")} matched, {I("unmatchedFiles")} unmatched, {I("ambiguousFiles")} ambiguous, {I("notAnalyzedFiles")} not analyzed, {I("outsideFiles")} outside\n");
        sb.Append(CultureInfo.InvariantCulture, $"data: {I("filesWithData")} files with data, {I("filesWithoutData")} without; {I("coveredLines")} of {I("coverableLines")} lines covered; {I("inactiveCoveredLines")} covered lines in inactive code; stale {c.GetProperty("stale").GetBoolean()}\n");
        sb.Append("projects:\n");
        foreach (var p in c.GetProperty("projects").EnumerateArray())
            sb.Append(CultureInfo.InvariantCulture, $"  {p.GetProperty("path").GetString()}: {p.GetProperty("files").GetInt32()} files, {p.GetProperty("filesWithData").GetInt32()} with data: {Facts(p)}\n");
        sb.Append("files:\n");
        foreach (var f in json.RootElement.GetProperty("files").EnumerateArray())
        {
            sb.Append(CultureInfo.InvariantCulture, $"  {f.GetProperty("path").GetString()}: {Facts(f.GetProperty("coverage"))}\n");
            foreach (var m in f.GetProperty("members").EnumerateArray().Where(m => m.TryGetProperty("coverage", out _)))
                sb.Append(CultureInfo.InvariantCulture, $"    {m.GetProperty("key").GetString()} (cyclomatic {m.GetProperty("cyclomatic").GetInt32()}): {Facts(m.GetProperty("coverage"))}\n");
        }
        sb.Append("untested-complex-method:\n");
        foreach (var f in json.RootElement.GetProperty("findings").EnumerateArray().Where(f => f.GetProperty("rule").GetString() == "untested-complex-method"))
            sb.Append(CultureInfo.InvariantCulture, $"  {f.GetProperty("severity").GetString()} {f.GetProperty("path").GetString()}:{f.GetProperty("line").GetInt32()} {f.GetProperty("id").GetString()}: {f.GetProperty("message").GetString()}\n");
        sb.Append("warnings:\n");
        foreach (var w in c.GetProperty("warnings").EnumerateArray())
            sb.Append(CultureInfo.InvariantCulture, $"  {w.GetProperty("kind").GetString()}: {w.GetProperty("message").GetString()} [{string.Join(", ", w.GetProperty("files").EnumerateArray().Select(x => x.GetString()))}]\n");
        return sb.ToString();
    }

    static Task Snapshot(string text, string name) =>
        Snapshots.Verify(text, "Coverage", $"MockSolutionTests.{name}", "txt", "SKARP_UPDATE_GOLDEN");

    [Test]
    public async Task Fresh_report_gives_the_expected_findings_and_facts()
    {
        var m = Ready();
        var (exit, err, json) = Analyze(m);
        await Assert.That(exit).IsEqualTo(0);
        // A fresh report: no staleness warning of either kind (0 false warnings).
        await Assert.That(err).DoesNotContain("older than the code");
        await Assert.That(err).DoesNotContain("changed after the newest report");
        await Snapshot(Describe(json), "Fresh");
    }

    [Test]
    public async Task Mapping_agrees_with_the_report_per_file()
    {
        // The mapping oracle: every analyzed file's coverable and covered counts equal the report's
        // own distinct lines for it (no [ExcludeFromCodeCoverage] code is in the report, no line lies beyond a file's end).
        var m = Ready();
        var (_, _, json) = Analyze(m);
        var report = CoberturaReader.Read(m.Report);
        var paths = new CoveragePaths(m.Root, json.RootElement.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()!));
        var expected = new Dictionary<string, (int Coverable, int Covered)>(StringComparer.Ordinal);
        foreach (var (name, lines) in report.Files)
            if (paths.Resolve(name, report.Sources).File is { } file) expected[file] = (lines.Count, lines.Count(l => l.Value));
        await Assert.That(expected.Count).IsGreaterThan(10);
        foreach (var f in json.RootElement.GetProperty("files").EnumerateArray())
        {
            var path = f.GetProperty("path").GetString()!;
            var c = f.GetProperty("coverage");
            if (!expected.TryGetValue(path, out var e)) { await Assert.That(c.GetProperty("status").GetString()).IsNotEqualTo("data"); continue; }
            await Assert.That((path, c.GetProperty("coverableLines").GetInt32(), c.GetProperty("coveredLines").GetInt32())).IsEqualTo((path, e.Coverable, e.Covered));
        }
    }

    [Test]
    [DependsOn(nameof(Fresh_report_gives_the_expected_findings_and_facts))]
    [DependsOn(nameof(Mapping_agrees_with_the_report_per_file))]
    [DependsOn(nameof(Baseline_keeps_untested_findings_without_a_report))]
    public async Task Stale_copy_shows_the_warning()
    {
        var m = Ready();
        // Edit a fully covered file after the report: three lines inserted after the class's opening brace move its code.
        var grades = Path.Combine(m.Root, "Mock.Lib", "Grades.cs");
        var text = File.ReadAllText(grades);
        // The checkout may have CRLF line endings (Windows), so find the line break after the brace instead of assuming "\n".
        int open = text.IndexOf('{', text.IndexOf("class Grades", StringComparison.Ordinal));
        int brace = text.IndexOf('\n', open) + 1;
        string eol = text[..brace].EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        File.WriteAllText(grades, text[..brace] + "    // inserted after the report" + eol + eol + "    // so the covered lines moved" + eol + text[brace..]);
        File.SetLastWriteTimeUtc(grades, DateTime.UtcNow.AddHours(1));
        try
        {
            var (exit, err, json) = Analyze(m);
            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(err).Contains("the reports look older than the code: 1 file has covered lines on blank, comment or declaration lines");
            await Assert.That(json.RootElement.GetProperty("coverage").GetProperty("stale").GetBoolean()).IsTrue();
            await Snapshot(Describe(json), "Stale");
            var (strict, strictErr, _) = Analyze(m, "--strict-coverage");
            await Assert.That(strict).IsEqualTo(2);
            await Assert.That(strictErr).Contains("--strict-coverage");
        }
        finally
        {
            File.WriteAllText(grades, text);
            File.SetLastWriteTimeUtc(grades, DateTime.UtcNow.AddHours(-1));
        }
    }

    [Test]
    public async Task Baseline_keeps_untested_findings_without_a_report()
    {
        var m = Ready();
        var baseline = Path.Combine(m.Root, "untested-baseline.json");
        try
        {
            BaselineTests.Run(["baseline", m.Root, "--no-history", "--no-semantic", "--no-config", "--baseline", baseline, "--coverage", m.Report]);
            var recorded = File.ReadAllText(baseline);
            // Variants.cs adds Tagged.Score, complex only with MOCK_LIB on (a variant) and untested.
            await Assert.That(recorded.Split('\n').Count(l => l.Contains("\"rule\": \"untested-complex-method\"", StringComparison.Ordinal))).IsEqualTo(5);
            var r = BaselineTests.Run(["analyze", m.Root, "--no-save", "--no-history", "--no-semantic", "--no-config", "--format", "json", "--baseline", baseline]);
            using var json = JsonDocument.Parse(r.Json);
            var b = json.RootElement.GetProperty("baseline");
            await Assert.That((b.GetProperty("fixed").GetInt32(), b.GetProperty("notEvaluated").GetInt32())).IsEqualTo((0, 5));
        }
        finally { File.Delete(baseline); }
    }
}
