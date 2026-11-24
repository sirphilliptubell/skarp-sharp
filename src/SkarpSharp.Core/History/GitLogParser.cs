using System.Globalization;
using System.Text;

namespace SkarpSharp.History;

/// <summary>One numstat entry of a commit. Paths are ids in the run's <see cref="PathTable"/>.</summary>
/// <param name="OldPathId">Rename source, or -1 when the row is not a rename.</param>
public readonly record struct RawFileRow(int Added, int Deleted, bool Binary, int PathId, int OldPathId)
{
    public bool IsRename => OldPathId >= 0;
}

/// <summary>
/// One commit as git reported it, before any policy. The reader reuses one instance per run: consumers must not keep
/// it (or its row list) beyond <see cref="IHistoryConsumer.OnCommit"/>.
/// </summary>
public sealed class RawCommit
{
    public string Hash { get; set; } = "";
    public int ParentCount { get; set; }
    public string AuthorName { get; set; } = "";
    public string AuthorEmail { get; set; } = "";
    /// <summary>Unix seconds.</summary>
    public long AuthorTime { get; set; }
    /// <summary>Unix seconds.</summary>
    public long CommitterTime { get; set; }
    public List<RawFileRow> Files { get; } = [];
}

/// <summary>
/// Receives raw history rows in stream order (newest first), one commit at a time. The preprocessing and the
/// per-file accumulators are one consumer; a result-store writer is meant to be another.
/// </summary>
public interface IHistoryConsumer
{
    void OnCommit(RawCommit commit);
    void OnComplete();
}

/// <summary>Raw reader counters (before any policy), comparable with a bare <c>git log --numstat</c>.</summary>
public sealed class ReaderTotals
{
    public int Commits { get; set; }
    public long Rows { get; set; }
    public long Added { get; set; }
    public long Deleted { get; set; }
    public long Renames { get; set; }
    public long BinaryRows { get; set; }
    /// <summary>Tokens that did not match the grammar (skipped). Always 0 on a healthy stream.</summary>
    public long Malformed { get; set; }
}

/// <summary>
/// Streaming parser for <c>git log -z --numstat --format=<see cref="Format"/></c>, fed raw bytes in chunks of any size.
/// Grammar (observed on git 2.56.1): every token ends with NUL. A header token starts with 0x01
/// and holds fields separated by 0x1F. The first entry token after a header starts with one LF. An entry is
/// <c>added TAB deleted TAB path</c>; a rename has an empty path and is followed by two tokens, old path then new path.
/// Binary files show <c>-</c> for both counts and count 0/0. Only the current partial token is buffered.
/// </summary>
public sealed class GitLogParser
{
    /// <summary>Header: marker, hash, parents, author name and email (mailmap applied), author time, committer time.</summary>
    public const string Format = "%x01%H%x1f%P%x1f%aN%x1f%aE%x1f%at%x1f%ct";

    enum State { Entries, RenameOld, RenameNew }

    readonly PathTable _paths;
    readonly IHistoryConsumer _consumer;
    readonly RawCommit _commit = new();
    byte[] _partial = new byte[1024];
    int _partialLength;
    bool _inCommit;
    bool _firstEntry;
    State _state;
    int _pendingAdded, _pendingDeleted, _pendingOld;
    bool _pendingBinary;

    public GitLogParser(PathTable paths, IHistoryConsumer consumer)
    {
        _paths = paths;
        _consumer = consumer;
    }

    public ReaderTotals Totals { get; } = new();

    public void Feed(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            int nul = data.IndexOf((byte)0);
            if (nul < 0)
            {
                AppendPartial(data);
                return;
            }
            if (_partialLength == 0)
                Token(data[..nul]);
            else
            {
                AppendPartial(data[..nul]);
                Token(_partial.AsSpan(0, _partialLength));
                _partialLength = 0;
            }
            data = data[(nul + 1)..];
        }
    }

    /// <summary>End of stream: flushes the last commit and notifies the consumer.</summary>
    public void Complete()
    {
        if (_partialLength > 0)
        {
            // A trailing token without NUL can only be a header of an empty last commit or stray line feeds.
            var rest = _partial.AsSpan(0, _partialLength);
            if (rest.Length > 0 && rest[0] == 0x01) Token(rest);
            else if (rest.TrimStart((byte)'\n').Length > 0) Totals.Malformed++;
            _partialLength = 0;
        }
        if (_state != State.Entries) Totals.Malformed++;
        FlushCommit();
        _consumer.OnComplete();
    }

    void AppendPartial(ReadOnlySpan<byte> data)
    {
        if (_partialLength + data.Length > _partial.Length)
            Array.Resize(ref _partial, Math.Max(_partial.Length * 2, _partialLength + data.Length));
        data.CopyTo(_partial.AsSpan(_partialLength));
        _partialLength += data.Length;
    }

    void Token(ReadOnlySpan<byte> token)
    {
        switch (_state)
        {
            case State.RenameOld:
                _pendingOld = _paths.Intern(token);
                _state = State.RenameNew;
                return;
            case State.RenameNew:
                AddRow(_pendingAdded, _pendingDeleted, _pendingBinary, _paths.Intern(token), _pendingOld);
                Totals.Renames++;
                _state = State.Entries;
                return;
        }

        // The diff part of a commit starts with one LF. When a commit has no rows left (for example all of them
        // whitespace-only under -w) git still prints that LF, directly followed by the next header.
        if (_firstEntry && !token.IsEmpty && token[0] == (byte)'\n')
            token = token[1..];
        if (!token.IsEmpty && token[0] == 0x01)
        {
            FlushCommit();
            Header(token[1..]);
            return;
        }
        _firstEntry = false;
        if (token.IsEmpty) return;
        if (!_inCommit) { Totals.Malformed++; return; }

        int tab1 = token.IndexOf((byte)'\t');
        if (tab1 < 0) { Totals.Malformed++; return; }
        var rest = token[(tab1 + 1)..];
        int tab2 = rest.IndexOf((byte)'\t');
        if (tab2 < 0) { Totals.Malformed++; return; }
        var addedText = token[..tab1];
        var deletedText = rest[..tab2];
        var path = rest[(tab2 + 1)..];

        bool binary = addedText.SequenceEqual("-"u8) && deletedText.SequenceEqual("-"u8);
        int added = 0, deleted = 0;
        if (!binary && (!int.TryParse(addedText, NumberStyles.None, CultureInfo.InvariantCulture, out added) || !int.TryParse(deletedText, NumberStyles.None, CultureInfo.InvariantCulture, out deleted)))
        {
            Totals.Malformed++;
            return;
        }
        if (path.IsEmpty)
        {
            _pendingAdded = added;
            _pendingDeleted = deleted;
            _pendingBinary = binary;
            _state = State.RenameOld;
            return;
        }
        AddRow(added, deleted, binary, _paths.Intern(path), -1);
    }

    void AddRow(int added, int deleted, bool binary, int pathId, int oldPathId)
    {
        _commit.Files.Add(new RawFileRow(added, deleted, binary, pathId, oldPathId));
        Totals.Rows++;
        Totals.Added += added;
        Totals.Deleted += deleted;
        if (binary) Totals.BinaryRows++;
    }

    void Header(ReadOnlySpan<byte> h)
    {
        _inCommit = true;
        _firstEntry = true;
        _commit.Files.Clear();
        Span<Range> f = stackalloc Range[6];
        int n = 0, start = 0;
        for (int i = 0; i <= h.Length && n < 6; i++)
        {
            if (i < h.Length && h[i] != 0x1f) continue;
            f[n++] = new Range(start, i);
            start = i + 1;
        }
        if (n != 6 || start <= h.Length)
        {
            Totals.Malformed++;
            _commit.Hash = Encoding.UTF8.GetString(h[f[0]]);
            _commit.ParentCount = 0;
            _commit.AuthorName = _commit.AuthorEmail = "";
            _commit.AuthorTime = _commit.CommitterTime = 0;
            return;
        }
        _commit.Hash = Encoding.ASCII.GetString(h[f[0]]);
        var parents = h[f[1]];
        _commit.ParentCount = parents.IsEmpty ? 0 : parents.Count((byte)' ') + 1;
        _commit.AuthorName = Encoding.UTF8.GetString(h[f[2]]);
        _commit.AuthorEmail = Encoding.UTF8.GetString(h[f[3]]);
        _commit.AuthorTime = long.TryParse(h[f[4]], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var at) ? at : 0;
        _commit.CommitterTime = long.TryParse(h[f[5]], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ct) ? ct : 0;
    }

    void FlushCommit()
    {
        if (!_inCommit) return;
        Totals.Commits++;
        _consumer.OnCommit(_commit);
        _inCommit = false;
    }
}
