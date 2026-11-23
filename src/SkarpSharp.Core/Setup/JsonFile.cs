using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SkarpSharp.Setup;

/// <summary>
/// The file-agnostic part of setup's writer, shared by <c>skarp setup</c> (Skarp's own
/// settings, comments tolerated) and <c>skarp install</c> (another program's settings, strict JSON only). It parses a
/// JSON object keeping key order, detects the file's indentation and line endings, renders a tree back in that form with
/// a final newline, and writes a file atomically after an optional <c>.bak</c> copy.
/// </summary>
public static class JsonFile
{
    static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = false };
    static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    static readonly JsonDocumentOptions Strict = new();

    /// <summary>Parses <paramref name="text"/> as a JSON value; <paramref name="strict"/> refuses comments and trailing
    /// commas (as Claude Code's settings loader does).</summary>
    /// <exception cref="JsonException">Not valid JSON (the exception carries the 0-based line).</exception>
    /// <exception cref="ArgumentException">A duplicate key.</exception>
    public static JsonNode? Parse(string text, bool strict) => JsonNode.Parse(text, NodeOptions, strict ? Strict : Lenient);

    /// <summary>The comments in <paramref name="text"/> (a lenient read; 0 for strict JSON).</summary>
    public static int CountComments(string text)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text), new JsonReaderOptions { CommentHandling = JsonCommentHandling.Allow, AllowTrailingCommas = true });
        int n = 0;
        while (reader.Read()) if (reader.TokenType == JsonTokenType.Comment) n++;
        return n;
    }

    /// <summary>The first indented line's leading whitespace: a tab, or its number of spaces (2 spaces when none).</summary>
    public static (char Character, int Size) DetectIndent(string text)
    {
        foreach (var raw in text.Split('\n').Skip(1))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            if (line[0] == '\t') return ('\t', 1);
            int spaces = line.Length - line.TrimStart(' ').Length;
            if (spaces > 0) return (' ', Math.Min(spaces, 127));
        }
        return (' ', 2);
    }

    /// <summary><c>\r\n</c> when the text uses it, else <c>\n</c> (also for a new file).</summary>
    public static string DetectNewLine(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary><paramref name="node"/> indented with <paramref name="indentCharacter"/> × <paramref name="indentSize"/>,
    /// lines ended with <paramref name="newLine"/>, plus a final newline; non-ASCII text is written as is.</summary>
    public static string Render(JsonNode node, char indentCharacter = ' ', int indentSize = 2, string newLine = "\n")
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true, IndentCharacter = indentCharacter, IndentSize = indentSize, NewLine = newLine,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            node.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + newLine;
    }

    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/>: its folder created when missing, the existing file
    /// copied to <c>&lt;path&gt;.bak</c> first when <paramref name="backup"/> (an older <c>.bak</c> is overwritten), then a
    /// temporary file moved over the target (<see cref="Agent.Paths.WriteAtomic"/>).
    /// </summary>
    /// <exception cref="IOException">The write failed.</exception>
    /// <exception cref="UnauthorizedAccessException">The write was not allowed.</exception>
    public static void Write(string path, string text, bool backup)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } folder && !Directory.Exists(folder)) Directory.CreateDirectory(folder);
        if (backup && File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
        Agent.Paths.WriteAtomic(path, text);
    }
}
