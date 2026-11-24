using SkarpSharp.Health;

namespace SkarpSharp.Tests.Health;

/// <summary>
/// The three health limits of the gate on the shown
/// one-decimal values: a drop of 0.9 passes and 1.0 blocks, the floor only after a drop, new files from 6.5, 0 turns each
/// rule off, and the headings and facts each surface prints.
/// </summary>
public sealed class HealthLimitTests
{
    static readonly HealthLimits D = HealthLimits.Default;

    [Test]
    public async Task Defaults_are_drop_1_new_file_6_5_floor_6_5()
    {
        await Assert.That((D.MaxDrop, D.NewFileMin, D.Floor)).IsEqualTo((1.0, 6.5, 6.5));
        await Assert.That(D.Describe()).IsEqualTo("a drop under 1.0, new files at least 6.5, no drop below 6.5");
        await Assert.That(new HealthLimits(0, 0, 0).Any).IsFalse();
        await Assert.That(new HealthLimits(0, 0, 0).Describe()).IsEmpty();
    }

    [Test]
    public async Task A_drop_of_0_9_passes_and_1_0_blocks()
    {
        await Assert.That(HealthLimit.Judge(8.3, 7.4, false, D)).IsNull();
        await Assert.That(HealthLimit.Judge(8.3, 7.3, false, D)).IsEqualTo(HealthRule.Drop);
        await Assert.That(HealthLimit.Judge(10, 9.0, false, D)).IsEqualTo(HealthRule.Drop);
        await Assert.That(HealthLimit.Judge(10, 9.1, false, D)).IsNull();
        // A rise never blocks, however low.
        await Assert.That(HealthLimit.Judge(2.0, 3.0, false, D)).IsNull();
    }

    [Test]
    public async Task Rules_compare_the_shown_values()
    {
        // 8.36 shows 8.4, 7.36 shows 7.4: a shown drop of 1.0.
        await Assert.That(HealthLimit.Judge(8.36, 7.36, false, D)).IsEqualTo(HealthRule.Drop);
        // 8.34 shows 8.3 and 7.36 shows 7.4: 0.9.
        await Assert.That(HealthLimit.Judge(8.34, 7.36, false, D)).IsNull();
        await Assert.That(HealthLimit.Judge(8.34, 7.34, false, D)).IsEqualTo(HealthRule.Drop);
    }

    [Test]
    public async Task The_floor_blocks_only_a_file_that_got_worse()
    {
        await Assert.That(HealthLimit.Judge(6.8, 6.2, false, D)).IsEqualTo(HealthRule.Floor);
        await Assert.That(HealthLimit.Judge(6.2, 6.2, false, D)).IsNull();
        await Assert.That(HealthLimit.Judge(6.0, 6.3, false, D)).IsNull();
        await Assert.That(HealthLimit.Judge(6.6, 6.5, false, D)).IsNull();
        await Assert.That(HealthLimit.Judge(6.6, 6.4, false, D)).IsEqualTo(HealthRule.Floor);
        // A large drop that also ends below the floor is a drop.
        await Assert.That(HealthLimit.Judge(9.0, 5.0, false, D)).IsEqualTo(HealthRule.Drop);
    }

    [Test]
    public async Task New_files_from_6_5()
    {
        await Assert.That(HealthLimit.Judge(null, 6.4, true, D)).IsEqualTo(HealthRule.NewFile);
        await Assert.That(HealthLimit.Judge(null, 6.5, true, D)).IsNull();
        await Assert.That(HealthLimit.Judge(null, 6.45, true, D)).IsNull(); // shows 6.5
        // A known file without a known earlier score is not judged.
        await Assert.That(HealthLimit.Judge(null, 2.0, false, D)).IsNull();
    }

    [Test]
    public async Task Zero_turns_each_rule_off()
    {
        await Assert.That(HealthLimit.Judge(9.0, 2.0, false, new HealthLimits(0, 6.5, 0))).IsNull();
        await Assert.That(HealthLimit.Judge(9.0, 2.0, false, new HealthLimits(0, 6.5, 6.5))).IsEqualTo(HealthRule.Floor);
        await Assert.That(HealthLimit.Judge(6.8, 6.2, false, new HealthLimits(1, 6.5, 0))).IsNull();
        await Assert.That(HealthLimit.Judge(null, 1.0, true, new HealthLimits(1, 0, 6.5))).IsNull();
        // 0.1: any shown drop.
        await Assert.That(HealthLimit.Judge(9.9, 9.8, false, new HealthLimits(0.1, 0, 0))).IsEqualTo(HealthRule.Drop);
    }

    [Test]
    public async Task Headings_and_facts_name_the_limit()
    {
        var drop = HealthLimit.Of("src/A.cs", 8.3, 7.1, false, D)!;
        var floor = HealthLimit.Of("src/A.cs", 6.8, 6.2, false, D)!;
        var created = HealthLimit.Of("src/A.cs", null, 5.4, true, D)!;
        await Assert.That(HealthLimit.Heading("src/A.cs", drop)).IsEqualTo("src/A.cs health 8.3 -> 7.1 (down 1.2, limit under 1.0)");
        await Assert.That(HealthLimit.Heading("src/A.cs", floor)).IsEqualTo("src/A.cs health 6.8 -> 6.2 (below 6.5 after a drop)");
        await Assert.That(HealthLimit.Heading("src/A.cs", created)).IsEqualTo("src/A.cs health 5.4 (new file, limit at least 6.5)");
        await Assert.That(HealthLimit.Fact("src/A.cs", drop)).IsEqualTo("src/A.cs is down 1.2; the gate stops at 1.0 or more");
        await Assert.That(HealthLimit.Fact("src/A.cs", floor)).IsEqualTo("src/A.cs fell to 6.2; the gate stops a drop that ends below 6.5");
        await Assert.That(HealthLimit.Fact("src/A.cs", created)).IsEqualTo("src/A.cs is a new file at 5.4; the gate stops new files below 6.5");
        await Assert.That(HealthLimit.Verdict(1)).IsEqualTo("health past its limit in 1 file");
        await Assert.That(HealthLimit.Verdict(2, "staged")).IsEqualTo("health past its limit in 2 staged files");
    }
}
