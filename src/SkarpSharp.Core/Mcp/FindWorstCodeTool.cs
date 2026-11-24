using SkarpSharp.Configuration;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SkarpSharp.Ranking;

namespace SkarpSharp.Mcp;

/// <summary>
/// The tool <c>find_worst_code</c>, a hand-written tool with its JSON schema as a
/// string (minimum, maximum and enum stated). It ranks; it never tells the reader to fix anything.
/// </summary>
public sealed class FindWorstCodeTool(McpHost host) : McpServerTool
{
    public const string Name = "find_worst_code";
    public const int MaxCount = 25, DefaultCount = 10, MaxOffset = 10_000;
    static readonly string[] Parameters = ["path", "count", "depth", "sort", "unit", "offset"];

    static string Quoted(IEnumerable<string> values) => string.Join(",", values.Select(v => $"\"{v}\""));

    static readonly string Schema = $$$"""
        {"type":"object","properties":{
        "path":{"type":"string","description":"A folder or one C# file, absolute or relative to the server's working folder. Default: the working folder."},
        "count":{"type":"integer","minimum":1,"maximum":{{{MaxCount}}},"default":{{{DefaultCount}}},"description":"How many entries to list, 1 to {{{MaxCount}}}. Default {{{DefaultCount}}}."},
        "depth":{"type":"string","enum":["syntax","semantic"],"default":"syntax","description":"syntax (default): fast. semantic: also class coupling, god classes, unused code and namespace cycles, from a design-time build of the solution (tens of seconds, writes obj/ as an IDE does, never restores); reuses the saved run when no file or setting changed."},
        "sort":{"type":"string","enum":["health","hotspot",{{{Quoted(RankChoice.SortableRules)}}}],"default":"health","description":"health (default): files with findings, lowest score first. hotspot: files with findings by commits in the git history window, then complexity; by health without git history. A rule id: by that rule's own measure, largest first, code under every threshold included."},
        "unit":{"type":"string","enum":["auto","files","methods"],"default":"auto","description":"auto (default): files for health, hotspot, large-type and large-file, methods for the other rules. files with a method rule ranks each file by its worst method. methods needs a method rule."},
        "offset":{"type":"integer","minimum":0,"maximum":{{{MaxOffset}}},"default":0,"description":"Entries to skip, for the next page; the answer's last lines give the next offset. Default 0."}},
        "additionalProperties":false}
        """;

    public const string Description =
        "Ranks the least healthy C# code under a folder or in one file, worst first, as text. Each file comes with its health " +
        "score (1 to 10, 10 = healthy), its band (healthy 9 or more, problematic, unhealthy below 4) and up to 3 findings with " +
        "ids; a closing line says how many listed entries are healthy, since a ranking always has a top. Use it to find what to " +
        "clean up or review first, the most active complex files (sort hotspot), or the longest or most complex methods (sort by " +
        "a rule). For files just edited use check. Limits: at most 25 entries per call (page with offset); test code and " +
        "excluded files are not ranked; depth syntax takes about a second for a few hundred files, depth semantic tens of " +
        "seconds and one run per repository at a time. Saves the run in .skarp/last-run/.";

    public override Tool ProtocolTool { get; } = new()
    {
        Name = Name,
        Description = Description,
        InputSchema = JsonDocument.Parse(Schema).RootElement.Clone(),
        Annotations = new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = false, IdempotentHint = true, OpenWorldHint = false },
    };

    public override IReadOnlyList<object> Metadata { get; } = [];

    /// <summary>Reads and checks the arguments.</summary>
    /// <exception cref="McpException">An unknown argument, a value out of range, or a sort and unit that do not fit.</exception>
    public static WorstCodeRequest Read(IDictionary<string, JsonElement>? arguments)
    {
        var a = new ToolArgs(Name, arguments, Parameters);
        var path = a.String("path") ?? ".";
        int count = a.Int("count", DefaultCount, 1, MaxCount);
        bool semantic = a.Choice("depth", "syntax", "syntax", "semantic") == "semantic";
        var (choice, error) = RankChoice.Resolve(a.String("sort"), a.String("unit"));
        int offset = a.Int("offset", 0, 0, MaxOffset);
        return new WorstCodeRequest(path, count, semantic, choice ?? throw new McpException(error!), offset);
    }

    /// <summary>The call without the protocol (tests).</summary>
    public Task<string> CallAsync(IDictionary<string, JsonElement>? arguments, CancellationToken ct) =>
        new WorstCodeService(host).RunAsync(Read(arguments), ct);

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default) =>
        new() { Content = [new TextContentBlock { Text = await CallAsync(request.Params?.Arguments, cancellationToken).ConfigureAwait(false) }] };
}
