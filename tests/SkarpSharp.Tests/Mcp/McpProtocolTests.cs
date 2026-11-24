using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static SkarpSharp.Tests.Mcp.McpFixture;

namespace SkarpSharp.Tests.Mcp;

/// <summary>
/// The built <c>skarp mcp</c> as a
/// process over stdio, with both protocol revisions (2025-11-25 <c>initialize</c>, 2026-07-28 <c>server/discover</c>),
/// <c>tools/list</c> and the tools. Fails on any standard output line that is not a JSON-RPC 2.0 message and on any
/// byte after the last line feed. The CLI project's JIT build is copied with this test run's <c>SkarpSharp.Core.dll</c>,
/// so a stale build cannot pass; skipped when the CLI is not built. The process reads no machine settings
/// (<c>SKARP_NO_USER_CONFIG</c>, a temp <c>SKARP_HOME</c>).
/// </summary>
[NotInParallel(nameof(McpProtocolTests))]
public sealed class McpProtocolTests : IDisposable
{
    readonly McpFixture _f = new();
    readonly string _temp = Directory.CreateTempSubdirectory("skarp-mcp-bin-").FullName;

    public void Dispose()
    {
        _f.Dispose();
        try { Directory.Delete(_temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    string? Exe()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SkarpSharp.slnx"))) root = root.Parent;
        var exe = OperatingSystem.IsWindows() ? "skarp.exe" : "skarp";
        var built = root is null ? null : new[] { "Debug", "Release" }.Select(c => Path.Combine(root.FullName, "src", "SkarpSharp.Cli", "bin", c, "net10.0"))
            .FirstOrDefault(d => File.Exists(Path.Combine(d, exe)) && File.Exists(Path.Combine(d, "ModelContextProtocol.Core.dll")));
        if (built is null) return null;
        foreach (var f in Directory.GetFiles(built)) File.Copy(f, Path.Combine(_temp, Path.GetFileName(f)), overwrite: true);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "SkarpSharp.Core.dll"), Path.Combine(_temp, "SkarpSharp.Core.dll"), overwrite: true);
        return Path.Combine(_temp, exe);
    }

    /// <summary>One server process: requests in, responses matched by id, every standard output line checked.</summary>
    sealed class Server : IAsyncDisposable
    {
        readonly Process _p;
        readonly MemoryStream _raw = new();
        readonly List<string> _bad = [];
        readonly Dictionary<int, TaskCompletionSource<JsonObject>> _waiting = [];
        readonly Task _reader;
        readonly Task<string> _stderr;
        readonly JsonObject? _meta;
        int _next = 1;

        public Server(string exe, string cwd, string home, bool july, IReadOnlyDictionary<string, string?>? environment = null)
        {
            var psi = new ProcessStartInfo(exe, "mcp")
            {
                WorkingDirectory = cwd, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            psi.Environment["SKARP_HOME"] = home;
            psi.Environment["SKARP_NO_USER_CONFIG"] = "1";
            psi.Environment.Remove("SKARP_CONFIG");
            foreach (var (k, v) in environment ?? new Dictionary<string, string?>()) psi.Environment[k] = v;
            _p = Process.Start(psi)!;
            _stderr = _p.StandardError.ReadToEndAsync();
            _reader = Task.Run(ReadAsync);
            if (july)
                _meta = new JsonObject
                {
                    ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                    ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "skarp-tests", ["version"] = "1.0" },
                    ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
                };
        }

        public List<string> Bad => _bad;
        public int Lines { get; private set; }

        async Task ReadAsync()
        {
            var stream = _p.StandardOutput.BaseStream;
            var buffer = new byte[65536];
            int n;
            while ((n = await stream.ReadAsync(buffer)) > 0)
            {
                lock (_raw) _raw.Write(buffer, 0, n);
                Drain();
            }
        }

        /// <summary>Takes every complete line from the raw bytes and checks it.</summary>
        void Drain()
        {
            while (true)
            {
                string line;
                lock (_raw)
                {
                    var bytes = _raw.ToArray();
                    int nl = Array.IndexOf(bytes, (byte)'\n');
                    if (nl < 0) return;
                    line = Encoding.UTF8.GetString(bytes, 0, nl);
                    _raw.SetLength(0);
                    _raw.Write(bytes, nl + 1, bytes.Length - nl - 1);
                }
                Lines++;
                JsonObject? msg = null;
                try { msg = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { }
                if (msg is null || msg["jsonrpc"]?.GetValue<string>() != "2.0") { lock (_bad) _bad.Add(line); continue; }
                if (msg["id"] is JsonValue id && id.TryGetValue<int>(out var i))
                    lock (_waiting) if (_waiting.Remove(i, out var w)) w.SetResult(msg);
            }
        }

        public long Trailing { get { lock (_raw) return _raw.Length; } }

        public async Task<JsonObject> Request(string method, JsonObject? parameters = null)
        {
            int id = _next++;
            var done = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_waiting) _waiting[id] = done;
            parameters ??= [];
            if (_meta is not null) parameters["_meta"] = _meta.DeepClone();
            await Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(120));
        }

        public Task Notify(string method) => Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = new JsonObject() });

        async Task Send(JsonObject message)
        {
            await _p.StandardInput.WriteAsync(message.ToJsonString() + "\n");
            await _p.StandardInput.FlushAsync();
        }

        /// <summary>Closes standard input and waits for the exit; the exit code and standard error.</summary>
        public async Task<(int Exit, string Error)> Close()
        {
            _p.StandardInput.Close();
            await _p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await _reader;
            return (_p.ExitCode, await _stderr);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_p.HasExited) { try { _p.Kill(true); } catch (InvalidOperationException) { } await _p.WaitForExitAsync(); }
            _p.Dispose();
        }
    }

    static JsonObject Call(string tool, JsonObject arguments) => new() { ["name"] = tool, ["arguments"] = arguments };

    static string Text(JsonObject response) => response["result"]!["content"]![0]!["text"]!.GetValue<string>();

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Stdio_carries_only_protocol_messages(bool july)
    {
        if (Exe() is not { } exe) { Skip.Test("the CLI project is not built (dotnet build src/SkarpSharp.Cli)"); return; }
        // A git repository isolated from the machine's configuration: check and find_worst_code run git while the
        // protocol runs over standard output.
        using var repo = new History.SyntheticRepo();
        repo.Write("Bad.cs", Class("Bad", Method("Huge", 72)));
        repo.Commit("add", DateTimeOffset.UtcNow.AddDays(-1));
        repo.Write("Bad.cs", Class("Bad", Method("Huge", 75)));
        await using var server = new Server(exe, repo.Root, Path.Combine(_temp, "home"), july, repo.Environment);
        JsonObject first;
        if (july) first = await server.Request("server/discover");
        else
        {
            first = await server.Request("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-11-25", ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "skarp-tests", ["version"] = "1.0" },
            });
            await server.Notify("notifications/initialized");
        }
        var result = first["result"]!.AsObject();
        if (july) await Assert.That(result["supportedVersions"]!.ToJsonString()).Contains("2026-07-28");
        else
        {
            await Assert.That(result["serverInfo"]!["name"]!.GetValue<string>()).IsEqualTo("skarp");
            await Assert.That(result["protocolVersion"]!.GetValue<string>()).IsEqualTo("2025-11-25");
            await Assert.That(result["instructions"]!.GetValue<string>()).StartsWith("Skarp measures C# code health");
            // No tools.listChanged: the list never changes.
            await Assert.That(result["capabilities"]!["tools"]!.ToJsonString()).IsEqualTo("{}");
        }
        var list = await server.Request("tools/list");
        var names = list["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        await Assert.That(names).IsEquivalentTo(new[] { "find_worst_code", "check", "findings", "explain" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        var worst = await server.Request("tools/call", Call("find_worst_code", new JsonObject { ["count"] = 5 }));
        await Assert.That(Text(worst)).StartsWith("Skarp: worst 1 of 1 files with findings in . (syntax tier), sorted by health\n");
        var capped = await server.Request("tools/call", Call("find_worst_code", new JsonObject { ["count"] = 30 }));
        await Assert.That(capped["result"]!["isError"]!.GetValue<bool>()).IsTrue();
        await Assert.That(Text(capped)).EndsWith("count must be 1 to 25 (got 30)");
        var explain = await server.Request("tools/call", Call("explain", new JsonObject { ["rule"] = "long-method" }));
        await Assert.That(Text(explain)).StartsWith("long-method: Long method");
        var check = await server.Request("tools/call", Call("check", new JsonObject()));
        await Assert.That(Text(check)).StartsWith("Skarp: 1 changed C# file checked against the last commit: 1 new or worse in 1 file\n");
        var findings = await server.Request("tools/call", Call("findings", new JsonObject { ["file"] = "Bad.cs" }));
        await Assert.That(Text(findings)).StartsWith("Skarp: findings 1 to ");
        var unknown = await server.Request("tools/call", Call("findings", new JsonObject { ["id"] = "0000" }));
        await Assert.That(unknown["result"]!["isError"]!.GetValue<bool>()).IsTrue();
        await Assert.That(Text(unknown)).Contains("no finding with id 0000 in the saved run of ");
        var (exit, error) = await server.Close();
        await Assert.That(exit).IsEqualTo(0).Because(error);
        await Assert.That(server.Bad).IsEmpty().Because(string.Join("\n", server.Bad));
        await Assert.That(server.Trailing).IsEqualTo(0);
        await Assert.That(server.Lines).IsEqualTo(8);
        await Assert.That(error).Contains("[skarp mcp +");
    }

    [Test]
    public async Task Help_and_a_usage_error()
    {
        if (Exe() is not { } exe) { Skip.Test("the CLI project is not built (dotnet build src/SkarpSharp.Cli)"); return; }
        static (int, string, string) Run(string exe, string arg)
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
            psi.ArgumentList.Add("mcp");
            psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var err = p.StandardError.ReadToEndAsync();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, output, err.Result);
        }
        var (exit, output, _) = Run(exe, "--help");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("(experimental)");
        var (bad, _, error) = Run(exe, "--port");
        await Assert.That(bad).IsEqualTo(2);
        await Assert.That(error).Contains("skarp mcp: unknown argument '--port'; it takes none");
    }
}
