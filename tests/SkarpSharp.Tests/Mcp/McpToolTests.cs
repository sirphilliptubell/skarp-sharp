using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using SkarpSharp.Agent;
using SkarpSharp.Mcp;
using SkarpSharp.Semantic;
using static SkarpSharp.Tests.Mcp.McpFixture;

namespace SkarpSharp.Tests.Mcp;

/// <summary>
/// The MCP tools without the protocol: the golden test against
/// <c>skarp analyze --sort</c>, the tool-list shape snapshot, argument reading and its errors, the answer budget, the saved
/// run's reuse at semantic depth, the one-run lock, the call's token reaching the semantic tier's child processes (a fake
/// runner: no real msbuild), <c>explain</c>, and no internal references in anything the server says.
/// </summary>
public sealed class McpToolTests : IDisposable
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
        _f.Write("Big.cs", Class("Big", Method("Huge", 75)));
        _f.Write("Fine.cs", Class("Fine", Method("Small", 2)));
        _f.Write("notes.txt", "not code");
    }

    static string Lf(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>The lines both outputs share: without the fold lines and the closing lines that point at the CLI or the tools.</summary>
    static string WithoutFooters(string text) =>
        string.Join("\n", Lf(text).Split('\n').Where(l => !l.StartsWith("next: ", StringComparison.Ordinal) && !l.StartsWith("About ", StringComparison.Ordinal)
            && !l.StartsWith("  +", StringComparison.Ordinal)));

    [Test]
    [Arguments("health", null)]
    [Arguments("long-method", null)]
    [Arguments("complex-method", "files")]
    [Arguments("large-file", null)]
    public async Task The_tool_text_equals_analyze_sort_apart_from_the_footers(string sort, string? unit)
    {
        Sample();
        var json = $$"""{"path":{{JsonSerializer.Serialize(_f.Root)}},"sort":"{{sort}}"{{(unit is null ? "" : $",\"unit\":\"{unit}\"")}},"count":3}""";
        var tool = await _f.Worst(json);
        var cli = _f.Agent.Run(_f.Root, ["analyze", _f.Root, "--no-semantic", "--sort", sort, "--top", "3", "--format", "text", .. unit is null ? Array.Empty<string>() : ["--unit", unit]]);
        await Assert.That(cli.Exit).IsEqualTo(0).Because(cli.Err);
        await Assert.That(WithoutFooters(tool)).IsEqualTo(WithoutFooters(cli.Out));
        // MCP answers use \n only, name the explain tool, never a CLI command, --verbose or a baseline.
        await Assert.That(tool).DoesNotContain("\r");
        await Assert.That(tool).Contains("explain rule=");
        await Assert.That(tool).Contains("\nAbout a finding: findings id=");
        // Fold lines name the findings tool in place of the CLI's "in this file".
        foreach (var fold in Lf(tool).Split('\n').Where(l => l.StartsWith("  +", StringComparison.Ordinal)))
            await Assert.That(fold).Contains(" more: findings file=");
        foreach (var cliOnly in new[] { "skarp ", "--verbose", "baseline", "`" }) await Assert.That(tool).DoesNotContain(cliOnly);
        await Assert.That(Lf(cli.Out)).Contains("About ");
    }

    [Test]
    public async Task The_json_document_holds_the_ranking()
    {
        Sample();
        var cli = _f.Agent.Run(_f.Root, "analyze", _f.Root, "--no-semantic", "--no-save", "--sort", "long-method", "--top", "2", "--format", "json");
        using var doc = JsonDocument.Parse(cli.Out);
        var ranking = doc.RootElement.GetProperty("ranking");
        await Assert.That(ranking.GetProperty("sort").GetString()).IsEqualTo("long-method");
        await Assert.That(ranking.GetProperty("unit").GetString()).IsEqualTo("methods");
        await Assert.That(ranking.GetProperty("metric").GetString()).IsEqualTo("physicalLines");
        var first = ranking.GetProperty("entries")[0];
        await Assert.That((first.GetProperty("rank").GetInt32(), first.GetProperty("path").GetString(), first.GetProperty("member").GetString(), first.GetProperty("limit").GetString()))
            .IsEqualTo((1, "Big.cs", "Big.Huge", "70"));
        await Assert.That(ranking.GetProperty("entries").GetArrayLength()).IsEqualTo(2);
    }

    [Test]
    public async Task Tool_list_shape()
    {
        var host = new McpHost();
        var options = McpCommand.Options(host);
        var list = new System.Text.StringBuilder();
        list.AppendLine($"serverInfo: {options.ServerInfo!.Name}");
        list.AppendLine($"instructions ({McpCommand.Instructions.Length} characters):");
        list.AppendLine(McpCommand.Instructions);
        foreach (var tool in McpCommand.Tools(host))
            list.AppendLine(JsonSerializer.Serialize(tool.ProtocolTool, McpJsonUtilities.DefaultOptions));
        await Snapshots.Verify(Pretty(list.ToString()), "Mcp", "tool-list", "txt", "SKARP_UPDATE_SHAPES");
        await Assert.That(McpCommand.Instructions.Length).IsLessThan(1200);
        await Assert.That(McpCommand.Tools(host).Select(t => t.ProtocolTool.Name))
            .IsEquivalentTo(new[] { "find_worst_code", "check", "findings", "explain" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        // No rule-id list in any schema but the closed set of sorts.
        await Assert.That(McpCommand.Tools(host).Count(t => t.ProtocolTool.InputSchema.GetRawText().Contains("\"enum\"", StringComparison.Ordinal)
            && t.ProtocolTool.InputSchema.GetRawText().Contains("unused-member", StringComparison.Ordinal))).IsEqualTo(0);
    }

    /// <summary>Each JSON line indented, so the snapshot diffs by property.</summary>
    static string Pretty(string text) => string.Join("\n", text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l =>
        l.StartsWith('{') ? JsonSerializer.Serialize(JsonDocument.Parse(l).RootElement, PrettyOptions) : l));

    static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, TypeInfoResolver = Indented.Default };

    [Test]
    public async Task Null_and_empty_mean_absent()
    {
        var request = FindWorstCodeTool.Read(Json("""{"path":"","count":"null","depth":null,"sort":"null","unit":"","offset":"null"}"""));
        await Assert.That((request.Path, request.Count, request.Semantic, request.Choice.Sort, request.Choice.Unit, request.Offset))
            .IsEqualTo((".", 10, false, "health", "files", 0));
        var strings = FindWorstCodeTool.Read(Json("""{"count":"7","offset":"3","depth":"semantic","sort":"deep-nesting"}"""));
        await Assert.That((strings.Count, strings.Offset, strings.Semantic, strings.Choice.Unit)).IsEqualTo((7, 3, true, "methods"));
    }

    [Test]
    [Arguments("""{"count":30}""", "count must be 1 to 25 (got 30)")]
    [Arguments("""{"count":0}""", "count must be 1 to 25 (got 0)")]
    [Arguments("""{"count":"many"}""", "count must be a whole number from 1 to 25 (got 'many')")]
    [Arguments("""{"offset":-1}""", "offset must be 0 to 10000 (got -1)")]
    [Arguments("""{"depth":"deep"}""", "depth must be syntax or semantic (got 'deep')")]
    [Arguments("""{"sort":"worst"}""", "unknown sort 'worst'; sort takes health, hotspot or a rule: complex-method")]
    [Arguments("""{"unit":"methods"}""", "unit methods needs a method rule as the sort")]
    [Arguments("""{"limit":5}""", "unknown argument 'limit'; find_worst_code takes path, count, depth, sort, unit, offset")]
    [Arguments("""{"path":5}""", "path must be a string")]
    public async Task Argument_errors_name_the_cap_or_the_valid_values(string json, string message)
    {
        var ex = await Assert.ThrowsAsync<McpException>(() => _f.Worst(json));
        await Assert.That(ex!.Message).StartsWith(message);
    }

    [Test]
    public async Task Explain_lists_rules_explains_one_and_refuses_an_unknown_one()
    {
        var tool = new ExplainTool(_f.Host);
        var list = tool.Call(Json("""{"rule":""}"""));
        await Assert.That(list).StartsWith("Skarp rules (explain rule=<id>:");
        await Assert.That(list).Contains("\n  long-method ");
        // In force: the repository's settings (here a notice limit of 50), not the table of every preset.
        _f.Write(".skarp.json", """{ "version": 1, "thresholds": { "long-method": { "notice": 50 } } }""");
        var inForce = tool.Call(Json("""{"rule":"long-method"}"""));
        await Assert.That(inForce).StartsWith("long-method: Long method (judged per member)\n");
        await Assert.That(inForce).Contains("Thresholds in force here (preset default;");
        await Assert.That(inForce).Contains("  notice:  physicalLines over 50");
        await Assert.That(inForce).DoesNotContain("Thresholds per preset");
        var all = tool.Call(Json("""{"rule":"long-method","all_presets":true}"""));
        await Assert.That(all).Contains("Thresholds per preset");
        await Assert.That(all).Contains("\n  codescene\n");
        var ex = Assert.Throws<McpException>(() => tool.Call(Json("""{"rule":"long-methods"}""")));
        await Assert.That(ex!.Message).StartsWith("unknown rule 'long-methods'; rules: complex-method, deep-nesting, long-method,");
        var unknown = Assert.Throws<McpException>(() => tool.Call(Json("""{"rules":"x"}""")));
        await Assert.That(unknown!.Message).IsEqualTo("unknown argument 'rules'; explain takes rule, all_presets, path");
        var flag = Assert.Throws<McpException>(() => tool.Call(Json("""{"rule":"long-method","all_presets":"yes"}""")));
        await Assert.That(flag!.Message).IsEqualTo("all_presets must be true or false (got 'yes')");
    }

    [Test]
    public async Task Skipped_inputs_are_listed_with_the_reason()
    {
        Sample();
        _f.Write("tests/Unit.Tests/Unit.Tests.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="xunit" Version="2.9.0" /></ItemGroup></Project>""");
        _f.Write("tests/Unit.Tests/OrderTests.cs", Class("OrderTests", Method("T", 20)));
        // The settings file makes this folder the root for every path under it (outside git, a file's own folder would be).
        _f.Write(".skarp.json", """{ "version": 1 }""");
        await Assert.That(await _f.Worst("""{"path":"notes.txt"}""")).Contains("\nSkipped: notes.txt (not C#)\n");
        await Assert.That(await _f.Worst("""{"path":"tests/Unit.Tests/OrderTests.cs"}""")).Contains("\nSkipped: tests/Unit.Tests/OrderTests.cs (test code)\n");
        var missing = await Assert.ThrowsAsync<McpException>(() => _f.Worst("""{"path":"gone"}"""));
        await Assert.That(missing!.Message).IsEqualTo($"path not found: {Path.Combine(_f.Root, "gone")}");
        // One file ranks on its own; a folder ranks its files; paths stay relative to the root.
        var one = await _f.Worst("""{"path":"Bad.cs"}""");
        await Assert.That(one).StartsWith("Skarp: worst 1 of 1 files with findings in Bad.cs (syntax tier), sorted by health\n");
    }

    [Test]
    public async Task The_largest_answer_stays_under_the_budget()
    {
        for (int i = 0; i < 40; i++)
            _f.Write($"src/Area{i % 4}/Module{i}.cs", Class("Module" + i, [.. Enumerable.Range(1, 6).Select(m => Method($"VeryLongMethodNameNumber{m}", 18 + m * 9))]));
        foreach (var json in new[] { """{"count":25}""", """{"count":25,"sort":"long-method"}""", """{"count":25,"sort":"complex-method","unit":"files"}""" })
        {
            var text = await _f.Worst(json);
            await Assert.That(Regex.Matches(text, @"^\S", RegexOptions.Multiline).Count).IsGreaterThan(25);
            await Assert.That(text.Length).IsLessThan(40_000).Because(json);
        }
    }

    /// <summary>The saved run of <see cref="Sample"/> made by analyze, marked as a run whose semantic tier ran (the fake
    /// runner cannot make the tier run).</summary>
    async Task SavedSemanticRun()
    {
        Sample();
        var r = _f.Agent.Run(_f.Root, "analyze", _f.Root, "--no-semantic");
        await Assert.That(r.Exit).IsEqualTo(0).Because(r.Err);
        var saved = LastRun.Load(_f.Root)!;
        saved.Analysis!.Run.Semantic = new SkarpSharp.Model.SemanticTierInfo { Status = "ran" };
        saved.Analysis.SemanticTier = new SkarpSharp.Model.SemanticTierResults { Status = "ran", Results = [] };
        LastRun.Save(_f.Root, saved.Analysis, saved.Header.SettingsFingerprint, "test", GitState.None, DateTimeOffset.UtcNow);
        _f.Write("Lib.slnx", "<Solution />");
    }

    [Test]
    public async Task A_second_semantic_call_reuses_the_saved_run_and_a_changed_file_runs_the_tier_again()
    {
        await SavedSemanticRun();
        int calls = 0;
        DotnetRunner.Replacement.Value = (_, _, _) => { Interlocked.Increment(ref calls); return Task.FromResult(new ProcessRun(1, "", "error: fake", 1, 0)); };
        var reused = await _f.Worst("""{"depth":"semantic"}""");
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(reused).StartsWith("Skarp: worst 2 of 2 files with findings in . (semantic tier), sorted by health\nFrom the saved run:");
        // The findings read back from the saved run print as the analysis printed them (short place names, ids).
        static List<string> Indented(string text) => [.. text.Split('\n').Where(l => l.StartsWith("  ", StringComparison.Ordinal))];
        await Assert.That(Indented(reused)).IsEquivalentTo(Indented(await _f.Worst("{}")), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        _f.Write("Fine.cs", Class("Fine", Method("Small", 3)));
        var again = await _f.Worst("""{"depth":"semantic"}""");
        await Assert.That(calls).IsGreaterThan(0);
        // The tier could not run: the syntax ranking and the reason, not an error.
        await Assert.That(again).StartsWith("Skarp: worst 2 of 2 files with findings in . (syntax tier), sorted by health\n");
        await Assert.That(again).Contains("\nSemantic tier: not evaluated: no solution could be listed");
        await Assert.That(again).Contains(Agent.AgentHost.Lines(again).First(l => l.StartsWith("Not looked at", StringComparison.Ordinal)));
    }

    [Test]
    public async Task A_second_semantic_call_while_one_runs_is_refused()
    {
        Sample();
        _f.Write("Lib.slnx", "<Solution />");
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        DotnetRunner.Replacement.Value = async (_, _, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return new ProcessRun(1, "", "error: fake", 1, 0);
        };
        var first = _f.Worst("""{"depth":"semantic"}""");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(60));
        var ex = await Assert.ThrowsAsync<McpException>(() => _f.Worst("""{"depth":"semantic","path":"Bad.cs"}"""));
        await Assert.That(ex!.Message).IsEqualTo($"a semantic run is already going for {_f.Root}; try again when it ends");
        // Syntax depth is not held up.
        await Assert.That(await _f.Worst("{}")).Contains("(syntax tier)");
        release.SetResult();
        await Assert.That(await first).Contains("Semantic tier: not evaluated:");
        await Assert.That(await _f.Worst("""{"depth":"semantic"}""")).Contains("Semantic tier: not evaluated:");
    }

    [Test]
    public async Task The_calls_token_reaches_the_child_processes()
    {
        Sample();
        _f.Write("Lib.slnx", "<Solution />");
        using var cts = new CancellationTokenSource();
        var seen = new List<CancellationToken>();
        DotnetRunner.Replacement.Value = (args, _, ct) =>
        {
            lock (seen) seen.Add(ct);
            return Task.FromResult(new ProcessRun(1, "", "error: fake", 1, 0));
        };
        await _f.Worst("""{"depth":"semantic"}""", cts.Token);
        await Assert.That(seen).IsNotEmpty();
        cts.Cancel();
        await Assert.That(seen.All(t => t.IsCancellationRequested)).IsTrue();
    }

    [Test]
    public async Task Nothing_the_server_says_has_internal_references()
    {
        Sample();
        var texts = new List<(string Name, string Text)>
        {
            ("usage", McpCommand.Usage), ("instructions", McpCommand.Instructions),
            ("find_worst_code description", FindWorstCodeTool.Description), ("explain description", ExplainTool.Description),
            ("check description", CheckTool.Description), ("findings description", FindingsTool.Description),
            ("check", await new CheckTool(_f.Host).CallAsync(Json($$"""{"files":["{{_f.Root.Replace("\\", "/")}}/Bad.cs","notes.txt"]}"""), default)),
            ("health", await _f.Worst("{}")), ("rule", await _f.Worst("""{"sort":"deep-nesting"}""")),
            ("findings", new FindingsTool(_f.Host).Call(null)), ("findings file", new FindingsTool(_f.Host).Call(Json("""{"file":"Bad.cs"}"""))),
            ("explain", new ExplainTool(_f.Host).Call(null)),
        };
        foreach (var tool in McpCommand.Tools(_f.Host)) texts.Add((tool.ProtocolTool.Name + " schema", tool.ProtocolTool.InputSchema.GetRawText()));
        foreach (var rule in SkarpSharp.Findings.RuleCatalog.Every)
            texts.Add(("explain " + rule.Id, new ExplainTool(_f.Host).Call(Json($$"""{"rule":"{{rule.Id}}"}"""))));
        var hits = texts.SelectMany(t => SkarpSharp.Tests.NoInternalReferencesTests.Hits(t.Name, t.Text)).ToList();
        await Assert.That(hits).IsEmpty().Because(string.Join("\n", hits));
        // Descriptions: no examples, no capitals for emphasis.
        foreach (var d in new[] { FindWorstCodeTool.Description, ExplainTool.Description, CheckTool.Description, FindingsTool.Description, McpCommand.Instructions })
        {
            await Assert.That(d).DoesNotContain("e.g.");
            await Assert.That(d).DoesNotContain("for example");
            await Assert.That(Regex.IsMatch(d, @"\b[A-Z]{4,}\b")).IsFalse().Because(d);
        }
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(JsonElement))]
internal sealed partial class Indented : System.Text.Json.Serialization.JsonSerializerContext;
