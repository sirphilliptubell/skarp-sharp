using System.Globalization;

namespace SkarpSharp.History;

/// <summary>
/// Environment probe: git found and its version, repository state (work tree or bare, root, HEAD), shallow and
/// partial clone. Problems become notices, never exceptions.
/// </summary>
public sealed class GitEnvironment
{
    /// <summary>Git floor: <c>rev-parse --is-shallow-repository</c> needs 2.15; everything else used here is older.</summary>
    public static readonly Version MinimumVersion = new(2, 15);

    public bool GitFound { get; private set; }
    public string? Version { get; private set; }
    public Version? VersionNumber { get; private set; }
    public bool IsRepository { get; private set; }
    public bool IsBare { get; private set; }
    /// <summary>Work tree top level (or the git directory of a bare repository), as git prints it ('/' separators).</summary>
    public string? RepositoryRoot { get; private set; }
    public string? GitDirectory { get; private set; }
    public string? Head { get; private set; }
    /// <summary>Committer time of HEAD (unix seconds); the default window ends here.</summary>
    public long HeadCommitterTime { get; private set; }
    public bool IsShallow { get; private set; }
    public bool IsPartialClone { get; private set; }
    /// <summary>Boundary commits of a shallow clone (they look like root commits adding every file).</summary>
    public HashSet<string> ShallowBoundaries { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Notices { get; } = [];

    /// <summary>History can be read: git present and new enough, a repository with at least one commit.</summary>
    public bool Usable => GitFound && VersionNumber is not null && VersionNumber >= MinimumVersion && IsRepository && Head is not null;

    public static async Task<GitEnvironment> ProbeAsync(string directory, IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var env = new GitEnvironment();
        var ver = await GitProcess.RunTextAsync(directory, ["--version"], environment, cancellationToken).ConfigureAwait(false);
        if (!ver.Started)
        {
            env.Notices.Add("git not found on PATH: history skipped");
            return env;
        }
        env.GitFound = true;
        env.Version = ver.StandardOutput.Trim().Replace("git version ", "", StringComparison.Ordinal);
        env.VersionNumber = ParseVersion(env.Version);
        if (env.VersionNumber is null || env.VersionNumber < MinimumVersion)
        {
            env.Notices.Add($"git {env.Version} found; git {MinimumVersion} or later is needed for history: history skipped");
            return env;
        }

        var rp = await GitProcess.RunTextAsync(directory,
            ["rev-parse", "--is-bare-repository", "--is-inside-work-tree", "--is-shallow-repository", "--absolute-git-dir"],
            environment, cancellationToken).ConfigureAwait(false);
        if (!rp.Succeeded)
        {
            var line = rp.FirstErrorLine;
            env.Notices.Add(line.Contains("dubious ownership", StringComparison.OrdinalIgnoreCase)
                ? $"git refuses this repository (safe.directory): {line}; history skipped"
                : "not a git repository: history skipped");
            return env;
        }
        var lines = rp.StandardOutput.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 4)
        {
            env.Notices.Add("unexpected git rev-parse output: history skipped");
            return env;
        }
        env.IsRepository = true;
        env.IsBare = lines[0] == "true";
        bool inWorkTree = lines[1] == "true";
        env.IsShallow = lines[2] == "true";
        env.GitDirectory = lines[3];
        if (inWorkTree)
        {
            var top = await GitProcess.RunTextAsync(directory, ["rev-parse", "--show-toplevel"], environment, cancellationToken).ConfigureAwait(false);
            env.RepositoryRoot = top.Succeeded ? top.StandardOutput.Trim() : null;
        }
        else if (env.IsBare)
            env.RepositoryRoot = env.GitDirectory;
        if (env.RepositoryRoot is null)
        {
            env.IsRepository = false;
            env.Notices.Add("inside a .git directory but not a work tree: history skipped");
            return env;
        }

        var head = await GitProcess.RunTextAsync(directory, ["log", "-1", "--no-show-signature", "--format=%H%x1f%ct", "HEAD"], environment, cancellationToken).ConfigureAwait(false);
        var parts = head.Succeeded ? head.StandardOutput.Trim().Split('\x1f') : [];
        if (parts.Length == 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ct))
        {
            env.Head = parts[0];
            env.HeadCommitterTime = ct;
        }
        else
        {
            env.Notices.Add("repository has no commits: history skipped");
            return env;
        }

        var promisor = await GitProcess.RunTextAsync(directory, ["config", "--get-regexp", @"^(extensions\.partialclone|remote\..*\.promisor)$"],
            environment, cancellationToken).ConfigureAwait(false);
        env.IsPartialClone = promisor.Succeeded && promisor.StandardOutput.Trim().Length > 0;
        if (env.IsPartialClone)
            env.Notices.Add("partial clone: line counts fetch missing file contents on demand, which can be very slow; " +
                "run 'git backfill' (git 2.49 or later) or use a full clone");

        if (env.IsShallow)
        {
            env.Notices.Add("shallow clone: history before the shallow boundary is missing and boundary commits are dropped; " +
                "run 'git fetch --unshallow' for full history");
            var shallowFile = Path.Combine(env.GitDirectory, "shallow");
            try
            {
                if (File.Exists(shallowFile))
                    foreach (var l in File.ReadAllLines(shallowFile))
                        if (l.Trim().Length > 0) env.ShallowBoundaries.Add(l.Trim());
            }
            catch (IOException) { }
        }
        return env;
    }

    /// <summary>"2.56.1.windows.1" gives 2.56.1; "2.39.5 (Apple Git-154)" gives 2.39.5.</summary>
    internal static Version? ParseVersion(string text)
    {
        var nums = new List<int>();
        foreach (var piece in text.Split('.', ' '))
        {
            if (!int.TryParse(piece, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) break;
            nums.Add(n);
            if (nums.Count == 3) break;
        }
        return nums.Count switch
        {
            >= 3 => new Version(nums[0], nums[1], nums[2]),
            2 => new Version(nums[0], nums[1]),
            _ => null,
        };
    }
}
