using System.Text.Json.Nodes;
using SkarpSharp.Setup;

namespace SkarpSharp.Install;

/// <summary>
/// The MCP part of <c>skarp install claude</c> and <c>--remove</c> on
/// one scope's file (<see cref="InstallSeams.McpFileOf"/>). Reads it (strict JSON only), merges or removes Skarp's entry
/// (<see cref="ClaudeMcp"/>), notes the entries at the other scopes, prints the preview, asks at a terminal, then writes a
/// <c>.bak</c> and the file atomically. Exit codes as <see cref="Commands.InstallCommand"/>.
/// </summary>
public sealed class McpInstaller(InstallRequest request, TextWriter output, TextWriter error, InstallSeams seams, string projectDir)
{
    const int Refused = 2;
    readonly string _path = seams.McpFileOf(request.Scope, projectDir);
    /// <summary>The entry runs through <c>cmd.exe /c</c> (Windows, a <c>.cmd</c> shim, not project scope).</summary>
    readonly bool _viaCmd = seams.UsesCmdWrapper(request.Spec.Command, request.Spec.CommandGiven, request.Scope, projectDir);
    string Title(ClaudeFile file, string missing) => $"Skarp MCP server, {request.Scope.Name()} scope: {_path}{(file.Existed ? "" : missing)}";

    public int Install()
    {
        if (ClaudeInstaller.PortabilityError(request) is { } why) { error.WriteLine(why); return Refused; }
        var file = ClaudeFile.Read(_path);
        if (file.Error is not null) return Refuse(file.Error);
        JsonObject result;
        McpChange change;
        try { (result, change) = ClaudeMcp.Install(file.Root, request.Scope, projectDir, request.Spec.Command, _viaCmd); }
        catch (HookShapeException ex) { return Refuse(ex.Message); }
        Preview(request.Print ? error : output, file, change);
        if (change.Kind == McpChangeKind.Unchanged)
        {
            (request.Print ? error : output).WriteLine("Nothing to change: Skarp's MCP server is already installed.");
            if (request.Print) output.Write(file.Text);
            return 0;
        }
        var text = file.Render(result);
        if (request.Print) { output.Write(text); return 0; }
        if (!Save(text)) return Refused;
        if (!request.DryRun) output.WriteLine(Loaded);
        return 0;
    }

    string Loaded => "Claude Code starts MCP servers when a session starts: start a new session (or use /mcp) to load it" +
        (request.Scope == ClaudeScope.Project ? "; it asks once to approve a server from .mcp.json." : ".");

    /// <summary>The file, the change, the entries at the other scopes and a missing program.</summary>
    void Preview(TextWriter preview, ClaudeFile file, McpChange change)
    {
        preview.WriteLine(Title(file, " (new file)"));
        foreach (var line in ChangeLines(change)) preview.WriteLine(line);
        foreach (var note in Others()) preview.WriteLine(note);
        if (seams.Locate(request.Spec.Command, projectDir) is null)
            preview.WriteLine($"warning: {request.Spec.Command} not found{(request.Spec.CommandGiven ? "" : " on PATH")}: Claude Code will show the server as failed");
    }

    public int Remove()
    {
        var file = ClaudeFile.Read(_path);
        if (file.Error is not null)
        {
            error.WriteLine($"skarp install: {_path}: {file.Error}; Skarp does not edit it. Remove the \"{ClaudeMcp.ServerName}\" entry under mcpServers by hand; nothing written");
            return Refused;
        }
        JsonObject result;
        McpChange change;
        try { (result, change) = ClaudeMcp.Remove(file.Root, request.Scope, projectDir); }
        catch (HookShapeException ex) { error.WriteLine($"skarp install: {_path}: {ex.Message}; nothing written"); return Refused; }
        output.WriteLine(Title(file, " (no file)"));
        foreach (var line in ChangeLines(change)) output.WriteLine(line);
        foreach (var note in Others()) output.WriteLine(note);
        if (change.Kind == McpChangeKind.None) { output.WriteLine("Nothing to remove: no Skarp MCP server in this file."); return 0; }
        return Save(file.Render(result)) ? 0 : Refused;
    }

    static IEnumerable<string> ChangeLines(McpChange c) => c.Kind switch
    {
        McpChangeKind.Add => [$"  {ClaudeMcp.ServerName}: add {ClaudeMcp.Describe(c.New!)}"],
        McpChangeKind.Unchanged => [$"  {ClaudeMcp.ServerName}: already installed: {ClaudeMcp.Describe(c.New!)}"],
        McpChangeKind.Remove => [$"  {ClaudeMcp.ServerName}: remove {ClaudeMcp.Describe(c.Old!)}"],
        McpChangeKind.Update => [$"  {ClaudeMcp.ServerName}: update", $"    old: {ClaudeMcp.Describe(c.Old!)}", $"    new: {ClaudeMcp.Describe(c.New!)}"],
        _ => [],
    };

    /// <summary>Notes for Skarp entries at the other scopes (Claude Code uses the local one first, then project, then user).</summary>
    List<string> Others()
    {
        var notes = new List<string>();
        foreach (var scope in ClaudeScopes.All)
        {
            if (scope == request.Scope) continue;
            var file = ClaudeFile.Read(seams.McpFileOf(scope, projectDir));
            if (!file.Existed || file.Error is not null) continue;
            try
            {
                if (ClaudeMcp.Find(file.Root, scope, projectDir) is { } entry)
                    notes.Add($"note: also installed at {scope.Name()} scope ({file.Path}): {ClaudeMcp.Describe(entry)}; Claude Code uses the local entry first, then project, then user");
            }
            catch (HookShapeException)
            {
                // Reported when that scope is installed or checked.
            }
        }
        return notes;
    }

    int Refuse(string reason)
    {
        error.WriteLine($"skarp install: {_path}: {reason}; Skarp only edits strict JSON (no comments, no trailing commas). Nothing written.");
        error.WriteLine($"Fix the file, or add this entry to its \"mcpServers\"{(request.Scope == ClaudeScope.Local ? $" under projects[\"{projectDir.Replace('\\', '/')}\"]" : "")} by hand:");
        error.Write(JsonFile.Render(new JsonObject { [ClaudeMcp.ServerName] = ClaudeMcp.Entry(request.Spec.Command, _viaCmd) }));
        return Refused;
    }

    bool Save(string text)
    {
        if (request.DryRun) { output.WriteLine("dry run: nothing written"); return true; }
        if (!seams.Confirm($"Write {_path}?", request.Yes, output, error)) return false;
        try { JsonFile.Write(_path, text, backup: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"skarp install: cannot write {_path}: {ex.Message}; --print writes the new content to standard output instead");
            return false;
        }
        output.WriteLine($"Wrote {_path}" + (File.Exists(_path + ".bak") ? $"; the original is kept as {_path}.bak" : ""));
        return true;
    }
}
