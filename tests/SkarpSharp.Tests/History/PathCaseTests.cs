using SkarpSharp.History;
using SkarpSharp.Model;

namespace SkarpSharp.Tests.History;

/// <summary>Join of disk paths with git paths when they differ only in case (Windows and macOS file systems).</summary>
// Its tests toggle the process-wide HistoryJoin.CaseInsensitiveFileSystem,
// so they run one at a time (TUnit runs the tests of a class in parallel unless told otherwise).
[NotInParallel(nameof(PathCaseTests))]
public sealed class PathCaseTests
{
    static Dictionary<string, FileHistory> Files(params (string Path, int Commits)[] rows)
        => rows.ToDictionary(r => r.Path, r => new FileHistory { Commits = r.Commits }, StringComparer.Ordinal);

    static T WithCaseInsensitive<T>(bool value, Func<T> body)
    {
        var old = HistoryJoin.CaseInsensitiveFileSystem;
        HistoryJoin.CaseInsensitiveFileSystem = value;
        try { return body(); }
        finally { HistoryJoin.CaseInsensitiveFileSystem = old; }
    }

    [Test]
    public async Task ExactMatchWins()
    {
        var files = Files(("src/Foo.cs", 3));
        var h = WithCaseInsensitive(true, () => HistoryJoin.Lookup(files, HistoryJoin.CaseFoldedIndex(files), "src/Foo.cs"));
        await Assert.That(h!.Commits).IsEqualTo(3);
    }

    [Test]
    public async Task CaseOnlyDifferenceMatchesOnCaseInsensitiveFileSystem()
    {
        var files = Files(("Src/Foo.cs", 3));
        var h = WithCaseInsensitive(true, () => HistoryJoin.Lookup(files, HistoryJoin.CaseFoldedIndex(files), "src/foo.cs"));
        await Assert.That(h!.Commits).IsEqualTo(3);
    }

    [Test]
    public async Task CaseOnlyDifferenceDoesNotMatchOnCaseSensitiveFileSystem()
    {
        var files = Files(("Src/Foo.cs", 3));
        var h = WithCaseInsensitive(false, () => HistoryJoin.Lookup(files, HistoryJoin.CaseFoldedIndex(files), "src/foo.cs"));
        await Assert.That(h).IsNull();
    }

    [Test]
    public async Task AmbiguousCaseFoldedPathsDoNotMatch()
    {
        var files = Files(("src/Foo.cs", 3), ("src/FOO.cs", 5));
        var h = WithCaseInsensitive(true, () => HistoryJoin.Lookup(files, HistoryJoin.CaseFoldedIndex(files), "src/foo.cs"));
        await Assert.That(h).IsNull();
    }

    [Test]
    public async Task FilesUnderMatchesPrefixIgnoringCase()
    {
        var root = Path.GetTempPath();
        var run = new HistoryRun { RepositoryRoot = root, Files = Files(("Sub/A.cs", 1), ("Other/B.cs", 2)) };
        var list = WithCaseInsensitive(true, () => HistoryJoin.FilesUnder(run, Path.Combine(root, "sub")));
        await Assert.That((await Assert.That(list).HasSingleItem()).Path).IsEqualTo("A.cs");
    }
}