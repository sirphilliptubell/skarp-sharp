using System.Globalization;
using SkarpSharp.Agent;
using SkarpSharp.Reporting;

namespace SkarpSharp.Mcp;

/// <summary>
/// The text of a <c>check</c> answer. A first line that is always there (how many
/// files, against what, the counts), the file part as <c>skarp check</c> prints it (headings with health, finding lines,
/// at most 15), the skipped inputs, the semantic part (its own line and at most 15 lines), and closing lines that name
/// the tools, never a command.
/// </summary>
internal sealed class CheckAnswer(string cwd, string path, FilePart files, SemanticPart? semantic)
{
    public const int Budget = 15, MaxSkipped = 15;

    public const string NotJudged = "Not judged from single files: duplicate code, change activity, test coverage, and the rules that need a solution " +
        "(class coupling, god classes, unused code, namespace cycles).";
    public const string NotJudgedWithSolution = "Not judged from single files: duplicate code, change activity and test coverage.";
    public const string Reused = "From the saved run: no analyzed file and no setting changed since it was made.";

    readonly LineStyle _style = new(FileCheck.RootOfFolder(cwd), false);

    public string Write()
    {
        var lines = new List<string> { FirstLine() };
        var fileLines = CheckText.Lines(files.Outcome, Print(Fold), out var printed);
        lines.AddRange(fileLines);
        // One fact per file past a health limit; no verdict, no instruction.
        foreach (var b in files.Outcome.Breaches) lines.Add($"Health limit: {Health.HealthLimit.Fact(_style.Display(b.Root, b.Path), b)}.");
        foreach (var note in files.Outcome.Header) lines.Add($"Health limits: {note}.");
        WriteSkipped(lines);
        List<AgentLine> semanticPrinted = [];
        if (semantic is not null) semanticPrinted = WriteSemantic(lines, semantic);
        if (fileLines.Count > 0 || semantic is not null) lines.Add(semantic is { Outcome: not null } ? NotJudgedWithSolution : NotJudged);
        if (First(semanticPrinted) is { } id) lines.Add($"About a finding: findings id={id.Handle}");
        if ((First(printed) ?? First(semanticPrinted)) is { } rule) lines.Add($"About a rule and how to fix it: explain rule={rule.Rule}");
        return string.Join("\n", lines) + "\n";
    }

    static AgentLine? First(List<AgentLine> printed) => printed.Find(l => l.Kind != LineKind.Health);

    CheckPrint Print(Func<IReadOnlyList<AgentLine>, string> fold) => new(Budget, _style, null) { More = fold, Bare = true };

    /// <summary><c>Skarp: 3 changed C# files checked against the last commit: 2 new or worse in 1 file, 1 fixed</c>.</summary>
    string FirstLine()
    {
        var changed = files.Changed ? "changed " : "";
        if (files.Considered == 0) return $"Skarp: no changed C# files in {path.Replace('\\', '/')} (compared with the last commit)";
        if (files.Checked == 0) return $"Skarp: no C# file checked: {Count(files.Considered, changed + "C# file")} skipped";
        return $"Skarp: {Count(files.Checked, changed + "C# file")} checked {Against()}: {Result(files.Outcome)}";
    }

    string Against()
    {
        var with = new List<string>();
        if (files.Against.Contains("baseline")) with.Add("the baseline");
        if (files.Against.Contains("commit")) with.Add("the last commit");
        return with.Count == 0 ? "with nothing to compare (no baseline, not in git)" : "against " + string.Join(" or ", with);
    }

    /// <summary><c>2 new or worse in 1 file, 1 better, 1 fixed</c>, or <c>nothing new or worse</c>.</summary>
    static string Result(CheckOutcome outcome)
    {
        int worse = 0, better = 0, fixedCount = 0, present = 0;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in outcome.Lines)
        {
            switch (l.Kind)
            {
                case LineKind.New or LineKind.Worse: worse++; paths.Add((l.Root ?? "") + "|" + l.Path); break;
                case LineKind.Better: better++; break;
                case LineKind.Resolved: fixedCount++; break;
                case LineKind.Present: present++; break;
            }
        }
        var parts = new List<string>();
        if (worse > 0) parts.Add($"{worse.ToString(CultureInfo.InvariantCulture)} new or worse in {Count(paths.Count, "file")}");
        if (better > 0) parts.Add($"{better.ToString(CultureInfo.InvariantCulture)} better");
        if (fixedCount > 0) parts.Add($"{fixedCount.ToString(CultureInfo.InvariantCulture)} fixed");
        if (present > 0) parts.Add(Count(present, "finding"));
        return parts.Count == 0 ? "nothing new or worse" : string.Join(", ", parts);
    }

    static string Count(int n, string noun) => TextFormat.Count(n, noun);

    /// <summary>The file part's fold: the files the rest is in, and the call that shows one file's first lines.</summary>
    string Fold(IReadOnlyList<AgentLine> folded)
    {
        var byFile = folded.GroupBy(_style.PathOf, StringComparer.Ordinal).Select(g => (Path: g.Key, N: g.Count()))
            .OrderByDescending(x => x.N).ThenBy(x => x.Path, StringComparer.Ordinal).ToList();
        var named = byFile.Take(AgentFormat.MaxFoldFiles).Select(x => $"{x.N} in {x.Path}").ToList();
        if (byFile.Count > AgentFormat.MaxFoldFiles)
            named.Add($"{byFile.Skip(AgentFormat.MaxFoldFiles).Sum(x => x.N)} in {Count(byFile.Count - AgentFormat.MaxFoldFiles, "other file")}");
        return $"+{folded.Count} more ({string.Join(", ", named)}); check files=<one file> shows that file's first {Budget}";
    }

    void WriteSkipped(List<string> lines)
    {
        for (int i = 0; i < files.Skipped.Count && i < MaxSkipped; i++) lines.Add($"Skipped: {files.Skipped[i].Path.Replace('\\', '/')} ({files.Skipped[i].Reason})");
        if (files.Skipped.Count > MaxSkipped) lines.Add($"+{files.Skipped.Count - MaxSkipped} more skipped");
    }

    /// <summary>The semantic part: its line, its findings (or why the tier did not run), the reuse line; returns the
    /// finding lines printed.</summary>
    List<AgentLine> WriteSemantic(List<string> lines, SemanticPart part)
    {
        if (part.Outcome is not { } outcome)
        {
            lines.Add($"Semantic tier: not evaluated: {part.NotEvaluated}");
            return [];
        }
        lines.Add(part.Baseline
            ? $"{part.Solution}, semantic rules against the semantic baseline: {Result(outcome)}"
            : $"{part.Solution}, semantic rules: no semantic baseline to compare with, so the warnings in the checked files are shown: {Count(outcome.Lines.Count, "finding")}");
        lines.AddRange(CheckText.Lines(outcome, Print(part.Baseline ? SemanticFold : FileFold), out var printed));
        if (part.Run.Reused) lines.Add(Reused);
        return printed;
    }

    static string SemanticFold(IReadOnlyList<AgentLine> folded) => $"+{folded.Count} more: findings new_only=true";

    string FileFold(IReadOnlyList<AgentLine> folded) => $"+{folded.Count} more: findings file={_style.PathOf(folded[0])} lists a file's findings";
}
