namespace SkarpSharp.Semantic;

/// <summary>A solution file and its project set (absolute project paths, sorted), or the error of listing it.</summary>
public sealed record SolutionListing(string Path, DateTime ModifiedUtc, IReadOnlyList<string>? Projects, string? Error);

/// <summary>The solution the semantic tier uses, or why it uses none (with a hint).</summary>
public sealed record SolutionDecision(string? Chosen, string Reason, string? Hint = null, IReadOnlyList<string>? Notes = null);

/// <summary>
/// Solution choice: find every <c>.sln</c> and <c>.slnx</c> under the analyzed directory;
/// solutions with the same project set count as one (prefer <c>.slnx</c>, else the most recently modified); otherwise a
/// solution whose projects include all others' projects; otherwise the one at the analyzed directory's root; otherwise
/// none, with a note naming the candidates and how to choose (<c>--solution</c>). Projects are listed by the SDK
/// (<c>dotnet sln list</c>), so solution formats are never parsed here.
/// </summary>
public static class SolutionChoice
{
    static readonly HashSet<string> Pruned = new(StringComparer.OrdinalIgnoreCase) { ".git", "bin", "obj", "node_modules", ".vs", ".idea", "artifacts" };

    static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Solution files under <paramref name="root"/> (build output and tool folders skipped), sorted.</summary>
    public static List<string> Find(string root)
    {
        var result = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var file in Directory.EnumerateFiles(dir, "*", options))
                if (file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                    result.Add(file);
            foreach (var sub in Directory.EnumerateDirectories(dir, "*", options))
                if (!Pruned.Contains(Path.GetFileName(sub))) stack.Push(sub);
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>Lists one solution's projects with <c>dotnet sln &lt;file&gt; list</c> (run in the solution's folder).</summary>
    public static async Task<SolutionListing> ListAsync(string solution, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(solution)!;
        var modified = File.GetLastWriteTimeUtc(solution);
        var run = await DotnetRunner.RunAsync(["sln", solution, "list"], dir, ct).ConfigureAwait(false);
        if (!run.Succeeded) return new SolutionListing(solution, modified, null, run.ErrorSummary());
        return new SolutionListing(solution, modified, ParseList(run.StandardOutput, dir), null);
    }

    /// <summary>The project paths of <c>dotnet sln list</c> output (the lines after the dashes), absolute and sorted.</summary>
    public static IReadOnlyList<string> ParseList(string output, string solutionDirectory)
    {
        var lines = output.Split('\n').Select(l => l.Trim()).ToList();
        int dash = lines.FindIndex(l => l.StartsWith("---", StringComparison.Ordinal));
        return [.. lines.Skip(dash + 1).Where(l => l.Length > 0 && l.EndsWith("proj", StringComparison.OrdinalIgnoreCase))
            .Select(l => Path.GetFullPath(Path.Combine(solutionDirectory, l.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar))))
            .Order(PathComparer)];
    }

    /// <summary>Applies the choice rules to listed solutions (pure: no file or process access).</summary>
    public static SolutionDecision Choose(string root, IReadOnlyList<SolutionListing> listings)
    {
        root = Path.GetFullPath(root);
        string Rel(string p) => Path.GetRelativePath(root, p).Replace('\\', '/');
        if (listings.Count == 0)
            return new SolutionDecision(null, "no .sln or .slnx file found", "the semantic tier needs a solution; pass one with --solution <file> or use --no-semantic");
        var failed = listings.Where(l => l.Projects is null).ToList();
        var notes = failed.Select(f => $"{Rel(f.Path)} could not be listed: {f.Error}").ToList();
        var ok = listings.Where(l => l.Projects is not null).ToList();
        if (ok.Count == 0)
            return new SolutionDecision(null, $"no solution could be listed ({string.Join("; ", failed.Select(f => Rel(f.Path) + ": " + f.Error))})",
                HintFor(failed[0].Error ?? ""), notes);
        var groups = new List<List<SolutionListing>>();
        foreach (var l in ok)
        {
            var g = groups.FirstOrDefault(g => g[0].Projects!.SequenceEqual(l.Projects!, PathComparer));
            if (g is null) groups.Add([l]); else g.Add(l);
        }
        var reps = groups.Select(Representative).ToList();
        string Why(List<SolutionListing> g, SolutionListing pick) => g.Count == 1 ? "" :
            pick.Path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ? $"; {g.Count} equivalent solutions, .slnx preferred"
            : $"; {g.Count} equivalent solutions, most recently modified";
        if (groups.Count == 1)
            return new SolutionDecision(reps[0].Path, (ok.Count == 1 ? "the only solution" : "the only project set") + Why(groups[0], reps[0]), null, notes);
        for (int i = 0; i < reps.Count; i++)
        {
            var set = new HashSet<string>(reps[i].Projects!, PathComparer);
            if (reps.Where((_, j) => j != i).All(o => o.Projects!.All(set.Contains)))
                return new SolutionDecision(reps[i].Path, $"contains the projects of the other {reps.Count - 1} solution{(reps.Count == 2 ? "" : "s")}" + Why(groups[i], reps[i]), null, notes);
        }
        var atRoot = reps.Where(r => PathComparer.Equals(Path.GetDirectoryName(r.Path), root)).ToList();
        if (atRoot.Count == 1)
            return new SolutionDecision(atRoot[0].Path, "the solution at the analyzed directory's root" + Why(groups[reps.IndexOf(atRoot[0])], atRoot[0]), null, notes);
        var names = string.Join(", ", groups.Select(g => string.Join(" = ", g.Select(x => Rel(x.Path))) + $" [{g[0].Projects!.Count} projects]"));
        return new SolutionDecision(null, $"{groups.Count} different solutions ({names})",
            "choose one with --solution <file> (or semantic.solution in .skarp.json)", notes);
    }

    static SolutionListing Representative(List<SolutionListing> group) =>
        group.Where(x => x.Path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.ModifiedUtc).FirstOrDefault()
        ?? group.OrderByDescending(x => x.ModifiedUtc).First();

    /// <summary>A hint for a failed <c>dotnet</c> call: a missing pinned SDK, no SDK at all, or the message itself.</summary>
    public static string HintFor(string error)
    {
        if (error.Contains("Requested SDK version", StringComparison.OrdinalIgnoreCase) || error.Contains("compatible .NET SDK was not found", StringComparison.OrdinalIgnoreCase))
            return "the .NET SDK version pinned in global.json is not installed; `dotnet --list-sdks` lists the installed ones";
        if (error.StartsWith("cannot start", StringComparison.Ordinal))
            return "the semantic tier needs the .NET SDK (`dotnet`) on the path; --no-semantic skips it";
        return "--no-semantic skips the semantic tier";
    }
}
