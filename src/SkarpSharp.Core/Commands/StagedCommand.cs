using SkarpSharp.Agent;
using SkarpSharp.Findings;

namespace SkarpSharp.Commands;

/// <summary><c>skarp check --staged</c>, the commit hook's check (<see cref="StagedCheck"/>), kept
/// apart from <see cref="CheckCommand"/>.</summary>
static class StagedCommand
{
    /// <summary>The options of <c>check</c> that <c>--staged</c> does not take.</summary>
    static readonly string[] NotStaged = ["--track", "--semantic", "--session", "--agent", "--hook", "--config", "--baseline", "--no-config", "--no-baseline"];

    /// <summary>Why the options do not go with <c>--staged</c>, or null.</summary>
    internal static string? UsageError(CheckCommand.Args a, IReadOnlyList<string> args)
    {
        if (a.Files.Count > 0) return "check --staged takes no files (it reads the staged C# files from git's index)";
        foreach (var arg in args)
            if (Array.IndexOf(NotStaged, arg) >= 0) return $"check --staged does not take {arg} (it reads the settings and the baseline from git's index)";
        return null;
    }

    /// <summary>Without <c>--gate</c> (by hand, not the hook): the text format, as <c>skarp check</c> prints it.</summary>
    static int Plain(CheckCommand.Args a, CommandIO io, CheckOutcome outcome, LineStyle style)
    {
        int max = a.MaxLines ?? outcome.Budget(a.Gate);
        var print = new CheckPrint(max, style, null)
        {
            More = folded => AgentFormat.More(folded.Count, $"skarp check --staged --max-lines {max + folded.Count}"),
        };
        foreach (var line in CheckText.Lines(outcome, print, out _)) io.Error.WriteLine(line);
        return 0;
    }

    /// <summary>
    /// <c>check --staged</c>, the commit hook's check: the staged C# files against the staged baseline, on
    /// standard error, exit 3 when <c>--gate</c> trips. With <c>--gate</c> the hook report
    /// (<see cref="HookReport"/>), without it the text format. Silent without staged C# files; one line without a
    /// baseline in the index (exit 0); nothing saved and standard input never read.
    /// </summary>
    internal static int Run(CheckCommand.Args a, CommandIO io)
    {
        try
        {
            var s = CheckCommand.Settings(a, io, false, null, "main", null);
            var result = StagedCheck.Run(io.Cwd, s, io.GitEnvironment);
            if (result.NoFiles) return 0;
            foreach (var b in result.MissingBaselines)
                io.Error.WriteLine($"Skarp: no baseline ({b}) in the commit, so it is not checked: `skarp baseline .` records one, then `git add {Path.GetFileName(b)}`");
            foreach (var b in result.ChangedBaselines)
                io.Error.WriteLine($"Skarp: this commit changes {b}: the staged files are compared with the staged baseline");
            var outcome = result.Check;
            var style = new LineStyle(FileCheck.RootOfFolder(Path.GetFullPath(io.Cwd)), a.Gate);
            if (!a.Gate) return Plain(a, io, outcome, style);
            // With --gate (the commit hook) the report for a person.
            foreach (var note in outcome.Header) io.Error.WriteLine("Skarp: " + note);
            var data = HookReport.FromCheck(outcome, style);
            data.Claude = HookReport.UnderClaude(io.SettingsLocations);
            if (!outcome.GateTripped)
            {
                if (HookReport.Passed(data) is { } line) io.Error.WriteLine(line);
                return 0;
            }
            foreach (var line in HookReport.Blocked(data)) io.Error.WriteLine(line);
            return Gate.TrippedExitCode;
        }
        catch (CheckUsageException ex) { io.Error.WriteLine(ex.Message); return 2; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            io.Error.WriteLine($"skarp check --staged: {ex.Message}");
            return 1;
        }
    }
}
