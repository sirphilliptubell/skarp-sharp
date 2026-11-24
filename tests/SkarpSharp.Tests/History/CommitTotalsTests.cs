using System.Text.Json;
using SkarpSharp.Baselines;
using SkarpSharp.History;
using SkarpSharp.Tests.Baselines;

namespace SkarpSharp.Tests.History;

/// <summary>
/// Commits in the repository (every commit, no pathspec), outside-window commits of any kind, and the C# commit
/// total obtained from the commit and count a baseline recorded.
/// </summary>
public class CommitTotalsTests
{
    static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1700000000);

    static HistoryRun Run(SyntheticRepo r, HistoryOptions? o = null)
    {
        o ??= new HistoryOptions();
        return HistoryRunner.RunAsync(r.Root, new HistoryOptions
        {
            Environment = r.Environment, WindowMonths = o.WindowMonths, AllHistory = o.AllHistory,
            Anchor = o.Anchor, AnchorMissingNote = o.AnchorMissingNote,
        }).GetAwaiter().GetResult();
    }

    /// <summary>Old: C# commit, docs-only commit, a C# side branch merged; then (60 days later) a C# commit and a docs commit.</summary>
    static SyntheticRepo Build()
    {
        var r = new SyntheticRepo();
        r.Write("A.cs", "a\n");
        r.Commit("c1", T0);
        r.Write("README.md", "docs\n");
        r.Commit("docs1", T0.AddDays(1));
        r.Git("checkout", "-q", "-b", "side");
        r.Write("S.cs", "s\n");
        r.Commit("side1", T0.AddDays(2));
        r.Git("checkout", "-q", "main");
        r.Write("README.md", "docs 2\n");
        r.Commit("docs2", T0.AddDays(3));
        var date = "@" + T0.AddDays(4).ToUnixTimeSeconds() + " +0000";
        r.Git(new Dictionary<string, string?> { ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date },
            "merge", "-q", "--no-ff", "--no-edit", "side", "-m", "merge");
        r.Write("A.cs", "a\nb\n");
        r.Commit("c2", T0.AddDays(60));
        r.Write("README.md", "docs 3\n");
        r.Commit("docs3", T0.AddDays(61));
        return r;
    }

    static int CSharpCount(SyntheticRepo r, string range = "HEAD") =>
        int.Parse(r.Git("rev-list", "--count", "--full-history", range, "--", HistoryPolicy.CSharpPathspec).Trim());

    [Test]
    public async Task Commits_in_repository_count_every_commit_and_outside_window_counts_any_kind()
    {
        using var r = Build();
        var s = Run(r, new HistoryOptions { WindowMonths = 1 }).Summary!;
        await Assert.That(s.CommitsInRepository).IsEqualTo(7); // c1, docs1, side1, docs2, merge, c2, docs3
        await Assert.That(s.CommitsRead).IsEqualTo(1);           // c2
        await Assert.That(s.Dropped.MergeCommits).IsEqualTo(0);  // the merge is outside the window
        await Assert.That(s.Dropped.OutsideWindowCommits).IsEqualTo(5); // c1, docs1, side1, docs2, merge (any kind)
        var all = Run(r, new HistoryOptions { AllHistory = true }).Summary!;
        await Assert.That(all.CommitsInRepository).IsEqualTo(7);
        await Assert.That(all.Dropped.OutsideWindowCommits).IsEqualTo(0);
        // Whole history: commits read plus C# merges is the C#-filtered total.
        await Assert.That(all.CommitsRead + all.Dropped.MergeCommits).IsEqualTo(CSharpCount(r));
    }

    [Test]
    public async Task Without_an_anchor_there_is_no_csharp_total_and_the_note_says_why()
    {
        using var r = Build();
        var run = Run(r, new HistoryOptions { AnchorMissingNote = "no baseline file; C# commit total not given" });
        await Assert.That(run.Summary!.CSharpCommitsInRepository).IsNull();
        await Assert.That(run.Summary.CSharpCommitsSource).IsNull();
        await Assert.That(run.Git.Notices).Contains("no baseline file; C# commit total not given");
        await Assert.That(run.Summary.CommitsInRepository).IsEqualTo(7);
    }

    [Test]
    public async Task Anchor_at_an_ancestor_adds_the_csharp_commits_since_it()
    {
        using var r = Build();
        r.Git("reset", "-q", "--hard", "HEAD~2"); // back to the merge
        var anchor = CommitAnchor.RecordAsync(r.Root, r.Environment).GetAwaiter().GetResult()!;
        await Assert.That(anchor.Commit).IsEqualTo(r.Git("rev-parse", "HEAD").Trim());
        await Assert.That(anchor.CSharpCommits).IsEqualTo(CSharpCount(r));
        r.Write("A.cs", "a\nb\n");
        r.Commit("c2", T0.AddDays(60));
        r.Write("README.md", "docs 3\n");
        r.Commit("docs3", T0.AddDays(61));
        r.Write("B.CS", "b\n");
        r.Commit("c3", T0.AddDays(62));
        var run = Run(r, new HistoryOptions { Anchor = anchor });
        var s = run.Summary!;
        await Assert.That(s.CSharpCommitsInRepository).IsEqualTo(CSharpCount(r));
        await Assert.That(s.CSharpCommitsInRepository).IsEqualTo(anchor.CSharpCommits + 2);
        await Assert.That(s.CSharpCommitsSource).IsEqualTo("baseline");
        await Assert.That(s.CSharpCommitsBaselineCommit).IsEqualTo(anchor.Commit);
        await Assert.That(run.Git.Notices).DoesNotContain(n => n.Contains("C# commit total", StringComparison.Ordinal));
    }

    [Test]
    public async Task Anchor_that_is_not_an_ancestor_falls_back_with_a_note()
    {
        using var r = Build();
        r.Git("checkout", "-q", "-b", "other", "HEAD~1");
        r.Write("O.cs", "o\n");
        r.Commit("other1", T0.AddDays(70));
        var anchor = CommitAnchor.RecordAsync(r.Root, r.Environment).GetAwaiter().GetResult()!;
        r.Git("checkout", "-q", "main");
        var run = Run(r, new HistoryOptions { Anchor = anchor });
        await Assert.That(run.Summary!.CSharpCommitsInRepository).IsNull();
        await Assert.That(run.Summary.CSharpCommitsSource).IsNull();
        await Assert.That(run.Git.Notices).Contains(n => n.Contains("is not an ancestor of HEAD", StringComparison.Ordinal));
        await Assert.That(run.Summary.CommitsInRepository).IsEqualTo(7);
    }

    [Test]
    public async Task Anchor_missing_from_the_clone_falls_back_with_a_note()
    {
        using var r = Build();
        var run = Run(r, new HistoryOptions { Anchor = new CommitAnchor(new string('a', 40), 5) });
        await Assert.That(run.Summary!.CSharpCommitsInRepository).IsNull();
        await Assert.That(run.Git.Notices).Contains(n => n.Contains("is not in this clone", StringComparison.Ordinal));
    }

    [Test]
    public async Task Recorded_anchor_must_be_a_full_hash()
    {
        await Assert.That(CommitAnchor.FromRecorded(new string('0', 40), 3)).IsNotNull();
        await Assert.That(CommitAnchor.FromRecorded("--output=x", 3)).IsNull();
        await Assert.That(CommitAnchor.FromRecorded("abc123", 3)).IsNull();
        await Assert.That(CommitAnchor.FromRecorded(new string('0', 40), null)).IsNull();
        await Assert.That(CommitAnchor.FromRecorded(null, 3)).IsNull();
    }

    [Test]
    public async Task Baseline_records_the_anchor_and_history_reports_the_csharp_total()
    {
        using var r = Build();
        var b = BaselineTests.Run("baseline", r.Root, "--parallelism", "1");
        await Assert.That(b.Exit).IsEqualTo(0);
        var file = Baseline.Read(Path.Combine(r.Root, Baseline.FileName));
        await Assert.That(file.Commit).IsEqualTo(r.Git("rev-parse", "HEAD").Trim());
        await Assert.That(file.CsharpCommits).IsEqualTo(CSharpCount(r));

        r.Write("C.cs", "class C { }\n");
        r.Commit("c4", T0.AddDays(80));
        var h = BaselineTests.Run("history", r.Root, "--format", "json");
        await Assert.That(h.Exit).IsEqualTo(0);
        var history = JsonDocument.Parse(h.Json).RootElement.GetProperty("history");
        await Assert.That(history.GetProperty("csharpCommitsInRepository").GetInt32()).IsEqualTo(CSharpCount(r));
        await Assert.That(history.GetProperty("csharpCommitsSource").GetString()).IsEqualTo("baseline");
        await Assert.That(history.GetProperty("csharpCommitsBaselineCommit").GetString()).IsEqualTo(file.Commit);
        await Assert.That(history.GetProperty("commitsInRepository").GetInt32()).IsEqualTo(8);

        var text = BaselineTests.Run("history", r.Root).Out;
        await Assert.That(text).Contains($"; 8 commits in repository ({CSharpCount(r)} C#, via baseline {file.Commit![..12]})");

        // Older baselines without the fields still load; the run says why there is no C# total.
        var old = File.ReadAllLines(Path.Combine(r.Root, Baseline.FileName)).Where(l => !l.Contains("\"commit\"") && !l.Contains("\"csharpCommits\""));
        File.WriteAllLines(Path.Combine(r.Root, Baseline.FileName), old);
        var o = BaselineTests.Run("history", r.Root);
        await Assert.That(o.Out).Contains("note:       the baseline records no commit count");
        await Assert.That(o.Out.Replace("\r\n", "\n")).Contains("; 8 commits in repository\n");
    }
}