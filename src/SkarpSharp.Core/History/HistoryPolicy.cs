using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SkarpSharp.History;

/// <summary>
/// Read-time and consumption-time policy of a history run. The canonical text and its digest
/// go into the run header so two runs are comparable only when the policy is equal.
/// </summary>
public sealed class HistoryPolicy
{
    public const int RenameSimilarityPercent = 50;
    /// <summary>Pinned rename limit (git's default since 2.33; was 400 before), so results do not depend on the git version.</summary>
    public const int RenameLimit = 1000;
    public const int DefaultBulkFlag = 300;

    /// <summary>
    /// Git reads only C# changes. <c>top</c> makes the pattern relative to the repository root (git runs in the
    /// analyzed folder, which may be a subfolder), <c>icase</c> matches <c>.CS</c> like the preprocessor does, and without
    /// <c>glob</c> magic <c>*</c> also matches <c>/</c>, so nested paths match. A rename between a C# and a non-C# name
    /// is then read as an add or a delete of the C# side (git pairs renames only among the paths the pathspec keeps).
    /// </summary>
    public const string CSharpPathspec = ":(top,icase)*.cs";

    /// <summary>Default bot patterns (case-insensitive regular expressions on author name and email).</summary>
    public static readonly IReadOnlyList<string> DefaultBotPatterns = [@"\[bot\]", "dependabot", "renovate", "github-actions", "snyk-bot"];

    /// <summary>Window start (committer time, unix seconds); null means whole history.</summary>
    public long? SinceUnix { get; init; }
    public bool IgnoreWhitespace { get; init; }
    /// <summary>
    /// Commits touching more files than this are listed. The count is the commit's C# rows, taken before
    /// the generated, build-output and no-change filters.
    /// </summary>
    public int BulkFlag { get; init; } = DefaultBulkFlag;
    /// <summary>Commits touching more files than this are dropped; null keeps all (the default).</summary>
    public int? BulkDrop { get; init; }
    public bool IncludeBots { get; init; }
    /// <summary>Bot patterns in effect (defaults plus <c>--bot-pattern</c> additions).</summary>
    public IReadOnlyList<string> BotPatterns { get; init; } = DefaultBotPatterns;

    Regex? _bot;

    public bool IsBot(string authorName, string authorEmail)
    {
        if (IncludeBots || BotPatterns.Count == 0) return false;
        _bot ??= new Regex(string.Join("|", BotPatterns.Select(p => $"(?:{p})")),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return _bot.IsMatch(authorName) || _bot.IsMatch(authorEmail);
    }

    /// <summary>
    /// Git arguments for the one streamed read, limited to C# files by
    /// <see cref="CSharpPathspec"/>. <c>--full-history</c> keeps every non-merge commit that changes a C# file:
    /// with a pathspec, git's default history simplification would skip side branches a merge is TREESAME to.
    /// </summary>
    public List<string> LogArguments()
    {
        var args = new List<string>
        {
            "-c", "core.quotePath=false", "-c", "log.showSignature=false",
            "log", "-z", "--no-color", "--no-ext-diff", "--no-textconv", "--no-merges", "--full-history",
            $"-M{RenameSimilarityPercent}%", $"-l{RenameLimit}", "--numstat", "--format=" + GitLogParser.Format,
        };
        if (IgnoreWhitespace) args.Add("-w");
        if (SinceUnix is long since) args.Add(SinceArgument(since));
        args.Add("HEAD");
        args.Add("--");
        args.Add(CSharpPathspec);
        return args;
    }

    public static string SinceArgument(long since) => "--since=@" + since.ToString(CultureInfo.InvariantCulture);

    /// <summary>Canonical, stable description of every setting that changes numbers.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append("merges=skip; renames=-M").Append(RenameSimilarityPercent).Append("% -l").Append(RenameLimit);
        sb.Append("; whitespace=").Append(IgnoreWhitespace ? "ignored(-w)" : "counted");
        sb.Append("; window=").Append(SinceUnix is long s ? "since " + Iso(s) + " (committer date)" : "all");
        sb.Append("; bulkFlag=").Append(BulkFlag.ToString(CultureInfo.InvariantCulture));
        sb.Append("; bulkDrop=").Append(BulkDrop?.ToString(CultureInfo.InvariantCulture) ?? "none");
        sb.Append("; bots=").Append(IncludeBots ? "included" : "excluded[" + string.Join(",", BotPatterns) + "]");
        sb.Append("; files=*.cs only (git pathspec ").Append(CSharpPathspec).Append(", --full-history; commit size counts C# files), generated and build output excluded; pureMoves=no change; binary=0/0; identity=lowercase %aE (mailmap)");
        return sb.ToString();
    }

    public string Digest() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Describe())))[..16];

    public static string Iso(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
