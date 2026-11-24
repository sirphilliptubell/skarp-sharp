using System.Net.Sockets;
using System.Text.Json;
using SkarpSharp.Semantic;
using SkarpSharp.Tests.Baselines;

namespace SkarpSharp.Tests.Semantic;

/// <summary>
/// A TUnit skip attribute, used together with [Test]. Runs only when the .NET SDK can be started and nuget.org is reachable (the fixtures are restored); otherwise skipped
/// with the reason. With SKARP_SEMANTIC_FIXTURES=1 (CI) it never skips, so a broken environment fails the tests.
/// </summary>
public sealed class SemanticFixtureFactAttribute() : SkipAttribute("semantic fixture prerequisites missing")
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(Environment.GetEnvironmentVariable("SKARP_SEMANTIC_FIXTURES") != "1" && Prerequisites.Missing.Value is not null);

    protected override string GetSkipReason(TestRegisteredContext context) => Prerequisites.Missing.Value ?? Reason;

    static class Prerequisites
    {
        public static readonly Lazy<string?> Missing = new(() =>
        {
            var sdk = DotnetRunner.RunAsync(["--version"], Path.GetTempPath()).GetAwaiter().GetResult();
            if (!sdk.Succeeded) return "semantic fixtures need the .NET SDK: " + sdk.ErrorSummary();
            try
            {
                using var tcp = new TcpClient();
                if (!tcp.ConnectAsync("api.nuget.org", 443).Wait(TimeSpan.FromSeconds(5))) return "semantic fixtures need nuget.org (restore): no connection";
            }
            catch (Exception ex) when (ex is SocketException or AggregateException) { return "semantic fixtures need nuget.org (restore): " + ex.Message; }
            return null;
        });
    }
}

/// <summary>
/// The prototype's fixtures (Semantic/Fixtures) as permanent tests of the semantic tier: copied to a temporary folder
/// (solution and global.json files are stored with a <c>.fixture</c> suffix so they do not affect this repository),
/// restored, and analyzed in process. Covers solution choice, the restore check, compile errors, generators on and off,
/// a generator built before, a pinned SDK, target framework choice and file names, and the semantic baseline round trip
/// including the tier-missing case.
/// </summary>
// The tests share the restored fixture folders (analyze writes .skarp/ into them, MSBuild reads obj/), so they run
// one at a time as they did under xUnit (TUnit runs the tests of a class in parallel unless told otherwise).
[NotInParallel(nameof(SemanticFixtureTests))]
public class SemanticFixtureTests
{
    static readonly Lazy<string> Prepared = new(Prepare);

    static string Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SkarpSharp.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "SkarpSharp.Tests", "Semantic", "Fixtures");
    }

    static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f).EndsWith(".fixture", StringComparison.Ordinal) ? Path.GetFileName(f)[..^8] : Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from))
            if (Path.GetFileName(d) is not ("bin" or "obj")) Copy(d, Path.Combine(to, Path.GetFileName(d)));
    }

    static void Dotnet(string dir, params string[] args)
    {
        var r = DotnetRunner.RunAsync(args, dir).GetAwaiter().GetResult();
        if (!r.Succeeded) throw new InvalidOperationException($"dotnet {string.Join(' ', args)} in {dir} failed: {r.ErrorSummary()}");
    }

    static string Prepare()
    {
        var root = Path.Combine(Path.GetTempPath(), "skarp-fx-" + Guid.NewGuid().ToString("N")[..8]);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } };
        Copy(Source(), root);
        Copy(Path.Combine(root, "lib"), Path.Combine(root, "unrestored"));
        var sln = Path.Combine(root, "twoeq-sln");
        Copy(Path.Combine(root, "twoeq", "Lib"), Path.Combine(sln, "Lib"));
        File.Copy(Path.Combine(root, "twoeq", "Lib_2020.sln"), Path.Combine(sln, "Lib_2020.sln"));
        File.Copy(Path.Combine(root, "twoeq", "Lib_2020.sln"), Path.Combine(sln, "Lib_2024.sln"));
        File.SetLastWriteTimeUtc(Path.Combine(sln, "Lib_2020.sln"), new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        foreach (var (dir, solution) in new[] { ("lib", "Lib.slnx"), ("errors", "Err.slnx"), ("gen", "Gen.slnx"), ("twoeq", "Lib.slnx"), ("twoeq-sln", "Lib_2024.sln"), ("twodiff", "All.slnx"), ("multi", "Multi.slnx"), ("custom", "Custom.slnx"), ("unused", "Unused.slnx") })
            Dotnet(Path.Combine(root, dir), "restore", solution, "-nologo");
        Copy(Path.Combine(root, "gen"), Path.Combine(root, "gen-built"));
        Dotnet(Path.Combine(root, "gen-built"), "restore", "Gen.slnx", "-nologo");
        Dotnet(Path.Combine(root, "gen-built"), "build", Path.Combine("Gen.Generator", "Gen.Generator.csproj"), "-nologo");
        return root;
    }

    static string Fixture(string name) => Path.Combine(Prepared.Value, name);

    /// <summary>Runs <c>skarp analyze</c> with JSON output; returns the exit code, the syntax run header's semantic entry and the semantic file(s).</summary>
    static (int Exit, JsonElement Tier, List<JsonDocument> Files, string Err) Analyze(string dir, params string[] more)
    {
        var outDir = Directory.CreateTempSubdirectory("skarp-fxout").FullName;
        var output = Path.Combine(outDir, "r.json");
        var r = BaselineTests.Run(["analyze", dir, "--no-history", "--no-config", "--format", "json", "-o", output, .. more]);
        var tier = JsonDocument.Parse(File.ReadAllText(output)).RootElement.GetProperty("run").GetProperty("semantic").Clone();
        var files = Directory.GetFiles(outDir, "r.semantic*.json").Order(StringComparer.Ordinal).Select(f => JsonDocument.Parse(File.ReadAllText(f))).ToList();
        Directory.Delete(outDir, true);
        return (r.Exit, tier, files, r.Err);
    }

    static string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

    static IEnumerable<JsonElement> Types(JsonDocument d) => d.RootElement.GetProperty("types").EnumerateArray();

    static JsonElement Type(JsonDocument d, string name) => Types(d).Single(t => S(t, "name") == name);

    /// <summary>With the semantic tier, every score is <c>full</c>, keeps its <c>syntaxScore</c>, and the
    /// semantic findings lower the full score of the file whose path they carry.</summary>
    [Test, SemanticFixtureFact]
    public async Task Full_scores_add_the_semantic_findings_to_the_syntax_score()
    {
        var outDir = Directory.CreateTempSubdirectory("skarp-fxhealth").FullName;
        var output = Path.Combine(outDir, "r.json");
        var r = BaselineTests.Run(["analyze", Fixture("unused"), "--no-history", "--no-config", "--no-save", "--format", "json", "-o", output]);
        await Assert.That(r.Exit).IsEqualTo(0);
        using var doc = JsonDocument.Parse(File.ReadAllText(output));
        var semantic = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(outDir, "r.semantic*.json").Single()));
        Directory.Delete(outDir, true);
        await Assert.That(doc.RootElement.GetProperty("health").GetProperty("kind").GetString()).IsEqualTo("full");
        var lowered = semantic.RootElement.GetProperty("findings").EnumerateArray()
            .Where(f => S(f, "rule") is "unused-member" or "unused-type" or "unread-member" or "god-class" or "high-coupling")
            .Select(f => S(f, "path")).ToHashSet(StringComparer.Ordinal);
        await Assert.That(lowered).IsNotEmpty();
        foreach (var file in doc.RootElement.GetProperty("files").EnumerateArray())
        {
            var h = file.GetProperty("health");
            await Assert.That(S(h, "kind")).IsEqualTo("full");
            double score = h.GetProperty("score").GetDouble(), syntax = h.GetProperty("syntaxScore").GetDouble();
            if (lowered.Contains(S(file, "path"))) await Assert.That(score < syntax).IsTrue().Because($"{S(file, "path")}: {score} < {syntax}");
            else await Assert.That(score).IsEqualTo(syntax);
        }
    }

    [Test, SemanticFixtureFact]
    public async Task Restored_library_is_measured()
    {
        var r = Analyze(Fixture("lib"));
        await Assert.That(r.Exit).IsEqualTo(0);
        await Assert.That((S(r.Tier, "status"), S(r.Tier, "solution"))).IsEqualTo(("ran", "Lib.slnx"));
        var file = await Assert.That(r.Files).HasSingleItem();
        var run = file.RootElement.GetProperty("run");
        await Assert.That(S(run, "solutionReason")).IsEqualTo("the only solution");
        await Assert.That(run.TryGetProperty("targetFramework", out var tf) ? tf.ValueKind : JsonValueKind.Undefined).IsEqualTo(JsonValueKind.Undefined);
        var order = Type(file, "Fx.Lib.Order");
        await Assert.That(S(order, "targetFramework")).IsEqualTo("net10.0");
        await Assert.That(order.GetProperty("cbo").GetInt32() >= 1).IsTrue();
        await Assert.That(file.RootElement.GetProperty("notEvaluated").EnumerateArray()).IsEmpty();
        var human = BaselineTests.Run("analyze", Fixture("lib"), "--no-history", "--no-config", "--verbose");
        await Assert.That(human.Out).Contains("semantic:   ran on Lib.slnx; target framework net10.0;");
        await Assert.That(human.Out).Contains("Semantic tier: ");
    }

    [Test, SemanticFixtureFact]
    public async Task Unrestored_repository_runs_syntax_only_with_a_hint_and_restore_fixes_it()
    {
        var r = Analyze(Fixture("unrestored"));
        await Assert.That(r.Exit).IsEqualTo(0);
        await Assert.That(S(r.Tier, "status")).IsEqualTo("not evaluated");
        await Assert.That(S(r.Tier, "reason")).StartsWith("not restored: Lib/Lib.csproj");
        await Assert.That(S(r.Tier, "hint")).Contains("--restore");
        await Assert.That(r.Files).IsEmpty();
        var copy = Path.Combine(Prepared.Value, "unrestored-" + Guid.NewGuid().ToString("N")[..6]);
        Copy(Fixture("unrestored"), copy);
        var restored = Analyze(copy, "--restore");
        await Assert.That(S(restored.Tier, "status")).IsEqualTo("ran");
    }

    [Test, SemanticFixtureFact]
    public async Task Compile_errors_unresolved_symbols_are_not_evaluated()
    {
        var r = Analyze(Fixture("errors"));
        var file = await Assert.That(r.Files).HasSingleItem();
        var ne = await Assert.That(file.RootElement.GetProperty("notEvaluated").EnumerateArray()).HasSingleItem();
        await Assert.That((S(ne, "name"), S(ne, "reason"))).IsEqualTo(("Fx.Err.UsesMissing", "unresolved symbols"));
        await Assert.That(Types(file)).Contains(t => S(t, "name") == "Fx.Err.TypeError");
        var project = file.RootElement.GetProperty("projects")[0];
        await Assert.That(project.GetProperty("errors").GetInt32()).IsEqualTo(3);
        await Assert.That(file.RootElement.GetProperty("notEvaluatedReasons")[0].GetProperty("types").GetInt32()).IsEqualTo(1);
    }

    [Test, SemanticFixtureFact]
    public async Task In_solution_generator_runs_and_generated_types_count_as_targets()
    {
        var r = Analyze(Fixture("gen"));
        var file = await Assert.That(r.Files).HasSingleItem();
        await Assert.That(file.RootElement.GetProperty("notEvaluated").EnumerateArray()).IsEmpty();
        var uses = Type(file, "Gen.App.UsesGenerated");
        await Assert.That(uses.GetProperty("coupledTypes").EnumerateArray().Select(x => x.GetString()).Order(StringComparer.Ordinal)).IsEquivalentTo(new string?[] { "Hello", "Widget" }, CollectionOrdering.Matching);
        await Assert.That(Types(file)).DoesNotContain(t => S(t, "name").StartsWith("Generated.", StringComparison.Ordinal)); // generated code is not measured

        var off = Analyze(Fixture("gen"), "--no-generators");
        var ne = await Assert.That((await Assert.That(off.Files).HasSingleItem()).RootElement.GetProperty("notEvaluated").EnumerateArray()).HasSingleItem();
        await Assert.That(S(ne, "name")).IsEqualTo("Gen.App.UsesGenerated");
        await Assert.That(S(ne, "reason")).Contains("--no-generators");

        var built = Analyze(Fixture("gen-built"));
        await Assert.That((await Assert.That(built.Files).HasSingleItem()).RootElement.GetProperty("notEvaluated").EnumerateArray()).IsEmpty();
    }

    [Test, SemanticFixtureFact]
    public async Task Pinned_sdk_that_is_not_installed_is_explained()
    {
        var r = Analyze(Fixture("pinned"));
        await Assert.That(r.Exit).IsEqualTo(0);
        await Assert.That(S(r.Tier, "status")).IsEqualTo("not evaluated");
        await Assert.That(S(r.Tier, "reason")).Contains("12.0.100");
        await Assert.That(S(r.Tier, "hint")).Contains("global.json");
    }

    [Test, SemanticFixtureFact]
    public async Task Solution_choice_on_real_solution_files()
    {
        var eq = Analyze(Fixture("twoeq"));
        await Assert.That(S(eq.Tier, "solution")).IsEqualTo("Lib.slnx");
        await Assert.That(S(eq.Files[0].RootElement.GetProperty("run"), "solutionReason")).Contains(".slnx preferred");
        await Assert.That(S(Analyze(Fixture("twoeq-sln")).Tier, "solution")).IsEqualTo("Lib_2024.sln");
        var diff = Analyze(Fixture("twodiff"));
        await Assert.That(S(diff.Tier, "solution")).IsEqualTo("All.slnx");
        await Assert.That(diff.Files[0].RootElement.GetProperty("projects").GetArrayLength()).IsEqualTo(2);
        var chosen = Analyze(Fixture("twodiff"), "--solution", Path.Combine(Fixture("twodiff"), "P1.slnx"));
        await Assert.That(S(chosen.Tier, "solution")).IsEqualTo("P1.slnx");
        await Assert.That(chosen.Files[0].RootElement.GetProperty("projects").GetArrayLength()).IsEqualTo(1);
    }

    [Test, SemanticFixtureFact]
    public async Task Target_frameworks_and_output_file_names()
    {
        var highest = Analyze(Fixture("multi"));
        var one = await Assert.That(highest.Files).HasSingleItem();
        await Assert.That(S(one.RootElement.GetProperty("projects")[0], "targetFramework")).IsEqualTo("net10.0");
        await Assert.That(highest.Tier.GetProperty("targetFrameworks").EnumerateArray().Select(x => x.GetString())).IsEquivalentTo(new string?[] { "net10.0" }, CollectionOrdering.Matching);

        var all = Analyze(Fixture("multi"), "--all-targets");
        await Assert.That(all.Files.Count).IsEqualTo(2);
        await Assert.That(all.Files.Select(f => S(f.RootElement.GetProperty("run"), "targetFramework"))).IsEquivalentTo(["net10.0", "net9.0"], CollectionOrdering.Matching);
        foreach (var o in all.Tier.GetProperty("outputs").EnumerateArray()) await Assert.That(o.GetString()!).Matches(@"r\.semantic\.net(9|10)\.0\.json$");

        var nine = Analyze(Fixture("multi"), "--target", "net9.0");
        await Assert.That(S((await Assert.That(nine.Files).HasSingleItem()).RootElement.GetProperty("projects")[0], "targetFramework")).IsEqualTo("net9.0");
    }

    [Test, SemanticFixtureFact]
    public async Task Repository_custom_msbuild_extension_points_still_apply()
    {
        // The repository sets CustomBeforeMicrosoftCommonTargets (a define), CustomAfterMicrosoftCommonTargets (a source file
        // outside the project folder) and CustomAfterMicrosoftCommonCrossTargetingTargets (a marker of the outer build).
        // The design-time build must import them as a normal build does, not replace them with its own targets.
        var marker = Path.Combine(Fixture("custom"), "Custom", "obj", "repo-cross-targeting.marker");
        if (File.Exists(marker)) File.Delete(marker);
        var r = Analyze(Fixture("custom"));
        await Assert.That(S(r.Tier, "status")).IsEqualTo("ran");
        var file = await Assert.That(r.Files).HasSingleItem();
        await Assert.That(file.RootElement.GetProperty("notEvaluated").EnumerateArray()).IsEmpty();
        await Assert.That(Type(file, "Fx.Custom.UsesExtra").GetProperty("coupledTypes").EnumerateArray().Select(x => x.GetString())).IsEquivalentTo(new string?[] { "Extra" }, CollectionOrdering.Matching);
        await Assert.That(Types(file)).Contains(t => S(t, "name") == "Fx.Custom.FromBefore");
        await Assert.That(File.Exists(marker)).IsTrue().Because("the repository's cross-targeting targets did not run");
    }

    [Test, SemanticFixtureFact]
    public async Task Semantic_baseline_round_trip_gate_and_missing_tier()
    {
        var dir = Path.Combine(Prepared.Value, "lib-baseline-" + Guid.NewGuid().ToString("N")[..6]);
        Copy(Fixture("lib"), dir);
        Dotnet(dir, "restore", "Lib.slnx", "-nologo");
        string[] common = ["--no-history", "--no-config", "--threshold", "high-coupling.notice=0"];
        var b = BaselineTests.Run(["baseline", dir, .. common]);
        await Assert.That(b.Exit).IsEqualTo(0);
        await Assert.That(b.Out).Contains("semantic baseline");
        var semanticBaseline = Path.Combine(dir, ".skarp-baseline.semantic.json");
        var text = File.ReadAllText(semanticBaseline);
        await Assert.That(text).Contains("\"type\": \"semantic-baseline\"");
        await Assert.That(text).Contains("\"Lib/Order.cs\": [").And.Contains("{ \"rule\": \"high-coupling\", \"place\": \"Fx.Lib.");

        var same = Analyze(dir, [.. common, "--gate", "--gate-on", "*:*"]);
        await Assert.That(same.Exit).IsEqualTo(0);
        var baseline = same.Files[0].RootElement.GetProperty("baseline");
        await Assert.That((baseline.GetProperty("new").GetInt32(), baseline.GetProperty("fixed").GetInt32())).IsEqualTo((0, 0));
        await Assert.That(same.Files[0].RootElement.GetProperty("gate").GetProperty("tripped").GetBoolean()).IsFalse();

        // The tier did not run: no false fixed or new; the gate ignores semantic findings.
        var missing = BaselineTests.Run(["analyze", dir, .. common, "--no-semantic", "--gate", "--gate-on", "*:*"]);
        await Assert.That(missing.Exit).IsEqualTo(0);
        await Assert.That(missing.Out).Contains("semantic baseline findings not evaluated (neither fixed nor new; the gate ignores them)");

        // A new coupling makes a new semantic finding: the gate trips.
        File.AppendAllText(Path.Combine(dir, "Lib", "Order.cs"), "\nnamespace Fx.Lib { public class Extra { public Order? O; } }\n");
        var trip = Analyze(dir, [.. common, "--gate", "--gate-on", "*:*"]);
        await Assert.That(trip.Exit).IsEqualTo(3);
        await Assert.That(trip.Files[0].RootElement.GetProperty("gate").GetProperty("tripped").GetBoolean()).IsTrue();
    }

    // ---- unused code ---------------------------------------------------------------------------------------

    static List<JsonElement> Findings(IEnumerable<JsonDocument> files) => [.. files.SelectMany(f => f.RootElement.GetProperty("findings").EnumerateArray())];

    static string Place(JsonElement f) => S(f, "rule") + " " + S(f, "member");

    [Test, SemanticFixtureFact]
    public async Task Unused_code_with_a_test_project_and_all_targets_matches_the_IDE0051_oracle()
    {
        var r = Analyze(Fixture("unused"), "--all-targets");
        await Assert.That(r.Exit).IsEqualTo(0);
        await Assert.That(r.Files.Count).IsEqualTo(2);
        var unused = Findings(r.Files).Where(f => S(f, "rule") is "unused-member" or "unused-type" or "used-only-by-tests" or "unread-member").Select(Place).Order(StringComparer.Ordinal).ToList();
        await Assert.That(unused).IsEquivalentTo([
            "unread-member Fx.Unused.UnreadCases::Auto", "unread-member Fx.Unused.UnreadCases::InternalWritten", "unread-member Fx.Unused.UnreadCases::_count",
            "unread-member Fx.Unused.UnreadCases::_readByDead", "unread-member Fx.Unused.UnreadCases::_written",
            "unused-member Fx.Unused.A1::.ctor", "unused-member Fx.Unused.A1::Helper", "unused-member Fx.Unused.A1::Nowhere", "unused-member Fx.Unused.A1::Prop",
            "unused-member Fx.Unused.A1::Rec", "unused-member Fx.Unused.A1::WithNeutral", "unused-member Fx.Unused.A1::_a",
            "unused-member Fx.Unused.UnreadCases::Dead", "unused-member Fx.Unused.UnreadCases::Head", "unused-member Fx.Unused.UnreadCases::Mid",
            "unused-type Fx.Unused.Holder+PrivateNested", "unused-type Fx.Unused.Unreferenced", "used-only-by-tests Fx.Unused.A1::OnlyTests",
            "used-only-by-tests Fx.Unused.UnreadCases::ReadByTests"], CollectionOrdering.Matching);
        var summary = r.Files[0].RootElement.GetProperty("unusedCode");
        await Assert.That(summary.GetProperty("notEvaluated").GetInt32()).IsEqualTo(0);
        await Assert.That(summary.GetProperty("projects").EnumerateArray()).Contains(p => S(p, "path") == "Lib.Tests/Lib.Tests.csproj" && p.GetProperty("test").GetBoolean());

        // On private members our findings equal IDE0051's apart from the documented differences.
        var copy = Path.Combine(Prepared.Value, "unused-ide-" + Guid.NewGuid().ToString("N")[..6]);
        Copy(Fixture("unused"), copy);
        Dotnet(copy, "restore", "Unused.slnx", "-nologo");
        var build = DotnetRunner.RunAsync(["build", Path.Combine("Lib", "Lib.csproj"), "-f", "net10.0", "-p:EnforceCodeStyleInBuild=true", "--no-incremental", "-nologo"], copy)
            .GetAwaiter().GetResult();
        await Assert.That(build.Succeeded).IsTrue().Because(build.ErrorSummary());
        var ide = System.Text.RegularExpressions.Regex.Matches(build.StandardOutput, @"Code\.cs\((\d+),\d+\): warning IDE0051")
            .Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
        var ours = PrivateLines(r.Files, "unused-member", "Lib/Code.cs");
        await Assert.That(ide.Except(ours).Order()).IsEquivalentTo([16, 22], CollectionOrdering.Matching);   // WithAttribute (an attribute roots it), ForOld (used in net9.0 only)
        await Assert.That(ours.Except(ide).Order()).IsEquivalentTo([14], CollectionOrdering.Matching);       // Rec: self-recursion is not a use for us

        // Unread-member against IDE0052 on private members.
        var ide52 = System.Text.RegularExpressions.Regex.Matches(build.StandardOutput, @"Unread\.cs\((\d+),\d+\): warning IDE0052")
            .Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
        var unread = PrivateLines(r.Files, "unread-member", "Lib/Unread.cs");
        await Assert.That(ide52.Except(unread).Order()).IsEquivalentTo([9, 12], CollectionOrdering.Matching);  // _lock, _timer: object and IDisposable fields keep a value alive
        await Assert.That(unread.Except(ide52).Order()).IsEquivalentTo([10, 14], CollectionOrdering.Matching); // Auto: Roslyn counts no property initializer; _readByDead: read only by unused code
        // Transitive deadness: Mid is used only by the unused Head (IDE0051 counts that use).
        var ide51 = System.Text.RegularExpressions.Regex.Matches(build.StandardOutput, @"Unread\.cs\((\d+),\d+\): warning IDE0051")
            .Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
        var unusedHere = PrivateLines(r.Files, "unused-member", "Lib/Unread.cs");
        await Assert.That(ide51.Except(unusedHere).Order()).IsEquivalentTo([10], CollectionOrdering.Matching); // Auto: unread for us
        await Assert.That(unusedHere.Except(ide51).Order()).IsEquivalentTo([18], CollectionOrdering.Matching); // Mid
        var mid = Findings(r.Files).Single(f => S(f, "member") == "Fx.Unused.UnreadCases::Mid");
        await Assert.That(mid.GetProperty("depth").GetInt32()).IsEqualTo(1);
        await Assert.That(mid.GetProperty("usedOnlyBy")[0].GetString()).IsEqualTo("Fx.Unused.UnreadCases::Head");
    }

    static HashSet<int> PrivateLines(IEnumerable<JsonDocument> files, string rule, string path) =>
        [.. Findings(files).Where(f => S(f, "rule") == rule && S(f, "path") == path && S(f, "message").StartsWith("private", StringComparison.Ordinal))
            .Select(f => f.GetProperty("line").GetInt32())];

    [Test, SemanticFixtureFact]
    public async Task Unused_code_of_a_multi_targeted_project_is_reported_from_the_highest_target_without_all_targets()
    {
        // Net10.0 alone is analyzed; ForOld (used in an inactive #if block) and ViaOldFile (used only from
        // Old/OldOnly.cs, compiled for net9.0 only) are not reported. The findings are the --all-targets ones except the
        // private constructor A1(int): OldOnly.cs names A1 (new A1()), and a constructor's name is its type's (a miss
        // accepted for certainty, as with inactive #if text).
        var r = Analyze(Fixture("unused"));
        var file = await Assert.That(r.Files).HasSingleItem();
        var findings = Findings(r.Files).Where(f => S(f, "rule") is "unused-member" or "unused-type" or "used-only-by-tests" or "unread-member").ToList();
        await Assert.That(findings.Select(Place).Order(StringComparer.Ordinal)).IsEquivalentTo([
            "unread-member Fx.Unused.UnreadCases::Auto", "unread-member Fx.Unused.UnreadCases::InternalWritten", "unread-member Fx.Unused.UnreadCases::_count",
            "unread-member Fx.Unused.UnreadCases::_readByDead", "unread-member Fx.Unused.UnreadCases::_written",
            "unused-member Fx.Unused.A1::Helper", "unused-member Fx.Unused.A1::Nowhere", "unused-member Fx.Unused.A1::Prop",
            "unused-member Fx.Unused.A1::Rec", "unused-member Fx.Unused.A1::WithNeutral", "unused-member Fx.Unused.A1::_a",
            "unused-member Fx.Unused.UnreadCases::Dead", "unused-member Fx.Unused.UnreadCases::Head", "unused-member Fx.Unused.UnreadCases::Mid",
            "unused-type Fx.Unused.Holder+PrivateNested", "unused-type Fx.Unused.Unreferenced", "used-only-by-tests Fx.Unused.A1::OnlyTests",
            "used-only-by-tests Fx.Unused.UnreadCases::ReadByTests"], CollectionOrdering.Matching);
        foreach (var f in findings) await Assert.That(S(f, "message")).EndsWith("(net10.0 only; --all-targets for all)");
        var summary = file.RootElement.GetProperty("unusedCode");
        await Assert.That(summary.GetProperty("notEvaluated").GetInt32()).IsEqualTo(0);
        var lib = summary.GetProperty("projects").EnumerateArray().Single(p => S(p, "path") == "Lib/Lib.csproj");
        await Assert.That(lib.GetProperty("targetFrameworksNotAnalyzed")[0].GetString()).IsEqualTo("net9.0");
        var human = BaselineTests.Run("analyze", Fixture("unused"), "--no-history", "--no-config", "--min-severity", "notice", "--verbose");
        await Assert.That(human.Out).Contains("Unused code: ");
        await Assert.That(human.Out).Contains("Lib/Lib.csproj: net10.0 of net10.0, net9.0");
        await Assert.That(human.Out).Contains("(net10.0 only; --all-targets for all)");
    }

    [Test, SemanticFixtureFact]
    public async Task Unused_code_baseline_round_trip_gates_new_unused_code_but_never_the_test_only_label()
    {
        var dir = Path.Combine(Prepared.Value, "unused-baseline-" + Guid.NewGuid().ToString("N")[..6]);
        Copy(Fixture("unused"), dir);
        Dotnet(dir, "restore", "Unused.slnx", "-nologo");
        string[] common = ["--no-history", "--no-config", "--all-targets"];
        await Assert.That(BaselineTests.Run(["baseline", dir, .. common]).Exit).IsEqualTo(0);
        var text = File.ReadAllText(Path.Combine(dir, ".skarp-baseline.semantic.net10.0.json"));
        await Assert.That(text).Contains("{ \"rule\": \"unused-member\", \"place\": \"Fx.Unused.A1::Helper\"");
        await Assert.That(text).Contains("{ \"rule\": \"used-only-by-tests\", \"place\": \"Fx.Unused.A1::OnlyTests\"");
        await Assert.That(Analyze(dir, [.. common, "--gate", "--gate-on", "unused-member:notice", "--gate-on", "unused-type:notice"]).Exit).IsEqualTo(0);

        // A new member used only by tests: a new label, never gated, not even with every rule in the gate set.
        File.WriteAllText(Path.Combine(dir, "Lib", "Hook.cs"), "namespace Fx.Unused { public class Hook { internal void ForTests2() { } } }\n");
        File.WriteAllText(Path.Combine(dir, "Lib.Tests", "More.cs"), "namespace Fx.Unused.Tests { public class More { public void T() => new Hook().ForTests2(); } }\n");
        var label = Analyze(dir, [.. common, "--gate", "--gate-on", "*:*"]);
        await Assert.That(label.Exit).IsEqualTo(0);
        await Assert.That(Findings(label.Files)).Contains(f => S(f, "member") == "Fx.Unused.Hook::ForTests2" && S(f, "baselineState") == "new");

        // A new unused private method trips the gate configured for unused members.
        File.WriteAllText(Path.Combine(dir, "Lib", "Late.cs"), "namespace Fx.Unused { public class Late { private void Dead() { } } }\n");
        var trip = Analyze(dir, [.. common, "--gate", "--gate-on", "unused-member:notice"]);
        await Assert.That(trip.Exit).IsEqualTo(3);
        await Assert.That(Findings(trip.Files)).Contains(f => S(f, "member") == "Fx.Unused.Late::Dead" && S(f, "baselineState") == "new");
    }

    // ---- SARIF ------------------------------------------------------------------------------------

    [Test, SemanticFixtureFact]
    public async Task Sarif_holds_both_tiers_with_baseline_states_and_validates()
    {
        var dir = Path.Combine(Prepared.Value, "unused-sarif-" + Guid.NewGuid().ToString("N")[..6]);
        Copy(Fixture("unused"), dir);
        Dotnet(dir, "restore", "Unused.slnx", "-nologo");
        // A public method with eight parameters: a syntax-tier warning next to the fixture's semantic findings.
        File.WriteAllText(Path.Combine(dir, "Lib", "Wide.cs"),
            "namespace Fx.Unused { public class Wide { public int Sum(int a, int b, int c, int d, int e, int f, int g, int h) => a + b + c + d + e + f + g + h; } }\n");
        string[] common = ["--no-history", "--no-config"];
        var plain = BaselineTests.Run(["analyze", dir, .. common, "--format", "sarif"]);
        await Assert.That(plain.Exit).IsEqualTo(0);
        await SkarpSharp.Tests.SarifOutput.SarifTests.AssertValid(plain.Json, "sarif of the unused fixture");
        var run = JsonDocument.Parse(plain.Json).RootElement.GetProperty("runs")[0];
        var results = run.GetProperty("results").EnumerateArray().ToList();
        await Assert.That(results).Contains(r => r.GetProperty("properties").GetProperty("tier").GetString() == "semantic");
        await Assert.That(results).Contains(r => r.GetProperty("properties").GetProperty("tier").GetString() == "syntax" && S(r, "ruleId") == "too-many-parameters" && S(r, "level") == "warning");
        await Assert.That(results).Contains(r => S(r, "ruleId") == "unused-member" && S(r, "level") == "note");
        await Assert.That(S(run.GetProperty("properties"), "semanticTier")).IsEqualTo("ran");
        // Outside a git work tree SRCROOT is the analyzed directory and the URIs are relative to it.
        foreach (var r in results) await Assert.That(S(r.GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("artifactLocation"), "uri")).DoesNotContain(":");
        await Assert.That(results).All(r => !(r.TryGetProperty("baselineState", out _)));

        // After `skarp baseline` both tiers are compared: every result is unchanged.
        await Assert.That(BaselineTests.Run(["baseline", dir, .. common]).Exit).IsEqualTo(0);
        var again = BaselineTests.Run(["analyze", dir, .. common, "--format", "sarif"]);
        await SkarpSharp.Tests.SarifOutput.SarifTests.AssertValid(again.Json, "sarif with both baselines");
        var states = JsonDocument.Parse(again.Json).RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray().Select(r => S(r, "baselineState")).Distinct().ToList();
        await Assert.That(states).IsEquivalentTo(["unchanged"], CollectionOrdering.Matching);
    }
}