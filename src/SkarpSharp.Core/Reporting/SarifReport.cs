using System.Text;
using SkarpSharp.Findings;
using SkarpSharp.Model;

namespace SkarpSharp.Reporting;

/// <summary>What the SARIF log is built from: the findings of both tiers and where the files are.</summary>
public sealed class SarifInput
{
    public required string ToolVersion { get; init; }
    /// <summary>Absolute path of SRCROOT: the git work tree root, or the analyzed directory outside one.</summary>
    public required string SourceRoot { get; init; }
    /// <summary>The analyzed directory relative to <see cref="SourceRoot"/> ("" or ending in <c>/</c>); finding paths get it as prefix.</summary>
    public required string PathPrefix { get; init; }
    public required RuleSettings Settings { get; init; }
    public IReadOnlyList<Finding> SyntaxFindings { get; init; } = [];
    /// <summary>True when the syntax findings were compared with a baseline (each then has a baseline state).</summary>
    public bool SyntaxBaseline { get; init; }
    /// <summary>Semantic results, one per target framework file (empty when the tier did not run).</summary>
    public IReadOnlyList<SemanticResult> SemanticResults { get; init; } = [];
    /// <summary><c>ran</c>, <c>off</c> or <c>not evaluated</c>; null when the semantic tier was not started.</summary>
    public string? SemanticStatus { get; init; }
    /// <summary>The analyzed files; each scored one becomes an artifact with its health.</summary>
    public IReadOnlyList<FileResult> Files { get; init; } = [];
    /// <summary>The composites (brain-method, brain-class); null when the composite rules are off.</summary>
    public IReadOnlyList<CompositeFinding>? Composites { get; init; }
    /// <summary>A coverage report was read, so the coverage rules are listed.</summary>
    public bool Coverage { get; init; }
}

/// <summary>
/// The findings of both tiers as one SARIF 2.1.0 run, for GitHub code scanning
/// and SARIF viewers. Every finding becomes a result (warnings <c>warning</c>, notices <c>note</c>); the
/// gate stays separate. Deterministic for the same input: no times, no runtime, ordinal order (results in report order,
/// rules in catalog order). Contract: docs/contracts.md section 9.
/// </summary>
public static class SarifReport
{
    /// <summary>Version of what skarp writes inside SARIF (run.properties.profileVersion; contracts.md section 9).</summary>
    public const int ProfileVersion = 1;
    /// <summary>partialFingerprints key of the finding identity; its version follows the finding id format (identity v1).</summary>
    public const string FingerprintKey = "skarpFindingId/v1";
    public const string SourceRootId = "SRCROOT";
    /// <summary>GitHub code scanning rejects a run with more results (docs.github.com, SARIF support, file compatibility).</summary>
    public const int MaxResultsPerRun = 25_000;
    /// <summary>GitHub code scanning limit for rule descriptions (shortDescription.text, fullDescription.text).</summary>
    public const int MaxDescriptionLength = 1024;
    public const string InformationUri = "https://github.com/sirphilliptubell/skarp-sharp";

    /// <summary>One finding on its way to a result: its tier, its framework (multi-target semantic runs) and whether its
    /// tier was compared with a baseline.</summary>
    sealed record Entry(Finding Finding, string Tier, string? TargetFramework, bool HasBaseline);

    public static SarifLog Build(SarifInput input)
    {
        var notes = new List<string>();
        var (entries, omitted) = Cap(Entries(input), notes);
        // SARIF 3.27.24: baselineState on one result means on every result.
        bool baselineStates = entries.Count > 0 && entries.All(e => e.HasBaseline && e.Finding.BaselineState is not null);
        if (!baselineStates && entries.Any(e => e.Finding.BaselineState is not null))
            notes.Add("baselineState left out: not every result was compared with a baseline (a tier without a baseline file)");
        var index = Rules(input).Select((r, i) => (r.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        var run = new SarifRun
        {
            Tool = new SarifTool { Driver = Driver(input) },
            OriginalUriBaseIds = new(StringComparer.Ordinal)
            {
                [SourceRootId] = new SarifArtifactLocation
                {
                    Uri = DirectoryUri(input.SourceRoot),
                    Description = new SarifText { Text = "The root the result URIs are relative to: the git work tree, or the analyzed directory outside one." },
                },
            },
            Results = [.. entries.Select(e => Result(e, index[e.Finding.Rule], input.PathPrefix, baselineStates))],
            Artifacts = Artifacts(input),
            Properties = new SarifRunProperties
            {
                ProfileVersion = ProfileVersion,
                SemanticTier = input.SemanticStatus,
                Notes = notes.Count > 0 ? notes : null,
                ResultsOmitted = omitted > 0 ? omitted : null,
            },
        };
        return new SarifLog { Runs = [run] };
    }

    /// <summary>A <c>health</c> property bag (score, band, kind) on each scored file's artifact entry.</summary>
    static List<SarifArtifact>? Artifacts(SarifInput input)
    {
        var list = input.Files.Where(f => f.Health is not null).OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => new SarifArtifact
        {
            Location = new SarifArtifactLocation { Uri = RelativeUri(input.PathPrefix + f.Path), UriBaseId = SourceRootId },
            Properties = new SarifArtifactProperties { Health = new SarifHealth { Score = f.Health!.Score, Band = f.Health.Band, Kind = f.Health.Kind } },
        }).ToList();
        return list.Count > 0 ? list : null;
    }

    /// <summary>Syntax findings, then semantic ones; a finding repeated by several target framework files appears once
    /// (the first file's, the highest framework).</summary>
    static List<Entry> Entries(SarifInput input)
    {
        var entries = input.SyntaxFindings.Select(f => new Entry(f, "syntax", null, input.SyntaxBaseline)).ToList();
        // Each composite as a result of its own after the syntax findings; its parts' results stay.
        if (input.Composites is { } composites)
            entries.AddRange(BrainRules.StandIns(input.SyntaxFindings, composites).Select(f => new Entry(f, "syntax", null, input.SyntaxBaseline)));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool perFramework = input.SemanticResults.Count > 1;
        foreach (var r in input.SemanticResults)
            entries.AddRange(r.Findings.Where(f => seen.Add(f.Id)).Select(f => new Entry(f, "semantic", perFramework ? r.Run.TargetFramework : null, r.Baseline is not null)));
        return entries;
    }

    /// <summary>GitHub rejects a run above <see cref="MaxResultsPerRun"/>: keep warnings before notices, each in report order,
    /// as GitHub would rank them (it shows the top 5,000 by severity); the kept results stay in report order.</summary>
    static (List<Entry> Kept, int Omitted) Cap(List<Entry> entries, List<string> notes)
    {
        if (entries.Count <= MaxResultsPerRun) return (entries, 0);
        int omitted = entries.Count - MaxResultsPerRun;
        var kept = entries.Select((e, i) => (e, i)).OrderBy(x => x.e.Finding.Severity == "warning" ? 0 : 1).ThenBy(x => x.i)
            .Take(MaxResultsPerRun).OrderBy(x => x.i).Select(x => x.e).ToList();
        notes.Add($"{omitted} results left out: GitHub code scanning rejects a run with more than {MaxResultsPerRun} results (warnings kept first; the JSON output has all findings)");
        return (kept, omitted);
    }

    static SarifToolComponent Driver(SarifInput input) => new()
    {
        Name = "skarp",
        Version = input.ToolVersion,
        SemanticVersion = SemanticVersion(input.ToolVersion),
        InformationUri = InformationUri,
        Rules = [.. Rules(input).Select(r => Rule(r, input.Settings))],
    };

    /// <summary>Every rule of contracts section 3, then the composite rules that are on, then the coverage
    /// rules when a report was read.</summary>
    static IEnumerable<RuleInfo> Rules(SarifInput input) =>
        RuleCatalog.Every
            .Concat(input.Composites is null ? [] : RuleCatalog.Composite.Where(c => input.Settings[c.Id].Enabled))
            .Concat(input.Coverage ? RuleCatalog.Coverage : []);

    /// <summary>The semantic version (GitHub prefers semanticVersion): 0.1.0 and 0.2.0-rc.1 stay as they are, a four-part
    /// version such as 0.1.0.0 gives 0.1.0.</summary>
    static string? SemanticVersion(string version)
    {
        var dash = version.IndexOf('-');
        var parts = (dash < 0 ? version : version[..dash]).Split('.');
        if (parts.Length == 3 && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit))) return version;
        return Version.TryParse(version, out var v) && v.Build >= 0 ? v.ToString(fieldCount: 3) : null;
    }

    static string DirectoryUri(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.EndsWith(Path.DirectorySeparatorChar)) full += Path.DirectorySeparatorChar;
        return new Uri(full).AbsoluteUri;
    }

    /// <summary>A relative URI reference: each segment percent-encoded, <c>/</c> separators.</summary>
    internal static string RelativeUri(string path) =>
        string.Join('/', path.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));

    static SarifReportingDescriptor Rule(RuleInfo rule, RuleSettings settings)
    {
        var active = settings.Rules.GetValueOrDefault(rule.Id);
        bool warning = active?.Warning is not null;
        var explain = ExplainText(rule);
        return new SarifReportingDescriptor
        {
            Id = rule.Id,
            Name = PascalCase(rule.Id),
            ShortDescription = new SarifText { Text = Limit(rule.Short) },
            FullDescription = new SarifText { Text = Limit(Paragraphs(rule.Long)[0]) },
            Help = new SarifMultiformatText { Text = explain, Markdown = Markdown(explain) },
            DefaultConfiguration = new SarifConfiguration
            {
                Enabled = active is { Enabled: false } ? false : null,
                Level = warning ? "warning" : "note",
            },
            Properties = new SarifRuleProperties
            {
                Tags = ["maintainability", rule.Tier],
                ProblemSeverity = warning ? "warning" : "recommendation",
                Unit = rule.Unit,
            },
        };
    }

    /// <summary>The text of <c>skarp explain &lt;rule&gt;</c>, with <c>\n</c> line ends on every platform.</summary>
    internal static string ExplainText(RuleInfo rule)
    {
        var w = new StringWriter { NewLine = "\n" };
        HumanReport.WriteExplain(w, rule);
        return w.ToString().TrimEnd('\n');
    }

    /// <summary>The paragraphs of a wrapped text block, each joined into one line.</summary>
    static List<string> Paragraphs(string text)
    {
        var result = new List<string>();
        foreach (var block in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n"))
        {
            var joined = string.Join(' ', block.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
            if (joined.Length > 0) result.Add(joined);
        }
        return result;
    }

    /// <summary>
    /// The explain text as Markdown: the first line as a heading, wrapped paragraphs joined, and blocks whose lines after
    /// the first are indented (the threshold tables) kept as preformatted text.
    /// </summary>
    static string Markdown(string explain)
    {
        var blocks = explain.Split("\n\n");
        var sb = new StringBuilder();
        for (int b = 0; b < blocks.Length; b++)
        {
            var lines = blocks[b].Split('\n');
            if (sb.Length > 0) sb.Append("\n\n");
            if (b == 0 && lines.Length == 1) { sb.Append("**").Append(lines[0]).Append("**"); continue; }
            if (lines.Length > 1 && lines.Skip(1).All(l => l.StartsWith("  ", StringComparison.Ordinal)))
            {
                sb.Append(lines[0]).Append("\n\n```text\n").AppendJoin('\n', lines.Skip(1)).Append("\n```");
                continue;
            }
            sb.AppendJoin(' ', lines.Select(l => l.Trim()));
        }
        return sb.ToString();
    }

    /// <summary>GitHub code scanning limits rule descriptions to 1,024 characters: cut at the last sentence that fits.</summary>
    static string Limit(string text)
    {
        if (text.Length <= MaxDescriptionLength) return text;
        var cut = text[..(MaxDescriptionLength - 1)];
        int end = cut.LastIndexOf(". ", StringComparison.Ordinal);
        return end > 0 ? cut[..(end + 1)] : cut + "~";
    }

    static string PascalCase(string id) => string.Concat(id.Split('-').Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));

    static SarifResult Result(Entry e, int ruleIndex, string prefix, bool baselineStates)
    {
        var f = e.Finding;
        var rule = RuleCatalog.Get(f.Rule);
        return new SarifResult
        {
            RuleId = f.Rule,
            RuleIndex = ruleIndex,
            Level = f.Severity == "warning" ? "warning" : "note",
            Message = new SarifText { Text = Message(f, rule) },
            Locations =
            [
                new SarifLocation
                {
                    PhysicalLocation = new SarifPhysicalLocation
                    {
                        ArtifactLocation = new SarifArtifactLocation { Uri = RelativeUri(prefix + f.Path), UriBaseId = SourceRootId },
                        // The declaration line of the member or type; line 1 for file findings (GitHub hashes the start line).
                        Region = new SarifRegion { StartLine = Math.Max(1, f.Line), EndLine = f.Clone?.Instances[0].EndLine },
                    },
                    LogicalLocations = f.Clone is not null ? null : f.Rule == RuleIds.NamespaceCycle ? Namespace(f.Member) : Logical(f.Member),
                },
            ],
            RelatedLocations = f.Clone is { } clone ? [.. clone.Instances.Skip(1).Select(i => Copy(i, prefix))]
                : f.Parts is { } parts ? [.. parts.Select(p => PartLocation(p, prefix))] : null,
            PartialFingerprints = new(StringComparer.Ordinal) { [FingerprintKey] = f.Id },
            BaselineState = baselineStates ? BaselineState(f.BaselineState) : null,
            Properties = new SarifResultProperties
            {
                Tier = e.Tier,
                Value = f.Value,
                Metric = f.Metric,
                Threshold = f.Threshold,
                Comparison = f.Comparison,
                Preset = f.Preset.Length > 0 ? f.Preset : null,
                ThresholdSource = f.ThresholdSource.Length > 0 ? f.ThresholdSource : null,
                BaselineValue = f.BaselineValue,
                PossiblyRenamedFrom = f.PossiblyRenamedFrom,
                Depth = f.Depth,
                TargetFramework = e.TargetFramework,
            },
        };
    }

    /// <summary>Another copy of a clone group as a related location.</summary>
    static SarifLocation Copy(CloneInstance i, string prefix) => new()
    {
        PhysicalLocation = new SarifPhysicalLocation
        {
            ArtifactLocation = new SarifArtifactLocation { Uri = RelativeUri(prefix + i.Path), UriBaseId = SourceRootId },
            Region = new SarifRegion { StartLine = Math.Max(1, i.StartLine), EndLine = i.EndLine },
        },
    };

    /// <summary>A part of a composite as a related location, with the part's rule, measure and finding id.</summary>
    static SarifLocation PartLocation(Finding p, string prefix) => new()
    {
        PhysicalLocation = new SarifPhysicalLocation
        {
            ArtifactLocation = new SarifArtifactLocation { Uri = RelativeUri(prefix + p.Path), UriBaseId = SourceRootId },
            Region = new SarifRegion { StartLine = Math.Max(1, p.Line) },
        },
        Message = new SarifText { Text = $"{p.Rule}: {p.Message} ({p.Id})" },
    };

    /// <summary>skarp's baseline states in SARIF words: known is unchanged, worsened is updated (SARIF 3.27.24).</summary>
    internal static string? BaselineState(string? state) => state switch
    {
        "new" => "new",
        "known" => "unchanged",
        "worsened" => "updated",
        _ => null,
    };

    /// <summary>"Long method: 83 physical lines in Ns.Type::M() (warning threshold: physicalLines over 80)".</summary>
    static string Message(Finding f, RuleInfo rule)
    {
        var sb = new StringBuilder(rule.Title).Append(": ").Append(f.Message);
        if (f.Member is { Length: > 0 } member) sb.Append(" in ").Append(member);
        if (f.Threshold is double t && f.Metric is { } metric)
            sb.Append(" (").Append(f.Severity).Append(" threshold: ").Append(metric).Append(' ').Append(f.Comparison ?? "over").Append(' ').Append(Condition.Num(t)).Append(')');
        if (f.BaselineState == "worsened" && f.BaselineValue is double was) sb.Append("; baseline value ").Append(Condition.Num(was));
        return sb.ToString();
    }

    /// <summary>A namespace place (<c>namespace X</c>, or <c>namespace X (Assembly)</c> when the name is declared in
    /// several assemblies): kind <c>namespace</c>, the namespace as both names.</summary>
    static List<SarifLogicalLocation>? Namespace(string? place)
    {
        if (string.IsNullOrEmpty(place)) return null;
        var name = place.StartsWith("namespace ", StringComparison.Ordinal) ? place["namespace ".Length..] : place;
        return [new SarifLogicalLocation { Name = name, FullyQualifiedName = name, Kind = "namespace" }];
    }

    /// <summary>Member places (<c>Type::Member</c>) and type places (a type's full name); none for file findings.</summary>
    static List<SarifLogicalLocation>? Logical(string? member)
    {
        if (string.IsNullOrEmpty(member)) return null;
        int sep = member.IndexOf("::", StringComparison.Ordinal);
        if (sep >= 0)
            return [new SarifLogicalLocation { Name = member[(sep + 2)..], FullyQualifiedName = member, Kind = "member" }];
        int dot = member.LastIndexOfAny(['.', '+']);
        return [new SarifLogicalLocation { Name = member[(dot + 1)..], FullyQualifiedName = member, Kind = "type" }];
    }
}
