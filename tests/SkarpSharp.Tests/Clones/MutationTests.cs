using SkarpSharp.Findings;

namespace SkarpSharp.Tests.Clones;

/// <summary>
/// Mutation tests: a committed fixture (Clones/Mutations) of methods and their mutants (our own
/// code, after published mutation operators for clone detection): T1 reformatted with comments, T2 consistent renames, T2 changed literals,
/// T3 one statement inserted, T3 one statement deleted, T3 mixed. Each mutant is analyzed with its original (default
/// thresholds, duplicate-code-near on); the expected table says which rule reports the pair. Recall here: Type 1/2 6 of 6,
/// Type 3 5 of 6.
/// </summary>
public sealed class MutationTests
{
    static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Clones", "Mutations");

    /// <summary>Mutant -> the rule expected to report it with its original (null: neither).</summary>
    public static IEnumerable<(string, string?)> Expected() =>
    [
        ("m1.t1-format", RuleIds.DuplicateCode),
        ("m1.t2-rename", RuleIds.DuplicateCode),
        ("m1.t2-literals", RuleIds.DuplicateCode),
        ("m1.t3-insert", RuleIds.DuplicateCodeNear),
        ("m1.t3-delete", RuleIds.DuplicateCodeNear),
        ("m1.t3-mixed", RuleIds.DuplicateCodeNear),
        ("m2.t1-format", RuleIds.DuplicateCode),
        ("m2.t2-rename", RuleIds.DuplicateCode),
        ("m2.t2-literals", RuleIds.DuplicateCode),
        ("m2.t3-insert", RuleIds.DuplicateCodeNear),
        ("m2.t3-delete", RuleIds.DuplicateCodeNear),
        // Missed (recorded): four edits at once drop the similarity below 0.75 (15 of 20 mixed mutants were found in an earlier experiment).
        ("m2.t3-mixed", null),
    ];

    /// <summary>The rules that report a group holding both files (sorted).</summary>
    static List<string> Reported(string mutant)
    {
        var original = mutant[..mutant.IndexOf('.')] + ".original";
        var files = new[] { original, mutant }.Select(n => new CloneHost.Source(n + ".cs", File.ReadAllText(Path.Combine(Folder, n + ".cs"))));
        var r = CloneHost.Run(files, near: true);
        return [.. (r.Findings ?? []).Where(f => f.Clone is { } c && c.Instances.Select(i => i.Path).Distinct().Count() == 2).Select(f => f.Rule).Distinct().Order()];
    }

    [Test]
    [MethodDataSource(nameof(Expected))]
    public async Task Each_mutant_is_reported_by_the_expected_rule(string mutant, string? rule)
    {
        var reported = Reported(mutant);
        if (rule is null) await Assert.That(reported).IsEmpty();
        else await Assert.That(reported).IsEquivalentTo([rule], CollectionOrdering.Matching);
    }
}