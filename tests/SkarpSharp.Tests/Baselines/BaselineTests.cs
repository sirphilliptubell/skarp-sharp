using System.Text;
using System.Text.Json;
using SkarpSharp.Baselines;
using SkarpSharp.Commands;

namespace SkarpSharp.Tests.Baselines;

/// <summary>Baseline round trip: write, unchanged run all known, worsened, fixed, new, --new-only.</summary>
public sealed class BaselineTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "skarp-baseline-" + Guid.NewGuid().ToString("N"));

    public BaselineTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    internal static (int Exit, string Out, string Err, string Json) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var stdout = new MemoryStream();
        int exit = SkarpCommand.RunAsync(args, new CommandIO { Out = o, Error = e, OpenStdout = () => new NonClosing(stdout) }).GetAwaiter().GetResult();
        return (exit, o.ToString(), e.ToString(), Encoding.UTF8.GetString(stdout.ToArray()));
    }

    sealed class NonClosing(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }

    static string Method(string name, int ifs) =>
        $"    void {name}(bool b)\n    {{\n{string.Concat(Enumerable.Repeat("        if (b) { }\n", ifs))}    }}\n";

    void Write(string file, params string[] methods) =>
        File.WriteAllText(Path.Combine(_dir, file), "class " + Path.GetFileNameWithoutExtension(file) + "\n{\n" + string.Concat(methods) + "}\n");

    // The generated methods repeat one shape, so duplicate-code is switched off (it is not the subject here); so is
    // duplicate-code-near, which is on by default.
    string[] Args(string command, params string[] more) => [command, _dir, "--no-history", "--no-config", "--rule", "duplicate-code=off", "--rule", "duplicate-code-near=off", .. more];

    static JsonElement Findings(string json) => JsonDocument.Parse(json).RootElement.GetProperty("findings");

    [Test]
    public async Task Round_trip_known_worsened_new_fixed()
    {
        Write("A.cs", Method("Big", 20), Method("Small", 17));
        Write("B.cs", Method("Gone", 18));

        var b = Run(Args("baseline"));
        await Assert.That(b.Exit).IsEqualTo(0);
        await Assert.That(b.Out).Contains("skarp baseline: recorded 7 findings");
        var path = Path.Combine(_dir, Baseline.FileName);
        var lines = File.ReadAllLines(path);
        // Header, then the files by path, each with its findings one per line sorted by rule and place.
        await Assert.That(lines[0]).IsEqualTo("{");
        await Assert.That(lines[1]).IsEqualTo("  \"version\": 1,");
        await Assert.That(lines[2]).IsEqualTo("  \"type\": \"baseline\",");
        var entries = lines.Where(l => l.StartsWith("      { \"rule\": ", StringComparison.Ordinal)).ToList();
        await Assert.That(entries.Count).IsEqualTo(7); // complex-method and path-explosion of three methods, large-type of A (WMC)
        var paths = JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("files").EnumerateObject().Select(p => p.Name).ToList();
        await Assert.That(paths).IsEquivalentTo(paths.Order(StringComparer.Ordinal), CollectionOrdering.Matching);
        await Assert.That(lines.Any(l => l.StartsWith("      { \"rule\": \"complex-method\", \"place\": \"A::Big\", \"value\": 20 }", StringComparison.Ordinal))).IsTrue();
        await Assert.That(entries).DoesNotContain(l => l.Contains("line", StringComparison.OrdinalIgnoreCase));

        // Unchanged: everything known.
        var same = Run(Args("analyze", "--format", "json"));
        await Assert.That(same.Exit).IsEqualTo(0);
        foreach (var f in Findings(same.Json).EnumerateArray()) await Assert.That(f.GetProperty("baselineState").GetString()).IsEqualTo("known");
        await Assert.That(same.Err).Contains("(0 new, 0 worsened, 7 known, 0 fixed)");

        // Rewriting the baseline from an unchanged tree gives the same bytes (deterministic, merge-friendly).
        var before = File.ReadAllText(path);
        Run(Args("baseline"));
        await Assert.That(File.ReadAllText(path)).IsEqualTo(before);

        // Worsen Big, remove Gone, add New, shift every line.
        Write("A.cs", "    // a comment moves everything down\n\n" + Method("Big", 25), Method("Small", 17), Method("New", 16));
        File.Delete(Path.Combine(_dir, "B.cs"));
        var after = Run(Args("analyze", "--format", "json"));
        var states = Findings(after.Json).EnumerateArray().ToDictionary(f => f.GetProperty("id").GetString()!, f => f.GetProperty("baselineState").GetString());
        await Assert.That(states["complex-method|A.cs|A::Big"]).IsEqualTo("worsened");
        await Assert.That(states["complex-method|A.cs|A::Small"]).IsEqualTo("known");
        await Assert.That(states["complex-method|A.cs|A::New"]).IsEqualTo("new");
        var big = Findings(after.Json).EnumerateArray().First(f => f.GetProperty("id").GetString() == "complex-method|A.cs|A::Big");
        await Assert.That((big.GetProperty("value").GetDouble(), big.GetProperty("baselineValue").GetDouble())).IsEqualTo((25.0, 20.0));
        var summary = JsonDocument.Parse(after.Json).RootElement.GetProperty("baseline");
        await Assert.That((summary.GetProperty("entries").GetInt32(), summary.GetProperty("new").GetInt32(),
            summary.GetProperty("worsened").GetInt32(), summary.GetProperty("known").GetInt32(), summary.GetProperty("fixed").GetInt32())).IsEqualTo((7, 2, 3, 2, 2));
        // path-explosion of Big (2^25 > 2^20) and large-type of A (WMC) are worsened too; both of Gone are fixed.

        // Text report: new and worse counts first (the full counts with --verbose); with --new-only the change leads each
        // line and known findings are hidden.
        var human = Run(Args("analyze"));
        await Assert.That(human.Out).Contains("2 new, 3 worse than the baseline. ");
        await Assert.That(human.Out).Contains("  notice line ").And.Contains(" A.Small complex-method: ");
        await Assert.That(Run(Args("analyze", "--verbose")).Out).Contains("2 new, 3 worsened, 2 known, 2 fixed");
        var newOnly = Run(Args("analyze", "--new-only"));
        await Assert.That(newOnly.Out).Contains(" A.Big complex-method (notice): cognitive complexity 20 -> 25 (limit 15) [");
        await Assert.That(newOnly.Out).Contains("  new line ");
        await Assert.That(newOnly.Out).Contains("new or worse only");
        await Assert.That(newOnly.Out).DoesNotContain("A.Small");

        // The exit code never depends on the baseline; --no-baseline ignores it.
        await Assert.That(after.Exit).IsEqualTo(0);
        await Assert.That(Run(Args("analyze", "--format", "json", "--no-baseline")).Json).DoesNotContain("baselineState");
    }

    [Test]
    public async Task Baseline_path_option_errors_and_settings_notes()
    {
        Write("A.cs", Method("Big", 20));
        var custom = Path.Combine(_dir, "custom-baseline.json");
        await Assert.That(Run(Args("baseline", "--baseline", custom)).Exit).IsEqualTo(0);
        await Assert.That(File.Exists(custom)).IsTrue();
        await Assert.That(File.Exists(Path.Combine(_dir, Baseline.FileName))).IsFalse();
        var r = Run(Args("analyze", "--baseline", custom, "--preset", "pmd"));
        await Assert.That(r.Out).Contains("baseline recorded with preset default, this run uses pmd");

        var missing = Run(Args("analyze", "--baseline", Path.Combine(_dir, "nope.json")));
        await Assert.That(missing.Exit).IsEqualTo(2);
        await Assert.That(missing.Err).Contains("Baseline file not found");

        File.WriteAllText(Path.Combine(_dir, Baseline.FileName), "{ \"version\": 7, \"type\": \"baseline\", \"files\": {} }");
        var bad = Run(Args("analyze"));
        await Assert.That(bad.Exit).IsEqualTo(1);
        await Assert.That(bad.Err).Contains("not supported");

        // Without a baseline file --new-only shows everything, with a note.
        File.Delete(Path.Combine(_dir, Baseline.FileName));
        var none = Run(Args("analyze", "--new-only"));
        await Assert.That(none.Out).Contains("No baseline file: --new-only shows everything");
        await Assert.That(none.Out).Contains("A.Big");
    }

    [Test]
    public async Task Settings_fingerprint_flags_changed_rule_settings_once()
    {
        // The baseline header records a fingerprint of the effective rule settings.
        Write("A.cs", Method("Big", 20));
        await Assert.That(Run(Args("baseline")).Exit).IsEqualTo(0);
        var path = Path.Combine(_dir, Baseline.FileName);
        var fp = Baseline.Read(path).SettingsFingerprint;
        await Assert.That(fp).Matches("^[0-9a-f]{16}$");

        static JsonElement Summary(string json) => JsonDocument.Parse(json).RootElement.GetProperty("baseline");
        var same = Run(Args("analyze", "--format", "json"));
        await Assert.That(Summary(same.Json).GetProperty("settingsChanged").GetBoolean()).IsFalse();
        await Assert.That(Run(Args("analyze")).Out).DoesNotContain("rule settings changed");
        // An override that restates the value in effect is no change (the fingerprint uses effective thresholds).
        await Assert.That(Summary(Run(Args("analyze", "--format", "json", "--threshold", "complex-method.notice=15")).Json).GetProperty("settingsChanged").GetBoolean()).IsFalse();
        // Listing magic numbers changes only what is shown, not the finding set, so it is no change either.
        await Assert.That(Summary(Run(Args("analyze", "--format", "json", "--magic-numbers", "list")).Json).GetProperty("settingsChanged").GetBoolean()).IsFalse();

        foreach (var change in new[] { new[] { "--threshold", "complex-method.notice=10" }, ["--rule", "path-explosion=off"], ["--include-tests"],
            ["--exclude", "x/**"], ["--complex-metric", "cyclomatic"], ["--long-method-lines", "source"], ["--record-parameters", "check"],
            ["--preset", "pmd"] })
        {
            var json = Run(Args("analyze", ["--format", "json", .. change]));
            await Assert.That(Summary(json.Json).GetProperty("settingsChanged").GetBoolean()).IsTrue().Because(string.Join(" ", change));
            var human = Run(Args("analyze", change));
            await Assert.That(human.Out.Split('\n')).HasSingleItem(l => l.Contains("rule settings changed since the baseline", StringComparison.Ordinal));
            await Assert.That(human.Out).Contains("Baseline: rule settings changed since the baseline; new and fixed counts include settings effects; re-run `skarp baseline` to reset.");
        }

        // A baseline written before fingerprints: a notice that it is unknown, no error, no settingsChanged.
        File.WriteAllLines(path, File.ReadAllLines(path).Where(l => !l.Contains("settingsFingerprint", StringComparison.Ordinal)));
        var old = Run(Args("analyze", "--format", "json"));
        await Assert.That(old.Exit).IsEqualTo(0);
        await Assert.That(Summary(old.Json).TryGetProperty("settingsChanged", out _)).IsFalse();
        var oldHuman = Run(Args("analyze"));
        await Assert.That(oldHuman.Out).Contains("no rule-settings fingerprint");
        await Assert.That(oldHuman.Out).DoesNotContain("Baseline: rule settings changed since the baseline;");
    }

    [Test]
    public async Task Activity_rules_and_findings_without_value_are_never_worsened()
    {
        var baseline = new BaselineFile
        {
            Version = 1, Preset = "default",
            Findings =
            [
                new() { Id = "active-complex-file|a.cs|", Value = 3 },
                new() { Id = "empty-catch|a.cs|C::M" },
                new() { Id = "long-method|a.cs|C::M", Value = 80 },
            ],
        };
        var findings = new List<Model.Finding>
        {
            new() { Id = "active-complex-file|a.cs|", Rule = "active-complex-file", Value = 9 },
            new() { Id = "empty-catch|a.cs|C::M", Rule = "empty-catch" },
            new() { Id = "long-method|a.cs|C::M", Rule = "long-method", Value = 79 },
        };
        var s = Baseline.Compare(findings, baseline, "b", null);
        await Assert.That(findings.Select(f => f.BaselineState)).IsEquivalentTo(new string?[] { "known", "known", "known" }, CollectionOrdering.Matching);
        await Assert.That((s.New, s.Known, s.Worsened, s.Fixed)).IsEqualTo((0, 3, 0, 0));
        // Write and parse back.
        var text = Baseline.Write(findings, "1.2.3", new Model.FindingSettings { Preset = "default", ComplexMetric = "cognitive", LongMethodLines = "physical" });
        var back = Baseline.Parse(text, "b");
        await Assert.That(back.Findings.Select(e => (e.Id, e.Value))).IsEquivalentTo(findings.Select(f => (f.Id, f.Value)).OrderBy(x => x.Id, StringComparer.Ordinal), CollectionOrdering.Matching);
        await Assert.That(back.ToolVersion).IsEqualTo("1.2.3");
    }
}