using SkarpSharp.History;

namespace SkarpSharp.Tests.History;

/// <summary>Preprocessing as a pure function of raw commits (newest first) and the policy; no git involved.</summary>
public class PreprocessorTests
{
    sealed class Builder
    {
        public PathTable Paths { get; } = new();
        public List<RawCommit> Commits { get; } = [];

        public Builder Commit(string hash, long time, string author = "Alice", string email = "alice@x.org", int parents = 1)
        {
            Commits.Add(new RawCommit { Hash = hash, ParentCount = parents, AuthorName = author, AuthorEmail = email, AuthorTime = time, CommitterTime = time });
            return this;
        }

        public Builder Row(string path, int added, int deleted, string? from = null, bool binary = false)
        {
            Commits[^1].Files.Add(new RawFileRow(added, deleted, binary, Paths.Intern(path), from is null ? -1 : Paths.Intern(from)));
            return this;
        }

        public (Dictionary<string, FileAccumulator> Files, PreprocessCounts Counts, FileAccumulators Acc) Run(HistoryPolicy? policy = null, IReadOnlySet<string>? shallow = null)
        {
            var acc = new FileAccumulators();
            var pre = new Preprocessor(Paths, policy ?? new HistoryPolicy(), acc, shallow);
            foreach (var c in Commits) pre.OnCommit(c);
            pre.OnComplete();
            return (acc.All().ToDictionary(x => Paths[x.FileId], x => x.File), pre.Counts, acc);
        }
    }

    [Test]
    public async Task Rename_chain_maps_older_rows_to_the_head_path()
    {
        // Newest first: c3 renames B.cs -> C.cs, c2 renames A.cs -> B.cs, c1 creates A.cs.
        var (files, counts, _) = new Builder()
            .Commit("c3", 30).Row("C.cs", 1, 1, from: "B.cs")
            .Commit("c2", 20).Row("B.cs", 0, 0, from: "A.cs")
            .Commit("c1", 10).Row("A.cs", 5, 0)
            .Run();
        await Assert.That(files.Keys).IsEquivalentTo(new[] { "C.cs" }, CollectionOrdering.Matching);
        await Assert.That(files["C.cs"].Commits).IsEqualTo(2); // c2 is a pure move: no change
        await Assert.That(files["C.cs"].Added).IsEqualTo(6);
        await Assert.That(counts.PureMoveRows).IsEqualTo(1);
        await Assert.That(files["C.cs"].Renamed).IsTrue();
    }

    [Test]
    public async Task Path_reused_after_a_rename_keeps_two_identities()
    {
        // c3 creates a new A.cs after c2 moved the old A.cs to B.cs; c1 edited the old A.cs.
        var (files, _, _) = new Builder()
            .Commit("c3", 30).Row("A.cs", 7, 0)
            .Commit("c2", 20).Row("B.cs", 1, 0, from: "A.cs")
            .Commit("c1", 10).Row("A.cs", 3, 0)
            .Run();
        await Assert.That(files["A.cs"].Commits).IsEqualTo(1);
        await Assert.That(files["A.cs"].Added).IsEqualTo(7);
        await Assert.That(files["B.cs"].Commits).IsEqualTo(2);
        await Assert.That(files["B.cs"].Added).IsEqualTo(4);
    }

    [Test]
    public async Task Rows_in_one_commit_resolve_before_that_commits_renames()
    {
        // In c2 a file is renamed X.cs -> Y.cs; the same commit also touches Y2.cs. Older X.cs rows go to Y.cs.
        var (files, _, _) = new Builder()
            .Commit("c2", 20).Row("Y.cs", 1, 0, from: "X.cs").Row("Y2.cs", 1, 0)
            .Commit("c1", 10).Row("X.cs", 2, 0)
            .Run();
        await Assert.That(files["Y.cs"].Commits).IsEqualTo(2);
        await Assert.That(files["Y.cs"].Added).IsEqualTo(3);
    }

    [Test]
    public async Task Two_rows_of_one_commit_on_one_file_count_one_commit()
    {
        var (files, _, _) = new Builder()
            .Commit("c2", 20).Row("N.cs", 1, 0, from: "O.cs")
            .Commit("c1", 10).Row("O.cs", 2, 0).Row("N.cs", 4, 0) // impossible in git, but the counter must not double count
            .Run();
        await Assert.That(files["N.cs"].Commits).IsEqualTo(2);
        await Assert.That(files["N.cs"].Added).IsEqualTo(7);
    }

    [Test]
    public async Task Binary_rows_count_zero_lines_and_stay()
    {
        var (files, _, _) = new Builder().Commit("c1", 1).Row("Res.cs", 0, 0, binary: true).Run();
        await Assert.That(files["Res.cs"].Commits).IsEqualTo(1);
        await Assert.That(files["Res.cs"].Added + files["Res.cs"].Deleted).IsEqualTo(0);
    }

    [Test]
    public async Task Generated_build_output_and_non_csharp_rows_are_dropped_by_reason()
    {
        var (files, counts, _) = new Builder()
            .Commit("c1", 1)
            .Row("Form1.Designer.cs", 5, 0).Row("obj/Debug/x.cs", 5, 0).Row("A.g.cs", 1, 0).Row("README.md", 3, 0)
            .Row("Mode.cs", 0, 0).Row("Real.cs", 1, 0)
            .Run();
        await Assert.That(files.Keys).IsEquivalentTo(new[] { "Real.cs" }, CollectionOrdering.Matching);
        await Assert.That(counts.GeneratedRows).IsEqualTo(3);
        await Assert.That(counts.OtherFileRows).IsEqualTo(1);
        await Assert.That(counts.NoChangeRows).IsEqualTo(1);
        await Assert.That(counts.CommitsUsed).IsEqualTo(1);
    }

    /// <summary>
    /// The pure preprocessor still maps such a rename. Git reads only C# changes, so a real read never
    /// delivers the .txt rows (SyntheticHistoryTests.Renames_across_the_csharp_boundary_are_adds_and_deletes).
    /// </summary>
    [Test]
    public async Task Non_csharp_file_renamed_to_csharp_keeps_its_history()
    {
        var (files, counts, _) = new Builder()
            .Commit("c2", 20).Row("Code.cs", 1, 1, from: "Code.txt")
            .Commit("c1", 10).Row("Code.txt", 9, 0)
            .Run();
        await Assert.That(files["Code.cs"].Commits).IsEqualTo(2);
        await Assert.That(counts.OtherFileRows).IsEqualTo(0);
    }

    [Test]
    public async Task Bots_are_excluded_by_default_and_counted()
    {
        var b = new Builder()
            .Commit("c3", 30, "dependabot[bot]", "49699333+dependabot[bot]@users.noreply.github.com").Row("A.cs", 1, 1)
            .Commit("c2", 20, "Renovate Bot", "bot@renovateapp.com").Row("A.cs", 1, 1)
            .Commit("c1", 10).Row("A.cs", 1, 0);
        var (files, counts, _) = b.Run();
        await Assert.That(files["A.cs"].Commits).IsEqualTo(1);
        await Assert.That(counts.BotCommits).IsEqualTo(2);
        await Assert.That(b.Run(new HistoryPolicy { IncludeBots = true }).Files["A.cs"].Commits).IsEqualTo(3);
        var extra = new HistoryPolicy { BotPatterns = [.. HistoryPolicy.DefaultBotPatterns, "^Alice$"] };
        await Assert.That(b.Run(extra).Counts.BotCommits).IsEqualTo(3);
    }

    [Test]
    public async Task Bulk_commits_are_flagged_and_optionally_dropped_on_the_csharp_file_count()
    {
        var b = new Builder().Commit("big", 2);
        for (int i = 0; i < 4; i++) b.Row($"F{i}.cs", 1, 0);
        b.Row("obj/G.cs", 1, 0); // generated C# counts toward the size (C# rows count before the generated filter)
        b.Row("notes.txt", 1, 0).Row("readme.md", 1, 0); // non-C# rows do not count
        b.Commit("small", 1).Row("F0.cs", 1, 0);
        var (files, counts, _) = b.Run(new HistoryPolicy { BulkFlag = 4 });
        await Assert.That(counts.FlaggedBulk).IsEquivalentTo(new[] { ("big", 5) }, CollectionOrdering.Matching);
        await Assert.That(files["F0.cs"].Commits).IsEqualTo(2);
        await Assert.That(b.Run(new HistoryPolicy { BulkFlag = 5 }).Counts.FlaggedBulk).IsEmpty();
        var dropped = b.Run(new HistoryPolicy { BulkFlag = 4, BulkDrop = 4 });
        await Assert.That(dropped.Counts.BulkCommits).IsEqualTo(1);
        await Assert.That(dropped.Files["F0.cs"].Commits).IsEqualTo(1);
        await Assert.That(dropped.Files.ContainsKey("F1.cs")).IsFalse();
    }

    [Test]
    public async Task Window_merges_and_shallow_boundaries_drop_whole_commits_but_keep_renames()
    {
        var (files, counts, _) = new Builder()
            .Commit("m", 40, parents: 2)
            .Commit("c3", 30).Row("New.cs", 1, 0)
            .Commit("old", 5).Row("New.cs", 2, 0, from: "Old.cs") // outside the window, rename still recorded
            .Commit("ancient", 4).Row("Old.cs", 3, 0)
            .Commit("edge", 3).Row("Old.cs", 1, 0)
            .Run(new HistoryPolicy { SinceUnix = 10 }, shallow: new HashSet<string> { "edge" });
        await Assert.That(files["New.cs"].Commits).IsEqualTo(1);
        await Assert.That(counts.MergeCommits).IsEqualTo(1);
        await Assert.That(counts.OutsideWindowCommits).IsEqualTo(2);
        await Assert.That(counts.ShallowBoundaryCommits).IsEqualTo(1);
        await Assert.That(counts.CommitsRead).IsEqualTo(5);
    }

    [Test]
    public async Task Authors_are_distinct_lowercase_emails_and_dates_are_author_times()
    {
        var (files, _, acc) = new Builder()
            .Commit("c3", 300, "Bob", "bob@x.org").Row("A.cs", 1, 0)
            .Commit("c2", 200, "Alice B", "ALICE@X.ORG").Row("A.cs", 1, 0)
            .Commit("c1", 100).Row("A.cs", 1, 0)
            .Run();
        await Assert.That(files["A.cs"].Authors).IsEqualTo(2);
        await Assert.That(files["A.cs"].FirstChange).IsEqualTo(100);
        await Assert.That(files["A.cs"].LastChange).IsEqualTo(300);
        await Assert.That(acc.TotalRows).IsEqualTo(3);
    }

    [Test]
    public async Task Policy_digest_changes_with_every_setting()
    {
        var digests = new[]
        {
            new HistoryPolicy(), new HistoryPolicy { IgnoreWhitespace = true }, new HistoryPolicy { SinceUnix = 1 },
            new HistoryPolicy { BulkFlag = 30 }, new HistoryPolicy { BulkDrop = 300 }, new HistoryPolicy { IncludeBots = true },
            new HistoryPolicy { BotPatterns = ["x"] },
        }.Select(p => p.Digest()).ToList();
        await Assert.That(digests.Distinct().Count()).IsEqualTo(digests.Count);
        await Assert.That(new HistoryPolicy().Digest()).IsEqualTo(new HistoryPolicy().Digest());
    }

    [Test]
    public async Task Log_arguments_pin_the_policy()
    {
        var args = new HistoryPolicy { IgnoreWhitespace = true, SinceUnix = 1700000000 }.LogArguments();
        await Assert.That(args).Contains("--no-merges");
        await Assert.That(args).Contains("-M50%");
        await Assert.That(args).Contains("-l1000");
        await Assert.That(args).Contains("--no-ext-diff");
        await Assert.That(args).Contains("--no-textconv");
        await Assert.That(args).Contains("log.showSignature=false");
        await Assert.That(args).Contains("-w");
        await Assert.That(args).Contains("--since=@1700000000");
        await Assert.That(args).Contains("--full-history");
        await Assert.That(args[^2]).IsEqualTo("--");
        await Assert.That(args[^1]).IsEqualTo(":(top,icase)*.cs"); // C# changes only
        await Assert.That(args[^1]).IsEqualTo(HistoryPolicy.CSharpPathspec);
    }

    [Test]
    [Arguments("2.56.1.windows.1", "2.56.1")]
    [Arguments("2.39.5 (Apple Git-154)", "2.39.5")]
    [Arguments("2.15", "2.15")]
    public async Task Git_version_text_is_parsed(string text, string expected) =>
        await Assert.That(GitEnvironment.ParseVersion(text)).IsEqualTo(Version.Parse(expected));
}