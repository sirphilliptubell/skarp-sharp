using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkarpSharp.Configuration;

namespace SkarpSharp.Setup;

/// <summary>
/// One settings level's raw file as <c>skarp setup</c> edits it. Read with comments
/// and trailing commas allowed and edited as a <see cref="JsonObject"/>, so untouched keys keep their order and values;
/// written back with the file's own indentation and line endings and a final newline. Comments cannot be kept: they are
/// counted, so the preview can warn and the save can keep the original as <c>&lt;file&gt;.bak</c>.
/// </summary>
public sealed class SettingsDocument
{
    public required string Path { get; init; }
    /// <summary>False: the file does not exist yet (it is created on save).</summary>
    public bool Existed { get; init; }
    /// <summary>The file as it was read (empty for a new file).</summary>
    public string OriginalText { get; init; } = "";
    /// <summary>The tree as it was read; never changed.</summary>
    public required JsonObject Original { get; init; }
    /// <summary>The tree being edited.</summary>
    public JsonObject Current { get; set; } = [];
    /// <summary>Comments in the original file (they are lost on save).</summary>
    public int Comments { get; init; }
    /// <summary>One level of indentation: tabs or spaces, as the file's first indented line (2 spaces for a new file).</summary>
    public char IndentCharacter { get; init; } = ' ';
    public int IndentSize { get; init; } = 2;
    /// <summary><c>\r\n</c> when the file uses it, else <c>\n</c> (also for a new file).</summary>
    public string NewLine { get; init; } = "\n";

    /// <summary>The content of an empty level: <c>{ "version": 1 }</c>.</summary>
    public static JsonObject Empty() => new() { ["version"] = SettingsFileReader.CurrentVersion };

    /// <summary>Reads <paramref name="path"/>, or starts an empty level when it does not exist.</summary>
    /// <exception cref="SetupException">The file cannot be read or is not a JSON object.</exception>
    public static SettingsDocument Open(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!File.Exists(path)) return new SettingsDocument { Path = path, Original = Empty(), Current = Empty() };
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new SetupException($"{path}: cannot read: {ex.Message}"); }
        return FromText(path, text);
    }

    /// <summary>A document for <paramref name="text"/> read from <paramref name="path"/>.</summary>
    public static SettingsDocument FromText(string path, string text)
    {
        JsonObject? node;
        int comments;
        try
        {
            node = JsonFile.Parse(text, strict: false) as JsonObject;
            comments = JsonFile.CountComments(text);
        }
        catch (JsonException ex) { throw new SetupException($"{path}: not valid JSON: {ex.Message}"); }
        catch (ArgumentException ex) { throw new SetupException($"{path}: {ex.Message}"); } // duplicate keys
        if (node is null) throw new SetupException($"{path}: expected a JSON object");
        var (indentChar, indentSize) = JsonFile.DetectIndent(text);
        return new SettingsDocument
        {
            Path = path, Existed = true, OriginalText = text, Original = node, Current = (JsonObject)node.DeepClone(), Comments = comments,
            IndentCharacter = indentChar, IndentSize = indentSize, NewLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n",
        };
    }

    /// <summary>The text <see cref="Current"/> would be saved as.</summary>
    public string Render() => JsonFile.Render(Current, IndentCharacter, IndentSize, NewLine);

    // ---- dotted keys ------------------------------------------------------------------------------------------------

    /// <summary>The value at <paramref name="key"/>; <paramref name="found"/> is false when the key is absent (a JSON
    /// <c>null</c> is found, with a null value).</summary>
    public static JsonNode? Get(JsonObject root, string key, out bool found) => SettingText.GetPath(root, key, out found);

    public static bool Has(JsonObject root, string key) { _ = Get(root, key, out bool found); return found; }

    /// <summary>Sets <paramref name="key"/>: an existing key keeps its place, a new key goes at the end of its object,
    /// and missing parent objects are created (a parent that is not an object is replaced).</summary>
    public static void Set(JsonObject root, string key, JsonNode? value) => SettingText.SetPath(root, key, value);

    /// <summary>Removes <paramref name="key"/> and the parent objects it leaves empty; false when it was absent.</summary>
    public static bool Remove(JsonObject root, string key)
    {
        var parts = key.Split('.');
        var chain = new List<JsonObject> { root };
        var o = root;
        foreach (var part in parts[..^1])
        {
            if (o[part] is not JsonObject child) return false;
            chain.Add(child);
            o = child;
        }
        if (!o.Remove(parts[^1])) return false;
        for (int i = chain.Count - 1; i > 0 && chain[i].Count == 0; i--) chain[i - 1].Remove(parts[i - 1]);
        return true;
    }

    /// <summary>The leaf keys of a tree: values, and objects that are one setting (a threshold level's metric values).</summary>
    public static IEnumerable<string> Leaves(JsonObject root, string prefix = "")
    {
        foreach (var (name, value) in root)
        {
            var key = prefix + name;
            bool oneSetting = SettingsRegistry.Row(key) is { Kind: not SettingKind.Object };
            if (value is JsonObject child && !oneSetting)
            {
                if (child.Count == 0) yield return key;
                else foreach (var k in Leaves(child, key + ".")) yield return k;
            }
            else yield return key;
        }
    }

    /// <summary>A value in words: <c>(empty)</c> for an empty list, metric values as <c>sourceLines 300, wmc 30</c>.</summary>
    public static string Text(JsonNode? node) => node switch
    {
        null => "null",
        JsonArray a => a.Count == 0 ? "(empty)" : string.Join(", ", a.Select(Text)),
        JsonObject o => string.Join(", ", o.Select(p => $"{p.Key} {Text(p.Value)}")),
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        _ => node.ToJsonString(),
    };

    /// <summary>A JSON number for <paramref name="v"/>: whole numbers without a fraction.</summary>
    public static JsonNode Number(double v) => SettingText.Number(v);

    /// <summary>The number a JSON value holds, whatever its .NET type; null for anything else.</summary>
    public static double? AsNumber(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    internal static string N(double v) => SettingText.N(v);
}

/// <summary>A <c>skarp setup</c> error; the message is complete (file, key, reason).</summary>
public sealed class SetupException(string message) : Exception(message);
