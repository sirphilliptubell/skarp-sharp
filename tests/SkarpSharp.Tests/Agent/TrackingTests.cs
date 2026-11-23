using System.Text.Json;
using SkarpSharp.Agent;
using SkarpSharp.Tests.History;
using static SkarpSharp.Tests.Agent.AgentHost;

namespace SkarpSharp.Tests.Agent;

/// <summary>
/// Tracking: <c>skarp check --track</c>
/// compares the first edit with git HEAD, later edits with the session's record; worse, better, resolved once,
/// re-crossing, a file not in git, outside git, lookback across days, two agents, two sessions, the 30-day cleanup and
/// concurrent writers.
/// </summary>
public sealed class TrackingTests : IDisposable
{
    readonly AgentHost _host = new();
    readonly SyntheticRepo _repo = new();
    static readonly DateTimeOffset T0 = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    public TrackingTests() => _host.Now = T0;

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

    // Returns a value, so it checks synchronously (Assert.Fail throws) and its callers stay unchanged.
    List<string> Check(string file, params string[] extra)
    {
        var r = _host.Run(_repo.Root, ["check", file, "--track", "--session", "s1", .. extra]);
        if (r.Exit != 0) Assert.Fail(r.Err);
        return FindingLines(r.Out);   // the finding lines (health lines are tested in HealthLoopTests)
    }

    [Test]
    public async Task First_edit_against_HEAD_then_worse_better_resolved_once_and_crossing_again()
    {
        var file = Commit("src/A.cs", Class("A", Method(9)));
        await Assert.That(Check(file)).IsEmpty();                                   // first check, unchanged against HEAD: silent

        File.WriteAllText(file, Class("A", Method(10)));
        var worse = await Assert.That(Check(file)).HasSingleItem();
        await Assert.That(worse).Matches(@"^worse line \d+ A\.M0 too-many-parameters: parameters 9 -> 10 \(limit at least 8\) \[[0-9a-f]{8}\]$");

        File.WriteAllText(file, Class("A", Method(11)));
        await Assert.That(await Assert.That(Check(file)).HasSingleItem()).StartsWith("worse ").And.Contains("parameters 10 -> 11");
        File.WriteAllText(file, Class("A", Method(10)));
        var better = await Assert.That(Check(file)).HasSingleItem();                     // better, still over the limit
        await Assert.That(better).Matches(@"^better line \d+ A\.M0 too-many-parameters: parameters 11 -> 10 \(limit at least 8\) \[[0-9a-f]{8}\]$");
        await Assert.That(Check(file)).IsEmpty();                                   // unchanged: nothing

        File.WriteAllText(file, Class("A", Method(2)));
        await Assert.That(await Assert.That(Check(file)).HasSingleItem()).Matches(@"^fixed line \d+ A\.M0 too-many-parameters: parameters 10, now within the limit \[[0-9a-f]{8}\]$");
        await Assert.That(Check(file)).IsEmpty();                                   // resolved once
        File.WriteAllText(file, Class("A", Method(3)));
        await Assert.That(Check(file)).IsEmpty();                                   // still under: quiet

        File.WriteAllText(file, Class("A", Method(9)));
        await Assert.That(await Assert.That(Check(file)).HasSingleItem()).StartsWith("new ").And.Contains("parameters 9 (limit");  // crossing again is new

        var state = State("s1");
        var entry = state.Agents["main"].Files[TrackingStore.Key(file)];
        await Assert.That(entry.ComparedWith).IsEqualTo("head");
        await Assert.That((entry.Reported, entry.ResolvedCount)).IsEqualTo((5, 1));
    }

    [Test]
    public async Task A_deleted_member_is_reported_fixed_without_a_line()
    {
        var file = Commit("src/D.cs", Class("D", Method(10, 0), Method(2, 1)));
        File.WriteAllText(file, Class("D", Method(2, 1)));
        var line = await Assert.That(Check(file)).HasSingleItem();
        await Assert.That(line).Matches(@"^fixed D\.M0 too-many-parameters: method removed \[[0-9a-f]{8}\]$");
    }

    /// <summary>A brain method that goes away is reported fixed under brain-method, the rule its
    /// line was shown with, once and without a line number when the member was deleted; not as its parts.</summary>
    [Test]
    public async Task A_fixed_composite_is_reported_under_its_own_rule()
    {
        var file = Commit("src/Importer.cs", SkarpSharp.Tests.Findings.BrainTests.Importer());
        await Assert.That(Check(file, "--max-lines", "20")).IsEmpty();                       // as at HEAD: silent
        File.WriteAllText(file, Class("Importer"));                                            // Run (the brain method) deleted
        var lines = Check(file, "--max-lines", "20", "--include-notices");
        await Assert.That(lines).Contains(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^fixed Importer\.Run brain-method: method removed \[[0-9a-f]{8}\]$"));
        await Assert.That(lines).DoesNotContain(l => l.Contains("Importer.Run long-method", StringComparison.Ordinal) || l.Contains("Importer.Run deep-nesting", StringComparison.Ordinal));
        await Assert.That(lines).DoesNotContain(l => l.Contains("Importer.Run brain-method", StringComparison.Ordinal) && l.Contains(" line ", StringComparison.Ordinal));
        // Every finding line ends with its id.
        foreach (var l in lines) await Assert.That(l).Matches(@" \[[0-9a-f]{8}\]$");
    }

    [Test]
    public async Task Better_ranks_after_worse_and_new_in_the_budget()
    {
        var file = Commit("src/R.cs", Class("R", Method(10, 0), Method(10, 1)));
        File.WriteAllText(file, Class("R", Method(9, 0), Method(11, 1), Method(9, 2)));
        var lines = Check(file);
        await Assert.That(lines.Count).IsEqualTo(3);                                // printed by line
        await Assert.That(lines[0]).StartsWith("better ").And.Contains("R.M0 too-many-parameters: parameters 10 -> 9");
        await Assert.That(lines[1]).StartsWith("worse ").And.Contains("R.M1 too-many-parameters: parameters 10 -> 11");
        await Assert.That(lines[2]).StartsWith("new ").And.Contains("R.M2 too-many-parameters: parameters 9 ");
        // With a budget of 2 finding lines (the heading is free) the better line is the one folded.
        File.WriteAllText(file, Class("R", Method(8, 0), Method(12, 1), Method(9, 2), Method(9, 3)));
        var output = _host.Run(_repo.Root, "check", file, "--track", "--session", "s1", "--max-lines", "2").Out;
        lines = Lines(output);
        await Assert.That(lines[0]).IsEqualTo("Skarp: src/R.cs health 9.3 -> 9.2 (10 = healthy)");
        await Assert.That(FindingLines(output).Count).IsEqualTo(2);
        await Assert.That(FindingLines(output)[0]).StartsWith("worse ").And.Contains("R.M1 too-many-parameters: parameters 11 -> 12");
        await Assert.That(FindingLines(output)[1]).StartsWith("new ").And.Contains("R.M3 ");
        // With a session, the fold line names skarp show --session (short id) instead of files.
        await Assert.That(lines).Contains("+1 more: skarp show --session s1 --new-only");
    }

    /// <summary>The Stop hook compares each changed tracked file with its session-start state.</summary>
    [Test]
    public async Task Stop_reports_only_what_got_worse_or_new_during_the_session()
    {
        var file = Commit("src/S.cs", Class("S", Method(9, 0), Method(9, 1)));   // old debt at HEAD
        string Hook(string ev) => _host.RunWithInput(_repo.Root, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = "st", ["hook_event_name"] = ev, ["tool_name"] = "Edit", ["stop_hook_active"] = false,
            ["tool_input"] = new Dictionary<string, string?> { ["file_path"] = file },
        }), "check", "--hook", "claude").Out;

        File.AppendAllText(file, "// touched, no change in findings" + Environment.NewLine);
        await Assert.That(Hook("PostToolUse")).IsEqualTo("");
        await Assert.That(Hook("Stop")).IsEqualTo("");                                       // old debt, no change: silent

        File.WriteAllText(file, Class("S", Method(10, 0), Method(9, 1), Method(9, 2)));
        // Changed outside the edit tools (an edit check's lines are not repeated by the Stop summary).
        File.WriteAllText(file, Class("S", Method(10, 0), Method(9, 1), Method(9, 2)) + Environment.NewLine);
        using var doc = JsonDocument.Parse(Hook("Stop"));
        var lines = FindingLines(doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!);
        await Assert.That(lines.Count).IsEqualTo(2);
        await Assert.That(lines[0]).StartsWith("worse ").And.Contains("S.M0 too-many-parameters: parameters 9 -> 10");   // against HEAD, the session start
        await Assert.That(lines[1]).StartsWith("new ").And.Contains("S.M2 too-many-parameters: parameters 9 ");
        await Assert.That(lines).DoesNotContain(l => l.Contains("S.M1", StringComparison.Ordinal));
        await Assert.That(Hook("Stop")).IsEqualTo("");                                       // nothing changed since the last Stop

        // A baseline holding the finding at that value makes it known, not new (it only seeds "known"). M0 moved
        // after that Stop showed it, so it is printed again.
        File.WriteAllText(Path.Combine(_repo.Root, ".skarp-baseline.json"),
            "{ \"version\": 1, \"type\": \"baseline\", \"files\": { \"src/S.cs\": [ { \"rule\": \"too-many-parameters\", \"place\": \"Demo.S::M2\", \"value\": 9 } ] } }");
        File.WriteAllText(file, Class("S", Method(11, 0), Method(9, 1), Method(9, 2)) + "// again" + Environment.NewLine);
        using var doc2 = JsonDocument.Parse(Hook("Stop"));
        await Assert.That(await Assert.That(FindingLines(doc2.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!)).HasSingleItem()).Contains("S.M0 too-many-parameters: parameters 9 -> 11");
    }

    /// <summary>
    /// The Stop hook reports a duplicate-code group with a copy in a changed file when it is new
    /// against the baseline (the whole root is matched), with the hint line once; the per-edit check never does.
    /// </summary>
    [Test]
    public async Task Stop_reports_a_new_clone_group_with_the_hint()
    {
        var code = SkarpSharp.Tests.Clones.CloneHost.Method("A", "Run", 50);
        Commit("src/A.cs", code);
        await Assert.That(_host.Run(_repo.Root, "baseline", _repo.Root, "--no-history", "--no-semantic").Exit).IsEqualTo(0);
        var copy = Path.Combine(_repo.Root, "src", "B.cs");
        string Hook(string ev) => _host.RunWithInput(_repo.Root, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = "clones", ["hook_event_name"] = ev, ["tool_name"] = "Write", ["stop_hook_active"] = false,
            ["tool_input"] = new Dictionary<string, string?> { ["file_path"] = copy },
        }), "check", "--hook", "claude").Out;

        File.WriteAllText(copy, code.Replace("class A", "class B", StringComparison.Ordinal));
        await Assert.That(Hook("PostToolUse")).DoesNotContain("duplicate-code").And.Contains(CheckText.AboutSkarp);
        // The note follows the About line's rhythm, which the edit's message used; a compaction makes it due again.
        _host.RunWithInput(_repo.Root, """{"session_id":"clones","hook_event_name":"PostCompact"}""", "check", "--hook", "claude");
        using var doc = JsonDocument.Parse(Hook("Stop"));
        var text = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
        var line = await Assert.That(WithPaths(text)).HasSingleItem(l => l.Contains(" duplicate-code: ", StringComparison.Ordinal));
        await Assert.That(line).StartsWith("src/A.cs: new line 2 ");
        await Assert.That(line).Contains("2 copies of");
        await Assert.That(line).Matches(@"\[[0-9a-f]{8}\]$");
        // The hook's note names no way around the finding, and follows the About line's rhythm.
        await Assert.That(Lines(text)).HasSingleItem(l => l == SkarpSharp.Findings.DuplicationText.ClaudeHint);
    }

    /// <summary>Metrics in the human report's words in the agent text, ids only in JSON.</summary>
    [Test]
    public async Task Agent_text_names_metrics_in_words()
    {
        Commit("src/Other.cs", Class("Other"));
        _repo.Write("src/W.cs", """
            namespace Demo;

            public class W
            {
                public void Deep(int a)
                {
                    if (a > 1) { if (a > 2) { if (a > 3) { if (a > 4) { a++; } } } }
                }
            }

            """);
        var line = await Assert.That(Check(Path.Combine(_repo.Root, "src/W.cs"), "--include-notices").Where(l => l.Contains(" deep-nesting ", StringComparison.Ordinal))).HasSingleItem();
        await Assert.That(line).StartsWith("new ").And.Contains("W.Deep deep-nesting (notice): nesting depth 4 (limit 3)");
        await Assert.That(line).DoesNotContain(" nesting 4");
    }

    TrackingState State(string session)
    {
        var path = Directory.GetFiles(Path.Combine(_host.Home, "tracked"), "*-" + session + ".json").Single();
        return TrackingStore.Read(path)!;
    }

    [Test]
    public async Task A_file_not_in_git_reports_its_warnings_as_new_and_notices_only_on_request()
    {
        Commit("src/Other.cs", Class("Other"));
        _repo.Write("src/New.cs", Class("N", Method(9, 0), Method(5, 1)));
        var file = Path.Combine(_repo.Root, "src/New.cs");
        await Assert.That(await Assert.That(Check(file)).HasSingleItem()).StartsWith("new ").And.Contains("parameters 9 (limit");
        await Assert.That(State("s1").Agents["main"].Files[TrackingStore.Key(file)].ComparedWith).IsEqualTo("untracked");
        // The notice was tracked: on the next edit it is not new any more, even when asked for notices.
        File.WriteAllText(file, Class("N", Method(9, 0), Method(5, 1)) + "\n");
        await Assert.That(Check(file, "--include-notices")).IsEmpty();
    }

    [Test]
    public async Task Outside_git_every_warning_is_new_without_a_health_line()
    {
        var dir = _host.Folder("plain");
        var file = Path.Combine(dir, "P.cs");
        File.WriteAllText(file, Class("P", Method(8, 0), Method(9, 1), Method(5, 2)));
        var r = _host.Run(dir, "check", file, "--track");
        await Assert.That(2 == FindingLines(r.Out).Count).IsTrue().Because(r.Out + r.Err);
        foreach (var l in FindingLines(r.Out)) await Assert.That(l).StartsWith("new line ");
        await Assert.That(Headings(r.Out)).IsEquivalentTo(["P.cs"]);
        // No earlier score and not new to git, so no health line (it said "new" before).
        await Assert.That(HealthOnly(r.Out)).IsEmpty();
        await Assert.That(State(TrackingStore.NoSession).Agents["main"].Files[TrackingStore.Key(file)].ComparedWith).IsEqualTo("outside-git");
    }

    [Test]
    public async Task Lookback_finds_yesterdays_record_without_a_session()
    {
        var dir = _host.Folder("days");
        var file = Path.Combine(dir, "D.cs");
        File.WriteAllText(file, Class("D", Method(9)));
        await Assert.That(FindingLines(_host.Run(dir, "check", file, "--track").Out)).HasSingleItem();
        _host.Now = T0.AddDays(1);                                   // a new day: a new shared file
        await Assert.That(Lines(_host.Run(dir, "check", file, "--track").Out)).IsEmpty();
        await Assert.That(Directory.GetFiles(Path.Combine(_host.Home, "tracked"), "*-nosession.json").Length).IsEqualTo(2);

        // With the lookback off, the record of the day before is not used.
        File.WriteAllText(Path.Combine(dir, ".skarp.json"), """{ "version": 1, "tracking": { "lookbackDays": 0 } }""");
        _host.Now = T0.AddDays(2);
        await Assert.That(FindingLines(_host.Run(dir, "check", file, "--track").Out)).HasSingleItem();
    }

    [Test]
    public async Task A_session_resumed_on_a_later_day_keeps_its_first_file()
    {
        // Measured: claude --resume and --continue keep the session id; --fork-session gets a new one.
        var file = Commit("src/E.cs", Class("E", Method(9)));
        File.WriteAllText(file, Class("E", Method(10)));
        await Assert.That(Check(file)).HasSingleItem();
        _host.Now = T0.AddDays(3);
        File.WriteAllText(file, Class("E", Method(11)));
        await Assert.That(await Assert.That(Check(file)).HasSingleItem()).StartsWith("worse ").And.Contains("parameters 10 -> 11");
        await Assert.That(Directory.GetFiles(Path.Combine(_host.Home, "tracked"), "*-s1.json")).HasSingleItem();
    }

    [Test]
    public async Task Two_agents_and_two_sessions_keep_separate_records()
    {
        var file = Commit("src/B.cs", Class("B", Method(9)));
        File.WriteAllText(file, Class("B", Method(10)));
        await Assert.That(Check(file, "--agent", "a1")).HasSingleItem();
        await Assert.That(Check(file, "--agent", "a1")).IsEmpty();
        await Assert.That(Check(file, "--agent", "a2")).HasSingleItem();                 // another agent: its own first check against HEAD
        var r = _host.Run(_repo.Root, "check", file, "--track", "--session", "s2");
        await Assert.That(FindingLines(r.Out)).HasSingleItem();                              // another session: its own file
        await Assert.That(State("s1").Agents.Keys.Order(StringComparer.Ordinal)).IsEquivalentTo(["a1", "a2"], CollectionOrdering.Matching);
        await Assert.That(State("s2").Agents.Keys).IsEquivalentTo(["main"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Files_older_than_the_retention_are_deleted()
    {
        var tracked = Path.Combine(_host.Home, "tracked");
        Directory.CreateDirectory(tracked);
        var old = Path.Combine(tracked, "2026-10-01-old.json");
        var recent = Path.Combine(tracked, "2026-11-01-recent.json");
        foreach (var f in new[] { old, recent })
        {
            File.WriteAllText(f, "{}");
            File.SetLastWriteTimeUtc(f, DateTime.SpecifyKind(DateTime.ParseExact(Path.GetFileName(f)[..10], "yyyy-MM-dd", null), DateTimeKind.Utc));
        }
        var file = Commit("src/C.cs", Class("C"));
        Check(file);
        await Assert.That(File.Exists(old)).IsFalse();
        await Assert.That(File.Exists(recent)).IsTrue();
    }

    [Test]
    public async Task Concurrent_writers_lose_no_entries()
    {
        var files = Enumerable.Range(0, 8).Select(i => Commit($"src/K{i}.cs", Class($"K{i}", Method(9)))).ToList();
        Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            File.WriteAllText(files[i], Class($"K{i}", Method(10)));
            var r = _host.Run(_repo.Root, "check", files[i], "--track", "--session", "par", "--agent", $"agent{i}");
            // Inside Parallel.For the checks stay synchronous (Assert.Fail throws; Parallel.For rethrows).
            if (r.Exit != 0) Assert.Fail($"exit code {r.Exit}: {r.Err}");
            if (FindingLines(r.Out).Count != 1) Assert.Fail($"expected one finding line: {r.Out}");
        });
        var state = State("par");
        await Assert.That(state.Agents.Count).IsEqualTo(8);
        foreach (var a in state.Agents.Values) await Assert.That(a.Files).HasSingleItem();
    }

    [Test]
    public async Task The_state_file_has_the_documented_shape()
    {
        var file = Commit("src/E.cs", Class("E", Method(9)));
        File.WriteAllText(file, Class("E", Method(10)));
        Check(file);
        var path = Directory.GetFiles(Path.Combine(_host.Home, "tracked"), "*-s1.json").Single();
        await Assert.That(path).Matches(@"[\\/]2026-11-16-s1\.json$");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var entry = doc.RootElement.GetProperty("agents").GetProperty("main").GetProperty("files").EnumerateObject().Single().Value;
        foreach (var key in new[] { "size", "modified", "sha256", "comparedWith", "findings" }) await Assert.That(entry.TryGetProperty(key, out _)).IsTrue().Because(key);
        var finding = entry.GetProperty("findings").EnumerateObject().Single().Value;
        await Assert.That((finding.GetProperty("state").GetString(), finding.GetProperty("value").GetInt32())).IsEqualTo(("open", 10));
    }
}