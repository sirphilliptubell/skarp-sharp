using System.Text.Json;
using System.Text.RegularExpressions;
using SkarpSharp.Agent;
using SkarpSharp.Commands;
using SkarpSharp.Findings;
using SkarpSharp.Tests.History;
using static SkarpSharp.Tests.Agent.AgentHost;

namespace SkarpSharp.Tests.Agent;

/// <summary>
/// The saved run, <c>skarp show</c>, <c>skarp check</c> with and without a
/// baseline, the Claude Code hook protocol, and the rule that every command printed (a finding's id for
/// <c>skarp show &lt;id&gt;</c>, a <c>+N more</c> line) runs and returns what it promised, in the text format.
/// </summary>
public sealed class AgentLoopTests : IDisposable
{
    readonly AgentHost _host = new();

    public void Dispose() => _host.Dispose();

    /// <summary>A finding line with its file (<see cref="WithPaths"/>): <c>src/A.cs: new line 5 A.M0 too-many-parameters: ... [id]</c>.</summary>
    static readonly Regex FindingLine = new(@"^(?<path>\S+): (?<lead>new|worse|better|fixed|warning|notice|recorded) (line (?<line>\d+) )?(?<place>.+?) (?<rule>[a-z]+(-[a-z]+)*)( \(notice\))?: (?<text>.+) \[(?<handle>[0-9a-f]{8})\]$");

    /// <summary>A folder with a settings file (it is then the root) and the given files.</summary>
    string Project(string name, params (string Path, string Content)[] files)
    {
        var dir = _host.Folder(name);
        File.WriteAllText(Path.Combine(dir, ".skarp.json"), """{ "version": 1, "history": { "enabled": false }, "semantic": { "enabled": false } }""");
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return dir;
    }

    static string Many(string name, int warnings, int notices = 0) =>
        Class(name, [.. Enumerable.Range(0, warnings).Select(i => Method(9, i)), .. Enumerable.Range(warnings, notices).Select(i => Method(5, i))]);

    /// <summary>The ids at the end of the finding lines of a command's output.</summary>
    static List<string> Handles(string output) =>
        [.. WithPaths(output).Select(l => FindingLine.Match(l)).Where(m => m.Success).Select(m => m.Groups["handle"].Value)];

    /// <summary>The arguments of a printed <c>skarp ...</c> command (double quotes group words).</summary>
    // Returns a value, so it checks synchronously (Assert.Fail throws) and its callers stay unchanged.
    static string[] CommandArgs(string command)
    {
        if (!command.StartsWith("skarp ", StringComparison.Ordinal)) Assert.Fail($"not a skarp command: {command}");
        return [.. Regex.Matches(command["skarp ".Length..], "\"[^\"]*\"|\\S+").Select(m => m.Value.Trim('"'))];
    }

    /// <summary>
    /// Every command printed: each finding line's id resolves with <c>skarp show &lt;id&gt;</c> to that finding; each
    /// <c>+N more</c> command lists (following its own fold lines) every finding the budget left out.
    /// </summary>
    async Task AssertCommandsResolve(string cwd, string printed, string unfolded)
    {
        foreach (var line in WithPaths(printed))
        {
            var m = FindingLine.Match(line);
            await Assert.That(m.Success).IsTrue().Because(line);
            var handle = m.Groups["handle"].Value;
            var r = _host.Run(cwd, "show", handle, "--format", "json");
            await Assert.That(r.Exit == 0).IsTrue().Because($"skarp show {handle}: {r.Err}");
            using var doc = JsonDocument.Parse(r.Out);
            var f = (await Assert.That(doc.RootElement.GetProperty("findings").EnumerateArray()).HasSingleItem()).GetProperty("finding");
            await Assert.That(f.GetProperty("handle").GetString()).IsEqualTo(handle);
            await Assert.That(f.GetProperty("rule").GetString()).IsEqualTo(m.Groups["rule"].Value);
            await Assert.That(m.Groups["path"].Value).EndsWith(f.GetProperty("path").GetString()!);
            await Assert.That(handle).IsEqualTo(FindingEngine.HandleOf(f.GetProperty("id").GetString()!));
        }
        foreach (var line in Lines(printed).Where(l => l.StartsWith('+') && l.Contains(" more: ", StringComparison.Ordinal)))
        {
            var fold = Regex.Match(line, @"^\+(?<n>\d+) more: (?<cmd>skarp .+)$");
            await Assert.That(fold.Success).IsTrue().Because(line);
            var listed = new List<string>();
            string? command = fold.Groups["cmd"].Value;
            for (int guard = 0; guard < 10 && command is not null; guard++)
            {
                var r = _host.Run(cwd, CommandArgs(command));
                await Assert.That(r.Exit == 0).IsTrue().Because($"{command}: {r.Err}");
                listed.AddRange(Handles(r.Out));
                command = Lines(r.Out).Select(l => Regex.Match(l, @"^\+\d+ more: (?<cmd>skarp .+)$")).FirstOrDefault(x => x.Success)?.Groups["cmd"].Value;
            }
            var folded = Handles(unfolded).Except(Handles(printed)).ToList();
            await Assert.That(folded.Count).IsEqualTo(int.Parse(fold.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture));
            foreach (var h in folded) await Assert.That(listed).Contains(h);
        }
    }

    [Test]
    public async Task Analyze_saves_the_run_in_a_self_ignored_folder_unless_told_not_to()
    {
        var dir = Project("save", ("A.cs", Many("A", 1)));
        await Assert.That(_host.Run(dir, "analyze", dir).Exit).IsEqualTo(0);
        var skarp = Path.Combine(dir, ".skarp");
        await Assert.That(File.ReadAllLines(Path.Combine(skarp, ".gitignore")).Last()).IsEqualTo("*");
        var header = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(skarp, "last-run", "run.json")), AgentJsonContext.Default.LastRunHeader)!;
        await Assert.That(header.Version).IsEqualTo(1);
        await Assert.That(header.Documents).IsEquivalentTo(["analysis.json"], CollectionOrdering.Matching);
        await Assert.That(header.Files["A.cs"]).IsEqualTo(Paths.FileSha256(Path.Combine(dir, "A.cs")));
        await Assert.That(header.Commit).IsNull();                                   // outside git
        await Assert.That(Directory.GetDirectories(skarp).Select(Path.GetFileName)).IsEquivalentTo(new string?[] { "last-run" }, CollectionOrdering.Matching);

        var other = Project("nosave", ("A.cs", Many("A", 1)));
        _host.Run(other, "analyze", other, "--no-save");
        _host.Run(other, "baseline", other);
        _host.Run(other, "hotspots", other);
        await Assert.That(Directory.Exists(Path.Combine(other, ".skarp"))).IsFalse();
        File.WriteAllText(Path.Combine(other, ".skarp.json"), """{ "version": 1, "saveLastRun": false, "history": { "enabled": false } }""");
        _host.Run(other, "analyze", other);
        await Assert.That(Directory.Exists(Path.Combine(other, ".skarp"))).IsFalse();
    }

    [Test]
    public async Task Analyze_text_ids_resolve_with_show_and_compare_with_the_baseline()
    {
        var dir = Project("budget", ("A.cs", Many("A", 12)), ("B.cs", Many("B", 8, 2)));
        var r = _host.Run(dir, "analyze", dir);
        await Assert.That(r.Exit).IsEqualTo(0);
        var lines = Lines(r.Out);
        await Assert.That(lines[0]).StartsWith($"Skarp: analyze {dir.Replace('\\', '/')}, 2 files, health ").And.EndsWith(" average (10 = healthy)");
        await Assert.That(lines[1]).StartsWith("20 warnings, ").And.Contains("; showing the 20 worst");
        await Assert.That(FindingLines(r.Out).Count).IsEqualTo(20);
        foreach (var l in FindingLines(r.Out)) await Assert.That(l).StartsWith("warning line ");          // no comparison: the level
        await Assert.That(lines).Contains("No baseline file: `skarp baseline` records one; later runs then mark findings new or worse.");
        await AssertCommandsResolve(dir, r.Out, r.Out);
        var eg = Regex.Match(r.Out, "About a problem: `skarp show <id>` eg: `skarp show (?<id>[0-9a-f]{8})`");
        await Assert.That(eg.Success).IsTrue().Because(r.Out);
        await Assert.That(_host.Run(dir, "show", eg.Groups["id"].Value).Exit).IsEqualTo(0);

        // Against a baseline with --new-only: the change leads (worse, new); without --save, skarp show is not named.
        await Assert.That(_host.Run(dir, "baseline", dir).Exit).IsEqualTo(0);
        File.WriteAllText(Path.Combine(dir, "B.cs"), Class("B", [.. Enumerable.Range(0, 8).Select(i => Method(i == 3 ? 10 : 9, i)), Method(9, 20), Method(9, 21), Method(9, 22)]));
        r = _host.Run(dir, "analyze", dir, "--new-only");
        var found = FindingLines(r.Out);
        await Assert.That(found).Contains(l => l.StartsWith("worse line ", StringComparison.Ordinal) && l.Contains("B.M3 too-many-parameters: parameters 9 -> 10 (limit at least 8) [", StringComparison.Ordinal));
        await Assert.That(found.Count(l => l.StartsWith("new line ", StringComparison.Ordinal))).IsEqualTo(3);
        await Assert.That(found.Count).IsEqualTo(4);
        await Assert.That(Lines(r.Out)[1]).StartsWith("3 new, 1 worse than the baseline. ");
        await AssertCommandsResolve(dir, r.Out, r.Out);
        await Assert.That(_host.Run(dir, "analyze", dir, "--new-only", "--no-save").Out).DoesNotContain("skarp show");
    }

    [Test]
    public async Task Check_without_tracking_compares_with_the_baseline_and_holds_5_lines()
    {
        var dir = Project("check", ("src/A.cs", Many("A", 9)));
        var file = Path.Combine(dir, "src", "A.cs");
        var r = _host.Run(dir, "check", "src/A.cs");
        var lines = Lines(r.Out);
        await Assert.That(FindingLines(r.Out).Count).IsEqualTo(5);
        await Assert.That(lines[0]).IsEqualTo("Skarp: no baseline file, so every warning is shown; `skarp baseline` records one, then only new or worse findings are shown");
        await Assert.That(lines[1]).IsEqualTo("src/A.cs");
        await Assert.That(lines).Contains("+4 more: skarp show --file src/A.cs");
        await AssertCommandsResolve(dir, r.Out, _host.Run(dir, "check", "src/A.cs", "--max-lines", "100").Out);

        _host.Run(dir, "baseline", dir);
        await Assert.That(Lines(_host.Run(dir, "check", file).Out)).IsEmpty();        // nothing new or worse: silent, exit 0
        File.WriteAllText(file, Many("A", 9) + "public class Z { public void Q(int a, int b, int c, int d, int e, int f, int g, int h) { } }\n");
        r = _host.Run(dir, "check", file);
        await Assert.That(await Assert.That(FindingLines(r.Out)).HasSingleItem()).StartsWith("new ").And.Contains("Z.Q too-many-parameters: parameters 8 (limit at least 8) [");
        // Too-many-parameters was already at its cap, so the score is unchanged (the heading shows it).
        await Assert.That(Lines(r.Out)[0]).Matches(@"^Skarp: src/A\.cs health \d+\.\d \(10 = healthy\)$");
        await AssertCommandsResolve(dir, r.Out, r.Out);

        // --gate: exit 3 and the lines on standard error.
        r = _host.Run(dir, "check", file, "--gate");
        await Assert.That((r.Exit, r.Out)).IsEqualTo((3, ""));
        await Assert.That(r.Err).StartsWith("Skarp: gate failed: 1 problem new or worse.").And.Contains("Z.Q");
        await Assert.That(_host.Run(_host.Folder("empty"), "check", "x.cs", "--bogus").Exit).IsEqualTo(2);
    }

    [Test]
    public async Task Check_help_lists_what_is_left_out()
    {
        var r = _host.Run(_host.Folder("help"), "check", "--help");
        await Assert.That(r.Exit).IsEqualTo(0);
        foreach (var id in new[] { "active-complex-file", "large-type", "namespace-cycle", "high-coupling", "unused-member" }) await Assert.That(r.Out).Contains(id);
        await Assert.That(_host.Run(_host.Folder("help2"), "--help").Out).Contains("skarp check");
    }

    [Test]
    public async Task Hook_claude_answers_in_the_documented_json_and_fails_open()
    {
        var dir = Project("hook", ("A.cs", Class("A", Method(2))));
        var file = Path.Combine(dir, "A.cs");
        string Input(string ev, string? path = null, bool active = false, string? agent = null) => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = "sess-1", ["hook_event_name"] = ev, ["tool_name"] = "Edit", ["agent_id"] = agent, ["agent_type"] = agent is null ? null : "general-purpose",
            ["tool_input"] = new Dictionary<string, string?> { ["file_path"] = path }, ["stop_hook_active"] = active, ["cwd"] = dir,
        });
        (int Exit, string Out, string Err) Hook(string input, params string[] extra) => _host.RunWithInput(dir, input, ["check", "--hook", "claude", .. extra]);

        await Assert.That((Hook(Input("PostToolUse", file)).Exit, Hook(Input("PostToolUse", file)).Out)).IsEqualTo((0, ""));   // nothing to say
        File.WriteAllText(file, Class("A", Method(9)));
        var r = Hook(Input("PostToolUse", file));
        await Assert.That(r.Exit).IsEqualTo(0);
        using (var doc = JsonDocument.Parse(r.Out))
        {
            var root = doc.RootElement;
            await Assert.That(root.EnumerateObject().Select(p => p.Name)).IsEquivalentTo(["hookSpecificOutput"], CollectionOrdering.Matching);
            var hso = root.GetProperty("hookSpecificOutput");
            await Assert.That(hso.EnumerateObject().Select(p => p.Name)).IsEquivalentTo(["hookEventName", "additionalContext"], CollectionOrdering.Matching);
            await Assert.That(hso.GetProperty("hookEventName").GetString()).IsEqualTo("PostToolUse");
            var text = hso.GetProperty("additionalContext").GetString()!;
            var context = await Assert.That(WithPaths(text)).HasSingleItem();
            await Assert.That(context).Matches(FindingLine);
            await Assert.That(context).StartsWith("A.cs: new ").And.Contains("parameters 9 (limit");
            await Assert.That(Lines(text)[^1]).IsEqualTo(CheckText.AboutSkarp);
            await AssertCommandsResolve(dir, text, text);
        }
        await Assert.That(Hook(Input("PostToolUse", file)).Out).IsEqualTo("");                                // unchanged: silent
        await Assert.That(Hook(Input("PostToolUse", Path.Combine(dir, "README.md"))).Out).IsEqualTo("");      // not C#
        await Assert.That(Hook(Input("PreToolUse", file)).Out).IsEqualTo("");                                 // other event
        await Assert.That((Hook("not json").Exit, Hook("not json").Out)).IsEqualTo((0, ""));                  // fail open
        var tracked = Directory.GetFiles(Path.Combine(_host.Home, "tracked"), "*-sess-1.json").Single();
        await Assert.That(File.ReadAllText(tracked)).Contains("\"main\"");

        // --gate: worse or new warnings block.
        File.WriteAllText(file, Class("A", Method(10)));
        using (var doc = JsonDocument.Parse(Hook(Input("PostToolUse", file, agent: "sub-7"), "--gate").Out))
        {
            await Assert.That(doc.RootElement.GetProperty("decision").GetString()).IsEqualTo("block");
            await Assert.That(doc.RootElement.GetProperty("reason").GetString()).Contains("too-many-parameters");
        }
        await Assert.That(File.ReadAllText(tracked)).Contains("general-purpose");

        // Stop: the changed tracked files once; never while a Stop hook is already continuing; then quiet. A
        // change outside the edit tools counts, as the edit check's own line is not repeated.
        File.WriteAllText(file, Class("A", Method(11)));
        await Assert.That(Hook(Input("Stop", active: true)).Out).IsEqualTo("");
        using (var doc = JsonDocument.Parse(Hook(Input("Stop")).Out))
        {
            var hso = doc.RootElement.GetProperty("hookSpecificOutput");
            await Assert.That(hso.GetProperty("hookEventName").GetString()).IsEqualTo("Stop");
            var text = hso.GetProperty("additionalContext").GetString()!;
            // Against the session-start state (outside git: nothing), so A.M0 is new; no note line.
            await Assert.That(await Assert.That(FindingLines(text)).HasSingleItem()).StartsWith("new ").And.Contains("A.M0 too-many-parameters: parameters 11 ");
            await Assert.That(Lines(text)[0]).IsEqualTo("Skarp, end of turn: not shown after the edits (compared with the start of the session)");
            await AssertCommandsResolve(dir, text, text);
        }
        await Assert.That(Hook(Input("Stop")).Out).IsEqualTo("");
    }

    [Test]
    public async Task Show_warns_when_stale_lists_handle_collisions_and_reads_the_baseline()
    {
        // 1000 methods with 9 parameters: 1000 handles, so some share their first 4 characters (forced prefix).
        var dir = Project("show", ("Big.cs", Many("Big", 1000)), ("Small.cs", Many("Small", 1)));
        _host.Run(dir, "analyze", dir, "--no-config", "--no-history", "--no-semantic", "--rule", "duplicate-code=off");
        var handles = Handles(_host.Run(dir, "show", "--all", "--rule", "too-many-parameters").Out);
        await Assert.That(handles.Count).IsEqualTo(1001);
        var shared = handles.GroupBy(h => h[..4]).First(g => g.Count() > 1);
        var r = _host.Run(dir, "show", shared.Key, "--format", "text");
        await Assert.That(r.Exit).IsEqualTo(0);
        await Assert.That(Lines(r.Out)[0]).StartsWith($"Skarp: {shared.Count()} findings share this handle prefix");
        await Assert.That(Handles(r.Out).Order(StringComparer.Ordinal)).IsEquivalentTo(shared.Order(StringComparer.Ordinal), CollectionOrdering.Matching);
        var one = shared.First();
        var detail = _host.Run(dir, "show", one).Out;
        await Assert.That(Lines(detail)[0]).IsEqualTo("Skarp: Big.cs");
        await Assert.That(await Assert.That(FindingLines(detail)).HasSingleItem()).EndsWith($" [{one}]");
        r = _host.Run(dir, "show", "0000abcd");
        await Assert.That(r.Exit).IsEqualTo(2);
        await Assert.That(Lines(r.Err)).HasSingleItem();
        await Assert.That(_host.Run(dir, "show", "xyz!").Exit).IsEqualTo(2);

        // The default list is capped; its fold line runs.
        var listed = _host.Run(dir, "show", "--format", "text").Out;
        await Assert.That(FindingLines(listed).Count).IsEqualTo(ShowCommand.ListCap);
        var list = Lines(listed);
        await Assert.That(list).Contains("+951 more: skarp show --format text --all");   // warnings only
        await Assert.That(list).Contains("+2 notices in these files: add --include-notices");   // large-type and large-file of Big.cs

        // Stale: one of the files shown changed since the run was saved.
        await Assert.That(_host.Run(dir, "show", "--file", "Small.cs").Out).DoesNotContain("stale");
        File.AppendAllText(Path.Combine(dir, "Small.cs"), "// edited\n");
        var staleText = _host.Run(dir, "show", "--file", "Small.cs", "--format", "text").Out;
        var stale = Lines(staleText);
        await Assert.That(stale[1]).Matches(@"^stale 1 of 1 file shown changed since the data was saved \(.+\); refresh: skarp analyze \.$");
        await Assert.That(Handles(staleText)).HasSingleItem();
        await Assert.That(_host.Run(dir, "show", one).Out).DoesNotContain("stale");   // Big.cs did not change

        // --from baseline: ids and values, enriched from the saved run.
        await Assert.That(_host.Run(dir, "show", "--from", "baseline").Exit).IsEqualTo(2);
        _host.Run(dir, "baseline", dir, "--no-config", "--no-history", "--no-semantic");
        var fromBaseline = FindingLines(_host.Run(dir, "show", "--from", "baseline", "--file", "Small.cs", "--format", "text").Out);
        await Assert.That(await Assert.That(fromBaseline).HasSingleItem()).Contains("Small.M0");
        using var doc = JsonDocument.Parse(_host.Run(dir, "show", one, "--from", "baseline", "--format", "json").Out);
        await Assert.That(doc.RootElement.GetProperty("from").GetString()).IsEqualTo("baseline");
        await Assert.That(doc.RootElement.GetProperty("findings")[0].GetProperty("finding").GetProperty("value").GetInt32()).IsEqualTo(9);
    }

    [Test]
    public async Task Show_detail_has_the_member_metrics_and_runs_from_a_subfolder()
    {
        var dir = Project("detail", ("src/deep/A.cs", Many("A", 1)));
        _host.Run(dir, "analyze", dir);
        var handle = Handles(_host.Run(dir, "show").Out).Single();
        var r = _host.Run(Path.Combine(dir, "src", "deep"), "show", handle[..5]);
        await Assert.That(r.Exit).IsEqualTo(0);
        await Assert.That(r.Out).Contains("member metrics: lines 5-5");
        await Assert.That(r.Out).Contains("parameters 9");
        await Assert.That(r.Out).Contains($"id: too-many-parameters|src/deep/A.cs|Demo.A::M0");
        await Assert.That(r.Out).Contains("baseline: not compared");
    }

    [Test]
    public async Task A_check_after_the_saved_run_answers_show_for_its_new_findings()
    {
        using var repo = new SyntheticRepo();
        repo.Write("A.cs", Class("A", Method(2)));
        repo.Commit("one", DateTimeOffset.UtcNow.AddDays(-1));
        _host.Run(repo.Root, "analyze", repo.Root, "--no-history", "--no-semantic");
        File.WriteAllText(Path.Combine(repo.Root, "A.cs"), Class("A", Method(9), Method(3, 1)));
        var r = _host.Run(repo.Root, "check", "A.cs", "--track", "--session", "x");
        await Assert.That(FindingLines(r.Out)).HasSingleItem();
        await AssertCommandsResolve(repo.Root, r.Out, r.Out);
        File.WriteAllText(Path.Combine(repo.Root, "A.cs"), Class("A", Method(2)));
        var text = _host.Run(repo.Root, "check", "A.cs", "--track", "--session", "x").Out;
        var line = await Assert.That(WithPaths(text)).HasSingleItem();
        await Assert.That(line).StartsWith("A.cs: fixed line ");
        await AssertCommandsResolve(repo.Root, text, text);
        var shown = _host.Run(repo.Root, "show", FindingLine.Match(line).Groups["handle"].Value).Out;
        await Assert.That(shown).Contains("  fixed line ").And.Contains("resolved");
        await Assert.That(repo.Git("status", "--porcelain", "--", ".skarp").Trim()).IsEqualTo("");   // .skarp ignores itself
    }
}