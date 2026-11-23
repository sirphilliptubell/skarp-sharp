using System.Diagnostics;
using ModelContextProtocol;
using SkarpSharp.Agent;
using SkarpSharp.Baselines;
using SkarpSharp.Findings;
using SkarpSharp.Ranking;

namespace SkarpSharp.Mcp;

/// <summary>The C# files a <c>check</c> call compared: the outcome, what each root compared with, and what was skipped.</summary>
internal sealed class FilePart
{
    public CheckOutcome Outcome { get; } = new();
    /// <summary>The files came from <c>git status</c> (none were given).</summary>
    public bool Changed { get; init; }
    /// <summary>The C# files looked at (checked or skipped as test code or excluded).</summary>
    public int Considered { get; set; }
    public int Checked { get; set; }
    /// <summary><c>baseline</c>, <c>commit</c> (the last commit) or <c>nothing</c> (no baseline, not in git).</summary>
    public SortedSet<string> Against { get; } = new(StringComparer.Ordinal);
    public List<SkippedInput> Skipped { get; init; } = [];
    /// <summary>The checked files by root and relative path (the semantic part without a baseline keeps only these).</summary>
    public HashSet<(string Root, string Path)> Files { get; } = [];
}

/// <summary>The semantic part of a <c>check</c> call: the run, and its lines (null when the tier did not run).</summary>
internal sealed record SemanticPart(string Solution, AnalysisRun Run, CheckOutcome? Outcome, bool Baseline, string? NotEvaluated);

/// <summary>
/// The work behind <c>check</c>. The C# files given, else the changed ones (<see cref="ChangedFiles"/>),
/// each compared with its root's baseline, else with the file at the last commit (<see cref="HeadFiles"/>), in memory
/// (<see cref="CheckRun.AgainstBaselineFile"/>, <see cref="CheckRun.AgainstEarlier"/>): nothing is written. A solution
/// among the files adds the semantic tier on it (<see cref="AnalysisRuns"/>, which saves and reuses the run).
/// </summary>
public sealed class CheckService(McpHost host)
{
    public const int MaxChanged = 2_000;
    readonly AnalysisRuns _runs = new(host);

    public async Task<string> RunAsync(CheckRequest q, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var (csharp, solution, skipped) = Inputs(q.Files);
        bool changed = csharp.Count == 0;
        var files = changed ? await ChangedAsync(q.Path, ct).ConfigureAwait(false) : csharp;
        var part = new FilePart { Changed = changed, Skipped = skipped };
        Compare(files, part, q.Notices);
        var semantic = solution is null ? null : await SemanticAsync(solution, part, q.Notices, ct).ConfigureAwait(false);
        var text = new CheckAnswer(host.Cwd, q.Path, part, semantic).Write();
        host.Write($"check {(changed ? "changed files under " + q.Path : csharp.Count + " files")}{(solution is null ? "" : " with " + solution)}: " +
            $"{part.Checked} checked, {part.Outcome.Lines.Count} lines{(semantic?.Run.Reused == true ? ", saved run reused" : "")} in {sw.ElapsedMilliseconds} ms");
        return text;
    }

    /// <summary>The given paths split into C# files, at most one solution, and skipped inputs with the reason.</summary>
    /// <exception cref="McpException">More than one solution.</exception>
    (List<string> CSharp, string? Solution, List<SkippedInput> Skipped) Inputs(IReadOnlyList<string> given)
    {
        var csharp = new List<string>();
        var skipped = new List<SkippedInput>();
        string? solution = null;
        foreach (var path in given)
        {
            var full = Path.GetFullPath(Path.Combine(host.Cwd, path));
            bool exists = File.Exists(full);
            if (IsSolution(full) && exists)
            {
                if (solution is not null) throw new McpException($"files holds more than one solution ({solution} and {full}); check takes one");
                solution = full;
            }
            else if (!exists) skipped.Add(new SkippedInput(path, "not found"));
            else if (!full.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) skipped.Add(new SkippedInput(path, "not C#"));
            else if (!csharp.Contains(full, StringComparer.OrdinalIgnoreCase)) csharp.Add(full);
        }
        return (csharp, solution, skipped);
    }

    static bool IsSolution(string path) => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    /// <summary>The changed C# files of the repository at <paramref name="path"/>, under it.</summary>
    /// <exception cref="McpException">The path is missing or not in git, git failed, or there are more than the cap.</exception>
    async Task<List<string>> ChangedAsync(string path, CancellationToken ct)
    {
        var full = Path.GetFullPath(Path.Combine(host.Cwd, path));
        bool isFile = File.Exists(full);
        if (!isFile && !Directory.Exists(full)) throw new McpException($"path not found: {full}");
        List<string>? changed;
        try { changed = await ChangedFiles.ReadAsync(isFile ? Path.GetDirectoryName(full)! : full, host.GitEnvironment, ct).ConfigureAwait(false); }
        catch (InvalidOperationException ex) { throw new McpException($"the changed files could not be listed: {ex.Message}"); }
        if (changed is null) throw new McpException($"{full} is not in a git repository; pass files");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var under = changed.FindAll(f => string.Equals(f, full, comparison) || f.StartsWith(Path.TrimEndingDirectorySeparator(full) + Path.DirectorySeparatorChar, comparison));
        if (under.Count > MaxChanged)
            throw new McpException($"check takes at most {MaxChanged:N0} changed C# files per call (found {under.Count:N0} under {full}); pass files or a narrower path");
        return under;
    }

    /// <summary>Each file against its root's baseline, else the last commit; skipped files listed with the reason.</summary>
    void Compare(List<string> files, FilePart part, bool notices)
    {
        var s = new CheckSettings
        {
            Save = false, MinSeverity = notices ? Severity.Notice : Severity.Warning, Home = "", Now = host.Now,
            Locations = host.SettingsLocations, GitEnvironment = host.GitEnvironment, JudgeHealth = true,
        };
        var roots = new Dictionary<string, CheckRun.RootContext>(StringComparer.OrdinalIgnoreCase);
        var baselines = new Dictionary<string, BaselineFile?>(StringComparer.OrdinalIgnoreCase);
        var style = new LineStyle(FileCheck.RootOfFolder(host.Cwd), false);
        using var heads = new HeadFiles(host.GitEnvironment);
        foreach (var full in files)
        {
            var root = FileCheck.RootOf(full);
            CheckRun.RootContext ctx;
            try { ctx = CheckRun.Context(root, s, roots); }
            catch (CheckUsageException ex) { throw new McpException(ex.Message); }
            part.Outcome.Enter(root, ctx.Options);
            var rel = FileCheck.Relative(root, full);
            var current = CheckRun.Current(full, root, rel, ctx, out _);
            if (current.ExcludedReason is { } why)
            {
                if (current.ExcludedKind != "walk") part.Considered++;
                part.Skipped.Add(new SkippedInput(style.Display(root, rel), current.ExcludedKind == "test" ? "test code" : $"excluded: {why}"));
                continue;
            }
            part.Considered++;
            part.Checked++;
            part.Files.Add((root, rel));
            part.Against.Add(One(new CheckedFile(full, root, current, ctx), s, part.Outcome, heads, Baseline(root, ctx, baselines)));
        }
    }

    /// <summary>A file being checked: its path, root, findings now and its root's settings.</summary>
    sealed record CheckedFile(string Full, string Root, FileCheckResult Current, CheckRun.RootContext Ctx);

    /// <summary>One file against <paramref name="baseline"/>, else the last commit; returns what it compared with.</summary>
    static string One(CheckedFile file, CheckSettings s, CheckOutcome outcome, HeadFiles heads, (BaselineFile File, string Path)? baseline)
    {
        if (baseline is { } b)
        {
            CheckRun.AgainstBaselineFile(b.File, b.Path, file.Current, file.Ctx, s, outcome, new SortedDictionary<string, string>(StringComparer.Ordinal));
            return "baseline";
        }
        bool inGit = Paths.WorkTreeRoot(Path.GetDirectoryName(file.Full)!) is not null;
        var (before, comparedWith) = CheckRun.Earlier(inGit ? heads.Bytes(file.Full) : null, inGit, file.Root, file.Current.Path, file.Ctx);
        // The health limits against the last commit (a file not in it is new); its notices are listed.
        var breach = comparedWith == "outside-git" ? null : Limit(file, before, comparedWith == "untracked");
        var limit = new LimitMark(outcome, breach);
        var (_, _, then, now) = CheckRun.AgainstEarlier(file.Current, before, comparedWith, new TrackedFile(), file.Ctx, breach is null ? s : s.WithNotices(), outcome);
        limit.Apply();
        // The heading shows the score also when it did not change.
        outcome.AddScore(file.Current.Path, then?.Score ?? now.Score, now.Score);
        return inGit ? "commit" : "nothing";
    }

    /// <summary>The file's breach of a health limit against the last commit's findings, or null.</summary>
    static Health.HealthBreach? Limit(CheckedFile file, Dictionary<string, TrackedFinding> before, bool isNew)
    {
        int src = file.Current.File?.Lines.Source ?? 0;
        var then = HealthLines.Of(file.Ctx.Model, before, src).Score;
        var now = HealthLines.Of(file.Ctx.Model, file.Current.Findings, src).Score;
        return Health.HealthLimit.Of(file.Current.Path, isNew ? null : then, now, isNew, file.Ctx.Options.HealthLimits);
    }

    /// <summary>The root's baseline file (read once per root), or null when it has none.</summary>
    static (BaselineFile File, string Path)? Baseline(string root, CheckRun.RootContext ctx, Dictionary<string, BaselineFile?> cache)
    {
        var path = Path.GetFullPath(ctx.Options.BaselineFile());
        if (!cache.TryGetValue(root, out var file))
        {
            file = !ctx.Options.NoBaseline && File.Exists(path) ? ReadBaseline(path) : null;
            cache[root] = file;
        }
        return file is null ? null : (file, path);
    }

    static BaselineFile ReadBaseline(string path)
    {
        try { return Baselines.Baseline.Read(path); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException) { throw new McpException($"the baseline {path} could not be read: {ex.Message}"); }
    }

    /// <summary>The semantic tier on the solution (saved run reused when fresh), and the lines it gives: new or worse than
    /// the semantic baseline anywhere, or without one the warnings in the checked files.</summary>
    async Task<SemanticPart> SemanticAsync(string solution, FilePart files, bool notices, CancellationToken ct)
    {
        var root = FileCheck.RootOfFolder(Path.GetDirectoryName(solution)!);
        var run = await _runs.SemanticAsync(root, solution, ct).ConfigureAwait(false);
        var name = new LineStyle(FileCheck.RootOfFolder(host.Cwd), false).Display(root, FileCheck.Relative(root, solution));
        if (run.Result.SemanticTier is not { Status: "ran" } tier)
            return new SemanticPart(name, run, null, false, run.Result.SemanticTier?.Reason ?? run.Result.Run.Semantic?.Reason ?? "the tier did not run");
        var r = Join.Highest(tier.Results);
        bool baseline = r?.Baseline is not null;
        var outcome = new CheckOutcome();
        outcome.Enter(root, run.Options);
        foreach (var f in r?.Findings ?? [])
        {
            if (!notices && f.Severity != "warning") continue;
            LineKind? kind = baseline
                ? f.BaselineState switch { "worsened" => LineKind.Worse, "new" => LineKind.New, _ => null }
                : files.Files.Contains((root, f.Path)) ? LineKind.Present : null;
            if (kind is not { } k) continue;
            AgentFormat.Restore(f);
            outcome.Add(AgentFormat.For(f, k, f.BaselineValue));
        }
        return new SemanticPart(name, run, outcome, baseline, null);
    }
}
