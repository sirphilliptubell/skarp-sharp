using SkarpSharp.Baselines;
using SkarpSharp.Findings;
using SkarpSharp.Health;
using SkarpSharp.Model;

namespace SkarpSharp.Tests.Health;

/// <summary>
/// The health score model: worked examples as oracles, adjusted to the final weights, and
/// the property tests (monotone both ways, independent of the order of the findings).
/// </summary>
public sealed class HealthScoreTests
{
    static readonly IScoreModel Model = ScoreModels.Create(ScoreSettings.Default);

    static int _next;

    static ScoredFinding F(string rule, string severity = "warning", double ratio = 1.5) =>
        new($"{rule}|A.cs|M{Interlocked.Increment(ref _next)}", rule, severity, ratio);

    static ScoreResult Score(params ScoredFinding[] findings) => Model.Score(new ScoreInput(findings, 100));

    static IEnumerable<ScoredFinding> Many(string rule, int n, string severity = "warning") => Enumerable.Range(0, n).Select(_ => F(rule, severity));

    [Test]
    public async Task No_findings_score_10()
    {
        var r = Score();
        await Assert.That((r.Score, r.Penalty, r.Band)).IsEqualTo((10.0, 0.0, "healthy"));
        await Assert.That(r.Contributions).IsEmpty();
        await Assert.That(HealthText.Score(r.Score)).IsEqualTo("10.0");
    }

    [Test]
    public async Task One_complex_method_warning_costs_1_and_a_notice_half()
    {
        await Assert.That(Math.Round(Score(F(RuleIds.ComplexMethod)).Score, 10)).IsEqualTo(Math.Round(9.0, 10));
        await Assert.That(Math.Round(Score(F(RuleIds.ComplexMethod, "notice")).Score, 10)).IsEqualTo(Math.Round(9.5, 10));
    }

    [Test]
    public async Task Decay_three_complex_method_warnings_cost_1_plus_0_7_plus_0_49()
    {
        var r = Score([.. Many(RuleIds.ComplexMethod, 3)]);
        await Assert.That(Math.Round(r.Score, 10)).IsEqualTo(Math.Round(7.81, 10));
        await Assert.That(HealthText.Score(r.Score)).IsEqualTo("7.8");
        var c = await Assert.That(r.Contributions).HasSingleItem();
        await Assert.That((c.Rule, c.Severity, c.Count, c.Capped)).IsEqualTo((RuleIds.ComplexMethod, "warning", 3, (bool?)null));
        await Assert.That(Math.Round(c.Points, 10)).IsEqualTo(Math.Round(2.19, 10));
    }

    [Test]
    public async Task Ten_magic_numbers_never_reach_their_cap()
    {
        var r = Score([.. Many(RuleIds.MagicNumber, 10, "notice")]);
        await Assert.That(Math.Round(r.Score, 10)).IsEqualTo(Math.Round(10 - 0.05 * (1 - Math.Pow(0.7, 10)) / 0.3, 10));
        await Assert.That(HealthText.Score(r.Score)).IsEqualTo("9.8");
        await Assert.That(HealthText.Json(r.Score)).IsEqualTo(9.84);
    }

    /// <summary>A worked example, adjusted: five complex-method warnings sum to 2.77 (under the 3.0 cap), so seven
    /// are used to reach the cap; large-file warning 1.5, two god-class findings capped at 1.5: 10 - 1.5 - 1.5 - 3.0 = 4.0.</summary>
    [Test]
    public async Task Caps_limit_each_rule_and_scale_its_contributions()
    {
        var r = Score([F(RuleIds.LargeFile), F(RuleIds.GodClass, "notice"), F(RuleIds.GodClass, "notice"), .. Many(RuleIds.ComplexMethod, 7)]);
        await Assert.That(Math.Round(r.Score, 10)).IsEqualTo(Math.Round(4.0, 10));
        await Assert.That(r.Band).IsEqualTo("problematic");
        await Assert.That(Math.Round(r.Contributions.Sum(c => c.Points), 10)).IsEqualTo(Math.Round(6.0, 10));
        await Assert.That(r.Contributions.Select(c => c.Rule)).IsEquivalentTo([RuleIds.ComplexMethod, RuleIds.LargeFile, RuleIds.GodClass], CollectionOrdering.Matching);
        await Assert.That(r.Contributions.Single(c => c.Rule == RuleIds.ComplexMethod).Capped).IsTrue();
        await Assert.That(r.Contributions.Single(c => c.Rule == RuleIds.GodClass).Capped).IsTrue();
        await Assert.That(r.Contributions.Single(c => c.Rule == RuleIds.LargeFile).Capped).IsNull();   // 1.5 is the cap, not over it

        var five = Score([.. Many(RuleIds.ComplexMethod, 5)]);
        await Assert.That(Math.Round(five.Score, 10)).IsEqualTo(Math.Round(10 - (1 - Math.Pow(0.7, 5)) / 0.3, 10));              // 7.23: below the cap
    }

    [Test]
    public async Task Floor_at_1_keeps_the_unfloored_penalty()
    {
        var r = Score([.. Many(RuleIds.ComplexMethod, 10), .. Many(RuleIds.LongMethod, 10), F(RuleIds.LargeFile), F(RuleIds.GodClass, "notice"),
            .. Many(RuleIds.DeepNesting, 10, "notice")]);
        await Assert.That(r.Score).IsEqualTo(1.0);
        await Assert.That(Math.Round(r.Penalty, 10)).IsEqualTo(Math.Round(3.0 + 2.5 + 1.5 + 1.5 + 0.5 * (1 - Math.Pow(0.7, 10)) / 0.3, 10));   // deep-nesting 1.62, under its cap
        await Assert.That(r.Band).IsEqualTo("unhealthy");
        await Assert.That(Math.Round(r.Contributions.Sum(c => c.Points), 10)).IsEqualTo(Math.Round(r.Penalty, 10));
        // A worse file at 1 still ranks below: its penalty is larger.
        var worse = Score([.. Many(RuleIds.ComplexMethod, 10), .. Many(RuleIds.LongMethod, 10), F(RuleIds.LargeFile), F(RuleIds.GodClass, "notice"),
            .. Many(RuleIds.DeepNesting, 10, "notice"), .. Many(RuleIds.BumpyRoad, 10, "notice")]);
        await Assert.That(worse.Score).IsEqualTo(1.0);
        await Assert.That(worse.Penalty > r.Penalty).IsTrue();
    }

    [Test]
    public async Task Rollup_is_the_source_line_weighted_mean()
    {
        await Assert.That(Rollup.Mean([(2.0, 5000), (10, 100), (10, 10)])).IsEqualTo(2.17);
        await Assert.That(Rollup.Mean([(6.0, 10), (1.0, 0)])).IsEqualTo(6.0);                       // no source lines: skipped
        await Assert.That(Rollup.Mean([(5.0, 0)])).IsNull();
    }

    [Test]
    public async Task Worst_files_folders_and_their_order()
    {
        FileResult File(string path, double score, double penalty, int lines) => new()
        {
            Path = path, Lines = new LineCountsDto { Source = lines },
            Health = new FileHealth { Score = score, Penalty = penalty, Band = Bands.Of(score), Kind = "syntax" },
        };
        var files = new[] { File("b/X.cs", 1, 9.5, 10), File("a/Y.cs", 1, 12, 10), File("a/b/Z.cs", 6.99, 3.01, 30), File("Top.cs", 7, 3, 50), File("a/W.cs", 1, 9.5, 10) };
        var worst = Rollup.Worst(files, ScoreSettings.Default);
        await Assert.That(worst.Select(w => w.Path)).IsEquivalentTo(["a/Y.cs", "a/W.cs", "b/X.cs", "a/b/Z.cs"], CollectionOrdering.Matching);   // 7 is not below 7
        await Assert.That(Rollup.Worst(files, new ScoreSettings { WorstCount = 2 }).Select(w => w.Path)).IsEquivalentTo(["a/Y.cs", "a/W.cs"], CollectionOrdering.Matching);
        var folders = Rollup.Folders(files);
        await Assert.That(folders.Select(f => f.Path)).IsEquivalentTo(["a", "a/b", "b"], CollectionOrdering.Matching);
        await Assert.That((folders[0].Files, folders[0].SourceLines)).IsEqualTo((3, 50L));
        await Assert.That(folders[0].Average).IsEqualTo(HealthText.Json((10 + 10 + 6.99 * 30) / 50));
    }

    [Test]
    public async Task A_dash_weight_follows_the_other_severity_and_overrides_apply()
    {
        await Assert.That(Math.Round(Score(F(RuleIds.DeepNesting, "warning")).Score, 10)).IsEqualTo(Math.Round(9.5, 10));      // no warning weight: the notice's
        var settings = new ScoreSettings
        {
            Decay = 0.5,
            Weights = new Dictionary<string, WeightOverride>(StringComparer.Ordinal)
            {
                [RuleIds.DeepNesting] = new(0.8, null, null), [RuleIds.LongMethod] = new(null, 2.0, 2.2),
            },
        };
        var model = ScoreModels.Create(settings);
        await Assert.That(Math.Round(model.Score(new ScoreInput([F(RuleIds.DeepNesting, "warning")], 1)).Score, 10)).IsEqualTo(Math.Round(9.2, 10));
        await Assert.That(Math.Round(model.Score(new ScoreInput([F(RuleIds.LongMethod), F(RuleIds.LongMethod)], 1)).Score, 10)).IsEqualTo(Math.Round(10 - 2.2, 10));   // 2 + 1 capped at 2.2
        await Assert.That(Math.Round(model.Score(new ScoreInput([F(RuleIds.LongMethod, "notice"), F(RuleIds.LongMethod, "notice")], 1)).Score, 10)).IsEqualTo(Math.Round(10 - 0.4 - 0.2, 10));
        await Assert.That(Score(F(RuleIds.UsedOnlyByTests, "notice"), F(RuleIds.NamespaceCycle, "warning"), F(RuleIds.ActiveComplexFile, "notice")).Score).IsEqualTo(10);
    }

    [Test]
    public async Task Heavier_findings_of_a_rule_are_taken_first()
    {
        // A notice listed first must not take the full weight before the warning (that would break monotonicity).
        var r = Score(F(RuleIds.ComplexMethod, "notice", 9), F(RuleIds.ComplexMethod, "warning", 1.01));
        await Assert.That(Math.Round(r.Score, 10)).IsEqualTo(Math.Round(10 - 1.0 - 0.5 * 0.7, 10));
    }

    [Test]
    public async Task Display_rounds_half_away_from_zero_from_the_two_decimal_value()
    {
        await Assert.That(HealthText.Score(7.85)).IsEqualTo("7.9");
        await Assert.That(HealthText.Score(7.849)).IsEqualTo("7.9");    // 7.85 in JSON, so 7.9 everywhere
        await Assert.That(HealthText.Score(7.95)).IsEqualTo("8.0");
        await Assert.That(HealthText.Json(7.8499999)).IsEqualTo(7.85);
    }

    // ---- property tests ---------------------------------------------------------------------------------------------

    static readonly string[] Rules = [.. RuleCatalog.Every.Select(r => r.Id)];

    static List<ScoredFinding> RandomFindings(Random rng, int n) =>
        [.. Enumerable.Range(0, n).Select(i => new ScoredFinding($"r{rng.Next(1000)}|A.cs|{i}", Rules[rng.Next(Rules.Length)], rng.Next(2) == 0 ? "notice" : "warning", 1 + rng.NextDouble() * 3))];

    static IEnumerable<IScoreModel> Models(Random rng)
    {
        yield return Model;
        // Random weights, notices sometimes heavier than warnings, random decay: the properties hold for any table.
        for (int m = 0; m < 5; m++)
        {
            var w = Rules.ToDictionary(r => r, _ => new WeightOverride(rng.NextDouble() * 2, rng.NextDouble() * 2, rng.NextDouble() * 4), StringComparer.Ordinal);
            yield return ScoreModels.Create(new ScoreSettings { Weights = w, Decay = 0.1 + rng.NextDouble() * 0.9 });
        }
    }

    [Test]
    public async Task Adding_a_finding_never_raises_the_score_and_removing_one_never_lowers_it()
    {
        var rng = new Random(12);
        foreach (var model in Models(rng))
            for (int trial = 0; trial < 300; trial++)
            {
                var findings = RandomFindings(rng, rng.Next(0, 25));
                var before = model.Score(new ScoreInput(findings, 50));
                var added = model.Score(new ScoreInput([.. findings, .. RandomFindings(rng, 1)], 50));
                await Assert.That(added.Score <= before.Score + 1e-9 && added.Penalty >= before.Penalty - 1e-9).IsTrue().Because($"trial {trial}: adding raised the score");
                if (findings.Count == 0) continue;
                var removed = model.Score(new ScoreInput([.. findings.Where((_, i) => i != rng.Next(findings.Count))], 50));
                await Assert.That(removed.Score >= before.Score - 1e-9).IsTrue().Because($"trial {trial}: removing lowered the score");
            }
    }

    [Test]
    public async Task The_score_does_not_depend_on_the_order_of_the_findings()
    {
        var rng = new Random(7);
        foreach (var model in Models(rng))
            for (int trial = 0; trial < 200; trial++)
            {
                var findings = RandomFindings(rng, rng.Next(1, 30));
                var a = model.Score(new ScoreInput(findings, 10));
                var b = model.Score(new ScoreInput([.. findings.OrderBy(_ => rng.Next())], 10));
                await Assert.That(b.Score).IsEqualTo(a.Score);
                await Assert.That(b.Penalty).IsEqualTo(a.Penalty);
                await Assert.That(b.Contributions.Select(c => (c.Rule, c.Severity, c.Count, c.Points))).IsEquivalentTo(a.Contributions.Select(c => (c.Rule, c.Severity, c.Count, c.Points)), CollectionOrdering.Matching);
            }
    }

    // ---- baseline recomputation --------------------------------------------------------------------------------------

    [Test]
    public async Task Baseline_entries_get_their_severity_back()
    {
        var rules = RuleSettings.Resolve("default", null, []);
        var current = new Finding { Id = "too-many-parameters|A.cs|A::M", Rule = RuleIds.TooManyParameters, Severity = "warning", Value = 9, Threshold = 8, Metric = "parameters" };
        // The same id and value: the current finding's severity.
        await Assert.That(HealthBaseline.Judge(RuleIds.TooManyParameters, 9, current, rules).Severity).IsEqualTo("warning");
        // A smaller recorded value: judged against the warning level (8 or more parameters).
        await Assert.That(HealthBaseline.Judge(RuleIds.TooManyParameters, 6, current, rules).Severity).IsEqualTo("notice");
        await Assert.That(HealthBaseline.Judge(RuleIds.TooManyParameters, 12, null, rules).Severity).IsEqualTo("warning");
        // A structural entry with no current finding: the rule's highest level.
        await Assert.That(HealthBaseline.Judge(RuleIds.EmptyCatch, null, null, rules).Severity).IsEqualTo("warning");
        await Assert.That(HealthBaseline.Judge(RuleIds.MagicNumber, null, null, rules).Severity).IsEqualTo("notice");

        var byPath = HealthBaseline.ByPath([new BaselineEntry { Id = current.Id, Value = 9 }, new BaselineEntry { Id = "namespace-cycle||namespace X" }],
            new Dictionary<string, Finding> { [current.Id] = current }, rules);
        await Assert.That(byPath.Keys).IsEquivalentTo(["A.cs"], CollectionOrdering.Matching);                                          // path-less entries count nowhere
        await Assert.That(Math.Round(Model.Score(new ScoreInput(byPath["A.cs"], 10)).Score, 10)).IsEqualTo(Math.Round(9.7, 10));
    }
}