using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SkarpSharp.History;
using SkarpSharp.Model;

namespace SkarpSharp.Baselines;

/// <summary>
/// The semantic baseline: <c>.skarp-baseline.semantic.json</c> next to the syntax baseline (with
/// the target framework in the name when one file per framework is written). Same layout as the syntax
/// baseline (findings grouped under their file) with <c>type</c> "semantic-baseline" plus <c>solution</c> and
/// <c>targets</c>; <c>commit</c> is HEAD, or <c>{commit}+</c> when an
/// analyzed <c>.cs</c> file had uncommitted changes. Compared like the syntax baseline, except that
/// entries whose type or tier was not evaluated in this run are neither fixed nor new.
/// </summary>
public static class SemanticBaseline
{
    /// <summary>The semantic baseline path for a syntax baseline path: <c>x.json</c> to <c>x.semantic[.tfm].json</c>.</summary>
    public static string PathFor(string syntaxBaseline, string? targetFramework)
    {
        var stem = syntaxBaseline.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? syntaxBaseline[..^5] : syntaxBaseline;
        return stem + ".semantic" + (targetFramework is null ? "" : "." + targetFramework) + ".json";
    }

    /// <summary>The <c>type</c> property of a semantic baseline file.</summary>
    public const string TypeName = "semantic-baseline";

    public static string Write(SemanticResult result, string toolVersion, string fingerprint, string? commit)
    {
        static string Str(string s) => BaselineLayout.Str(s);
        var settings = result.Run.Findings!;
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append($"  \"version\": {Baseline.CurrentVersion},\n");
        sb.Append($"  \"type\": \"{TypeName}\",\n");
        sb.Append($"  \"toolVersion\": {Str(toolVersion)},\n");
        sb.Append($"  \"preset\": {Str(settings.Preset)},\n");
        sb.Append($"  \"settingsFingerprint\": {Str(fingerprint)},\n");
        if (commit is not null) sb.Append($"  \"commit\": {Str(commit)},\n");
        if (result.Run.Solution is { } sln) sb.Append($"  \"solution\": {Str(sln)},\n");
        sb.Append($"  \"targets\": {Str(result.Run.Targets)},\n");
        if (result.Run.TargetFramework is { } tfm) sb.Append($"  \"targetFramework\": {Str(tfm)},\n");
        BaselineLayout.AppendBody(sb, result.Findings, null);
        return sb.ToString();
    }

    /// <summary>Reads the text of a semantic baseline (<c>type</c> "semantic-baseline"); the file must be of that type.</summary>
    public static BaselineFile Parse(string json, string path) => BaselineLayout.Parse(json, path, TypeName);

    public static BaselineFile Read(string path) => Parse(File.ReadAllText(path), path);

    /// <summary>
    /// A fingerprint of the semantic rule settings in effect (the rules, their thresholds,
    /// off switches) plus the coupling definition version, so a changed rule set is reported.
    /// </summary>
    public static string Fingerprint(FindingSettings settings, bool generators, string targets, bool includeGeneratedTargets = false,
        IEnumerable<string>? overrides = null)
    {
        var sb = new StringBuilder("semantic-v1\n");
        sb.Append("preset=").Append(settings.Preset).Append('\n');
        sb.Append("generators=").Append(generators ? "true" : "false").Append('\n');
        sb.Append("targets=").Append(targets).Append('\n');
        // Written only when on, so the default keeps the fingerprint's input format.
        if (includeGeneratedTargets) sb.Append("includeGeneratedTargets=true\n");
        foreach (var r in settings.Rules.OrderBy(r => r.Rule, StringComparer.Ordinal))
            sb.Append(r.Rule).Append(r.Off == true ? " off" : $" notice={r.Notice ?? "-"} warning={r.Warning ?? "-"}")
                .AppendJoin("", (r.Parameters ?? []).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $" {p.Key}={p.Value}")).Append('\n');
        // Each overrides entry in file order, last, so no overrides gives the same text.
        foreach (var o in overrides ?? []) sb.Append(o).Append('\n');
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    /// <summary>
    /// Marks the findings new, known or worsened and counts fixed entries; an entry whose place (path and type) is in
    /// <paramref name="notEvaluatedPlaces"/>, or whose path lies under a folder in <paramref name="notEvaluatedFolders"/>
    /// (projects that could not be compiled), counts as not evaluated instead of fixed.
    /// </summary>
    public static BaselineSummary Compare(IReadOnlyList<Finding> findings, BaselineFile baseline, string path, string fingerprint,
        IReadOnlySet<string> notEvaluatedPlaces, IReadOnlyList<string> notEvaluatedFolders)
    {
        var summary = Baseline.Compare(findings, baseline, path, null);
        var present = new HashSet<string>(findings.Select(f => f.Id), StringComparer.Ordinal);
        int notEvaluated = 0;
        foreach (var e in baseline.Findings)
        {
            if (present.Contains(e.Id)) continue;
            var parts = e.Id.Split('|');
            if (parts.Length < 3) continue;
            if (notEvaluatedPlaces.Contains(parts[1] + "|" + parts[2]) || notEvaluatedFolders.Any(f => parts[1].StartsWith(f, StringComparison.Ordinal)))
                notEvaluated++;
        }
        summary.Fixed -= notEvaluated;
        summary.NotEvaluated = notEvaluated;
        if (baseline.SettingsFingerprint.Length > 0)
        {
            summary.SettingsChanged = baseline.SettingsFingerprint != fingerprint;
            if (summary.SettingsChanged == true)
                summary.Notes.Add("semantic rule settings changed since the semantic baseline; new and fixed counts include settings effects; re-run `skarp baseline` to reset");
        }
        return summary;
    }

    /// <summary>The whole baseline is not evaluated: the tier did not run.</summary>
    public static BaselineSummary NotEvaluated(BaselineFile baseline, string path) => new()
    {
        Path = path, ToolVersion = baseline.ToolVersion, Preset = baseline.Preset, Entries = baseline.Findings.Count, NotEvaluated = baseline.Findings.Count,
    };

    /// <summary>HEAD, or <c>HEAD+</c> when a <c>.cs</c> file under the directory has uncommitted changes; null without git.</summary>
    public static async Task<string?> CommitAsync(string directory, CancellationToken ct = default)
    {
        var head = await GitProcess.RunTextAsync(directory, ["rev-parse", "--verify", "-q", "HEAD^{commit}"], null, ct).ConfigureAwait(false);
        if (!head.Succeeded) return null;
        var hash = head.StandardOutput.Trim();
        return await DirtyAsync(directory, ct).ConfigureAwait(false) ? hash + "+" : hash;
    }

    static async Task<bool> DirtyAsync(string directory, CancellationToken ct)
    {
        var status = await GitProcess.RunTextAsync(directory, ["status", "--porcelain", "--untracked-files=all", "--", ":(icase)*.cs"], null, ct).ConfigureAwait(false);
        return status.Succeeded && status.StandardOutput.Trim().Length > 0;
    }

    /// <summary>
    /// How a recorded <c>commit</c> or <c>commit+</c> relates to this run: equal to this commit, written just
    /// before it, from N commits ago, or not in this branch's history. Comparison itself is always by finding id.
    /// </summary>
    public static async Task<string?> RelationAsync(string directory, string? recorded, CancellationToken ct = default)
    {
        if (recorded is null || recorded.TrimEnd('+') is not { Length: 40 or 64 } commit || !commit.All(char.IsAsciiHexDigit)) return null;
        bool plus = recorded.EndsWith('+');
        var head = await GitProcess.RunTextAsync(directory, ["rev-parse", "--verify", "-q", "HEAD^{commit}"], null, ct).ConfigureAwait(false);
        if (!head.Succeeded) return null;
        var short12 = commit[..12];
        if (head.StandardOutput.Trim() == commit)
            return $"baseline commit {short12}{(plus ? "+" : "")}: this commit" + (plus ? " (written with uncommitted changes)" : "")
                + (await DirtyAsync(directory, ct).ConfigureAwait(false) ? "; this run has uncommitted changes" : "");
        var parent = await GitProcess.RunTextAsync(directory, ["rev-parse", "--verify", "-q", "HEAD^1"], null, ct).ConfigureAwait(false);
        if (plus && parent.Succeeded && parent.StandardOutput.Trim() == commit)
            return $"baseline commit {short12}+: written just before this commit";
        var ancestor = await GitProcess.RunTextAsync(directory, ["merge-base", "--is-ancestor", commit, "HEAD"], null, ct).ConfigureAwait(false);
        if (!ancestor.Succeeded) return $"baseline commit {short12}{(plus ? "+" : "")}: not in this branch's history (rebased, squashed or another branch)";
        var count = await GitProcess.RunTextAsync(directory, ["rev-list", "--count", commit + "..HEAD"], null, ct).ConfigureAwait(false);
        return count.Succeeded ? $"baseline commit {short12}{(plus ? "+" : "")}: {count.StandardOutput.Trim()} commits ago" : null;
    }
}
