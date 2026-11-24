using SkarpSharp.Findings;

namespace SkarpSharp.Tests.Clones;

/// <summary>The low-content filter of duplicate-code (constructors that only store injected services
/// and similar boilerplate are skipped by default; duplicate-code.lowContent=report brings them back).</summary>
public class LowContentTests
{
    /// <summary>A class whose constructor only stores <paramref name="count"/> injected services, one parameter per line
    /// (the nopCommerce shape: about 3 to 4 tokens per line, a handful of line shapes).</summary>
    static string Injected(string type, string prefix, int count)
    {
        var names = Enumerable.Range(1, count).Select(i => $"{prefix}Service{i}").ToList();
        var fields = string.Join("\n", names.Select(n => $"    private readonly I{n} _{Camel(n)};"));
        var parameters = string.Join(",\n", names.Select(n => $"        I{n} {Camel(n)}"));
        var assignments = string.Join("\n", names.Select(n => $"        _{Camel(n)} = {Camel(n)};"));
        return $$"""
            namespace N;
            public class {{type}}
            {
            {{fields}}

                public {{type}}(
            {{parameters}})
                {
            {{assignments}}
                }
            }
            """;
    }

    static string Camel(string s) => char.ToLowerInvariant(s[0]) + s[1..];

    static List<CloneHost.Source> InjectedPair() =>
    [
        new("src/OrderController.cs", Injected("OrderController", "Order", 16)),
        new("src/ProductController.cs", Injected("ProductController", "Product", 16)),
    ];

    [Test]
    public async Task A_constructor_that_only_stores_injected_services_is_not_reported()
    {
        var r = CloneHost.Run(InjectedPair());
        await Assert.That(CloneHost.Clones(r)).IsEmpty();
        await Assert.That(r.Repository.Duplication!.Groups).IsEqualTo(0);
    }

    [Test]
    public async Task The_setting_report_restores_the_group()
    {
        var r = CloneHost.Run(InjectedPair(), ["duplicate-code.lowContent=report"]);
        var f = await Assert.That(CloneHost.Clones(r)).HasSingleItem();
        await Assert.That(f.Clone!.Kind).IsEqualTo("renamed");
        await Assert.That(f.Clone.Instances.Count).IsEqualTo(2);
    }

    [Test]
    public async Task An_ordinary_copy_is_kept()
    {
        var code = CloneHost.Method("A", "Run", 14, seed: 3);
        var r = CloneHost.Run([new("src/A.cs", code), new("src/B.cs", code.Replace("class A", "class B", StringComparison.Ordinal))]);
        await Assert.That(CloneHost.Clones(r)).HasSingleItem();
    }

    [Test]
    public async Task A_skipped_group_does_not_come_back_as_a_near_miss()
    {
        var r = CloneHost.Run(InjectedPair(), near: true);
        await Assert.That(CloneHost.Clones(r)).IsEmpty();
        await Assert.That(CloneHost.Clones(r, RuleIds.DuplicateCodeNear)).IsEmpty();
    }

    [Test]
    [Arguments("skip", true)]
    [Arguments("report", true)]
    [Arguments("off", false)]
    [Arguments("1", false)]
    public async Task The_setting_accepts_skip_and_report(string value, bool ok)
    {
        var act = () => CloneHost.Settings([$"duplicate-code.lowContent={value}"]);
        if (ok) act();
        else await Assert.That(Assert.Throws<ArgumentException>(() => act()).Message).Contains("skip or report");
    }
}