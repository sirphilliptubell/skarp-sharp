using System.Text.Json.Nodes;
using SkarpSharp.Commands;
using SkarpSharp.Install;

namespace SkarpSharp.Tests.Install;

/// <summary>
/// <c>skarp install claude</c> with <c>--mcp</c>,
/// <c>--hooks</c> and neither (both) at each scope, the merge into <c>.mcp.json</c> and <c>~/.claude.json</c> (other
/// servers and keys untouched, an existing entry updated in place), <c>--remove</c>, <c>--check</c> and the setup
/// screen's row. Every machine read goes through <see cref="InstallSeams"/>: a temp repository and a temp home; nothing
/// reads or writes the real <c>~/.claude.json</c>.
/// </summary>
public sealed class InstallMcpTests : IDisposable
{
    readonly string _base = Path.Combine(Path.GetTempPath(), "skarp-install-mcp-" + Guid.NewGuid().ToString("N"));
    readonly string _repo, _home, _bin;
    readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);

    public InstallMcpTests()
    {
        _repo = Path.Combine(_base, "repo");
        _home = Path.Combine(_base, "home");
        _bin = Path.Combine(_base, "bin");
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_bin);
        _env["PATH"] = _bin;
        _env["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
        File.WriteAllText(Path.Combine(_bin, OperatingSystem.IsWindows() ? "skarp.exe" : "skarp"), "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, true); } catch (IOException) { }
    }

    string McpJson => Path.Combine(_repo, ".mcp.json");
    string ClaudeJson => Path.Combine(_home, ".claude.json");
    string LocalHooks => Path.Combine(_repo, ".claude", "settings.local.json");
    string ProjectKey => _repo.Replace('\\', '/');

    InstallSeams Seams => new()
    {
        Variable = n => _env.GetValueOrDefault(n), UserHome = _home, GitIgnored = (_, _) => true,
        Interactive = () => false, Answers = new StringReader(""),
        RunProgram = _ => new ProgramResult { ExitCode = 0, Milliseconds = 5 },
    };

    async Task<(int Exit, string Out, string Error)> Run(params string[] args)
    {
        var o = new StringWriter { NewLine = "\n" };
        var e = new StringWriter { NewLine = "\n" };
        int exit = await SkarpCommand.RunAsync(["install", .. args], new CommandIO
        {
            Out = o, Error = e, OpenStdout = () => Stream.Null, WorkingDirectory = _repo, Install = Seams,
        });
        return (exit, o.ToString(), e.ToString());
    }

    static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    static string Entry(string command = "skarp") => new JsonObject { ["type"] = "stdio", ["command"] = command, ["args"] = new JsonArray("mcp") }.ToJsonString();

    [Test]
    public async Task Mcp_alone_at_each_scope_writes_only_the_entry()
    {
        var (exit, output, error) = await Run("claude", "--mcp", "--yes");
        await Assert.That(exit).IsEqualTo(0).Because(error);
        await Assert.That(File.Exists(LocalHooks)).IsFalse();
        await Assert.That(output).Contains($"Skarp MCP server, local scope: {ClaudeJson} (new file)");
        await Assert.That(output).Contains("  skarp: add stdio: skarp mcp");
        await Assert.That(Read(ClaudeJson)["projects"]![ProjectKey]!["mcpServers"]!["skarp"]!.ToJsonString()).IsEqualTo(Entry());

        (exit, output, error) = await Run("claude", "--mcp", "--scope", "project", "--yes");
        await Assert.That(exit).IsEqualTo(0).Because(error);
        await Assert.That(Read(McpJson)["mcpServers"]!["skarp"]!.ToJsonString()).IsEqualTo(Entry());
        await Assert.That(output).Contains("it asks once to approve a server from .mcp.json");
        await Assert.That(output).Contains($"note: also installed at local scope ({ClaudeJson})");

        (exit, _, error) = await Run("claude", "--mcp", "--scope", "user", "--yes");
        await Assert.That(exit).IsEqualTo(0).Because(error);
        var user = Read(ClaudeJson);
        await Assert.That(user["mcpServers"]!["skarp"]!.ToJsonString()).IsEqualTo(Entry());
        await Assert.That(user["projects"]![ProjectKey]!["mcpServers"]!["skarp"]).IsNotNull();
        await Assert.That(File.Exists(ClaudeJson + ".bak")).IsTrue();
    }

    [Test]
    public async Task Neither_option_writes_both_and_hooks_alone_writes_only_the_hooks()
    {
        var (exit, output, error) = await Run("claude", "--scope", "project", "--yes");
        await Assert.That(exit).IsEqualTo(0).Because(error);
        await Assert.That(output).Contains("Skarp hooks, project scope:");
        await Assert.That(output).Contains("Skarp MCP server, project scope:");
        await Assert.That(File.Exists(Path.Combine(_repo, ".claude", "settings.json"))).IsTrue();
        await Assert.That(File.Exists(McpJson)).IsTrue();

        (exit, output, error) = await Run("claude", "--hooks", "--yes");
        await Assert.That(exit).IsEqualTo(0).Because(error);
        await Assert.That(output).DoesNotContain("MCP");
        await Assert.That(File.Exists(LocalHooks)).IsTrue();
        await Assert.That(File.Exists(ClaudeJson)).IsFalse();
    }

    [Test]
    public async Task Merging_keeps_other_servers_and_updates_the_entry_in_place()
    {
        File.WriteAllText(McpJson, """
            {
                "mcpServers": {
                    "other": { "type": "stdio", "command": "other-server" },
                    "skarp": { "type": "stdio", "command": "old/skarp", "args": [ "serve" ], "env": { "A": "1" } }
                },
                "extra": true
            }
            """);
        var (exit, output, error) = await Run("claude", "--mcp", "--scope", "project", "--yes");
        await Assert.That(exit).IsEqualTo(0).Because(error);
        await Assert.That(output).Contains("  skarp: update\n    old: stdio: old/skarp serve\n    new: stdio: skarp mcp\n");
        var root = Read(McpJson);
        await Assert.That(root["extra"]!.GetValue<bool>()).IsTrue();
        await Assert.That(root["mcpServers"]!["other"]!.ToJsonString()).IsEqualTo("""{"type":"stdio","command":"other-server"}""");
        await Assert.That(root["mcpServers"]!["skarp"]!.ToJsonString()).IsEqualTo("""{"type":"stdio","command":"skarp","args":["mcp"],"env":{"A":"1"}}""");
        // The four-space indentation is kept; a second run changes nothing.
        await Assert.That(File.ReadAllText(McpJson)).Contains("\n    \"mcpServers\"");
        (exit, output, _) = await Run("claude", "--mcp", "--scope", "project", "--yes");
        await Assert.That(output).Contains("Nothing to change: Skarp's MCP server is already installed.");
        // A file that is not strict JSON is refused with the entry to add by hand.
        File.WriteAllText(McpJson, "{ // comment\n}");
        (exit, _, error) = await Run("claude", "--mcp", "--scope", "project", "--yes");
        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(error).Contains("\"skarp\": {");
    }

    [Test]
    public async Task Local_scope_keeps_the_rest_of_claude_json()
    {
        File.WriteAllText(ClaudeJson, $$"""
            {
              "numStartups": 3,
              "mcpServers": { "global": { "command": "g" } },
              "projects": { "{{ProjectKey}}": { "allowedTools": [], "mcpServers": { "mine": { "command": "m" } } } }
            }
            """);
        var (exit, _, error) = await Run("claude", "--mcp", "--yes");
        await Assert.That(exit).IsEqualTo(0).Because(error);
        var root = Read(ClaudeJson);
        await Assert.That(root["numStartups"]!.GetValue<int>()).IsEqualTo(3);
        await Assert.That(root["mcpServers"]!.ToJsonString()).IsEqualTo("""{"global":{"command":"g"}}""");
        var project = root["projects"]![ProjectKey]!;
        await Assert.That(project["allowedTools"]).IsNotNull();
        await Assert.That(project["mcpServers"]!["mine"]).IsNotNull();
        await Assert.That(project["mcpServers"]!["skarp"]!.ToJsonString()).IsEqualTo(Entry());
    }

    [Test]
    public async Task Remove_and_check_take_the_same_options()
    {
        await Run("claude", "--yes");
        File.WriteAllText(McpJson, $$"""{ "mcpServers": { "skarp": {{Entry("missing/skarp.exe")}}, "other": { "command": "o" } } }""");
        var (exit, output, _) = await Run("claude", "--check", "--mcp");
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(output).DoesNotContain("Skarp hooks for Claude Code:");
        await Assert.That(output).Contains("Skarp MCP server for Claude Code:");
        await Assert.That(output).Contains("local: ");
        await Assert.That(output).Contains("  skarp: stdio: skarp mcp");
        await Assert.That(output).Contains("  skarp: stdio: missing/skarp.exe mcp (not used: an entry at a scope listed above wins)");
        await Assert.That(output).Contains("problem: missing/skarp.exe not found: Claude Code shows the server as failed");
        (exit, output, _) = await Run("claude", "--check");
        await Assert.That(output).Contains("Skarp hooks for Claude Code:");
        await Assert.That(output).Contains("Skarp MCP server for Claude Code:");

        (exit, output, var error) = await Run("claude", "--remove", "--mcp", "--scope", "project", "--yes");
        await Assert.That(exit).IsEqualTo(0).Because(error);
        await Assert.That(output).Contains("  skarp: remove stdio: missing/skarp.exe mcp");
        await Assert.That(Read(McpJson)["mcpServers"]!.ToJsonString()).IsEqualTo("""{"other":{"command":"o"}}""");
        (exit, output, error) = await Run("claude", "--remove", "--yes");
        await Assert.That(exit).IsEqualTo(0).Because(error);
        await Assert.That(output).Contains("Skarp hooks, local scope:");
        await Assert.That(output).Contains("  skarp: remove stdio: skarp mcp");
        await Assert.That(Read(ClaudeJson)["projects"]![ProjectKey]!["mcpServers"]!.ToJsonString()).IsEqualTo("{}");
        (_, output, _) = await Run("claude", "--check");
        await Assert.That(output).Contains("no Skarp MCP server installed");
    }

    [Test]
    [Arguments(new[] { "claude", "--mcp", "--gate" }, "--gate applies to the hooks; it cannot be used with --mcp alone")]
    [Arguments(new[] { "claude", "--mcp", "--no-stop" }, "--no-stop applies to the hooks")]
    [Arguments(new[] { "claude", "--print" }, "--print writes one file: add --hooks or --mcp")]
    [Arguments(new[] { "claude", "--check", "--gate" }, "--check takes --hooks or --mcp only")]
    public async Task Usage_errors(string[] args, string message)
    {
        var (exit, _, error) = await Run(args);
        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(error).Contains(message);
    }

    [Test]
    public async Task Print_writes_the_new_mcp_file()
    {
        var (exit, output, error) = await Run("claude", "--mcp", "--scope", "project", "--print");
        await Assert.That(exit).IsEqualTo(0).Because(error);
        await Assert.That(JsonNode.Parse(output)!["mcpServers"]!["skarp"]!.ToJsonString()).IsEqualTo(Entry());
        await Assert.That(error).Contains("Skarp MCP server, project scope:");
        await Assert.That(File.Exists(McpJson)).IsFalse();
    }

    [Test]
    public async Task The_setup_screen_has_an_mcp_row()
    {
        var target = HooksTarget.Open(_repo, Seams, SkarpSharp.Configuration.SettingsLocations.None);
        // Nothing installed: the row is on (an install writes both), and the call is plain install claude.
        await Assert.That((target.Mcp, target.Changed)).IsEqualTo((true, false));
        await Assert.That(string.Join(" ", target.Args())).IsEqualTo("install claude");
        target.ToggleMcp();
        await Assert.That(target.Changed).IsTrue();
        await Assert.That(string.Join(" ", target.Args())).IsEqualTo("install claude --hooks");
        await Assert.That(new HooksText(target).Line(HookRow.Mcp)).IsEqualTo("*MCP server            off  not installed");
        target.Reset(HookRow.Mcp);
        await Assert.That(target.Mcp).IsTrue();
        // Hooks installed without the MCP entry: the row reads off; with it: on and installed.
        await Run("claude", "--hooks", "--yes");
        target = HooksTarget.Open(_repo, Seams, SkarpSharp.Configuration.SettingsLocations.None);
        await Assert.That(target.Mcp).IsFalse();
        await Run("claude", "--mcp", "--yes");
        target = HooksTarget.Open(_repo, Seams, SkarpSharp.Configuration.SettingsLocations.None);
        await Assert.That(new HooksText(target).Line(HookRow.Mcp)).IsEqualTo(" MCP server            on  installed");
        await Assert.That(InstallCall.Parse(["install", "claude", "--mcp", "--scope", "user"]).Call).IsEqualTo(new InstallCall(ClaudeScope.User, false, new HookSpec(), Hooks: false, Mcp: true));
    }
}
