using SkarpSharp.Discovery;

namespace SkarpSharp.History;

/// <summary>One row that survived the policy, keyed by its stable file id (the path at HEAD, as a path id).</summary>
public readonly record struct EligibleRow(int FileId, int Added, int Deleted, bool Binary, bool FromRename);

/// <summary>One eligible commit (change set) with its person id (lowercase email after mailmap).</summary>
public readonly record struct ChangeSet(int Sequence, string Hash, long AuthorTime, long CommitterTime, int PersonId, int RawFileCount);

/// <summary>Receives the cleaned change sets (rows valid only during the call).</summary>
public interface IChangeSetSink
{
    void OnChangeSet(in ChangeSet changeSet, ReadOnlySpan<EligibleRow> rows);
}

/// <summary>Counts of what preprocessing read, kept and dropped, by reason.</summary>
public sealed class PreprocessCounts
{
    public int CommitsRead { get; set; }
    public int CommitsUsed { get; set; }
    public int MergeCommits { get; set; }
    public int OutsideWindowCommits { get; set; }
    public int BotCommits { get; set; }
    public int BulkCommits { get; set; }
    public int ShallowBoundaryCommits { get; set; }
    public long GeneratedRows { get; set; }
    public long OtherFileRows { get; set; }
    public long PureMoveRows { get; set; }
    public long NoChangeRows { get; set; }
    public List<(string Hash, int Files)> FlaggedBulk { get; } = [];
}

/// <summary>
/// History preprocessing as a deterministic function of the raw commit stream
/// and the policy: no I/O, no clock. Commits arrive newest first, so a rename seen at commit c maps the old path of every
/// older row to the file's path at HEAD (alias = resolve(new)); path reuse after a rename resolves correctly because
/// newer rows were attributed before the rename was seen. Renames are applied for every commit, even dropped ones, so
/// path continuity never depends on the drop rules. Row rules: pure moves (rename, 0/0, not binary) and other 0/0 text
/// rows are no change; binary rows count 0/0 and stay; generated and build-output paths and non-C# files are dropped,
/// judged by the resolved (HEAD) path so a file renamed to .cs keeps its earlier history.
/// </summary>
public sealed class Preprocessor : IHistoryConsumer
{
    enum PathKind : byte { Unknown, CSharp, Generated, Other }

    readonly PathTable _paths;
    readonly HistoryPolicy _policy;
    readonly IReadOnlySet<string> _shallowBoundaries;
    readonly IChangeSetSink _sink;
    readonly Dictionary<int, int> _alias = [];
    readonly Dictionary<string, int> _people = new(StringComparer.Ordinal);
    readonly List<PathKind> _kinds = [];
    readonly List<EligibleRow> _rows = [];
    readonly List<int> _resolved = [];
    int _sequence;

    public Preprocessor(PathTable paths, HistoryPolicy policy, IChangeSetSink sink, IReadOnlySet<string>? shallowBoundaries = null)
    {
        _paths = paths;
        _policy = policy;
        _sink = sink;
        _shallowBoundaries = shallowBoundaries ?? new HashSet<string>();
    }

    public PreprocessCounts Counts { get; } = new();

    /// <summary>Distinct people seen in eligible commits.</summary>
    public int People => _people.Count;

    /// <summary>Stable file id of a path id: its path at HEAD, as far as the renames read so far tell.</summary>
    public int Resolve(int pathId) => _alias.TryGetValue(pathId, out var id) ? id : pathId;

    public void OnCommit(RawCommit c)
    {
        Counts.CommitsRead++;
        // Resolve all rows against the state before this commit's renames, then record the renames.
        _resolved.Clear();
        foreach (var r in c.Files) _resolved.Add(Resolve(r.PathId));
        for (int i = 0; i < c.Files.Count; i++)
            if (c.Files[i].IsRename && c.Files[i].OldPathId != c.Files[i].PathId)
                _alias[c.Files[i].OldPathId] = _resolved[i];

        if (c.ParentCount > 1) { Counts.MergeCommits++; return; }
        if (_shallowBoundaries.Contains(c.Hash)) { Counts.ShallowBoundaryCommits++; return; }
        if (_policy.SinceUnix is long since && c.CommitterTime < since) { Counts.OutsideWindowCommits++; return; }

        int rawFileCount = CSharpRows(c);
        if (rawFileCount > _policy.BulkFlag) Counts.FlaggedBulk.Add((c.Hash, rawFileCount));
        if (_policy.IsBot(c.AuthorName, c.AuthorEmail)) { Counts.BotCommits++; return; }
        if (_policy.BulkDrop is int cap && rawFileCount > cap) { Counts.BulkCommits++; return; }

        _rows.Clear();
        for (int i = 0; i < c.Files.Count; i++)
        {
            var r = c.Files[i];
            if (!r.Binary && r.Added == 0 && r.Deleted == 0)
            {
                if (r.IsRename) Counts.PureMoveRows++; else Counts.NoChangeRows++;
                continue;
            }
            int file = _resolved[i];
            switch (Kind(file))
            {
                case PathKind.Generated: Counts.GeneratedRows++; continue;
                case PathKind.Other: Counts.OtherFileRows++; continue;
            }
            _rows.Add(new EligibleRow(file, r.Added, r.Deleted, r.Binary, r.IsRename));
        }
        if (_rows.Count == 0) return;

        Counts.CommitsUsed++;
        var email = c.AuthorEmail.ToLowerInvariant();
        if (!_people.TryGetValue(email, out var person)) _people[email] = person = _people.Count;
        var set = new ChangeSet(_sequence++, c.Hash, c.AuthorTime, c.CommitterTime, person, rawFileCount);
        _sink.OnChangeSet(set, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_rows));
    }

    public void OnComplete() { }

    /// <summary>
    /// A commit's size for the bulk rules: its C# rows, before the generated, build-output and no-change
    /// filters. Git already reads only C# rows; counted here too so the rule does not depend on the read.
    /// </summary>
    int CSharpRows(RawCommit c)
    {
        int n = 0;
        foreach (var r in c.Files)
            if (_paths[r.PathId].EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }

    PathKind Kind(int pathId)
    {
        while (_kinds.Count <= pathId) _kinds.Add(PathKind.Unknown);
        var k = _kinds[pathId];
        if (k != PathKind.Unknown) return k;
        k = Classify(_paths[pathId]);
        _kinds[pathId] = k;
        return k;
    }

    static PathKind Classify(string path)
    {
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return PathKind.Other;
        if (GeneratedCode.IsGeneratedFileName(path)) return PathKind.Generated;
        int start = 0;
        for (int slash = path.IndexOf('/'); slash >= 0; start = slash + 1, slash = path.IndexOf('/', start))
            if (FileDiscovery.IsPrunedDirectoryName(path[start..slash])) return PathKind.Generated;
        return PathKind.CSharp;
    }

    /// <summary>True when a repository path is a C# file that history counts (same rules as the rows).</summary>
    public static bool IsEligiblePath(string path) => Classify(path) == PathKind.CSharp;
}
