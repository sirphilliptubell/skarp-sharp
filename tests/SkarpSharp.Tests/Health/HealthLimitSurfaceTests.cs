using System.Text.Json;
using SkarpSharp.Agent;
using SkarpSharp.Mcp;
using SkarpSharp.Tests.Agent;
using SkarpSharp.Tests.History;
using static SkarpSharp.Tests.Agent.AgentHost;

namespace SkarpSharp.Tests.Health;

/// <summary>
/// A drop made only of
/// notices, which the default gate set (<c>*:warning</c>) never sees, blocks on every surface: <c>analyze --gate</c> (text
/// and JSON), the commit hook, the push hook, the Claude edit note and Stop, and is stated by MCP <c>check</c>. Each shows
/// the heading with the limit and the file's notices, the verdict and the exit code; <c>skarp baseline</c> accepts it.
/// Real git in a temporary repository isolated from the machine's configuration; nothing reads <c>~/.skarp</c>.
/// </summary>
public sealed class HealthLimitSurfaceTests : IDisposable
{
    readonly SyntheticRepo _repo = new();
    readonly AgentHost _host = new();
    static readonly DateTimeOffset T0 = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
    const string Session = "0123abcd-5555-4444-8888-999999999999";

    /// <summary>History and the semantic tier off; duplicate code off (the fixtures repeat one method).</summary>
    const string Settings = """{ "version": 1, "history": { "enabled": false }, "semantic": { "enabled": false }, "rules": { "duplicate-code": false } }""";

    public HealthLimitSurfaceTests()
    {
        _host.Now = T0;
        _host.GitEnvironment = _repo.Environment;
    }

    public void Dispose()
    {
        _host.Dispose();
        _repo.Dispose();
    }

    /// <summary>A method with four notices and no warning (deep-nesting, too-many-parameters, complex-condition,
    /// magic-number): alone in a file it costs exactly 1.0 (10.0 -&gt; 9.0).</summary>
    static string Noticey(string name) => $$"""
            public int {{name}}(int a, int b, int c, int d, int e)
            {
                if (a > 1 && b > 2 || c > 3 && d > 4)
                {
                    if (a > 5)
                    {
                        if (b > 6)
                        {
                            if (c > 7) { return a + 17; }
                        }
                    }
                }
                return a + b + c + d + e;
            }

        """;

    static string Type(string name, params string[] members) =>
        $"namespace N;\n\npublic class {name}\n{{\n    public int Small(int x) => x + 1;\n{string.Concat(members)}}}\n";

    (int Exit, string Out, string Err) Skarp(params string[] args)
    {
        var (exit, output, error) = _host.Run(_repo.Root, args);
        return (exit, output.Replace("\r\n", "\n", StringComparison.Ordinal), error.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    /// <summary>A repository with <c>src/A.cs</c> (clean), its settings and its baseline committed.</summary>
    void Committed()
    {
        _repo.Write(".skarp.json", Settings);
        _repo.Write("src/A.cs", Type("A"));
        Baseline();
        _repo.Commit("init", T0.AddDays(-1));
    }

    void Baseline()
    {
        var b = Skarp("baseline", _repo.Root);
        if (b.Exit != 0) throw new InvalidOperationException(b.Err);
        _repo.Git("add", ".skarp-baseline.json");
    }

    // ---- analyze --gate ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Analyze_gate_trips_on_a_notice_only_drop_and_skarp_baseline_accepts_it()
    {
        Committed();
        _repo.Write("src/A.cs", Type("A", Noticey("Plan")));
        var r = Skarp("analyze", _repo.Root, "--gate", "--no-save");
        await Assert.That(r.Exit).IsEqualTo(3).Because(r.Out + r.Err);
        var lines = Lines(r.Out);
        await Assert.That(lines[0]).IsEqualTo("Skarp: gate failed: health past its limit in 1 file (health: a drop under 1.0, new files at least 6.5, no drop below 6.5)");
        await Assert.That(r.Out).Contains("\nsrc/A.cs health 10.0 -> 9.0 (down 1.0, limit under 1.0)\n");
        // The notices that cost the points are listed under the heading, in line order.
        var notices = FindingLines(r.Out);
        await Assert.That(notices.Count).IsEqualTo(4).Because(r.Out);
        foreach (var l in notices) await Assert.That(l).StartsWith("new line ").And.Contains("(notice)");

        // JSON: gate.health with the limits and the file; the gate's line on standard error names it.
        var json = Skarp("analyze", _repo.Root, "--gate", "--no-save", "--format", "json");
        await Assert.That(json.Exit).IsEqualTo(3);
        await Assert.That(json.Err).Contains("skarp: gate tripped: health past its limit in 1 file: src/A.cs health 10.0 -> 9.0 (down 1.0, limit under 1.0). Fix them");
        using (var doc = JsonDocument.Parse(json.Out))
        {
            var gate = doc.RootElement.GetProperty("gate");
            await Assert.That(gate.GetProperty("tripped").GetBoolean()).IsTrue();
            var health = gate.GetProperty("health");
            await Assert.That((health.GetProperty("maxDrop").GetDouble(), health.GetProperty("newFileMin").GetDouble(), health.GetProperty("floor").GetDouble())).IsEqualTo((1.0, 6.5, 6.5));
            var file = health.GetProperty("files")[0];
            await Assert.That((file.GetProperty("path").GetString(), file.GetProperty("before").GetDouble(), file.GetProperty("now").GetDouble(), file.GetProperty("rule").GetString()))
                .IsEqualTo(("src/A.cs", 10.0, 9.0, "drop"));
        }

        // Markdown and GitHub annotations carry the same verdict.
        var md = Skarp("analyze", _repo.Root, "--gate", "--no-save", "--format", "markdown");
        await Assert.That((md.Exit, md.Out.Contains("**Gate tripped**: health past its limit in 1 file: src/A.cs health 10.0 -> 9.0 (down 1.0, limit under 1.0).", StringComparison.Ordinal)))
            .IsEqualTo((3, true)).Because(md.Out);
        var gh = Skarp("analyze", _repo.Root, "--gate", "--no-save", "--format", "github");
        await Assert.That((gh.Exit, gh.Out.Contains("::error title=skarp gate::gate tripped: health past its limit in 1 file: src/A.cs", StringComparison.Ordinal)))
            .IsEqualTo((3, true)).Because(gh.Out);

        // A narrowed gate set does not narrow the health rules; a larger drop limit or 0 lets it pass.
        await Assert.That(Skarp("analyze", _repo.Root, "--gate", "--no-save", "--gate-on", "long-method:warning").Exit).IsEqualTo(3);
        await Assert.That(Skarp("analyze", _repo.Root, "--gate", "--no-save", "--gate-health-drop", "1.1").Exit).IsEqualTo(0);
        var off = Skarp("analyze", _repo.Root, "--gate", "--no-save", "--gate-health-drop", "0");
        await Assert.That((off.Exit, Lines(off.Out)[0])).IsEqualTo((0, "Skarp: gate passed"));

        // Accepting: skarp baseline, as for findings.
        Baseline();
        await Assert.That(Skarp("analyze", _repo.Root, "--gate", "--no-save").Exit).IsEqualTo(0);
    }

    [Test]
    public async Task Analyze_gate_judges_a_new_file_against_the_baseline_file_list()
    {
        Committed();
        _repo.Write("src/B.cs", Type("B", Noticey("Plan")));
        var r = Skarp("analyze", _repo.Root, "--gate", "--no-save", "--gate-health-new-min", "9.5");
        await Assert.That(r.Exit).IsEqualTo(3).Because(r.Out);
        await Assert.That(r.Out).Contains("\nsrc/B.cs health 9.0 (new file, limit at least 9.5)\n");
        await Assert.That(Skarp("analyze", _repo.Root, "--gate", "--no-save").Exit).IsEqualTo(0); // 9.0 is above 6.5

    }

    // ---- git hooks --------------------------------------------------------------------------------------------------

    [Test]
    public async Task The_commit_hook_blocks_a_notice_only_drop_then_skarp_baseline_accepts_it()
    {
        Committed();
        _repo.Write("src/A.cs", Type("A", Noticey("Plan")));
        _repo.Git("add", "src/A.cs");
        var r = Skarp("check", "--staged", "--gate");
        await Assert.That(r.Exit).IsEqualTo(3).Because(r.Err);
        await Assert.That(r.Err).StartsWith("Skarp stopped this commit: health past its limit in 1 staged file.\n\nsrc/A.cs health 10.0 -> 9.0 (down 1.0, limit under 1.0) (10 = healthy)\n  new line 6 A.Plan ");
        await Assert.That(FindingLines(r.Err).Count).IsEqualTo(4);
        await Assert.That(r.Err).Contains("  1. Fix them, then stage and commit again.");

        Baseline();
        var accepted = Skarp("check", "--staged", "--gate");
        await Assert.That(accepted.Exit).IsEqualTo(0).Because(accepted.Err);

        // A new staged file below the new-file minimum.
        _repo.Write("src/B.cs", Type("B", Noticey("Plan")));
        _repo.Git("add", "src/B.cs");
        var created = Skarp("check", "--staged", "--gate", "--gate-health-new-min", "9.5");
        await Assert.That(created.Exit).IsEqualTo(3);
        await Assert.That(created.Err).Contains("\nsrc/B.cs health 9.0 (new file, limit at least 9.5) (10 = healthy)\n");
    }

    [Test]
    public async Task The_push_hook_blocks_with_the_limit_heading()
    {
        Committed();
        _repo.Write("src/A.cs", Type("A", Noticey("Plan")));
        _repo.Commit("worse", T0);
        var r = Skarp("analyze", _repo.Root, "--gate", "--push-check");
        await Assert.That(r.Exit).IsEqualTo(3).Because(r.Err);
        await Assert.That(r.Err).StartsWith("Skarp stopped this push: health past its limit in 1 file.\nProject health ");
        await Assert.That(r.Err).Contains("\nsrc/A.cs health 10.0 -> 9.0 (down 1.0, limit under 1.0) (10 = healthy)\n  new line 6 A.Plan ");
        await Assert.That(FindingLines(r.Err).Count).IsEqualTo(4);
    }

    // ---- Claude hook ------------------------------------------------------------------------------------------------

    string Hook(string ev, string? file, params string[] extra) => _host.RunWithInput(_repo.Root, JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["session_id"] = Session, ["hook_event_name"] = ev, ["tool_name"] = "Edit", ["stop_hook_active"] = false, ["cwd"] = _repo.Root,
        ["tool_input"] = new Dictionary<string, string?> { ["file_path"] = file },
    }), ["check", "--hook", "claude", .. extra]).Out;

    static (List<string> Lines, bool Blocked) Answer(string raw)
    {
        if (raw.Length == 0) return ([], false);
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        return root.TryGetProperty("reason", out var reason)
            ? (Lines(reason.GetString()!), true)
            : (Lines(root.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!), false);
    }

    [Test]
    public async Task The_Claude_edit_note_names_the_limit_once_and_the_Stop_blocks()
    {
        Committed();
        var file = Path.Combine(_repo.Root, "src", "A.cs");
        File.WriteAllText(file, Type("A", Noticey("Plan")));
        var (edit, blocked) = Answer(Hook("PostToolUse", file, "--gate"));
        await Assert.That(blocked).IsTrue();
        await Assert.That(edit[0]).IsEqualTo("Skarp: this edit took health past its limit in 1 file, which blocks the end of the turn.");
        await Assert.That(edit[1]).IsEqualTo("src/A.cs health 10.0 -> 9.0 (down 1.0, limit under 1.0)");
        await Assert.That(FindingLines(string.Join("\n", edit)).Count).IsEqualTo(4);

        // A later edit that keeps the file past its limit does not repeat it.
        File.WriteAllText(file, Type("A", Noticey("Plan")) + "\n");
        var (again, _) = Answer(Hook("PostToolUse", file, "--gate"));
        await Assert.That(string.Join("\n", again)).DoesNotContain("limit under");

        // The Stop blocks and lists the notices, although the edit showed them.
        var (stop, stopBlocked) = Answer(Hook("Stop", null, "--gate"));
        await Assert.That(stopBlocked).IsTrue();
        await Assert.That(stop[0]).IsEqualTo("Skarp did not end the turn: health past its limit in 1 file this session edited.");
        await Assert.That(stop).Contains("src/A.cs health 10.0 -> 9.0 (down 1.0, limit under 1.0)");
        await Assert.That(FindingLines(string.Join("\n", stop)).Count).IsEqualTo(4);
        await Assert.That(stop).Contains(CheckText.StopWayOut);
    }

    [Test]
    public async Task Without_the_gate_the_Claude_hook_does_not_judge_health()
    {
        Committed();
        var file = Path.Combine(_repo.Root, "src", "A.cs");
        File.WriteAllText(file, Type("A", Noticey("Plan")));
        var (edit, blocked) = Answer(Hook("PostToolUse", file));
        await Assert.That(blocked).IsFalse();
        await Assert.That(string.Join("\n", edit)).DoesNotContain("limit");
        var (_, stopBlocked) = Answer(Hook("Stop", null));
        await Assert.That(stopBlocked).IsFalse();
    }

    // ---- MCP check --------------------------------------------------------------------------------------------------

    [Test]
    public async Task Mcp_check_states_the_limit_as_a_fact_and_nothing_when_the_limits_are_off()
    {
        Committed();
        _repo.Write("src/A.cs", Type("A", Noticey("Plan")));
        var host = new McpHost { WorkingDirectory = _repo.Root, GitEnvironment = _repo.Environment };
        var answer = await new CheckTool(host).CallAsync(SkarpSharp.Tests.Mcp.McpFixture.Json("{}"), CancellationToken.None);
        await Assert.That(answer).Contains("\nHealth limit: src/A.cs is down 1.0; the gate stops at 1.0 or more.\n").Because(answer);
        await Assert.That(FindingLines(answer).Count).IsEqualTo(4).Because(answer);
        await Assert.That(answer).DoesNotContain("blocks").And.DoesNotContain("limit under");

        _repo.Write(".skarp.json", Settings.Replace("\"version\": 1,", "\"version\": 1, \"gate\": { \"health\": { \"maxDrop\": 0, \"newFileMin\": 0, \"floor\": 0 } },", StringComparison.Ordinal));
        var off = await new CheckTool(host).CallAsync(SkarpSharp.Tests.Mcp.McpFixture.Json("{}"), CancellationToken.None);
        await Assert.That(off).DoesNotContain("Health limit").Because(off);
        await Assert.That(FindingLines(off)).IsEmpty();
    }
}
