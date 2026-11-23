using System.Diagnostics;
using System.Text.Json;
using SkarpSharp.Analysis;
using SkarpSharp.Baselines;
using SkarpSharp.Configuration;
using SkarpSharp.Findings;
using SkarpSharp.History;
using SkarpSharp.Model;
using SkarpSharp.Reporting;

namespace SkarpSharp.Commands;

/// <summary>Where a command writes: the report, messages, and a raw stream for JSON on standard output.</summary>
public sealed class CommandIO
{
    public required TextWriter Out { get; init; }
    public required TextWriter Error { get; init; }
    /// <summary>Opens standard output as a stream (JSON with <c>--format json</c> and no <c>--output</c>).</summary>
    public required Func<Stream> OpenStdout { get; init; }
    /// <summary>Standard input (the hook JSON of <c>skarp check --hook claude</c>); null reads nothing.</summary>
    public TextReader? In { get; init; }
    /// <summary>The folder relative paths and <c>skarp show</c>'s search start from (default: the current directory).</summary>
    public string? WorkingDirectory { get; init; }
    /// <summary>The folder of the tracking files (default: <c>SKARP_HOME</c>, else <c>~/.skarp</c>).</summary>
    public string? Home { get; init; }
    /// <summary>The clock (tests); default the system clock.</summary>
    public Func<DateTimeOffset>? Clock { get; init; }
    /// <summary>The system and personal settings files and the environment (tests); default <see cref="SettingsLocations.Default"/>.</summary>
    public SettingsLocations? SettingsLocations { get; init; }
    /// <summary>The keys of <c>skarp setup</c>'s menu (tests); null reads the console.</summary>
    public Setup.IKeySource? Keys { get; init; }
    /// <summary>What <c>skarp setup</c>'s menu draws on (tests); null draws on the console.</summary>
    public Setup.ITerminal? Terminal { get; init; }
    /// <summary>What <c>skarp install</c> reads from the machine (environment, home, git, terminal, programs); tests.</summary>
    public Install.InstallSeams? Install { get; init; }
    /// <summary>Variables for the git processes of <c>check --staged</c> and the push check (tests isolate git
    /// from the machine's configuration); null: inherited only.</summary>
    public IReadOnlyDictionary<string, string?>? GitEnvironment { get; init; }

    internal string Cwd => WorkingDirectory ?? Environment.CurrentDirectory;
    internal string HomeFolder => Home ?? Agent.TrackingStore.DefaultHome();
    internal DateTimeOffset Now => Clock?.Invoke() ?? DateTimeOffset.UtcNow;
}

/// <summary>
/// The <c>skarp</c> commands (kept apart from Program.cs so they can be tested end to end in process). Exit codes:
/// 0 done, 1 tool error, 2 usage error, 3 gate tripped (only with the opt-in gate; without it findings
/// and baselines never change the exit code).
/// </summary>
public static class SkarpCommand
{
    public static string ToolVersion => Analyzer.ToolVersion;

    public static Task<int> RunAsync(IReadOnlyList<string> args, CommandIO io) => RunAsync(args, io, null);

    /// <param name="report"><c>analyze</c> hands its result to this in place of printing it (the push hook's
    /// report): the result, the options, and whether the run was saved in <c>.skarp/last-run</c>.</param>
    internal static async Task<int> RunAsync(IReadOnlyList<string> args, CommandIO io, Action<AnalysisResult, SkarpOptions, bool>? report)
    {
        if (OwnParsers.Run(args, io) is { } own) return await own;
        var parsed = CommandLine.Parse(args, io.SettingsLocations);
        if (parsed.Explain)
        {
            if (parsed.ExplainRule is null) { HumanReport.WriteRuleList(io.Out); return 0; }
            if (RuleCatalog.Find(parsed.ExplainRule) is not { } rule)
            {
                io.Error.WriteLine($"Unknown rule: {parsed.ExplainRule}");
                HumanReport.WriteRuleList(io.Error);
                return 2;
            }
            HumanReport.WriteExplain(io.Out, rule);
            return 0;
        }
        if (parsed.Options is not { } o)
        {
            if (parsed.Error is not null) io.Error.WriteLine(parsed.Error);
            if (parsed.ShowUsage) (parsed.ExitCode == 0 ? io.Out : io.Error).WriteLine(CommandLine.Usage);
            return parsed.ExitCode;
        }
        if (!Directory.Exists(o.Directory))
        {
            io.Error.WriteLine($"Directory not found: {o.Directory}");
            return 2;
        }
        if (o.Command == "config") return Config(o, io);
        if (o.Command == "analyze" && o.BaselinePath is not null && !o.NoBaseline && !File.Exists(o.BaselinePath))
        {
            io.Error.WriteLine($"Baseline file not found: {o.BaselinePath} (skarp baseline writes it)");
            return 2;
        }
        if (o.Command == "analyze" && o.Gate && (o.NoBaseline || !File.Exists(o.BaselineFile())))
        {
            io.Error.WriteLine(o.NoBaseline
                ? "--gate needs the baseline: it cannot be used with --no-baseline"
                : $"--gate needs a baseline file: {Path.GetFullPath(o.BaselineFile())} not found. Record one with `skarp baseline {o.Directory}` and commit it; the gate then trips on findings that are new or worse than in it.");
            return 2;
        }

        try
        {
            if (o.Command == "history") return await History(o, io);
            return await Analyze(o, io, report);
        }
        catch (Coverage.CoverageFormatException ex)
        {
            io.Error.WriteLine($"skarp: coverage report {ex.Message}");
            return 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            io.Error.WriteLine($"skarp: {ex.Message}");
            return 1;
        }
    }

    static async Task<int> History(SkarpOptions o, CommandIO io)
    {
        var sw = Stopwatch.StartNew();
        var analyzerOptions = o.AnalyzerOptions();
        var historyTask = HistoryRunner.RunAsync(o.Directory, HistoryOptions(o));
        var excluded = Analyzer.FindExcluded(o.Directory, analyzerOptions);
        var run = await historyTask;
        var report = new HistoryReport
        {
            SchemaVersion = Analyzer.SchemaVersion,
            Run = new HistoryRunHeader
            {
                ToolVersion = ToolVersion,
                Root = Path.GetFullPath(o.Directory),
                Runtime = RuntimeName(),
                Git = run.Git,
                IncludeTests = o.IncludeTests,
                IncludeGlobs = o.Include.Count > 0 ? [.. o.Include] : null,
                ExcludeGlobs = o.Exclude.Count > 0 ? [.. o.Exclude] : null,
                SettingsFiles = SettingsFilesOf(o),
            },
            History = run.Summary,
            Files = HistoryJoin.FilesUnder(run, o.Directory, [.. excluded.Select(e => e.Path)]),
            Excluded = excluded.Count > 0 ? excluded : null,
        };
        report.Run.DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 3);
        report.Run.PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64;
        bool json = o.Format == "json";
        WriteJson(io, o.Output, json, s => JsonSerializer.Serialize(s, report, ResultJsonContext.Default.HistoryReport));
        if (json)
            io.Error.WriteLine($"skarp history: {report.Files.Count} files with history, {report.Run.DurationSeconds:0.000} s, {report.Run.Runtime}");
        else
            HumanReport.WriteHistory(io.Out, report, o.Top, excluded, o.IncludeTests, o.ExcludeTestsWhy);
        return 0;
    }

    /// <summary>The analysis of <c>analyze</c>, <c>hotspots</c> and <c>baseline</c> (both tiers, baseline written or
    /// compared, gate), without writing any output; also used by the <c>Stop</c> hook with <c>--semantic</c>.</summary>
    internal static async Task<(AnalysisResult Result, RuleSettings Settings, string Fingerprint, string BaselinePath)> AnalyzeResultAsync(SkarpOptions o,
        CancellationToken ct = default)
    {
        var pathRules = o.PathRules();
        var settings = pathRules.Base;
        // The git read runs in its own process while the syntax tier parses.
        var historyTask = o.History ? HistoryRunner.RunAsync(o.Directory, HistoryOptions(o)) : null;
        // The baseline records HEAD and its C# commit count (history on only: it reads git).
        var anchorTask = o.Command == "baseline" && o.History ? CommitAnchor.RecordAsync(o.Directory) : null;
        // The semantic tier's acquisition (child processes) runs while the syntax tier parses.
        var semanticTask = o.Command is "analyze" or "baseline" ? SemanticStage.Start(o, ct) : null;
        // Coverage reports (analyze, hotspots and baseline); the syntax walk then also records the line facts.
        var coverage = o.CoverageInputs();
        var result = Analyzer.AnalyzeDirectory(o.Directory, o.AnalyzerOptions(coverageLines: coverage is not null));
        result.Run.SettingsFiles = SettingsFilesOf(o);
        if (historyTask is not null)
            HistoryJoin.Apply(result, await historyTask, o.Top, o.IncludeGenerated);
        if (coverage is not null) Coverage.CoverageEngine.Apply(result, coverage);
        FindingEngine.Apply(result, pathRules, o.IncludeGenerated);
        OverridesInfo(o, pathRules, result);
        Coverage.CoverageBaseline.LabelHotspots(result);
        var notEvaluated = Coverage.CoverageBaseline.NotEvaluated(result);

        var baselinePath = Path.GetFullPath(o.BaselineFile());
        var fingerprint = Baseline.SettingsFingerprint(result.Run.Findings!, o.IncludeTests, o.IncludeGenerated, o.Include, o.Exclude, o.Variants.FingerprintText,
            o.RuleOverrides.Count == 0 ? null : RuleOverrides.FingerprintLines(o.RuleOverrides, pathRules, semantic: false));
        if (o.Command == "baseline")
        {
            var anchor = anchorTask is null ? null : await anchorTask;
            // Recorded untested-complex-method findings this run cannot judge are kept as they are.
            var kept = Coverage.CoverageBaseline.Kept(baselinePath, notEvaluated);
            var text = Baseline.Write([.. result.Findings ?? [], .. kept], ToolVersion, result.Run.Findings!, fingerprint, anchor, result.Files.Select(f => f.Path));
            File.WriteAllText(baselinePath, text);
        }
        BaselineFile? baselineFile = null;
        if (o.Command != "baseline" && !o.NoBaseline && File.Exists(baselinePath))
        {
            baselineFile = Baseline.Read(baselinePath);
            result.Baseline = Baseline.Compare(result.Findings ?? [], baselineFile, baselinePath, result.Run.Findings, fingerprint, notEvaluated);
            if (result.Baseline.SettingsChanged == true && MachineSettingsNote(o) is { } machine) result.Baseline.Notes.Add(machine);
            BrainRules.ApplyStates(result.Composites, result.Findings ?? []);
        }

        if (o.Command == "analyze" && o.Gate && result.Baseline is not null)
            result.Gate = Gate.Evaluate(result.Findings ?? [], o.GateOn);
        if (semanticTask is not null) await SemanticStage.FinishAsync(semanticTask, o, result, pathRules, ToolVersion, ct);
        ApplyHealth(o, result, settings, baselineFile, pathRules);

        if (o.Detail == "files") HistoryJoin.ToFileDetail(result);
        result.Run.PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64;
        return (result, settings, fingerprint, baselinePath);
    }

    /// <summary><c>skarp config</c>. <c>--format json</c> gives the levels and origins for skarp setup.
    /// The overrides section counts the files a full run analyzes; <c>--file</c> shows one file.</summary>
    static int Config(SkarpOptions o, CommandIO io)
    {
        ConfigOverrides? view;
        try { view = ConfigOverridesOf(o, io.Cwd); }
        catch (ArgumentException ex) { io.Error.WriteLine(ex.Message); return 2; }
        if (o.Format == "json")
            WriteJson(io, o.Output, json: true, s => JsonSerializer.Serialize(s, ConfigReport.Of(o, ToolVersion, view), ConfigJsonContext.Default.ConfigReport));
        else HumanReport.WriteConfig(io.Out, o, o.PathRules().Base, ToolVersion, view);
        return 0;
    }

    /// <summary>
    /// The overrides part of <c>skarp config</c>, or null with neither overrides nor <c>--file</c>. With
    /// overrides, the syntax tier runs (no history, no semantic tier) to count the files each entry matches, since
    /// <c>"tests": true</c> needs Skarp's test detection; <c>--file</c> analyzes that file as <c>skarp check</c> does.
    /// </summary>
    /// <exception cref="ArgumentException">The <c>--file</c> path does not exist or lies outside the analyzed directory.</exception>
    static ConfigOverrides? ConfigOverridesOf(SkarpOptions o, string cwd)
    {
        if (o.RuleOverrides.Count == 0 && o.ConfigFile is null) return null;
        var rules = o.PathRules();
        var root = Path.GetFullPath(o.Directory);
        int[]? matched = null;
        int analyzed = 0, any = 0;
        if (o.RuleOverrides.Count > 0)
        {
            var result = Analyzer.AnalyzeDirectory(root, o.AnalyzerOptions());
            var kept = result.Files.Where(f => f.Error is null && (o.IncludeGenerated || !f.Generated)).Select(f => (f.Path, f.TestCode)).ToList();
            (matched, any) = rules.MatchCounts(kept);
            analyzed = kept.Count;
        }
        ConfigFileView? file = null;
        if (o.ConfigFile is { } p)
        {
            var full = Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(cwd, p));
            if (!File.Exists(full)) throw new ArgumentException($"--file: file not found: {full}");
            var rel = Agent.FileCheck.Relative(root, full);
            if (rel.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(rel)) throw new ArgumentException($"--file: {full} is not under {root}");
            var check = Agent.FileCheck.Analyze(o, rules, root, rel, Agent.FileCheck.Decode(File.ReadAllBytes(full)));
            bool test = check.File?.TestCode ?? false;
            file = new ConfigFileView { Path = rel, TestCode = test, Excluded = check.ExcludedReason, Matched = rules.Matched(rel, test), Settings = rules.For(rel, test) };
        }
        var walk = new List<(RuleOverride, string)>();
        foreach (var r in o.RuleOverrides)
            foreach (var id in (ReadOnlySpan<string>)[RuleIds.UnusedMember, RuleIds.UnusedType, RuleIds.UsedOnlyByTests, RuleIds.UnreadMember])
                if (r.RulesNamed.Contains(id) && rules.Base[id].Notice is null && rules.Combination([r.Index])[id].Notice is not null) walk.Add((r, id));
        return new ConfigOverrides { Entries = o.RuleOverrides, Matched = matched, AnalyzedFiles = analyzed, MatchedFiles = any, File = file, WalkRules = walk };
    }

    /// <summary><c>run.settingsFiles</c>, the levels whose file applied; null when none did.</summary>
    internal static List<SettingsFileInfo>? SettingsFilesOf(SkarpOptions o) =>
        o.SettingsFiles.Count == 0 ? null : [.. o.SettingsFiles.Select(l => new SettingsFileInfo { Level = l.Level.Name(), Path = l.Path! })];

    /// <summary>
    /// When the settings fingerprint differs from the baseline's, the system and personal files that
    /// set keys affecting findings in this run (they may explain the difference); null when none did (a gate run never).
    /// </summary>
    internal static string? MachineSettingsNote(SkarpOptions o)
    {
        var levels = o.MachineFindingKeys();
        if (levels.Count == 0) return null;
        return "machine settings that affect findings applied: " + string.Join("; ", levels.Select(l =>
            $"{l.Level.Level.Name()} {l.Level.Path} ({string.Join(", ", l.Keys)})")) + "; the gate skips them (--gate)";
    }

    /// <summary>The code health scores and roll-ups, from both tiers' findings when the semantic tier ran.</summary>
    static void ApplyHealth(SkarpOptions o, AnalysisResult result, RuleSettings settings, BaselineFile? baselineFile, PathRules? pathRules = null)
    {
        var semantic = result.SemanticTier is { Status: "ran" } tier ? Agent.Join.Highest(tier.Results) : null;
        BaselineFile? semanticBaseline = null;
        if (semantic?.Baseline is { Path: { Length: > 0 } p } && File.Exists(p)) semanticBaseline = SemanticBaseline.Read(p);
        var inputs = new Health.HealthInputs
        {
            Settings = o.Score, Rules = settings, IncludeGenerated = o.IncludeGenerated, Semantic = semantic, RulesOf = RulesOf(pathRules, result),
            SyntaxBaseline = baselineFile, SemanticBaseline = semanticBaseline,
        };
        Health.HealthEngine.Apply(result, inputs);
        // The gate also judges each file's health against the baseline, whatever its gate set.
        if (result.Gate is { } gate)
        {
            gate.Health = Health.AnalysisLimits.Judge(result, inputs, o.HealthLimits);
            if (gate.Health.Files.Count > 0) gate.Tripped = true;
        }
    }

    /// <summary>The settings of a recorded baseline entry's path, judged with the file's test status in
    /// this run; a path the run did not keep (deleted, now excluded) is matched by its globs only, as production code. Null
    /// without overrides.</summary>
    internal static Func<string, RuleSettings>? RulesOf(PathRules? rules, AnalysisResult result)
    {
        if (rules is not { Count: > 0 }) return null;
        var test = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var f in result.Files) test.TryAdd(f.Path, f.TestCode);
        return p => rules.For(p, test.GetValueOrDefault(p));
    }

    /// <summary><c>run.findings.pathOverrides</c> and the note for each <c>.skarp.json</c> found below the
    /// analyzed root, which is not applied.</summary>
    static void OverridesInfo(SkarpOptions o, PathRules rules, AnalysisResult result)
    {
        var info = result.Run.Findings!;
        var kept = result.Files.Where(f => f.Error is null && (o.IncludeGenerated || !f.Generated)).Select(f => (f.Path, f.TestCode));
        info.PathOverrides = RuleOverrides.Info(o.RuleOverrides, rules, kept, out var matched);
        if (info.PathOverrides is not null) info.PathOverrideFiles = matched;
        if (!o.IncludeTests)
            foreach (var r in o.RuleOverrides.Where(r => r.Tests))
                info.Notes.Add($"{r.Name} selects test code, but tests are not analyzed (includeTests is off)");
        if (result.NestedSettingsFiles is not { Count: > 0 } nestedFiles) return;
        var repositoryFile = o.ConfigPath ?? Path.Combine(Path.GetFullPath(o.Directory), SettingsFileReader.FileName);
        foreach (var nested in nestedFiles)
            info.Notes.Add($"found {nested}: not applied; use overrides in {repositoryFile}");
    }

    static async Task<int> Analyze(SkarpOptions o, CommandIO io, Action<AnalysisResult, SkarpOptions, bool>? report)
    {
        // Analyze saves its documents in .skarp/last-run/; HEAD and the dirty flag are read while it runs.
        bool save = o.Command == "analyze" && o.SaveLastRun;
        var gitTask = save ? Agent.GitState.ReadAsync(o.Directory, io.GitEnvironment) : null;
        var (result, settings, fingerprint, baselinePath) = await AnalyzeResultAsync(o);
        // Coverage warnings go to standard error in every format; --strict-coverage turns staleness into exit 2.
        if (result.Coverage is { } cov)
        {
            foreach (var w in cov.Warnings) io.Error.WriteLine("skarp coverage: warning: " + Coverage.CoverageBaseline.Describe(w));
            if (o.StrictCoverage && cov.Stale)
            {
                io.Error.WriteLine("skarp: --strict-coverage: the coverage report is stale (see the warnings above); produce it again from the current code");
                return 2;
            }
        }
        bool saved = save && await SaveLastRun(o, io, result, fingerprint, gitTask!);
        var ranking = o.Sort is null ? null : Ranking.WorstCode.Of(o, result);

        // --format sarif writes the SARIF log instead of the JSON document (to --output, else standard output).
        if (o.Format == "sarif") return WriteSarif(io, o, result, settings);
        int exit = result.Gate is { Tripped: true } || SemanticStage.Tripped(result.SemanticTier) ? Gate.TrippedExitCode : 0;
        if (report is not null && o.Command == "analyze")
        {
            report(result, o, saved);
            return exit;
        }
        bool json = o.Format == "json";
        // Primary values are the base view's, merged values alongside (VariantView).
        WriteJson(io, o.Output, json, s => VariantView.Writing(result, () => JsonSerializer.Serialize(s, result, ResultJsonContext.Default.AnalysisResult)));
        var findings = result.Findings ?? [];
        if (o.Command == "baseline")
        {
            int warnings = findings.Count(f => f.Severity == "warning");
            (json ? io.Error : io.Out).WriteLine($"skarp baseline: recorded {findings.Count} findings ({warnings} warning, {findings.Count - warnings} notice) in {baselinePath}");
            if (!json) io.Out.WriteLine("Later runs of skarp analyze mark findings new, known or worsened against it; commit it with the code.");
            (json ? io.Error : io.Out).WriteLine(SemanticStage.BaselineLine(o, result));
            return 0;
        }
        if (json)
        {
            var repo = result.Repository;
            io.Error.WriteLine($"skarp {o.Command}: {repo.Files} files, {repo.Members} members, {findings.Count} findings" +
                (result.Baseline is { } b ? $" ({b.New} new, {b.Worsened} worsened, {b.Known} known, {b.Fixed} fixed)" : "") +
                $"; {result.Run.DurationSeconds:0.000} s, peak working set {result.Run.PeakWorkingSetBytes / (1024 * 1024)} MB, {result.Run.Runtime}");
            if (result.Gate is { } g) io.Error.WriteLine("skarp: " + Gate.Describe(g, LimitText.Describe(g.Health)));
            if (result.Run.Semantic is { } sem) io.Error.WriteLine("skarp semantic: " + SemanticReport.HeaderLine(sem));
        }
        else if (o.Format == "markdown") { CiReports.WriteMarkdown(io.Out, result, o.ReportOptions()); SemanticReport.WriteMarkdown(io.Out, result); }
        else if (o.Format == "github") { CiReports.WriteGitHub(io.Out, result, o.ReportOptions(), ReportLayout.RepositoryPrefix(o.Directory) ?? ""); SemanticReport.WriteGitHub(io.Out, result); }
        else if (ranking is not null) Ranking.RankingText.Write(io.Out, ranking, Ranking.RankingFooter.Cli, saved);
        else if (o.Command == "hotspots") HumanReport.WriteHotspots(io.Out, result, o.ReportOptions());
        else HumanReport.WriteAnalyze(io.Out, result, o.ReportOptions() with { ShowIds = saved });
        return exit;
    }

    /// <summary>Writes .skarp/last-run/; a failure is reported on standard error and never fails the run
    /// (false: not saved).</summary>
    static async Task<bool> SaveLastRun(SkarpOptions o, CommandIO io, AnalysisResult result, string fingerprint, Task<Agent.GitState> gitTask)
    {
        try
        {
            var git = await gitTask;
            Agent.LastRun.Save(o.Directory, result, fingerprint, ToolVersion, git, io.Now, o.Solution);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            io.Error.WriteLine($"skarp: the run was not saved in .skarp/last-run ({ex.Message}); --no-save turns saving off");
            return false;
        }
    }

    /// <summary>
    /// History settings plus the anchor: the commit and C# commit count the baseline file recorded, from which the
    /// run gives the C# commit total; otherwise the note saying why there is none. <c>skarp baseline</c> takes neither
    /// (it records a new anchor).
    /// </summary>
    static HistoryOptions HistoryOptions(SkarpOptions o)
    {
        var h = o.HistoryOptions();
        if (o.Command == "baseline") return h;
        const string Missing = "; C# commit total not given (`skarp baseline` records the count it starts from)";
        var path = Path.GetFullPath(o.BaselineFile());
        if (o.NoBaseline) { h.AnchorMissingNote = "no baseline (--no-baseline)" + Missing; return h; }
        if (!File.Exists(path)) { h.AnchorMissingNote = "no baseline file" + Missing; return h; }
        BaselineFile? file = null;
        try { file = Baseline.Read(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Reported by the baseline comparison of analyze; here it only means no anchor.
        }
        h.Anchor = CommitAnchor.FromRecorded(file?.Commit, file?.CsharpCommits);
        if (h.Anchor is null)
            h.AnchorMissingNote = file is null ? "baseline file not readable" + Missing
                : "the baseline records no commit count (older skarp, history off or a shallow clone)" + Missing;
        return h;
    }

    /// <summary>
    /// <c>analyze --format sarif</c>: the SARIF log of both tiers to <c>--output</c> or standard output; the summary, the
    /// gate and the semantic tier status on standard error; the exit code as for the other formats.
    /// </summary>
    static int WriteSarif(CommandIO io, SkarpOptions o, AnalysisResult result, RuleSettings settings)
    {
        var log = SarifReport.Build(SarifInput(o, result, settings));
        WriteJson(io, o.Output, true, s => JsonSerializer.Serialize(s, log, SarifJsonContext.Default.SarifLog));
        var run = log.Runs[0];
        int semantic = run.Results.Count(r => r.Properties?.Tier == "semantic");
        io.Error.WriteLine($"skarp analyze: {run.Results.Count} SARIF result{(run.Results.Count == 1 ? "" : "s")} ({run.Results.Count - semantic} syntax, {semantic} semantic)" +
            (run.Properties?.ResultsOmitted is int omitted ? $"; {omitted} left out (GitHub code scanning accepts at most {SarifReport.MaxResultsPerRun} per run; warnings kept first)" : "") +
            (o.Output is { } file ? $" in {file}" : ""));
        if (result.Gate is { } g) io.Error.WriteLine("skarp: " + Gate.Describe(g, LimitText.Describe(g.Health)));
        if (result.Run.Semantic is { } sem) io.Error.WriteLine("skarp semantic: " + SemanticReport.HeaderLine(sem));
        return result.Gate is { Tripped: true } || SemanticStage.Tripped(result.SemanticTier) ? Gate.TrippedExitCode : 0;
    }

    /// <summary>The SARIF builder's input: both tiers' findings, the baselines they were compared with, and SRCROOT.</summary>
    static SarifInput SarifInput(SkarpOptions o, AnalysisResult result, RuleSettings settings)
    {
        var full = Path.GetFullPath(o.Directory);
        var root = ReportLayout.RepositoryRoot(full) ?? full;
        var prefix = Path.GetRelativePath(root, full).Replace('\\', '/');
        return new SarifInput
        {
            ToolVersion = ToolVersion,
            SourceRoot = root,
            PathPrefix = prefix == "." ? "" : prefix.TrimEnd('/') + "/",
            Settings = settings,
            SyntaxFindings = result.Findings ?? [],
            SyntaxBaseline = result.Baseline is not null,
            SemanticResults = result.SemanticTier?.Results ?? [],
            SemanticStatus = result.SemanticTier?.Status,
            Files = result.Files,
            Composites = result.Composites,
            Coverage = result.Coverage is not null,
        };
    }

    // JSON goes to the --output file when given; to standard output only with --format json and no file.
    static void WriteJson(CommandIO io, string? output, bool json, Action<Stream> serialize)
    {
        if (output is not null)
        {
            using var stream = File.Create(output);
            serialize(stream);
        }
        else if (json)
        {
            io.Out.Flush();
            using var stdout = io.OpenStdout();
            serialize(stdout);
        }
    }

    static string RuntimeName() => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
        + (System.Runtime.JitInfo.GetCompiledMethodCount() > 0 ? " (JIT)" : " (AOT)");
}
