using System.Text;
using System.Text.Json;
using SkarpSharp.Analysis;
using SkarpSharp.History;
using SkarpSharp.Model;

namespace SkarpSharp.Tests.History;

/// <summary>
/// End-to-end history on synthetic repositories that reproduce worked examples: preprocessing scenarios P1, P2,
/// P4 and P5 (the shallow boundary is covered by the preprocessing tests),
/// churn Example 1 and hotspots Example 1.
/// </summary>
public class SyntheticHistoryTests
{
    static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1700000000);

    static string Lines(IEnumerable<string> lines, string eol = "\n") => string.Concat(lines.Select(l => l + eol));

    static HistoryRun Run(SyntheticRepo repo, bool ignoreWhitespace = false, bool includeBots = false, int windowMonths = 12,
        bool allHistory = false, IHistoryConsumer? raw = null, IChangeSetSink? sink = null, int? bulkDrop = null, int bulkFlag = 300) =>
        HistoryRunner.RunAsync(repo.Root, new HistoryOptions
        {
            IgnoreWhitespace = ignoreWhitespace, IncludeBots = includeBots, WindowMonths = windowMonths, AllHistory = allHistory,
            RawConsumer = raw, ChangeSetSink = sink, BulkDrop = bulkDrop, BulkFlag = bulkFlag, Environment = repo.Environment,
        }).GetAwaiter().GetResult();

    /// <summary>The history-preprocessing scenario repository (worked examples), commits c1 to bin.</summary>
    static SyntheticRepo BuildPreprocessingScenario()
    {
        var r = new SyntheticRepo();
        string[] a = ["a", "b", "c", "d", "e", "f", "g", "h", "i", "j"];
        r.Write("A.cs", Lines(a));
        r.Write("B.cs", Lines(["x", "y", "z"]));
        r.Commit("c1", T0);
        r.Write("A.cs", Lines([.. a, "k"]));
        r.Write("B.cs", Lines(["x", "y", "z", "w"]));
        r.Commit("c2", T0.AddSeconds(100), "Alice B <ALICE@X.ORG>");
        Directory.CreateDirectory(Path.Combine(r.Root, "sub"));
        r.Git("mv", "B.cs", "sub/B with space.cs");
        r.Commit("c3", T0.AddSeconds(200));
        r.Write("sub/B with space.cs", Lines(["x", "y", "z", "w", "v"]));
        r.Commit("c4", T0.AddSeconds(300), "Bot <49699333+dependabot[bot]@users.noreply.github.com>");
        r.WriteBytes("img.bin", [.. Enumerable.Range(0, 20).Select(i => (byte)('A' + (i * 7) % 26))]);
        r.Commit("c5", T0.AddSeconds(400));
        r.Write("A.cs", Lines([.. a, "k"], "\r\n"));
        r.Commit("crlf", T0.AddSeconds(500));
        r.Git("checkout", "-q", "-b", "feat");
        r.Write("F.cs", "f\n");
        r.Commit("feat1", T0.AddSeconds(600));
        r.Git("checkout", "-q", "main");
        r.Write("A.cs", Lines([.. a, "k", "l"], "\r\n"));
        r.Commit("main1", T0.AddSeconds(700));
        var date = "@" + T0.AddSeconds(800).ToUnixTimeSeconds() + " +0000";
        r.Git(new Dictionary<string, string?> { ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date },
            "merge", "-q", "--no-ff", "--no-edit", "feat", "-m", "merge");
        r.WriteBytes("data.bin", [0, 1, 2, 0, 3, 4]);
        r.Commit("bin", T0.AddSeconds(900));
        return r;
    }

    [Test]
    public async Task P1_change_frequency_and_churn_with_ignore_whitespace()
    {
        using var r = BuildPreprocessingScenario();
        var run = Run(r, ignoreWhitespace: true);
        await Assert.That(run.Summary).IsNotNull();
        await Assert.That(run.Files.Keys.Order(StringComparer.Ordinal)).IsEquivalentTo(new[] { "A.cs", "F.cs", "sub/B with space.cs" }, CollectionOrdering.Matching);
        await Assert.That(run.Files["A.cs"].Commits).IsEqualTo(3);
        await Assert.That(run.Files["A.cs"].Added + run.Files["A.cs"].Deleted).IsEqualTo(12);
        await Assert.That(run.Files["sub/B with space.cs"].Commits).IsEqualTo(2);
        await Assert.That(run.Files["sub/B with space.cs"].Added + run.Files["sub/B with space.cs"].Deleted).IsEqualTo(4);
        await Assert.That(run.Files["F.cs"].Commits).IsEqualTo(1);
        await Assert.That(run.Files["F.cs"].Added).IsEqualTo(1);
        var d = run.Summary!.Dropped;
        await Assert.That(d.MergeCommits).IsEqualTo(1);
        await Assert.That(d.BotCommits).IsEqualTo(1);
        await Assert.That(d.PureMoveRows).IsEqualTo(1);
        // Git reads only C# changes, so img.bin and data.bin (and their commits c5 and bin) are not read.
        await Assert.That(d.OtherFileRows).IsEqualTo(0);
        await Assert.That(run.Summary.Reader.BinaryRows).IsEqualTo(0);
        await Assert.That(run.Summary.CommitsRead).IsEqualTo(7);
        await Assert.That(run.Summary.Reader.Renames).IsEqualTo(1);
    }

    [Test]
    public async Task P1_without_ignore_whitespace_counts_the_line_ending_commit()
    {
        using var r = BuildPreprocessingScenario();
        var run = Run(r);
        await Assert.That(run.Files["A.cs"].Commits).IsEqualTo(4);
        await Assert.That(run.Files["A.cs"].Added + run.Files["A.cs"].Deleted).IsEqualTo(34);
    }

    [Test]
    public async Task P1_with_bots_included()
    {
        using var r = BuildPreprocessingScenario();
        var run = Run(r, ignoreWhitespace: true, includeBots: true);
        await Assert.That(run.Files["sub/B with space.cs"].Commits).IsEqualTo(3);
        await Assert.That(run.Files["sub/B with space.cs"].Added + run.Files["sub/B with space.cs"].Deleted).IsEqualTo(5);
        await Assert.That(run.Summary!.Dropped.BotCommits).IsEqualTo(0);
    }

    sealed class ChangeSetRecorder : IChangeSetSink
    {
        public List<(string Hash, int[] Files)> Sets { get; } = [];
        public void OnChangeSet(in ChangeSet changeSet, ReadOnlySpan<EligibleRow> rows) =>
            Sets.Add((changeSet.Hash, [.. rows.ToArray().Select(x => x.FileId).Distinct().Order()]));
    }

    [Test]
    public async Task P2_change_sets_after_rename_mapping()
    {
        using var r = BuildPreprocessingScenario();
        var sets = new ChangeSetRecorder();
        Run(r, ignoreWhitespace: true, sink: sets);
        // c1 {A, B}, c2 {A, B}, feat1 {F}, main1 {A}; B.cs and sub/B with space.cs are one entity.
        await Assert.That(sets.Sets.Count).IsEqualTo(4);
        var pairs = sets.Sets.Where(s => s.Files.Length == 2).ToList();
        await Assert.That(pairs.Count).IsEqualTo(2);
        await Assert.That(pairs[1].Files).IsEquivalentTo(pairs[0].Files, CollectionOrdering.Matching);
        await Assert.That(sets.Sets.Count(s => s.Files.Length == 1)).IsEqualTo(2);
    }

    sealed class AuthorRecorder : IHistoryConsumer
    {
        public Dictionary<string, string> Authors { get; } = [];
        public void OnCommit(RawCommit c) => Authors[c.Hash] = $"{c.AuthorName} <{c.AuthorEmail}>";
        public void OnComplete() { }
    }

    [Test]
    public async Task P4_identity_is_the_lowercase_email_after_mailmap()
    {
        using var r = BuildPreprocessingScenario();
        var c2 = r.Git("log", "--format=%H", "--grep=^c2$").Trim();
        var raw = new AuthorRecorder();
        var without = Run(r, ignoreWhitespace: true, raw: raw);
        await Assert.That(raw.Authors[c2]).IsEqualTo("Alice B <ALICE@X.ORG>");
        await Assert.That(without.Files["A.cs"].Authors).IsEqualTo(1);
        await Assert.That(without.Summary!.People).IsEqualTo(1);

        r.Write(".mailmap", "Alice <alice@x.org>\nAlice <alice@x.org> Alice B <ALICE@X.ORG>\n"); // uncommitted
        raw = new AuthorRecorder();
        var with = Run(r, ignoreWhitespace: true, raw: raw);
        await Assert.That(raw.Authors[c2]).IsEqualTo("Alice <alice@x.org>");
        await Assert.That(with.Files["A.cs"].Authors).IsEqualTo(1);
        await Assert.That(with.Summary!.People).IsEqualTo(1);
    }

    [Test]
    public async Task P5_bot_commit_is_flagged_and_dropped_and_ownership_stays()
    {
        using var r = BuildPreprocessingScenario();
        var run = Run(r, ignoreWhitespace: true);
        await Assert.That(run.Summary!.Dropped.BotCommits).IsEqualTo(1);
        await Assert.That(run.Files["sub/B with space.cs"].Authors).IsEqualTo(1);
        await Assert.That(Run(r, ignoreWhitespace: true, includeBots: true).Files["A.cs"].Authors).IsEqualTo(1);
        await Assert.That(Run(r, ignoreWhitespace: true, includeBots: true).Files["sub/B with space.cs"].Authors).IsEqualTo(2);
    }

    /// <summary>A churn history: c1..c4 on src/Core/A.cs, src/Core/B.cs, src/Web/C.cs (numstat added/deleted).</summary>
    static SyntheticRepo BuildChurnExample()
    {
        var r = new SyntheticRepo();
        static List<string> Seq(string p, int from, int count) => [.. Enumerable.Range(from, count).Select(i => $"{p}{i}")];
        var day = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        // c1 alice: A 100/0, B 50/0
        var a = Seq("a", 1, 100);
        var b = Seq("b", 1, 50);
        r.Write("src/Core/A.cs", Lines(a));
        r.Write("src/Core/B.cs", Lines(b));
        r.Commit("c1", day, "alice <alice@x.org>");
        // c2 bob: A 10/5 (replace 5 lines, append 5), C 20/0
        a = [.. Seq("a2r", 1, 5), .. a.Skip(5), .. Seq("a2n", 1, 5)];
        var c = Seq("c", 1, 20);
        r.Write("src/Core/A.cs", Lines(a));
        r.Write("src/Web/C.cs", Lines(c));
        r.Commit("c2", day.AddMonths(1), "bob <bob@x.org>");
        // c3 alice: A 30/30 (replace lines 11..40), B 4/2 (replace 2, append 2)
        a = [.. a.Take(10), .. Seq("a3r", 1, 30), .. a.Skip(40)];
        b = [.. b.Take(20), .. Seq("b3r", 1, 2), .. b.Skip(22), .. Seq("b3n", 1, 2)];
        r.Write("src/Core/A.cs", Lines(a));
        r.Write("src/Core/B.cs", Lines(b));
        r.Commit("c3", day.AddMonths(2), "alice <alice@x.org>");
        // c4 alice (one hour later in the day, so a one-month window excludes c3): A 2/1, B 6/6, C 10/2
        a = [.. a.Take(49), "a4r1", .. a.Skip(50), "a4n1"];
        b = [.. b.Take(30), .. Seq("b4r", 1, 6), .. b.Skip(36)];
        c = [.. c.Take(5), "c4r1", "c4r2", .. c.Skip(7), .. Seq("c4n", 1, 8)];
        r.Write("src/Core/A.cs", Lines(a));
        r.Write("src/Core/B.cs", Lines(b));
        r.Write("src/Web/C.cs", Lines(c));
        r.Commit("c4", day.AddMonths(3).AddHours(1), "alice <alice@x.org>");
        return r;
    }

    [Test]
    public async Task Churn_example_1_absolute_churn_and_frequency()
    {
        using var r = BuildChurnExample();
        var run = Run(r);
        async Task Check(string path, int commits, long added, long deleted, int authors)
        {
            var h = run.Files[path];
            await Assert.That((h.Commits, h.Added, h.Deleted, h.Authors)).IsEqualTo((commits, added, deleted, authors));
        }
        await Check("src/Core/A.cs", 4, 142, 36, 2);
        await Check("src/Core/B.cs", 3, 60, 8, 1);
        await Check("src/Web/C.cs", 2, 30, 2, 2);
        await Assert.That(run.Summary!.CommitsUsed).IsEqualTo(4);
        await Assert.That(run.Summary.Added).IsEqualTo(232);
        await Assert.That(run.Summary.Deleted).IsEqualTo(46);
        await Assert.That(run.Files["src/Core/A.cs"].FirstChange).IsEqualTo("2026-01-10T12:00:00Z");
        await Assert.That(run.Files["src/Core/A.cs"].LastChange).IsEqualTo("2026-04-10T13:00:00Z");
        await Assert.That(run.Git.WindowEnd).IsEqualTo("2026-04-10T13:00:00Z");
        await Assert.That(run.Git.WindowStart).IsEqualTo("2025-04-10T13:00:00Z");
    }

    [Test]
    public async Task Window_is_pushed_down_and_outside_commits_are_counted()
    {
        using var r = BuildChurnExample();
        var run = Run(r, windowMonths: 1);
        await Assert.That(run.Summary!.CommitsRead).IsEqualTo(1);
        await Assert.That(run.Summary.Dropped.OutsideWindowCommits).IsEqualTo(3);
        await Assert.That(run.Summary.CommitsInRepository).IsEqualTo(4);
        await Assert.That((run.Files["src/Core/A.cs"].Commits, run.Files["src/Core/A.cs"].Added, run.Files["src/Core/A.cs"].Deleted)).IsEqualTo((1, 2L, 1L));
        await Assert.That(Run(r, allHistory: true).Summary!.CommitsRead).IsEqualTo(4);
        await Assert.That(Run(r, allHistory: true).Git.WindowStart).IsNull();
    }

    [Test]
    public async Task Analyzing_a_subdirectory_joins_on_the_relative_path()
    {
        using var r = BuildChurnExample();
        var run = Run(r);
        var result = Analyzer.AnalyzeDirectory(Path.Combine(r.Root, "src", "Core"), new AnalyzerOptions { Parallelism = 2 });
        HistoryJoin.Apply(result, run, top: 20, includeGenerated: false);
        await Assert.That(result.Files.Select(f => f.Path)).IsEquivalentTo(new[] { "A.cs", "B.cs" }, CollectionOrdering.Matching);
        await Assert.That(result.Files[0].History!.Commits).IsEqualTo(4);
        await Assert.That(result.Hotspots!.Select(h => h.Path)).IsEquivalentTo(new[] { "A.cs", "B.cs" }, CollectionOrdering.Matching);
        await Assert.That(HistoryJoin.FilesUnder(run, Path.Combine(r.Root, "src", "Core")).Select(f => f.Path)).IsEquivalentTo(new[] { "A.cs", "B.cs" }, CollectionOrdering.Matching);
    }

    /// <summary>A C# file whose single method has cognitive complexity n (n sequential if statements).</summary>
    static string ComplexFile(string type, int n, int edits) =>
        $"class {type}\n{{\n    void M(bool b)\n    {{\n{string.Concat(Enumerable.Repeat("        if (b) { }\n", n))}    }}\n}}\n"
        + string.Concat(Enumerable.Range(1, edits).Select(i => $"// edit {i}\n"));

    [Test]
    public async Task Hotspots_example_1_ranks_activity_then_complexity()
    {
        // Example 1 files: A 40 commits / complexity 200, B 5 / 400, C 30 / 20, D 2 / 10 (cognitive sums here).
        using var r = new SyntheticRepo();
        var spec = new (string Name, int Commits, int Complexity)[] { ("A", 40, 200), ("B", 5, 400), ("C", 30, 20), ("D", 2, 10) };
        for (int k = 1; k <= 40; k++)
        {
            foreach (var (name, commits, complexity) in spec)
                if (k <= commits) r.Write($"{name}.cs", ComplexFile(name, complexity, k));
            r.Commit($"k{k}", T0.AddHours(k));
        }
        var run = Run(r);
        var result = Analyzer.AnalyzeDirectory(r.Root, new AnalyzerOptions { Parallelism = 2 });
        HistoryJoin.Apply(result, run, top: 20, includeGenerated: false);
        var hs = result.Hotspots!;
        await Assert.That(hs.Select(h => h.Path)).IsEquivalentTo(new[] { "A.cs", "C.cs", "B.cs", "D.cs" }, CollectionOrdering.Matching);
        await Assert.That(hs.Select(h => h.Rank)).IsEquivalentTo(new[] { 1, 2, 3, 4 }, CollectionOrdering.Matching);
        await Assert.That(hs.Select(h => h.Commits)).IsEquivalentTo(new[] { 40, 30, 5, 2 }, CollectionOrdering.Matching);
        await Assert.That(hs.Select(h => h.CognitiveSum)).IsEquivalentTo(new long[] { 200, 20, 400, 10 }, CollectionOrdering.Matching);
        await Assert.That(hs.Select(h => h.CognitiveMax)).IsEquivalentTo(new long[] { 200, 20, 400, 10 }, CollectionOrdering.Matching);
        await Assert.That(Hotspots.Rank(hs.Select(h => new HotspotCandidate(h.Path, h.Commits, h.Added, h.Deleted, h.CognitiveSum, h.CognitiveMax, h.SourceLines)), 2).Count).IsEqualTo(2);
    }

    [Test]
    public async Task Hotspot_ties_on_activity_are_broken_by_cognitive_sum_then_path()
    {
        var list = Hotspots.Rank(
        [
            new("b.cs", 5, 0, 0, 10, 10, 1), new("a.cs", 5, 0, 0, 10, 9, 1), new("c.cs", 5, 0, 0, 30, 3, 1),
            new("d.cs", 9, 0, 0, 1, 1, 1), new("e.cs", 0, 0, 0, 999, 999, 1),
        ], 10);
        await Assert.That(list.Select(h => h.Path)).IsEquivalentTo(new[] { "d.cs", "c.cs", "a.cs", "b.cs" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Invariants_per_file_sums_equal_totals()
    {
        using var r = BuildPreprocessingScenario();
        r.Git("rm", "-q", "F.cs");
        r.Commit("drop F", T0.AddSeconds(1000));
        foreach (var run in new[] { Run(r), Run(r, ignoreWhitespace: true), Run(r, includeBots: true) })
        {
            var s = run.Summary!;
            await Assert.That(run.Files.Values.Sum(f => f.Added)).IsEqualTo(s.Added);
            await Assert.That(run.Files.Values.Sum(f => f.Deleted)).IsEqualTo(s.Deleted);
            await Assert.That(run.Files.Count).IsEqualTo(s.Files);
            await Assert.That(s.Dropped.NotAtHeadFiles).IsEqualTo(1); // F.cs deleted at HEAD
            await Assert.That(s.Rows + s.Dropped.NotAtHeadRows).IsEqualTo(run.AllFileTotals.Rows);
            await Assert.That(run.Files.ContainsKey("F.cs")).IsFalse();
            await Assert.That(s.CommitsRead).IsEqualTo(s.Reader.Commits);
            await Assert.That(s.Reader.Malformed).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments(false, "A.cs:4:")]
    [Arguments(true, "A.cs:3:")]
    public async Task Output_does_not_depend_on_the_read_chunk_size(bool ignoreWhitespace, string expectedA)
    {
        using var r = BuildPreprocessingScenario();
        var policy = new HistoryPolicy { IgnoreWhitespace = ignoreWhitespace };
        var bytes = Encoding.UTF8.GetBytes(r.Git([.. policy.LogArguments()]));
        string Parse(int chunk)
        {
            var paths = new PathTable();
            var acc = new FileAccumulators();
            var pre = new Preprocessor(paths, policy, acc);
            var parser = new GitLogParser(paths, pre);
            for (int i = 0; i < bytes.Length; i += chunk) parser.Feed(bytes.AsSpan(i, Math.Min(chunk, bytes.Length - i)));
            parser.Complete();
            return string.Join(";", acc.All().Select(x => $"{paths[x.FileId]}:{x.File.Commits}:{x.File.Added}:{x.File.Deleted}:{x.File.Authors}"))
                + $"|{parser.Totals.Rows}|{pre.Counts.CommitsUsed}|{pre.Counts.CommitsRead}|malformed {parser.Totals.Malformed}";
        }
        var one = Parse(bytes.Length);
        await Assert.That(one).Contains(expectedA);
        await Assert.That(one).EndsWith("|malformed 0");
        for (int chunk = 1; chunk < bytes.Length; chunk++) await Assert.That(Parse(chunk)).IsEqualTo(one);
    }

    [Test]
    public async Task Folder_outside_a_repository_gives_a_note_not_a_failure()
    {
        var dir = Directory.CreateTempSubdirectory("skarp-nogit-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "X.cs"), "class X { }");
            var run = HistoryRunner.RunAsync(dir.FullName, new HistoryOptions
            {
                Environment = new Dictionary<string, string?> { ["GIT_CEILING_DIRECTORIES"] = dir.Parent!.FullName },
            }).GetAwaiter().GetResult();
            await Assert.That(run.Summary).IsNull();
            await Assert.That(run.Git.Notices).Contains(n => n.Contains("not a git repository", StringComparison.Ordinal));
            var result = Analyzer.AnalyzeDirectory(dir.FullName, new AnalyzerOptions());
            HistoryJoin.Apply(result, run, 20, false);
            await Assert.That(result.History).IsNull();
            await Assert.That(result.Hotspots).IsNull();
            await Assert.That(result.Files[0].History).IsNull();
            await Assert.That(result.Run.Git).IsNotNull();
        }
        finally { dir.Delete(recursive: true); }
    }

    [Test]
    public async Task Empty_repository_gives_a_note()
    {
        using var r = new SyntheticRepo();
        var run = Run(r);
        await Assert.That(run.Summary).IsNull();
        await Assert.That(run.Git.Notices).Contains(n => n.Contains("no commits", StringComparison.Ordinal));
    }

    [Test]
    public async Task File_detail_drops_members_and_keeps_history()
    {
        using var r = BuildChurnExample();
        r.Write("src/Core/A.cs", "class A { void M(bool b) { if (b) { } } }\n");
        r.Commit("c5", new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        var result = Analyzer.AnalyzeDirectory(r.Root, new AnalyzerOptions());
        HistoryJoin.Apply(result, Run(r), 20, false);
        await Assert.That(result.Files.First(f => f.Path == "src/Core/A.cs").Members).IsNotEmpty();
        HistoryJoin.ToFileDetail(result);
        foreach (var f in result.Files) await Assert.That(f.Members).IsEmpty();
        var json = JsonSerializer.Serialize(result, ResultJsonContext.Default.AnalysisResult);
        await Assert.That(json).Contains("\"hotspots\"");
        await Assert.That(json).Contains("\"cognitiveSum\": 1");
        await Assert.That(json).Contains("\"policyDigest\"");
    }

    [Test]
    public async Task Renames_across_the_csharp_boundary_are_adds_and_deletes()
    {
        // Git pairs renames only among C# paths, so .txt -> .cs starts the file's history with an add of every
        // line, and .cs -> .txt ends it with a delete (the old path is not at HEAD). .cs -> .cs stays a rename.
        using var r = new SyntheticRepo();
        r.Write("B.txt", Lines(Enumerable.Range(1, 50).Select(i => $"b{i}")));
        r.Write("C.cs", Lines(Enumerable.Range(1, 51).Select(i => $"c{i}")));
        r.Write("D.cs", Lines(Enumerable.Range(1, 20).Select(i => $"d{i}")));
        r.Commit("c1", T0);
        r.Git("mv", "B.txt", "B.cs");
        r.Git("mv", "C.cs", "C.txt");
        r.Git("mv", "D.cs", "E.cs");
        r.Commit("c2", T0.AddHours(1));
        var run = Run(r);
        var s = run.Summary!;
        await Assert.That(run.Files.Keys.Order(StringComparer.Ordinal)).IsEquivalentTo(new[] { "B.cs", "E.cs" }, CollectionOrdering.Matching);
        await Assert.That((run.Files["B.cs"].Commits, run.Files["B.cs"].Added, run.Files["B.cs"].Deleted)).IsEqualTo((1, 50L, 0L));
        await Assert.That((run.Files["E.cs"].Commits, run.Files["E.cs"].Added, run.Files["E.cs"].Deleted)).IsEqualTo((1, 20L, 0L));
        await Assert.That(s.Reader.Renames).IsEqualTo(1);
        await Assert.That(s.Dropped.PureMoveRows).IsEqualTo(1);
        await Assert.That(s.Dropped.OtherFileRows).IsEqualTo(0);
        await Assert.That(s.Dropped.NotAtHeadFiles).IsEqualTo(1); // C.cs
        await Assert.That(s.Dropped.NotAtHeadRows).IsEqualTo(2); // +51 in c1, -51 in c2
        await Assert.That(s.CommitsRead).IsEqualTo(2);
    }

    [Test]
    public async Task Only_csharp_changes_are_read_and_counted()
    {
        // Commits without C# changes are not read; a commit's size for the bulk flag counts its C# files; nested
        // paths and .CS match; a subfolder reads the C# history of the whole repository.
        using var r = new SyntheticRepo();
        r.Write("src/deep/X.cs", "class X { }\n");
        r.Write("Y.CS", "class Y { }\n");
        r.Write("README.md", "readme\n");
        r.Commit("c1", T0);
        r.Write("README.md", "readme 2\n");
        r.Write("docs/a.md", "a\n");
        r.Commit("docs only", T0.AddHours(1));
        r.Write("src/deep/X.cs", "class X { int a; }\n");
        r.Write("Y.CS", "class Y { int a; }\n");
        for (int i = 0; i < 5; i++) r.Write($"docs/n{i}.md", $"{i}\n");
        r.Commit("mixed", T0.AddHours(2));
        r.Git("checkout", "-q", "-b", "side");
        r.Write("docs/side.md", "side\n");
        r.Commit("side docs", T0.AddHours(3));
        r.Git("checkout", "-q", "main");
        r.Write("src/deep/X.cs", "class X { int a, b; }\n");
        r.Commit("main code", T0.AddHours(4));
        var date = "@" + T0.AddHours(5).ToUnixTimeSeconds() + " +0000";
        r.Git(new Dictionary<string, string?> { ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date },
            "merge", "-q", "--no-ff", "--no-edit", "side", "-m", "merge docs");

        var run = Run(r, bulkFlag: 1);
        var s = run.Summary!;
        await Assert.That(s.CommitsRead).IsEqualTo(3); // c1, mixed, main code; not "docs only" or "side docs"
        await Assert.That(s.Reader.Commits).IsEqualTo(3);
        await Assert.That(s.Reader.Rows).IsEqualTo(5); // X and Y in c1 and mixed, X in main code
        await Assert.That(s.Dropped.OtherFileRows).IsEqualTo(0);
        await Assert.That(s.Dropped.MergeCommits).IsEqualTo(1); // differs in C# from its side parent (git --full-history keeps such merges)
        await Assert.That(s.FlaggedBulk.Select(b => b.Files)).IsEquivalentTo(new[] { 2, 2 }, CollectionOrdering.Matching); // mixed and c1: 2 C# files each, not 7 and 3
        await Assert.That(run.Files["src/deep/X.cs"].Commits).IsEqualTo(3);
        await Assert.That(run.Files["Y.CS"].Commits).IsEqualTo(2);

        var sub = HistoryRunner.RunAsync(Path.Combine(r.Root, "src"), new HistoryOptions { Environment = r.Environment, BulkFlag = 1 })
            .GetAwaiter().GetResult();
        await Assert.That(sub.Files.Keys.Order(StringComparer.Ordinal)).IsEquivalentTo(new[] { "Y.CS", "src/deep/X.cs" }, CollectionOrdering.Matching);
        await Assert.That(sub.Summary!.CommitsRead).IsEqualTo(3);
    }

    [Test]
    public async Task Bulk_flag_lists_large_commits()
    {
        using var r = new SyntheticRepo();
        for (int i = 0; i < 6; i++) r.Write($"F{i}.cs", $"class F{i} {{ }}\n");
        var big = r.Commit("big", T0);
        r.Write("F0.cs", "class F0 { int x; }\n");
        r.Commit("small", T0.AddHours(1));
        var run = Run(r, bulkFlag: 5);
        await Assert.That((await Assert.That(run.Summary!.FlaggedBulk).HasSingleItem()).Hash).IsEqualTo(big);
        await Assert.That(run.Summary.FlaggedBulk[0].Files).IsEqualTo(6);
        var dropped = Run(r, bulkFlag: 5, bulkDrop: 5);
        await Assert.That(dropped.Summary!.Dropped.BulkCommits).IsEqualTo(1);
        await Assert.That(dropped.Files).HasSingleItem();
    }
}