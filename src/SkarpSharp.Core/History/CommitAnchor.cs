using System.Globalization;

namespace SkarpSharp.History;

/// <summary>A commit and the number of commits reachable from it that change C# files (recorded in the baseline).</summary>
public sealed record CommitAnchor(string Commit, int CSharpCommits)
{
    /// <summary>The anchor recorded in a baseline header, or null when it records none or the values are not usable
    /// (the commit must be a full hexadecimal object id, so it can never be read by git as an option).</summary>
    public static CommitAnchor? FromRecorded(string? commit, int? csharpCommits) =>
        commit is { Length: 40 or 64 } c && c.All(char.IsAsciiHexDigit) && csharpCommits is int n && n >= 0 ? new CommitAnchor(c, n) : null;

    /// <summary>
    /// At baseline time: HEAD (full hash) and its C#-filtered whole-history commit count
    /// (<c>git rev-list --count --full-history HEAD -- :(top,icase)*.cs</c>; slow on large repositories, once). Null when
    /// the directory is not in a git repository with commits, the clone is shallow (the count would be partial) or git fails.
    /// </summary>
    public static async Task<CommitAnchor?> RecordAsync(string directory, IReadOnlyDictionary<string, string?>? env = null,
        CancellationToken ct = default)
    {
        var head = await GitProcess.RunTextAsync(directory, ["rev-parse", "--verify", "-q", "HEAD^{commit}"], env, ct).ConfigureAwait(false);
        if (!head.Succeeded) return null;
        var hash = head.StandardOutput.Trim();
        var shallow = await GitProcess.RunTextAsync(directory, ["rev-parse", "--is-shallow-repository"], env, ct).ConfigureAwait(false);
        if (!shallow.Succeeded || shallow.StandardOutput.Trim() != "false") return null;
        var n = await CountAsync(directory, hash, env, ct).ConfigureAwait(false);
        return n is int c ? new CommitAnchor(hash, c) : null;
    }

    /// <summary>
    /// On later runs: the C# commit total at HEAD as the recorded count plus the C# commits since the anchor
    /// (<c>anchor..HEAD</c>, fast). Returns the total, or null and the reason it is not available.
    /// </summary>
    public async Task<(int? Total, string? Note)> TotalAtHeadAsync(string directory, IReadOnlyDictionary<string, string?>? env = null,
        CancellationToken ct = default)
    {
        var shortHash = Commit[..Math.Min(12, Commit.Length)];
        // Both git calls run at once; the count is used only when the anchor is an ancestor of HEAD.
        var sinceTask = CountAsync(directory, Commit + "..HEAD", env, ct);
        var ancestor = await GitProcess.RunTextAsync(directory, ["merge-base", "--is-ancestor", Commit, "HEAD"], env, ct).ConfigureAwait(false);
        var since = await sinceTask.ConfigureAwait(false);
        if (ancestor.Started && ancestor.ExitCode == 1)
            return (null, $"baseline commit {shortHash} is not an ancestor of HEAD (rebased, squashed or another branch); C# commit total not given");
        if (!ancestor.Succeeded)
            return (null, $"baseline commit {shortHash} is not in this clone (shallow or not fetched); C# commit total not given");
        return since is int s ? (CSharpCommits + s, null) : (null, "counting C# commits since the baseline commit failed; C# commit total not given");
    }

    static async Task<int?> CountAsync(string directory, string range, IReadOnlyDictionary<string, string?>? env, CancellationToken ct)
    {
        var r = await GitProcess.RunTextAsync(directory, ["rev-list", "--count", "--full-history", range, "--", HistoryPolicy.CSharpPathspec], env, ct)
            .ConfigureAwait(false);
        return r.Succeeded && int.TryParse(r.StandardOutput.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    }
}
