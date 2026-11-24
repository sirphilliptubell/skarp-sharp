using System.Text;
using System.Text.Json;
using SkarpSharp.Mcp;
using SkarpSharp.Tests.Agent;

namespace SkarpSharp.Tests.Mcp;

/// <summary>
/// A temp folder outside any git work tree with generated C# files, and the MCP tools on it with an
/// <see cref="McpHost"/> that reads no machine settings (the module initializer sets none), no real home and no log.
/// </summary>
internal sealed class McpFixture : IDisposable
{
    readonly AgentHost _host = new();

    public McpFixture(string name = "repo") => Root = _host.Folder(name);

    public string Root { get; }
    public AgentHost Agent => _host;
    public McpHost Host => new() { WorkingDirectory = Root };

    public void Dispose() => _host.Dispose();

    public string Write(string relative, string text)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>A class with one method of <paramref name="ifs"/> sequential ifs (cognitive complexity = ifs; about
    /// ifs + 6 physical lines).</summary>
    public static string Method(string name, int ifs)
    {
        var sb = new StringBuilder();
        sb.Append("    public int ").Append(name).AppendLine("(int x)").AppendLine("    {").AppendLine("        int y = 0;");
        for (int i = 0; i < ifs; i++) sb.Append("        if (x > ").Append(i).AppendLine(") y++;");
        sb.AppendLine("        return y;").AppendLine("    }");
        return sb.ToString();
    }

    /// <summary>A file with one class holding the given methods.</summary>
    public static string Class(string name, params string[] methods) =>
        $"namespace Sample;\n\npublic class {name}\n{{\n{string.Join("\n", methods)}}}\n";

    public static Dictionary<string, JsonElement> Json(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    /// <summary>find_worst_code with JSON arguments.</summary>
    public Task<string> Worst(string json, CancellationToken ct = default) => new FindWorstCodeTool(Host).CallAsync(Json(json), ct);
}

