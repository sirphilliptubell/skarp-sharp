using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SkarpSharp.Mcp;

/// <summary>
/// <c>skarp mcp</c>, Skarp's MCP server over standard input
/// and output (experimental). It runs on <c>ModelContextProtocol.Core</c> without hosting; <c>ProtocolVersion</c> stays
/// null so both protocol revisions work (2025-11-25 <c>initialize</c>, 2026-07-28 <c>server/discover</c>). The tools are
/// served by list and call handlers rather than the SDK's tool collection, which always advertises
/// <c>tools.listChanged</c> for a list that never changes. Standard output carries protocol messages only; log lines go to
/// standard error. No network, no telemetry.
/// </summary>
public static class McpCommand
{
    public const string Usage = """
        skarp mcp: Skarp's MCP server for AI agents, over standard input and output (experimental). An MCP client
        starts it and talks to it until the client closes standard input.

        Usage:
          skarp mcp

        Tools, in this order:
          find_worst_code  the least healthy C# files or methods under a folder, worst first, by health, change
                           activity or one rule's measure (saves the run in .skarp/last-run/)
          check            what the changed C# files (or the files given) made worse or better, against the baseline
                           or else the last commit; with a .sln or .slnx also the rules that need a solution
          findings         the findings of the saved run, listed by file and rule, or one in full with how to
                           improve it; never analyzes
          explain          what a rule measures, why it matters, how to improve code it flags, the thresholds in force
        Paths are absolute or relative to the working folder the client starts the server in, which is also the
        default. Standard output carries protocol messages only; log lines go to standard error. No network, no
        telemetry.

        skarp install claude --mcp adds the server to Claude Code (skarp install --help); docs/check-and-agents.md has the entries
        for other clients.
        """;

    /// <summary>What Skarp is, one line per tool, the one instruction (call check
    /// before finishing), the bands; under 1,200 characters.</summary>
    public const string Instructions =
        "Skarp measures C# code health with Roslyn. Each file gets a score from 1 to 10, where 10 is healthy, lowered by its " +
        "findings: places worth a look such as complex, long or deeply nested methods, large types, long parameter lists and " +
        "duplicated code (with a solution also class coupling, god classes and unused code).\n" +
        "find_worst_code ranks the least healthy files or methods under a folder, worst first: use it to find what to clean " +
        "up or review first.\n" +
        "check says what the changed C# files made worse or better, against the baseline or else the last commit. Call it on " +
        "the C# files you edited before you finish a task. Where Skarp notes already appear after each edit, they cover that " +
        "edit; check then covers changes made another way.\n" +
        "findings lists the findings of the saved run, or shows one in full by its id with how to improve it.\n" +
        "explain describes a rule: what it measures, why it matters, how to improve code it flags and the thresholds in " +
        "force.\n" +
        "Bands: 9 or more is healthy, 4 to below 9 problematic, below 4 unhealthy. A healthy file is not worth reworking just " +
        "because it appears in a ranking.";

    /// <summary>The tools in their fixed order.</summary>
    public static IReadOnlyList<McpServerTool> Tools(McpHost host) => [new FindWorstCodeTool(host), new CheckTool(host), new FindingsTool(host), new ExplainTool(host)];

    /// <summary>The server's options: name, version, instructions and the four tools.</summary>
    public static McpServerOptions Options(McpHost host)
    {
        var tools = Tools(host);
        return new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "skarp", Version = Commands.SkarpCommand.ToolVersion },
            ServerInstructions = Instructions,
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [.. tools.Select(t => t.ProtocolTool)] }),
                CallToolHandler = (request, ct) => tools.FirstOrDefault(t => t.ProtocolTool.Name == request.Params?.Name) is { } tool
                    ? tool.InvokeAsync(request, ct)
                    : throw new McpProtocolException($"unknown tool '{request.Params?.Name}'; tools: {string.Join(", ", tools.Select(t => t.ProtocolTool.Name))}", McpErrorCode.InvalidParams),
            },
        };
    }

    /// <summary><c>skarp mcp</c> (<paramref name="args"/> start with <c>mcp</c>): 0 when the client closed the connection,
    /// 2 on a usage error.</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        // First: a stray Console.Out write anywhere becomes a log line instead of a protocol break.
        Console.SetOut(Console.Error);
        if (args.Count > 1)
        {
            bool help = args[1] is "-h" or "--help";
            using var w = new StreamWriter(help ? Console.OpenStandardOutput() : Console.OpenStandardError());
            if (!help) w.WriteLine($"skarp mcp: unknown argument '{args[1]}'; it takes none (skarp mcp --help)");
            else w.Write(Usage.ReplaceLineEndings() + Environment.NewLine);
            return help ? 0 : 2;
        }
        var host = new McpHost { Log = Console.Error };
        host.Write($"start, working folder {host.Cwd}");
        var options = Options(host);
        await using var server = McpServer.Create(new StdioServerTransport(options), options);
        await server.RunAsync().ConfigureAwait(false);
        host.Write("stop");
        return 0;
    }
}
