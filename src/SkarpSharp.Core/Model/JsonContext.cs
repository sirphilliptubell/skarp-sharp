using System.Text.Json.Serialization;

namespace SkarpSharp.Model;

/// <summary>Source-generated JSON (AOT safe).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AnalysisResult))]
[JsonSerializable(typeof(HistoryReport))]
public sealed partial class ResultJsonContext : JsonSerializerContext;
