using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;

namespace SkarpSharp.Mcp;

/// <summary>
/// A tool call's arguments, read by hand. A missing argument, JSON null, an
/// empty string and the string <c>"null"</c> all mean absent (some clients send those for optional parameters). An
/// unknown argument is an error. Every error is an <see cref="McpException"/> whose text names the cap or the valid values.
/// </summary>
public sealed class ToolArgs
{
    readonly IDictionary<string, JsonElement> _args;

    /// <param name="tool">The tool's name, for the unknown-argument error.</param>
    /// <param name="known">The tool's parameters in schema order.</param>
    public ToolArgs(string tool, IDictionary<string, JsonElement>? args, IReadOnlyList<string> known)
    {
        _args = args ?? new Dictionary<string, JsonElement>();
        foreach (var name in _args.Keys)
            if (!known.Contains(name))
                throw new McpException($"unknown argument '{name}'; {tool} takes {string.Join(", ", known)}");
    }

    /// <summary>The raw value, or null when absent.</summary>
    JsonElement? Raw(string name)
    {
        if (!_args.TryGetValue(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String when v.GetString() is "" or "null" => null,
            _ => v,
        };
    }

    public string? String(string name)
    {
        if (Raw(name) is not { } v) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : throw new McpException($"{name} must be a string");
    }

    /// <summary>A string out of <paramref name="allowed"/>, or <paramref name="fallback"/> when absent.</summary>
    public string Choice(string name, string fallback, params string[] allowed)
    {
        var v = String(name) ?? fallback;
        return allowed.Contains(v) ? v : throw new McpException($"{name} must be {Or(allowed)} (got '{v}')");
    }

    /// <summary>A whole number from <paramref name="min"/> to <paramref name="max"/> (a number, or a string of digits), or
    /// <paramref name="fallback"/> when absent.</summary>
    public int Int(string name, int fallback, int min, int max)
    {
        if (Raw(name) is not { } v) return fallback;
        long n;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i)) n = i;
        else if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var s)) n = s;
        else throw new McpException($"{name} must be a whole number from {min} to {max} (got {Shown(v)})");
        return n >= min && n <= max ? (int)n : throw new McpException($"{name} must be {min} to {max} (got {n.ToString(CultureInfo.InvariantCulture)})");
    }

    /// <summary>true or false (a boolean, or the string <c>true</c> or <c>false</c>), or <paramref name="fallback"/> when absent.</summary>
    public bool Bool(string name, bool fallback)
    {
        if (Raw(name) is not { } v) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when v.GetString() is "true" or "false" => v.GetString() == "true",
            _ => throw new McpException($"{name} must be true or false (got {Shown(v)})"),
        };
    }

    /// <summary>
    /// A list of strings with at most <paramref name="max"/> entries, or null when absent or empty. A string
    /// holding a JSON array is read as that array (some clients send lists that way); any other string is a list of one.
    /// </summary>
    public List<string>? Strings(string name, int max)
    {
        if (Raw(name) is not { } v) return null;
        if (v.ValueKind == JsonValueKind.String)
        {
            var text = v.GetString()!.Trim();
            if (!text.StartsWith('[')) return [text];
            try
            {
                using var doc = JsonDocument.Parse(text);
                v = doc.RootElement.Clone();
            }
            catch (JsonException) { throw new McpException($"{name} must be a list of paths (got {Shown(v)})"); }
        }
        if (v.ValueKind != JsonValueKind.Array) throw new McpException($"{name} must be a list of paths (got {Shown(v)})");
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw new McpException($"{name} must be a list of paths (got {Shown(item)} in it)");
            if (item.GetString() is { Length: > 0 } s && s != "null") list.Add(s);
        }
        if (list.Count > max) throw new McpException($"{name} takes at most {max.ToString(CultureInfo.InvariantCulture)} entries (got {list.Count.ToString(CultureInfo.InvariantCulture)})");
        return list.Count == 0 ? null : list;
    }

    static string Shown(JsonElement v) => v.ValueKind == JsonValueKind.String ? $"'{v.GetString()}'" : v.GetRawText();

    static string Or(string[] values) => values.Length == 2 ? $"{values[0]} or {values[1]}" : string.Join(", ", values[..^1]) + " or " + values[^1];
}
