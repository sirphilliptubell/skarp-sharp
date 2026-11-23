using SkarpSharp.Agent;
using SkarpSharp.Commands;
using SkarpSharp.Configuration;
using SkarpSharp.Tests.History;

namespace SkarpSharp.Tests.Install;

/// <summary>
/// The report the commit and push hooks print for a person (the text format has one line per finding, the id at its
/// end). The
/// four shapes from structured data (commit blocked, push blocked, several files with a worse finding and a fold, a pass
/// with a health line), then the same report from real staged and pushed content in a <see cref="SyntheticRepo"/>
/// (git isolated from the machine's configuration; nothing reads <c>~/.skarp</c>), and the one-line notes.
/// Snapshots: Install/reports/HookReportTests.*.verified.txt (SKARP_UPDATE_GOLDEN=1 rewrites them).
/// </summary>
public sealed class HookReportTests : IDisposable
{
    readonly SyntheticRepo _repo = new();
    readonly string _home = Directory.CreateTempSubdirectory("skarp-report-home-").FullName;

    public void Dispose()
    {
        _repo.Dispose();
        try { Directory.Delete(_home, true); } catch (IOException) { }
    }

    static Task Verify(string text) => Snapshots.Verify(text, Path.Combine("Install", "reports"), null, "txt", "SKARP_UPDATE_GOLDEN");

    static string Text(IEnumerable<string> lines) => string.Join("\n", lines) + "\n";

    static HookItem Brain(string path, int line, string place, string parts, string id = "4cca475f") =>
        new(path, line, place, "brain-method", LineKind.New, true, parts) { Handle = id };

    static HookItem Item(string path, int line, string place, string rule, LineKind kind, bool blocks, string numbers, string limit) =>
        new(path, line, place, rule, kind, blocks, numbers) { Limit = limit, Handle = line.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) };

    // ---- the shapes, from structured data --------------------------------------------------------------------------

    [Test]
    public async Task Commit_blocked()
    {
        var data = new HookReportData { What = "commit" };
        data.Items.Add(Brain("src/Orders.cs", 5, "Orders.Total", "long (84 physical lines), complex (cognitive complexity 1,120), deeply nested (nesting depth 4)"));
        data.Files.Add(new HookHealth("src/Orders.cs", 10, 7.2));
        await Verify(Text(HookReport.Blocked(data)));
    }

    [Test]
    public async Task Push_blocked()
    {
        var data = new HookReportData { What = "push", Project = "Project health 7.0 over 2 files (was 10.0)", ShowIds = true };
        data.Items.Add(Brain("src/Orders.cs", 5, "Orders.Total", "long (84 physical lines), complex (cognitive complexity 1,120), deeply nested (nesting depth 4)"));
        data.Files.Add(new HookHealth("src/Orders.cs", 10, 7.2));
        data.Files.Add(new HookHealth("src/Billing.cs", 10, 10));
        await Verify(Text(HookReport.Blocked(data)));
    }

    [Test]
    public async Task Several_files_with_a_worse_finding_and_a_fold()
    {
        var data = new HookReportData { What = "commit" };
        // Given out of print order: the report sorts files by health drop and findings by line.
        data.Items.Add(Item("src/Billing.cs", 40, "Billing.Run", "long-method", LineKind.New, true, "physical lines 96", "60"));
        data.Items.Add(Item("src/Billing.cs", 70, "Billing.Map", "deep-nesting", LineKind.New, false, "nesting depth 5", "4"));
        data.Items.Add(Item("src/Orders.cs", 120, "Orders.Parse", "complex-method", LineKind.Worse, true, "cognitive complexity 18 -> 27", "15"));
        data.Items.Add(Brain("src/Orders.cs", 5, "Orders.Total", "long (84 physical lines), complex (cognitive complexity 61)"));
        data.Items.Add(Item("src/Pricing.cs", 9, "Pricing.Round", "deep-nesting", LineKind.New, false, "nesting depth 5", "4"));
        data.Files.Add(new HookHealth("src/Billing.cs", 8.4, 7.9));
        data.Files.Add(new HookHealth("src/Orders.cs", 10, 7.2));
        data.Files.Add(new HookHealth("src/Pricing.cs", 10, 9.5));
        await Verify(Text(HookReport.Blocked(data)));
    }

    [Test]
    public async Task Pass_with_a_health_line()
    {
        var one = new HookReportData { What = "commit" };
        one.Files.Add(new HookHealth("src/Orders.cs", 7.2, 8.1));
        one.Files.Add(new HookHealth("src/Billing.cs", 9.0, 9.04)); // shown the same: not a change
        var several = new HookReportData { What = "commit" };
        several.Files.Add(new HookHealth("src/A.cs", 7.2, 8.1));
        several.Files.Add(new HookHealth("src/B.cs", 6.0, 9.0));
        several.Files.Add(new HookHealth("src/C.cs", 9.0, 8.0));
        var worse = new HookReportData { What = "commit" };
        worse.Files.Add(new HookHealth("src/A.cs", 9.2, 8.1));
        worse.Files.Add(new HookHealth("src/B.cs", 9.0, 8.0));
        var quiet = new HookReportData { What = "commit" };
        quiet.Files.Add(new HookHealth("src/A.cs", 10, 10));
        await Assert.That(HookReport.Passed(quiet)).IsNull();
        await Verify(Text([HookReport.Passed(one)!, HookReport.Passed(several)!, HookReport.Passed(worse)!]));
    }

    [Test]
    public async Task One_line_per_finding_with_the_id_last_and_the_scale_note_once()
    {
        var data = new HookReportData { What = "commit" };
        data.Items.Add(Brain("a.cs", 1, "A.F", "long (84 physical lines), complex (cognitive complexity 61)", "aaaaaaaa"));
        data.Items.Add(Brain("b.cs", 1, "B.F", "long (90 physical lines)", "bbbbbbbb"));
        data.Items.Add(Brain("c.cs", 1, "C.F", "long (95 physical lines)", "cccccccc"));
        data.Items.Add(new HookItem("a.cs", 7, "A.G", "deep-nesting", LineKind.New, true, "nesting depth 5") { Limit = "4", Notice = true, Handle = "dddddddd" });
        data.Files.Add(new HookHealth("a.cs", 9, 8));
        data.Files.Add(new HookHealth("b.cs", null, 8));
        var text = Text(HookReport.Blocked(data));
        await Assert.That(text).Contains("\na.cs health 9.0 -> 8.0 (10 = healthy)\n  new line 1 A.F brain-method: long (84 physical lines), complex (cognitive complexity 61) [aaaaaaaa]\n");
        await Assert.That(text).Contains("  new line 7 A.G deep-nesting (notice): nesting depth 5 (limit 4) [dddddddd]\n");
        await Assert.That(text).Contains("\nb.cs health 8.0\n").And.Contains("\nc.cs\n");
        await Assert.That(text.Split("(10 = healthy)").Length).IsEqualTo(2);
        await Assert.That(text).Contains("Skarp stopped this commit: 4 new problems in 3 staged files.").And.Contains("About the rules: `skarp explain brain-method`, `skarp explain deep-nesting`");
        // Nothing is saved on commit, so skarp show cannot resolve the ids: the report does not name it.
        await Assert.That(text).DoesNotContain("gate set").And.DoesNotContain("skarp show");
    }

    // ---- real content ----------------------------------------------------------------------------------------------

    /// <summary>The environment skarp sees (the settings seam; never the test runner's own).</summary>
    SettingsLocations _locations = SettingsLocations.None;

    CommandIO IO(StringWriter o, StringWriter e, TextReader? input = null) => new()
    {
        Out = o, Error = e, OpenStdout = () => Stream.Null, WorkingDirectory = _repo.Root, Home = _home,
        SettingsLocations = _locations, GitEnvironment = _repo.Environment, In = input ?? new StringReader(""),
    };

    async Task<(int Exit, string Out, string Error)> Skarp(TextReader? input, params string[] args)
    {
        var o = new StringWriter { NewLine = "\n" };
        var e = new StringWriter { NewLine = "\n" };
        int exit = await SkarpCommand.RunAsync(args, IO(o, e, input));
        return (exit, o.ToString(), e.ToString());
    }

    /// <summary>
    /// When Claude runs git (<c>CLAUDECODE=1</c>), the blocked commit and push reports offer two
    /// ways out, fix or stop and let the user decide, and name neither <c>skarp baseline</c> nor <c>--no-verify</c>.
    /// </summary>
    [Test]
    public async Task Under_Claude_a_blocked_commit_and_push_offer_two_ways_out()
    {
        await Committed();
        _locations = new SettingsLocations { Variable = n => n == "CLAUDECODE" ? "1" : null };
        _repo.Write("src/A.cs", StagedCheckTests.Bad());
        _repo.Git("add", "src/A.cs");
        var commit = await Skarp(null, "check", "--staged", "--gate");
        await Assert.That(commit.Exit).IsEqualTo(3);
        // The file is also past its health limit, so its notices are listed too ("them").
        await Assert.That(commit.Error).Contains("\nWhat you can do:\n  1. Fix them, then stage and commit again.\n" + HookReport.ClaudeWayOut + "\nAbout ");
        _repo.Commit("worse", DateTimeOffset.UnixEpoch);
        var push = await Skarp(null, "analyze", _repo.Root, "--gate", "--no-history", "--no-semantic", "--push-check");
        await Assert.That(push.Exit).IsEqualTo(3);
        await Assert.That(push.Error).Contains("\nWhat you can do:\n  1. Fix them, commit the fix, and push again.\n" + HookReport.ClaudeWayOut + "\nAbout ");
        var explicitFormat = await Skarp(null, "analyze", _repo.Root, "--gate", "--no-history", "--no-semantic", "--format", "text", "--push-check");
        await Assert.That(explicitFormat.Error).IsEqualTo("What you can do:\n  1. Fix them, commit the fix, and push again.\n" + HookReport.ClaudeWayOut + "\n");
        foreach (var text in new[] { commit.Error, push.Error, explicitFormat.Error })
            await Assert.That(text).DoesNotContain("skarp baseline").And.DoesNotContain("--no-verify").And.DoesNotContain("SuppressMessage");

        // People (no CLAUDECODE, or another value) keep the three.
        _locations = new SettingsLocations { Variable = n => n == "CLAUDECODE" ? "0" : null };
        push = await Skarp(null, "analyze", _repo.Root, "--gate", "--no-history", "--no-semantic", "--push-check");
        await Assert.That(push.Error).Contains("  3. Skip the check once:  `git push --no-verify`");
    }

    async Task Committed()
    {
        _repo.Write("src/A.cs", StagedCheckTests.Good);
        _repo.Write("src/B.cs", StagedCheckTests.Good.Replace("class A", "class B"));
        var (exit, output, error) = await Skarp(null, "baseline", _repo.Root, "--no-history", "--no-semantic");
        if (exit != 0) throw new InvalidOperationException(output + error);
        _repo.Commit("init", DateTimeOffset.UnixEpoch);
    }

    [Test]
    public async Task A_blocked_commit_from_staged_content()
    {
        await Committed();
        _repo.Write("src/A.cs", StagedCheckTests.Bad());
        _repo.Git("add", "src/A.cs");
        var (exit, output, error) = await Skarp(null, "check", "--staged", "--gate");
        await Assert.That(exit).IsEqualTo(3);
        await Assert.That(output).IsEmpty();
        await Verify(error);
    }

    [Test]
    public async Task A_blocked_push_keeps_the_handle_that_skarp_show_resolves()
    {
        await Committed();
        _repo.Write("src/A.cs", StagedCheckTests.Bad());
        _repo.Commit("worse", DateTimeOffset.UnixEpoch);
        var (exit, output, error) = await Skarp(null, "analyze", _repo.Root, "--gate", "--no-history", "--no-semantic", "--push-check", "origin", "url");
        await Assert.That(exit).IsEqualTo(3).Because(error);
        await Assert.That(output).IsEmpty();
        await Assert.That(error).DoesNotContain("hotspot").And.DoesNotContain("gate set");
        int at = error.IndexOf("eg: `skarp show ", StringComparison.Ordinal);
        await Assert.That(at).IsGreaterThan(0).Because(error);
        var handle = error[(at + "eg: `skarp show ".Length)..].Split('`')[0];
        var show = await Skarp(null, "show", handle);
        await Assert.That(show.Exit).IsEqualTo(0).Because(show.Error);
        // The first id is now a notice of the file past its health limit (they are listed by line).
        await Assert.That(show.Out).Contains("src/A.cs").And.Contains("baseline: new");
        await Verify(error.Replace(handle, "<handle>", StringComparison.Ordinal));

        // With --no-save the run is not saved, so skarp show is not named (the id stays at the end of the line).
        (exit, _, error) = await Skarp(null, "analyze", _repo.Root, "--gate", "--no-history", "--no-semantic", "--no-save", "--push-check");
        await Assert.That(exit).IsEqualTo(3);
        await Assert.That(error).DoesNotContain("skarp show").And.Contains($"  new line 5 A.F brain-method: ").And.Contains($" [{handle}]\n");
    }

    [Test]
    public async Task A_push_with_an_explicit_format_keeps_that_format_and_ends_with_the_ways_out()
    {
        await Committed();
        _repo.Write("src/A.cs", StagedCheckTests.Bad());
        _repo.Commit("worse", DateTimeOffset.UnixEpoch);
        var (exit, output, error) = await Skarp(null, "analyze", _repo.Root, "--gate", "--no-history", "--no-semantic", "--format", "text", "--push-check");
        await Assert.That(exit).IsEqualTo(3);
        await Assert.That(output).StartsWith("Skarp: gate failed: ").And.Contains("  new line 5 A.F brain-method: ");
        await Assert.That(error).StartsWith("What you can do:\n").And.Contains("`git push --no-verify`");
    }

    [Test]
    public async Task A_passing_commit_and_push_are_silent_or_one_line_and_the_notes_are_single_lines()
    {
        await Committed();
        // Clean change: nothing to say.
        _repo.Write("src/B.cs", StagedCheckTests.Good.Replace("class A", "class B").Replace("x + 1", "x + 2"));
        _repo.Git("add", "src/B.cs");
        var (exit, output, error) = await Skarp(null, "check", "--staged", "--gate");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output + error).IsEmpty();
        var push = await Skarp(null, "analyze", _repo.Root, "--gate", "--no-history", "--no-semantic", "--push-check");
        await Assert.That(push.Exit).IsEqualTo(0).Because(push.Error);
        await Assert.That(push.Out + push.Error).IsEmpty();

        // A known finding gets better: one line.
        _repo.Write("src/A.cs", StagedCheckTests.Bad());
        (exit, output, error) = await Skarp(null, "baseline", _repo.Root, "--no-history", "--no-semantic");
        _repo.Commit("accepted", DateTimeOffset.UnixEpoch);
        _repo.Write("src/A.cs", StagedCheckTests.Good);
        _repo.Git("add", "src/A.cs");
        (exit, output, error) = await Skarp(null, "check", "--staged", "--gate");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).IsEmpty();
        await Assert.That(error).Matches(@"^Skarp: commit ok\. src/A\.cs health \d+\.\d -> 10\.0 \(better\)\n$");

        // The notes.
        // A commit that fixes A.cs and records the baseline again: compared with the staged baseline, so nothing changed.
        await Skarp(null, "baseline", _repo.Root, "--no-history", "--no-semantic");
        _repo.Git("add", ".skarp-baseline.json");
        (_, _, error) = await Skarp(null, "check", "--staged", "--gate");
        await Assert.That(error).IsEqualTo("Skarp: this commit changes .skarp-baseline.json: the staged files are compared with the staged baseline\n");
        _repo.Git("rm", "-q", "--cached", ".skarp-baseline.json");
        (_, _, error) = await Skarp(null, "check", "--staged", "--gate");
        await Assert.That(error).IsEqualTo("Skarp: no baseline (.skarp-baseline.json) in the commit, so it is not checked: `skarp baseline .` records one, then `git add .skarp-baseline.json`\n");
        var other = await Skarp(new StringReader("refs/heads/x 1111111111111111111111111111111111111111 refs/heads/x 0000000000000000000000000000000000000000\n"),
            "analyze", _repo.Root, "--gate", "--no-history", "--no-semantic", "--push-check");
        await Assert.That(other.Exit).IsEqualTo(0);
        await Assert.That(other.Error).StartsWith("Skarp: push not checked: ");
    }
}
