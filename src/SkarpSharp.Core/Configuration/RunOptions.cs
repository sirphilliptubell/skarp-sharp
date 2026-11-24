namespace SkarpSharp.Configuration;

/// <summary>How a run option is edited: one of a few words, a path, or a switch.</summary>
public enum RunOptionKind { Choice, Path, Switch }

/// <summary>One flag that is not a setting and that <c>skarp setup command</c> offers.</summary>
/// <param name="Name">The name in <c>--option &lt;name&gt;=&lt;value&gt;</c> and in the menu.</param>
/// <param name="Flag">The flag (<c>--format</c>); empty for the directory, which is positional.</param>
/// <param name="Default">What applies when the option is not given, in words.</param>
public sealed class RunOption(string name, string flag, RunOptionKind kind, SettingCommands commands, IReadOnlyList<string>? allowed,
    string description, string @default)
{
    public string Name { get; } = name;
    public string Flag { get; } = flag;
    public RunOptionKind Kind { get; } = kind;
    public SettingCommands Commands { get; } = commands;
    public IReadOnlyList<string>? Allowed { get; } = allowed;
    public string Description { get; } = description;
    public string Default { get; } = @default;

    /// <summary>The allowed values for <paramref name="command"/> (<c>--format</c>: the four analyze-only formats only for analyze).</summary>
    public IReadOnlyList<string> AllowedFor(string command) =>
        Name == "format" && command != "analyze" ? ["text", "json"] : Allowed ?? [];
}

/// <summary>What a flag of <see cref="CommandLine"/> is: the settings key it sets, a run option, or neither
/// (a command-specific or output detail flag the builder does not offer).</summary>
public sealed class FlagClass(string? key, bool runOption)
{
    /// <summary>The settings key the flag sets (exactly or in part), or null.</summary>
    public string? Key { get; } = key;
    /// <summary>The builder offers it as a run option.</summary>
    public bool RunOption { get; } = runOption;
}

/// <summary>
/// The run options of <c>skarp setup command</c> (flags that are not settings) and
/// the classification of every parser flag, which the completeness test checks against <see cref="CommandLine"/>.
/// </summary>
public static class RunOptions
{
    const SettingCommands A = SettingCommands.Analyze, B = SettingCommands.Baseline, H = SettingCommands.Hotspots, Hi = SettingCommands.History;

    /// <summary>The run options in menu order.</summary>
    public static readonly IReadOnlyList<RunOption> All =
    [
        new("directory", "", RunOptionKind.Path, A | B | H | Hi, null, "The folder to analyze, relative to the current folder.", "."),
        new("format", "--format", RunOptionKind.Choice, A | B | H | Hi, ["text", "json", "markdown", "github", "sarif"],
            "The report format: text (for reading), JSON (for tools), or (analyze only) Markdown, GitHub annotations or SARIF.", "text"),
        new("output", "--output", RunOptionKind.Path, A | B | H | Hi, null, "Also write the JSON document to this file (with json or sarif: only there).", "(none)"),
        new("baseline", "--baseline", RunOptionKind.Path, A | B | H, null, "The baseline file to write or compare with.", ".skarp-baseline.json in the directory"),
        new("no-baseline", "--no-baseline", RunOptionKind.Switch, A | H, null, "Do not compare with the baseline file.", "off"),
        new("new-only", "--new-only", RunOptionKind.Switch, A | H, null, "Show only findings that are new or worse than in the baseline.", "off"),
        new("gate", "--gate", RunOptionKind.Switch, A, null, "Exit code 3 when findings are new or worsened against the baseline; the gate skips the system and personal settings files.", "the repository's gate.enabled"),
        new("min-severity", "--min-severity", RunOptionKind.Choice, A | H, ["notice", "warning"], "The findings shown (JSON always holds all).", "notice"),
        new("all", "--all", RunOptionKind.Switch, A | H, null, "Show every place and finding, without caps.", "off"),
    ];

    /// <summary>The run option of a name, or null.</summary>
    public static RunOption? Find(string name)
    {
        foreach (var o in All) if (o.Name == name) return o;
        return null;
    }

    static FlagClass K(string key) => new(key, false);
    static readonly FlagClass Run = new(null, true), Other = new(null, false);

    /// <summary>Every flag of the analyze-family parser with its class.</summary>
    public static readonly IReadOnlyDictionary<string, FlagClass> Flags = new Dictionary<string, FlagClass>(StringComparer.Ordinal)
    {
        ["--preset"] = K("preset"), ["--complex-metric"] = K("complexMetric"), ["--long-method-lines"] = K("longMethodLines"),
        ["--record-parameters"] = K("recordParameters"), ["--magic-numbers"] = K("magicNumbers"), ["--rule"] = K("rules"),
        ["--threshold"] = K("thresholds"), ["--include-tests"] = K("includeTests"), ["--no-include-tests"] = K("includeTests"),
        ["--include"] = K("include"), ["--exclude"] = K("exclude"), ["--top"] = K("top"), ["--per-file"] = K("report.perFile"),
        ["--history"] = K("history.enabled"), ["--no-history"] = K("history.enabled"), ["--window"] = K("history.window"),
        ["--since"] = K("history.since"), ["--all-history"] = K("history.allHistory"), ["--ignore-whitespace"] = K("history.ignoreWhitespace"),
        ["-w"] = K("history.ignoreWhitespace"), ["--no-ignore-whitespace"] = K("history.ignoreWhitespace"), ["--bulk-flag"] = K("history.bulkFlag"),
        ["--bulk-drop"] = K("history.bulkDrop"), ["--include-bots"] = K("history.includeBots"), ["--no-include-bots"] = K("history.includeBots"),
        ["--bot-pattern"] = K("history.botPatterns"), ["--gate"] = new("gate.enabled", true), ["--no-gate"] = K("gate.enabled"),
        ["--gate-on"] = K("gate.on"), ["--gate-health-drop"] = K("gate.health.maxDrop"), ["--gate-health-new-min"] = K("gate.health.newFileMin"),
        ["--gate-health-floor"] = K("gate.health.floor"),["--semantic"] = K("semantic.enabled"), ["--no-semantic"] = K("semantic.enabled"),
        ["--solution"] = K("semantic.solution"), ["--generators"] = K("semantic.generators"), ["--no-generators"] = K("semantic.generators"),
        ["--all-targets"] = K("semantic.targets"), ["--target"] = K("semantic.targets"),
        ["--include-generated-targets"] = K("namespaceCycles.includeGeneratedTargets"), ["--no-save"] = K("saveLastRun"),
        ["--score-model"] = K("score.model"), ["--coverage"] = K("coverage"),
        ["--format"] = Run, ["--output"] = Run, ["-o"] = Run, ["--baseline"] = Run, ["--no-baseline"] = Run, ["--new-only"] = Run,
        ["--min-severity"] = Run, ["--all"] = Run,
        ["--sort"] = Other, ["--unit"] = Other, ["--group"] = Other, ["--detail"] = Other, ["--exclude-tests-why"] = Other, ["--show-suppressed"] = Other, ["--verbose"] = Other,
        ["--include-generated"] = Other, ["--parallelism"] = Other, ["--restore"] = Other, ["--semantic-output"] = Other,
        ["--strict-coverage"] = Other, ["--config"] = Other, ["--no-config"] = Other, ["--no-user-config"] = Other,
        ["--gate-user-config"] = Other, ["--origins"] = Other, ["--file"] = Other, ["--setting"] = Other,
    };
}
