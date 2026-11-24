using System.IO.Enumeration;

namespace SkarpSharp.Discovery;

/// <summary>
/// File-system walk for *.cs files with directory pruning. Only the walk is used for now; the git index as file source arrives with the git wrapper.
/// </summary>
public static class FileDiscovery
{
    static readonly HashSet<string> PrunedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", "node_modules", "packages", "artifacts",
    };

    /// <summary>True when a directory name is pruned by the walk (build output, tool folders); history uses the same rule.</summary>
    public static bool IsPrunedDirectoryName(string name) => PrunedDirectories.Contains(name);

    /// <summary>Returns paths relative to <paramref name="root"/> with '/' separators, sorted ordinally.</summary>
    public static List<string> Find(string root) => Find(root, null);

    /// <summary>The settings file name (<c>.skarp.json</c>; Discovery does not depend on Configuration).</summary>
    const string SettingsFileName = ".skarp.json";

    /// <summary>
    /// The .cs files as <see cref="Find(string)"/>; when <paramref name="settingsFiles"/> is given, the
    /// same walk also adds every <c>.skarp.json</c> below <paramref name="root"/> (not the root's own), relative, sorted.
    /// </summary>
    public static List<string> Find(string root, List<string>? settingsFiles)
    {
        if (settingsFiles is null)
        {
            var only = Walk(root, "*.cs", PrunedDirectories.Contains);
            only.Sort(StringComparer.Ordinal);
            return only;
        }
        var result = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        bool ignoreCase = !OperatingSystem.IsLinux();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            var files = new FileSystemEnumerable<string>(dir, (ref FileSystemEntry e) => e.ToFullPath(), options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory
                    && (FileSystemName.MatchesSimpleExpression("*.cs", e.FileName, ignoreCase) || e.FileName.SequenceEqual(SettingsFileName)),
            };
            foreach (var file in files)
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (!IsSettingsFile(rel)) result.Add(rel);
                else if (rel.Length > SettingsFileName.Length) settingsFiles.Add(rel); // the root's own file is not nested
            }
            foreach (var sub in Directory.EnumerateDirectories(dir, "*", options))
                if (!PrunedDirectories.Contains(Path.GetFileName(sub))) stack.Push(sub);
        }
        result.Sort(StringComparer.Ordinal);
        settingsFiles.Sort(StringComparer.Ordinal);
        return result;
    }

    static bool IsSettingsFile(string rel) =>
        rel.EndsWith(SettingsFileName, StringComparison.Ordinal) && (rel.Length == SettingsFileName.Length || rel[^(SettingsFileName.Length + 1)] == '/');

    /// <summary>The files matching <paramref name="pattern"/> under <paramref name="root"/>, relative with '/' separators,
    /// in walk order; folders whose name <paramref name="pruned"/> accepts are not entered (also used for the coverage
    /// report globs).</summary>
    public static List<string> Walk(string root, string pattern, Func<string, bool> pruned)
    {
        var result = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var file in Directory.EnumerateFiles(dir, pattern, options))
                result.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            foreach (var sub in Directory.EnumerateDirectories(dir, "*", options))
                if (!pruned(Path.GetFileName(sub))) stack.Push(sub);
        }
        return result;
    }
}

/// <summary>Roslyn's generated-code heuristics: file name and header comment.</summary>
public static class GeneratedCode
{
    public static bool IsGeneratedFileName(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith("TemporaryGeneratedFile_", StringComparison.OrdinalIgnoreCase)) return true;
        var stem = Path.GetFileNameWithoutExtension(name);
        return stem.EndsWith(".designer", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith(".generated", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith(".g", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith(".g.i", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A leading comment (before the first token) containing &lt;autogenerated or &lt;auto-generated (case-sensitive).</summary>
    public static bool HasGeneratedHeader(Microsoft.CodeAnalysis.SyntaxTree tree)
    {
        var first = tree.GetRoot().GetFirstToken(includeZeroWidth: true);
        foreach (var trivia in first.LeadingTrivia)
        {
            var kind = (Microsoft.CodeAnalysis.CSharp.SyntaxKind)trivia.RawKind;
            if (kind is Microsoft.CodeAnalysis.CSharp.SyntaxKind.SingleLineCommentTrivia or Microsoft.CodeAnalysis.CSharp.SyntaxKind.MultiLineCommentTrivia)
            {
                var s = trivia.ToString();
                if (s.Contains("<autogenerated", StringComparison.Ordinal) || s.Contains("<auto-generated", StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }
}
