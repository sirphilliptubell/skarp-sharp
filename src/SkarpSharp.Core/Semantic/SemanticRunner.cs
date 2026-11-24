using System.Diagnostics;
using SkarpSharp.Findings;
using SkarpSharp.Model;

namespace SkarpSharp.Semantic;

/// <summary>Options of the semantic tier (settings <c>semantic.*</c>).</summary>
public sealed class SemanticOptions
{
    /// <summary>Run the tier when the inputs allow; <c>--no-semantic</c> turns it off.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary><c>--restore</c>: run <c>dotnet restore</c> on the solution first.</summary>
    public bool Restore { get; init; }
    /// <summary>Explicit solution (absolute path); null means the automatic choice.</summary>
    public string? Solution { get; init; }
    /// <summary>Run source generators (<c>--no-generators</c>).</summary>
    public bool Generators { get; init; } = true;
    /// <summary>Empty: the highest target framework per project; <c>all</c>; or the frameworks to analyze.</summary>
    public IReadOnlyList<string> Targets { get; init; } = [];

    public string TargetsText => Targets.Count == 0 ? "highest" : string.Join(", ", Targets);
}

/// <summary>
/// What the measure stage needs from the rest of the run: the root, the tier's options, whether the syntax tier kept a
/// file (path relative to the root), the rule settings, the tool version, and the commits per file in the history window
/// (finding order; null without history).
/// </summary>
public sealed record MeasureContext(string Root, SemanticOptions Options, Func<string, bool> KeptFile, RuleSettings Settings, string ToolVersion,
    IReadOnlyDictionary<string, int>? Commits)
{
    /// <summary><c>unusedCode.allow</c> patterns of the settings file.</summary>
    public IReadOnlyList<string> UnusedAllow { get; init; } = [];
    /// <summary>Keep namespace references to types declared only in generated code (experimental).</summary>
    public bool IncludeGeneratedTargets { get; init; }
    /// <summary>The settings of a file (relative path) under the repository's <c>overrides</c>; null:
    /// <see cref="Settings"/> for every file.</summary>
    public Func<string, RuleSettings>? RulesOf { get; init; }
    /// <summary>Every distinct settings the syntax tier judged kept files by (the base first); null: only
    /// <see cref="Settings"/>. The unused-code walk runs when any of them has an unused-code rule on.</summary>
    public IReadOnlyList<RuleSettings>? SettingsInUse { get; init; }
}

/// <summary>Stage 1 of the tier: solution, restore check and compiler arguments (runs while the syntax tier parses).</summary>
public sealed class SemanticAcquisition
{
    public string Status { get; set; } = "ran";
    public string? Reason { get; set; }
    public string? Hint { get; set; }
    public string? Solution { get; set; }
    public string? SolutionReason { get; set; }
    public List<string> Notes { get; } = [];
    public List<RecordedProject> Records { get; } = [];
    /// <summary>C# projects of the solution that gave no compiler arguments (absolute paths).</summary>
    public List<string> FailedProjects { get; } = [];
    public string WorkFolder { get; set; } = "";
    public SemanticTimings Timings { get; } = new();
    /// <summary>Stopwatch timestamp of the tier's start (for its wall time).</summary>
    public long StartedAt { get; } = Stopwatch.GetTimestamp();

    public static SemanticAcquisition Off(string reason) => new() { Status = "off", Reason = reason };

    public SemanticAcquisition Fail(string reason, string? hint)
    {
        Status = "not evaluated";
        Reason = reason;
        Hint = hint;
        return this;
    }
}

/// <summary>The tier's outcome: status and one result per output file (one, or one per target framework).</summary>
public sealed class SemanticOutcome
{
    public required SemanticAcquisition Acquisition { get; init; }
    public List<SemanticResult> Results { get; } = [];
    /// <summary>True when some project was analyzed for more than one framework (one file per framework).</summary>
    public bool PerFramework { get; set; }
    public string Status => Acquisition.Status;
    /// <summary>When the tier did not run: the semantic baseline entries that exist, all not evaluated.</summary>
    public BaselineSummary? NotEvaluatedBaseline { get; set; }
}

/// <summary>
/// The semantic tier: chooses the solution, checks the restore, records compiler
/// arguments with a design-time build, runs source generators, compiles one project at a time and measures the types.
/// When the inputs do not allow it, it says why and the rest of the run is unchanged.
/// </summary>
public static class SemanticRunner
{
    public const int SchemaVersion = 1;

    public static async Task<SemanticAcquisition> AcquireAsync(string root, SemanticOptions options, CancellationToken ct = default)
    {
        root = Path.GetFullPath(root);
        if (!options.Enabled) return SemanticAcquisition.Off("--no-semantic or semantic.enabled false");
        var acq = new SemanticAcquisition();
        var sw = Stopwatch.StartNew();
        IReadOnlyList<string> projects;
        if (options.Solution is { } explicitSolution)
        {
            if (!File.Exists(explicitSolution)) return acq.Fail($"solution file not found: {explicitSolution}", "--solution names a .sln or .slnx file");
            var listing = await SolutionChoice.ListAsync(explicitSolution, ct).ConfigureAwait(false);
            if (listing.Projects is null) return acq.Fail($"{Path.GetFileName(explicitSolution)} could not be listed: {listing.Error}", SolutionChoice.HintFor(listing.Error ?? ""));
            acq.Solution = explicitSolution;
            acq.SolutionReason = "given with --solution or semantic.solution";
            projects = listing.Projects;
        }
        else
        {
            var found = SolutionChoice.Find(root);
            var listings = new List<SolutionListing>();
            foreach (var chunk in found.Chunk(4))
                listings.AddRange(await Task.WhenAll(chunk.Select(f => SolutionChoice.ListAsync(f, ct))).ConfigureAwait(false));
            var decision = SolutionChoice.Choose(root, listings);
            acq.Notes.AddRange(decision.Notes ?? []);
            if (decision.Chosen is null) return acq.Fail(decision.Reason, decision.Hint);
            acq.Solution = decision.Chosen;
            acq.SolutionReason = decision.Reason;
            projects = listings.First(l => l.Path == decision.Chosen).Projects!;
        }
        acq.Timings.SolutionSeconds = Seconds(sw);
        var solutionName = Path.GetRelativePath(root, acq.Solution).Replace('\\', '/');
        if (options.Restore)
        {
            sw.Restart();
            var restore = await DotnetRunner.RunAsync(["restore", acq.Solution, "-nologo"], Path.GetDirectoryName(acq.Solution)!, ct).ConfigureAwait(false);
            acq.Timings.RestoreSeconds = Seconds(sw);
            if (!restore.Succeeded) return acq.Fail($"dotnet restore {solutionName} failed: {restore.ErrorSummary()}", SolutionChoice.HintFor(restore.ErrorSummary()));
        }
        sw.Restart();
        var missing = await DesignTimeBuild.NotRestoredAsync(root, projects, ct).ConfigureAwait(false);
        if (missing.Count > 0)
            return acq.Fail($"not restored: {string.Join(", ", missing.Take(3))}{(missing.Count > 3 ? $" and {missing.Count - 3} more" : "")} (project.assets.json missing)",
                $"run `dotnet restore {solutionName}` first, or add --restore");
        acq.WorkFolder = Path.Combine(Path.GetTempPath(), "skarp-semantic-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(acq.WorkFolder);
        var dtb = await DesignTimeBuild.RunAsync(acq.Solution, projects, acq.WorkFolder, ct).ConfigureAwait(false);
        acq.Timings.DesignTimeBuildSeconds = Seconds(sw);
        acq.Timings.ChildPeakWorkingSetBytes = dtb.PeakWorkingSetBytes;
        acq.Records.AddRange(dtb.Projects);
        acq.Notes.AddRange(dtb.Failures);
        acq.FailedProjects.AddRange(dtb.FailedProjects);
        if (acq.Records.Count == 0)
        {
            Cleanup(acq);
            return acq.Fail("the design-time build gave no compiler arguments" + (dtb.Failures.Count > 0 ? $" ({dtb.Failures[0]})" : ""),
                SolutionChoice.HintFor(dtb.Failures.FirstOrDefault() ?? ""));
        }
        var unrestored = acq.Records.Where(r => r.AssetsFile.Length > 0 && !File.Exists(r.AssetsFile)).Select(r => Path.GetFileName(r.Project)).Distinct().ToList();
        if (unrestored.Count > 0)
        {
            Cleanup(acq);
            return acq.Fail($"not restored: {string.Join(", ", unrestored.Take(3))} (project.assets.json missing)", $"run `dotnet restore {solutionName}` first, or add --restore");
        }
        return acq;
    }

    static double Seconds(Stopwatch sw) => Math.Round(sw.Elapsed.TotalSeconds, 3);

    public static void Cleanup(SemanticAcquisition acq)
    {
        if (acq.WorkFolder.Length == 0 || !Directory.Exists(acq.WorkFolder)) return;
        try { Directory.Delete(acq.WorkFolder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { acq.Notes.Add($"temporary folder not removed: {acq.WorkFolder} ({ex.Message})"); }
    }

    /// <summary>
    /// Stage 2: picks the target frameworks, compiles and measures, and builds the result(s) (one, or one per framework).
    /// </summary>
    public static async Task<SemanticOutcome> MeasureAsync(SemanticAcquisition acq, MeasureContext context, CancellationToken ct = default)
    {
        var (root, options, keptFile) = (Path.GetFullPath(context.Root), context.Options, context.KeptFile);
        var outcome = new SemanticOutcome { Acquisition = acq };
        if (acq.Status != "ran") return outcome;
        try
        {
            var inputs = acq.Records.Select(CompileInput.From).ToList();
            var chosen = ChooseTargets(inputs, options, acq.Notes);
            // Which inputs are walked for references (unused code, namespace cycles): WalkPlan.
            var walk = WalkPlan.For(context, chosen);
            foreach (var i in chosen)
            {
                i.Measure = i.SourcePaths.Any(p => keptFile(Path.GetRelativePath(root, p).Replace('\\', '/')));
                i.Walk = walk.Walks(i);
            }
            outcome.PerFramework = chosen.GroupBy(i => i.Record.Project, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);
            var assemblies = new HashSet<string>(inputs.Select(i => i.AssemblyName), StringComparer.OrdinalIgnoreCase);
            var pipeline = new CompilationPipeline(inputs, options.Generators, acq.WorkFolder);
            var measured = new List<ProjectMeasurement>();
            var facts = new List<ProjectFacts>();
            long walkTicks = 0;
            await pipeline.RunAsync(c =>
            {
                var models = TypeMeasurement.Models(c);
                if (c.Input.Measure) measured.Add(TypeMeasurement.Measure(c, root, keptFile, assemblies, options.Generators, models));
                if (!c.Input.Walk) return;
                var started = Stopwatch.GetTimestamp();
                facts.Add(ReferenceIndex.Extract(c, new ReferenceOptions
                {
                    Root = root, KeptFile = keptFile, SolutionAssemblies = assemblies, IsTestProject = walk.TestProjects.Contains(c.Input.Record.Project),
                    Generators = options.Generators, Models = models, Namespaces = walk.Cycles, Candidates = walk.Unused,
                }));
                walkTicks += Stopwatch.GetTimestamp() - started;
            }, ct).ConfigureAwait(false);
            acq.Timings.GeneratorSeconds = Math.Round(pipeline.Stats.GeneratorMilliseconds / 1000.0, 3);
            acq.Timings.GeneratorProcessSeconds = Math.Round(pipeline.Stats.GeneratorProcessMilliseconds / 1000.0, 3);
            acq.Timings.CompileSeconds = Math.Round(pipeline.Stats.CompileMilliseconds / 1000.0, 3);
            var analysisStarted = Stopwatch.GetTimestamp();
            var analysis = walk.Unused ? UnusedCode.Analyze(facts, UnusedOptions(acq, context, root, inputs, chosen)) : null;
            var namespaces = walk.NamespaceResults(facts, outcome.PerFramework, measured, context.IncludeGeneratedTargets);
            walkTicks += Stopwatch.GetTimestamp() - analysisStarted;
            acq.Timings.ReferenceSeconds = Math.Round(Stopwatch.GetElapsedTime(0, walkTicks).TotalSeconds, 3);
            acq.Timings.MetricsSeconds = Math.Round(Math.Max(0, pipeline.Stats.MeasureMilliseconds / 1000.0 - acq.Timings.ReferenceSeconds), 3);
            acq.Timings.ChildPeakWorkingSetBytes = Math.Max(acq.Timings.ChildPeakWorkingSetBytes, pipeline.Stats.ChildPeakWorkingSetBytes);
            int skipped = chosen.Count(i => !i.Measure);
            if (skipped > 0) acq.Notes.Add($"{skipped} project{(skipped == 1 ? "" : "s")} not measured: no file kept by the file selection (tests and exclude globs)" +
                (walk.Unused ? "; walked for references (unused code)" : ""));
            IEnumerable<(string? Tfm, List<ProjectMeasurement> List)> groups = outcome.PerFramework
                ? measured.GroupBy(m => m.Project.TargetFramework, StringComparer.Ordinal).OrderByDescending(g => TargetFrameworks.Key(g.Key)).Select(g => ((string?)g.Key, g.ToList()))
                : [((string?)null, measured)];
            foreach (var (tfm, list) in groups)
            {
                var verdicts = analysis?.Verdicts.Where(v => tfm is null || v.TargetFramework == tfm).ToList() ?? [];
                outcome.Results.Add(Build(acq, context with { Root = root }, tfm, list, new BuildFacts(verdicts, outcome.Results.Count == 0 ? analysis : null,
                    analysis?.NotEvaluatedPlaces, namespaces(tfm))));
            }
        }
        finally { Cleanup(acq); }
        return outcome;
    }

    /// <summary>The inputs to measure: per project the highest framework (default), every framework (<c>all</c>), or the
    /// frameworks asked for (projects without them are skipped with a note).</summary>
    static List<CompileInput> ChooseTargets(List<CompileInput> inputs, SemanticOptions options, List<string> notes)
    {
        var chosen = new List<CompileInput>();
        foreach (var g in inputs.GroupBy(i => i.Record.Project, StringComparer.OrdinalIgnoreCase))
        {
            if (options.Targets.Count == 0)
                chosen.Add(g.OrderByDescending(i => TargetFrameworks.Key(i.Record.TargetFramework)).First());
            else if (options.Targets.Contains("all", StringComparer.OrdinalIgnoreCase))
                chosen.AddRange(g);
            else
            {
                var picked = g.Where(i => options.Targets.Contains(i.Record.TargetFramework, StringComparer.OrdinalIgnoreCase)).ToList();
                if (picked.Count == 0)
                    notes.Add($"{Path.GetFileName(g.Key)} skipped: it has no target framework {string.Join(" or ", options.Targets)} (it has {string.Join(", ", g.Select(i => i.Record.TargetFramework))})");
                chosen.AddRange(picked);
            }
        }
        return chosen;
    }

    /// <summary>The unused-code analysis inputs besides the facts: every project's frameworks, projects not analyzed, the
    /// allowlist, and the files of the targets not analyzed, from the recorded compiler arguments (no compile).</summary>
    static UnusedCodeOptions UnusedOptions(SemanticAcquisition acq, MeasureContext context, string root, List<CompileInput> inputs, List<CompileInput> chosen)
    {
        var chosenSet = chosen.ToHashSet();
        var recorded = inputs.Select(i => (i.Record.Project, i.Record.TargetFramework, i.SourcePaths, Analyzed: chosenSet.Contains(i))).ToList();
        return new UnusedCodeOptions
        {
            ProjectFrameworks = acq.Records.GroupBy(r => r.Project, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(r => r.TargetFramework)], StringComparer.OrdinalIgnoreCase),
            FailedAssemblies = [.. acq.FailedProjects.Select(AssemblyNameOf), .. ProjectsWithoutInputs(inputs, chosen)],
            Allow = context.UnusedAllow, Root = root,
            UnanalyzedOnlyFiles = UnusedCode.FilesOnlyInUnanalyzedTargets(recorded),
            UnanalyzedCompiledFiles = UnusedCode.FilesInUnanalyzedTargets(recorded, root),
        };
    }

    /// <summary>
    /// Which chosen inputs the reference walk visits: with an unused-code rule on every one (test projects included);
    /// with only namespace-cycle on the measured non-test ones.
    /// </summary>
    internal sealed record WalkPlan(bool Unused, bool Cycles, HashSet<string> TestProjects)
    {
        /// <remarks>Whether code is used is decided over every input whatever folder asks, so a rule switched on by
        /// one override runs the whole walk; namespace-cycle stays one setting per run.</remarks>
        public static WalkPlan For(MeasureContext context, List<CompileInput> chosen) => new((context.SettingsInUse ?? [context.Settings]).Any(UnusedRulesOn),
            (context.Settings[RuleIds.NamespaceCycle].Warning ?? context.Settings[RuleIds.NamespaceCycle].Notice) is not null,
            new HashSet<string>(chosen.Select(i => i.Record.Project).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(p => Discovery.TestDetection.TestProjectReason(p) is not null), StringComparer.OrdinalIgnoreCase));

        public bool Walks(CompileInput i) => Unused || (Cycles && i.Measure && !TestProjects.Contains(i.Record.Project));

        /// <summary>The namespace summary of each output file (by its target framework, null for the single file); null for
        /// every file when the rule is off. Computed here, once per file.</summary>
        public Func<string?, NamespaceSummary?> NamespaceResults(List<ProjectFacts> facts, bool perFramework, List<ProjectMeasurement> measured, bool keepGeneratedTargets)
        {
            if (!Cycles) return _ => null;
            if (!perFramework)
            {
                var single = NamespaceCycles.Analyze(facts, keepGeneratedTargets);
                return _ => single;
            }
            var byFramework = measured.Select(m => m.Project.TargetFramework).Distinct(StringComparer.Ordinal)
                .ToDictionary(t => t, t => NamespaceCycles.Analyze(facts.Where(f => f.TargetFramework == t), keepGeneratedTargets), StringComparer.Ordinal);
            return t => t is null ? null : byFramework.GetValueOrDefault(t);
        }
    }

    /// <summary>True when one of the unused-code rules is on.</summary>
    internal static bool UnusedRulesOn(RuleSettings settings) =>
        settings[RuleIds.UnusedMember].Notice is not null || settings[RuleIds.UnusedType].Notice is not null || settings[RuleIds.UsedOnlyByTests].Notice is not null
        || settings[RuleIds.UnreadMember].Notice is not null;

    /// <summary>The assembly name of a project that gave no compiler arguments: its AssemblyName property, else its file name.</summary>
    static string AssemblyNameOf(string project)
    {
        try
        {
            var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(project), "<AssemblyName>([^<$]+)</AssemblyName>",
                System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
            if (m.Success) return m.Groups[1].Value.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Path.GetFileNameWithoutExtension(project);
    }

    /// <summary>Assemblies of projects none of whose frameworks was chosen (--target): their references are unknown.</summary>
    static IEnumerable<string> ProjectsWithoutInputs(List<CompileInput> inputs, List<CompileInput> chosen)
    {
        var walked = new HashSet<string>(chosen.Select(i => i.Record.Project), StringComparer.OrdinalIgnoreCase);
        return inputs.Where(i => !walked.Contains(i.Record.Project)).Select(i => i.AssemblyName).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>What one output file gets from the reference walk: its unused-code verdicts, the analysis summary (first file
    /// only), the places not evaluated, and its namespace summary.</summary>
    sealed record BuildFacts(List<UnusedVerdict> Verdicts, UnusedCodeAnalysis? Analysis, IReadOnlySet<string>? NotEvaluatedPlaces, NamespaceSummary? Namespaces);

    static SemanticResult Build(SemanticAcquisition acq, MeasureContext context, string? tfm, List<ProjectMeasurement> list, BuildFacts facts)
    {
        var (root, options, settings) = (context.Root, context.Options, context.Settings);
        var (verdicts, analysis, notEvaluatedPlaces, namespaces) = facts;
        var result = new SemanticResult
        {
            SchemaVersion = SchemaVersion,
            Run = new SemanticRunInfo
            {
                ToolVersion = context.ToolVersion, Root = root, Status = acq.Status,
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + (System.Runtime.JitInfo.GetCompiledMethodCount() > 0 ? " (JIT)" : " (AOT)"),
                Solution = acq.Solution is null ? null : Path.GetRelativePath(root, acq.Solution).Replace('\\', '/'),
                SolutionReason = acq.SolutionReason, Targets = options.TargetsText, TargetFramework = tfm, Generators = options.Generators,
                Findings = settings.ToSemanticInfo(), Timings = acq.Timings,
            },
            Projects = [.. list.Select(m => m.Project).OrderBy(p => p.Path, StringComparer.Ordinal)],
            Types = [.. list.SelectMany(m => m.Types).OrderBy(t => t.Path, StringComparer.Ordinal).ThenBy(t => t.Line).ThenBy(t => t.Name, StringComparer.Ordinal)],
            NotEvaluated = [.. list.SelectMany(m => m.NotEvaluated).OrderBy(t => t.Path, StringComparer.Ordinal).ThenBy(t => t.Line).ThenBy(t => t.Name, StringComparer.Ordinal)],
        };
        result.Run.Notes.Add("class coupling (cbo) and ATFD exclude .NET framework types (System.*); cboAll and atfdAll count them; cboMicrosoft follows Microsoft's rules; atfd counts distinct foreign attributes over the whole type (God Class uses it), atfdMemberSum sums the per-member counts");
        result.Run.Notes.AddRange(acq.Notes);
        result.NotEvaluatedReasons = [.. result.NotEvaluated.GroupBy(t => t.Reason).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new NotEvaluatedCount { Reason = g.Key, Types = g.Count() })];
        Afferent(result.Types);
        result.UnusedCode = analysis?.Summary;
        if (notEvaluatedPlaces is not null) result.UnusedNotEvaluatedPlaces = notEvaluatedPlaces;
        result.Namespaces = namespaces;
        SemanticFindings.Apply(result, settings, context.Commits, verdicts, context.RulesOf);
        return result;
    }

    /// <summary>Afferent coupling: for every measured type, the other measured types whose coupling set holds it.</summary>
    internal static void Afferent(List<SemanticType> types)
    {
        var byKey = types.GroupBy(t => t.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var s in types)
            foreach (var k in s.CoupledKeys)
                if (k != s.Key && byKey.TryGetValue(k, out var targets))
                    foreach (var t in targets) t.Ca++;
    }
}
