using System.Text.Json;
using SkarpSharp.Baselines;
using SkarpSharp.Model;

namespace SkarpSharp.Tests.Baselines;

/// <summary>The grouped file layout of both baselines: findings under the path of their file, the <c>type</c> check on read.</summary>
public class BaselineLayoutTests
{
    static readonly FindingSettings Settings = new() { Preset = "default", ComplexMetric = "cognitive", LongMethodLines = "physical" };

    static Finding F(string id, double? value = null) => new() { Id = id, Rule = id.Split('|')[0], Value = value };

    [Test]
    public async Task Findings_are_grouped_under_their_file_and_clean_files_are_listed_with_an_empty_array()
    {
        var text = Baseline.Write(
            [F("long-method|src/B.cs|B::Run", 60), F("large-type|src/B.cs|B", 120), F("long-method|src/A.cs|A::Go", 55)],
            "1.0", Settings, "fp", null, ["src/C.cs", "src/B.cs", "src/A.cs"]);
        var lines = text.Split('\n');
        await Assert.That(lines[2]).IsEqualTo("  \"type\": \"baseline\",");
        using var doc = JsonDocument.Parse(text);
        var files = doc.RootElement.GetProperty("files");
        await Assert.That(files.EnumerateObject().Select(p => p.Name)).IsEquivalentTo(["src/A.cs", "src/B.cs", "src/C.cs"], CollectionOrdering.Matching);
        await Assert.That(files.GetProperty("src/C.cs").GetArrayLength()).IsEqualTo(0);
        // Inside a file: sorted by rule, then place; one finding per line.
        var b = files.GetProperty("src/B.cs").EnumerateArray().Select(e => e.GetProperty("rule").GetString()).ToList();
        await Assert.That(b).IsEquivalentTo(["large-type", "long-method"], CollectionOrdering.Matching);
        await Assert.That(lines.Count(l => l.StartsWith("      { \"rule\": ", StringComparison.Ordinal))).IsEqualTo(3);
        await Assert.That(doc.RootElement.TryGetProperty("repository", out _)).IsFalse();
    }

    [Test]
    public async Task A_finding_without_a_file_goes_to_the_repository_list_and_ids_round_trip_with_a_bar_in_a_path()
    {
        var findings = new List<Finding>
        {
            F("namespace-cycle||namespace A (App)", 2),
            F("long-method|src/a|b.cs|C::M", 61),   // a '|' inside the path moves into the place and joins back
            F("active-complex-file|src/Z.cs|", 3),
        };
        var text = Baseline.Write(findings, "1.0", Settings, null, null, ["src/Z.cs", "src/a|b.cs"]);
        await Assert.That(text).Contains("\"repository\":");
        var back = Baseline.Parse(text, "b");
        await Assert.That(back.Findings.Select(e => (e.Id, e.Value))).IsEquivalentTo(findings.Select(f => (f.Id, f.Value)), CollectionOrdering.Any);
    }

    [Test]
    public async Task The_reader_checks_the_type_of_the_file()
    {
        var syntax = Baseline.Write([], "1.0", Settings, null, null, []);
        var semanticResult = new SemanticResult { Run = new SemanticRunInfo { Findings = SkarpSharp.Findings.RuleSettings.Resolve("default", null, []).ToSemanticInfo(), Targets = "highest" } };
        var semantic = SemanticBaseline.Write(semanticResult, "1.0", "fp", null);

        var wrongSyntax = Assert.Throws<InvalidDataException>(() => Baseline.Parse(semantic, "s.json"));
        await Assert.That(wrongSyntax!.Message).Contains("semantic-baseline file, not a baseline file");
        var wrongSemantic = Assert.Throws<InvalidDataException>(() => SemanticBaseline.Parse(syntax, "b.json"));
        await Assert.That(wrongSemantic!.Message).Contains("baseline file, not a semantic-baseline file");
        var missing = Assert.Throws<InvalidDataException>(() => Baseline.Parse("{ \"version\": 1, \"files\": {} }", "x.json"));
        await Assert.That(missing!.Message).Contains("no \"type\" property");
        await Assert.That(Baseline.Parse(syntax, "b.json").Type).IsEqualTo(Baseline.TypeName);
        await Assert.That(SemanticBaseline.Parse(semantic, "s.json").Files).IsEmpty();
    }
}
