using SkarpSharp.Discovery;

namespace SkarpSharp.Tests.Configuration;

/// <summary>
/// The glob fast paths give the same answer as the regular expression that
/// defines a glob's meaning. The patterns are the generated selectors of case c, the Acme.Lib equality settings, this repository's excludes and hand-picked edge
/// shapes, plus mixed-case and non-ASCII ones; the paths are this repository's .cs files, edge paths, mixed-case and
/// non-ASCII paths.
/// </summary>
public sealed class GlobEquivalenceTests
{
    static readonly string Repo = FindRepo();

    static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SkarpSharp.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root (SkarpSharp.slnx) not found");
    }

    /// <summary>Case c: 20 selectors of three globs each, generated from Acme.Lib's folders and file names.</summary>
    internal static readonly IReadOnlyList<string> SelectorGlobs =
    [
        "Src/Acme.Lib/**", "**/*Json*.cs", "Src/Acme.Lib.FuzzTests/*Schema*.cs", "Src/Acme.Lib.TestConsole/**", "**/*Reader*.cs",
        "Src/Acme.Lib.Tests/*Path*.cs", "Src/Acme.Lib.Tests/Benchmarks/**", "**/*Writer*.cs", "Src/Acme.Lib.Tests/Bson/*Utils*.cs",
        "Src/Acme.Lib.Tests/Converters/**", "**/*Converter*.cs", "Src/Acme.Lib.Tests/Documentation/*Bson*.cs",
        "Src/Acme.Lib.Tests/Documentation/Samples/**", "**/*Test*.cs", "Src/Acme.Lib.Tests/Documentation/Samples/Bson/*Xml*.cs",
        "Src/Acme.Lib.Tests/Documentation/Samples/Json/**", "**/*Token*.cs", "Src/Acme.Lib.Tests/Documentation/Samples/JsonPath/*Date*.cs",
        "Src/Acme.Lib.Tests/Documentation/Samples/Linq/**", "**/*Contract*.cs", "Src/Acme.Lib.Tests/Documentation/Samples/Schema/*Enum*.cs",
        "Src/Acme.Lib.Tests/Documentation/Samples/Serializer/**", "**/*Schema*.cs", "Src/Acme.Lib.Tests/Documentation/Samples/Xml/*Dynamic*.cs",
        "Src/Acme.Lib.Tests/Issues/**", "**/*Path*.cs", "Src/Acme.Lib.Tests/JsonTextReaderTests/*Linq*.cs", "Src/Acme.Lib.Tests/Linq/**",
        "**/*Utils*.cs", "Src/Acme.Lib.Tests/Linq/ComponentModel/*Serializer*.cs", "Src/Acme.Lib.Tests/Linq/JsonPath/**", "**/*Bson*.cs",
        "Src/Acme.Lib.Tests/LinqToSql/*Resolver*.cs", "Src/Acme.Lib.Tests/Properties/**", "**/*Xml*.cs", "Src/Acme.Lib.Tests/Schema/*Property*.cs",
        "Src/Acme.Lib.Tests/Schema/Specs/**", "**/*Date*.cs", "Src/Acme.Lib.Tests/Serialization/*Value*.cs", "Src/Acme.Lib.Tests/TestObjects/**",
        "**/*Enum*.cs", "Src/Acme.Lib.Tests/TestObjects/Events/*Json*.cs", "Src/Acme.Lib.Tests/TestObjects/GeoCoding/**", "**/*Dynamic*.cs",
        "Src/Acme.Lib.Tests/TestObjects/GeometricForms/*Reader*.cs", "Src/Acme.Lib.Tests/TestObjects/JsonTextReaderTests/**", "**/*Linq*.cs",
        "Src/Acme.Lib.Tests/TestObjects/Money/*Writer*.cs", "Src/Acme.Lib.Tests/TestObjects/Organization/**", "**/*Serializer*.cs",
        "Src/Acme.Lib.Tests/Utilities/*Converter*.cs", "Src/Acme.Lib/Bson/**", "**/*Resolver*.cs", "Src/Acme.Lib/Converters/*Test*.cs",
        "Src/Acme.Lib/Linq/**", "**/*Property*.cs", "Src/Acme.Lib/Linq/JsonPath/*Token*.cs", "Src/Acme.Lib/Properties/**", "**/*Value*.cs",
        "Src/Acme.Lib/Schema/*Contract*.cs",
    ];

    /// <summary>The Acme.Lib equality settings and this repository's excludes.</summary>
    static readonly string[] Settings =
    [
        "Src/Acme.Lib/Serialization/**", "**/Utilities/*.cs", "**/JPath/**", "Src/Acme.Lib/Serialization/Json*Contract*.cs",
        "tools/bench/**", "tests/SkarpSharp.Tests/Golden/**", "tests/SkarpSharp.Tests/Mock/**",
    ];

    /// <summary>Edge shapes, plus mixed case, non-ASCII and <c>dir/**/name</c>.</summary>
    static readonly string[] Edge =
    [
        "Migrations", "*.g.cs", "/Migrations", "**", "/**", "**/*.cs", "src/**", "src", "Src/Acme.Lib/**", "SRC/ACME.LIB/**",
        "**/Linq/*.cs", "**/Linq/**", "x/*/z.cs", "?.cs", "*", "a/b/**", "**/Migrations", "./src/**", "\\Src\\Acme.Lib\\**", "*Json*", "J?Token.cs",
        "**.cs", "v.1/[x]/**", "v.1", "*+b.cs", "deep/Migrations", "tests/SkarpSharp.Tests/Golden/**", "**/*Test*.cs", "*Tests", "Src/*/Linq/**",
        "src/**/Golden", "tests/**/expected/*.json", "src/SkarpSharp.Core/**/*Rules*.cs", "**/obj/**", "a/*/**", "a/**/b/**/c.cs", "/x/y.cs",
        "*.CS", "SRC/skarpsharp.core/**", "Ünïcode/**", "**/Ärger*.cs", "ÉTÉ/*.cs", "данные/**", "**/Файл*.cs", "straße/*", "日本/**/*.cs",
        "a?c/**", "**/[x]/*", "**/k.cs", "i*/**", "x/K.cs", "*.Designer.cs", "Generated", "src/x/../y/**",
    ];

    static readonly string[] EdgePaths =
    [
        "a", "A.cs", "x/y/z.cs", "Src/Acme.Lib", "src/acme.lib/Linq/JToken.cs", "a//b.cs", "foo.g.cs", "Migrations/M1.cs",
        "deep/Migrations", "deep/MigrationsX/a.cs", "tests/SkarpSharp.Tests/Golden/x.cs", "v.1/[x]/a+b.cs",
        "SRC/SKARPSHARP.CORE/Findings/BrainRules.cs", "Src/Golden", "src/a/Golden/b.cs", "a/b", "a/b/c", "a/x/b/y/c.cs", "a/b/c.cs", "abc/d.cs",
        "x/y.cs", "X/Y.CS", "Form1.Designer.cs", "Generated/A.cs", "src/obj/Debug/A.cs",
        "ünïcode/A.cs", "ÜNÏCODE/A.cs", "x/ärgerlich.cs", "x/ÄRGER.cs", "été/a.cs", "ÉTÉ/b.cs", "ДАННЫЕ/x.cs", "данные/x.cs", "y/файлы.cs", "y/ФАЙЛ.cs",
        "STRASSE/a", "straße/a", "STRAẞE/a", "x/K.cs", "x/k.cs", "ıx/a.cs", "İx/a.cs", "ix/a.cs", "日本/語/a.cs", "日本/a.cs",
    ];

    [Test]
    public async Task Fast_paths_equal_the_regular_expression()
    {
        var paths = new List<string>(EdgePaths);
        paths.AddRange(FileDiscovery.Find(Repo));
        paths.AddRange([.. paths.Take(200).Select(p => p.ToUpperInvariant())]);
        var patterns = SelectorGlobs.Concat(Settings).Concat(Edge).Distinct(StringComparer.Ordinal).ToList();
        int checks = 0;
        var differences = new List<string>();
        foreach (var pattern in patterns)
        {
            var glob = new Glob(pattern);
            foreach (var p in paths)
            {
                checks++;
                bool fast = glob.Matches(p), regex = glob.MatchesByRegex(p);
                if (fast != regex) differences.Add($"'{pattern}' '{p}': fast {fast}, regex {regex}");
            }
        }
        Console.WriteLine($"glob equivalence: {patterns.Count} patterns x {paths.Count} paths = {checks} checks, {differences.Count} differences; " +
            $"{patterns.Count(p => new Glob(p).UsesRegex)} patterns need the regex");
        await Assert.That(differences.Count).IsEqualTo(0).Because(string.Join("\n", differences.Take(30)));
    }

    [Test]
    public async Task Common_shapes_need_no_regular_expression()
    {
        foreach (var p in (string[])["**/*.cs", "src/Legacy/**", "src/A.cs", "src/**/Generated", "*.g.cs", "Migrations", "src/*/Linq/**"])
            await Assert.That(new Glob(p).UsesRegex).IsFalse().Because(p);
        await Assert.That(new Glob("**.cs").UsesRegex).IsTrue();
    }
}
