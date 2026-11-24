using SkarpSharp.Analysis;
using SkarpSharp.Discovery;
using SkarpSharp.Model;

namespace SkarpSharp.Tests.Findings;

/// <summary>Test-file detection and --include / --exclude.</summary>
public sealed class TestDetectionTests : IDisposable
{
    readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("skarp-tests-");

    public TestDetectionTests()
    {
        // A folder of its own (".git" marker) so the project search stops here.
        Directory.CreateDirectory(Path.Combine(_dir.FullName, ".git"));
        Write("lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Example.Package\" Version=\"1.0.0\" /></ItemGroup></Project>");
        Write("lib/A.cs", "class A { }");
        Write("lib/Sub/Deep.cs", "class Deep { }");
        Write("lib/UsesNUnit.cs", "using NUnit.Framework;\nclass Helper { }");
        Write("lib/Generated/X.g.cs", "class X { }");
        Write("tests/xunit/T.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"xunit.v3\" Version=\"3.0.0\" /></ItemGroup></Project>");
        Write("tests/xunit/T1.cs", "public class T1 { [Fact] public void F() { } }");
        Write("tests/xunit/Helpers/H.cs", "class H { }");
        Write("tests/flag/F.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
        Write("tests/flag/B.cs", "class B { }");
        Write("tests/mstest/M.csproj", "<Project Sdk=\"MSTest.Sdk/3.6.0\"></Project>");
        Write("tests/mstest/M.cs", "class M { }");
        Write("gproj/G.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        Write("gproj/Usings.cs", "global using Xunit;");
        Write("gproj/C.cs", "class C { }");
        Write("noproj/N.cs", "using Microsoft.VisualStudio.TestTools.UnitTesting;\nnamespace Q { class N { } }");
        Write("noproj/Plain.cs", "namespace Q { using Xunit.Abstractions; class P { } }");
    }

    void Write(string rel, string text)
    {
        var path = Path.Combine(_dir.FullName, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Dispose() => _dir.Delete(recursive: true);

    AnalysisResult Analyze(bool includeTests = false, string[]? include = null, string[]? exclude = null) =>
        Analyzer.AnalyzeDirectory(_dir.FullName, new AnalyzerOptions { Parallelism = 2, IncludeTests = includeTests, Include = include ?? [], Exclude = exclude ?? [] });

    static string[] Paths(AnalysisResult r) => [.. r.Files.Select(f => f.Path)];

    [Test]
    public async Task Test_projects_test_usings_and_global_usings_are_excluded_by_default()
    {
        var r = Analyze();
        await Assert.That(Paths(r)).IsEquivalentTo(new[] { "lib/A.cs", "lib/Generated/X.g.cs", "lib/Sub/Deep.cs" }, CollectionOrdering.Matching);
        var why = r.Excluded!.ToDictionary(e => e.Path, e => e.Reason);
        await Assert.That(why["tests/xunit/T1.cs"]).IsEqualTo("test project tests/xunit/T.csproj (references xunit.v3)");
        await Assert.That(why["tests/xunit/Helpers/H.cs"]).IsEqualTo("test project tests/xunit/T.csproj (references xunit.v3)");
        await Assert.That(why["tests/flag/B.cs"]).IsEqualTo("test project tests/flag/F.csproj (IsTestProject)");
        await Assert.That(why["tests/mstest/M.cs"]).IsEqualTo("test project tests/mstest/M.csproj (uses MSTest.Sdk)");
        await Assert.That(why["gproj/C.cs"]).IsEqualTo("test project gproj/G.csproj (global using Xunit in gproj/Usings.cs)");
        await Assert.That(why["lib/UsesNUnit.cs"]).IsEqualTo("uses NUnit.Framework");
        await Assert.That(why["noproj/N.cs"]).IsEqualTo("uses Microsoft.VisualStudio.TestTools.UnitTesting");
        await Assert.That(why["noproj/Plain.cs"]).IsEqualTo("uses Xunit");
        await Assert.That(r.Repository.TestFiles).IsEqualTo(9);
        await Assert.That(r.Repository.Files).IsEqualTo(2); // the generated file is listed but not counted
        foreach (var e in r.Excluded!) await Assert.That(e.Kind).IsEqualTo("test");
    }

    [Test]
    public async Task Include_tests_keeps_everything()
    {
        var r = Analyze(includeTests: true);
        await Assert.That(r.Files.Count).IsEqualTo(12);
        await Assert.That(r.Excluded).IsNull();
        await Assert.That(r.Run.IncludeTests).IsTrue();
    }

    [Test]
    public async Task Include_brings_back_detected_files_and_exclude_wins()
    {
        var r = Analyze(include: ["tests/xunit/**", "noproj/N.cs"]);
        await Assert.That(Paths(r)).Contains("tests/xunit/T1.cs");
        await Assert.That(Paths(r)).Contains("tests/xunit/Helpers/H.cs");
        await Assert.That(Paths(r)).Contains("noproj/N.cs");
        await Assert.That(Paths(r)).DoesNotContain("tests/flag/B.cs");

        var both = Analyze(include: ["tests/xunit"], exclude: ["Helpers"]);
        await Assert.That(Paths(both)).Contains("tests/xunit/T1.cs");
        await Assert.That(Paths(both)).DoesNotContain("tests/xunit/Helpers/H.cs");
        await Assert.That(both.Excluded!.Where(e => e.Path == "tests/xunit/Helpers/H.cs").Select(e => (e.Kind, e.Reason)).Single()).IsEqualTo(("exclude", "--exclude Helpers"));
        await Assert.That(both.Repository.ExcludedFiles).IsEqualTo(1);
    }

    [Test]
    public async Task Exclude_globs()
    {
        await Assert.That(Paths(Analyze(exclude: ["lib/A.cs"]))).IsEquivalentTo(new[] { "lib/Generated/X.g.cs", "lib/Sub/Deep.cs" }, CollectionOrdering.Matching);
        await Assert.That(Paths(Analyze(exclude: ["*.g.cs"]))).IsEquivalentTo(new[] { "lib/A.cs", "lib/Sub/Deep.cs" }, CollectionOrdering.Matching);
        await Assert.That(Paths(Analyze(exclude: ["**/Sub/**"]))).IsEquivalentTo(new[] { "lib/A.cs", "lib/Generated/X.g.cs" }, CollectionOrdering.Matching);
        await Assert.That(Paths(Analyze(exclude: ["lib"]))).IsEmpty();
    }

    [Test]
    public async Task Find_excluded_matches_the_analysis()
    {
        var a = Analyze(include: ["noproj/**"], exclude: ["lib/Sub"]);
        var f = Analyzer.FindExcluded(_dir.FullName, new AnalyzerOptions { Include = ["noproj/**"], Exclude = ["lib/Sub"] });
        await Assert.That(f.Select(e => (e.Path, e.Reason))).IsEquivalentTo(a.Excluded!.Select(e => (e.Path, e.Reason)), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("*.cs", "a/b/C.cs", true)]
    [Arguments("b", "a/b/C.cs", true)]
    [Arguments("a/b", "a/b/C.cs", true)]
    [Arguments("a/c", "a/b/C.cs", false)]
    [Arguments("/b", "a/b/C.cs", false)]
    [Arguments("a/*/C.cs", "a/b/C.cs", true)]
    [Arguments("a/**/C.cs", "a/C.cs", true)]
    [Arguments("a/**/C.cs", "a/x/y/C.cs", true)]
    [Arguments("C?.cs", "a/C1.cs", true)]
    [Arguments("C?.cs", "a/C12.cs", false)]
    [Arguments("./a/b/", "a/b/C.cs", true)]
    public async Task Glob_matching(string pattern, string path, bool expected) => await Assert.That(new Glob(pattern).Matches(path)).IsEqualTo(expected);

    [Test]
    [Arguments("<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"NUnit\">\n      <PrivateAssets>all</PrivateAssets>\n    </PackageReference>\n  </ItemGroup>\n</Project>", "references NUnit")]
    [Arguments("<Project><!-- <PackageReference Include=\"xunit\" /> --><ItemGroup><PackageReference Include='Moq' /></ItemGroup></Project>", null)]
    [Arguments("<Project><ItemGroup><PackageReference Update=\"xunit.v3\" Version=\"3\"/></ItemGroup></Project>", "references xunit.v3")]
    [Arguments("<Project><PropertyGroup><IsTestProject> true </IsTestProject></PropertyGroup></Project>", "IsTestProject")]
    [Arguments("<Project><PropertyGroup><IsTestProject>false</IsTestProject></PropertyGroup></Project>", null)]
    [Arguments("<Project><Sdk Name=\"MSTest.Sdk\" Version=\"3.6.0\" /></Project>", "uses MSTest.Sdk")]
    [Arguments("<Project><ItemGroup><Using Include=\"NUnit.Framework\" /></ItemGroup></Project>", "global using NUnit.Framework")]
    public async Task Project_file_reading(string xml, string? expected) => await Assert.That(TestDetection.TestProjectReasonFromText(xml)).IsEqualTo(expected);

    [Test]
    [Arguments("Microsoft.NET.Test.Sdk", true)]
    [Arguments("xunit.runner.visualstudio", true)]
    [Arguments("NUnit3TestAdapter", true)]
    [Arguments("MSTest.TestFramework", true)]
    [Arguments("xunitish", false)]
    [Arguments("Moq", false)]
    public async Task Test_packages(string id, bool expected) => await Assert.That(TestDetection.IsTestPackage(id)).IsEqualTo(expected);
}