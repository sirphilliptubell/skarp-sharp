using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SkarpSharp.Findings;

namespace SkarpSharp.Configuration;

/// <summary>Result of parsing a command line: options, or a message and exit code (0 for help, 2 for usage errors).</summary>
public sealed class ParseResult
{
    public SkarpOptions? Options { get; init; }
    public string? Error { get; init; }
    public int ExitCode { get; init; }
    /// <summary>Print the usage text (help, or after an unknown option).</summary>
    public bool ShowUsage { get; init; }
    /// <summary><c>skarp explain</c>: the rule asked for (null lists the rules).</summary>
    public string? ExplainRule { get; init; }
    public bool Explain { get; init; }
}

/// <summary>
/// Command-line parsing: the command, its directory and the flags, applied over the
/// settings file over the defaults (default &lt; file &lt; flag).
/// </summary>
public static class CommandLine
{
    public static readonly IReadOnlyList<string> Commands = ["analyze", "hotspots", "history", "baseline", "config"];

    public const string Usage = """
        skarp: C# code health (metrics, git history, findings, baseline).

        Usage:
          skarp analyze  <directory> [options]     findings report (top 20 places, most active files first) plus all metrics in JSON
          skarp hotspots <directory> [options]     activity hotspot table (--detail defaults to files)
          skarp history  <directory> [options]     git history only
          skarp baseline <directory> [options]     record the current findings in .skarp-baseline.json (later runs mark new ones)
          skarp config   <directory> [options]     print the effective settings and where each came from
          skarp setup    [<level>|command|claude|git]  edit the system, personal or repository settings in a menu;
                                                   --level <level> --set <key>=<value> without one; command: build a
                                                   command line for one run; claude, git: hooks (skarp setup --help)
          skarp explain  [<rule>]                  explain a rule and its thresholds per preset; without a rule: list rules and presets
          skarp check    <file>... [options]       experimental: the findings of files just written, for an AI agent's hook
                                                   (skarp check --help)
          skarp show     [<handle>] [options]      experimental: findings of the saved last run or the baseline, without
                                                   analyzing again (skarp show --help)
          skarp install  claude|git-hooks [options]  experimental: Skarp's hooks and MCP server for Claude Code, or
                                                   hooks for git (each commit); --remove, --check (skarp install --help)
          skarp mcp                                experimental: Skarp's MCP server for AI agents over standard input
                                                   and output: find_worst_code, check, findings and explain (skarp mcp --help)
          skarp --version                          print the version

        Settings levels, lowest first, each overriding the ones before: skarp's defaults, the system file
        (%ProgramData%\skarp\settings.json; /etc/skarp/settings.json; macOS /Library/Application Support/skarp/settings.json),
        the personal file (~/.skarp/settings.json; SKARP_HOME replaces ~/.skarp), the repository's .skarp.json (in the
        directory or a parent up to the git work tree root), then the flags. Objects merge key by key, lists replace unless
        the file sets "<name>Mode": "append", null resets a key to skarp's default. skarp config shows where each value
        came from.
          --config <file>        read this file as the repository level (also SKARP_CONFIG=<file>; the flag wins)
          --no-config            read no settings file at all
          --no-user-config       skip the system and personal files (also SKARP_NO_USER_CONFIG=1)
          --gate-user-config     with --gate: apply the system and personal files anyway (the gate skips them by default)
          --setting <key>=<value>  experimental: any run setting by its settings-file key (repeatable; split at the first
                                 =): history.window=6, exclude=gen/**,obj/** (a list, comma-separated), excludeMode=append,
                                 exclude=[] (empty), top=null (Skarp's default). One level above the files, below the
                                 dedicated flags, which win; not overrides, useInRepositories, root or version; only keys
                                 the command uses (skarp config shows the origin "command line")
          --origins              config: also show the file of each value and the values it overrode
                                 (config --format json gives the same for tools)
          --file <path>          config: one file's effective rules and thresholds, each with its origin (a level or
                                 "override N"), and whether Skarp treats it as test code

        Per-path settings: the repository's .skarp.json may hold "overrides", an ordered list of entries. Each
        selects files with "files" (globs relative to the file; "src/Legacy/**" for a folder), optional "excludeFiles",
        and "tests": true (test code as Skarp detects it); both must hold when both are given. Its "rules" and
        "thresholds" (the same grammar as at the top level) apply to the matched files: after the repository's own
        values, in file order (later wins key by key, null resets to the default), with the flags on top. Everything
        else stays one setting per run; duplicate-code, duplicate-code-near and namespace-cycle cannot vary, and
        active-complex-file can only be switched off. skarp config lists the entries and the files each matches.

        Output:
          --format text|json|markdown|github|sarif
                                 the text format (default: for people and AI agents to read; it may change in any
                                 release), the JSON document (for tools), a Markdown summary (for $GITHUB_STEP_SUMMARY),
                                 GitHub workflow commands (annotations for new and worsened findings) or SARIF 2.1.0
                                 (every finding of both tiers, for GitHub code scanning and SARIF viewers); the last
                                 three analyze only
          --verbose              analyze: also the run details (files, history, thresholds, settings, semantic tier,
                                 timing), the health block, each rule's one-line explanation and the tables
          --no-save              analyze: do not save the run in .skarp/last-run/ (skarp show reads it; default: saved)
          --output, -o <file>    also write the JSON document to a file (with --format json or sarif: only that document, only there)
          --top <n>              places shown and rows in the hotspot and activity tables (default 20)
          --per-file <n>         places shown per file before "+N more in this file" (default 3; 0: no cap)
          --sort health|hotspot|<rule>  analyze: a worst-first ranking instead of the top places, --top entries long:
                                 health (files with findings by score), hotspot (files with findings by commits in the
                                 history window, then complexity; by health without git history) or a rule's own
                                 measure (complex-method, deep-nesting, long-method, large-type, large-file,
                                 too-many-parameters, path-explosion, bumpy-road, complex-condition; code under every
                                 threshold ranks too); with --format text or json (JSON adds the ranking)
          --unit auto|files|methods  what --sort ranks (default auto: files; methods for a method rule); files with a
                                 method rule ranks each file by its worst method; methods needs a method rule
          --all                  show every place and finding (magic numbers included; no caps; every copy of a clone group)
          --show-suppressed      experimental: also list the findings an inline [SuppressMessage("skarp", ...)] left out
          --min-severity notice|warning   findings shown (JSON always holds all findings)
          --group file|rule      one entry per place, grouped by file (default), or the findings grouped by rule
          --detail files|members JSON detail (default members for analyze, files for hotspots)

        Baseline:
          --baseline <file>      baseline file to write or compare with (default .skarp-baseline.json in the directory)
          --no-baseline          do not compare with the baseline file
          --new-only             show only findings that are new or worse than in the baseline

        Gate (analyze only; off by default):
          --gate                 exit code 3 when findings in the gate set are new or worsened against the baseline
                                 (needs a baseline file); --no-gate turns a gate from the settings file off
          --gate-on <rule>:<severity>   gate set (repeatable; replaces the settings file's set): a rule id or *, and
                                 notice, warning or *; default *:warning
          The gate also judges each file's health score against the baseline (whatever the gate set), with three
          limits (settings gate.health.maxDrop, newFileMin, floor; 0 turns a limit off):
          --gate-health-drop <n>     a file may not lose n or more (default 1.0; 0 to 9)
          --gate-health-new-min <n>  a new file must score at least n (default 6.5; 0 to 10)
          --gate-health-floor <n>    a file whose score fell may not end below n (default 6.5; 0 to 10); a file that
                                 did not get worse never blocks, and the project average never gates
          --push-check          experimental, last: the git pre-push hook (skarp install git-hooks --pre-push)

        Findings:
          --preset <id>          threshold preset: default (composite), codescene, clean-cadet, doomen, reportgenerator,
                                 ndepend, pmd, checkstyle, microsoft, jscpd, alves-percentile
          --complex-metric cognitive|cyclomatic   metric of the complex-method rule (default cognitive, whatever the preset)
          --long-method-lines physical|source     line unit of the long-method rule (default physical: the whole declaration)
          --threshold <rule>.<level>=<value>      override a threshold (level notice or warning; value a number or off);
                                 <rule>.<level>.<metric>=<value> for rules with several metrics; <rule>.<parameter>=<value>
          --rule <rule>=on|off   switch a rule on or off (brain-class is experimental and off by default)
          --record-parameters exempt|check        positional record parameters and too-many-parameters (default exempt)
          --magic-numbers count|list              magic numbers in the top list (default count: counted, not listed)

        Files:
          --include-generated    include generated files in metrics and findings
          --include-tests        analyze test files (detected automatically and excluded by default); --no-include-tests
          --include <glob>       bring back files that test detection left out (repeatable)
          --exclude <glob>       leave files or folders out (repeatable; wins over --include)
          --exclude-tests-why    list the files left out and why
          --parallelism <n>      parse threads (default: processor count)

        History (used when the directory is inside a git work tree; otherwise a note is printed):
          --no-history           analyze, hotspots and baseline: no history (--history turns it back on)
          --since <date>         window start (committer date); default: 12 months before HEAD's committer date
          --window <months>      window length ending at HEAD's committer date (default 12)
          --all-history          whole history
          --ignore-whitespace, -w  ignore whitespace changes in line counts (--no-ignore-whitespace)
          --bulk-flag <n>        list commits touching more than n C# files (default 300)
          --bulk-drop <n>        drop commits touching more than n C# files (default: keep all)
          --include-bots         count commits by bots (excluded by default; --no-include-bots)
          --bot-pattern <regex>  extra bot pattern on author name or email (repeatable; adds to the defaults)

        Duplicate code: duplicate-code reports copied code (exact or renamed) once per clone group, across
        files; --threshold duplicate-code.notice.tokens=<n> and .lines=<n> set the minimum size. Inline suppression for
        every rule: [SuppressMessage("skarp", "<rule>", Justification = "<why>")] on a member or type (skarp explain
        suppression-without-justification).

        Findings are places worth a look, not violations; without --gate the exit code never depends on them.
        Semantic tier (analyze and baseline; runs when the repository's inputs allow, never restores or builds by default):
          --no-semantic          syntax only (--semantic turns it back on); the tier runs design-time MSBuild of the repository
          --restore              run dotnet restore on the solution first (implies the semantic tier)
          --solution <file>      the .sln or .slnx to use (default: chosen automatically)
          --no-generators        skip source generators (types that need generated code are not evaluated); --generators
          --all-targets          every target framework of each project (default: the highest per project)
          --target <tfm>         this target framework (repeatable)
          --semantic-output <file>  semantic JSON file (default: next to --output, as <name>.semantic.json)
          --include-generated-targets  namespace-cycle: keep references to types declared only in generated code

        Coverage (analyze and baseline; skarp never runs tests, it reads the reports you produce):
          --coverage <file>      a Cobertura coverage report (Coverlet or Microsoft code coverage; repeatable, merged per
                                 line); replaces the settings file's "coverage" globs. Adds covered and coverable lines per
                                 member, file and project to the JSON and turns on untested-complex-method (notice, score
                                 weight 0, not in the default gate); without a report that rule is not evaluated
          --strict-coverage      a stale report (covered lines on blank or declaration lines, files gone) fails the run
                                 (exit code 2) instead of a warning

        Code health (experimental; --gate judges each file's score with the health limits above):
          every analyzed file gets a score from 1 to 10 (10 = healthy) lowered by its findings with documented weights
          per rule and severity (docs/health-score.md); the reports show the size-weighted average, the hotspot
          average and the worst files; `skarp show --file <path>` lists where a file's points went
          --score-model <name>   the score model (default and only value: grade); settings: score.decay,
                                 score.weights.<rule>.notice|warning|cap|decay, score.worst.count, score.worst.below

        Exit codes: 0 done (gate passed or off), 1 tool error, 2 usage error (also --gate without a baseline file, a
        coverage report that is not Cobertura, and with --strict-coverage a stale report),
        3 gate tripped (new or worsened findings in the gate set, syntax or semantic, or a file past a health limit).
        """;

    // Option name -> takes a value, allowed for the history command (options of the syntax tier are not).
    sealed record Flag(bool Value, bool History = true);

    static readonly Dictionary<string, Flag> Flags = new(StringComparer.Ordinal)
    {
        ["--output"] = new(true), ["-o"] = new(true), ["--format"] = new(true), ["--top"] = new(true),
        ["--all"] = new(false, false), ["--min-severity"] = new(true, false), ["--group"] = new(true, false), ["--detail"] = new(true, false),
        ["--exclude-tests-why"] = new(false), ["--show-suppressed"] = new(false, false),
        ["--baseline"] = new(true, false), ["--no-baseline"] = new(false, false), ["--new-only"] = new(false, false),
        ["--preset"] = new(true, false), ["--complex-metric"] = new(true, false), ["--long-method-lines"] = new(true, false),
        ["--threshold"] = new(true, false), ["--rule"] = new(true, false), ["--record-parameters"] = new(true, false),
        ["--magic-numbers"] = new(true, false),
        ["--include-generated"] = new(false, false), ["--include-tests"] = new(false), ["--no-include-tests"] = new(false),
        ["--include"] = new(true), ["--exclude"] = new(true), ["--parallelism"] = new(true, false),
        ["--no-history"] = new(false, false), ["--history"] = new(false, false),
        ["--since"] = new(true), ["--window"] = new(true), ["--all-history"] = new(false),
        ["--ignore-whitespace"] = new(false), ["-w"] = new(false), ["--no-ignore-whitespace"] = new(false),
        ["--bulk-flag"] = new(true), ["--bulk-drop"] = new(true),
        ["--include-bots"] = new(false), ["--no-include-bots"] = new(false), ["--bot-pattern"] = new(true),
        ["--config"] = new(true), ["--no-config"] = new(false), ["--no-user-config"] = new(false), ["--gate-user-config"] = new(false, false),
        ["--origins"] = new(false, false), ["--file"] = new(true, false),
        ["--per-file"] = new(true, false), ["--sort"] = new(true, false), ["--unit"] = new(true, false),
        ["--gate"] = new(false, false), ["--no-gate"] = new(false, false), ["--gate-on"] = new(true, false),
        ["--gate-health-drop"] = new(true, false), ["--gate-health-new-min"] = new(true, false), ["--gate-health-floor"] = new(true, false),
        ["--semantic"] = new(false, false), ["--no-semantic"] = new(false, false), ["--restore"] = new(false, false), ["--solution"] = new(true, false),
        ["--generators"] = new(false, false), ["--no-generators"] = new(false, false), ["--all-targets"] = new(false, false), ["--target"] = new(true, false),
        ["--semantic-output"] = new(true, false), ["--include-generated-targets"] = new(false, false),
        ["--no-save"] = new(false, false), ["--verbose"] = new(false, false),
        ["--score-model"] = new(true, false),
        ["--coverage"] = new(true, false), ["--strict-coverage"] = new(false, false),
        ["--setting"] = new(true),
    };

    /// <summary>The option names of the parser (the completeness test classifies each one).</summary>
    public static IEnumerable<string> FlagNames => Flags.Keys;

    /// <summary>Whether <paramref name="name"/> takes a value.</summary>
    public static bool TakesValue(string name) => Flags.TryGetValue(name, out var f) && f.Value;

    static ParseResult Fail(string message, bool usage = false) => new() { Error = message, ExitCode = 2, ShowUsage = usage };

    public static ParseResult Parse(IReadOnlyList<string> args) => Parse(args, null);

    /// <param name="locations">The system and personal files and the environment (tests); default <see cref="SettingsLocations.Default"/>.</param>
    public static ParseResult Parse(IReadOnlyList<string> args, SettingsLocations? locations)
    {
        if (args.Count == 0 || args[0] is "-h" or "--help" or "help") return new() { ShowUsage = true, ExitCode = 0 };
        if (args[0] == "explain")
        {
            if (args.Count > 2) return Fail("explain takes at most one rule id");
            return new() { Explain = true, ExplainRule = args.Count == 2 ? args[1] : null };
        }
        if (!Commands.Contains(args[0])) return Fail($"Unknown command: {args[0]}", usage: true);
        if (args is [_, "-h" or "--help"]) return new() { ShowUsage = true, ExitCode = 0 };
        if (args.Count < 2|| args[1].StartsWith('-')) return Fail($"{args[0]}: missing <directory>", usage: true);
        string command = args[0];

        // 1. Tokens: flags with their values, checked for arity and command.
        var flags = new List<(string Name, string? Value)>();
        for (int i = 2; i < args.Count; i++)
        {
            var name = args[i];
            if (!Flags.TryGetValue(name, out var flag)) return Fail($"Unknown option: {name}", usage: true);
            if (command == "history" && !flag.History) return Fail($"Option {name} does not apply to the history command", usage: true);
            string? value = null;
            if (flag.Value)
            {
                if (i + 1 >= args.Count) return Fail($"Option {name} needs a value", usage: true);
                value = args[++i];
            }
            flags.Add((name, value));
        }

        var o = new SkarpOptions { Command = command, Directory = args[1], Detail = command == "hotspots" ? "files" : "members" };

        // 2. Settings levels: system < personal < repository, merged per the registry, then the flags.
        if (Request(command, o.Directory, flags, locations, out var settingError) is not { } request) return Fail(settingError!);
        try
        {
            var layered = SettingsLevels.Resolve(request);
            o.Keep(layered);
            if (layered.Merged is { } merged)
            {
                try { ApplyAll(o, merged, layered.MergedPath!); }
                catch (SettingsException ex) { return Fail($"skarp: {(layered.Levels.LastOrDefault(l => l.Applied && l.Path == layered.MergedPath) is { } at ? SettingsLevels.Label(at.Level) : "--setting:")} {ex.Message}"); }
            }
        }
        catch (LevelException ex) { return Fail(ex.Message); }

        // 3. Flags.
        foreach (var (name, value) in flags)
            if ((SemanticCommandLine.ApplyFlag(o, name, value) ?? ScoreCommandLine.ApplyFlag(o, name, value) ?? ApplyAgentFlag(o, name, value) ?? ApplyHealthFlag(o, name, value) ?? ApplyFlag(o, name, value)) is { } error) return Fail(error, usage: error.StartsWith("Invalid value", StringComparison.Ordinal));
        if (SemanticCommandLine.Check(o, flags.Select(f => f.Name).ToList()) is { } semanticError) return Fail(semanticError, usage: true);

        if ((o.Origins ? "--origins" : o.ConfigFile is not null ? "--file" : null) is { } only && command != "config") return Fail($"{only} applies to the config command only");
        if (o.Format is "markdown" or "github" or "sarif" && command != "analyze")
            return Fail($"--format {o.Format} applies to the analyze command only");
        if (SortError(o) is { } sortError) return Fail(sortError);

        // 4. Thresholds are checked once, after the file and flags combined.
        if (command != "history")
        {
            try { o.ValidateRuleSettings(); }
            catch (ArgumentException ex) { return Fail($"skarp: {ex.Message}"); }
        }
        return new() { Options = o };
    }

    /// <summary>What the level resolver needs from the flags, including the --setting level (null and an error
    /// when a value is rejected) and its gate.enabled when no --gate or --no-gate is given.</summary>
    static LevelRequest? Request(string command, string directory, List<(string Name, string? Value)> flags, SettingsLocations? locations, out string? error)
    {
        error = null;
        CommandLineLevel? settingLevel = null;
        if (flags.Any(f => f.Name == "--setting"))
        {
            settingLevel = CommandLineSettings.Build(command, [.. flags.Where(f => f.Name == "--setting").Select(f => f.Value!)], directory, out error);
            if (settingLevel is null) return null;
        }
        var gateFlag = flags.LastOrDefault(f => f.Name is "--gate" or "--no-gate").Name is { } g ? g == "--gate"
            : settingLevel?.Tree["gate"]?["enabled"] is JsonValue ge && ge.TryGetValue<bool>(out var gateSetting) ? gateSetting : (bool?)null;
        return new LevelRequest(directory, flags.LastOrDefault(f => f.Name == "--config").Value, flags.Any(f => f.Name == "--no-config"),
            flags.Any(f => f.Name == "--no-user-config"), gateFlag, flags.Any(f => f.Name == "--gate-user-config"), locations ?? SettingsLocations.Default, settingLevel);
    }

    /// <summary>Applies merged settings (every part: core, semantic, score); <paramref name="path"/> names the file in errors
    /// and anchors <c>coverage</c> and <c>semantic.solution</c>.</summary>
    static void ApplyAll(SkarpOptions o, SettingsFile f, string path)
    {
        ApplyFile(o, f, path);
        SemanticCommandLine.ApplyFile(o, f, path);
        ScoreCommandLine.ApplyFile(o, f, path);
    }

    /// <summary>Checks one level's file on its own, so an invalid value names that file.</summary>
    internal static void Validate(SettingsFile f, string path, string directory) => ApplyAll(new SkarpOptions { Directory = directory }, f, path);

    /// <summary>Checks the <c>rules</c> and <c>thresholds</c> of one override entry (as a settings file of their own).</summary>
    internal static void ValidateRules(SettingsFile f, string path)
    {
        var scratch = new SkarpOptions();
        ApplyFileRules(scratch, f, path);
        ApplyFileThresholds(scratch, f, path);
    }

    static void ApplyFile(SkarpOptions o, SettingsFile f, string path)
    {
        ApplyFileChoices(o, f, path);
        ApplyFileRules(o, f, path);
        ApplyFileThresholds(o, f, path);
        ApplyFileScope(o, f, path);
        ApplyFileReport(o, f, path);
        if (f.Gate is { } gate) ApplyFileGate(o, gate, path);
        if (f.History is { } h) ApplyFileHistory(o, h, path);
        ApplyFileAgent(o, f, path);
        ApplyFileVariants(o, f, path);
        if (f.Coverage is { } cov)
        {
            o._coverageGlobs.AddRange(NonEmpty(cov, $"{path}: coverage: empty glob"));
            o.CoverageGlobBase = Path.GetDirectoryName(Path.GetFullPath(path));
           
        }
    }

    /// <summary><c>saveLastRun</c>, <c>output.*</c> and <c>tracking.*</c>.</summary>
    static void ApplyFileAgent(SkarpOptions o, SettingsFile f, string path)
    {
        if (f.SaveLastRun is bool save) { o.SaveLastRun = save; }
        if (f.Output is { } output) ApplyOutputLines(o, output, path);
        if (f.Tracking?.RetentionDays is int rd) { o.TrackingRetentionDays = Positive(path, "tracking.retentionDays", rd); }
        if (f.Tracking?.LookbackDays is int lb) { o.TrackingLookbackDays = NotNegative(path, "tracking.lookbackDays", lb); }
    }

    /// <summary>The finding-line budgets <c>output.*</c> (<c>output.gateMaxLines</c>: 1 to 1,000).</summary>
    static void ApplyOutputLines(SkarpOptions o, OutputSettings a, string path)
    {
        if (a.CheckMaxLines is int cml) { o.CheckMaxLines = Positive(path, "output.checkMaxLines", cml); }
        if (a.GateMaxLines is int gml) { o.GateMaxLines = AtMost(path, "output.gateMaxLines", Positive(path, "output.gateMaxLines", gml), SkarpOptions.MaxGateMaxLines); }
    }

    static int AtMost(string path, string key, int value, int max) =>
        value <= max ? value : throw FileError(path, key, $"must be at most {max}");

    /// <summary><c>symbols</c>, <c>variants.cap</c> and <c>variants.checkCap</c>.</summary>
    static void ApplyFileVariants(SkarpOptions o, SettingsFile f, string path)
    {
        var v = o.Variants;
        var symbols = v.Symbols;
        if (f.Symbols is { } list)
        {
            foreach (var s in list)
                if (s is null || s.Length == 0 || !(char.IsLetter(s[0]) || s[0] == '_') || !s.All(c => char.IsLetterOrDigit(c) || c == '_'))
                    throw FileError(path, "symbols", $"'{s}' is not a preprocessor symbol (letters, digits and underscores, not starting with a digit)");
            symbols = [.. list.Distinct(StringComparer.Ordinal)];
           
        }
        int cap = v.Cap, checkCap = v.CheckCap;
        if (f.Variants?.Cap is int c) { cap = c >= 0 ? c : throw FileError(path, "variants.cap", "must be 0 or more"); }
        if (f.Variants?.CheckCap is int cc) { checkCap = Positive(path, "variants.checkCap", cc); }
        o.Variants = new Analysis.VariantSettings { Symbols = symbols, Cap = cap, CheckCap = checkCap };
    }

    static int Positive(string path, string key, int value) =>
        value >= 1 ? value : throw FileError(path, key, "must be 1 or more");

    static SettingsException FileError(string path, string key, string message) => new($"{path}: {key}: {message}");

    /// <summary>A setting with two allowed words: the value when it is one of them, else a settings error.</summary>
    static string Choice(string path, string key, string value, (string First, string Second) allowed) =>
        value == allowed.First || value == allowed.Second ? value : throw FileError(path, key, $"'{value}' is not {allowed.First} or {allowed.Second}");

    /// <summary>The preset and the settings that pick one of two words.</summary>
    static void ApplyFileChoices(SkarpOptions o, SettingsFile f, string path)
    {
        if (f.Preset is { } preset)
        {
            if (Presets.Find(preset) is null) throw FileError(path, "preset", $"unknown preset '{preset}' (known: {string.Join(", ", Presets.All.Select(p => p.Id))})");
            o.Preset = preset;
        }
        if (f.ComplexMetric is { } cm) { o.ComplexMetric = Choice(path, "complexMetric", cm, ("cognitive", "cyclomatic")); }
        if (f.LongMethodLines is { } lml) { o.LongMethodLines = Choice(path, "longMethodLines", lml, ("physical", "source")); }
        if (f.RecordParameters is { } rp) { o.CheckRecordParameters = Choice(path, "recordParameters", rp, ("exempt", "check")) == "check"; }
        if (f.MagicNumbers is { } mn) { o.ListMagicNumbers = Choice(path, "magicNumbers", mn, ("count", "list")) == "list"; }
    }

    static string KnownRules() => $"unknown rule (known: {string.Join(", ", RuleCatalog.All.Select(r => r.Id))})";

    static void ApplyFileRules(SkarpOptions o, SettingsFile f, string path)
    {
        if (f.Rules is null) return;
        foreach (var (rule, on) in f.Rules)
        {
            if (RuleCatalog.Find(rule) is null) throw FileError(path, $"rules.{rule}", KnownRules());
            o._rules[rule] = on;
        }
    }

    static void ApplyFileThresholds(SkarpOptions o, SettingsFile f, string path)
    {
        foreach (var (text, key) in SettingsFileReader.ThresholdOverrides(f, path))
        {
            var rule = text[..text.IndexOf('.')];
            if (RuleCatalog.Find(rule) is null) throw FileError(path, $"thresholds.{rule}", KnownRules());
            try { RuleSettings.Check(text); }
            catch (ArgumentException ex) { throw FileError(path, key, ex.Message); }
            o._thresholds.Add(text);
        }
    }

    /// <summary>Test files and the include and exclude globs.</summary>
    static void ApplyFileScope(SkarpOptions o, SettingsFile f, string path)
    {
        if (f.IncludeTests is bool it) { o.IncludeTests = it; }
        // The globs arrive rebased onto the analyzed directory (SettingsLevels, per level) and merged.
        if (f.Include is { } inc) o._include.AddRange(NonEmpty(inc, $"{path}: include: empty glob"));
        if (f.Exclude is { } exc) o._exclude.AddRange(NonEmpty(exc, $"{path}: exclude: empty glob"));
    }

    static void ApplyFileReport(SkarpOptions o, SettingsFile f, string path)
    {
        if (f.Top is int top)
        {
            if (top < 0) throw FileError(path, "top", "must be 0 or more");
            o.Top = top;
        }
        if (f.Report is { } rep && rep.PerFile is int perFile)
        {
            if (perFile < 0) throw FileError(path, "report.perFile", "must be 0 or more (0: no cap)");
            o.PerFile = perFile;
        }
    }

    static void ApplyFileGate(SkarpOptions o, GateSettings gate, string path)
    {
        if (gate.Enabled is bool ge) { o.Gate = ge; }
        if (gate.Health is { } h)
            o.HealthLimits = o.HealthLimits.With(
                HealthLimitValue(path, "gate.health.maxDrop", h.MaxDrop, Health.HealthLimits.MaxDropLimit),
                HealthLimitValue(path, "gate.health.newFileMin", h.NewFileMin, Health.HealthLimits.MaxScoreLimit),
                HealthLimitValue(path, "gate.health.floor", h.Floor, Health.HealthLimits.MaxScoreLimit));
        if (gate.On is not { } on) return;
        foreach (var pair in on)
            if (Findings.Gate.Validate(pair ?? "") is { } gateError) throw FileError(path, "gate.on", gateError);
        o._gateOn.AddRange(on);
    }

    /// <summary>A health limit of the settings file, checked against its range.</summary>
    static double? HealthLimitValue(string path, string key, double? value, double max) =>
        value is not double v ? null : v >= 0 && v <= max ? v : throw FileError(path, key, HealthLimitRange(max));

    /// <summary>The range of a health limit in words.</summary>
    internal static string HealthLimitRange(double max) => $"must be from 0 to {max.ToString(CultureInfo.InvariantCulture)} (0 turns the limit off)";

    /// <summary><c>--gate-health-drop</c>, <c>--gate-health-new-min</c>, <c>--gate-health-floor</c>; an error or null.</summary>
    static string? ApplyHealthFlag(SkarpOptions o, string name, string? v)
    {
        var (key, max) = name switch
        {
            "--gate-health-drop" => ("gate.health.maxDrop", Health.HealthLimits.MaxDropLimit),
            "--gate-health-new-min" => ("gate.health.newFileMin", Health.HealthLimits.MaxScoreLimit),
            "--gate-health-floor" => ("gate.health.floor", Health.HealthLimits.MaxScoreLimit),
            _ => ("", 0.0),
        };
        if (key.Length == 0) return null;
        if (!double.TryParse(v, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) || n > max)
            return $"Invalid value for {name}: '{v}' (expected a number from 0 to {max.ToString(CultureInfo.InvariantCulture)}; 0 turns the limit off)";
        o.HealthLimits = name switch
        {
            "--gate-health-drop" => o.HealthLimits.With(maxDrop: n),
            "--gate-health-new-min" => o.HealthLimits.With(newFileMin: n),
            _ => o.HealthLimits.With(floor: n),
        };
        o.Mark(key, SettingOrigin.CommandLine);
        return null;
    }

    static void ApplyFileHistory(SkarpOptions o, HistorySettings h, string path)
    {
        ApplyFileHistoryWindow(o, h, path);
        if (h.IgnoreWhitespace is bool ws) { o.IgnoreWhitespace = ws; }
        if (h.BulkFlag is int bf) { o.BulkFlag = NotNegative(path, "history.bulkFlag", bf); }
        if (h.BulkDrop is int bd) { o.BulkDrop = NotNegative(path, "history.bulkDrop", bd); }
        if (h.IncludeBots is bool ib) { o.IncludeBots = ib; }
        if (h.BotPatterns is { } bp)
        {
            foreach (var p in bp)
                if (!IsValidRegex(p)) throw FileError(path, "history.botPatterns", $"'{p}' is not a valid regular expression");
            o._botPatterns.AddRange(bp);
        }
    }

    /// <summary>Whether history is read and the window of commits it covers.</summary>
    static void ApplyFileHistoryWindow(SkarpOptions o, HistorySettings h, string path)
    {
        if (h.Enabled is bool en) { o.History = en; }
        if (h.Window is int w)
        {
            if (w <= 0) throw FileError(path, "history.window", "must be 1 or more months");
            o.WindowMonths = w;
        }
        if (h.Since is { } s)
        {
            if (ParseDate(s) is not { } d) throw FileError(path, "history.since", $"'{s}' is not a date");
            o.Since = d;
        }
        if (h.AllHistory is bool all) { o.AllHistory = all; }
    }

    static int NotNegative(string path, string key, int value) =>
        value >= 0 ? value : throw FileError(path, key, "must be 0 or more");

    /// <summary>The analyzed directory relative to the globs' folder ('' when equal); null when not below it.</summary>
    internal static string? GlobPrefix(string folder, string directory)
    {
        var rel = Path.GetRelativePath(folder, Path.GetFullPath(directory)).Replace(Path.DirectorySeparatorChar, '/');
        if (rel == ".") return "";
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? null : rel.TrimEnd('/');
    }

    static IEnumerable<string> NonEmpty(List<string> list, string error) =>
        list.Any(string.IsNullOrEmpty) ? throw new SettingsException(error) : list;

    /// <summary><c>--sort</c> and <c>--unit</c> (analyze, text or JSON), with the values and refusals of
    /// <see cref="RankChoice"/>, so the CLI and the MCP tool refuse the same way.</summary>
    static string? SortError(SkarpOptions o)
    {
        if (o.Sort is null) return o.Unit is null ? null : "--unit needs --sort";
        if (o.Command != "analyze") return "--sort applies to the analyze command only";
        if (o.Format is not ("text" or "json")) return $"--sort works with --format text or json, not {o.Format}";
        return RankChoice.Resolve(o.Sort, o.Unit).Error is { } error ? $"Invalid value for --sort or --unit: {error}" : null;
    }

    /// <summary>Applies one flag; returns an error message or null.</summary>
    static string? ApplyFlag(SkarpOptions o, string name, string? v)
    {
        const SettingOrigin Fl = SettingOrigin.CommandLine;
        string Invalid(string expected) => $"Invalid value for {name}: '{v}' (expected {expected})";
        switch (name)
        {
            case "--output" or "-o": o.Output = v; break;
            case "--format": if (v is not ("text" or "json" or "markdown" or "github" or "sarif")) return Invalid("text, json, markdown, github or sarif"); o.Format = v; break;
            case "--per-file": if (!NonNegative(v, out var pf)) return Invalid("a whole number (0: no cap)"); o.PerFile = pf; o.Mark("report.perFile", Fl); break;
            case "--sort" or "--unit": if (name == "--sort") o.Sort = v; else o.Unit = v; break;
            case "--gate": o.Gate = true; o.Mark("gate.enabled", Fl); break;
            case "--no-gate": o.Gate = false; o.Mark("gate.enabled", Fl); break;
            case "--gate-on":
                if (Findings.Gate.Validate(v!) is { } gateError) return $"Invalid value for --gate-on: {gateError}";
                // The flags replace the file's set (a narrower set from the command line must be possible).
                if (o.OriginOf("gate.on") != SettingOrigin.CommandLine) o._gateOn.Clear();
                o._gateOn.Add(v!); o.Mark("gate.on", Fl);
                break;
            case "--top": if (!NonNegative(v, out var top)) return Invalid("a whole number"); o.Top = top; o.Mark("top", Fl); break;
            case "--all": o.All = true; break;
            case "--min-severity":
                if (v is not ("notice" or "warning")) return Invalid("notice or warning");
                o.MinSeverity = v == "warning" ? Severity.Warning : Severity.Notice; o.MinSeverityGiven = true;
                break;
            case "--group": if (v is not ("file" or "rule")) return Invalid("file or rule"); o.Group = v; break;
            case "--detail": if (v is not ("files" or "members")) return Invalid("files or members"); o.Detail = v; break;
            case "--exclude-tests-why": o.ExcludeTestsWhy = true; break;
            case "--baseline": if (string.IsNullOrEmpty(v)) return Invalid("a file"); o.BaselinePath = v; break;
            case "--no-baseline": o.NoBaseline = true; break;
            case "--new-only": o.NewOnly = true; break;
            case "--preset":
                if (Presets.Find(v!) is null) return Invalid($"one of {string.Join(", ", Presets.All.Select(x => x.Id))}");
                o.Preset = v!; o.Mark("preset", Fl);
                break;
            case "--complex-metric": if (v is not ("cognitive" or "cyclomatic")) return Invalid("cognitive or cyclomatic"); o.ComplexMetric = v; o.Mark("complexMetric", Fl); break;
            case "--long-method-lines": if (v is not ("physical" or "source")) return Invalid("physical or source"); o.LongMethodLines = v; o.Mark("longMethodLines", Fl); break;
            case "--record-parameters": if (v is not ("exempt" or "check")) return Invalid("exempt or check"); o.CheckRecordParameters = v == "check"; o.Mark("recordParameters", Fl); break;
            case "--magic-numbers": if (v is not ("count" or "list")) return Invalid("count or list"); o.ListMagicNumbers = v == "list"; o.Mark("magicNumbers", Fl); break;
            case "--threshold":
            {
                if (string.IsNullOrEmpty(v)) return Invalid("<rule>.<level>=<value>");
                o._thresholds.Add(v); o._cliThresholds.Add(v);
                int eq = v.IndexOf('=');
                if (eq > 0) o.Mark("thresholds." + v[..eq].Trim(), Fl);
                break;
            }
            case "--rule":
            {
                int eq = v!.IndexOf('=');
                var rule = eq > 0 ? v[..eq] : "";
                var state = eq > 0 ? v[(eq + 1)..] : "";
                if (RuleCatalog.Find(rule) is null || state is not ("on" or "off")) return Invalid($"<rule>=on|off with a rule of {string.Join(", ", RuleCatalog.All.Select(r => r.Id))}");
                o._rules[rule] = state == "on"; o._cliRules[rule] = state == "on"; o.Mark($"rules.{rule}", Fl);
                break;
            }
            case "--include-generated": o.IncludeGenerated = true; break;
            case "--include-tests": o.IncludeTests = true; o.Mark("includeTests", Fl); break;
            case "--no-include-tests": o.IncludeTests = false; o.Mark("includeTests", Fl); break;
            case "--include": if (string.IsNullOrEmpty(v)) return Invalid("a glob"); o._include.Add(v); o.Mark("include", Fl); break;
            case "--exclude": if (string.IsNullOrEmpty(v)) return Invalid("a glob"); o._exclude.Add(v); o.Mark("exclude", Fl); break;
            case "--parallelism": if (!NonNegative(v, out var par) || par == 0) return Invalid("a number of threads, 1 or more"); o.Parallelism = par; break;
            case "--no-history": o.History = false; o.Mark("history.enabled", Fl); break;
            case "--history": o.History = true; o.Mark("history.enabled", Fl); break;
            case "--since": if (ParseDate(v!) is not { } d) return Invalid("a date"); o.Since = d; o.Mark("history.since", Fl); break;
            case "--window": if (!NonNegative(v, out var w) || w == 0) return Invalid("months, 1 or more"); o.WindowMonths = w; o.Mark("history.window", Fl); break;
            case "--all-history": o.AllHistory = true; o.Mark("history.allHistory", Fl); break;
            case "--ignore-whitespace" or "-w": o.IgnoreWhitespace = true; o.Mark("history.ignoreWhitespace", Fl); break;
            case "--no-ignore-whitespace": o.IgnoreWhitespace = false; o.Mark("history.ignoreWhitespace", Fl); break;
            case "--bulk-flag": if (!NonNegative(v, out var bf)) return Invalid("a whole number"); o.BulkFlag = bf; o.Mark("history.bulkFlag", Fl); break;
            case "--bulk-drop": if (!NonNegative(v, out var bd)) return Invalid("a whole number"); o.BulkDrop = bd; o.Mark("history.bulkDrop", Fl); break;
            case "--include-bots": o.IncludeBots = true; o.Mark("history.includeBots", Fl); break;
            case "--no-include-bots": o.IncludeBots = false; o.Mark("history.includeBots", Fl); break;
            case "--bot-pattern": if (!IsValidRegex(v!)) return Invalid("a regular expression"); o._botPatterns.Add(v!); o.Mark("history.botPatterns", Fl); break;
            case "--config" or "--no-config" or "--no-user-config" or "--gate-user-config" or "--setting": break; // used before the flags
        }
        return null;
    }

    /// <summary><c>--no-save</c>, <c>--show-suppressed</c>,
    /// <c>--coverage</c> and <c>--strict-coverage</c>. Returns an error message or null.</summary>
    static string? ApplyAgentFlag(SkarpOptions o, string name, string? v)
    {
        switch (name)
        {
            case "--show-suppressed": o.ShowSuppressed = true; break;
            case "--verbose": o.Verbose = true; break;
            case "--origins": o.Origins = true; break;
            case "--file": o.ConfigFile = v; break;
            case "--strict-coverage": o.StrictCoverage = true; break;
            case "--coverage":
                if (string.IsNullOrEmpty(v)) return "Invalid value for --coverage: expected a coverage report file";
                if (!File.Exists(v)) return $"Coverage report not found: {v}";
                // The flags replace the settings file's globs.
                o._coverageFiles.Add(Path.GetFullPath(v)); o.Mark("coverage", SettingOrigin.CommandLine);
                break;
            case "--no-save": o.SaveLastRun = false; o.Mark("saveLastRun", SettingOrigin.CommandLine); break;
        }
        return null;
    }

    static bool NonNegative(string? s, out int value)
    {
        value = 0;
        return s is not null && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    static DateTimeOffset? ParseDate(string s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    static bool IsValidRegex(string pattern)
    {
        try { _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1)); return true; }
        catch (ArgumentException) { return false; }
    }
}
