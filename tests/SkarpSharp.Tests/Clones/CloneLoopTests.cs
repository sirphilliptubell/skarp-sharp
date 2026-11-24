using SkarpSharp.Agent;
using SkarpSharp.Baselines;
using SkarpSharp.Findings;
using SkarpSharp.Health;
using SkarpSharp.Model;

namespace SkarpSharp.Tests.Clones;

/// <summary>
/// The loop around a clone group: baseline (a new group is new, a grown group worsened, the
/// gate trips on a new large group), the score (0.3 per notice group, 0.6 per warning group, once per file,
/// no decay, cap 1.5), baseline health, and the agent hint line.
/// </summary>
public sealed class CloneLoopTests
{
    static CloneHost.Source S(string path, string code) => new(path, code);

    static string Copy(string code, string type) => code.Replace("class A", "class " + type, StringComparison.Ordinal);

    static readonly string Small = CloneHost.Method("A", "Run", 12);
    static readonly string Big = CloneHost.Method("A", "Large", 50, "sum", 9);

    static BaselineFile Record(AnalysisResult r) =>
        Baseline.Parse(Baseline.Write(r.Findings ?? [], "test", r.Run.Findings ?? new FindingSettings()), "test");

    [Test]
    public async Task A_new_group_is_new_a_grown_group_worsened_and_the_gate_trips_on_a_new_large_group()
    {
        var before = CloneHost.Run([S("A.cs", Small), S("B.cs", Copy(Small, "B"))]);
        var baseline = Record(before);
        var entry = await Assert.That(baseline.Findings).HasSingleItem(e => e.Id.StartsWith("duplicate-code|", StringComparison.Ordinal));
        await Assert.That(entry.Severity).IsEqualTo("notice");
        await Assert.That(entry.Files!).IsEquivalentTo(["A.cs", "B.cs"], CollectionOrdering.Matching);

        // A third copy (in a file ordered after the first): the same id, more lines in all copies: worsened.
        var grown = CloneHost.Run([S("A.cs", Small), S("B.cs", Copy(Small, "B")), S("C.cs", Copy(Small, "C"))]);
        Baseline.Compare(grown.Findings!, baseline, "b", null);
        var g = await Assert.That(CloneHost.Clones(grown)).HasSingleItem();
        await Assert.That((g.Id, g.BaselineState)).IsEqualTo((entry.Id, "worsened"));
        await Assert.That(g.BaselineValue).IsEqualTo(entry.Value);

        // A new large copy elsewhere: a new warning, and the default gate (*:warning) trips.
        var pasted = CloneHost.Run([S("A.cs", Small), S("B.cs", Copy(Small, "B")), S("D.cs", Big), S("E.cs", Copy(Big, "E"))]);
        var gate = Gate.Evaluate(Mark(pasted, baseline), []);
        var large = await Assert.That(CloneHost.Clones(pasted)).HasSingleItem(f => f.Severity == "warning");
        await Assert.That(large.BaselineState).IsEqualTo("new");
        await Assert.That(gate.Tripped).IsTrue();
        await Assert.That(gate.New).IsEqualTo(1);

        // Unchanged: known, the gate passes.
        var same = CloneHost.Run([S("A.cs", Small), S("B.cs", Copy(Small, "B"))]);
        await Assert.That(Gate.Evaluate(Mark(same, baseline), []).Tripped).IsFalse();
        await Assert.That((await Assert.That(CloneHost.Clones(same)).HasSingleItem()).BaselineState).IsEqualTo("known");
    }

    static List<Finding> Mark(AnalysisResult r, BaselineFile baseline)
    {
        Baseline.Compare(r.Findings!, baseline, "b", null);
        return r.Findings!;
    }

    static readonly IScoreModel Model = ScoreModels.Create(ScoreSettings.Default);

    static int _next;

    static ScoredFinding F(string severity) => new($"duplicate-code||clone {Interlocked.Increment(ref _next)}", RuleIds.DuplicateCode, severity, 1);

    static double Score(params ScoredFinding[] f) => Model.Score(new ScoreInput(f, 100)).Score;

    [Test]
    public async Task Score_oracles_for_the_duplication_weight()
    {
        await Assert.That(Math.Round(Score(F("notice")), 10)).IsEqualTo(Math.Round(9.7, 10));
        await Assert.That(Math.Round(Score(F("warning")), 10)).IsEqualTo(Math.Round(9.4, 10));
        // No decay: three notice groups cost 3 x 0.3.
        await Assert.That(Math.Round(Score(F("notice"), F("notice"), F("notice")), 10)).IsEqualTo(Math.Round(9.1, 10));
        // Cap 1.5: six notice groups (1.8) or three warning groups (1.8) cost 1.5.
        await Assert.That(Math.Round(Score([.. Enumerable.Range(0, 6).Select(_ => F("notice"))]), 10)).IsEqualTo(Math.Round(8.5, 10));
        await Assert.That(Math.Round(Score(F("warning"), F("warning"), F("warning")), 10)).IsEqualTo(Math.Round(8.5, 10));
        var c = await Assert.That(Model.Score(new ScoreInput([.. Enumerable.Range(0, 6).Select(_ => F("notice"))], 100)).Contributions).HasSingleItem();
        await Assert.That((c.Rule, c.Count, c.Capped)).IsEqualTo((RuleIds.DuplicateCode, 6, (bool?)true));
        // Duplicate-code-near is on by default with score weight 0.
        await Assert.That((HealthWeights.Resolve(new Dictionary<string, WeightOverride>())[RuleIds.DuplicateCodeNear] is var w ? (w.Notice, w.Warning, w.Cap, w.Decay) : default)).IsEqualTo((0.0, 0.0, 0.0, (double?)1.0));
    }

    [Test]
    public async Task A_group_counts_once_in_every_file_holding_a_copy()
    {
        var f = new Finding
        {
            Id = "duplicate-code||clone 1", Rule = RuleIds.DuplicateCode, Severity = "notice", Path = "A.cs", PathlessId = true,
            Clone = new CloneInfo { Instances = [new() { Path = "A.cs" }, new() { Path = "A.cs" }, new() { Path = "B.cs" }] },
        };
        var other = new Finding { Id = "namespace-cycle||namespace N", Rule = RuleIds.NamespaceCycle, Severity = "warning", Path = "A.cs", PathlessId = true };
        var byPath = HealthEngine.ByPath([f, other]);
        await Assert.That(byPath.Keys.Order()).IsEquivalentTo(["A.cs", "B.cs"], CollectionOrdering.Matching);
        await Assert.That(byPath["A.cs"]).HasSingleItem();
        await Assert.That(byPath["B.cs"]).HasSingleItem();

        // In a run: both files of a notice group lose 0.3 (no other findings in these files at default thresholds but
        // the clone and magic numbers, which the score weighs separately).
        var r = CloneHost.Run([S("A.cs", Small), S("B.cs", Copy(Small, "B"))]);
        HealthEngine.Apply(r, new HealthInputs { Settings = ScoreSettings.Default, Rules = CloneHost.Settings() });
        foreach (var file in r.Files) await Assert.That(file.Health!.Contributions).Contains(c => c.Rule == RuleIds.DuplicateCode && c.Points == 0.3);

        // Baseline scores: the recorded files carry the group's points too.
        var baseline = Record(r);
        var scored = HealthBaseline.ByPath(baseline.Findings, new Dictionary<string, Finding>(), CloneHost.Settings());
        await Assert.That(scored["B.cs"]).Contains(s => s.Rule == RuleIds.DuplicateCode && s.Severity == "notice");
    }

    [Test]
    public async Task Score_weights_take_a_per_rule_decay_from_the_settings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "skarp-decay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".skarp.json"), """{ "version": 1, "score": { "weights": { "duplicate-code": { "decay": 0.5 } } } }""");
            var o = SkarpSharp.Configuration.CommandLine.Parse(["analyze", dir]).Options!;
            var model = ScoreModels.Create(o.Score);
            await Assert.That(Math.Round(model.Score(new ScoreInput([F("notice"), F("notice")], 100)).Score, 10)).IsEqualTo(Math.Round(10 - 0.3 - 0.15, 10));
            File.WriteAllText(Path.Combine(dir, ".skarp.json"), """{ "version": 1, "score": { "weights": { "duplicate-code": { "decay": 0 } } } }""");
            await Assert.That(SkarpSharp.Configuration.CommandLine.Parse(["analyze", dir]).Error).IsNotNull();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Agent_output_adds_one_hint_line_when_it_reports_a_clone_group()
    {
        var r = CloneHost.Run([S("A.cs", Small), S("B.cs", Copy(Small, "B")), S("C.cs", Copy(Small, "C")), S("D.cs", Big), S("E.cs", Copy(Big, "E"))]);
        var lines = Stop(r, 15);
        await Assert.That(lines).HasSingleItem(l => l == DuplicationText.ClaudeHint);
        await Assert.That(lines).Contains("D.cs");
        await Assert.That(lines).Contains(l => l.StartsWith("  warning line 2 ", StringComparison.Ordinal) && l.Contains(" duplicate-code: ", StringComparison.Ordinal) && l.Contains("2 copies of", StringComparison.Ordinal));
        await Assert.That(DuplicationText.AgentHint).Contains("[SuppressMessage(\"skarp\", \"duplicate-code\", Justification = \"<why>\")]");
        // Claude's note names no way around the finding.
        await Assert.That(DuplicationText.ClaudeHint).DoesNotContain("SuppressMessage").And.DoesNotContain("baseline");
        // The hint is not a finding line, so it does not count toward the budget.
        await Assert.That(Stop(r, 1)).Contains(DuplicationText.ClaudeHint);
        // It follows the About line's rhythm.
        await Assert.That(Stop(r, 15, about: false)).DoesNotContain(DuplicationText.ClaudeHint);
        // No clone line, no hint.
        var plain = CloneHost.Run([S("A.cs", Small)], ["long-method.notice=0"]);
        await Assert.That(Stop(plain, 15)).DoesNotContain(DuplicationText.ClaudeHint);
    }

    /// <summary>The text of an end-of-turn summary of the analysis's lines (no comparison: its warnings).</summary>
    static List<string> Stop(AnalysisResult r, int max, bool about = true)
    {
        var outcome = new CheckOutcome { EndOfTurn = true };
        outcome.Lines.AddRange(AgentSummary.Entries(r, Severity.Notice, null));
        return CheckText.Lines(outcome, new CheckPrint(max, new LineStyle(null, false), null) { Hook = true, About = about }, out _);
    }

    [Test]
    public async Task Explain_and_check_help_carry_the_guidance()
    {
        var w = new StringWriter();
        Reporting.HumanReport.WriteExplain(w, RuleCatalog.Get(RuleIds.DuplicateCode));
        var text = w.ToString();
        await Assert.That(text).Contains("[SuppressMessage(\"skarp\", \"duplicate-code\", Justification = \"<why>\")]");
        await Assert.That(text).Contains("tokens at least 100 and lines at least 10");
        await Assert.That(text).Contains("jscpd");
        await Assert.That(Commands.CheckCommand.Usage).Contains(RuleIds.DuplicateCode);
        await Assert.That(FileCheck.LeftOut).Contains(RuleIds.DuplicateCode);
    }
}