using System.Text.RegularExpressions;
using ModelContextProtocol;
using SkarpSharp.Agent;
using SkarpSharp.Findings;
using SkarpSharp.Mcp;
using SkarpSharp.Model;
using SkarpSharp.Semantic;
using static SkarpSharp.Tests.Mcp.McpFixture;

namespace SkarpSharp.Tests.Mcp;

/// <summary>
/// The MCP tool <c>findings</c> without the protocol: the list equal to
/// <c>skarp show</c> apart from the first line and the closing lines, paging, each filter, an id in full and by prefix,
/// several matches, the errors, the stale line, <c>new_only</c> without a baseline, the "How to improve" paragraph of every
/// rule, a composite and a namespace cycle in full, the ids of <c>find_worst_code</c> resolving, and the saved run a
/// syntax call keeps or replaces.
/// </summary>
public sealed class McpFindingsTests : IDisposable
{
    readonly McpFixture _f = new();

    public void Dispose()
    {
        DotnetRunner.Replacement.Value = null;
        _f.Dispose();
    }

    void Sample()
    {
        _f.Write("Bad.cs", Class("Bad", [.. Enumerable.Range(1, 5).Select(i => Method("M" + i, 17 + i))]));
        _f.Write("Big.cs", Class("Big", Method("Huge", 75), Method("Large", 72)));
        _f.Write("Fine.cs", Class("Fine", Method("Small", 2)));
    }

    string Findings(string json = "{}") => new FindingsTool(_f.Host).Call(Json(json));

    static string Lf(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal);

    static List<string> Lines(string text) => [.. Lf(text).Split('\n')];

    /// <summary>The lines both outputs share: the body, without the first line and the closing lines.</summary>
    static List<string> Body(string text) => [.. Lines(text).Skip(1).Where(l => !l.StartsWith("About ", StringComparison.Ordinal)
        && !l.StartsWith('+') && !l.StartsWith("next: ", StringComparison.Ordinal) && l.Length > 0)];

    static List<string> Ids(string text) => [.. Regex.Matches(text, @"\[([0-9a-f]{8})\]$", RegexOptions.Multiline).Select(m => m.Groups[1].Value)];

    async Task Analyze(params string[] extra)
    {
        var r = _f.Agent.Run(_f.Root, ["analyze", _f.Root, "--no-semantic", "--no-history", .. extra]);
        await Assert.That(r.Exit).IsEqualTo(0).Because(r.Err);
    }

    [Test]
    public async Task The_list_equals_skarp_show_apart_from_the_first_and_closing_lines()
    {
        Sample();
        await _f.Worst("{}");
        foreach (var (tool, cli) in new[]
        {
            ("""{"limit":100}""", new[] { "show", "--all" }),
            ("""{"limit":100,"include_notices":true}""", ["show", "--all", "--include-notices"]),
            ("""{"file":"Bad.cs","include_notices":true}""", ["show", "--file", "Bad.cs", "--include-notices"]),
            ("""{"rule":"long-method"}""", ["show", "--rule", "long-method"]),
        })
        {
            var answer = Findings(tool);
            var show = _f.Agent.Run(_f.Root, cli);
            await Assert.That(show.Exit).IsEqualTo(0).Because(show.Err);
            await Assert.That(Body(answer)).IsEquivalentTo(Body(show.Out), TUnit.Assertions.Enums.CollectionOrdering.Matching).Because(answer + "\n---\n" + show.Out);
            await Assert.That(answer).StartsWith("Skarp: findings 1 to ");
            await Assert.That(answer).Contains(" in the saved run (saved ");
            await Assert.That(answer).Contains("; syntax tier)\n");
            foreach (var cliOnly in new[] { "skarp ", "`", "--" }) await Assert.That(answer).DoesNotContain(cliOnly);
        }
        // With file: the heading has the file's health and where its points went.
        var file = Findings("""{"file":"Bad.cs"}""");
        await Assert.That(file).Contains("\nBad.cs health ");
        await Assert.That(file).Contains("\n  points (");
    }

    [Test]
    public async Task Paging_and_the_closing_lines()
    {
        Sample();
        await _f.Worst("{}");
        var all = Findings("""{"include_notices":true,"limit":100}""");
        int total = Ids(all).Count;
        var first = Findings("""{"include_notices":true,"limit":2}""");
        await Assert.That(Lines(first)[0]).StartsWith($"Skarp: findings 1 to 2 of {total} in the saved run (saved ");
        await Assert.That(first).Contains("\nnext: offset=2\n");
        await Assert.That(first).Contains($"\nAbout a finding: findings id={Ids(first)[0]}\n");
        await Assert.That(first).Contains("\nAbout a rule and how to fix it: explain rule=");
        var second = Findings("""{"include_notices":true,"limit":2,"offset":2}""");
        await Assert.That(Lines(second)[0]).StartsWith($"Skarp: findings 3 to 4 of {total} ");
        // The pages together hold every finding once (warnings first, then by path and line; each page grouped by file).
        var paged = new List<string>();
        for (int offset = 0; offset < total; offset += 2) paged.AddRange(Ids(Findings($$"""{"include_notices":true,"limit":2,"offset":{{offset}}}""")));
        await Assert.That(paged).IsEquivalentTo(Ids(all));
        await Assert.That(paged.Distinct().Count()).IsEqualTo(total);
        await Assert.That(Lines(first).Count(l => l.Contains(" (notice): ", StringComparison.Ordinal))).IsEqualTo(0);
        var last = Findings($$"""{"include_notices":true,"offset":{{total - 1}}}""");
        await Assert.That(last).DoesNotContain("next: ");
        await Assert.That(Findings($$"""{"offset":{{total + 5}}}""")).StartsWith($"Skarp: no findings at offset {total + 5}: ");
        // Notices left out are counted; the rule filter names its rule.
        var warnings = Findings();
        await Assert.That(warnings).Contains(" notices left out: include_notices=true\n");
        await Assert.That(Findings("""{"rule":"complex-method","include_notices":true}""")).Contains("\nAbout a rule and how to fix it: explain rule=complex-method\n");
        await Assert.That(Findings("""{"rule":"god-class"}""")).StartsWith("Skarp: no findings match in the saved run (saved ");
    }

    [Test]
    public async Task An_id_in_full_by_prefix_and_the_errors()
    {
        Sample();
        await _f.Worst("{}");
        var id = Ids(Findings("""{"rule":"long-method"}"""))[0];
        var detail = Findings($$"""{"id":"{{id}}"}""");
        var show = _f.Agent.Run(_f.Root, "show", id);
        // The detail is skarp show's, then the rule's "How to improve" paragraph and the explain call.
        await Assert.That(Lf(detail)).StartsWith(Lf(show.Out).TrimEnd('\n') + "\n\nHow to improve: ");
        await Assert.That(detail).EndsWith("\nMore about the rule: explain rule=long-method\n");
        await Assert.That(Findings($$"""{"id":"{{id[..4].ToUpperInvariant()}}"}""")).IsEqualTo(detail);
        var unknown = Assert.Throws<McpException>(() => Findings("""{"id":"0000"}"""));
        await Assert.That(unknown!.Message).StartsWith($"no finding with id 0000 in the saved run of {_f.Root} (saved ");
        await Assert.That(unknown.Message).EndsWith("); findings file=<path> lists a file's findings");
        foreach (var (json, message) in new[]
        {
            ("""{"id":"cdce","file":"Bad.cs"}""", "id shows one finding; it does not combine with file, rule or new_only"),
            ("""{"id":"xyz1"}""", "id must be 4 to 8 hexadecimal characters, as in the brackets at the end of a finding line (got 'xyz1')"),
            ("""{"rule":"long-methods"}""", "unknown rule 'long-methods'; rules: complex-method, "),
            ("""{"limit":101}""", "limit must be 1 to 100 (got 101)"),
            ("""{"offset":-1}""", "offset must be 0 to 100000 (got -1)"),
            ("""{"ids":"x"}""", "unknown argument 'ids'; findings takes id, file, rule, new_only, include_notices, limit, offset, path"),
        })
        {
            var ex = Assert.Throws<McpException>(() => Findings(json));
            await Assert.That(ex!.Message).StartsWith(message);
        }
    }

    [Test]
    public async Task Without_a_saved_run_it_says_so_and_never_analyzes()
    {
        Sample();
        var ex = Assert.Throws<McpException>(() => Findings());
        await Assert.That(ex!.Message).IsEqualTo($"no saved run in {_f.Root}; find_worst_code saves one");
        await Assert.That(Directory.Exists(Path.Combine(_f.Root, ".skarp"))).IsFalse();
    }

    [Test]
    public async Task Stale_data_is_answered_with_a_line_and_new_only_without_a_baseline_says_why()
    {
        Sample();
        await _f.Worst("{}");
        _f.Write("Big.cs", Class("Big", Method("Huge", 76), Method("Large", 72)));
        var answer = Findings("""{"file":"Big.cs"}""");
        await Assert.That(Lines(answer)[1]).StartsWith("stale 1 of 1 file shown changed since the data was saved (");
        await Assert.That(Lines(answer)[1]).EndsWith("; refresh: find_worst_code");
        var newOnly = Findings("""{"new_only":true}""");
        await Assert.That(newOnly).Contains("\nThe saved run was not compared with a baseline, so nothing is marked new or worse.\n");
        // With a baseline: only what is new or worse than it.
        var b = _f.Agent.Run(_f.Root, "baseline", _f.Root, "--no-semantic", "--no-history");
        await Assert.That(b.Exit).IsEqualTo(0).Because(b.Err);
        _f.Write("Fine.cs", Class("Fine", Method("Small", 80)));
        await Analyze();
        var compared = Findings("""{"new_only":true}""");
        await Assert.That(compared).DoesNotContain("not compared with a baseline");
        await Assert.That(Ids(compared)).IsNotEmpty();
        await Assert.That(compared).Contains("\nFine.cs\n  new line ").Because(compared);
        await Assert.That(compared).DoesNotContain(" known ");
    }

    [Test]
    public async Task Every_rule_has_a_How_to_improve_paragraph_without_a_command()
    {
        foreach (var rule in RuleCatalog.Known)
        {
            var how = RuleCatalog.HowToImprove(rule);
            await Assert.That(how).IsNotNull().Because(rule.Id);
            await Assert.That(how!).StartsWith("How to improve: ");
            await Assert.That(how).DoesNotContain("\r");
            // As the detail prints it: no command line, no internal reference.
            var shown = FindingsTool.HowToImprove(rule)!;
            foreach (var cliOnly in new[] { "skarp show", "skarp explain", "skarp baseline", "--verbose" }) await Assert.That(shown).DoesNotContain(cliOnly).Because(rule.Id);
            await Assert.That(SkarpSharp.Tests.NoInternalReferencesTests.Hits(rule.Id, shown)).IsEmpty();
        }
    }

    [Test]
    public async Task A_composite_in_full_lists_its_parts()
    {
        _f.Write("Importer.cs", SkarpSharp.Tests.Findings.BrainTests.Importer());
        await Analyze();
        var list = Findings("""{"rule":"brain-method","include_notices":true}""");
        var id = Ids(list).Single();
        var detail = Findings($$"""{"id":"{{id}}"}""");
        await Assert.That(detail).Contains("\n  parts (").Because(detail);
        await Assert.That(detail).Contains("\nHow to improve: split it.");
        await Assert.That(detail).EndsWith("\nMore about the rule: explain rule=brain-method\n");
    }

    /// <summary>The saved run of <see cref="Sample"/> made by analyze, marked as a run whose semantic tier ran with
    /// <paramref name="findings"/> and <paramref name="components"/>.</summary>
    async Task SavedSemanticRun(List<Finding> findings, List<NamespaceComponent>? components = null)
    {
        await Analyze();
        var saved = LastRun.Load(_f.Root)!;
        saved.Analysis!.Run.Semantic = new SemanticTierInfo { Status = "ran" };
        saved.Analysis.SemanticTier = new SemanticTierResults
        {
            Status = "ran",
            Results = [new SemanticResult { Run = new SemanticRunInfo { Status = "ran" }, Findings = findings, Namespaces = components is null ? null : new NamespaceSummary { Components = components } }],
        };
        LastRun.Save(_f.Root, saved.Analysis, saved.Header.SettingsFingerprint, "test", GitState.None, DateTimeOffset.UtcNow);
    }

    static Finding Semantic(string rule, string path, string place, string? member = null) => new()
    {
        Id = $"{rule}|{path}|{place}", Rule = rule, Severity = "warning", Path = path, Line = 1, Member = member ?? place, Message = rule,
    };

    [Test]
    public async Task A_namespace_cycle_in_full_has_its_members_and_cuts_and_several_matches_are_listed()
    {
        Sample();
        var cycle = Semantic(RuleIds.NamespaceCycle, "", "Sample.A", "Sample.A");
        var component = new NamespaceComponent
        {
            Place = "Sample.A", Size = 2, Members = ["Sample.A", "Sample.B"], ShortestCycle = ["Sample.A", "Sample.B", "Sample.A"],
            Cut = [new NamespaceEdge { From = "Sample.B", To = "Sample.A", Weight = 1 }], CutTypePairs = 1, CutComplete = true,
            Dependencies = [new NamespaceDependency { From = "Sample.B", To = "Sample.A", Weight = 1, Examples = [new NamespaceExample { Path = "Big.cs", Line = 3, FromType = "Sample.B.X", ToType = "Sample.A.Y" }] }],
        };
        // Two findings whose ids share their first four characters.
        var byPrefix = new Dictionary<string, Finding>(StringComparer.Ordinal);
        (Finding, Finding)? pair = null;
        for (int i = 0; pair is null; i++)
        {
            var f = Semantic(RuleIds.HighCoupling, "Bad.cs", "T" + i);
            var prefix = FindingEngine.HandleOf(f.Id)[..4];
            if (byPrefix.TryGetValue(prefix, out var other)) pair = (other, f);
            else byPrefix[prefix] = f;
        }
        await SavedSemanticRun([cycle, pair.Value.Item1, pair.Value.Item2], [component]);
        var list = Findings("""{"rule":"namespace-cycle"}""");
        await Assert.That(list).Contains("; syntax and semantic tiers)\n").Because(list);
        var detail = Findings($$"""{"id":"{{FindingEngine.HandleOf(cycle.Id)}}"}""");
        await Assert.That(detail).Contains("\n  namespaces (2): Sample.A, Sample.B\n").Because(detail);
        await Assert.That(detail).Contains("\n  cut 1: Sample.B -> Sample.A (1 type pair)\n    Big.cs:3 Sample.B.X -> Sample.A.Y\n");
        await Assert.That(detail).Contains("\nHow to improve: ");
        var prefix4 = FindingEngine.HandleOf(pair.Value.Item1.Id)[..4];
        var several = Findings($$"""{"id":"{{prefix4}}"}""");
        await Assert.That(Lines(several)[0]).IsEqualTo($"Skarp: 2 findings share the id prefix {prefix4}; give more characters:");
        await Assert.That(Ids(several).Count).IsEqualTo(2);
    }

    [Test]
    public async Task Every_id_find_worst_code_prints_resolves_at_both_depths()
    {
        Sample();
        foreach (var json in new[] { "{}", """{"sort":"long-method"}""", """{"sort":"complex-method","unit":"files"}""" })
            foreach (var id in Ids(await _f.Worst(json)))
                await Assert.That(Findings($$"""{"id":"{{id}}"}""")).Contains($"[{id}]");
        await SavedSemanticRun([Semantic(RuleIds.HighCoupling, "Bad.cs", "Sample.Bad")]);
        _f.Write("Lib.slnx", "<Solution />");
        DotnetRunner.Replacement.Value = (_, _, _) => Task.FromResult(new ProcessRun(1, "", "error: fake", 1, 0));
        var semantic = await _f.Worst("""{"depth":"semantic"}""");
        await Assert.That(semantic).Contains("From the saved run").Because(semantic);
        foreach (var id in Ids(semantic))
            await Assert.That(Findings($$"""{"id":"{{id}}"}""")).Contains($"[{id}]");
    }

    [Test]
    public async Task A_syntax_call_keeps_a_fresh_semantic_run_and_replaces_a_stale_one()
    {
        Sample();
        await SavedSemanticRun([Semantic(RuleIds.HighCoupling, "Bad.cs", "Sample.Bad")]);
        var written = LastRun.Load(_f.Root)!.Header.WrittenAt;
        await _f.Worst("{}");
        var kept = LastRun.Load(_f.Root)!;
        await Assert.That(kept.Analysis!.Run.Semantic!.Status).IsEqualTo("ran");
        await Assert.That(kept.Header.WrittenAt).IsEqualTo(written);
        await Assert.That(Findings("""{"rule":"high-coupling"}""")).Contains(" high-coupling: ");
        _f.Write("Fine.cs", Class("Fine", Method("Small", 3)));
        await _f.Worst("{}");
        await Assert.That(LastRun.Load(_f.Root)!.Analysis!.Run.Semantic?.Status).IsNotEqualTo("ran");
    }

    [Test]
    public async Task The_largest_answer_stays_under_the_budget()
    {
        for (int i = 0; i < 40; i++)
            _f.Write($"src/Some/Deeply/Nested/Area{i % 4}/ModuleWithALongName{i}.cs", Class("Module" + i, [.. Enumerable.Range(1, 6).Select(m => Method($"VeryLongMethodNameNumber{m}", 18 + m * 9))]));
        await Analyze();
        var answer = Findings("""{"limit":100,"include_notices":true}""");
        await Assert.That(Ids(answer).Count).IsEqualTo(100);
        await Assert.That(answer.Length).IsLessThan(40_000);
        await Assert.That(answer).DoesNotContain("\r");
    }
}
