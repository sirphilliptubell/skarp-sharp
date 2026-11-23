using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SkarpSharp.Analysis;
using SkarpSharp.Baselines;
using SkarpSharp.Configuration;
using SkarpSharp.History;
using SkarpSharp.Model;
using SkarpSharp.Semantic;

namespace SkarpSharp.Tests.Contracts;

/// <summary>
/// The JSON contracts (analysis and history output, semantic
/// output, both baseline files, the settings file, and the SARIF profile) are guarded by a committed shape snapshot per schema version in
/// Contracts/shapes/{contract}.v{version}.verified.txt (compared through Verify.TUnit). The shape is every property path with its JSON kind and whether it may be
/// absent, read from the source-generated System.Text.Json metadata the tool itself serializes with, so optional
/// properties count even when a sample output leaves them out.
/// <list type="bullet">
/// <item>A removed, renamed or retyped property fails until the schema version is bumped (a new snapshot file).</item>
/// <item>An added property fails until the snapshot of the same version is updated (additive: the version stays).</item>
/// </list>
/// After an intended change: run once with SKARP_UPDATE_SHAPES=1 and review the diff. The update writes a missing
/// snapshot, or extends the current one when the change is additive; it never rewrites a snapshot to drop a property.
/// Committed samples (golden outputs, this repository's baselines and settings) must conform to the shapes.
/// </summary>
public class ContractShapeTests
{
    static readonly string Repo = FindRepo();
    static readonly string Shapes = Path.Combine(Repo, "tests", "SkarpSharp.Tests", "Contracts", "shapes");

    static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SkarpSharp.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root (SkarpSharp.slnx) not found");
    }

    /// <summary>The contracts: name, schema version constant, root type metadata.</summary>
    public static IEnumerable<string> Contracts => ["analysis", "history", "semantic", "baseline", "settings", "sarif", "last-run", "show", "tracking"];

    static (int Version, JsonTypeInfo Root) Contract(string name) => name switch
    {
        "analysis" => (Analyzer.SchemaVersion, ResultJsonContext.Default.AnalysisResult),
        // skarp history writes the same schemaVersion as analyze (SkarpCommand.History).
        "history" => (Analyzer.SchemaVersion, ResultJsonContext.Default.HistoryReport),
        "semantic" => (SemanticRunner.SchemaVersion, SemanticJsonContext.Default.SemanticResult),
        // Both baseline files (.skarp-baseline.json and .skarp-baseline.semantic*.json) are read as BaselineFile.
        "baseline" => (Baseline.CurrentVersion, BaselineJsonContext.Default.BaselineDisk),
        "settings" => (SettingsFileReader.CurrentVersion, SettingsJsonContext.Default.SettingsFile),
        // What skarp writes inside SARIF 2.1.0 (its profile version, contracts.md section 9).
        "sarif" => (SkarpSharp.Reporting.SarifReport.ProfileVersion, SarifJsonContext.Default.SarifLog),
        // .skarp/last-run/run.json, skarp show --format json, and the tracking files of skarp check --track.
        "last-run" => (SkarpSharp.Agent.LastRun.Version, SkarpSharp.Agent.AgentJsonContext.Default.LastRunHeader),
        "show" => (SkarpSharp.Agent.Join.ShowSchemaVersion, SkarpSharp.Agent.AgentJsonContext.Default.ShowReport),
        "tracking" => (SkarpSharp.Agent.TrackingStore.Version, SkarpSharp.Agent.AgentJsonContext.Default.TrackingState),
        _ => throw new ArgumentException(name),
    };

    // ---- shape extraction from the source-generated metadata ----

    internal static List<string> Shape(JsonTypeInfo root)
    {
        var lines = new List<string> { "$: " + Describe(root.Options, root.Type) };
        Walk(root.Options, root.Type, "$", lines, []);
        return lines;
    }

    static void Walk(JsonSerializerOptions options, Type type, string path, List<string> lines, HashSet<Type> stack)
    {
        var info = options.GetTypeInfo(Unwrap(type));
        switch (info.Kind)
        {
            case JsonTypeInfoKind.Object:
                if (!stack.Add(info.Type)) return;
                foreach (var p in Serialized(info).OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    var child = path + "." + p.Name;
                    lines.Add($"{child}{(p.IsGetNullable ? "?" : "")}: {Describe(options, p.PropertyType)}");
                    Walk(options, p.PropertyType, child, lines, stack);
                }
                stack.Remove(info.Type);
                break;
            case JsonTypeInfoKind.Enumerable:
                Walk(options, info.ElementType!, path + "[]", lines, stack);
                break;
            case JsonTypeInfoKind.Dictionary:
                Walk(options, info.ElementType!, path + ".*", lines, stack);
                break;
        }
    }

    /// <summary>The serialized properties: <c>[JsonIgnore]</c> ones come from the source generator without accessors.</summary>
    static IEnumerable<JsonPropertyInfo> Serialized(JsonTypeInfo info) => info.Properties.Where(p => p.Get is not null || p.Set is not null);

    static Type Unwrap(Type t) => Nullable.GetUnderlyingType(t) ?? t;

    static string Describe(JsonSerializerOptions options, Type type)
    {
        var t = Unwrap(type);
        var info = options.GetTypeInfo(t);
        return info.Kind switch
        {
            JsonTypeInfoKind.Object => "object",
            JsonTypeInfoKind.Enumerable => $"array<{Describe(options, info.ElementType!)}>",
            JsonTypeInfoKind.Dictionary => $"map<{Describe(options, info.ElementType!)}>",
            _ => Primitive(t),
        };
    }

    static string Primitive(Type t) =>
        t == typeof(string) ? "string"
        : t == typeof(bool) ? "boolean"
        : t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ? "integer"
        : t == typeof(double) || t == typeof(float) || t == typeof(decimal) ? "number"
        : t == typeof(JsonElement) ? "any"
        : "unknown:" + t.FullName;

    // ---- snapshots ----

    // Verify's naming ({name}.v{version}.verified.txt); Verify compares the whole snapshot (Snapshots.Verify).
    static string SnapshotName(string name, int version) => $"{name}.v{version}";

    static string SnapshotPath(string name, int version) => Path.Combine(Shapes, SnapshotName(name, version) + ".verified.txt");

    static string Header(string name, int version) =>
        $"# JSON shape of the {name} contract, schema version {version} (docs/contracts.md). Generated by ContractShapeTests;\n" +
        "# one line per property path: '?' marks a property that may be absent or null; [] an array element; .* a map value.\n";

    static List<string> ReadSnapshot(string path) =>
        [.. File.ReadAllLines(path).Where(l => l.Length > 0 && !l.StartsWith('#'))];

    [Test]
    [MethodDataSource(nameof(Contracts))]
    public async Task Shape_matches_the_snapshot_of_its_schema_version(string name)
    {
        var (version, root) = Contract(name);
        var current = Shape(root);
        await Assert.That(current).DoesNotContain(l => l.Contains("unknown:", StringComparison.Ordinal));
        var path = SnapshotPath(name, version);
        bool exists = File.Exists(path);
        var recorded = exists ? ReadSnapshot(path) : [];
        var removed = recorded.Except(current, StringComparer.Ordinal).ToList();
        var added = current.Except(recorded, StringComparer.Ordinal).ToList();
        // The versioning rule comes first: a removed or changed property fails here, before Verify, so no received file
        // (and no SKARP_UPDATE_SHAPES run) ever drops a line from the snapshot of a released version.
        if (removed.Count > 0)
            Assert.Fail($"{name}: properties removed, renamed or changed in kind or optionality without a schema version bump " +
                $"(version {version}; docs/contracts.md, versioning rule). Bump the version and record a new snapshot, or undo the change.\n" +
                $"Removed or changed:\n  {string.Join("\n  ", removed)}" + (added.Count > 0 ? $"\nNew:\n  {string.Join("\n  ", added)}" : ""));
        try
        {
            await Snapshots.Verify(Header(name, version) + string.Join("\n", current) + "\n", "Contracts/shapes", SnapshotName(name, version), "txt", "SKARP_UPDATE_SHAPES");
        }
        catch (Exception e) when (!exists)
        {
            Assert.Fail($"{name}: no shape snapshot for schema version {version} ({path}). After a version bump, run the tests once with SKARP_UPDATE_SHAPES=1 and keep the older snapshots.\n{e.Message}");
        }
        catch (Exception e) when (added.Count > 0)
        {
            Assert.Fail($"{name}: properties added (additive: schema version {version} stays). Record them in {path} " +
                $"(run once with SKARP_UPDATE_SHAPES=1) and list them in docs/contracts.md.\nNew:\n  {string.Join("\n  ", added)}\n{e.Message}");
        }
    }

    [Test]
    public async Task Every_snapshot_names_a_known_contract_at_or_below_its_current_version()
    {
        foreach (var file in Directory.GetFiles(Shapes, "*.txt").Where(f => !f.EndsWith(".received.txt", StringComparison.Ordinal)))
        {
            await Assert.That(file).EndsWith(".verified.txt");
            var stem = Path.GetFileName(file)[..^".verified.txt".Length];
            int dot = stem.LastIndexOf(".v", StringComparison.Ordinal);
            await Assert.That(dot > 0).IsTrue().Because($"snapshot name not <contract>.v<version>.verified.txt: {file}");
            var (current, _) = Contract(stem[..dot]);
            await Assert.That(int.Parse(stem[(dot + 2)..], System.Globalization.CultureInfo.InvariantCulture) <= current).IsTrue().Because($"snapshot newer than the code: {file}");
        }
    }

    // ---- committed and written samples conform to the shapes ----

    /// <summary>Every property of <paramref name="element"/> is a property of the contract type, with a matching kind.</summary>
    internal static void Conforms(JsonElement element, JsonSerializerOptions options, Type type, string path, List<string> errors)
    {
        var t = Unwrap(type);
        if (element.ValueKind == JsonValueKind.Null) return;
        var info = options.GetTypeInfo(t);
        switch (info.Kind)
        {
            case JsonTypeInfoKind.Object:
                if (element.ValueKind != JsonValueKind.Object) { errors.Add($"{path}: expected an object, found {element.ValueKind}"); return; }
                var props = Serialized(info).ToDictionary(p => p.Name, StringComparer.Ordinal);
                foreach (var p in element.EnumerateObject())
                {
                    if (!props.TryGetValue(p.Name, out var meta)) { errors.Add($"{path}.{p.Name}: not in the contract"); continue; }
                    Conforms(p.Value, options, meta.PropertyType, path + "." + p.Name, errors);
                }
                break;
            case JsonTypeInfoKind.Enumerable:
                if (element.ValueKind != JsonValueKind.Array) { errors.Add($"{path}: expected an array, found {element.ValueKind}"); return; }
                foreach (var item in element.EnumerateArray()) Conforms(item, options, info.ElementType!, path + "[]", errors);
                break;
            case JsonTypeInfoKind.Dictionary:
                if (element.ValueKind != JsonValueKind.Object) { errors.Add($"{path}: expected a map, found {element.ValueKind}"); return; }
                foreach (var p in element.EnumerateObject()) Conforms(p.Value, options, info.ElementType!, path + ".*", errors);
                break;
            default:
                var expected = Primitive(t);
                bool ok = expected switch
                {
                    "string" => element.ValueKind == JsonValueKind.String,
                    "boolean" => element.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "integer" or "number" => element.ValueKind == JsonValueKind.Number,
                    _ => true,
                };
                if (!ok) errors.Add($"{path}: expected {expected}, found {element.ValueKind}");
                break;
        }
    }

    static async Task AssertConforms(string json, JsonTypeInfo root, string what)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var errors = new List<string>();
        Conforms(doc.RootElement, root.Options, root.Type, "$", errors);
        await Assert.That(errors.Count == 0).IsTrue().Because($"{what} does not conform to its contract:\n  {string.Join("\n  ", errors)}");
    }

    [Test]
    [Arguments("tests/SkarpSharp.Tests/Golden/expected/CliGoldenTests.Analyze_json_schema_1.verified.json", "analysis")]
    [Arguments("tests/SkarpSharp.Tests/Golden/expected/CliGoldenTests.Baseline_writes_the_expected_file.verified.json", "baseline")]
    [Arguments(".skarp-baseline.json", "baseline")]
    [Arguments(".skarp-baseline.semantic.json", "baseline")]
    [Arguments(".skarp.json", "settings")]
    [Arguments("tests/SkarpSharp.Tests/Golden/sample/.skarp.json", "settings")]
    [Arguments("tests/SkarpSharp.Tests/Golden/expected/SarifTests.Output_is_deterministic_and_matches_the_golden_file.verified.sarif", "sarif")]
    public async Task Committed_samples_conform(string file, string contract) =>
        await AssertConforms(File.ReadAllText(Path.Combine(Repo, file)), Contract(contract).Root, file);

    /// <summary>The baselines are written by hand (one finding per line), so their writers are checked with every optional header.</summary>
    [Test]
    public async Task Written_baselines_conform()
    {
        var settings = new FindingSettings { Preset = "default", ComplexMetric = "cognitive", LongMethodLines = "physical" };
        var finding = new Finding { Id = "long-method|A.cs|A.M", Rule = "long-method", Value = 81 };
        var structural = new Finding { Id = "empty-catch|A.cs|A.M", Rule = "empty-catch" };
        var syntax = Baseline.Write([finding, structural], "0.1.0", settings, "0123456789abcdef", new CommitAnchor(new string('a', 40), 12), ["A.cs", "B.cs"]);
        await AssertConforms(syntax, BaselineJsonContext.Default.BaselineDisk, "syntax baseline");
        var parsed = Baseline.Parse(syntax, "test");
        await Assert.That(parsed.Findings.Count).IsEqualTo(2);
        await Assert.That(parsed.Files!).IsEquivalentTo(["A.cs", "B.cs"], CollectionOrdering.Matching);

        var semantic = new SemanticResult
        {
            Run = new SemanticRunInfo { Findings = settings, Solution = "S.slnx", Targets = "highest", TargetFramework = "net10.0" },
            Findings = [new Finding { Id = "high-coupling|A.cs|A", Rule = "high-coupling", Value = 40 }],
        };
        var semanticText = SemanticBaseline.Write(semantic, "0.1.0", "fedcba9876543210", new string('b', 40) + "+");
        await AssertConforms(semanticText, BaselineJsonContext.Default.BaselineDisk, "semantic baseline");
        await Assert.That(SemanticBaseline.Parse(semanticText, "test").Findings).HasSingleItem();
    }
}