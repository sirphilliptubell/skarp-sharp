using System.Text.Json;
using System.Text.RegularExpressions;
using SkarpSharp.Tests.Agent;
using SkarpSharp.Tests.History;
using static SkarpSharp.Tests.Agent.AgentHost;

namespace SkarpSharp.Tests.Health;

/// <summary>
/// The health outputs: the health line of <c>skarp check</c>
/// (on change, silent when unchanged, first and within the budget), the <c>Stop</c> summary, <c>show --file</c>, the
/// settings (unknown model or rule: exit 2), the JSON roll-ups with baseline values recomputed from the baseline's
/// findings, the reports and SARIF.
/// </summary>
public sealed class HealthLoopTests : IDisposable
{
    readonly AgentHost _host = new();
    readonly SyntheticRepo _repo = new();
    static readonly DateTimeOffset T0 = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
    /// <summary>A file heading with a health change (<c>Skarp: src/A.cs health 9.7 -&gt; 9.5 (10 = healthy)</c>).</summary>
    static readonly Regex HealthChange = new(@"^(Skarp: )?(?<path>\S+) health (?<old>\d+\.\d) -> (?<new>\d+\.\d)( \(10 = healthy\))?$");

    public HealthLoopTests() => _host.Now = T0;

    public void Dispose()
    {
        _host.Dispose();
        _repo.Dispose();
    }

    string Commit(string path, string content)
    {
        _repo.Write(path, content);
        _repo.Commit("add " + path, T0.AddDays(-1));
        return Path.Combine(_repo.Root, path);
    }

    // Returns a value, so it checks synchronously (Assert.Fail throws).
    List<string> Check(string file, params string[] extra)
    {
        var r = _host.Run(_repo.Root, ["check", file, "--track", "--session", "h1", .. extra]);
        if (r.Exit != 0) Assert.Fail(r.Err);
        return Lines(r.Out);
    }

    [Test]
    public async Task Check_prints_the_health_line_first_on_change_and_nothing_when_unchanged()
    {
        var file = Commit("src/A.cs", Class("A", Method(9)));
        await Assert.That(Check(file)).IsEmpty();                                         // first edit, same as HEAD: silent

        File.WriteAllText(file, Class("A", Method(9), Method(9, 1)));
        var lines = Check(file);
        var m = HealthChange.Match(lines[0]);
        await Assert.That(m.Success).IsTrue().Because(lines[0]);
        await Assert.That((m.Groups["path"].Value, m.Groups["old"].Value, m.Groups["new"].Value)).IsEqualTo(("src/A.cs", "9.7", "9.5"));
        await Assert.That(await Assert.That(FindingLines(string.Join("\n", lines))).HasSingleItem()).StartsWith("new ");

        File.AppendAllText(file, "// a comment only\n");
        await Assert.That(Check(file)).IsEmpty();                                         // same findings: no health line either

        // A notice changes the score but is not printed by default: the health line comes alone.
        File.WriteAllText(file, Class("A", Method(9), Method(9, 1), Method(5, 2)));
        await Assert.That(await Assert.That(Check(file)).HasSingleItem()).IsEqualTo("Skarp: src/A.cs health 9.5 -> 9.4 (10 = healthy)");

        File.WriteAllText(file, Class("A", Method(2)));
        lines = Check(file);
        await Assert.That(lines[0]).IsEqualTo("Skarp: src/A.cs health 9.4 -> 10.0 (10 = healthy)");
        var fixedLines = FindingLines(string.Join("\n", lines));
        await Assert.That(fixedLines).IsNotEmpty();
        foreach (var l in fixedLines) await Assert.That(l).StartsWith("fixed ");
    }

    [Test]
    public async Task Only_finding_lines_count_toward_the_5_line_budget()
    {
        var file = Commit("src/B.cs", Class("B"));
        File.WriteAllText(file, Class("B", [.. Enumerable.Range(0, 8).Select(i => Method(9, i))]));
        var lines = Check(file);
        await Assert.That(FindingLines(string.Join("\n", lines)).Count).IsEqualTo(5);   // the heading is free
        await Assert.That(lines[0]).Matches(HealthChange);
        await Assert.That(lines).Contains("+3 more: skarp show --session h1 --new-only");
        await Assert.That(Check(file, "--max-lines", "2")).IsEmpty();                    // unchanged now: silent
    }

    [Test]
    public async Task A_file_not_in_git_prints_its_score_when_below_10()
    {
        Commit("src/Other.cs", Class("Other"));
        _repo.Write("src/Clean.cs", Class("Clean", Method(1)));
        await Assert.That(Check(Path.Combine(_repo.Root, "src/Clean.cs"))).IsEmpty();
        _repo.Write("src/New.cs", Class("N", Method(9)));
        var lines = Check(Path.Combine(_repo.Root, "src/New.cs"));
        await Assert.That(lines[0]).IsEqualTo("Skarp: src/New.cs health 9.7 (new file; 10 = healthy)");
    }

    [Test]
    public async Task Stop_ranks_score_lines_first_worst_drop_first()
    {
        var a = Commit("src/A.cs", Class("A", Method(9)));
        var b = Commit("src/B.cs", Class("B", Method(9)));
        string Hook(string ev, string file) => _host.RunWithInput(_repo.Root, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = "hs", ["hook_event_name"] = ev, ["tool_name"] = "Edit", ["stop_hook_active"] = false,
            ["tool_input"] = new Dictionary<string, string?> { ["file_path"] = file },
        }), "check", "--hook", "claude").Out;
        Hook("PostToolUse", a);
        Hook("PostToolUse", b);
        File.WriteAllText(a, Class("A", Method(9), Method(5, 1)));                       // -0.1 (a notice)
        File.WriteAllText(b, Class("B", Method(9), Method(9, 1), Method(9, 2)));         // -0.4 (9.66 to 9.34)
        // Changed outside the edit tools: the Stop summary leaves out what an edit check printed.
        using var doc = JsonDocument.Parse(Hook("Stop", a));
        var text = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
        // A.cs's drop comes only from a notice the hook does not list, so it has no message.
        await Assert.That(HealthOnly(text)).IsEquivalentTo(["src/B.cs health 9.7 -> 9.3"], CollectionOrdering.Matching);
        await Assert.That(WithPaths(text)).IsNotEmpty();
        foreach (var l in WithPaths(text)) await Assert.That(l).StartsWith("src/B.cs: new ");
    }

    [Test]
    public async Task Show_file_lists_where_the_points_went()
    {
        var dir = _host.Folder("show");
        File.WriteAllText(Path.Combine(dir, ".skarp.json"), """{ "version": 1, "history": { "enabled": false }, "semantic": { "enabled": false } }""");
        File.WriteAllText(Path.Combine(dir, "S.cs"), Class("S", Method(9), Method(9, 1), Method(5, 2)));
        File.WriteAllText(Path.Combine(dir, "Clean.cs"), Class("Clean"));
        await Assert.That(_host.Run(dir, "analyze", dir).Exit).IsEqualTo(0);
        var lines = Lines(_host.Run(dir, "show", "--file", "S.cs", "--file", "Clean.cs").Out);
        await Assert.That(lines[0]).StartsWith("Skarp: 2 findings (saved ");
        await Assert.That(lines[1..5]).IsEquivalentTo(
        [
            "Clean.cs health 10.0 (10 = healthy)", "  points (healthy, syntax score): no findings",
            "S.cs health 9.4", "  points (healthy, syntax score): too-many-parameters 2 warnings -0.5, too-many-parameters 1 notice -0.1",
        ], CollectionOrdering.Matching);
        using var doc = JsonDocument.Parse(_host.Run(dir, "show", "--file", "S.cs", "--format", "json").Out);
        var h = await Assert.That(doc.RootElement.GetProperty("health").EnumerateArray()).HasSingleItem();
        await Assert.That(h.GetProperty("health").GetProperty("score").GetDouble()).IsEqualTo(9.42);
        await Assert.That(Lines(_host.Run(dir, "show").Out)).DoesNotContain(l => l.StartsWith("  points ", StringComparison.Ordinal));   // only with --file
    }

    [Test]
    public async Task Show_file_score_counts_the_clone_groups_touching_the_file()
    {
        // A clone group's id names its first copy's file, but its points are taken from every file
        // holding a copy; show --file now scores B.cs as analyze does.
        var dir = _host.Folder("show-clones");
        File.WriteAllText(Path.Combine(dir, ".skarp.json"), """{ "version": 1, "history": { "enabled": false }, "semantic": { "enabled": false } }""");
        var code = SkarpSharp.Tests.Clones.CloneHost.Method("A", "Run", 12);
        File.WriteAllText(Path.Combine(dir, "A.cs"), code);
        File.WriteAllText(Path.Combine(dir, "B.cs"), code.Replace("class A", "class B", StringComparison.Ordinal));
        await Assert.That(_host.Run(dir, "analyze", dir).Exit).IsEqualTo(0);
        using var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, ".skarp", "last-run", "analysis.json")));
        await Assert.That(run.RootElement.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("rule").GetString())).Contains("duplicate-code");
        var b = run.RootElement.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("path").GetString() == "B.cs").GetProperty("health").GetProperty("score").GetDouble();
        await Assert.That(b).IsLessThan(10);
        using var shown = JsonDocument.Parse(_host.Run(dir, "show", "--file", "B.cs", "--format", "json").Out);
        var h = await Assert.That(shown.RootElement.GetProperty("health").EnumerateArray()).HasSingleItem();
        await Assert.That(h.GetProperty("health").GetProperty("score").GetDouble()).IsEqualTo(b);
    }

    [Test]
    [Arguments("""{ "version": 1, "score": { "model": "sqale" } }""", "score.model: unknown model 'sqale' (known: grade)")]
    [Arguments("""{ "version": 1, "score": { "weights": { "long-methods": { "notice": 1 } } } }""", "score.weights.long-methods: unknown rule")]
    [Arguments("""{ "version": 1, "score": { "weights": { "long-method": { "notise": 1 } } } }""", "score.weights.long-method.notise: unknown key")]
    [Arguments("""{ "version": 1, "score": { "decay": 1.5 } }""", "score.decay: must be more than 0 and at most 1")]
    [Arguments("""{ "version": 1, "score": { "weights": { "long-method": { "cap": -1 } } } }""", "score.weights.long-method.cap: must be between 0 and 9 points")]
    public async Task Invalid_score_settings_exit_2(string settings, string message)
    {
        var dir = _host.Folder("bad" + Math.Abs(settings.GetHashCode()));
        File.WriteAllText(Path.Combine(dir, ".skarp.json"), settings);
        var r = _host.Run(dir, "analyze", dir, "--no-save");
        await Assert.That(r.Exit).IsEqualTo(2);
        await Assert.That(r.Err).Contains(message);
    }

    [Test]
    public async Task Score_model_flag_and_config()
    {
        var dir = _host.Folder("model");
        var r = _host.Run(dir, "analyze", dir, "--score-model", "mi");
        await Assert.That(r.Exit).IsEqualTo(2);
        await Assert.That(r.Err).Contains("Invalid value for --score-model: 'mi' (known: grade)");
        File.WriteAllText(Path.Combine(dir, ".skarp.json"), """{ "version": 1, "score": { "decay": 0.5, "weights": { "long-method": { "warning": 2 } }, "worst": { "count": 3, "below": 8 } } }""");
        var config = _host.Run(dir, "config", dir, "--score-model", "grade").Out;
        await Assert.That(config).Matches(@"score\.model\s+grade\s+command line");
        await Assert.That(config).Matches(@"score\.decay\s+0\.5\s+repository");
        await Assert.That(config).Matches(@"score\.weights\s+long-method warning 2\s+repository");
        await Assert.That(config).Matches(@"score\.worst\.count\s+3\s+repository");
        await Assert.That(config).Matches(@"score\.worst\.below\s+8\s+repository");
    }

    /// <summary>A file created after the baseline's commit is shown as new, with no delta, and both sides of
    /// the average comparison cover only the files that existed at that commit.</summary>
    [Test]
    public async Task Files_created_after_the_baseline_commit_are_new_not_a_drop_from_10()
    {
        File.WriteAllText(Path.Combine(_repo.Root, ".skarp.json"), """{ "version": 1, "semantic": { "enabled": false } }""");
        Commit("src/A.cs", Class("A", Method(9)));
        await Assert.That(_host.Run(_repo.Root, "baseline", _repo.Root).Exit).IsEqualTo(0);       // history on: records HEAD as its commit
        _repo.Write("src/New.cs", Class("N", Method(9), Method(9, 1), Method(9, 2)));
        File.WriteAllText(Path.Combine(_repo.Root, "src/A.cs"), Class("A", Method(9), Method(9, 1)));

        var output = Path.Combine(_host.Folder("newout"), "r.json");
        await Assert.That(_host.Run(_repo.Root, "analyze", _repo.Root, "--no-save", "--output", output).Exit).IsEqualTo(0);
        using var doc = JsonDocument.Parse(File.ReadAllText(output));
        var files = doc.RootElement.GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("path").GetString()!, f => f.GetProperty("health"));
        await Assert.That(files["src/New.cs"].GetProperty("inBaseline").GetBoolean()).IsFalse();
        await Assert.That(files["src/New.cs"].TryGetProperty("baselineScore", out _)).IsFalse();
        await Assert.That(files["src/A.cs"].GetProperty("baselineScore").GetDouble()).IsEqualTo(9.7);
        var b = doc.RootElement.GetProperty("health").GetProperty("baseline");
        await Assert.That(b.GetProperty("files").GetInt32()).IsEqualTo(1);
        await Assert.That((b.GetProperty("average").GetDouble(), b.GetProperty("averageNow").GetDouble())).IsEqualTo((9.7, 9.49));

        var human = _host.Run(_repo.Root, "analyze", _repo.Root, "--no-save", "--verbose").Out;
        await Assert.That(human).Contains("(baseline 9.7 over the 1 file it had, 9.5 now: -0.2)");
        await Assert.That(human).Contains("  new since the baseline (1 file, lowest first):");
        await Assert.That(human).Contains("     9.3  src/New.cs");
        await Assert.That(human).DoesNotContain("-> 9.3");
        await Assert.That(human).Contains("9.7 -> 9.5 (-0.2)  src/A.cs");
        await Assert.That(human).DoesNotContain("re-record the baseline");

    }

    /// <summary>Outside git, the baseline's own file list tells a new file from a file that had no findings.</summary>
    [Test]
    public async Task The_baseline_file_list_detects_new_files_without_git()
    {
        var dir = _host.Folder("listed");
        File.WriteAllText(Path.Combine(dir, ".skarp.json"), """{ "version": 1, "history": { "enabled": false }, "semantic": { "enabled": false } }""");
        File.WriteAllText(Path.Combine(dir, "A.cs"), Class("A", Method(9)));
        File.WriteAllText(Path.Combine(dir, "Clean.cs"), Class("Clean"));
        await Assert.That(_host.Run(dir, "baseline", dir).Exit).IsEqualTo(0);
        var recorded = SkarpSharp.Baselines.Baseline.Read(Path.Combine(dir, ".skarp-baseline.json"));
        await Assert.That(recorded.Files!).IsEquivalentTo(["A.cs", "Clean.cs"], CollectionOrdering.Matching);
        await Assert.That(recorded.Commit).IsNull();
        File.WriteAllText(Path.Combine(dir, "New.cs"), Class("N", Method(9), Method(9, 1)));

        var output = Path.Combine(dir, "out.json");
        await Assert.That(_host.Run(dir, "analyze", dir, "--no-save", "--output", output).Exit).IsEqualTo(0);
        using var doc = JsonDocument.Parse(File.ReadAllText(output));
        var files = doc.RootElement.GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("path").GetString()!, f => f.GetProperty("health"));
        await Assert.That(files["New.cs"].GetProperty("inBaseline").GetBoolean()).IsFalse();
        await Assert.That(files["Clean.cs"].GetProperty("baselineScore").GetDouble()).IsEqualTo(10.0);   // listed, no entry: 10 then
        var b = doc.RootElement.GetProperty("health").GetProperty("baseline");
        await Assert.That(b.GetProperty("files").GetInt32()).IsEqualTo(2);

        var human = _host.Run(dir, "analyze", dir, "--no-save", "--verbose").Out;
        await Assert.That(human).Contains("  new since the baseline (1 file, lowest first):");
        await Assert.That(human).Contains("over the 2 files it had");
        await Assert.That(human).DoesNotContain("re-record the baseline");
    }

    /// <summary>An older baseline with neither a file list nor a commit: a file without an entry may be new or have had no
    /// findings, so it gets no arrow, is left out of both sides of the comparison, and the report says to re-record.</summary>
    [Test]
    public async Task Json_rollups_with_baseline_values_recomputed_from_the_baseline_findings()
    {
        var dir = _host.Folder("rollup");
        File.WriteAllText(Path.Combine(dir, ".skarp.json"), """{ "version": 1, "history": { "enabled": false }, "semantic": { "enabled": false } }""");
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "src", "A.cs"), Class("A", Method(9), Method(9, 1)));
        File.WriteAllText(Path.Combine(dir, "B.cs"), Class("B"));
        await Assert.That(_host.Run(dir, "baseline", dir).Exit).IsEqualTo(0);
        File.WriteAllText(Path.Combine(dir, "src", "A.cs"), Class("A", Method(9), Method(9, 1), Method(9, 2)));

        var out1 = Path.Combine(dir, "out.json");
        await Assert.That(_host.Run(dir, "analyze", dir, "--no-save", "--output", out1).Exit).IsEqualTo(0);
        using var doc = JsonDocument.Parse(File.ReadAllText(out1));
        var health = doc.RootElement.GetProperty("health");
        await Assert.That((health.GetProperty("model").GetString(), health.GetProperty("kind").GetString(), health.GetProperty("files").GetInt32())).IsEqualTo(("grade", "syntax", 2));
        await Assert.That(health.GetProperty("hotspotNote").GetString()).IsEqualTo("hotspot health needs git history");
        var files = doc.RootElement.GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("path").GetString()!, f => f.GetProperty("health"));
        var a = files["src/A.cs"];
        await Assert.That((a.GetProperty("score").GetDouble(), a.GetProperty("baselineScore").GetDouble())).IsEqualTo((9.34, 9.49));
        // Outside git the baseline lists its files: B.cs is held with no entry, so it scored 10 then.
        await Assert.That(files["B.cs"].GetProperty("score").GetDouble()).IsEqualTo(10.0);
        await Assert.That(files["B.cs"].GetProperty("baselineScore").GetDouble()).IsEqualTo(10.0);
        await Assert.That(files["B.cs"].TryGetProperty("inBaseline", out _)).IsFalse();
        await Assert.That(a.TryGetProperty("inBaseline", out _)).IsFalse();
        var avg = health.GetProperty("average").GetDouble();
        var before = health.GetProperty("baseline").GetProperty("average").GetDouble();
        await Assert.That(health.GetProperty("baseline").GetProperty("files").GetInt32()).IsEqualTo(2);
        await Assert.That(avg < before).IsTrue().Because($"{avg} < {before}");
        await Assert.That((await Assert.That(health.GetProperty("folders").EnumerateArray()).HasSingleItem()).GetProperty("path").GetString()).IsEqualTo("src");
        await Assert.That(health.GetProperty("worst").EnumerateArray()).IsEmpty();

        var human = _host.Run(dir, "analyze", dir, "--no-save", "--verbose").Out;
        await Assert.That(human).Contains("Code health (grade model, syntax scores; 1 to 10, 10 = healthy; informs only):");
        await Assert.That(human).Contains("no file scores below 7.");
        await Assert.That(human).Contains("9.5 -> 9.3 (-0.2)  src/A.cs");
        await Assert.That(human).DoesNotContain("not in the baseline");
        await Assert.That(human).DoesNotContain("new since the baseline");
        await Assert.That(human).Contains("\nsrc/A.cs health 9.3".Replace("\n", Environment.NewLine, StringComparison.Ordinal));
        // Short by default: the average in the first line, against the baseline; a comparison shows the file's drop.
        var text = _host.Run(dir, "analyze", dir, "--no-save").Out;
        await Assert.That(Lines(text)[0]).Contains(" health 9.6 average, was 9.7 (10 = healthy)").Because(text);
        await Assert.That(text).DoesNotContain("Code health (");
        await Assert.That(Lines(_host.Run(dir, "analyze", dir, "--no-save", "--new-only").Out)).Contains("src/A.cs health 9.5 -> 9.3");
        var md = _host.Run(dir, "analyze", dir, "--no-save", "--format", "markdown").Out;
        await Assert.That(md).Contains("### Code health");
        await Assert.That(md).Contains("`src/A.cs` 9.5 -> 9.3");

        using var sarif = JsonDocument.Parse(_host.Run(dir, "analyze", dir, "--no-save", "--format", "sarif").Out);
        var artifacts = sarif.RootElement.GetProperty("runs")[0].GetProperty("artifacts").EnumerateArray().ToList();
        await Assert.That(artifacts.Select(x => x.GetProperty("location").GetProperty("uri").GetString())).IsEquivalentTo(new string?[] { "B.cs", "src/A.cs" }, CollectionOrdering.Matching);
        var sh = artifacts[1].GetProperty("properties").GetProperty("health");
        await Assert.That((sh.GetProperty("score").GetDouble(), sh.GetProperty("band").GetString(), sh.GetProperty("kind").GetString())).IsEqualTo((9.34, "healthy", "syntax"));
    }
}