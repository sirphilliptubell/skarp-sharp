using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using SkarpSharp.History;
using SkarpSharp.Model;

namespace SkarpSharp.Agent;

/// <summary>HEAD and the dirty flag of a directory, read once (two git calls started together).</summary>
public sealed record GitState(string? Commit, bool? Dirty)
{
    public static readonly GitState None = new(null, null);

    /// <summary>HEAD and whether a <c>.cs</c> file under <paramref name="directory"/> has uncommitted changes; <see cref="None"/>
    /// outside a git work tree (no git process is started then). <paramref name="environment"/>: extra variables for git
    /// (tests).</summary>
    public static async Task<GitState> ReadAsync(string directory, IReadOnlyDictionary<string, string?>? environment = null)
    {
        if (Paths.WorkTreeRoot(directory) is null) return None;
        var headTask = GitProcess.RunTextAsync(directory, ["rev-parse", "--verify", "-q", "HEAD^{commit}"], environment);
        var statusTask = GitProcess.RunTextAsync(directory, ["status", "--porcelain", "--untracked-files=all", "--", ":(icase)*.cs"], environment);
        var head = await headTask.ConfigureAwait(false);
        var status = await statusTask.ConfigureAwait(false);
        if (!head.Succeeded) return None;
        return new GitState(head.StandardOutput.Trim(), status.Succeeded ? status.StandardOutput.Trim().Length > 0 : null);
    }
}

/// <summary>Folder rules shared by the agent loop: the work tree root, the <c>.skarp</c> folder and file hashes.</summary>
public static class Paths
{
    public const string SkarpFolder = ".skarp";

    /// <summary>The root of the git work tree holding <paramref name="directory"/> (a folder with <c>.git</c>); null when not in one.</summary>
    public static string? WorkTreeRoot(string directory)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(directory));
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>Creates <c>&lt;root&gt;/.skarp/</c> with its own <c>.gitignore</c> (<c>*</c>) on first use; the user's
    /// <c>.gitignore</c> is never touched.</summary>
    public static string EnsureSkarpFolder(string root)
    {
        var folder = Path.Combine(root, SkarpFolder);
        Directory.CreateDirectory(folder);
        var ignore = Path.Combine(folder, ".gitignore");
        if (!File.Exists(ignore))
        {
            try { File.WriteAllText(ignore, "# Written by skarp: results of the last run, not for version control.\n*\n"); }
            catch (IOException) { } // another skarp process wrote it at the same time
        }
        return folder;
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>SHA-256 of a file's bytes, or null when it cannot be read (deleted, locked).</summary>
    public static string? FileSha256(string path)
    {
        try { return Sha256(File.ReadAllBytes(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Runs <paramref name="action"/> and retries a few times on a sharing violation (another process has the file open).</summary>
    public static void Retry(Action action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 8)
            {
                Thread.Sleep(15 * attempt);
            }
        }
    }

    /// <summary>Writes text to a temporary file next to <paramref name="path"/> and renames it over the target.</summary>
    public static void WriteAtomic(string path, string text)
    {
        var temp = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, text);
        try { Retry(() => File.Move(temp, path, overwrite: true)); }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }

    public static string UtcText(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static DateTimeOffset? ParseUtc(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
}

/// <summary>
/// The saved last run: every <c>analyze</c> writes its JSON documents to
/// <c>&lt;root&gt;/.skarp/last-run/</c> (<c>analysis.json</c>, <c>semantic[.tfm].json</c>, <c>run.json</c>). The documents are
/// written into a new folder that then replaces the old one, so a reader never sees half a run. A plain JSON folder: a
/// first step toward a content cache and store, not a replacement.
/// </summary>
public static class LastRun
{
    public const string Folder = "last-run";
    public const string HeaderFile = "run.json";
    public const string AnalysisFile = "analysis.json";
    public const string ChecksFolder = "checked";
    public const int Version = 1;

    public static string Directory(string root) => Path.Combine(root, Paths.SkarpFolder, Folder);

    /// <summary>The semantic document's file name: <c>semantic.json</c>, or <c>semantic.&lt;tfm&gt;.json</c> per framework.</summary>
    public static string SemanticFile(string? targetFramework) => targetFramework is null ? "semantic.json" : $"semantic.{targetFramework}.json";

    /// <summary>Saves the run. Per-check records (<c>.skarp/checked/</c>) are superseded by a full run and removed.
    /// <paramref name="solution"/>: the solution the run was asked to use (absolute), null when the tier chooses.</summary>
    public static void Save(string root, AnalysisResult result, string settingsFingerprint, string toolVersion, GitState git, DateTimeOffset now,
        string? solution = null)
    {
        root = Path.GetFullPath(root);
        var skarp = Paths.EnsureSkarpFolder(root);
        var target = Path.Combine(skarp, Folder);
        var temp = Path.Combine(skarp, $"{Folder}.{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}-{Guid.NewGuid().ToString("N")[..8]}.tmp");
        System.IO.Directory.CreateDirectory(temp);
        try
        {
            var header = new LastRunHeader
            {
                Version = Version, WrittenAt = Paths.UtcText(now), Commit = git.Commit, Dirty = git.Dirty,
                SettingsFingerprint = settingsFingerprint, ToolVersion = toolVersion, Root = root, SettingsFiles = result.Run.SettingsFiles,
                Solution = SolutionKey(root, solution),
            };
            using (var s = File.Create(Path.Combine(temp, AnalysisFile))) VariantView.Writing(result, () => JsonSerializer.Serialize(s, result, ResultJsonContext.Default.AnalysisResult));
            header.Documents.Add(AnalysisFile);
            foreach (var r in result.SemanticTier?.Results ?? [])
            {
                var name = SemanticFile(r.Run.TargetFramework);
                using (var s = File.Create(Path.Combine(temp, name))) JsonSerializer.Serialize(s, r, SemanticJsonContext.Default.SemanticResult);
                header.Documents.Add(name);
            }
            var paths = result.Files.Select(f => f.Path).ToArray();
            var hashes = new string?[paths.Length];
            Parallel.For(0, paths.Length, i => hashes[i] = Paths.FileSha256(Path.Combine(root, paths[i])));
            for (int i = 0; i < paths.Length; i++)
                if (hashes[i] is { } h) header.Files[paths[i]] = h;
            File.WriteAllText(Path.Combine(temp, HeaderFile), JsonSerializer.Serialize(header, AgentJsonContext.Default.LastRunHeader));

            // Swap the folders: the old one moves aside, the new one takes its name, the old one is deleted.
            var old = temp[..^".tmp".Length] + ".old";
            if (System.IO.Directory.Exists(target)) Paths.Retry(() => System.IO.Directory.Move(target, old));
            Paths.Retry(() => System.IO.Directory.Move(temp, target));
            TryDelete(old);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        TryDelete(Path.Combine(skarp, ChecksFolder));
        // Leftovers of runs that could not delete them (another process was reading): old folders now, temporary ones
        // only when abandoned (a concurrent run may still be writing a fresh one).
        foreach (var dir in System.IO.Directory.GetDirectories(skarp, Folder + ".*"))
            if (dir.EndsWith(".old", StringComparison.Ordinal)
                || (dir.EndsWith(".tmp", StringComparison.Ordinal) && DateTime.UtcNow - System.IO.Directory.GetLastWriteTimeUtc(dir) > TimeSpan.FromMinutes(10)))
                TryDelete(dir);
    }

    /// <summary>A solution as the header records it (relative to the root, '/' separators), or null.</summary>
    public static string? SolutionKey(string root, string? solution) =>
        solution is null ? null : Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(solution)).Replace('\\', '/');

    static void TryDelete(string dir)
    {
        try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The root whose <c>.skarp/</c> holds saved results, walking up from <paramref name="start"/> to the git work tree
    /// root (like <c>.skarp.json</c>); with <paramref name="baseline"/> a folder holding <c>.skarp-baseline.json</c> also counts.
    /// </summary>
    public static string? FindRoot(string start, bool baseline = false)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(start));
        while (dir is not null)
        {
            var skarp = Path.Combine(dir.FullName, Paths.SkarpFolder);
            if (File.Exists(Path.Combine(skarp, Folder, HeaderFile)) || System.IO.Directory.Exists(Path.Combine(skarp, ChecksFolder))) return dir.FullName;
            if (baseline && File.Exists(Path.Combine(dir.FullName, Baselines.Baseline.FileName))) return dir.FullName;
            if (System.IO.Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git"))) return null;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>The saved run of a root: header, analysis and semantic documents; null when there is none.</summary>
    public static SavedRun? Load(string root)
    {
        var dir = Directory(root);
        var headerPath = Path.Combine(dir, HeaderFile);
        if (!File.Exists(headerPath)) return null;
        var header = JsonSerializer.Deserialize(File.ReadAllText(headerPath), AgentJsonContext.Default.LastRunHeader)
            ?? throw new InvalidDataException($"{headerPath}: empty");
        if (header.Version != Version)
            throw new InvalidDataException($"{headerPath}: saved run version {header.Version} is not supported (this skarp reads {Version}); run skarp analyze again");
        AnalysisResult? analysis = null;
        var semantic = new List<SemanticResult>();
        foreach (var name in header.Documents)
        {
            var path = Path.Combine(dir, name);
            using var s = File.OpenRead(path);
            if (name == AnalysisFile)
            {
                analysis = JsonSerializer.Deserialize(s, ResultJsonContext.Default.AnalysisResult);
                // Back to the in-memory view (merged values in the records, base values aside).
                if (analysis is not null) VariantView.ToMerged(analysis);
            }
            else if (JsonSerializer.Deserialize(s, SemanticJsonContext.Default.SemanticResult) is { } r) semantic.Add(r);
        }
        return new SavedRun(header, analysis, semantic);
    }
}

/// <summary>A loaded saved run.</summary>
public sealed record SavedRun(LastRunHeader Header, AnalysisResult? Analysis, List<SemanticResult> Semantic);
