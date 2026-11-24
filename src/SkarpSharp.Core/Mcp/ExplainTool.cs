using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SkarpSharp.Agent;
using SkarpSharp.Configuration;
using SkarpSharp.Findings;
using SkarpSharp.Reporting;

namespace SkarpSharp.Mcp;

/// <summary>
/// The tool <c>explain</c>. Without a rule, the rule ids with one line each; with a
/// rule, the text of <c>skarp explain &lt;rule&gt;</c> with only the thresholds in force for the repository at
/// <c>path</c>, unless <c>all_presets</c>. Reads settings only.
/// </summary>
public sealed class ExplainTool(McpHost host) : McpServerTool
{
    public const string Name = "explain";
    static readonly string[] Parameters = ["rule", "all_presets", "path"];
    /// <summary>The width of the id column of the rule list (the longest id).</summary>
    static readonly int IdWidth = Rules.Max(r => r.Id.Length);
    static IEnumerable<RuleInfo> Rules => RuleCatalog.Known;

    static readonly string Schema = $$$"""
        {"type":"object","properties":{
        "rule":{"type":"string","description":"A rule id as finding lines show it. Leave it out to list every rule with one line."},
        "all_presets":{"type":"boolean","default":false,"description":"false (default): only the thresholds in force for path. true: the thresholds of every preset."},
        "path":{"type":"string","description":"A folder or file whose settings (.skarp.json, found as for an analysis) decide the thresholds in force, absolute or relative to the server's working folder. Default: the working folder."}},
        "additionalProperties":false}
        """;

    public const string Description =
        "Explains one Skarp rule: what it measures, why it matters, how to improve code it flags, and the thresholds in force " +
        "for the repository at path. Without rule, lists every rule id with one line. Use it when a finding's rule is " +
        "unfamiliar, or before changing code a rule flagged. Reads settings only; analyzes nothing.";

    public override Tool ProtocolTool { get; } = new()
    {
        Name = Name,
        Description = Description,
        InputSchema = JsonDocument.Parse(Schema).RootElement.Clone(),
        Annotations = new ToolAnnotations { ReadOnlyHint = true, IdempotentHint = true, OpenWorldHint = false },
    };

    public override IReadOnlyList<object> Metadata { get; } = [];

    /// <summary>The call without the protocol (tests).</summary>
    /// <exception cref="McpException">An unknown argument or rule, a missing path, or settings that cannot be read.</exception>
    public string Call(IDictionary<string, JsonElement>? arguments)
    {
        var a = new ToolArgs(Name, arguments, Parameters);
        var id = a.String("rule");
        bool all = a.Bool("all_presets", false);
        var path = a.String("path") ?? ".";
        var w = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        if (id is null)
        {
            w.WriteLine("Skarp rules (explain rule=<id>: what it measures, why it matters, how to improve, thresholds in force):");
            foreach (var r in Rules) w.WriteLine($"  {r.Id.PadRight(IdWidth)} {r.Short}");
            return w.ToString();
        }
        var rule = RuleCatalog.Find(id) ?? throw new McpException($"unknown rule '{id}'; rules: {string.Join(", ", Rules.Select(r => r.Id))}");
        HumanReport.WriteExplain(w, rule, all ? null : InForce(rule.Id, path));
        return w.ToString();
    }

    /// <summary>The rule's thresholds for the file or folder at <paramref name="path"/>.</summary>
    ActiveRule? InForce(string rule, string path)
    {
        var full = Path.GetFullPath(Path.Combine(host.Cwd, path));
        bool isFile = File.Exists(full);
        if (!isFile && !Directory.Exists(full)) throw new McpException($"path not found: {full}");
        var root = FileCheck.RootOfFolder(isFile ? Path.GetDirectoryName(full)! : full);
        var parsed = CommandLine.Parse(["analyze", root], host.SettingsLocations);
        var o = parsed.Options ?? throw new McpException(parsed.Error ?? "the settings could not be read");
        var rules = o.PathRules();
        var settings = isFile ? rules.For(FileCheck.Relative(root, full), testCode: false) : rules.Base;
        return settings.Rules.GetValueOrDefault(rule);
    }

    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = Call(request.Params?.Arguments) }] });
}
