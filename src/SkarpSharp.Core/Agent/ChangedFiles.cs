using SkarpSharp.History;

namespace SkarpSharp.Agent;

/// <summary>
/// The C# files of a git work tree that differ from the last commit, staged or not, plus the
/// untracked ones, from one <c>git status --porcelain -z</c> at the work tree root (optional locks off, so the index is
/// not written). Git leaves ignored files out; deleted files are dropped, and a rename gives its new path.
/// </summary>
public static class ChangedFiles
{
    /// <summary>The changed C# files (absolute paths, sorted) of the work tree holding <paramref name="folder"/>; null when
    /// the folder is not in one.</summary>
    /// <exception cref="InvalidOperationException">git could not be run or failed.</exception>
    public static async Task<List<string>?> ReadAsync(string folder, IReadOnlyDictionary<string, string?>? environment, CancellationToken ct)
    {
        if (Paths.WorkTreeRoot(folder) is not { } tree) return null;
        var r = await GitProcess.RunTextAsync(tree, ["status", "--porcelain", "-z", "--untracked-files=all", "--", ":(icase)*.cs"], environment, ct).ConfigureAwait(false);
        if (!r.Succeeded) throw new InvalidOperationException(r.Started ? $"git status failed: {r.FirstErrorLine}" : "git could not be started");
        return Parse(r.StandardOutput, tree);
    }

    /// <summary>The paths of <c>git status --porcelain -z</c> output that still exist, absolute and sorted.</summary>
    public static List<string> Parse(string output, string tree)
    {
        var files = new List<string>();
        var entries = output.Split('\0');
        for (int i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            if (e.Length < 4) continue;
            char x = e[0], y = e[1];
            // A rename or copy is followed by its original path.
            if (x is 'R' or 'C') i++;
            if (x == 'D' || y == 'D') continue;
            var full = Path.GetFullPath(Path.Combine(tree, e[3..]));
            if (File.Exists(full)) files.Add(full);
        }
        files.Sort(StringComparer.Ordinal);
        return files;
    }
}
