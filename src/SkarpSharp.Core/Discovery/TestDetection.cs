using System.Text;
using System.Text.RegularExpressions;
using SkarpSharp.Model;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SkarpSharp.Discovery;

/// <summary>
/// Test-file detection: a file is test code when its nearest project is a test project (a package reference
/// to a common test framework, <c>IsTestProject</c>, the MSTest SDK, or a global using of a test framework namespace in the
/// project file or in any file of the project), or when the file itself has a using directive for a common test framework
/// namespace. Syntax only; no build.
/// </summary>
public static class TestDetection
{
    /// <summary>Namespaces of common test frameworks (a using of the namespace or a sub-namespace marks a test file).</summary>
    public static readonly IReadOnlyList<string> TestNamespaces = ["Xunit", "NUnit.Framework", "Microsoft.VisualStudio.TestTools.UnitTesting", "TUnit"];

    /// <summary>Package ids of common test frameworks and the test SDK (also matches ids starting with the id and a dot).</summary>
    public static readonly IReadOnlyList<string> TestPackages = ["Microsoft.NET.Test.Sdk", "xunit", "xunit.v3", "NUnit", "NUnit3TestAdapter", "NUnitLite", "MSTest", "TUnit"];

    /// <summary>First test framework namespace imported by a using directive (file or namespace level), and whether it is a global using.</summary>
    public static (string? Namespace, bool Global) FindTestUsing(CompilationUnitSyntax root)
    {
        foreach (var u in root.Usings)
            if (TestNamespace(u) is { } ns) return (ns, u.GlobalKeyword.RawKind != 0);
        foreach (var nsDecl in root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax).OfType<BaseNamespaceDeclarationSyntax>())
            foreach (var u in nsDecl.Usings)
                if (TestNamespace(u) is { } ns) return (ns, false);
        return (null, false);
    }

    static string? TestNamespace(UsingDirectiveSyntax u)
    {
        if (u.Name is null) return null;
        var name = u.Name.ToString();
        foreach (var ns in TestNamespaces)
            if (name == ns || name.StartsWith(ns + ".", StringComparison.Ordinal)) return ns;
        return null;
    }

    public static bool IsTestPackage(string id)
    {
        foreach (var p in TestPackages)
            if (id.Equals(p, StringComparison.OrdinalIgnoreCase) || id.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // MSBuild project files are read with a small tag scanner instead of System.Xml, which would add about 5 MB to the
    // Native AOT binary. It sees element names and attributes outside comments; that is all the detection needs.
    static readonly Regex XmlComment = new("<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    static readonly Regex Element = new(@"<(?<name>[A-Za-z_][\w.\-]*)(?<attrs>(?:\s+[\w.\-:]+\s*=\s*(?:""[^""]*""|'[^']*'))*)\s*/?>(?:(?<value>[^<]*)</\k<name>\s*>)?",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    static readonly Regex Attribute = new(@"(?<key>[\w.\-:]+)\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    static string? Attr(Match element, string key)
    {
        foreach (Match a in Attribute.Matches(element.Groups["attrs"].Value))
            if (a.Groups["key"].Value.Equals(key, StringComparison.OrdinalIgnoreCase)) return a.Groups["v"].Value.Trim();
        return null;
    }

    /// <summary>Why a project file marks a test project, or null. Unreadable files count as not test.</summary>
    public static string? TestProjectReason(string projectPath)
    {
        string text;
        try { text = File.ReadAllText(projectPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        return TestProjectReasonFromText(text);
    }

    public static string? TestProjectReasonFromText(string text)
    {
        text = XmlComment.Replace(text, "");
        foreach (Match e in Element.Matches(text))
        {
            switch (e.Groups["name"].Value)
            {
                case "Project" or "Sdk" when (Attr(e, e.Groups["name"].Value == "Sdk" ? "Name" : "Sdk") ?? "").Contains("MSTest.Sdk", StringComparison.OrdinalIgnoreCase):
                    return "uses MSTest.Sdk";
                case "IsTestProject" when e.Groups["value"].Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase):
                    return "IsTestProject";
                case "PackageReference":
                    var id = Attr(e, "Include") ?? Attr(e, "Update");
                    if (id is not null && IsTestPackage(id)) return $"references {id}";
                    break;
                case "Using":
                    var ns = Attr(e, "Include");
                    if (ns is not null && TestNamespaces.Any(t => ns == t || ns.StartsWith(t + ".", StringComparison.Ordinal))) return $"global using {ns}";
                    break;
            }
        }
        return null;
    }

    /// <summary>The nearest project file of a directory and whether it is a test project.</summary>
    public sealed record ProjectInfo(string FullPath, string? TestReason);

    /// <summary>
    /// Finds the nearest project file (*.csproj) for directories, walking up from the file's folder and stopping at the
    /// repository root (a folder containing .git) or the file-system root. Results are cached per directory. Not thread safe.
    /// </summary>
    public sealed class ProjectLocator
    {
        readonly Dictionary<string, ProjectInfo?> _cache = new(StringComparer.OrdinalIgnoreCase);

        public ProjectInfo? Find(string directory)
        {
            directory = Path.GetFullPath(directory);
            if (_cache.TryGetValue(directory, out var cached)) return cached;
            ProjectInfo? result = null;
            try
            {
                var projects = Directory.GetFiles(directory, "*.csproj");
                if (projects.Length > 0)
                {
                    Array.Sort(projects, StringComparer.Ordinal);
                    string? reason = null;
                    string chosen = projects[0];
                    foreach (var p in projects)
                        if (TestProjectReason(p) is { } r) { reason = r; chosen = p; break; }
                    result = new ProjectInfo(chosen, reason);
                }
                else if (!Directory.Exists(Path.Combine(directory, ".git")) && !File.Exists(Path.Combine(directory, ".git"))
                         && Path.GetDirectoryName(directory) is { } parent)
                    result = Find(parent);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _cache[directory] = result;
            return result;
        }
    }

    /// <summary>
    /// Decides which files to leave out. Pass 1 (before parsing): <c>--exclude</c> globs, then test projects (unless tests are
    /// included or an <c>--include</c> glob brings the file back). Pass 2 (after parsing, see <see cref="ExcludeParsed"/>):
    /// test usings, and global test usings that make the whole project a test project.
    /// </summary>
    public sealed class Classifier(string root, bool includeTests, IReadOnlyList<Glob> include, IReadOnlyList<Glob> exclude)
    {
        readonly ProjectLocator _projects = new();
        readonly Dictionary<string, ProjectInfo?> _byFile = new(StringComparer.Ordinal);
        public List<ExcludedFile> Excluded { get; } = [];

        string Rel(string full) => Path.GetRelativePath(root, full).Replace('\\', '/');

        /// <summary>Pass 1 for one file (paths relative to the root, '/' separators); true when the file is left out.</summary>
        public bool ExcludeBeforeParse(string path)
        {
            foreach (var g in exclude)
                if (g.Matches(path)) { Excluded.Add(new ExcludedFile { Path = path, Kind = "exclude", Reason = $"--exclude {g.Pattern}" }); return true; }
            var dir = Path.GetDirectoryName(Path.Combine(root, path)) ?? root;
            var project = _projects.Find(dir);
            _byFile[path] = project;
            if (includeTests) return false;
            if (project?.TestReason is { } reason && !Included(path))
            {
                Excluded.Add(new ExcludedFile { Path = path, Kind = "test", Reason = $"test project {Rel(project.FullPath)} ({reason})" });
                return true;
            }
            return false;
        }

        bool Included(string path) => include.Any(g => g.Matches(path));

        /// <summary>Pass 2: removes parsed files that are test code; returns the files kept, in the same order.</summary>
        public List<FileResult> ExcludeParsed(IReadOnlyList<FileResult> files)
        {
            // A global using of a test namespace in any file makes the whole project a test project.
            var globalTestProjects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
                if (f.TestUsingIsGlobal && _byFile.GetValueOrDefault(f.Path) is { } p)
                    globalTestProjects.TryAdd(p.FullPath, $"global using {f.TestUsing} in {f.Path}");
            var kept = new List<FileResult>(files.Count);
            foreach (var f in files)
            {
                string? reason = null;
                if (_byFile.GetValueOrDefault(f.Path) is { } p && globalTestProjects.TryGetValue(p.FullPath, out var why))
                    reason = $"test project {Rel(p.FullPath)} ({why})";
                else if (f.TestUsing is { } ns)
                    reason = $"uses {ns}";
                else if (_byFile.GetValueOrDefault(f.Path)?.TestReason is { } projectReason)
                    reason = projectReason;
                // Test code that is kept (--include-tests, --include) is marked, so clones never pair it with production code.
                f.TestCode = reason is not null;
                if (reason is not null && !includeTests && !Included(f.Path))
                    Excluded.Add(new ExcludedFile { Path = f.Path, Kind = "test", Reason = reason });
                else
                    kept.Add(f);
            }
            return kept;
        }

        public List<ExcludedFile> SortedExcluded() => [.. Excluded.OrderBy(e => e.Path, StringComparer.Ordinal)];
    }
}
