using System.Text.Json;
using SkarpSharp.Agent;
using SkarpSharp.Tests.History;
using static SkarpSharp.Tests.Agent.AgentHost;

namespace SkarpSharp.Tests.Agent;

/// <summary>
/// The Claude hook's behaviour. The About line's rhythm (an agent's
/// first printed message, the first after a <c>PostCompact</c>, every 20 printed messages, never at budget 1), the
/// silent Stop when everything was seen, the health-only rule, and the Stop's starting point at git <c>HEAD</c> (the
/// false "10.0 -&gt; 7.7" and "new file" after a long session). Git is isolated from the machine's
/// configuration; the tracking home is private.
/// </summary>
public sealed class HookRhythmTests : IDisposable
{
    readonly AgentHost _host = new();
    readonly SyntheticRepo _repo = new();
    static readonly DateTimeOffset T0 = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    public HookRhythmTests()
    {
        _host.Now = T0;
        _host.GitEnvironment = _repo.Environment;
    }

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

    (int Exit, string Out) Hook(string ev, string? file = null, string? agent = null, params string[] extra)
    {
        var r = _host.RunWithInput(_repo.Root, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = "rhythm", ["hook_event_name"] = ev, ["tool_name"] = "Edit", ["stop_hook_active"] = false, ["agent_id"] = agent,
            ["tool_input"] = new Dictionary<string, string?> { ["file_path"] = file },
        }), ["check", "--hook", "claude", .. extra]);
        return (r.Exit, r.Out);
    }

    /// <summary>The lines a hook answered with (additional context or block reason); empty when it printed nothing.</summary>
    static List<string> Context(string raw)
    {
        if (raw.Length == 0) return [];
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        return Lines(root.TryGetProperty("reason", out var reason) ? reason.GetString()! : root.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!);
    }

    List<string> Edit(string file, string? agent = null, params string[] extra) => Context(Hook("PostToolUse", file, agent, extra).Out);

    List<string> Stop(params string[] extra) => Context(Hook("Stop", null, null, extra).Out);

    static bool HasAbout(List<string> lines) => lines.Contains(CheckText.AboutSkarp);

    TrackingState State() => TrackingStore.Read(Directory.GetFiles(Path.Combine(_host.Home, "tracked"), "*-rhythm.json").Single())!;

    // ---- the About line ----------------------------------------------------------------------------------------------

    [Test]
    public async Task The_first_message_has_the_About_line_and_the_second_not()
    {
        var file = Commit("src/A.cs", Class("A", Method(9)));
        File.WriteAllText(file, Class("A", Method(10)));
        var first = Edit(file);
        await Assert.That(first[^1]).IsEqualTo(CheckText.AboutSkarp);
        File.WriteAllText(file, Class("A", Method(11)));
        var second = Edit(file);
        await Assert.That(FindingLines(string.Join("\n", second))).HasSingleItem();
        await Assert.That(HasAbout(second)).IsFalse();
        // A silent check does not count as a printed message.
        File.WriteAllText(file, Class("A", Method(11)) + "// comment\n");
        await Assert.That(Edit(file)).IsEmpty();
        await Assert.That(State().Agents["main"].SinceAbout).IsEqualTo(2);
    }

    [Test]
    public async Task After_a_compaction_the_next_message_has_the_About_line_again()
    {
        var file = Commit("src/A.cs", Class("A", Method(9)));
        File.WriteAllText(file, Class("A", Method(10)));
        await Assert.That(HasAbout(Edit(file))).IsTrue();
        File.WriteAllText(file, Class("A", Method(11)));
        await Assert.That(HasAbout(Edit(file))).IsFalse();

        var compact = Hook("PostCompact");
        await Assert.That(compact.Exit).IsEqualTo(0);
        await Assert.That(compact.Out).IsEqualTo("");
        await Assert.That(State().Compactions).IsEqualTo(1);

        File.WriteAllText(file, Class("A", Method(12)));
        await Assert.That(HasAbout(Edit(file))).IsTrue();
        File.WriteAllText(file, Class("A", Method(13)));
        await Assert.That(HasAbout(Edit(file))).IsFalse();
        // One compaction re-arms every agent of the session, also one that printed nothing before it.
        var b = Commit("src/B.cs", Class("B", Method(9)));
        File.WriteAllText(b, Class("B", Method(10)));
        await Assert.That(HasAbout(Edit(b, "agent-1"))).IsTrue();
    }

    [Test]
    public async Task The_21st_printed_message_has_the_About_line()
    {
        var file = Commit("src/A.cs", Class("A", Method(9)));
        var withAbout = new List<int>();
        for (int i = 1; i <= 22; i++)
        {
            File.WriteAllText(file, Class("A", Method(i % 2 == 1 ? 10 : 11)));    // each edit moves the finding: printed
            var lines = Edit(file);
            await Assert.That(FindingLines(string.Join("\n", lines))).HasSingleItem();
            if (HasAbout(lines)) withAbout.Add(i);
        }
        await Assert.That(withAbout).IsEquivalentTo([1, 21]);
    }

    [Test]
    public async Task A_subagent_has_its_own_first_message()
    {
        var file = Commit("src/A.cs", Class("A", Method(9)));
        File.WriteAllText(file, Class("A", Method(10)));
        await Assert.That(HasAbout(Edit(file))).IsTrue();
        File.WriteAllText(file, Class("A", Method(11)));
        await Assert.That(HasAbout(Edit(file, "agent-1"))).IsTrue();
        File.WriteAllText(file, Class("A", Method(12)));
        await Assert.That(HasAbout(Edit(file, "agent-1"))).IsFalse();
        File.WriteAllText(file, Class("A", Method(13)));
        await Assert.That(HasAbout(Edit(file))).IsFalse();
    }

    [Test]
    public async Task A_budget_of_one_line_never_has_the_About_line()
    {
        var file = Commit("src/A.cs", Class("A", Method(9)));
        File.WriteAllText(file, Class("A", Method(10)));
        var lines = Edit(file, null, "--max-lines", "1");
        await Assert.That(FindingLines(string.Join("\n", lines))).HasSingleItem();
        await Assert.That(HasAbout(lines)).IsFalse();
        // The line stays due for the first message with room for it.
        File.WriteAllText(file, Class("A", Method(11)));
        await Assert.That(HasAbout(Edit(file))).IsTrue();
    }

    [Test]
    public async Task Other_events_and_a_PostCompact_without_a_session_print_nothing()
    {
        var r = _host.RunWithInput(_repo.Root, """{"hook_event_name":"PostCompact","trigger":"manual","compact_summary":"..."}""", "check", "--hook", "claude");
        await Assert.That(r.Exit).IsEqualTo(0);
        await Assert.That(r.Out + r.Err).IsEqualTo("");
        await Assert.That(Directory.GetFiles(Path.Combine(_host.Home, "tracked"), "*-nosession.json")).HasSingleItem();
    }

    // ---- the Stop ----------------------------------------------------------------------------------------------------

    [Test]
    public async Task A_Stop_that_only_sums_what_was_shown_is_silent()
    {
        var file = Commit("src/A.cs", Class("A", Method(9)));
        File.WriteAllText(file, Class("A", Method(10)));
        Edit(file);
        File.WriteAllText(file, Class("A", Method(11)));
        Edit(file);
        await Assert.That(Hook("Stop").Out).IsEqualTo("");                       // "9 -> 11" sums "9 -> 10" and "10 -> 11"
    }

    [Test]
    public async Task One_unseen_finding_is_the_only_line_of_the_Stop()
    {
        var file = Commit("src/A.cs", Class("A", Method(9)));
        File.WriteAllText(file, Class("A", Method(10)));
        Edit(file);
        File.WriteAllText(file, Class("A", Method(10), Method(9, 1)));             // a shell command, no edit check
        var stop = Stop();
        await Assert.That(stop[0]).IsEqualTo("Skarp, end of turn: not shown after the edits (compared with the start of the session)");
        await Assert.That(await Assert.That(FindingLines(string.Join("\n", stop))).HasSingleItem()).StartsWith("new line 6 A.M1 too-many-parameters: ");
        // That Stop's line now counts as seen too.
        File.WriteAllText(file, Class("A", Method(10), Method(9, 1)) + "// comment\n");
        await Assert.That(Hook("Stop").Out).IsEqualTo("");
    }

    [Test]
    public async Task A_value_that_moved_after_it_was_shown_is_printed()
    {
        var file = Commit("src/A.cs", Class("A", Method(9)));
        File.WriteAllText(file, Class("A", Method(10)));
        Edit(file);
        File.WriteAllText(file, Class("A", Method(12)));
        var stop = FindingLines(string.Join("\n", Stop()));
        await Assert.That(await Assert.That(stop).HasSingleItem()).StartsWith("worse line 5 A.M0 too-many-parameters: parameters 9 -> 12 ");
    }

    [Test]
    public async Task A_blocking_Stop_lists_every_blocking_finding_seen_or_not()
    {
        var file = Commit("src/A.cs", Class("A", Method(9)));
        File.WriteAllText(file, Class("A", Method(10)));
        Edit(file, null, "--gate");
        File.WriteAllText(file, Class("A", Method(10), Method(9, 1)));
        Edit(file, null, "--gate");
        var raw = Hook("Stop", null, null, "--gate").Out;
        using var doc = JsonDocument.Parse(raw);
        await Assert.That(doc.RootElement.GetProperty("decision").GetString()).IsEqualTo("block");
        var reason = doc.RootElement.GetProperty("reason").GetString()!;
        await Assert.That(reason).StartsWith("Skarp did not end the turn: 2 problems are new or worse in files this session edited.");
        await Assert.That(FindingLines(reason).Count).IsEqualTo(2);
        await Assert.That(reason).Contains(CheckText.StopWayOut);
    }

    // ---- health-only messages ------------------------------------------------------------------------------------------

    [Test]
    public async Task The_health_only_rule()
    {
        await Assert.That(HealthNote.Due(9.0, 8.8, false, false)).IsFalse();         // 0.2
        await Assert.That(HealthNote.Due(9.0, 8.7, false, false)).IsFalse();         // a first 0.3 drop
        await Assert.That(HealthNote.Due(9.0, 8.4, false, false)).IsTrue();          // a second: 0.6 since last shown
        await Assert.That(HealthNote.Due(9.0, 8.5, false, false)).IsTrue();          // exactly 0.5
        await Assert.That(HealthNote.Due(9.6, 9.7, false, true)).IsTrue();           // a fix
        await Assert.That(HealthNote.Due(9.6, 9.7, false, false)).IsFalse();
        await Assert.That(HealthNote.Due(9.0, 7.0, true, false)).IsFalse();          // a notice-only drop, however large
        await Assert.That(HealthNote.Due(7.0, 9.0, true, false)).IsTrue();
    }

    [Test]
    public async Task A_drop_from_notices_alone_is_silent_at_the_edit_and_at_the_Stop()
    {
        var file = Commit("src/H.cs", Class("H", Method(9)));
        File.WriteAllText(file, Class("H", Method(9), Method(5, 1)));                // a notice the hook does not list
        await Assert.That(Hook("PostToolUse", file).Out).IsEqualTo("");
        await Assert.That(Hook("Stop").Out).IsEqualTo("");
        // With notices listed, the notice is a finding line, under the health heading.
        File.WriteAllText(file, Class("H", Method(9), Method(5, 1), Method(5, 2)));
        var lines = Edit(file, null, "--include-notices");
        await Assert.That(lines[0]).IsEqualTo("Skarp: src/H.cs health 9.6 -> 9.5");
        await Assert.That(FindingLines(string.Join("\n", lines))).HasSingleItem();
    }

    [Test]
    public async Task A_rise_from_a_fixed_finding_is_printed()
    {
        var file = Commit("src/H.cs", Class("H", Method(9), Method(5, 1)));
        File.WriteAllText(file, Class("H", Method(9)));                              // the unlisted notice is fixed
        await Assert.That(Edit(file)).IsEquivalentTo(["Skarp: src/H.cs health 9.6 -> 9.7", CheckText.AboutSkarp]);
    }

    // ---- the Stop's starting point -------------------------------------------------------------------

    /// <summary>
    /// The case of a session that ran for days. One agent first checked <c>F.cs</c> while it was clean at HEAD
    /// (start 10.0), another created <c>G.cs</c> (start: not in git). Both were committed later with their findings. A
    /// subagent then changed them without an edit check (shell edits), so no fresh start was recorded: the Stop used
    /// the old starts and said <c>F.cs health 10.0 -&gt; 9.7</c> and <c>G.cs ... (new file)</c>, with the committed
    /// findings as new. The file at HEAD is a starting point too, so nothing is new.
    /// </summary>
    [Test]
    public async Task A_file_committed_since_its_first_check_is_compared_with_HEAD()
    {
        var f = Commit("src/F.cs", Class("F", Method(2)));
        File.WriteAllText(f, Class("F", Method(3)));
        Edit(f, "agent-old");
        var g = Path.Combine(_repo.Root, "src", "G.cs");
        File.WriteAllText(g, Class("G", Method(9)));
        Edit(g, "agent-new-file");
        await Assert.That(State().Agents["agent-new-file"].Files.Single().Value.ComparedWith).IsEqualTo("untracked");
        Stop();

        // Days later: both committed with their findings.
        _host.Now = T0.AddDays(2);
        File.WriteAllText(f, Class("F", Method(9)));
        _repo.Commit("work", T0.AddDays(1));
        // A subagent changes both through the shell (no edit check); the scores stay.
        File.WriteAllText(f, Class("F", Method(9)) + "// changed\n");
        File.WriteAllText(g, Class("G", Method(9)) + "// changed\n");
        await Assert.That(Hook("Stop").Out).IsEqualTo("");

        // A finding that is new against HEAD is still reported, under a heading that compares with HEAD.
        File.WriteAllText(f, Class("F", Method(9), Method(9, 1)));
        var stop = Stop();
        await Assert.That(Headings(string.Join("\n", stop))).IsEquivalentTo(["src/F.cs health 9.7 -> 9.5"]);
        await Assert.That(await Assert.That(FindingLines(string.Join("\n", stop))).HasSingleItem()).StartsWith("new line 6 F.M1 too-many-parameters: ");
    }

    [Test]
    public async Task A_file_with_no_known_start_shows_its_score_without_an_arrow()
    {
        var folder = _host.Folder("outside");
        var file = Path.Combine(folder, "O.cs");
        File.WriteAllText(file, Class("O", Method(2)));
        var r = _host.RunWithInput(folder, Input("PostToolUse", file), "check", "--hook", "claude");
        await Assert.That(r.Out).IsEqualTo("");
        File.WriteAllText(file, Class("O", Method(9)));                              // outside git: no earlier score is known
        var stop = string.Join("\n", Context(_host.RunWithInput(folder, Input("Stop", null), "check", "--hook", "claude").Out));
        await Assert.That(Headings(stop)).IsEquivalentTo(["O.cs health 9.7"]);
        await Assert.That(await Assert.That(FindingLines(stop)).HasSingleItem()).StartsWith("new line 5 O.M0 too-many-parameters: ");
    }

    static string Input(string ev, string? file) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["session_id"] = "rhythm", ["hook_event_name"] = ev, ["tool_name"] = "Edit", ["stop_hook_active"] = false,
        ["tool_input"] = new Dictionary<string, string?> { ["file_path"] = file },
    });
}
