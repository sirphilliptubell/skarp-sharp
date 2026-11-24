using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using SkarpSharp.Agent;
using SkarpSharp.Mcp;
using SkarpSharp.Semantic;
using SkarpSharp.Tests.Agent;
using SkarpSharp.Tests.History;
using static SkarpSharp.Tests.Mcp.McpFixture;

namespace SkarpSharp.Tests.Mcp;

/// <summary>
/// The MCP tool <c>check</c> without the protocol, in a git repository isolated from
/// the machine's configuration: the changed files, the comparison with the baseline (equal to <c>skarp check</c>) and
/// with the last commit, new and untracked files, files outside git, skipped inputs, notices, the fold, no writes,
/// argument errors and caps, and the semantic part with a fake runner (not evaluated, saved, reused or not, the lock and
/// the token).
/// </summary>
public sealed class McpCheckTests : IDisposable
{
    readonly SyntheticRepo _repo = new();
    readonly AgentHost _agent = new();
    static readonly DateTimeOffset T0 = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    public McpCheckTests() => _agent.GitEnvironment = _repo.Environment;

    public void Dispose()
    {
        DotnetRunner.Replacement.Value = null;
        _agent.Dispose();
        _repo.Dispose();
    }

    string Root => _repo.Root;
    McpHost Host => new() { WorkingDirectory = Root, GitEnvironment = _repo.Environment };

    void Write(string rel, string text) => _repo.Write(rel, text);
    void Commit() => _repo.Commit("change", T0.AddDays(-1));

    Task<string> Check(string json = "{}", CancellationToken ct = default) => new CheckTool(Host).CallAsync(Json(json), ct);

    static string Paths(params string[] files) => JsonSerializer.Serialize(files);

    static List<string> Lines(string text) => [.. text.Split('\n').Where(l => l.Length > 0)];

    /// <summary>The finding lines (indented, with an id) of an answer.</summary>
    static List<string> FindingLines(string text) => [.. Lines(text).Where(l => l.StartsWith("  ", StringComparison.Ordinal) && l.EndsWith(']'))];

    [Test]
    public async Task Changed_files_are_staged_unstaged_and_untracked_without_deleted_or_ignored_ones()
    {
        foreach (var name in new[] { "a.cs", "b.cs", "c.cs", "d.cs", "e.cs" }) Write(name, Class(name[..1].ToUpperInvariant(), Method("M", 1)));
        Write(".gitignore", "ignored/\n");
        Commit();
        Write("a.cs", Class("A", Method("M", 2)));                    // modified, not staged
        Write("b.cs", Class("B", Method("M", 2)));
        _repo.Git("add", "b.cs");                                      // staged
        Write("c.cs", Class("C", Method("M", 2)));
        _repo.Git("add", "c.cs");
        Write("c.cs", Class("C", Method("M", 3)));                    // staged, then modified
        _repo.Delete("d.cs");                                          // deleted: dropped
        _repo.Git("mv", "e.cs", "e2.cs");                              // renamed: the new path
        Write("new dir/f g.cs", Class("F", Method("M", 1)));          // untracked, a path with spaces
        Write("Upper.CS", Class("U", Method("M", 1)));                // the pathspec ignores case
        Write("ignored/h.cs", Class("H", Method("M", 1)));            // ignored: dropped
        Write("notes.txt", "not code");
        var changed = await ChangedFiles.ReadAsync(Root, _repo.Environment, default);
        var relative = changed!.Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/')).ToList();
        await Assert.That(relative).IsEquivalentTo(new[] { "Upper.CS", "a.cs", "b.cs", "c.cs", "e2.cs", "new dir/f g.cs" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        var answer = await Check();
        await Assert.That(answer).StartsWith("Skarp: 6 changed C# files checked against the last commit: ").Because(answer);
        // Path narrowing, and a folder with nothing changed.
        Directory.CreateDirectory(Path.Combine(Root, "other"));
        await Assert.That(await Check("""{"path":"new dir"}""")).StartsWith("Skarp: 1 changed C# file checked against the last commit: ");
        await Assert.That(await Check("""{"path":"other"}""")).IsEqualTo("Skarp: no changed C# files in other (compared with the last commit)\n");
    }

    [Test]
    public async Task Against_the_last_commit_lines_are_new_worse_better_and_fixed()
    {
        Write("src/Orders.cs", Class("Orders", Method("Ship", 70), Method("Pack", 80), Method("Bill", 70)));
        Write("src/Fine.cs", Class("Fine", Method("Small", 2)));
        Commit();
        Write("src/Orders.cs", Class("Orders", Method("Ship", 80), Method("Pack", 72), Method("Bill", 2), Method("Refund", 75)));
        var answer = await Check();
        var lines = Lines(answer);
        await Assert.That(lines[0]).IsEqualTo("Skarp: 1 changed C# file checked against the last commit: 2 new or worse in 1 file, 1 better, 1 fixed").Because(answer);
        await Assert.That(lines[1]).StartsWith("src/Orders.cs health ").Because(answer);
        var leads = FindingLines(answer).Select(l => l.Trim().Split(' ')[0]).Order(StringComparer.Ordinal).ToList();
        await Assert.That(leads).IsEquivalentTo(new[] { "better", "fixed", "new", "worse" }, TUnit.Assertions.Enums.CollectionOrdering.Matching).Because(answer);
        await Assert.That(answer).Contains("\nNot judged from single files: duplicate code, change activity, test coverage, and the rules that need a solution");
        await Assert.That(answer).Contains("\nAbout a rule and how to fix it: explain rule=long-method\n");
        await Assert.That(answer).DoesNotContain("About a finding");
        // Nothing written: no tracking file, no per-file record, no saved run.
        await Assert.That(Directory.Exists(Path.Combine(Root, ".skarp"))).IsFalse();
        await Assert.That(Directory.Exists(_agent.Home)).IsFalse();
    }

    [Test]
    public async Task With_a_baseline_the_lines_equal_skarp_check_and_only_new_and_worse_show()
    {
        Write("Orders.cs", Class("Orders", Method("Ship", 70), Method("Pack", 70)));
        Commit();
        var b = _agent.Run(Root, "baseline", Root, "--no-semantic");
        await Assert.That(b.Exit).IsEqualTo(0).Because(b.Err);
        Write("Orders.cs", Class("Orders", Method("Ship", 80), Method("Pack", 2), Method("Refund", 75), Method("Late", 72)));
        var answer = await Check();
        // The file fell below the floor, so its notices are listed too (as skarp check --gate lists them).
        await Assert.That(Lines(answer)[0]).IsEqualTo("Skarp: 1 changed C# file checked against the baseline: 12 new or worse in 1 file").Because(answer);
        await Assert.That(answer).Contains("\nHealth limit: Orders.cs fell to 5.8; the gate stops a drop that ends below 6.5.\n");
        await Assert.That(Directory.Exists(Path.Combine(Root, ".skarp"))).IsFalse();
        var cli = _agent.Run(Root, "check", Path.Combine(Root, "Orders.cs"), "--no-save", "--max-lines", "15", "--gate");
        await Assert.That(cli.Exit).IsEqualTo(3).Because(cli.Err);
        await Assert.That(cli.Err).Contains("Orders.cs health 6.7 -> 5.8 (below 6.5 after a drop)").Because(cli.Err);
        await Assert.That(FindingLines(answer)).IsEquivalentTo(FindingLines(cli.Err.Replace("\r\n", "\n", StringComparison.Ordinal)),
            TUnit.Assertions.Enums.CollectionOrdering.Matching).Because(answer + "\n---\n" + cli.Err);
        // The CLI's no-baseline note and About lines never print in the tool.
        foreach (var cliOnly in new[] { "skarp ", "`", "--verbose", "no baseline file" }) await Assert.That(answer).DoesNotContain(cliOnly);
        // A file given explicitly is checked the same way, and the first line says "C# files".
        var given = await Check($$"""{"files":{{Paths("Orders.cs")}}}""");
        await Assert.That(Lines(given)[0]).IsEqualTo("Skarp: 1 C# file checked against the baseline: 12 new or worse in 1 file");
        // Nothing changed: one line.
        Commit();
        b = _agent.Run(Root, "baseline", Root, "--no-semantic");
        await Assert.That(await Check($$"""{"files":{{Paths("Orders.cs")}}}""")).IsEqualTo("Skarp: 1 C# file checked against the baseline: nothing new or worse\n");
    }

    [Test]
    public async Task An_untracked_file_is_new_and_a_repository_without_commits_works()
    {
        Write("New.cs", Class("New", Method("M", 70)));
        var answer = await Check();
        await Assert.That(Lines(answer)[0]).IsEqualTo("Skarp: 1 changed C# file checked against the last commit: 1 new or worse in 1 file").Because(answer);
        await Assert.That(Lines(answer)[1]).Matches(@"^New\.cs health \d+\.\d \(new file; 10 = healthy\)$").Because(answer);
        await Assert.That(FindingLines(answer).Single()).StartsWith("  new line ");
        Write("Old.cs", Class("Old", Method("M", 1)));
        Commit();
        Write("Untracked.cs", Class("Untracked", Method("M", 70)));
        await Assert.That(await Check()).Contains("\nUntracked.cs health ");
    }

    [Test]
    public async Task Outside_git_given_files_show_all_warnings_and_changed_files_need_git()
    {
        using var f = new McpFixture();
        f.Write("Bad.cs", Class("Bad", Method("M", 70), Method("N", 2)));
        var host = f.Host;
        var answer = await new CheckTool(host).CallAsync(Json($$"""{"files":{{Paths("Bad.cs")}}}"""), default);
        await Assert.That(Lines(answer)[0]).IsEqualTo("Skarp: 1 C# file checked with nothing to compare (no baseline, not in git): 1 new or worse in 1 file").Because(answer);
        await Assert.That(Lines(answer)[1]).StartsWith("Bad.cs health ").Because(answer);
        await Assert.That(Lines(answer)[1]).DoesNotContain("->");
        var ex = await Assert.ThrowsAsync<McpException>(() => new CheckTool(host).CallAsync(null, default));
        await Assert.That(ex!.Message).IsEqualTo($"{f.Root} is not in a git repository; pass files");
    }

    [Test]
    public async Task Skipped_inputs_are_listed_with_the_reason_and_notices_show_when_asked()
    {
        Write("tests/Unit.Tests/Unit.Tests.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="xunit" Version="2.9.0" /></ItemGroup></Project>""");
        Write("tests/Unit.Tests/OrderTests.cs", Class("OrderTests", Method("T", 20)));
        Write(".skarp.json", """{ "version": 1, "exclude": ["gen/**"], "thresholds": { "long-method": { "notice": 10 } } }""");
        Write("gen/Gen.cs", Class("Gen", Method("G", 20)));
        Write("Ok.cs", Class("Ok", Method("Small", 6)));
        Write("notes.txt", "not code");
        var answer = await Check($$"""{"files":{{Paths("tests/Unit.Tests/OrderTests.cs", "gen/Gen.cs", "Ok.cs", "gone.cs", "notes.txt")}}}""");
        await Assert.That(answer).Contains("\nSkipped: gone.cs (not found)\n").Because(answer);
        await Assert.That(answer).Contains("\nSkipped: notes.txt (not C#)\n");
        await Assert.That(answer).Contains("\nSkipped: tests/Unit.Tests/OrderTests.cs (test code)\n");
        await Assert.That(answer).Contains("\nSkipped: gen/Gen.cs (excluded: ");
        await Assert.That(FindingLines(answer)).IsEmpty();
        var notices = await Check($$"""{"files":{{Paths("Ok.cs")}},"include_notices":true}""");
        await Assert.That(FindingLines(notices).Any(l => l.Contains(" long-method (notice): ", StringComparison.Ordinal))).IsTrue().Because(notices);
    }

    [Test]
    public async Task More_than_fifteen_lines_fold_into_one_line_naming_the_files()
    {
        Write("Big.cs", Class("Big", [.. Enumerable.Range(1, 12).Select(i => Method("M" + i, 70 + i))]));
        Write("Other.cs", Class("Other", [.. Enumerable.Range(1, 6).Select(i => Method("N" + i, 70 + i))]));
        var answer = await Check();
        await Assert.That(FindingLines(answer).Count).IsEqualTo(15).Because(answer);
        // Both new files start below 6.5, so their notices are listed too, and one fact line each.
        await Assert.That(answer).Contains("\n+59 more (37 in Big.cs, 22 in Other.cs)").Because(answer);
        await Assert.That(answer).Contains("\nHealth limit: Big.cs is a new file at 4.5; the gate stops new files below 6.5.\n");
        await Assert.That(answer).Contains("); check files=<one file> shows that file's first 15\n");
    }

    [Test]
    [Arguments("""{"files":[]}""")]
    [Arguments("""{"files":"null"}""")]
    [Arguments("""{"files":null}""")]
    [Arguments("""{"files":["notes.txt"]}""")]
    public async Task No_C_sharp_file_in_files_checks_the_changed_ones(string json)
    {
        Write("A.cs", Class("A", Method("M", 1)));
        Write("notes.txt", "x");
        await Assert.That(await Check(json)).StartsWith("Skarp: 1 changed C# file checked against the last commit: nothing new or worse");
    }

    [Test]
    [Arguments("""{"file":"A.cs"}""", "unknown argument 'file'; check takes files, path, include_notices")]
    [Arguments("""{"files":5}""", "files must be a list of paths (got 5)")]
    [Arguments("""{"files":[1]}""", "files must be a list of paths (got 1 in it)")]
    [Arguments("""{"include_notices":"yes"}""", "include_notices must be true or false (got 'yes')")]
    [Arguments("""{"path":"gone"}""", "path not found: ")]
    [Arguments("""{"files":["A.slnx","B.sln"]}""", "files holds more than one solution (")]
    public async Task Argument_errors_name_the_valid_values(string json, string message)
    {
        Write("A.slnx", "<Solution />");
        Write("B.sln", "");
        var ex = await Assert.ThrowsAsync<McpException>(() => Check(json));
        await Assert.That(ex!.Message).StartsWith(message);
    }

    [Test]
    public async Task The_caps_on_files_and_changed_files_are_errors_naming_the_cap()
    {
        var many = Paths([.. Enumerable.Range(0, 201).Select(i => $"F{i}.cs")]);
        var ex = await Assert.ThrowsAsync<McpException>(() => Check($$"""{"files":{{many}}}"""));
        await Assert.That(ex!.Message).IsEqualTo("files takes at most 200 entries (got 201)");
        // A string holding a JSON list is read as the list.
        Write("A.cs", Class("A", Method("M", 70)));
        await Assert.That(await Check("""{"files":"[\"A.cs\"]"}""")).StartsWith("Skarp: 1 C# file checked ");
        for (int i = 0; i < 2001; i++) Write($"many/F{i}.cs", "class F {}\n");
        var cap = await Assert.ThrowsAsync<McpException>(() => Check());
        await Assert.That(cap!.Message).StartsWith("check takes at most 2,000 changed C# files per call (found 2,002 under ");
        await Assert.That(cap.Message).EndsWith("; pass files or a narrower path");
    }

    [Test]
    public async Task The_largest_answer_stays_under_the_budget()
    {
        var files = new List<string>();
        for (int i = 0; i < 200; i++)
        {
            var rel = $"src/Some/Deeply/Nested/Feature{i % 7}/Area/ModuleWithAVeryLongName{i}.cs";
            Write(rel, Class("Module" + i, [.. Enumerable.Range(1, 4).Select(m => Method($"VeryLongMethodNameNumber{m}", 18 + m * 9))]));
            files.Add(rel);
        }
        var answer = await Check($$"""{"files":{{Paths([.. files])}},"include_notices":true}""");
        await Assert.That(FindingLines(answer).Count).IsEqualTo(15);
        await Assert.That(answer.Length).IsLessThan(40_000);
        await Assert.That(answer).DoesNotContain("\r");
    }

    /// <summary>The saved run of the repository made by analyze, marked as made with <paramref name="solution"/> and as a
    /// run whose semantic tier ran with <paramref name="findings"/> (the fake runner cannot make the tier run).</summary>
    async Task SavedSemanticRun(string solution, List<SkarpSharp.Model.Finding> findings, bool baseline)
    {
        var r = _agent.Run(Root, "analyze", Root, "--no-semantic", "--no-history");
        await Assert.That(r.Exit).IsEqualTo(0).Because(r.Err);
        var saved = LastRun.Load(Root)!;
        saved.Analysis!.Run.Semantic = new SkarpSharp.Model.SemanticTierInfo { Status = "ran", Solution = solution };
        saved.Analysis.SemanticTier = new SkarpSharp.Model.SemanticTierResults
        {
            Status = "ran",
            Results = [new SkarpSharp.Model.SemanticResult { Run = new SkarpSharp.Model.SemanticRunInfo { Status = "ran" }, Findings = findings, Baseline = baseline ? new SkarpSharp.Model.BaselineSummary() : null }],
        };
        LastRun.Save(Root, saved.Analysis, saved.Header.SettingsFingerprint, "test", GitState.None, DateTimeOffset.UtcNow, Path.Combine(Root, solution));
    }

    static SkarpSharp.Model.Finding Coupling(string path, string type, string state, double value) => new()
    {
        Id = $"high-coupling|{path}|{type}", Rule = "high-coupling", Severity = "warning", Path = path, Line = 3, Member = type,
        Metric = "cbo", Value = value, Threshold = 30, Comparison = ">", BaselineState = state, BaselineValue = state == "worsened" ? value - 5 : null,
        Message = "coupling", Handle = SkarpSharp.Findings.FindingEngine.HandleOf($"high-coupling|{path}|{type}"),
    };

    void FakeRunner(Action onCall) => DotnetRunner.Replacement.Value = (_, _, _) => { onCall(); return Task.FromResult(new ProcessRun(1, "", "error: fake", 1, 0)); };

    [Test]
    public async Task A_solution_that_cannot_run_gives_the_file_part_and_the_reason_and_saves_the_run()
    {
        Write("A.cs", Class("A", Method("M", 70)));
        Write("Lib.slnx", "<Solution />");
        int calls = 0;
        FakeRunner(() => Interlocked.Increment(ref calls));
        var answer = await Check($$"""{"files":{{Paths("Lib.slnx")}}}""");
        await Assert.That(calls).IsGreaterThan(0);
        await Assert.That(Lines(answer)[0]).StartsWith("Skarp: 1 changed C# file checked against the last commit: 1 new or worse").Because(answer);
        await Assert.That(answer).Contains("\nSemantic tier: not evaluated: ");
        await Assert.That(answer).Contains("\nNot judged from single files: duplicate code, change activity, test coverage, and the rules that need a solution");
        var header = LastRun.Load(Root)!.Header;
        await Assert.That(header.Solution).IsEqualTo("Lib.slnx");
    }

    [Test]
    public async Task A_fresh_saved_run_for_the_same_solution_is_reused_and_its_ids_resolve()
    {
        Write("src/Orders.cs", Class("Orders", Method("Ship", 2)));
        Write("src/Legacy.cs", Class("Legacy", Method("Old", 2)));
        Write("Lib.slnx", "<Solution />");
        Commit();
        Write("src/Orders.cs", Class("Orders", Method("Ship", 3)));
        await SavedSemanticRun("Lib.slnx", [Coupling("src/Orders.cs", "Orders", "worsened", 36), Coupling("src/Legacy.cs", "Legacy", "new", 31), Coupling("src/Known.cs", "Known", "known", 40)], baseline: true);
        int calls = 0;
        FakeRunner(() => Interlocked.Increment(ref calls));
        var answer = await Check($$"""{"files":{{Paths("Lib.slnx")}}}""");
        await Assert.That(calls).IsEqualTo(0).Because(answer);
        var lines = Lines(answer);
        await Assert.That(lines[0]).IsEqualTo("Skarp: 1 changed C# file checked against the last commit: nothing new or worse").Because(answer);
        await Assert.That(answer).Contains("\nLib.slnx, semantic rules against the semantic baseline: 2 new or worse in 2 files\n");
        await Assert.That(FindingLines(answer).Count).IsEqualTo(2);
        await Assert.That(answer).Contains("\nFrom the saved run: no analyzed file and no setting changed since it was made.\n");
        await Assert.That(answer).Contains("\nNot judged from single files: duplicate code, change activity and test coverage.\n");
        await Assert.That(answer).Contains("\nAbout a rule and how to fix it: explain rule=high-coupling\n");
        var id = Regex.Match(answer, @"About a finding: findings id=([0-9a-f]{8})").Groups[1].Value;
        await Assert.That(id).IsNotEmpty().Because(answer);
        // find_worst_code, which lets the tier choose, reuses it too: the root has only this solution.
        await Assert.That(await new FindWorstCodeTool(Host).CallAsync(Json("""{"depth":"semantic"}"""), default)).Contains("\nFrom the saved run: ");
        await Assert.That(calls).IsEqualTo(0);
        // Every id of the semantic part is in the saved run.
        foreach (var handle in FindingLines(answer).Select(l => l[^9..^1]))
            await Assert.That(new FindingsTool(Host).Call(Json($$"""{"id":"{{handle}}"}"""))).Contains($"[{handle}]");
    }

    [Test]
    public async Task Without_a_semantic_baseline_only_warnings_in_the_checked_files_show()
    {
        Write("src/Orders.cs", Class("Orders", Method("Ship", 2)));
        Write("Lib.slnx", "<Solution />");
        Commit();
        Write("src/Orders.cs", Class("Orders", Method("Ship", 3)));
        await SavedSemanticRun("Lib.slnx", [Coupling("src/Orders.cs", "Orders", null!, 36), Coupling("src/Other.cs", "Other", null!, 31)], baseline: false);
        FakeRunner(() => { });
        var answer = await Check($$"""{"files":{{Paths("Lib.slnx")}}}""");
        await Assert.That(answer).Contains("\nLib.slnx, semantic rules: no semantic baseline to compare with, so the warnings in the checked files are shown: 1 finding\n").Because(answer);
        await Assert.That(FindingLines(answer).Single()).StartsWith("  warning line 3 type Orders high-coupling: ");
    }

    [Test]
    public async Task Reuse_is_refused_for_another_solution_and_for_a_newer_baseline()
    {
        Write("src/Orders.cs", Class("Orders", Method("Ship", 2)));
        Write("Lib.slnx", "<Solution />");
        Write("Other.slnx", "<Solution />");
        await SavedSemanticRun("Lib.slnx", [Coupling("src/Orders.cs", "Orders", "new", 36)], baseline: true);
        int calls = 0;
        FakeRunner(() => Interlocked.Increment(ref calls));
        var other = await Check($$"""{"files":{{Paths("Other.slnx")}}}""");
        await Assert.That(calls).IsGreaterThan(0);
        await Assert.That(other).Contains("\nSemantic tier: not evaluated: ");
        await SavedSemanticRun("Lib.slnx", [Coupling("src/Orders.cs", "Orders", "new", 36)], baseline: true);
        calls = 0;
        await Check($$"""{"files":{{Paths("Lib.slnx")}}}""");
        await Assert.That(calls).IsEqualTo(0);
        Write(".skarp-baseline.semantic.json", "{}");
        File.SetLastWriteTimeUtc(Path.Combine(Root, ".skarp-baseline.semantic.json"), DateTime.UtcNow.AddMinutes(5));
        await Check($$"""{"files":{{Paths("Lib.slnx")}}}""");
        await Assert.That(calls).IsGreaterThan(0);
    }

    [Test]
    public async Task A_second_semantic_call_from_either_tool_is_refused_and_the_token_reaches_the_runner()
    {
        Write("A.cs", Class("A", Method("M", 70)));
        Write("Lib.slnx", "<Solution />");
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var seen = new List<CancellationToken>();
        DotnetRunner.Replacement.Value = async (_, _, ct) =>
        {
            lock (seen) seen.Add(ct);
            started.TrySetResult();
            await release.Task;
            return new ProcessRun(1, "", "error: fake", 1, 0);
        };
        using var cts = new CancellationTokenSource();
        var first = Check($$"""{"files":{{Paths("Lib.slnx")}}}""", cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(60));
        var ex = await Assert.ThrowsAsync<McpException>(() => new FindWorstCodeTool(Host).CallAsync(Json("""{"depth":"semantic"}"""), default));
        await Assert.That(ex!.Message).IsEqualTo($"a semantic run is already going for {Root}; try again when it ends");
        var again = await Assert.ThrowsAsync<McpException>(() => Check($$"""{"files":{{Paths("Lib.slnx")}}}"""));
        await Assert.That(again!.Message).IsEqualTo(ex.Message);
        release.SetResult();
        await first;
        cts.Cancel();
        await Assert.That(seen).IsNotEmpty();
        await Assert.That(seen.All(t => t.IsCancellationRequested)).IsTrue();
    }
}
