using System.Collections.Concurrent;
using SkarpSharp.Discovery;

namespace SkarpSharp.Conditionals;

/// <summary>
/// The names mentioned in the repository's project files (<c>.csproj</c>, <c>.props</c>, <c>.targets</c>).
/// A text scan, no MSBuild: every identifier-shaped word of those files counts as a mention. The files are those under
/// the root (the walk prunes the same folders as the .cs walk) plus <c>.props</c> and <c>.targets</c> files in the folders
/// above it up to the git work tree root, where <c>Directory.Build.props</c> usually lives. Read once per root and
/// process (a check reads it once).
/// </summary>
public static class ProjectSymbols
{
    static readonly ConcurrentDictionary<string, IReadOnlySet<string>> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> For(string root) => Cache.GetOrAdd(Path.GetFullPath(root), Scan);

    static IReadOnlySet<string> Scan(string root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rel in FileDiscovery.Walk(root, "*", FileDiscovery.IsPrunedDirectoryName))
            if (IsProjectFile(rel)) Read(Path.Combine(root, rel), names);
        var top = WorkTreeRoot(root);
        var dir = Directory.GetParent(root);
        while (top is not null && dir is not null && dir.FullName.Length >= top.Length)
        {
            foreach (var f in Directory.EnumerateFiles(dir.FullName))
                if (f.EndsWith(".props", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)) Read(f, names);
            dir = dir.Parent;
        }
        return names;
    }

    /// <summary>The folder holding <c>.git</c> at or above <paramref name="directory"/>, or null.</summary>
    static string? WorkTreeRoot(string directory)
    {
        for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git"))) return dir.FullName;
        return null;
    }

    static bool IsProjectFile(string path) =>
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase);

    static void Read(string path, HashSet<string> names)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        AddWords(text, names);
    }

    /// <summary>Adds every identifier-shaped word (a letter or underscore, then letters, digits and underscores).</summary>
    public static void AddWords(string text, HashSet<string> names)
    {
        int i = 0;
        while (i < text.Length)
        {
            if (!(char.IsLetter(text[i]) || text[i] == '_')) { i++; continue; }
            int start = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
            names.Add(text[start..i]);
        }
    }
}
