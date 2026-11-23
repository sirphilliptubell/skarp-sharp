using System.Text.RegularExpressions;
using SkarpSharp.Configuration;
using SkarpSharp.Findings;
using SkarpSharp.Tests.Agent;

namespace SkarpSharp.Tests;

/// <summary>
/// No user-visible text mentions the design process: decision numbers (DD-n), evidence levels (E1 to
/// E5, "evidence", "via research"), paths into the design docs, catalog or research, slice and spike numbers, or
/// vendor-file dates. The guard runs the main commands on a copy of the CLI golden sample (analyze in every format,
/// baseline, check, show, config, explain for every rule, the help texts), checks the static texts the outputs are
/// built from (rule texts, preset sources, settings descriptions), and scans every verified output file (CLI goldens,
/// setup frames, hook reports; not the contract shape snapshots, whose header the test writes).
/// </summary>
public class NoInternalReferencesTests
{
    static readonly Regex Internal = new(
        @"\bDD-\d|\bE[1-5]\b|\bevidence\b|via research|docs[/\\](catalog|design|research)|contracts\.md|rules file 20\d\d|\bslice \d|\bspike\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static readonly string Project = Path.Combine(FindRepo(), "tests", "SkarpSharp.Tests");

    static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SkarpSharp.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root (SkarpSharp.slnx) not found");
    }

    /// <summary>Every line of <paramref name="text"/> that matches, prefixed with <paramref name="where"/>.</summary>
    internal static IEnumerable<string> Hits(string where, string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(l => Internal.IsMatch(l))
            .Select(l => $"{where}: {(l.Length > 200 ? l[..200] + "..." : l)}");

    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }

    [Test]
    public async Task The_pattern_finds_what_it_should()
    {
        foreach (var bad in new[] { "(DD-28)", "evidence E3", "E1 via research", "docs/catalog/analysis/x.md", "docs\\design\\y.md",
                     "(vendor rules file 2025-05-09)", "slice 15", "the spike", "contracts.md section 10" })
            await Assert.That(Internal.IsMatch(bad)).IsTrue().Because(bad);
        foreach (var good in new[] { "(CodeScene: 70 lines)", "E10", "PE1", "evidenced-free text: none", "docs/git-hooks.md", "sliced 3" })
            await Assert.That(Internal.IsMatch(good)).IsFalse().Because(good);
    }

    [Test]
    public async Task Command_outputs_have_no_internal_references()
    {
        using var host = new AgentHost();
        var root = host.Folder("sample");
        CopyDirectory(Path.Combine(Project, "Golden", "sample"), root);
        var file = Path.Combine(root, "src", "OrderBook.cs");
        string[] common = ["--no-semantic", "--parallelism", "2"];
        List<string[]> runs =
        [
            ["--help"], ["check", "--help"], ["show", "--help"], ["setup", "--help"], ["install", "--help"],
            ["explain"],
            ["analyze", root, .. common],
            ["analyze", root, .. common, "--verbose", "--all", "--no-save"],
            ["analyze", root, .. common, "--group", "rule", "--all", "--magic-numbers", "list", "--no-save"],
            ["analyze", root, .. common, "--sort", "health", "--top", "1", "--no-save"],
            ["analyze", root, .. common, "--sort", "long-method", "--top", "2", "--no-save"],
            ["analyze", root, .. common, "--gate", "--gate-on", "*:notice", "--no-save"],
            ["analyze", root, .. common, "--no-baseline", "--no-save"],
            ["analyze", root, .. common, "--exclude-tests-why", "--no-save"],
            ["hotspots", root, "--parallelism", "2"],
            ["config", root], ["config", root, "--origins"], ["config", root, "--format", "json"],
            ["config", root, "--file", file],
            ["show"], ["show", "--all", "--include-notices"], ["show", "--format", "json", "--include-notices"],
            ["show", "--format", "text"], ["show", "--from", "baseline"], ["show", "--file", file],
            ["check", file], ["check", file, "--include-notices"],
        ];
        foreach (var format in new[] { "json", "markdown", "github", "sarif", "text" })
            runs.Add(["analyze", root, .. common, "--format", format, "--no-save"]);
        foreach (var rule in RuleCatalog.Every.Concat(RuleCatalog.Composite).Concat(RuleCatalog.Coverage))
            runs.Add(["explain", rule.Id]);

        var hits = new List<string>();
        var all = new System.Text.StringBuilder();
        foreach (var args in runs)
        {
            var r = host.Run(root, args);
            var name = "skarp " + string.Join(' ', args.Select(a => a.Replace(root, "<root>", StringComparison.Ordinal)));
            await Assert.That(r.Exit is 0 or 3).IsTrue().Because($"{name} exited {r.Exit}: {r.Err}");
            all.Append(r.Out);
            hits.AddRange(Hits(name, r.Out));
            hits.AddRange(Hits(name + " (stderr)", r.Err));
        }
        // The runs did produce the texts the guard is about.
        foreach (var expected in new[] { "(CodeScene: 70 lines)", "Thresholds used", "\"noticeSource\"", "Rules (skarp explain", "skarp check:" })
            await Assert.That(all.ToString()).Contains(expected);
        // The show handle of a finding shows the threshold's source.
        var handle = host.Run(root, "show", "--format", "json", "--include-notices", "--all").Out;
        foreach (Match m in Regex.Matches(handle, "\"handle\":\\s*\"([^\"]+)\"").Take(10))
            hits.AddRange(Hits("skarp show " + m.Groups[1].Value, host.Run(root, "show", m.Groups[1].Value).Out));
        // Written files: the baseline and the saved run.
        await Assert.That(host.Run(root, ["baseline", root, .. common]).Exit).IsEqualTo(0);
        foreach (var written in Directory.GetFiles(root, "*.json", SearchOption.AllDirectories))
            hits.AddRange(Hits(Path.GetRelativePath(root, written), File.ReadAllText(written)));

        await Assert.That(hits).IsEmpty().Because(string.Join("\n", hits));
    }

    [Test]
    public async Task Static_texts_have_no_internal_references()
    {
        var hits = new List<string>();
        foreach (var rule in RuleCatalog.Every.Concat(RuleCatalog.Composite).Concat(RuleCatalog.Coverage))
            hits.AddRange(Hits(rule.Id, rule.Title + "\n" + rule.Short + "\n" + rule.Long));
        foreach (var preset in Presets.All)
        {
            hits.AddRange(Hits("preset " + preset.Id, preset.Name + "\n" + preset.Description));
            foreach (var (key, t) in preset.Rules)
                foreach (var level in new[] { t.Notice, t.Warning })
                    if (level is not null) hits.AddRange(Hits($"preset {preset.Id} {key}", level.Source));
        }
        foreach (var set in GodClassPresets.Sets) hits.AddRange(Hits("god-class set " + set.Id, set.Source));
        foreach (var key in SettingsRegistry.All.Concat(SettingsRegistry.Generated).Concat(SettingsRegistry.Editable))
            hits.AddRange(Hits("setting " + key.Key, key.Description ?? ""));
        hits.AddRange(Hits("DuplicationText.AgentHint", DuplicationText.AgentHint));
        hits.AddRange(Hits("CheckText", SkarpSharp.Agent.CheckText.AboutSkarp + "\n" + SkarpSharp.Agent.CheckText.StopWayOut));
        await Assert.That(hits).IsEmpty().Because(string.Join("\n", hits));
    }

    [Test]
    public async Task Verified_output_files_have_no_internal_references()
    {
        var hits = new List<string>();
        var shapes = Path.Combine(Project, "Contracts", "shapes");
        foreach (var path in Directory.EnumerateFiles(Project, "*.verified.*", SearchOption.AllDirectories))
        {
            if (path.StartsWith(shapes, StringComparison.OrdinalIgnoreCase)) continue;
            var rel = Path.GetRelativePath(Project, path);
            if (rel.StartsWith("bin", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("obj", StringComparison.OrdinalIgnoreCase)) continue;
            hits.AddRange(Hits(rel, File.ReadAllText(path)));
        }
        await Assert.That(hits).IsEmpty().Because(string.Join("\n", hits));
    }
}
