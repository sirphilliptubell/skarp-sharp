using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SkarpSharp.Mcp;

/// <summary>What <c>check</c> was asked for, after the arguments were read.</summary>
public sealed record CheckRequest(IReadOnlyList<string> Files, string Path, bool Notices);

/// <summary>
/// The tool <c>check</c>: the changed (or given) C# files against the baseline or
/// else the last commit, writing nothing; with a solution among the files, also the semantic tier on it against the
/// semantic baseline (saved, reused when fresh, one run per repository at a time).
/// </summary>
public sealed class CheckTool(McpHost host) : McpServerTool
{
    public const string Name = "check";
    public const int MaxFiles = 200;
    static readonly string[] Parameters = ["files", "path", "include_notices"];

    static readonly string Schema = $$$"""
        {"type":"object","properties":{
        "files":{"type":"array","items":{"type":"string"},"maxItems":{{{MaxFiles}}},"description":"Up to {{{MaxFiles}}} paths, absolute or relative to the server's working folder. The C# files in it are checked; without one, the changed C# files are. One .sln or .slnx in it also runs the rules that need a solution."},
        "path":{"type":"string","description":"When files holds no C# file: the folder whose repository is looked at, and only changed files under it are checked. Default: the working folder."},
        "include_notices":{"type":"boolean","default":false,"description":"Also list notices, not only warnings (notices count in the health scores either way). Default false."}},
        "additionalProperties":false}
        """;

    public const string Description =
        "Checks changed C# files and says what got worse or better, as text: each file's health before and after (1 to 10, " +
        "10 = healthy) and one line per finding that is new, worse, better or fixed, with ids. Without C# files in files: " +
        "the C# files that differ from the last commit, staged or not, plus untracked ones. Compared with the baseline when " +
        "the repository has one (then new and worse only), else with each file at the last commit; a file not in git shows " +
        "all its warnings. Use it before finishing a task that edited C# files, or after edits made through a shell. Where " +
        "Skarp notes already appear after each edit, a call per edit adds nothing. A .sln or .slnx in files also runs the " +
        "rules that need a solution (class coupling, god classes, unused code, namespace cycles) against their baseline: " +
        "tens of seconds, writes obj/ as an IDE does, never restores, one run per repository at a time, saved in " +
        ".skarp/last-run/. Limits: at most 15 finding lines per part; single files cannot show duplicate code, change " +
        "activity or test coverage. Changes no source file, baseline or setting.";

    public override Tool ProtocolTool { get; } = new()
    {
        Name = Name,
        Description = Description,
        InputSchema = JsonDocument.Parse(Schema).RootElement.Clone(),
        Annotations = new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = false, IdempotentHint = true, OpenWorldHint = false },
    };

    public override IReadOnlyList<object> Metadata { get; } = [];

    /// <summary>Reads and checks the arguments.</summary>
    /// <exception cref="ModelContextProtocol.McpException">An unknown argument, a value of the wrong type, or too many files.</exception>
    public static CheckRequest Read(IDictionary<string, JsonElement>? arguments)
    {
        var a = new ToolArgs(Name, arguments, Parameters);
        return new CheckRequest(a.Strings("files", MaxFiles) ?? [], a.String("path") ?? ".", a.Bool("include_notices", false));
    }

    /// <summary>The call without the protocol (tests).</summary>
    public Task<string> CallAsync(IDictionary<string, JsonElement>? arguments, CancellationToken ct) =>
        new CheckService(host).RunAsync(Read(arguments), ct);

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default) =>
        new() { Content = [new TextContentBlock { Text = await CallAsync(request.Params?.Arguments, cancellationToken).ConfigureAwait(false) }] };
}
