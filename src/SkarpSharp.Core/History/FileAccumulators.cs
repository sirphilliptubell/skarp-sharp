namespace SkarpSharp.History;

/// <summary>Per-file history counters (churn, change frequency).</summary>
public sealed class FileAccumulator
{
    public int Commits { get; internal set; }
    public int Rows { get; internal set; }
    public long Added { get; internal set; }
    public long Deleted { get; internal set; }
    /// <summary>Oldest author time in the window (unix seconds).</summary>
    public long FirstChange { get; internal set; } = long.MaxValue;
    /// <summary>Newest author time in the window (unix seconds).</summary>
    public long LastChange { get; internal set; } = long.MinValue;
    /// <summary>True when any eligible row reached this file through a rename.</summary>
    public bool Renamed { get; internal set; }
    internal int LastSequence = -1;
    internal int FirstPerson = -1;
    internal HashSet<int>? MorePeople;

    public int Authors => FirstPerson < 0 ? 0 : 1 + (MorePeople?.Count ?? 0);
}

/// <summary>
/// One accumulator per stable file id, updated per eligible row; nothing proportional to the number of commits is kept.
/// A commit counts once per file even if two of its rows map to the same file.
/// </summary>
public sealed class FileAccumulators : IChangeSetSink
{
    readonly List<FileAccumulator?> _files = [];

    public long TotalRows { get; private set; }
    public long TotalAdded { get; private set; }
    public long TotalDeleted { get; private set; }

    public void OnChangeSet(in ChangeSet changeSet, ReadOnlySpan<EligibleRow> rows)
    {
        foreach (var r in rows)
        {
            while (_files.Count <= r.FileId) _files.Add(null);
            var f = _files[r.FileId] ??= new FileAccumulator();
            if (f.LastSequence != changeSet.Sequence)
            {
                f.LastSequence = changeSet.Sequence;
                f.Commits++;
                if (f.FirstPerson < 0) f.FirstPerson = changeSet.PersonId;
                else if (f.FirstPerson != changeSet.PersonId) (f.MorePeople ??= []).Add(changeSet.PersonId);
                if (changeSet.AuthorTime < f.FirstChange) f.FirstChange = changeSet.AuthorTime;
                if (changeSet.AuthorTime > f.LastChange) f.LastChange = changeSet.AuthorTime;
            }
            f.Rows++;
            f.Added += r.Added;
            f.Deleted += r.Deleted;
            if (r.FromRename) f.Renamed = true;
            TotalRows++;
            TotalAdded += r.Added;
            TotalDeleted += r.Deleted;
        }
    }

    public FileAccumulator? Get(int fileId) => fileId < _files.Count ? _files[fileId] : null;

    /// <summary>All files with at least one eligible row, by stable file id.</summary>
    public IEnumerable<(int FileId, FileAccumulator File)> All()
    {
        for (int i = 0; i < _files.Count; i++)
            if (_files[i] is { } f) yield return (i, f);
    }
}
