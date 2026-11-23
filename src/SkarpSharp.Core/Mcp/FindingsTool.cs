using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SkarpSharp.Agent;
using SkarpSharp.Findings;

namespace SkarpSharp.Mcp;

/// <summary>What <c>findings</c> was asked for, after the arguments were read.</summary>
public sealed record FindingsRequest(string? Id, string? File, string? Rule, bool NewOnly, bool Notices, int Limit, int Offset, string Path);

/// <summary>
/// The tool <c>findings</c>: the saved run (<c>.skarp/last-run/</c> plus newer per-file
/// records, as <c>skarp show</c> reads them) listed by file, rule and <c>new_only</c> with paging, or one finding in full
/// ending with its rule's "How to improve" paragraph. Never analyzes and writes nothing.
/// </summary>
public sealed partial class FindingsTool(McpHost host) : McpServerTool
{
    public const string Name = "findings";
    public const int DefaultLimit = 30, MaxLimit = 100, MaxOffset = 100_000;
    static readonly string[] Parameters = ["id", "file", "rule", "new_only", "include_notices", "limit", "offset", "path"];

    static readonly string Schema = $$$"""
        {"type":"object","properties":{
        "id":{"type":"string","description":"The 8 characters in brackets at the end of a finding line, or the first 4 or more. One match: that finding in full. Does not combine with file, rule or new_only."},
        "file":{"type":"string","description":"Only findings in this file: absolute, relative to the server's working folder, or relative to the analyzed folder."},
        "rule":{"type":"string","description":"Only findings of this rule: a rule id as finding lines show it."},
        "new_only":{"type":"boolean","default":false,"description":"Only findings new or worse than the baseline the saved run was compared with. Default false."},
        "include_notices":{"type":"boolean","default":false,"description":"Also list notices, not only warnings. Default false."},
        "limit":{"type":"integer","minimum":1,"maximum":{{{MaxLimit}}},"default":{{{DefaultLimit}}},"description":"How many findings to list, 1 to {{{MaxLimit}}}. Default {{{DefaultLimit}}}."},
        "offset":{"type":"integer","minimum":0,"maximum":{{{MaxOffset}}},"default":0,"description":"Findings to skip, for the next page; the answer's last lines give the next offset. Default 0."},
        "path":{"type":"string","description":"Where to look for the saved run, from this folder up to the repository root, absolute or relative to the server's working folder. Default: the working folder."}},
        "additionalProperties":false}
        """;

    public const string Description =
        "Lists findings from the saved run without analyzing again, or shows one in full, as text. With id (the characters " +
        "in brackets on a finding line, or the first 4 or more): its measure and limit, its baseline state, the member's " +
        "metrics and how to improve it. Without id: the findings that match file, rule and new_only, grouped by file, 30 per " +
        "call (at most 100; page with offset). Use it to read the rest of a file's findings after a ranking, or one finding " +
        "before changing its code. The saved run comes from find_worst_code, from check with a solution, or from skarp " +
        "analyze; when files changed since, a line says how many. Never analyzes: without a saved run it says so. Reads only.";

    public override Tool ProtocolTool { get; } = new()
    {
        Name = Name,
        Description = Description,
        InputSchema = JsonDocument.Parse(Schema).RootElement.Clone(),
        Annotations = new ToolAnnotations { ReadOnlyHint = true, IdempotentHint = true, OpenWorldHint = false },
    };

    public override IReadOnlyList<object> Metadata { get; } = [];

    /// <summary>Reads and checks the arguments.</summary>
    /// <exception cref="McpException">An unknown argument, a value out of range, an id that is not one, an unknown rule, or
    /// an id together with a filter.</exception>
    public static FindingsRequest Read(IDictionary<string, JsonElement>? arguments)
    {
        var a = new ToolArgs(Name, arguments, Parameters);
        var id = Id(a.String("id"));
        var rule = a.String("rule");
        if (rule is not null && RuleCatalog.Find(rule) is null)
            throw new McpException($"unknown rule '{rule}'; rules: {string.Join(", ", RuleCatalog.Known.Select(r => r.Id))}");
        var request = new FindingsRequest(id, a.String("file"), rule, a.Bool("new_only", false), a.Bool("include_notices", false),
            a.Int("limit", DefaultLimit, 1, MaxLimit), a.Int("offset", 0, 0, MaxOffset), a.String("path") ?? ".");
        if (id is not null && (request.File is not null || rule is not null || request.NewOnly))
            throw new McpException("id shows one finding; it does not combine with file, rule or new_only");
        return request;
    }

    /// <summary>An id as the handles are printed (lowercase), or null.</summary>
    /// <exception cref="McpException">Not 4 to 8 hexadecimal characters.</exception>
    static string? Id(string? given)
    {
        var id = given?.Trim().ToLowerInvariant();
        if (id is null || (id.Length is >= MinId and <= MaxId && id.All(char.IsAsciiHexDigit))) return id;
        throw new McpException($"id must be {MinId} to {MaxId} hexadecimal characters, as in the brackets at the end of a finding line (got '{id}')");
    }

    const int MinId = 4, MaxId = 8;

    /// <summary>The call without the protocol (tests).</summary>
    public string Call(IDictionary<string, JsonElement>? arguments)
    {
        var q = Read(arguments);
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(host.Cwd, q.Path));
        bool isFile = System.IO.File.Exists(full);
        if (!isFile && !Directory.Exists(full)) throw new McpException($"path not found: {full}");
        var folder = isFile ? System.IO.Path.GetDirectoryName(full)! : full;
        var root = LastRun.FindRoot(folder) ?? throw NoSavedRun(folder);
        var data = Load(root);
        if (data.Header is null && data.Entries.Count == 0) throw NoSavedRun(folder);
        var answer = new FindingsAnswer(host, data);
        var text = (q.Id is { } id ? answer.Find(id) : answer.List(q)).ToString();
        host.Write($"findings {(q.Id is null ? $"list offset {q.Offset} limit {q.Limit}" : "id " + q.Id)} in {root}: {text.Length} characters");
        return text;
    }

    static McpException NoSavedRun(string folder) => new($"no saved run in {FileCheck.RootOfFolder(folder)}; find_worst_code saves one");

    /// <summary>The saved run and newer records; one retry when a read meets a run being replaced.</summary>
    const int Attempts = 2, RetryDelayMs = 50;

    static ShowData Load(string root)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return Join.FromLastRun(root, null); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                if (attempt == Attempts) throw new McpException($"the saved run in {root} could not be read: {ex.Message}");
                Thread.Sleep(RetryDelayMs);
            }
        }
    }

    /// <summary>The rule's "How to improve" paragraph as the detail ends with it: its pointers to the command line become
    /// the tool that answers them; null when the rule has none.</summary>
    public static string? HowToImprove(RuleInfo rule) =>
        RuleCatalog.HowToImprove(rule) is { } how ? ExplainCommand().Replace(how, "explain rule=$1") : null;

    [GeneratedRegex(@"skarp explain ([a-z][a-z-]*)")]
    private static partial Regex ExplainCommand();

    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = Call(request.Params?.Arguments) }] });
}
