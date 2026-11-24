using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkarpSharp.Setup;

namespace SkarpSharp.Install;

/// <summary>
/// A Claude Code settings file as <c>skarp install</c> reads it. Strict JSON only, because Claude
/// Code itself skips a file with comments or trailing commas; the file's indentation and line endings are kept for the
/// rewrite.
/// </summary>
public sealed class ClaudeFile
{
    public required string Path { get; init; }
    public bool Existed { get; init; }
    public string Text { get; init; } = "";
    public JsonObject Root { get; init; } = [];
    /// <summary>Why the file cannot be used (not strict JSON, not an object, unreadable); null when it can.</summary>
    public string? Error { get; init; }
    public char IndentCharacter { get; init; } = ' ';
    public int IndentSize { get; init; } = 2;
    public string NewLine { get; init; } = "\n";

    /// <summary><paramref name="root"/> in this file's indentation and line endings, with a final newline.</summary>
    public string Render(JsonObject root) => JsonFile.Render(root, IndentCharacter, IndentSize, NewLine);

    /// <summary>Reads <paramref name="path"/>; a missing file reads as empty (<see cref="Existed"/> false).</summary>
    public static ClaudeFile Read(string path)
    {
        if (!File.Exists(path)) return new ClaudeFile { Path = path };
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new ClaudeFile { Path = path, Existed = true, Error = $"cannot read: {ex.Message}" }; }
        return FromText(path, text);
    }

    public static ClaudeFile FromText(string path, string text)
    {
        JsonNode? node;
        try { node = JsonFile.Parse(text, strict: true); }
        catch (JsonException ex) { return new ClaudeFile { Path = path, Existed = true, Text = text, Error = StrictError(ex) }; }
        catch (ArgumentException ex) { return new ClaudeFile { Path = path, Existed = true, Text = text, Error = $"not strict JSON: {ex.Message}" }; }
        if (node is not JsonObject root) return new ClaudeFile { Path = path, Existed = true, Text = text, Error = "not a JSON object" };
        var (c, size) = JsonFile.DetectIndent(text);
        return new ClaudeFile { Path = path, Existed = true, Text = text, Root = root, IndentCharacter = c, IndentSize = size, NewLine = JsonFile.DetectNewLine(text) };
    }

    /// <summary>"not strict JSON (line 3): ..." from the parser's message without its position suffix.</summary>
    static string StrictError(JsonException ex)
    {
        var reason = ex.Message;
        int at = reason.IndexOf(" LineNumber:", StringComparison.Ordinal);
        if (at > 0) reason = reason[..at];
        var line = ex.LineNumber is { } l ? $" (line {(l + 1).ToString(CultureInfo.InvariantCulture)})" : "";
        return $"not strict JSON{line}: {reason.Trim()}";
    }
}
