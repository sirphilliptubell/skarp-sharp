using SkarpSharp.Model;

namespace SkarpSharp.Clones;

/// <summary>
/// The token streams of a set of files concatenated into flat arrays, files in ordinal path order so
/// the result does not depend on discovery order. Each file starts a new run, so no window crosses a file boundary.
/// </summary>
public sealed class CloneCorpus
{
    public required string[] Paths { get; init; }
    public required CloneFile[] Files { get; init; }
    /// <summary>Global index of each file's first token.</summary>
    public required int[] FileStart { get; init; }
    public int Total { get; init; }
    public required ulong[] Values { get; init; }
    public required uint[] Raw { get; init; }
    /// <summary>Global index of the previous and next identifier with the same name in the same file; -1 when none.</summary>
    public required int[] Previous { get; init; }
    public required int[] Next { get; init; }
    public required int[] Lines { get; init; }
    public required int[] EndLines { get; init; }
    public required int[] FileOf { get; init; }
    /// <summary>File-local body ordinal (0 outside bodies).</summary>
    public required int[] Members { get; init; }
    public required byte[] Flags { get; init; }

    public static CloneCorpus Build(IEnumerable<(string Path, CloneFile File)> files)
    {
        var list = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        int total = 0;
        var starts = new int[list.Count];
        for (int i = 0; i < list.Count; i++) { starts[i] = total; total += list[i].File.Count; }
        var c = new CloneCorpus
        {
            Paths = [.. list.Select(f => f.Path)], Files = [.. list.Select(f => f.File)], FileStart = starts, Total = total,
            Values = new ulong[total], Raw = new uint[total], Previous = new int[total], Next = new int[total], Lines = new int[total],
            EndLines = new int[total], FileOf = new int[total], Members = new int[total], Flags = new byte[total],
        };
        Array.Fill(c.Next, -1);
        for (int i = 0; i < list.Count; i++)
        {
            var f = list[i].File;
            int g = starts[i];
            f.Values.CopyTo(c.Values, g);
            f.Raw.CopyTo(c.Raw, g);
            f.Lines.CopyTo(c.Lines, g);
            f.EndLines.CopyTo(c.EndLines, g);
            f.Members.CopyTo(c.Members, g);
            f.Flags.CopyTo(c.Flags, g);
            for (int k = 0; k < f.Count; k++)
            {
                c.FileOf[g + k] = i;
                int p = f.Previous[k];
                c.Previous[g + k] = p < 0 ? -1 : g + p;
                if (p >= 0) c.Next[g + p] = g + k;
            }
            if (f.Count > 0) c.Flags[g] |= TokenFlags.Break;
        }
        return c;
    }

    /// <summary>Baker's parameterized code of an identifier: the distance to the previous occurrence of its name inside
    /// the window starting at <paramref name="windowStart"/>, else 0.</summary>
    public static ulong IdentifierCode(int distance) => StableHash.Mix(0x5BD1E995UL + (ulong)distance * 0x100000001B3UL);

    /// <summary>The normalized value of token <paramref name="i"/> in a window (or match) starting at <paramref name="start"/>.</summary>
    public ulong Value(int i, int start)
    {
        if ((Flags[i] & TokenFlags.Ident) == 0) return Values[i];
        int p = Previous[i];
        return IdentifierCode(p >= start ? i - p : 0);
    }

    /// <summary>Equal normalized tokens at offsets of two matches starting at <paramref name="si"/> and <paramref name="sj"/>.</summary>
    public bool Equal(int i, int si, int j, int sj)
    {
        bool a = (Flags[i] & TokenFlags.Ident) != 0, b = (Flags[j] & TokenFlags.Ident) != 0;
        if (a != b) return false;
        if (!a) return Values[i] == Values[j];
        int pi = Previous[i], pj = Previous[j];
        return (pi >= si ? i - pi : 0) == (pj >= sj ? j - pj : 0);
    }

    /// <summary>Lines spanned by tokens <paramref name="first"/> to <paramref name="last"/>.</summary>
    public int SpanLines(int first, int last) => EndLines[last] - Lines[first] + 1;
}
