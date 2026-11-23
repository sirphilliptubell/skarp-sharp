using System.Text.Json;
using System.Text.RegularExpressions;
using SkarpSharp.Agent;
using SkarpSharp.Configuration;
using SkarpSharp.Tests.Findings;
using SkarpSharp.Tests.History;
using static SkarpSharp.Tests.Agent.AgentHost;

namespace SkarpSharp.Tests.Agent;

/// <summary>
/// The gate set, full paths for files outside the hook's project,
/// health only against a known earlier score, the line limits and the one-line <c>+N more</c>, <c>skarp show --session</c>,
/// and the notice hint. In the text format: the lead word in place of the labels <c>severe</c> and
/// <c>blocked</c>, and "blocks" said once, in the verdict.
/// </summary>
public sealed class HookOutputTests : IDisposable
{
    readonly AgentHost _host = new();
    readonly SyntheticRepo _repo = new();
    static readonly DateTimeOffset T0 = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);
    const string Session = "0123abcd-5555-4444-8888-999999999999";

    public HookOutputTests() => _host.Now = T0;

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

    /// <summary>A folder outside git with a settings file (its root) and one file.</summary>
    string Plain(string name, string settings, string file, string content)
    {
        var dir = _host.Folder(name);
        File.WriteAllText(Path.Combine(dir, ".skarp.json"), settings);
        File.WriteAllText(Path.Combine(dir, file), content);
        return Path.Combine(dir, file);
    }

    /// <summary>The raw hook output for an event, with the hook input's <c>cwd</c>.</summary>
    string Hook(string ev, string? file, string cwd, string session = Session, params string[] extra) => _host.RunWithInput(cwd, JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["session_id"] = session, ["hook_event_name"] = ev, ["tool_name"] = "Edit", ["stop_hook_active"] = false, ["cwd"] = cwd,
        ["tool_input"] = new Dictionary<string, string?> { ["file_path"] = file },
    }), ["check", "--hook", "claude", .. extra]).Out;

    /// <summary>The lines of a hook answer (context or block reason) and whether it blocked.</summary>
    static (List<string> Lines, bool Blocked) Answer(string raw)
    {
        if (raw.Length == 0) return ([], false);
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        return root.TryGetProperty("reason", out var reason)
            ? (Lines(reason.GetString()!), true)
            : (Lines(root.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!), false);
    }

    const string Settings = """{ "version": 1, "history": { "enabled": false }, "semantic": { "enabled": false } }""";
    const string Narrowed = """{ "version": 1, "history": { "enabled": false }, "semantic": { "enabled": false }, "gate": { "on": ["long-method:warning"] } }""";

    // ---- labels ------------------------------------------------------------------------------------------------------

    [Test]
    public async Task Gate_set_warnings_block_with_the_gate_and_the_verdict_says_so()
    {
        var file = Plain("labels", Settings, "L.cs", Class("L", Method(9), Method(5, 1)));
        var dir = Path.GetDirectoryName(file)!;
        var plain = FindingLines(_host.Run(dir, "check", file, "--track", "--include-notices").Out);
        await Assert.That(plain.Count).IsEqualTo(2);
        await Assert.That(plain[0]).StartsWith("new line ").And.Contains(" L.M0 too-many-parameters: parameters 9 (limit at least 8) [");
        await Assert.That(plain[1]).StartsWith("new line ").And.Contains(" L.M1 too-many-parameters (notice): parameters 5 ");

        File.WriteAllText(file, Class("L", Method(10), Method(5, 1)));
        var r = _host.Run(dir, "check", file, "--track", "--gate");
        await Assert.That((r.Exit, r.Out)).IsEqualTo((3, ""));
        await Assert.That(Lines(r.Err)[0]).IsEqualTo("Skarp: gate failed: 1 problem new or worse.");
        await Assert.That(await Assert.That(FindingLines(r.Err)).HasSingleItem()).StartsWith("worse line ").And.Contains("parameters 9 -> 10");

        // show leads with the level (it never blocks); the JSON keeps the severity.
        _host.Run(dir, "analyze", dir);
        var shown = FindingLines(_host.Run(dir, "show", "--format", "text").Out);
        await Assert.That(shown).Contains(l => l.StartsWith("warning line ", StringComparison.Ordinal) && l.Contains(" L.M0 ", StringComparison.Ordinal));
        await Assert.That(_host.Run(dir, "show", "--format", "json").Out).Contains("\"severity\": \"warning\"");
    }

    [Test]
    public async Task A_narrowed_gate_set_keeps_warning_and_does_not_block_outside_it()
    {
        var file = Plain("narrow", Narrowed, "N.cs", Class("N", Method(9)));
        var dir = Path.GetDirectoryName(file)!;
        var r = _host.Run(dir, "check", file, "--track", "--gate");
        await Assert.That(r.Exit).IsEqualTo(0);
        await Assert.That(await Assert.That(FindingLines(r.Out)).HasSingleItem()).StartsWith("new line ");
        await Assert.That(r.Out).DoesNotContain("gate failed");
        _host.Run(dir, "analyze", dir);
        await Assert.That(await Assert.That(FindingLines(_host.Run(dir, "show", "--format", "text").Out)).HasSingleItem()).StartsWith("warning line ");
    }

    [Test]
    public async Task A_composite_follows_its_own_severity_and_gate_membership()
    {
        var file = Plain("brain", Settings, "Importer.cs", BrainTests.Importer());
        var dir = Path.GetDirectoryName(file)!;
        static bool Brain(string l) => l.Contains(" brain-method: ", StringComparison.Ordinal);
        // New (outside git): the long-method part (80 lines) is a new warning, so the composite blocks.
        var r = _host.Run(dir, "check", file, "--track", "--max-lines", "20", "--gate");
        await Assert.That(r.Exit).IsEqualTo(3);
        await Assert.That(r.Err).StartsWith("Skarp: gate failed: ");
        await Assert.That(await Assert.That(FindingLines(r.Err)).HasSingleItem(Brain)).StartsWith("new line ");
        // Worse only in a notice part (cognitive complexity): the gate does not trip.
        File.WriteAllText(file, BrainTests.Importer(runBranches: 4));
        r = _host.Run(dir, "check", file, "--track", "--max-lines", "20", "--gate");
        await Assert.That(r.Exit).IsEqualTo(0);
        await Assert.That(await Assert.That(FindingLines(r.Out)).HasSingleItem(Brain)).StartsWith("worse line ");

        // Narrowed to long-method: the composite is outside the set, while its part trips the gate.
        var narrowed = Plain("brain2", Narrowed, "Importer.cs", BrainTests.Importer());
        r = _host.Run(Path.GetDirectoryName(narrowed)!, "check", narrowed, "--track", "--max-lines", "20", "--gate");
        await Assert.That(r.Exit).IsEqualTo(3);
        await Assert.That(await Assert.That(FindingLines(r.Err)).HasSingleItem(Brain)).StartsWith("new line ");
    }

    // ---- paths and health --------------------------------------------------------------------------------------------

    [Test]
    public async Task Files_of_another_root_show_their_full_path_and_no_false_health_new()
    {
        var a = Commit("src/A.cs", Class("A", Method(9)));
        var b = Plain("other", Settings, "B.cs", Class("B", Method(2)));
        Hook("PostToolUse", a, _repo.Root);
        Hook("PostToolUse", b, _repo.Root);
        File.WriteAllText(a, Class("A", Method(10), Method(9, 1)));                   // outside the edit tools
        File.WriteAllText(b, Class("B", Method(9)));
        var (lines, _) = Answer(Hook("Stop", null, _repo.Root));
        var full = b.Replace('\\', '/');
        var text = string.Join("\n", lines);
        await Assert.That(WithPaths(text)).Contains(l => l.StartsWith("src/A.cs: worse line ", StringComparison.Ordinal) && l.Contains("parameters 9 -> 10", StringComparison.Ordinal));
        await Assert.That(WithPaths(text)).Contains(l => l.StartsWith($"{full}: new line ", StringComparison.Ordinal) && l.Contains("B.M0 too-many-parameters: parameters 9 ", StringComparison.Ordinal));
        // A.cs has an earlier score (git HEAD): its health change; B.cs is outside git with no earlier score: its score
        // Without an arrow.
        await Assert.That(HealthOnly(text).Count(l => l.Contains(" -> ", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(HealthOnly(text)).Contains(l => l.StartsWith("src/A.cs health ", StringComparison.Ordinal) && l.Contains(" -> ", StringComparison.Ordinal));
        await Assert.That(Headings(text)).Contains($"{full} health 9.7");

        // The other way round: with the hook's cwd in the other root, src/A.cs is the full path.
        File.WriteAllText(b, Class("B", Method(10)));
        var (again, _) = Answer(Hook("Stop", null, Path.GetDirectoryName(b)!));
        await Assert.That(WithPaths(string.Join("\n", again))).Contains(l => l.StartsWith("B.cs: new line ", StringComparison.Ordinal));
    }

    [Test]
    public async Task A_file_new_to_git_keeps_its_health_new()
    {
        Commit("src/Other.cs", Class("Other"));
        _repo.Write("src/New.cs", Class("N", Method(9)));
        var (lines, _) = Answer(Hook("PostToolUse", Path.Combine(_repo.Root, "src/New.cs"), _repo.Root));
        await Assert.That(lines[0]).IsEqualTo("Skarp: src/New.cs health 9.7 (new file)");
    }

    // ---- limits and the fold line ------------------------------------------------------------------------------------

    /// <summary>A tracked file that gains 60 warnings outside the edit tools before the Stop.</summary>
    string Sixty()
    {
        var file = Commit("src/S.cs", Class("S"));
        File.WriteAllText(file, Class("S") + "\n");
        Hook("PostToolUse", file, _repo.Root);
        File.WriteAllText(file, Class("S", [.. Enumerable.Range(0, 60).Select(i => Method(9, i))]));
        return file;
    }

    [Test]
    public async Task Sixty_findings_without_the_gate_are_15_finding_lines_with_a_short_fold_line()
    {
        Sixty();
        var (lines, blocked) = Answer(Hook("Stop", null, _repo.Root));
        await Assert.That(blocked).IsFalse();
        var text = string.Join("\n", lines);
        await Assert.That(FindingLines(text).Count).IsEqualTo(15);
        await Assert.That(await Assert.That(HealthOnly(text)).HasSingleItem()).StartsWith("src/S.cs health ");
        await Assert.That(lines).Contains("+45 more: skarp show --session 0123abcd --new-only");

        // The fold line's command lists the rest (as the Stop judged them), then the notice hint is absent: no notices.
        _host.Run(_repo.Root, "analyze", _repo.Root, "--no-history", "--no-semantic");
        var show = _host.Run(_repo.Root, "show", "--session", "0123abcd", "--new-only", "--all", "--format", "text").Out;
        await Assert.That(WithPaths(show).Count(l => l.StartsWith("src/S.cs: new line ", StringComparison.Ordinal))).IsEqualTo(60);
    }

    [Test]
    public async Task With_the_gate_the_Stop_lists_blocked_lines_first_up_to_50()
    {
        Sixty();
        var (lines, blocked) = Answer(Hook("Stop", null, _repo.Root, Session, "--gate"));
        await Assert.That(blocked).IsTrue();
        // The file also lost more than 1.0 since the session start, so its notices are listed too.
        await Assert.That(lines[0]).IsEqualTo("Skarp did not end the turn: 60 problems are new or worse in files this session edited; health past its limit in 1 file.");
        var findings = FindingLines(string.Join("\n", lines));
        await Assert.That(findings.Count).IsEqualTo(50);
        foreach (var l in findings) await Assert.That(l).StartsWith("new line ");
        await Assert.That(lines.Any(l => l.StartsWith('+') && l.EndsWith(" more: skarp show --session 0123abcd --new-only", StringComparison.Ordinal))).IsTrue();
        await Assert.That(lines).Contains(CheckText.StopWayOut);
    }

    [Test]
    public async Task The_fold_line_names_the_session_also_when_notices_were_cut()
    {
        var file = Commit("src/T.cs", Class("T"));
        File.WriteAllText(file, Class("T", [.. Enumerable.Range(0, 8).Select(i => Method(5, i))]));
        var (lines, _) = Answer(Hook("PostToolUse", file, _repo.Root, Session, "--include-notices"));
        var findings = FindingLines(string.Join("\n", lines));
        await Assert.That(findings.Count).IsEqualTo(5);
        await Assert.That(findings[1]).StartsWith("new line ").And.Contains(" too-many-parameters (notice): ");
        await Assert.That(lines).Contains("+3 more: skarp show --session 0123abcd --new-only");
        await Assert.That(lines[^1]).IsEqualTo(CheckText.AboutSkarp);
    }

    [Test]
    public async Task The_gate_limit_is_a_setting()
    {
        _repo.Write(".skarp.json", """{ "version": 1, "output": { "gateMaxLines": 10 } }""");
        Sixty();
        var (lines, _) = Answer(Hook("Stop", null, _repo.Root, Session, "--gate"));
        await Assert.That(FindingLines(string.Join("\n", lines)).Count).IsEqualTo(10);
        // --setting (on skarp config, as for output.checkMaxLines) and the settings file check the range 1 to 1,000.
        var dir = _host.Folder("limits");
        await Assert.That(CommandLine.Parse(["config", dir, "--setting", "output.gateMaxLines=40"]).Options!.GateMaxLines).IsEqualTo(40);
        await Assert.That(CommandLine.Parse(["config", dir, "--setting", "output.gateMaxLines=1001"]).Error ?? "").Contains("output.gateMaxLines: 1001 is out of range (1 to 1000)");
        File.WriteAllText(Path.Combine(dir, ".skarp.json"), """{ "version": 1, "output": { "gateMaxLines": 1001 } }""");
        await Assert.That(CommandLine.Parse(["analyze", dir]).Error ?? "").Contains("output.gateMaxLines: must be at most 1000");
    }

    [Test]
    public async Task The_fold_line_never_lists_more_than_three_files()
    {
        var dir = _host.Folder("four");
        File.WriteAllText(Path.Combine(dir, ".skarp.json"), Settings);
        var files = Enumerable.Range(0, 4).Select(i => Path.Combine(dir, $"F{i}.cs")).ToList();
        foreach (var (f, i) in files.Select((f, i) => (f, i))) File.WriteAllText(f, Class($"F{i}", Method(9), Method(9, 1)));
        var four = Lines(_host.Run(dir, ["check", .. files, "--max-lines", "2"]).Out);
        await Assert.That(four).Contains("+6 more: skarp show --file F1.cs --file F2.cs --file F3.cs");   // the no-baseline note is free
        var all = Lines(_host.Run(dir, ["check", .. files, "--max-lines", "1"]).Out);
        await Assert.That(all).Contains("+7 more: skarp show");
        var three = Lines(_host.Run(dir, ["check", .. files.Take(2), "--max-lines", "3"]).Out);
        await Assert.That(three).Contains("+1 more: skarp show --file F1.cs");
        var both = Lines(_host.Run(dir, ["check", .. files.Take(2), "--max-lines", "1"]).Out);
        await Assert.That(both).Contains("+3 more: skarp show --file F0.cs --file F1.cs");
    }

    // ---- show --session ----------------------------------------------------------------------------------------------

    [Test]
    public async Task Show_session_reads_two_roots_one_without_a_saved_run()
    {
        var a = Commit("src/A.cs", Class("A", Method(2)));
        var b = Plain("other", Settings, "B.cs", Class("B", Method(2)));
        _host.Run(_repo.Root, "analyze", _repo.Root, "--no-history", "--no-semantic");
        File.WriteAllText(a, Class("A", Method(9), Method(5, 1)));
        File.WriteAllText(b, Class("B", Method(9)));
        Hook("PostToolUse", a, _repo.Root);
        Hook("PostToolUse", b, _repo.Root);

        var r = _host.Run(_repo.Root, "show", "--session", "0123", "--format", "text");
        await Assert.That(r.Exit).IsEqualTo(0).Because(r.Err);
        var lines = Lines(r.Out);
        var other = Path.GetDirectoryName(b)!.Replace('\\', '/');
        await Assert.That(lines[0]).StartsWith("Skarp: 2 findings in the files of session ");
        await Assert.That(lines[1]).IsEqualTo($"{other}: no saved run (only skarp check results); skarp analyze {other} saves one");
        await Assert.That(WithPaths(r.Out)).Contains(l => l.StartsWith("src/A.cs: warning line ", StringComparison.Ordinal));
        await Assert.That(WithPaths(r.Out)).Contains(l => l.StartsWith($"{other}/B.cs: warning line ", StringComparison.Ordinal));
        await Assert.That(lines).Contains("+1 notice in these files: add --include-notices");
        var notices = _host.Run(_repo.Root, "show", "--session", "0123", "--format", "text", "--include-notices").Out;
        await Assert.That(WithPaths(notices)).Contains(l => l.StartsWith("src/A.cs: notice line ", StringComparison.Ordinal));
        await Assert.That(Lines(notices)).DoesNotContain(l => l.StartsWith('+'));

        using var doc = JsonDocument.Parse(_host.Run(_repo.Root, "show", "--session", "0123", "--format", "json").Out);
        await Assert.That(doc.RootElement.GetProperty("session").GetString()).IsEqualTo(Session);
        await Assert.That(doc.RootElement.GetProperty("roots").GetArrayLength()).IsEqualTo(2);
        await Assert.That(doc.RootElement.GetProperty("noSavedRun").GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    public async Task Show_session_takes_a_prefix_and_lists_the_candidates_of_an_ambiguous_one()
    {
        var a = Commit("src/A.cs", Class("A", Method(2)));
        File.WriteAllText(a, Class("A", Method(9)));
        Hook("PostToolUse", a, _repo.Root, "abcd1111-x");
        Hook("PostToolUse", a, _repo.Root, "abcd2222-y");
        var r = _host.Run(_repo.Root, "show", "--session", "abcd");
        await Assert.That(r.Exit).IsEqualTo(2);
        await Assert.That(r.Err).Contains("abcd1111-x").And.Contains("abcd2222-y");
        r = _host.Run(_repo.Root, "show", "--session", "abcd1");
        await Assert.That(r.Exit).IsEqualTo(0).Because(r.Err);
        await Assert.That(r.Out).Contains("session abcd1111-x");
        await Assert.That(_host.Run(_repo.Root, "show", "--session", "abc").Exit).IsEqualTo(2);       // under 4 characters
        await Assert.That(_host.Run(_repo.Root, "show", "--session", "ffff").Exit).IsEqualTo(2);      // no such session
    }

    [Test]
    public async Task The_notice_hint_appears_only_when_notices_were_left_out()
    {
        var file = Plain("hint", Settings, "H.cs", Class("H", Method(9), Method(5, 1)));
        var dir = Path.GetDirectoryName(file)!;
        _host.Run(dir, "analyze", dir);
        await Assert.That(Lines(_host.Run(dir, "show").Out)).Contains("+1 notice in these files: add --include-notices");
        await Assert.That(Lines(_host.Run(dir, "show", "--include-notices").Out)).DoesNotContain(l => l.Contains("notices in these files", StringComparison.Ordinal) || l.Contains("notice in these files", StringComparison.Ordinal));
        await Assert.That(Lines(_host.Run(dir, "show", "--rule", "long-method").Out)).DoesNotContain(l => l.StartsWith('+'));   // no notices match
        await Assert.That(_host.Run(dir, "show", "--min-severity", "notice").Exit).IsEqualTo(2);              // removed
        await Assert.That(_host.Run(dir, "check", file, "--min-severity", "notice").Exit).IsEqualTo(2);
        await Assert.That(Regex.IsMatch(_host.Run(dir, "show", "--help").Out, "--include-notices")).IsTrue();
    }
}
