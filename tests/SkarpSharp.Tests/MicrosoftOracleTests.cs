using System.Text.Json;
using SkarpSharp.Analysis;
using SkarpSharp.Model;

namespace SkarpSharp.Tests;

/// <summary>
/// Regression test against Microsoft.CodeAnalysis.Metrics: the expected Microsoft CyclomaticComplexity of every
/// member of tests/oracle/EdgeCases, recorded in Oracle/microsoft-cyclomatic-edgecases.json from the oracle run
/// (regenerate with tools/oracle, README section 5), must equal skarp's
/// cyclomaticMicrosoft. Known differences carry skarp's documented value and the reason; there is one
/// (Branching.NonShortCircuitBool: <c>a &amp; b | c</c> on bool parameters needs types, Microsoft 3, skarp 1).
/// </summary>
public class MicrosoftOracleTests
{
    sealed record Expected(string File, string Id, string MicrosoftName, int MicrosoftLine, int Cyclomatic, int? SkarpValue, string? Reason);

    static readonly Lazy<(List<Expected> Members, List<string> SkarpOnly)> Data = new(LoadData);
    static readonly Lazy<AnalysisResult> Result = new(() =>
        Analyzer.AnalyzeDirectory(EdgeCasesDir(), new AnalyzerOptions { Parallelism = 2 }));

    static (List<Expected>, List<string>) LoadData()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Oracle", "microsoft-cyclomatic-edgecases.json")));
        var members = new List<Expected>();
        foreach (var m in doc.RootElement.GetProperty("members").EnumerateArray())
        {
            bool known = m.TryGetProperty("knownDifference", out var kd);
            members.Add(new Expected(m.GetProperty("file").GetString()!, m.GetProperty("id").GetString()!,
                m.GetProperty("microsoftName").GetString()!, m.GetProperty("microsoftLine").GetInt32(), m.GetProperty("cyclomatic").GetInt32(),
                known ? kd.GetProperty("skarp").GetInt32() : null, known ? kd.GetProperty("reason").GetString() : null));
        }
        var skarpOnly = doc.RootElement.GetProperty("skarpOnly").EnumerateArray().Select(e => e.GetString()!).ToList();
        return (members, skarpOnly);
    }

    /// <summary>tests/oracle/EdgeCases, found by walking up from the test binary to the repository root.</summary>
    static string EdgeCasesDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SkarpSharp.slnx")))
                return Path.Combine(d.FullName, "tests", "oracle", "EdgeCases");
        throw new DirectoryNotFoundException("repository root (SkarpSharp.slnx) not found above " + AppContext.BaseDirectory);
    }

    static Dictionary<(string File, string Id), MemberResult> TopLevelMembers() =>
        Result.Value.Files
            .SelectMany(f => f.Members.Where(m => m.Parent is null).Select(m => (Key: (f.Path.Replace('\\', '/'), m.Id), m)))
            .ToDictionary(x => x.Key, x => x.m);

    [Test]
    public async Task Cyclomatic_microsoft_equals_microsoft_metrics_on_edge_cases()
    {
        var ours = TopLevelMembers();
        var (expected, _) = Data.Value;
        await Assert.That(expected.Count >= 100).IsTrue().Because($"expected data too small: {expected.Count}");
        var failures = new List<string>();
        foreach (var e in expected)
        {
            if (!ours.TryGetValue((e.File, e.Id), out var m)) { failures.Add($"{e.File} {e.Id}: member not found (Microsoft: {e.MicrosoftName} @{e.MicrosoftLine})"); continue; }
            int want = e.SkarpValue ?? e.Cyclomatic;
            if (m.CyclomaticMicrosoft != want)
                failures.Add($"{e.File}:{e.MicrosoftLine} {e.Id}: skarp {m.CyclomaticMicrosoft}, expected {want}" +
                    (e.SkarpValue is null ? " (Microsoft's value)" : $" (documented difference; Microsoft {e.Cyclomatic}: {e.Reason})"));
        }
        await Assert.That(failures.Count == 0).IsTrue().Because($"{failures.Count} of {expected.Count} members differ:\n" + string.Join("\n", failures));
    }

    [Test]
    public async Task Exactly_one_documented_difference_remains()
    {
        var known = Data.Value.Members.Where(e => e.SkarpValue is not null).ToList();
        var k = await Assert.That(known).HasSingleItem();
        await Assert.That(k.Id).IsEqualTo("OracleEdgeCases.Branching::NonShortCircuitBool(bool,bool,bool)");
        await Assert.That((k.Cyclomatic, k.SkarpValue!.Value)).IsEqualTo((3, 1));
        await Assert.That(string.IsNullOrWhiteSpace(k.Reason) || k.Reason == "TODO").IsFalse().Because("the difference needs a documented reason");
    }

    [Test]
    public async Task Every_edge_case_member_is_covered_by_the_oracle_data()
    {
        // A member added to tests/oracle/EdgeCases without regenerating the data would otherwise go unchecked. The only
        // skarp-only member is the partial-method implementation, which Microsoft's engine does not measure (it reports
        // the defining declaration).
        var (expected, skarpOnly) = Data.Value;
        var covered = expected.Select(e => (e.File, e.Id)).ToHashSet();
        var missing = TopLevelMembers().Keys.Where(k => !covered.Contains(k) && !skarpOnly.Contains(k.Id)).Select(k => $"{k.File} {k.Id}").ToList();
        await Assert.That(missing.Count == 0).IsTrue().Because("members without expected Microsoft values (rerun tools/oracle, README section 5):\n" + string.Join("\n", missing));
        await Assert.That(skarpOnly).IsEquivalentTo(["OracleEdgeCases.PartialType::Hook(int)"], CollectionOrdering.Matching);
    }
}