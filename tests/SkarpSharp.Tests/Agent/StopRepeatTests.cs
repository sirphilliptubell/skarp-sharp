using System.Text.Json;
using SkarpSharp.Agent;
using SkarpSharp.Tests.Findings;
using SkarpSharp.Tests.History;
using static SkarpSharp.Tests.Agent.AgentHost;

namespace SkarpSharp.Tests.Agent;

/// <summary>
/// The <c>Stop</c> summary leaves out what the edit checks of the turn
/// already printed and is unchanged since; it still reports what added up across edits, lines the budget cut off,
/// files changed outside the edit tools, composites whose parts moved, and health scores that differ from the one shown.
/// Duplicate code at Stop after an edit check: <see cref="TrackingTests.Stop_reports_a_new_clone_group_with_the_hint"/>.
/// </summary>
public sealed class StopRepeatTests : IDisposable
{
    readonly AgentHost _host = new();
    readonly SyntheticRepo _repo = new();
    static readonly DateTimeOffset T0 = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    public StopRepeatTests() => _host.Now = T0;

    public void Dispose()
    {
        _host.Dispose();
        _repo.Dispose();
    }

    string Commit(string path, string content)
    {
        _repo.Write(path, content);
        _repo.Commit("add " + path, T0.AddDays(-1));
        return Path.Combine(_repo.Root, path);
    }

    /// <summary>The raw hook output (empty: nothing printed).</summary>
    string Raw(string ev, string? file = null, string? agent = null, params string[] extra) => _host.RunWithInput(_repo.Root, JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["session_id"] = "rep", ["hook_event_name"] = ev, ["tool_name"] = "Edit", ["stop_hook_active"] = false, ["agent_id"] = agent,
        ["tool_input"] = new Dictionary<string, string?> { ["file_path"] = file },
    }), ["check", "--hook", "claude", .. extra]).Out;

    /// <summary>The lines a hook answered with (additional context or block reason); empty when it printed nothing.</summary>
    static List<string> Context(string raw)
    {
        if (raw.Length == 0) return [];
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        return Lines(root.TryGetProperty("reason", out var reason) ? reason.GetString()! : root.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!);
    }

    List<string> Edit(string file, params string[] extra) => Context(Raw("PostToolUse", file, null, extra));

    List<string> Stop(params string[] extra) => Context(Raw("Stop", null, null, extra));

    static string Text(List<string> lines) => string.Join("\n", lines);

    [Test]
    public async Task One_edit_then_Stop_prints_nothing()
    {
        var file = Commit("src/A.cs", Class("A", Method(9)));
        File.WriteAllText(file, Class("A", Method(10), Method(9, 1)));
        var shown = Edit(file);
        await Assert.That(HealthOnly(Text(shown))).HasSingleItem();
        await Assert.That(FindingLines(Text(shown)).Count).IsEqualTo(2);              // worse, new
        await Assert.That(Raw("Stop")).IsEqualTo("");
        // The Stop state is recorded; the shown lines stay (an earlier Stop's lines count as seen too).
        var state = State();
        await Assert.That(state.Stop).IsNotNull();
        await Assert.That(state.Shown).IsNotNull();
        await Assert.That(Raw("Stop")).IsEqualTo("");
    }

    /// <summary>The session total "9 -> 11" only sums what the edits showed, so the Stop is silent.</summary>
    [Test]
    public async Task Two_edits_worsening_the_same_finding_leave_the_Stop_silent()
    {
        var file = Commit("src/A.cs", Class("A", Method(9)));
        File.WriteAllText(file, Class("A", Method(10)));
        await Assert.That(FindingLines(string.Join("\n", Edit(file)))).HasSingleItem(l => l.StartsWith("worse ", StringComparison.Ordinal) && l.Contains("parameters 9 -> 10 (limit", StringComparison.Ordinal));
        File.WriteAllText(file, Class("A", Method(11)));
        await Assert.That(FindingLines(string.Join("\n", Edit(file)))).HasSingleItem(l => l.StartsWith("worse ", StringComparison.Ordinal) && l.Contains("parameters 10 -> 11 (limit", StringComparison.Ordinal));
        await Assert.That(Raw("Stop")).IsEqualTo("");
    }

    [Test]
    public async Task A_line_the_budget_cut_off_is_shown_at_Stop()
    {
        var file = Commit("src/B.cs", Class("B"));
        File.WriteAllText(file, Class("B", [.. Enumerable.Range(0, 4).Select(i => Method(9, i))]));
        var shown = Edit(file, "--max-lines", "1");
        await Assert.That(shown[0]).StartsWith("Skarp: src/B.cs health ");
        await Assert.That(await Assert.That(FindingLines(Text(shown))).HasSingleItem()).StartsWith("new ").And.Contains("B.M0 too-many-parameters: parameters 9 ");
        await Assert.That(shown).Contains("+3 more: skarp show --session rep --new-only");
        var stop = FindingLines(Text(Stop()));
        await Assert.That(stop.Count).IsEqualTo(3);
        await Assert.That(HealthOnly(Text(Stop()))).IsEmpty();                         // no health: the same score
        foreach (var (line, i) in stop.Select((l, i) => (l, i + 1))) await Assert.That(line).StartsWith("new ").And.Contains($"B.M{i} too-many-parameters: parameters 9 ");
    }

    [Test]
    public async Task A_change_outside_the_edit_tools_is_shown()
    {
        var file = Commit("src/C.cs", Class("C", Method(9)));
        File.WriteAllText(file, Class("C", Method(10)));
        Edit(file);
        File.WriteAllText(file, Class("C", Method(10), Method(9, 1)));                // not through an edit tool
        var stop = Stop();
        await Assert.That(await Assert.That(HealthOnly(Text(stop))).HasSingleItem()).IsEqualTo("src/C.cs health 9.7 -> 9.5");
        await Assert.That(await Assert.That(FindingLines(string.Join("\n", stop))).HasSingleItem()).StartsWith("new ").And.Contains("C.M1 too-many-parameters: parameters 9 ");
    }

    [Test]
    public async Task A_health_line_is_shown_again_with_a_new_score()
    {
        var file = Commit("src/H.cs", Class("H", Method(9)));
        File.WriteAllText(file, Class("H", Method(9), Method(5, 1)));                 // a notice: no message
        await Assert.That(Raw("PostToolUse", file)).IsEqualTo("");
        await Assert.That(Raw("Stop")).IsEqualTo("");
        File.WriteAllText(file, Class("H", Method(9), Method(5, 1), Method(5, 2)));
        await Assert.That(Raw("PostToolUse", file)).IsEqualTo("");
        File.WriteAllText(file, Class("H", Method(9), Method(5, 1), Method(5, 2), Method(9, 3)));   // outside the edit tools
        var stop = Stop();
        await Assert.That(await Assert.That(HealthOnly(Text(stop))).HasSingleItem()).IsEqualTo("src/H.cs health 9.7 -> 9.4");
        await Assert.That(await Assert.That(FindingLines(string.Join("\n", stop))).HasSingleItem()).StartsWith("new ").And.Contains("H.M3 too-many-parameters: parameters 9 ");
    }

    [Test]
    public async Task Notices_count_as_shown_only_when_printed()
    {
        var file = Commit("src/N.cs", Class("N", Method(2)));
        File.WriteAllText(file, Class("N", Method(5)));
        await Assert.That(FindingLines(string.Join("\n", Edit(file)))).IsEmpty();             // a notice, not printed by default
        var stop = Stop("--include-notices");
        await Assert.That(await Assert.That(FindingLines(Text(stop))).HasSingleItem()).StartsWith("new ").And.Contains("N.M0 too-many-parameters (notice): parameters 5 ");

        // Printed with notices on: not repeated.
        file = Commit("src/N2.cs", Class("N2", Method(2)));
        File.WriteAllText(file, Class("N2", Method(5)));
        await Assert.That(FindingLines(string.Join("\n", Edit(file, "--include-notices")))).HasSingleItem(l => l.StartsWith("new ", StringComparison.Ordinal) && l.Contains("parameters 5 (limit", StringComparison.Ordinal));
        await Assert.That(Raw("Stop", null, null, "--include-notices")).IsEqualTo("");
    }

    [Test]
    public async Task Lines_shown_to_two_agents_are_not_repeated()
    {
        var a = Commit("src/A.cs", Class("A", Method(9)));
        var b = Commit("src/B.cs", Class("B", Method(9)));
        File.WriteAllText(a, Class("A", Method(10)));
        await Assert.That(Context(Raw("PostToolUse", a, "agent-1"))).IsNotEmpty();
        File.WriteAllText(b, Class("B", Method(10)));
        await Assert.That(Context(Raw("PostToolUse", b, "agent-2"))).IsNotEmpty();
        await Assert.That(Raw("Stop")).IsEqualTo("");
    }

    [Test]
    public async Task With_the_gate_a_blocking_line_is_listed_at_every_Stop()
    {
        var file = Commit("src/G.cs", Class("G", Method(9)));
        File.WriteAllText(file, Class("G", Method(10)));
        using (var doc = JsonDocument.Parse(Raw("PostToolUse", file, null, "--gate")))
        {
            await Assert.That(doc.RootElement.GetProperty("decision").GetString()).IsEqualTo("block");
            var reason = doc.RootElement.GetProperty("reason").GetString()!;
            await Assert.That(reason).StartsWith("Skarp: this edit added 1 problem that blocks the end of the turn.\nsrc/G.cs");
            await Assert.That(await Assert.That(FindingLines(reason)).HasSingleItem()).StartsWith("worse ");
        }
        // The Stop lists the line the edit check already blocked on, and blocks again.
        using (var doc = JsonDocument.Parse(Raw("Stop", null, null, "--gate")))
        {
            await Assert.That(doc.RootElement.GetProperty("decision").GetString()).IsEqualTo("block");
            var reason = doc.RootElement.GetProperty("reason").GetString()!;
            await Assert.That(reason).StartsWith("Skarp did not end the turn: 1 problem is new or worse in files this session edited.");
            await Assert.That(await Assert.That(WithPaths(reason)).HasSingleItem()).StartsWith("src/G.cs: worse ");
        }
        await Assert.That(Raw("Stop", null, null, "--gate")).IsEqualTo("");                    // nothing changed since that Stop

        // Two edits: the Stop blocks with the session total.
        File.WriteAllText(file, Class("G", Method(11)));
        Raw("PostToolUse", file, null, "--gate");
        File.WriteAllText(file, Class("G", Method(12)));
        Raw("PostToolUse", file, null, "--gate");
        using (var doc = JsonDocument.Parse(Raw("Stop", null, null, "--gate")))
            await Assert.That(doc.RootElement.GetProperty("reason").GetString()).Contains("  worse line 5 G.M0 too-many-parameters: parameters 9 -> 12 ");

        // Without the gate nothing blocks; the edit showed the value, so the Stop says nothing about it, and
        // speaks for a value that moved since.
        File.WriteAllText(file, Class("G", Method(13)));
        await Assert.That(await Assert.That(FindingLines(string.Join("\n", Edit(file)))).HasSingleItem()).StartsWith("worse ");
        await Assert.That(Raw("Stop")).IsEqualTo("");
        File.WriteAllText(file, Class("G", Method(14)));
        using (var doc = JsonDocument.Parse(Raw("Stop")))
        {
            await Assert.That(doc.RootElement.TryGetProperty("decision", out _)).IsFalse();
            var context = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
            await Assert.That(await Assert.That(FindingLines(context)).HasSingleItem()).StartsWith("worse line 5 G.M0 too-many-parameters: ");
        }
    }

    [Test]
    public async Task A_composite_follows_its_parts()
    {
        var file = Commit("src/Importer.cs", Class("Importer"));
        File.WriteAllText(file, BrainTests.Importer());
        await Assert.That(FindingLines(string.Join("\n", Edit(file, "--max-lines", "20")))).Contains(l => l.Contains(" brain-method: ", StringComparison.Ordinal));
        await Assert.That(Raw("Stop", null, null, "--max-lines", "20")).IsEqualTo("");
        File.WriteAllText(file, BrainTests.Importer(runBranches: 4));                  // a part moved, outside the edit tools
        // The composite's text holds its notice part's number (cognitive complexity 24 -> 27): not seen.
        await Assert.That(FindingLines(string.Join("\n", Stop("--max-lines", "20")))).Contains(l => l.Contains(" brain-method: ", StringComparison.Ordinal) && l.StartsWith("new ", StringComparison.Ordinal));
    }

    TrackingState State() => TrackingStore.Read(Directory.GetFiles(Path.Combine(_host.Home, "tracked"), "*-rep.json").Single())!;
}
