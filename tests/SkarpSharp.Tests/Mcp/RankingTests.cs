using SkarpSharp.Configuration;
using System.Globalization;
using System.Text.RegularExpressions;
using SkarpSharp.Ranking;
using static SkarpSharp.Tests.Mcp.McpFixture;

namespace SkarpSharp.Tests.Mcp;

/// <summary>
/// The worst-first ranking shared by <c>skarp analyze --sort</c> and
/// <c>find_worst_code</c>. The health order is a real ranking (a file of many moderate findings ranks
/// above files whose one finding is far over its limit), paging, bands and the healthy count, a rule sort that also
/// ranks code under every threshold, every unit combination and refusal, and hotspot without git.
/// </summary>
public sealed class RankingTests : IDisposable
{
    readonly McpFixture _f = new();

    public void Dispose() => _f.Dispose();

    /// <summary>Bad.cs: eight methods just over the complex-method notice (many findings, a low score); A, B and C: one
    /// method each far over its limits (one or two findings, far over; a high score).</summary>
    void SampleCase()
    {
        _f.Write("Bad.cs", Class("Bad", [.. Enumerable.Range(1, 8).Select(i => Method("M" + i, 17))]));
        foreach (var name in new[] { "A", "B", "C" }) _f.Write(name + ".cs", Class(name, Method("Huge", 60)));
        _f.Write("Fine.cs", Class("Fine", Method("Small", 2)));
    }

    static double Score(string text, string file) =>
        double.Parse(Regex.Match(text, Regex.Escape(file) + @" health (\d+\.\d)").Groups[1].Value, CultureInfo.InvariantCulture);

    static List<string> Headings(string text) => [.. Regex.Matches(text, @"^(\S+\.cs)\b", RegexOptions.Multiline).Select(m => m.Groups[1].Value)];

    [Test]
    public async Task Health_ranks_by_score_not_by_the_worst_place()
    {
        SampleCase();
        var text = await _f.Worst("""{"count":3}""");
        await Assert.That(text).StartsWith("Skarp: worst 3 of 4 files with findings in . (syntax tier), sorted by health\n");
        var headings = Headings(text);
        await Assert.That(headings[0]).IsEqualTo("Bad.cs");
        await Assert.That(Score(text, "Bad.cs")).IsLessThan(Score(text, "A.cs"));
        // The places-first view (top places, most active files first) starts with the far-over places instead.
        var places = _f.Agent.Run(_f.Root, "analyze", _f.Root, "--no-semantic", "--no-save", "--top", "3");
        await Assert.That(places.Out).DoesNotContain("Bad.cs health");
        // Ties of the score are broken by path; files without findings do not rank.
        await Assert.That(headings.Skip(1)).IsEquivalentTo(new[] { "A.cs", "B.cs" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(text).DoesNotContain("Fine.cs");
    }

    [Test]
    public async Task Paging_bands_and_the_healthy_count()
    {
        SampleCase();
        var page2 = await _f.Worst("""{"count":2,"offset":2}""");
        await Assert.That(page2).StartsWith("Skarp: worst 2 of 4 files with findings in . (syntax tier), sorted by health, entries 3 to 4\n");
        await Assert.That(Headings(page2)).IsEquivalentTo(new[] { "B.cs", "C.cs" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(page2).DoesNotContain("next: offset=");
        var page1 = await _f.Worst("""{"count":2}""");
        await Assert.That(page1).Contains("\nnext: offset=2\n");
        // Each file heading has its band; the closing line counts the healthy ones (9 or more).
        int healthy = Regex.Matches(page2, @"health \d+\.\d \(healthy").Count;
        await Assert.That(page2).Contains($"\n{healthy} of these 2 are healthy (9 or more)\n");
        await Assert.That(page1).Matches(@"Bad\.cs health \d\.\d \((problematic|unhealthy); 10 = healthy\)");
        // Past the end.
        var past = await _f.Worst("""{"count":2,"offset":9}""");
        await Assert.That(past).Contains("Nothing at offset 9: the ranking has 4 entries.");
    }

    [Test]
    public async Task A_rule_sort_ranks_code_under_every_threshold_too()
    {
        _f.Write("Small.cs", Class("Small", Method("One", 1), Method("Two", 3), Method("Three", 2)));
        var health = await _f.Worst("{}");
        await Assert.That(health).Contains("worst 0 of 0 files with findings");
        await Assert.That(health).Contains("Nothing to rank: no analyzed C# file here has a finding.");
        var methods = await _f.Worst("""{"sort":"long-method"}""");
        await Assert.That(methods).StartsWith("Skarp: worst 3 of 3 methods in . (syntax tier), sorted by long-method (physical lines)\n");
        await Assert.That(methods).Contains("\nSmall.cs line 12 Small.Two: physical lines 8 (limit 50)\n");
        await Assert.That(methods).Contains("3 of these 3 are in healthy files (9 or more)");
        await Assert.That(methods).Contains("About a rule and how to fix it: explain rule=long-method");
        // files with a method rule: each file by its worst method.
        var files = await _f.Worst("""{"sort":"complex-method","unit":"files"}""");
        await Assert.That(files).StartsWith("Skarp: worst 1 of 1 files in . (syntax tier), sorted by complex-method (cognitive complexity)\n");
        await Assert.That(files).Contains("Small.cs health 10.0 (healthy; 10 = healthy); cognitive complexity 3 (limit 15) at line 12 Small.Two");
        // A type rule ranks files by their largest type.
        var types = await _f.Worst("""{"sort":"large-type"}""");
        await Assert.That(types).Contains("sorted by large-type (source lines)");
        await Assert.That(types).Contains("(limit 100) at line 3 type Small");
    }

    [Test]
    [Arguments(null, null, "health", "files")]
    [Arguments("hotspot", "auto", "hotspot", "files")]
    [Arguments("long-method", null, "long-method", "methods")]
    [Arguments("long-method", "files", "long-method", "files")]
    [Arguments("complex-condition", "methods", "complex-condition", "methods")]
    [Arguments("large-type", null, "large-type", "files")]
    [Arguments("large-file", "files", "large-file", "files")]
    public async Task Unit_auto_and_the_allowed_combinations(string? sort, string? unit, string wantSort, string wantUnit)
    {
        var (choice, error) = RankChoice.Resolve(sort, unit);
        await Assert.That(error).IsNull();
        await Assert.That((choice!.Sort, choice.Unit)).IsEqualTo((wantSort, wantUnit));
    }

    [Test]
    [Arguments("health", "methods", "unit methods needs a method rule as the sort (complex-method, deep-nesting, long-method, too-many-parameters, path-explosion, bumpy-road, complex-condition); health ranks files")]
    [Arguments("hotspot", "methods", "unit methods needs a method rule as the sort")]
    [Arguments("large-type", "methods", "unit methods does not fit large-type, which judges types; use unit files or auto")]
    [Arguments("large-file", "methods", "unit methods does not fit large-file, which judges files")]
    [Arguments("health", "lines", "unknown unit 'lines'; unit takes auto, files or methods")]
    [Arguments("worst", null, "unknown sort 'worst'; sort takes health, hotspot or a rule: complex-method, deep-nesting, long-method, large-type, large-file")]
    [Arguments("empty-catch", null, "rule 'empty-catch' has no measure to sort by; sort takes health, hotspot or a rule:")]
    public async Task Refusals_name_the_valid_values(string sort, string? unit, string message)
    {
        var (choice, error) = RankChoice.Resolve(sort, unit);
        await Assert.That(choice).IsNull();
        await Assert.That(error!).StartsWith(message);
        // The CLI refuses the same way, with exit 2.
        var cli = _f.Agent.Run(_f.Root, ["analyze", _f.Root, "--no-semantic", "--no-save", "--sort", sort, .. unit is null ? Array.Empty<string>() : ["--unit", unit]]);
        await Assert.That(cli.Exit).IsEqualTo(2);
        await Assert.That(cli.Err).Contains(message);
    }

    [Test]
    public async Task Hotspot_without_git_falls_back_to_health_with_a_line()
    {
        SampleCase();
        var text = await _f.Worst("""{"sort":"hotspot","count":2}""");
        await Assert.That(text).StartsWith("Skarp: worst 2 of 4 files with findings in . (syntax tier), sorted by health\nNo git history in this run: sorted by health instead.\n");
        await Assert.That(Headings(text)[0]).IsEqualTo("Bad.cs");
        var cli = _f.Agent.Run(_f.Root, "analyze", _f.Root, "--no-semantic", "--no-save", "--sort", "hotspot", "--format", "json");
        await Assert.That(cli.Out).Contains("\"sort\": \"health\"");
        await Assert.That(cli.Out).Contains("\"requested\": \"hotspot\"");
    }

    [Test]
    public async Task Hotspot_orders_by_commits_then_complexity()
    {
        _f.Write("Quiet.cs", Class("Quiet", Method("M", 40)));
        _f.Write("Busy.cs", Class("Busy", Method("M", 17)));
        _f.Write("Busy2.cs", Class("Busy2", Method("M", 30)));
        var o = SkarpSharp.Configuration.CommandLine.Parse(["analyze", _f.Root, "--no-semantic", "--no-history"]).Options!;
        var (result, _, _, _) = await SkarpSharp.Commands.SkarpCommand.AnalyzeResultAsync(o);
        // The history a git run would add: Busy and Busy2 changed twice, Quiet once.
        result.History = new SkarpSharp.Model.HistorySummary();
        foreach (var file in result.Files) file.History = new SkarpSharp.Model.FileHistory { Commits = file.Path == "Quiet.cs" ? 1 : 2 };
        var ranking = WorstCode.Rank(result, new RankQuery(RankChoice.Resolve("hotspot", null).Choice!, new RankScope { Shown = "." }, 10, 0), o.PathRules());
        await Assert.That(ranking.Sort).IsEqualTo("hotspot");
        // Same commits: the larger cognitive complexity sum first (Busy2 30, Busy 17); then the less active Quiet.
        await Assert.That(ranking.Entries.Select(e => e.Path)).IsEquivalentTo(new[] { "Busy2.cs", "Busy.cs", "Quiet.cs" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        var w = new StringWriter { NewLine = "\n" };
        RankingText.Write(w, ranking, RankingFooter.Mcp);
        await Assert.That(w.ToString()).Contains(", 2 commits in the history window\n");
    }
}
