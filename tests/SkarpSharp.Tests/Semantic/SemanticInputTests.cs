using SkarpSharp.Baselines;
using SkarpSharp.Configuration;
using SkarpSharp.Model;
using SkarpSharp.Semantic;
using SkarpSharp.Tests.Baselines;

namespace SkarpSharp.Tests.Semantic;

/// <summary>Semantic-tier inputs without MSBuild: solution choice, target framework order, record parsing,
/// Options and settings, and the semantic baseline.</summary>
public class SemanticInputTests
{
    static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "skarp-choice"));
    static string P(params string[] parts) => Path.Combine([Root, .. parts]);
    static SolutionListing L(string rel, int day, params string[] projects) =>
        new(P(rel), new DateTime(2026, 1, day, 0, 0, 0, DateTimeKind.Utc), [.. projects.Select(p => P(p)).Order(StringComparer.OrdinalIgnoreCase)], null);

    [Test]
    public async Task One_solution_is_used()
    {
        var d = SolutionChoice.Choose(Root, [L("A.slnx", 1, "A/A.csproj")]);
        await Assert.That((d.Chosen, d.Reason)).IsEqualTo((P("A.slnx"), "the only solution"));
    }

    [Test]
    public async Task Equivalent_solutions_prefer_slnx_then_the_most_recent()
    {
        var d = SolutionChoice.Choose(Root, [L("Lib_2020.sln", 9, "Lib/Lib.csproj"), L("Lib.slnx", 1, "Lib/Lib.csproj")]);
        await Assert.That(d.Chosen).IsEqualTo(P("Lib.slnx"));
        await Assert.That(d.Reason).Contains(".slnx preferred");
        d = SolutionChoice.Choose(Root, [L("Lib_2020.sln", 1, "Lib/Lib.csproj"), L("Lib_2024.sln", 2, "Lib/Lib.csproj")]);
        await Assert.That(d.Chosen).IsEqualTo(P("Lib_2024.sln"));
        await Assert.That(d.Reason).Contains("most recently modified");
    }

    [Test]
    public async Task A_superset_solution_wins_then_the_root_one()
    {
        var d = SolutionChoice.Choose(Root, [L("All.slnx", 1, "P1/P1.csproj", "P2/P2.csproj"), L("P1.slnx", 2, "P1/P1.csproj")]);
        await Assert.That(d.Chosen).IsEqualTo(P("All.slnx"));
        d = SolutionChoice.Choose(Root, [L("Main.slnx", 1, "P1/P1.csproj"), L("samples/S.slnx", 2, "samples/S/S.csproj")]);
        await Assert.That((d.Chosen, d.Reason)).IsEqualTo((P("Main.slnx"), "the solution at the analyzed directory's root"));
    }

    [Test]
    public async Task Ambiguous_none_and_unlistable_solutions_explain_why()
    {
        var d = SolutionChoice.Choose(Root, [L("a/A.slnx", 1, "a/A.csproj"), L("b/B.slnx", 2, "b/B.csproj")]);
        await Assert.That(d.Chosen).IsNull();
        await Assert.That(d.Reason).IsEqualTo("2 different solutions (a/A.slnx [1 projects], b/B.slnx [1 projects])");
        await Assert.That(d.Hint).Contains("--solution");
        d = SolutionChoice.Choose(Root, []);
        await Assert.That(d.Reason).IsEqualTo("no .sln or .slnx file found");
        d = SolutionChoice.Choose(Root, [new SolutionListing(P("X.slnx"), DateTime.UtcNow, null, "A compatible .NET SDK was not found. | Requested SDK version: 12.0.100")]);
        await Assert.That(d.Chosen).IsNull();
        await Assert.That(d.Reason).StartsWith("no solution could be listed (X.slnx: A compatible");
        await Assert.That(d.Hint).Contains("global.json");
    }

    [Test]
    public async Task Sln_list_output_is_read_after_the_dashes()
    {
        var output = "Project(s)\n----------\nLib\\Lib.csproj\nTools/Tool.vbproj\n\n";
        await Assert.That(SolutionChoice.ParseList(output, Root)).IsEquivalentTo([P("Lib", "Lib.csproj"), P("Tools", "Tool.vbproj")], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Target_frameworks_order_highest_first()
    {
        await Assert.That(TargetFrameworks.Ordered(["net20", "netstandard2.0", "net48", "net10.0-windows", "netcoreapp3.1", "net9.0", "netstandard2.1", "net10.0"])).IsEquivalentTo(["net10.0", "net10.0-windows", "net9.0", "netcoreapp3.1", "netstandard2.1", "netstandard2.0", "net48", "net20"], CollectionOrdering.Matching);
        await Assert.That(TargetFrameworks.Highest(["net6.0"])).IsEqualTo("net6.0");
    }

    [Test]
    public async Task Record_files_are_parsed()
    {
        var r = DesignTimeBuild.Parse(["skarp-args-1", "C:/r/A.csproj", "net10.0", "A", "assets:C:/r/obj/project.assets.json", "roslyn:C:/sdk/Roslyn",
            "/noconfig", "/define:DEBUG%3BTRACE", "A.cs", ""]);
        await Assert.That(r).IsNotNull();
        await Assert.That((r.Project, r.TargetFramework, r.AssemblyName)).IsEqualTo(("C:/r/A.csproj", "net10.0", "A"));
        await Assert.That(r.Arguments).IsEquivalentTo(["/noconfig", "/define:DEBUG;TRACE", "A.cs"], CollectionOrdering.Matching);
        await Assert.That(DesignTimeBuild.Parse(["something else"])).IsNull();
    }

    [Test]
    public async Task Semantic_options_from_flags_and_settings_file()
    {
        var dir = Directory.CreateTempSubdirectory("skarp-semopt").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, ".skarp.json"), """{ "version": 1, "semantic": { "enabled": false, "solution": "x/My.slnx", "generators": false, "targets": ["net8.0"] } }""");
            var o = CommandLine.Parse(["analyze", dir]).Options!;
            await Assert.That(o.Semantic).IsFalse();
            await Assert.That(o.Solution).IsEqualTo(Path.Combine(dir, "x", "My.slnx"));
            await Assert.That(o.Generators).IsFalse();
            await Assert.That(o.Targets).IsEquivalentTo(["net8.0"], CollectionOrdering.Matching);
            await Assert.That(o.OriginText("semantic.targets")).IsEqualTo("repository");
            o = CommandLine.Parse(["analyze", dir, "--restore", "--generators", "--target", "net9.0", "--target", "net10.0"]).Options!;
            await Assert.That(o.Semantic).IsTrue();   // --restore implies the tier
            await Assert.That(o.Restore).IsTrue();
            await Assert.That(o.Generators).IsTrue();
            await Assert.That(o.Targets).IsEquivalentTo(["net9.0", "net10.0"], CollectionOrdering.Matching);
            o = CommandLine.Parse(["analyze", dir, "--all-targets", "--semantic"]).Options!;
            await Assert.That(o.Targets).IsEquivalentTo(["all"], CollectionOrdering.Matching);
            await Assert.That(o.SemanticOptions().Enabled).IsTrue();
            await Assert.That(CommandLine.Parse(["hotspots", dir, "--semantic"]).Options!.SemanticOptions().Enabled).IsFalse();
            var bad = CommandLine.Parse(["analyze", dir, "--restore", "--no-semantic"]);
            await Assert.That(bad.ExitCode).IsEqualTo(2);
            await Assert.That(bad.Error).Contains("--no-semantic");
            await Assert.That(CommandLine.Parse(["analyze", dir, "--all-targets", "--target", "net8.0"]).ExitCode).IsEqualTo(2);
            await Assert.That(CommandLine.Parse(["history", dir, "--no-semantic"]).ExitCode).IsEqualTo(2);
            File.WriteAllText(Path.Combine(dir, ".skarp.json"), """{ "version": 1, "semantic": { "targets": ["all", "net8.0"] } }""");
            await Assert.That(CommandLine.Parse(["analyze", dir]).Error).Contains("semantic.targets");
            File.WriteAllText(Path.Combine(dir, ".skarp.json"), """{ "version": 1, "semantic": { "colour": 1 } }""");
            await Assert.That(CommandLine.Parse(["analyze", dir]).Error).Contains("semantic.colour: unknown key");
            var config = BaselineTests.Run("config", dir, "--no-config", "--no-generators");
            await Assert.That(config.Out).Contains("semantic.generators                      false                    command line");
            await Assert.That(config.Out).Contains("god-class            preset  lanza-marinescu-2006");
        }
        finally { Directory.Delete(dir, true); }
    }

    static Finding F(string id, double value) => new() { Id = id, Rule = id.Split('|')[0], Path = id.Split('|')[1], Value = value, Severity = "notice" };

    [Test]
    public async Task Semantic_baseline_paths_round_trip_and_not_evaluated_entries()
    {
        await Assert.That(SemanticBaseline.PathFor("/r/.skarp-baseline.json", null)).IsEqualTo("/r/.skarp-baseline.semantic.json");
        await Assert.That(SemanticBaseline.PathFor("/r/b.json", "net8.0")).IsEqualTo("/r/b.semantic.net8.0.json");
        var result = new SemanticResult
        {
            Run = new SemanticRunInfo { Targets = "highest", Solution = "S.slnx", Findings = SkarpSharp.Findings.RuleSettings.Resolve("default", null, []).ToSemanticInfo() },
            Findings = [F("high-coupling|a/X.cs|N.X", 25), F("high-coupling|a/Y.cs|N.Y", 30), F("god-class|a/Y.cs|N.Y", 50)],
        };
        var fp = SemanticBaseline.Fingerprint(result.Run.Findings!, true, "highest");
        var text = SemanticBaseline.Write(result, "0.1", fp, "0123456789abcdef0123456789abcdef01234567+");
        await Assert.That(text).Contains("\"type\": \"semantic-baseline\"");
        await Assert.That(text).Contains("\"commit\": \"0123456789abcdef0123456789abcdef01234567+\"");
        var file = SemanticBaseline.Parse(text, "b.json");
        await Assert.That(file.Type).IsEqualTo(SemanticBaseline.TypeName);
        await Assert.That(file.Solution).IsEqualTo("S.slnx");

        // This run: X worsened, Y's type not evaluated, a new finding Z; a project folder that failed hides W.
        file.Findings.Add(new BaselineEntry { Id = "high-coupling|b/W.cs|N.W", Value = 21 });
        file.Findings.Add(new BaselineEntry { Id = "high-coupling|c/Gone.cs|N.Gone", Value = 21 });
        var now = new List<Finding> { F("high-coupling|a/X.cs|N.X", 26), F("high-coupling|a/Z.cs|N.Z", 22) };
        var s = SemanticBaseline.Compare(now, file, "b.json", fp, new HashSet<string> { "a/Y.cs|N.Y" }, ["b/"]);
        await Assert.That((s.New, s.Worsened, s.Known, s.Fixed, s.NotEvaluated)).IsEqualTo((1, 1, 0, 1, 3));
        await Assert.That(s.SettingsChanged).IsFalse();

        // The tier did not run: everything is not evaluated, nothing is fixed or new.
        var missing = SemanticBaseline.NotEvaluated(file, "b.json");
        await Assert.That((missing.Entries, missing.Fixed, missing.New, missing.NotEvaluated)).IsEqualTo((5, 0, 0, 5));
        await Assert.That(SemanticBaseline.Fingerprint(SkarpSharp.Findings.RuleSettings.Resolve("default", null, ["god-class.preset=iplasma"]).ToSemanticInfo(), true, "highest")).IsNotEqualTo(fp);
    }
}