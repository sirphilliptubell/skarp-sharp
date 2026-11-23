namespace SkarpSharp.Commands;

/// <summary>
/// <c>skarp analyze ... --push-check [&lt;remote&gt; &lt;url&gt;]</c>, the push hook. It takes the
/// two arguments git appends to a pre-push hook, reads git's ref lines on standard input, skips deleted refs, and runs
/// the analysis (printed as the hook report on standard error unless <c>--format</c> is given, and <c>.</c>
/// as the directory unless one is given) only when a pushed commit is the current checkout (<c>HEAD</c>), since the analysis reads the working tree;
/// otherwise one line says the push was not checked. Exit codes as <c>analyze</c>; non-zero aborts the push.
/// </summary>
public static class PushCheck
{
    public const string Flag = "--push-check";
    const int RefLineFields = 4;

    /// <summary>Standard input is the console's and a terminal (a push check run by hand): nothing to read.</summary>
    static bool Terminal(TextReader input) => ReferenceEquals(input, Console.In) && !Console.IsInputRedirected;
    /// <summary>How long to wait for git's ref lines (a manager may not pass standard input on).</summary>
    static readonly TimeSpan InputWait = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CommandIO io)
    {
        int at = IndexOf(args);
        if (args.Count - at - 1 > 2)
        {
            io.Error.WriteLine($"skarp analyze: {Flag} takes at most git's two pre-push arguments (<remote> <url>)");
            return 2;
        }
        List<string> analyze = [.. args.Take(at)];
        if (analyze.Count == 1 || analyze[1].StartsWith('-')) analyze.Insert(1, ".");
        // Without --format, the report for a person (HookReport) on standard error.
        bool human = !analyze.Contains("--format");

        var pushed = Pushed(io);
        if (pushed is { Count: 0 }) return 0; // only deletes: nothing to check
        if (pushed is not null)
        {
            var head = await History.GitProcess.RunTextAsync(io.Cwd, ["rev-parse", "--verify", "-q", "HEAD"], io.GitEnvironment);
            if (!head.Succeeded || !pushed.Contains(head.StandardOutput.Trim()))
            {
                io.Error.WriteLine("Skarp: push not checked: the pushed commits are not the current checkout (HEAD), and the check reads the working tree; CI checks them");
                return 0;
            }
        }
        if (!human)
        {
            int code = await SkarpCommand.RunAsync(analyze, io);
            if (code == Findings.Gate.TrippedExitCode) foreach (var line in Agent.HookReport.WaysOut("push", true, Agent.HookReport.UnderClaude(io.SettingsLocations))) io.Error.WriteLine(line);
            return code;
        }
        return await SkarpCommand.RunAsync(analyze, io, (result, o, saved) => Report(io, result, o, saved));
    }

    /// <summary>The push hook's report: blocked, or at most one line when it passes.</summary>
    static void Report(CommandIO io, Model.AnalysisResult result, Configuration.SkarpOptions o, bool saved)
    {
        bool tripped = result.Gate is { Tripped: true } || SemanticStage.Tripped(result.SemanticTier);
        var data = Agent.HookReport.FromAnalysis(result, o.MinSeverityGiven ? o.MinSeverity : Findings.Severity.Warning, o.GateOn, saved);
        data.Claude = Agent.HookReport.UnderClaude(io.SettingsLocations);
        if (!tripped)
        {
            if (Agent.HookReport.Passed(data) is { } line) io.Error.WriteLine(line);
            return;
        }
        foreach (var line in Agent.HookReport.Blocked(data)) io.Error.WriteLine(line);
    }

    static int IndexOf(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++) if (args[i] == Flag) return i;
        return -1;
    }

    /// <summary>
    /// The local object names git pushes (deleted refs left out); null when there are no ref lines to read (a terminal,
    /// nothing within the wait, or a hook manager that does not pass them): then the current checkout is checked.
    /// </summary>
    static HashSet<string>? Pushed(CommandIO io)
    {
        if (io.In is null || Terminal(io.In)) return null;
        var read = io.In.ReadToEndAsync();
        if (!read.Wait(InputWait)) return null;
        var text = read.Result;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return null;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            // <local ref> <local oid> <remote ref> <remote oid>; a delete has an all-zero local oid.
            var parts = line.Split(' ');
            if (parts.Length >= RefLineFields && parts[1].Trim('0').Length > 0) set.Add(parts[1]);
        }
        return set;
    }
}
