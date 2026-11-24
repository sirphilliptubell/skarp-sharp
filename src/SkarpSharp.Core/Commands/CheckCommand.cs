using System.Globalization;
using System.Text.Json;
using SkarpSharp.Agent;
using SkarpSharp.Configuration;
using SkarpSharp.Findings;

namespace SkarpSharp.Commands;

/// <summary>
/// <c>skarp check &lt;file&gt;...</c> (experimental): the syntax tier on files just written, for a
/// per-edit hook. The text format on standard output (at most 5 finding lines); with <c>--hook claude</c> the
/// Claude Code hook protocol on standard input and output, failing open (exit 0, nothing printed) on any internal error.
/// </summary>
public static class CheckCommand
{
    public static string Usage => $"""
        skarp check: the syntax findings of the files just written (one file is parsed; no history, no directory walk),
        for an AI agent's per-edit hook. Experimental.

        Usage:
          skarp check <file>... [options]
          skarp check --staged [--gate] [--max-lines <n>] [--include-notices]

        What it compares with:
          without --track        the baseline (.skarp-baseline.json of the root): findings new or worse than in it;
                                 without a baseline every warning
          --track                the file's earlier state in this session (a tracking file per session in
                                 ~/.skarp/tracked/, SKARP_HOME overrides ~/.skarp): new, worse and, once, resolved findings;
                                 the first check of a file compares with the last commit (git HEAD); a file not in git
                                 shows all its warnings; unchanged findings print nothing
          --session <id>         the session the tracking belongs to (default: one shared file per day)
          --agent <id>           the agent within the session (default main)
          --hook claude          read a Claude Code hook's JSON on standard input (PostToolUse on Edit|Write:
                                 the written file; Stop: the changed files against the session start and HEAD,
                                 leaving out what was shown at its current value; PostCompact: counts it, prints
                                 nothing) and answer in its JSON; implies --track and the session; non-.cs files
                                 and other events are ignored (exit 0)
          --gate                 block: with --hook, worse or new warnings in the gate set (gate.on, default
                                 *:warning) come back as decision "block"; without it, exit code 3 with the lines on
                                 standard error (needs --track or a baseline). The Stop summary then lists every
                                 blocking line first, also those an edit check already showed. The gate also
                                 judges each file's health (whatever gate.on says): a file that lost 1.0 or more, a
                                 file that fell and ends below 6.5, and a new file below 6.5 block; its heading
                                 says which limit, and its new or worse findings are listed, notices included.
                                 Compared with the baseline, with --hook since the session started
          --gate-health-drop <n>, --gate-health-new-min <n>, --gate-health-floor <n>   the health limits (settings
                                 gate.health.maxDrop, newFileMin, floor; 0 turns a limit off)
          --semantic             Stop only: also run the semantic tier over the root (slow; default syntax only)
          --staged               the git commit hook: the staged C# files from git's index against the staged
                                 baseline and .skarp.json, lines on standard error, nothing saved (docs/git-hooks.md)


        """ + OutputUsage;

    /// <summary>The second half of <see cref="Usage"/>: output, budgets, root, left-out rules, composites, exit codes.</summary>
    static string OutputUsage => $"""
        Output (the text format; tools read JSON, the text may change in any release): the findings grouped by file,
        one line each, under the file's heading:
          src/Orders.cs health 9.0 -> 8.8
            worse line 18 Orders.Ship long-method: physical lines 81 -> 97 (limit 70) [cdceba26]
        The first word says what changed: new, worse, better or fixed (without a comparison: warning or notice); a
        notice says (notice) after its rule. The heading shows the file's syntax health score (1 to 10, 10 = healthy;
        docs/health-score.md) when it changed at one decimal against what the check compares with (a file new to
        git: health 7.4 (new file), only below 10); files print with the largest drop first, findings by line. Paths:
        relative to the root for files in the project of the working folder (with --hook: the hook input's cwd), else
        absolute.

          --max-lines <n>        at most n finding lines (default 5, setting output.checkMaxLines; the Stop summary
                                 15, and with --gate output.gateMaxLines, default 50); headings and the other lines
                                 are free. The rest folds into "+N more: skarp show --session <id> --new-only" with a
                                 session (the hook, or --track --session), else "+N more: skarp show --file <path>"
                                 (at most three files)
          --include-notices      also print notices (default: warnings only; notices are tracked either way)
          --no-save              do not record the result in .skarp/checked/ (skarp show reads it)
          --config <file>, --no-config, --baseline <file>, --no-baseline   as for analyze
          --no-user-config, --gate-user-config   as for analyze: the settings levels (skarp --help); --gate skips the
                                 system and personal files unless --gate-user-config

        Root: the folder of the nearest .skarp.json, else the git work tree root, else the file's folder; the ids equal
        those of skarp analyze on that root. Left out, because one file cannot decide them: {string.Join(", ", FileCheck.LeftOut)}
        (active-complex-file needs git history; the duplicate-code rules match across files, even clones within the
        edited file alone, whose group id would differ from the full run's; untested-complex-method needs a coverage
        report, which is stale while the file is being edited), large-type for types declared partial (the
        type spans files), and every semantic-tier rule:
          {string.Join(", ", RuleCatalog.Semantic.Select(r => r.Id))}.
        skarp analyze covers them; the Stop hook also reports duplicate-code groups
        with a copy in a changed file that are new or worse than in the baseline (it matches the whole root).

        Composites: brain-method is on by default (rules.brain-method set to false in the root's .skarp.json
        switches it off), brain-class is experimental and on only when set to true: a member or type whose findings form a
        brain method or brain class is one line in place of its parts (new when every part is new, worse when any part is
        new or worse); --gate still judges the parts. A brain class of a partial type is left out with its large-type
        finding.

        Exit codes: 0 done (also when findings are printed), 1 tool error, 2 usage error, 3 --gate tripped (plain mode:
        new or worse findings in the gate set, or a file past a health limit).
        """;

    /// <summary>Hook output keeps <c>-&gt;</c> and quotes readable (<c>></c> is valid JSON but noise for the model).</summary>
    static readonly HookJsonContext Relaxed = new(new JsonSerializerOptions
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    });

    internal sealed class Args
    {
        public List<string> Files = [];
        public bool Track, Gate, Semantic, NoSave, Staged;
        public string? Session, Agent, Hook;
        public int? MaxLines;
        public Severity MinSeverity = Severity.Warning;
        public List<string> SettingsArgs = [];
    }

    static string? Parse(IReadOnlyList<string> args, Args a)
    {
        for (int i = 1; i < args.Count; i++)
        {
            var name = args[i];
            string? Value() => i + 1 < args.Count ? args[++i] : null;
            switch (name)
            {
                case "--staged": a.Staged = true; break;
                case "--track": a.Track = true; break;
                // The gate skips the system and personal files, as analyze --gate does.
                case "--gate": a.Gate = true; a.SettingsArgs.Add(name); break;
                case "--semantic": a.Semantic = true; break;
                case "--no-save": a.NoSave = true; break;
                case "--session": if (Value() is not { Length: > 0 } s) return "Option --session needs a value"; a.Session = s; break;
                case "--agent": if (Value() is not { Length: > 0 } ag) return "Option --agent needs a value"; a.Agent = ag; break;
                case "--hook": if (Value() is not "claude") return "Invalid value for --hook (expected claude)"; a.Hook = "claude"; break;
                case "--max-lines":
                    if (!int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out var ml) || ml < 1) return "Invalid value for --max-lines (expected a number of lines, 1 or more)";
                    a.MaxLines = ml; break;
                case "--include-notices": a.MinSeverity = Severity.Notice; break;
                case "--config" or "--baseline" or "--gate-health-drop" or "--gate-health-new-min" or "--gate-health-floor":
                    if (Value() is not { Length: > 0 } v) return $"Option {name} needs a value";
                    a.SettingsArgs.AddRange([name, v]); break;
                case "--no-config" or "--no-baseline" or "--no-user-config" or "--gate-user-config": a.SettingsArgs.Add(name); break;
                default:
                    if (name.StartsWith('-')) return $"Unknown option: {name}";
                    a.Files.Add(name);
                    break;
            }
        }
        return Remaining(a, args);
    }

    /// <summary>The checks after the options: <c>--staged</c>'s, else a missing file.</summary>
    static string? Remaining(Args a, IReadOnlyList<string> args) => a.Staged ? StagedCommand.UsageError(a, args) : a.Hook is null && a.Files.Count == 0 ? "check: missing <file>" : null;

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CommandIO io)
    {
        if (args.Count > 1 && args[1] is "-h" or "--help") { io.Out.WriteLine(Usage); return 0; }
        var a = new Args();
        if (Parse(args, a) is { } error)
        {
            io.Error.WriteLine(error);
            io.Error.WriteLine("skarp check --help lists the options.");
            return 2;
        }
        if (a.Hook is not null) return await HookAsync(a, io);
        if (a.Staged) return StagedCommand.Run(a, io);
        return await PlainAsync(a, io);
    }

    /// <summary>Plain <c>check &lt;file&gt;...</c>.</summary>
    static async Task<int> PlainAsync(Args a, CommandIO io)
    {
        try
        {
            var settings = Settings(a, io, a.Track, a.Session, a.Agent ?? "main", null);
            var files = a.Files.Select(f => Path.GetFullPath(Path.IsPathRooted(f) ? f : Path.Combine(io.Cwd, f))).ToList();
            var outcome = await CheckRun.RunAsync(files, settings);
            foreach (var note in outcome.Notes) io.Error.WriteLine(note);
            if (a.Gate && !a.Track && outcome.MissingBaseline)
            {
                io.Error.WriteLine("--gate needs --track or a baseline file: without either every warning would count as new. `skarp baseline` records one.");
                return 2;
            }
            var lines = CheckText.Lines(outcome, PlainPrinting(a, io, outcome), out _);
            bool tripped = a.Gate && outcome.GateTripped;
            foreach (var line in lines) (tripped ? io.Error : io.Out).WriteLine(line);
            return tripped ? Gate.TrippedExitCode : 0;
        }
        catch (CheckUsageException ex) { io.Error.WriteLine(ex.Message); return 2; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            io.Error.WriteLine($"skarp check: {ex.Message}");
            return 1;
        }
    }

    internal static CheckSettings Settings(Args a, CommandIO io, bool track, string? session, string agent, string? agentType) => new()
    {
        Track = track, Gate = a.Gate, Session = TrackingStore.SafeSession(session), Agent = agent, AgentType = agentType, MinSeverity = a.MinSeverity,
        Save = !a.NoSave, SettingsArgs = a.SettingsArgs, Home = io.HomeFolder, Now = io.Now, Locations = io.SettingsLocations,
        Hook = a.Hook is not null, GitEnvironment = io.GitEnvironment, JudgeHealth = a.Gate,
    };

    /// <summary>Plain <c>check</c>: a fold line names <c>skarp show --session</c> only with <c>--track --session</c>.</summary>
    static CheckPrint PlainPrinting(Args a, CommandIO io, CheckOutcome outcome) => Printing(a, io.Cwd, outcome, a.Track ? a.Session : null, false);

    /// <summary>How a check prints: the budget (<c>--max-lines</c>, else the outcome's), paths for the project of
    /// <paramref name="cwd"/>, and the short session id of <paramref name="session"/> (none: the fold line names files);
    /// <paramref name="hook"/> for the Claude hook's text.</summary>
    static CheckPrint Printing(Args a, string cwd, CheckOutcome outcome, string? session, bool hook)
    {
        var safe = TrackingStore.SafeSession(session);
        return new CheckPrint(a.MaxLines ?? outcome.Budget(a.Gate), new LineStyle(FileCheck.RootOfFolder(Path.GetFullPath(cwd)), a.Gate),
            safe == TrackingStore.NoSession ? null : AgentFormat.Short(safe)) { Hook = hook };
    }

    /// <summary>
    /// <c>--hook claude</c>: PostToolUse checks the written file with tracking; Stop summarizes the session's changed
    /// files once (never when <c>stop_hook_active</c>); PostCompact raises the session's compaction count and
    /// prints nothing. Always exit 0; internal errors go to standard error only.
    /// </summary>
    static async Task<int> HookAsync(Args a, CommandIO io)
    {
        try
        {
            var text = io.In is null ? "" : await io.In.ReadToEndAsync();
            var input = text.Trim().Length == 0 ? null : JsonSerializer.Deserialize(text, HookJsonContext.Default.ClaudeHookInput);
            if (input is null) return 0;
            var agent = string.IsNullOrEmpty(input.AgentId) ? "main" : input.AgentId;
            var session = a.Session ?? input.SessionId;
            CheckOutcome? outcome;
            var s = Settings(a, io, true, session, agent, input.AgentType);
            bool stop = false;
            switch (input.HookEventName)
            {
                case "PostToolUse":
                    if (input.ToolInput?.FilePath is not { Length: > 0 } file || !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return 0;
                    var full = Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(input.Cwd ?? io.Cwd, file));
                    outcome = await CheckRun.RunAsync([full], s);
                    break;
                case "Stop":
                    // Never continue twice in a row: Claude Code is already continuing because of a Stop hook.
                    if (input.StopHookActive == true) return 0;
                    stop = true;
                    outcome = CheckRun.Stop(s, a.Semantic ? (root, paths) => SemanticFindings(root, paths, s) : null, (root, paths) => CloneFindings(root, paths, s));
                    break;
                case "PostCompact":
                    // Claude's context was compacted; every agent of the session gets the About line again.
                    new TrackingStore(s.Home, s.Now).Update(s.Session, st => st.Compactions++);
                    return 0;
                default:
                    return 0;
            }
            if (outcome is not null) Answer(a, io, outcome, Printing(a, input.Cwd ?? io.Cwd, outcome, session, true), s, stop);
            return 0;
        }
        catch (Exception ex)
        {
            // Fail open: a broken hook that blocks an agent is worse than silence.
            io.Error.WriteLine($"skarp check --hook: {ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }

    /// <summary>The hook's answer on standard output (nothing when there are no lines). It records what it printed,
    /// so a later Stop summary does not repeat it, and in the same write the About-line rhythm, which decides
    /// whether this message carries the About line.</summary>
    static void Answer(Args a, CommandIO io, CheckOutcome outcome, CheckPrint print, CheckSettings s, bool stop)
    {
        var lines = CheckText.Lines(outcome, print, out var printed);
        if (lines.Count == 0) return;
        if (CheckRun.RecordShown(s, printed, outcome, print.Max > 1)) lines = CheckText.Lines(outcome, print.WithAbout(), out _);
        var context = string.Join("\n", lines);
        var answer = a.Gate && outcome.GateTripped
            ? new ClaudeHookOutput { Decision = "block", Reason = context }
            : new ClaudeHookOutput { HookSpecificOutput = new ClaudeHookSpecificOutput { HookEventName = stop ? "Stop" : "PostToolUse", AdditionalContext = context } };
        io.Out.Write(JsonSerializer.Serialize(answer, Relaxed.ClaudeHookOutput));
        io.Out.Write('\n');
    }

    /// <summary>
    /// The clone groups of the Stop hook: the syntax tier over the whole root (clones span files),
    /// compared with the root's baseline; the groups with a copy in one of the given files. Empty when both clone rules
    /// are off or the analysis fails.
    /// </summary>
    static List<Model.Finding> CloneFindings(string root, IReadOnlyList<string> paths, CheckSettings s)
    {
        var parsed = CommandLine.Parse(["analyze", root, "--no-history", "--no-semantic", .. s.SettingsArgs], s.Locations);
        if (parsed.Options is not { } o || o.AnalyzerOptions().Clones == Clones.CloneMode.None) return [];
        var result = Analysis.Analyzer.AnalyzeDirectory(o.Directory, o.AnalyzerOptions());
        FindingEngine.Apply(result, o.RuleSettings(), o.IncludeGenerated);
        var findings = (result.Findings ?? []).Where(f => f.Clone is not null).ToList();
        var baselinePath = Path.GetFullPath(o.BaselineFile());
        if (!o.NoBaseline && File.Exists(baselinePath)) Baselines.Baseline.Compare(findings, Baselines.Baseline.Read(baselinePath), baselinePath, null);
        var set = new HashSet<string>(paths, StringComparer.Ordinal);
        return [.. findings.Where(f => f.Clone!.Instances.Any(i => set.Contains(i.Path)))];
    }

    /// <summary>The semantic tier for the Stop hook with <c>--semantic</c>: a full analysis of the root, its semantic
    /// findings in the given files (the highest target framework's).</summary>
    static List<Model.Finding> SemanticFindings(string root, IReadOnlyList<string> paths, CheckSettings s)
    {
        var parsed = CommandLine.Parse(["analyze", root, "--no-history", .. s.SettingsArgs], s.Locations);
        if (parsed.Options is not { } o) return [];
        var (result, _, _, _) = SkarpCommand.AnalyzeResultAsync(o).GetAwaiter().GetResult();
        var set = new HashSet<string>(paths, StringComparer.Ordinal);
        return Join.Highest(result.SemanticTier?.Results ?? []) is { } r ? [.. r.Findings.Where(f => set.Contains(f.Path))] : [];
    }
}
