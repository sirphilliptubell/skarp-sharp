using System.Text;

namespace SkarpSharp.History;

/// <summary>
/// Interns repository paths. Paths arrive as raw bytes from git (<c>-z</c>, never quoted) and stay bytes until the
/// first time a path is seen; then it gets a dense id and is decoded once (UTF-8). Lookups by byte span allocate nothing.
/// </summary>
public sealed class PathTable
{
    readonly Dictionary<byte[], int> _ids = new(ByteArrayComparer.Instance);
    readonly Dictionary<byte[], int>.AlternateLookup<ReadOnlySpan<byte>> _lookup;
    readonly List<string> _paths = [];

    public PathTable() => _lookup = _ids.GetAlternateLookup<ReadOnlySpan<byte>>();

    public int Count => _paths.Count;

    public int Intern(ReadOnlySpan<byte> path)
    {
        if (_lookup.TryGetValue(path, out var id)) return id;
        id = _paths.Count;
        _lookup[path] = id; // the comparer copies the span into a new key array
        _paths.Add(Encoding.UTF8.GetString(path));
        return id;
    }

    public int Intern(string path) => Intern(Encoding.UTF8.GetBytes(path));

    public bool TryGet(string path, out int id) => _lookup.TryGetValue(Encoding.UTF8.GetBytes(path), out id);

    public string this[int id] => _paths[id];

    sealed class ByteArrayComparer : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] obj) => GetHashCode((ReadOnlySpan<byte>)obj);
        public bool Equals(ReadOnlySpan<byte> alternate, byte[] other) => alternate.SequenceEqual(other);
        public int GetHashCode(ReadOnlySpan<byte> alternate)
        {
            var h = new HashCode();
            h.AddBytes(alternate);
            return h.ToHashCode();
        }
        public byte[] Create(ReadOnlySpan<byte> alternate) => alternate.ToArray();
    }
}
