using System.Globalization;
using System.Text;
using System.Text.Json;
using SkarpSharp.Tests.Baselines;

namespace SkarpSharp.Tests.Conditionals;

/// <summary>
/// The mock solution's #if patterns (tests/SkarpSharp.Tests/Mock, our own code; Variants.cs,
/// Versions.cs, Features.cs) analyzed from the source tree (no build needed): every file's plan, the members a variant
/// adds or worsens with their ids, base and merged values, and the findings that name a variant, against a Verify
/// snapshot (Conditionals/MockVariantTests.Variants.verified.txt; update with SKARP_UPDATE_GOLDEN=1).
/// </summary>
public class MockVariantTests
{
    static string MockFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SkarpSharp.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "SkarpSharp.Tests", "Mock");
    }

    static string Describe(JsonElement root)
    {
        var sb = new StringBuilder();
        var v = root.GetProperty("run").GetProperty("variants");
        sb.Append(CultureInfo.InvariantCulture, $"run: {v.GetProperty("mode").GetString()}, cap {v.GetProperty("cap").GetInt32()}, check cap {v.GetProperty("checkCap").GetInt32()}; ")
            .Append(CultureInfo.InvariantCulture, $"{v.GetProperty("filesWithConditionals").GetInt32()} files with #if, {v.GetProperty("variants").GetInt32()} variants, ")
            .Append(CultureInfo.InvariantCulture, $"capped [{string.Join(", ", v.GetProperty("cappedFiles").EnumerateArray().Select(x => x.GetString()))}], ")
            .Append(CultureInfo.InvariantCulture, $"{v.GetProperty("unreachableBranches").GetInt32()} unreachable, {v.GetProperty("unplannedBranches").GetInt32()} unplanned, {v.GetProperty("filteredSymbols").GetInt32()} filtered\n");
        foreach (var f in root.GetProperty("files").EnumerateArray().Where(f => f.TryGetProperty("symbols", out _)))
        {
            sb.Append(CultureInfo.InvariantCulture, $"{f.GetProperty("path").GetString()}\n");
            sb.Append(CultureInfo.InvariantCulture, $"  symbols: {string.Join(", ", f.GetProperty("symbols").EnumerateArray().Select(x => x.GetString()))}\n");
            if (f.TryGetProperty("filteredSymbols", out var filtered)) sb.Append(CultureInfo.InvariantCulture, $"  filtered: {string.Join(", ", filtered.EnumerateArray().Select(x => x.GetString()))}\n");
            sb.Append(CultureInfo.InvariantCulture, $"  variants: {string.Join(", ", f.GetProperty("variants").EnumerateArray().Select(x => x.GetProperty("name").GetString()))}");
            sb.Append(f.TryGetProperty("variantsCapped", out _) ? " (capped)\n" : "\n");
            foreach (var u in f.TryGetProperty("unreachableBranches", out var un) ? un.EnumerateArray() : default)
                sb.Append(CultureInfo.InvariantCulture, $"  unreachable: line {u.GetProperty("line").GetInt32()} {u.GetProperty("directive").GetString()} ({u.GetProperty("reason").GetString()})\n");
            if (f.TryGetProperty("mergedLines", out var ml))
                sb.Append(CultureInfo.InvariantCulture, $"  source lines: base {f.GetProperty("lines").GetProperty("source").GetInt32()}, merged {ml.GetProperty("source").GetInt32()}\n");
            foreach (var m in f.GetProperty("members").EnumerateArray().Where(m => m.TryGetProperty("variant", out _)))
            {
                sb.Append(CultureInfo.InvariantCulture, $"  {m.GetProperty("id").GetString()} [{m.GetProperty("key").GetString()}] {m.GetProperty("variant").GetString()}");
                sb.Append(m.TryGetProperty("onlyInVariants", out _) ? " variant-only" : "");
                sb.Append(CultureInfo.InvariantCulture, $": cognitive {m.GetProperty("cognitive").GetInt32()}");
                if (m.TryGetProperty("merged", out var merged)) sb.Append(CultureInfo.InvariantCulture, $", merged {merged.GetProperty("cognitive").GetInt32()}");
                sb.Append('\n');
            }
        }
        sb.Append("findings naming a variant:\n");
        foreach (var x in root.GetProperty("findings").EnumerateArray().Where(x => x.TryGetProperty("variant", out _) && x.GetProperty("rule").GetString() != "magic-number"))
            sb.Append(CultureInfo.InvariantCulture, $"  {x.GetProperty("severity").GetString()} {x.GetProperty("id").GetString()}: {x.GetProperty("message").GetString()}\n");
        return sb.ToString();
    }

    [Test]
    public async Task Mock_variants_merged_values_and_ids()
    {
        var r = BaselineTests.Run(["analyze", MockFolder(), "--no-save", "--no-history", "--no-semantic", "--no-config", "--no-baseline", "--format", "json"]);
        await Assert.That(r.Exit).IsEqualTo(0);
        using var json = JsonDocument.Parse(r.Json);
        await Snapshots.Verify(Describe(json.RootElement), "Conditionals", "MockVariantTests.Variants", "txt", "SKARP_UPDATE_GOLDEN");
    }
}
