using System.Text.Json.Nodes;

namespace SkarpSharp.Install;

/// <summary>What installing or removing the MCP entry did.</summary>
public enum McpChangeKind { None, Add, Update, Unchanged, Remove }

/// <summary>One change to the MCP entry, with the entry before and after.</summary>
public sealed record McpChange(McpChangeKind Kind, JsonObject? Old, JsonObject? New);

/// <summary>
/// Skarp's MCP server entry for Claude Code, <c>"skarp":
/// {"type":"stdio","command":&lt;skarp&gt;,"args":["mcp"]}</c>, in the <c>mcpServers</c> object of a scope, where Claude
/// Code keeps it: <c>.mcp.json</c> at the project root (project), <c>~/.claude.json</c> under
/// <c>projects["&lt;project folder&gt;"]</c> (local) or at its top (user). Merged: other servers and other keys stay as
/// they are; an existing <c>skarp</c> entry is updated in place (its other keys, such as <c>env</c>, are kept).
/// </summary>
public static class ClaudeMcp
{
    public const string ServerName = "skarp";
    const string Servers = "mcpServers", Projects = "projects";

    /// <summary>The entry Skarp writes for <paramref name="command"/> (see <see cref="Command"/>); with
    /// <paramref name="viaCmd"/> it runs through <c>cmd.exe /c</c> (<see cref="ClaudeHooks.CmdProgram"/>).</summary>
    public static JsonObject Entry(string command, bool viaCmd = false) => new()
    {
        ["type"] = "stdio",
        ["command"] = viaCmd ? ClaudeHooks.CmdProgram : Command(command),
        ["args"] = viaCmd ? new JsonArray("/c", Command(command), "mcp") : new JsonArray("mcp"),
    };

    /// <summary>The program of an entry, seeing through <c>cmd.exe /c &lt;program&gt; ...</c>.</summary>
    public static string ProgramOf(JsonObject entry)
    {
        var command = entry["command"]?.ToString() ?? "";
        if (ClaudeHooks.IsCmdProgram(command) && entry["args"] is JsonArray { Count: >= 2 } a && a[0]?.ToString().Equals("/c", StringComparison.OrdinalIgnoreCase) == true)
            return a[1]?.ToString() ?? "";
        return command;
    }

    /// <summary>The project folder variable with a default. Claude Code sets <c>CLAUDE_PROJECT_DIR</c> in a hook's and an
    /// MCP server's environment, but not in its own, so in an MCP entry's <c>command</c> it expands only with a default.</summary>
    public const string ProjectDirWithDefault = "${CLAUDE_PROJECT_DIR:-.}";

    /// <summary>The command as an MCP entry holds it: <c>${CLAUDE_PROJECT_DIR}</c> (as hook entries write it) gets the
    /// default <c>:-.</c>.</summary>
    public static string Command(string command) => command.Replace(ClaudeHooks.ProjectDirVariable, ProjectDirWithDefault, StringComparison.Ordinal);

    /// <summary>The command with the project folder variable in the form <see cref="InstallSeams.Locate"/> expands.</summary>
    public static string Locatable(string command) => command.Replace(ProjectDirWithDefault, ClaudeHooks.ProjectDirVariable, StringComparison.Ordinal);

    /// <summary><c>skarp mcp</c> in words: <c>stdio: skarp mcp</c>.</summary>
    public static string Describe(JsonObject entry)
    {
        var args = entry["args"] is JsonArray a ? string.Join(" ", a.Select(x => x?.ToString() ?? "")) : "";
        return $"{entry["type"]?.ToString() ?? "stdio"}: {entry["command"]?.ToString() ?? "(no command)"}{(args.Length > 0 ? " " + args : "")}";
    }

    /// <summary>Skarp's entry at <paramref name="scope"/>, or null.</summary>
    /// <exception cref="HookShapeException">A key on the way is not an object.</exception>
    public static JsonObject? Find(JsonObject root, ClaudeScope scope, string projectDir) =>
        ServersOf(root, scope, projectDir, create: false)?[ServerName] is JsonNode n
            ? n as JsonObject ?? throw new HookShapeException($"{Servers}.{ServerName} is not an object")
            : null;

    /// <summary>Adds or updates Skarp's entry; returns the changed copy of <paramref name="root"/> and what changed.</summary>
    /// <exception cref="HookShapeException">A key on the way is not an object.</exception>
    public static (JsonObject Root, McpChange Change) Install(JsonObject root, ClaudeScope scope, string projectDir, string command, bool viaCmd = false)
    {
        var copy = (JsonObject)root.DeepClone();
        var servers = ServersOf(copy, scope, projectDir, create: true)!;
        var want = Entry(command, viaCmd);
        if (Find(copy, scope, projectDir) is not { } old)
        {
            servers[ServerName] = want;
            return (copy, new McpChange(McpChangeKind.Add, null, want));
        }
        var updated = (JsonObject)old.DeepClone();
        foreach (var (key, value) in want) updated[key] = value!.DeepClone();
        if (JsonNode.DeepEquals(old, updated)) return (copy, new McpChange(McpChangeKind.Unchanged, old, old));
        servers[ServerName] = updated;
        return (copy, new McpChange(McpChangeKind.Update, (JsonObject)old.DeepClone(), updated));
    }

    /// <summary>Removes Skarp's entry (an <c>mcpServers</c> left empty stays: Claude Code writes it so too).</summary>
    /// <exception cref="HookShapeException">A key on the way is not an object.</exception>
    public static (JsonObject Root, McpChange Change) Remove(JsonObject root, ClaudeScope scope, string projectDir)
    {
        var copy = (JsonObject)root.DeepClone();
        if (Find(copy, scope, projectDir) is not { } old) return (copy, new McpChange(McpChangeKind.None, null, null));
        ServersOf(copy, scope, projectDir, create: false)!.Remove(ServerName);
        return (copy, new McpChange(McpChangeKind.Remove, (JsonObject)old.DeepClone(), null));
    }

    /// <summary>The <c>mcpServers</c> object of a scope (created on the way with <paramref name="create"/>), or null.</summary>
    static JsonObject? ServersOf(JsonObject root, ClaudeScope scope, string projectDir, bool create)
    {
        var parent = root;
        if (scope == ClaudeScope.Local)
        {
            if (Child(root, Projects, create) is not { } projects) return null;
            var key = ProjectKey(projects, projectDir);
            if (Child(projects, key, create) is not { } project) return null;
            parent = project;
        }
        return Child(parent, Servers, create);
    }

    static JsonObject? Child(JsonObject parent, string key, bool create)
    {
        if (parent[key] is JsonNode node) return node as JsonObject ?? throw new HookShapeException($"{key} is not an object");
        if (!create) return null;
        var made = new JsonObject();
        parent[key] = made;
        return made;
    }

    /// <summary>The key of the project folder in <c>projects</c>: the folder with forward slashes, as Claude Code writes
    /// it; an existing key that differs only in case or slashes is used as it is (on Windows).</summary>
    static string ProjectKey(JsonObject projects, string projectDir)
    {
        var key = Path.GetFullPath(projectDir).TrimEnd('\\', '/').Replace('\\', '/');
        if (!OperatingSystem.IsWindows()) return key;
        foreach (var (existing, _) in projects)
            if (string.Equals(existing.Replace('\\', '/').TrimEnd('/'), key, StringComparison.OrdinalIgnoreCase)) return existing;
        return key;
    }
}
