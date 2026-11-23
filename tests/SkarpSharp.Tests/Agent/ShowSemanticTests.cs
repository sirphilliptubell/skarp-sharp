using System.Text.Json;
using SkarpSharp.Semantic;
using SkarpSharp.Tests.Semantic;
using static SkarpSharp.Tests.Agent.AgentHost;

namespace SkarpSharp.Tests.Agent;

/// <summary>
/// <c>skarp show</c> on a namespace-cycle finding of the saved run gives every member, every suggested
/// cut and every example reference (the caps of the report do not apply), on the cycles fixture (restored in a copy).
/// </summary>
public sealed class ShowSemanticTests : IDisposable
{
    readonly AgentHost _host = new();

    public void Dispose() => _host.Dispose();

    static string Fixtures()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SkarpSharp.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "SkarpSharp.Tests", "Semantic", "Fixtures");
    }

    static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
            File.Copy(f, Path.Combine(to, f.EndsWith(".fixture", StringComparison.Ordinal) ? Path.GetFileName(f)[..^8] : Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from))
            if (Path.GetFileName(d) is not ("bin" or "obj")) Copy(d, Path.Combine(to, Path.GetFileName(d)));
    }

    [Test, SemanticFixtureFact]
    public async Task Show_gives_a_namespace_cycle_in_full_and_the_text_report_points_at_it()
    {
        var dir = _host.Folder("cycles");
        Copy(Path.Combine(Fixtures(), "cycles"), dir);
        var restore = DotnetRunner.RunAsync(["restore", "Lib.slnx", "-nologo"], dir).GetAwaiter().GetResult();
        await Assert.That(restore.Succeeded).IsTrue().Because(restore.ErrorSummary());

        var r = _host.Run(dir, "analyze", dir, "--no-history", "--no-config");
        await Assert.That(r.Exit).IsEqualTo(0);
        var header = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, ".skarp", "last-run", "run.json"))).RootElement;
        await Assert.That(header.GetProperty("documents").EnumerateArray().Select(d => d.GetString())).Contains("semantic.json");
        var line = Lines(r.Out).Single(l => l.Contains(" namespace-cycle: ", StringComparison.Ordinal));
        var handle = System.Text.RegularExpressions.Regex.Match(line, @"\[([0-9a-f]{8})\]$").Groups[1].Value;

        var json = _host.Run(dir, "show", handle, "--format", "json");
        await Assert.That(json.Exit).IsEqualTo(0);
        var entry = JsonDocument.Parse(json.Out).RootElement.GetProperty("findings")[0];
        await Assert.That(entry.GetProperty("tier").GetString()).IsEqualTo("semantic");
        var component = entry.GetProperty("component");
        var members = component.GetProperty("members").EnumerateArray().Select(m => m.GetString()!).ToList();
        await Assert.That(members.Count >= 2).IsTrue();

        var text = _host.Run(dir, "show", handle).Out;
        await Assert.That(text).Contains($"namespaces ({members.Count}): {string.Join(", ", members)}");
        foreach (var cut in component.GetProperty("cut").EnumerateArray())
            await Assert.That(text).Contains($"{cut.GetProperty("from").GetString()} -> {cut.GetProperty("to").GetString()}");
        foreach (var dep in component.GetProperty("dependencies").EnumerateArray())
        {
            bool isCut = component.GetProperty("cut").EnumerateArray().Any(c => c.GetProperty("from").GetString() == dep.GetProperty("from").GetString() && c.GetProperty("to").GetString() == dep.GetProperty("to").GetString());
            if (!isCut) continue;
            foreach (var ex in dep.GetProperty("examples").EnumerateArray())
                await Assert.That(text).Contains($"{ex.GetProperty("path").GetString()}:{ex.GetProperty("line").GetInt32()} {ex.GetProperty("fromType").GetString()} -> {ex.GetProperty("toType").GetString()}");
        }
    }
}