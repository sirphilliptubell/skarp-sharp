namespace SkarpSharp.Agent;

/// <summary>
/// The files as committed at git <c>HEAD</c>, for the <c>Stop</c> summary's starting point.
/// One <c>git cat-file --batch-command</c> per work tree, started on the first file asked for and stopped on dispose; a
/// file not in <c>HEAD</c>, outside git, or a git that fails gives null (the Stop then keeps the recorded starts).
/// </summary>
public sealed class HeadFiles(IReadOnlyDictionary<string, string?>? environment) : IDisposable
{
    readonly Dictionary<string, StagedIndex?> _readers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The bytes of <paramref name="fullPath"/> at <c>HEAD</c>, or null.</summary>
    public byte[]? Bytes(string fullPath)
    {
        if (Paths.WorkTreeRoot(Path.GetDirectoryName(fullPath)!) is not { } tree) return null;
        if (!_readers.TryGetValue(tree, out var reader))
        {
            try { reader = StagedIndex.Blobs(tree, environment); }
            catch (InvalidOperationException) { reader = null; }
            _readers[tree] = reader;
        }
        if (reader is null) return null;
        var relative = Path.GetRelativePath(tree, fullPath).Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal)) return null;
        try { return reader.Contents("HEAD:" + relative).Content; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _readers[tree] = null;
            reader.Dispose();
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var reader in _readers.Values) reader?.Dispose();
        _readers.Clear();
    }
}
