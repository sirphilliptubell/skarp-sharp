using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SkarpSharp.Configuration;

/// <summary>
/// The one <c>&lt;key&gt;=&lt;value&gt;</c> grammar, shared by <c>skarp setup --set</c> and the
/// <c>--setting</c> flag. A value parses by its registry row's kind:
/// <c>null</c> writes JSON null ("use Skarp default"), <c>[]</c> (or nothing) empties a list, a list is comma-separated,
/// thresholds take a number or <c>off</c>, and entries of objects (<c>score.weights.&lt;rule&gt;.&lt;field&gt;</c>) infer
/// their type.
/// </summary>
public static class SettingText
{
    /// <summary>A value in the grammar, parsed by the key's kind.</summary>
    /// <exception cref="SettingTextException">The value does not fit the kind or the range.</exception>
    public static JsonNode? Parse(string key, string text)
    {
        if (text == "null") return null;
        var row = SettingsRegistry.Row(key);
        string Bad(string expected) => $"{key}: '{text}' is not {expected}";
        if (row is null) return Infer(text); // an entry of an object (score.weights.<rule>.<field>)
        if (row.Rule is not null && row.Part is "notice" or "warning" && row.Kind == SettingKind.Number)
        {
            if (text is "off" or "none") return JsonValue.Create("off");
            var metric = key.Count(c => c == '.') == 3 ? key[(key.LastIndexOf('.') + 1)..] : null;
            var (min, max) = metric is null ? (row.Min ?? 0, row.Max ?? SettingsRegistry.MaxCount) : SettingsRegistry.MetricRange(metric);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) throw new SettingTextException(Bad("a number or off"));
            if (t < min || t > max) throw new SettingTextException($"{key}: {text} is out of range ({N(min)} to {N(max)})");
            return Number(t);
        }
        switch (row.Kind)
        {
            case SettingKind.Boolean:
                return text switch
                {
                    "true" or "on" => JsonValue.Create(true),
                    "false" or "off" => JsonValue.Create(false),
                    _ => throw new SettingTextException(Bad("true or false")),
                };
            case SettingKind.Integer or SettingKind.Number:
            {
                bool whole = row.Kind == SettingKind.Integer;
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || (whole && d != Math.Floor(d)))
                    throw new SettingTextException(Bad(whole ? "a whole number" : "a number"));
                if (!row.InRange(d)) throw new SettingTextException($"{key}: {text} is out of range ({row.RangeText})");
                return Number(d);
            }
            case SettingKind.Choice:
                if (row.Allowed is { } allowed && !allowed.Contains(text, StringComparer.Ordinal))
                    throw new SettingTextException(Bad(Words(allowed)));
                return JsonValue.Create(text);
            case SettingKind.List:
                if (text == "[]") return new JsonArray();
                return new JsonArray([.. text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(v => (JsonNode)JsonValue.Create(v))]);
            default:
                if (text.Length == 0) throw new SettingTextException($"{key}: empty value");
                return row.Rule is not null ? ParameterValue(text) : JsonValue.Create(text);
        }
    }

    /// <summary>The text form of a value in the grammar (the inverse of <see cref="Parse"/>): <c>null</c>, <c>[]</c>, a
    /// comma-separated list, a number without a fraction when whole; null when the value has no text form (an object, or a
    /// list item holding a comma).</summary>
    public static string? Format(JsonNode? value)
    {
        switch (value)
        {
            case null: return "null";
            case JsonArray a:
                if (a.Count == 0) return "[]";
                var items = new List<string>(a.Count);
                foreach (var item in a)
                {
                    if (item is not JsonValue || Format(item) is not { } t || t.Contains(',') || t.Trim().Length != t.Length || t.Length == 0) return null;
                    items.Add(t);
                }
                return string.Join(",", items);
            case JsonObject: return null;
            default:
                return value.GetValueKind() switch
                {
                    JsonValueKind.String => value.GetValue<string>(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => value.ToJsonString(),
                };
        }
    }

    static string Words(IReadOnlyList<string> values) => values.Count == 1 ? values[0]
        : string.Join(", ", values.Take(values.Count - 1)) + " or " + values[^1];

    static JsonNode Infer(string text) =>
        text is "true" or "false" ? JsonValue.Create(text == "true")
        : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? Number(d)
        : JsonValue.Create(text);

    /// <summary>A rule parameter value as written: a number when it is one, else text.</summary>
    public static JsonNode ParameterValue(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !value.Contains(',')
            ? Number(d) : JsonValue.Create(value);

    /// <summary>A JSON number for <paramref name="v"/>: whole numbers without a fraction.</summary>
    public static JsonNode Number(double v) => v == Math.Floor(v) && Math.Abs(v) < 1e15
        ? JsonValue.Create((long)v) : JsonValue.Create(Math.Round(v, 6));

    internal static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Sets a dotted <paramref name="key"/> in <paramref name="root"/>: an existing key keeps its place, a new
    /// key goes at the end of its object, missing parents are created (a parent that is not an object is replaced).</summary>
    public static void SetPath(JsonObject root, string key, JsonNode? value)
    {
        var parts = key.Split('.');
        var o = root;
        foreach (var part in parts[..^1])
        {
            if (o[part] is not JsonObject child) { child = []; o[part] = child; }
            o = child;
        }
        o[parts[^1]] = value;
    }

    /// <summary>The value at a dotted key; <paramref name="found"/> is false when it is absent.</summary>
    public static JsonNode? GetPath(JsonObject root, string key, out bool found)
    {
        JsonNode? node = root;
        foreach (var part in key.Split('.'))
        {
            if (node is not JsonObject o || !o.TryGetPropertyValue(part, out node)) { found = false; return null; }
        }
        found = true;
        return node;
    }

    /// <summary>True for a well-formed dotted key (no empty part).</summary>
    public static bool WellFormed(string key) => key.Length > 0 && !key.Split('.').Any(p => p.Length == 0);

    /// <summary>The keys that describe a file rather than a run: never set on a command line.</summary>
    public static bool DescribesFile(string key) =>
        key is "overrides" or "useInRepositories" or "root" or "version"
        || key.StartsWith("overrides.", StringComparison.Ordinal) || key.StartsWith("overrides[", StringComparison.Ordinal);
}

/// <summary>A value that does not fit its key; the message names the key.</summary>
public sealed class SettingTextException(string message) : Exception(message);
