using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using SkarpSharp.Model;

namespace SkarpSharp.History;

/// <summary>Settings for one history run.</summary>
public sealed class HistoryOptions
{
    /// <summary>Explicit window start (<c>--since</c>); overrides <see cref="WindowMonths"/>.</summary>
    public DateTimeOffset? Since { get; init; }
    /// <summary>Window length ending at HEAD's committer date.</summary>
    public int WindowMonths { get; init; } = 12;
    public bool AllHistory { get; init; }
    public bool IgnoreWhitespace { get; init; }
    public int BulkFlag { get; init; } = HistoryPolicy.DefaultBulkFlag;
    public int? BulkDrop { get; init; }
    public bool IncludeBots { get; init; }
    /// <summary>Added to the default bot patterns (<c>--bot-pattern</c>).</summary>
    public IReadOnlyList<string> ExtraBotPatterns { get; init; } = [];
    /// <summary>Extra environment for git (tests isolate configuration this way).</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }
    /// <summary>Additional raw-row consumer, for example a result-store writer.</summary>
    public IHistoryConsumer? RawConsumer { get; init; }
    /// <summary>Additional consumer of the cleaned change sets (change coupling later; tests).</summary>
    public IChangeSetSink? ChangeSetSink { get; init; }
    /// <summary>The baseline's commit and C# commit count, for the C# commit total at HEAD.</summary>
    public CommitAnchor? Anchor { get; set; }
    /// <summary>Why there is no <see cref="Anchor"/> (added as a notice); null adds none.</summary>
    public string? AnchorMissingNote { get; set; }
}

/// <summary>Result of one history run.</summary>
public sealed class HistoryRun
{
    public GitRunInfo Git { get; init; } = new();
    /// <summary>Null when history could not be read (see <see cref="GitRunInfo.Notices"/>).</summary>
    public HistorySummary? Summary { get; init; }
    /// <summary>Work tree root (or git directory of a bare repository) as a full path.</summary>
    public string? RepositoryRoot { get; init; }
    /// <summary>History of each file present at HEAD with at least one eligible commit, by repository-relative path.</summary>
    public IReadOnlyDictionary<string, FileHistory> Files { get; init; } = new Dictionary<string, FileHistory>();
    /// <summary>Files (at HEAD) that received rows through a rename.</summary>
    public IReadOnlySet<string> RenamedFiles { get; init; } = new HashSet<string>();
    /// <summary>Sums over every accumulator, including files absent at HEAD (invariant checks).</summary>
    public (long Rows, long Added, long Deleted) AllFileTotals { get; init; }
}

/// <summary>
/// Reads the window once from git (one streamed <c>git log</c>) and turns it into per-file history: probe, read,
/// preprocess, accumulate, keep files present at HEAD. Side counts (merges, outside window, the HEAD file list) come
/// from small git calls that run while the log streams.
/// </summary>
public static class HistoryRunner
{
    public static async Task<HistoryRun> RunAsync(string directory, HistoryOptions options, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        directory = Path.GetFullPath(directory);
        var env = await GitEnvironment.ProbeAsync(directory, options.Environment, cancellationToken).ConfigureAwait(false);
        var git = new GitRunInfo { Version = env.Version, RepositoryRoot = env.RepositoryRoot, Head = env.Head };
        git.Notices.AddRange(env.Notices);
        if (!env.Usable)
        {
            git.DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 3);
            return new HistoryRun { Git = git };
        }

        long? since = options.AllHistory ? null
            : options.Since is { } s ? s.ToUnixTimeSeconds()
            : DateTimeOffset.FromUnixTimeSeconds(env.HeadCommitterTime).AddMonths(-Math.Max(0, options.WindowMonths)).ToUnixTimeSeconds();
        var policy = new HistoryPolicy
        {
            SinceUnix = since,
            IgnoreWhitespace = options.IgnoreWhitespace,
            BulkFlag = options.BulkFlag,
            BulkDrop = options.BulkDrop,
            IncludeBots = options.IncludeBots,
            BotPatterns = [.. HistoryPolicy.DefaultBotPatterns, .. options.ExtraBotPatterns],
        };
        git.WindowStart = since is long st ? HistoryPolicy.Iso(st) : null;
        git.WindowEnd = HistoryPolicy.Iso(env.HeadCommitterTime);
        git.Policy = policy.Describe();
        git.PolicyDigest = policy.Digest();

        var paths = new PathTable();
        var accumulators = new FileAccumulators();
        IChangeSetSink sink = options.ChangeSetSink is null ? accumulators : new SinkFanOut(accumulators, options.ChangeSetSink);
        var pre = new Preprocessor(paths, policy, sink, env.ShallowBoundaries);
        IHistoryConsumer consumer = options.RawConsumer is null ? pre : new ConsumerFanOut(pre, options.RawConsumer);
        var parser = new GitLogParser(paths, consumer);

        var headTree = new MemoryStream();
        var e = options.Environment;
        var lsTask = GitProcess.RunStreamAsync(directory, ["ls-tree", "-r", "-z", "--name-only", "--full-tree", "HEAD"],
            (stream, ct) => stream.CopyToAsync(headTree, ct), e, cancellationToken);
        var side = SideCounts.Start(directory, since, options, cancellationToken);
        var log = await GitProcess.RunStreamAsync(directory, policy.LogArguments(), async (stream, ct) =>
        {
            var buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
            try
            {
                int n;
                while ((n = await stream.ReadAsync(buffer.AsMemory(0, 1 << 16), ct).ConfigureAwait(false)) > 0)
                    parser.Feed(buffer.AsSpan(0, n));
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }, e, cancellationToken).ConfigureAwait(false);
        var ls = await lsTask.ConfigureAwait(false);

        if (!log.Succeeded || !ls.Succeeded)
        {
            git.Notices.Add($"git log failed (exit {log.ExitCode}): {(log.Succeeded ? ls.FirstErrorLine : log.FirstErrorLine)}; history skipped");
            git.DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 3);
            return new HistoryRun { Git = git, RepositoryRoot = Path.GetFullPath(env.RepositoryRoot!) };
        }
        parser.Complete();
        foreach (var line in log.StandardError.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().Take(3))
            git.Notices.Add("git: " + line + (line.Contains("rename", StringComparison.OrdinalIgnoreCase)
                ? " (commits with very many renames fell back to delete plus add)" : ""));

        // Files present at HEAD.
        var headIds = new HashSet<int>();
        var tree = headTree.GetBuffer().AsSpan(0, (int)headTree.Length);
        while (!tree.IsEmpty)
        {
            int nul = tree.IndexOf((byte)0);
            var name = nul < 0 ? tree : tree[..nul];
            if (!name.IsEmpty) headIds.Add(paths.Intern(name));
            tree = nul < 0 ? default : tree[(nul + 1)..];
        }

        var counts = pre.Counts;
        var summary = new HistorySummary
        {
            CommitsRead = counts.CommitsRead,
            CommitsUsed = counts.CommitsUsed,
            People = pre.People,
            Reader = new ReaderCounts
            {
                Commits = parser.Totals.Commits, Rows = parser.Totals.Rows, Added = parser.Totals.Added, Deleted = parser.Totals.Deleted,
                Renames = parser.Totals.Renames, BinaryRows = parser.Totals.BinaryRows, Malformed = parser.Totals.Malformed,
            },
            Dropped = new DroppedCounts
            {
                MergeCommits = counts.MergeCommits,
                OutsideWindowCommits = counts.OutsideWindowCommits,
                BotCommits = counts.BotCommits,
                BulkCommits = counts.BulkCommits,
                ShallowBoundaryCommits = counts.ShallowBoundaryCommits,
                GeneratedRows = counts.GeneratedRows,
                OtherFileRows = counts.OtherFileRows,
                PureMoveRows = counts.PureMoveRows,
                NoChangeRows = counts.NoChangeRows,
            },
            FlaggedBulk = [.. counts.FlaggedBulk.Select(b => new BulkCommit { Hash = b.Hash, Files = b.Files })],
        };
        await side.ApplyAsync(summary, git, options.Anchor).ConfigureAwait(false);

        var files = new Dictionary<string, FileHistory>(StringComparer.Ordinal);
        var renamed = new HashSet<string>(StringComparer.Ordinal);
        long allRows = 0, allAdded = 0, allDeleted = 0;
        foreach (var (id, f) in accumulators.All())
        {
            allRows += f.Rows; allAdded += f.Added; allDeleted += f.Deleted;
            if (!headIds.Contains(id))
            {
                summary.Dropped.NotAtHeadRows += f.Rows;
                summary.Dropped.NotAtHeadFiles++;
                continue;
            }
            var path = paths[id];
            files[path] = new FileHistory
            {
                Commits = f.Commits, Added = f.Added, Deleted = f.Deleted, Authors = f.Authors, Rows = f.Rows,
                FirstChange = HistoryPolicy.Iso(f.FirstChange), LastChange = HistoryPolicy.Iso(f.LastChange),
            };
            if (f.Renamed) renamed.Add(path);
            summary.Files++;
            summary.Rows += f.Rows;
            summary.Added += f.Added;
            summary.Deleted += f.Deleted;
        }

        git.DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 3);
        return new HistoryRun
        {
            Git = git,
            Summary = summary,
            RepositoryRoot = Path.GetFullPath(env.RepositoryRoot!),
            Files = files,
            RenamedFiles = renamed,
            AllFileTotals = (allRows, allAdded, allDeleted),
        };
    }

    /// <summary>
    /// The small git counts that run while the log streams. Merges keep the log's C# pathspec (window-limited,
    /// or with --all-history concurrent with a log that walks the same commits with the same pathspec, so never the longest
    /// part); the whole-history and in-window totals are counted without it, so git diffs no trees (the C#-filtered
    /// whole-history count cost 2.65 s on aspnetcore against 0.15 s unfiltered). Outside-window commits are their
    /// difference: commits of any kind. The C# total at HEAD comes from the baseline's anchor when there is one.
    /// </summary>
    sealed class SideCounts
    {
        Task<int> _merges = null!, _all = null!, _inWindow = null!;
        Task<(int? Total, string? Note)> _csharp = null!;

        public static SideCounts Start(string directory, long? since, HistoryOptions options, CancellationToken ct)
        {
            var e = options.Environment;
            return new SideCounts
            {
                _merges = Count(directory, since, merges: true, e, ct),
                _all = Count(directory, null, merges: false, e, ct),
                _inWindow = since is null ? Task.FromResult(-1) : Count(directory, since, merges: false, e, ct),
                _csharp = options.Anchor is { } anchor ? anchor.TotalAtHeadAsync(directory, e, ct)
                    : Task.FromResult<(int? Total, string? Note)>((null, options.AnchorMissingNote)),
            };
        }

        public async Task ApplyAsync(HistorySummary summary, GitRunInfo git, CommitAnchor? anchor)
        {
            int merges = await _merges.ConfigureAwait(false), all = await _all.ConfigureAwait(false), inWindow = await _inWindow.ConfigureAwait(false);
            var (csharp, note) = await _csharp.ConfigureAwait(false);
            summary.Dropped.MergeCommits += Math.Max(0, merges);
            if (all >= 0 && inWindow >= 0) summary.Dropped.OutsideWindowCommits += all - inWindow;
            summary.CommitsInRepository = all >= 0 ? all : null;
            summary.CSharpCommitsInRepository = csharp;
            summary.CSharpCommitsSource = csharp is null ? null : "baseline";
            summary.CSharpCommitsBaselineCommit = csharp is null ? null : anchor?.Commit;
            if (merges < 0) git.Notices.Add("merge commit count unavailable (git rev-list failed)");
            if (all < 0) git.Notices.Add("commit count of the repository unavailable (git rev-list failed)");
            if (note is not null) git.Notices.Add(note);
        }
    }

    /// <summary>Merges are counted with the C# pathspec (merges that change C# files); totals without it.</summary>
    static async Task<int> Count(string directory, long? since, bool merges, IReadOnlyDictionary<string, string?>? env, CancellationToken ct)
    {
        var args = new List<string> { "rev-list", "--count", "--full-history" };
        if (merges) args.Add("--merges");
        if (since is long s) args.Add(HistoryPolicy.SinceArgument(s));
        args.Add("HEAD");
        if (merges)
        {
            args.Add("--");
            args.Add(HistoryPolicy.CSharpPathspec);
        }
        var r = await GitProcess.RunTextAsync(directory, args, env, ct).ConfigureAwait(false);
        return r.Succeeded && int.TryParse(r.StandardOutput.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
    }

    sealed class ConsumerFanOut(IHistoryConsumer a, IHistoryConsumer b) : IHistoryConsumer
    {
        public void OnCommit(RawCommit commit) { a.OnCommit(commit); b.OnCommit(commit); }
        public void OnComplete() { a.OnComplete(); b.OnComplete(); }
    }

    sealed class SinkFanOut(IChangeSetSink a, IChangeSetSink b) : IChangeSetSink
    {
        public void OnChangeSet(in ChangeSet changeSet, ReadOnlySpan<EligibleRow> rows) { a.OnChangeSet(changeSet, rows); b.OnChangeSet(changeSet, rows); }
    }
}
