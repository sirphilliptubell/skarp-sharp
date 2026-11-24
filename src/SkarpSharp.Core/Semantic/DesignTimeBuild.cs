using System.Text.Json;

namespace SkarpSharp.Semantic;

/// <summary>The compiler arguments of one project and target framework, recorded by the design-time build.</summary>
public sealed class RecordedProject
{
    public required string Project { get; init; }
    public required string TargetFramework { get; init; }
    public required string AssemblyName { get; init; }
    public required string AssetsFile { get; init; }
    /// <summary>Folder of the SDK's Roslyn targets; the compiler is <c>bincore/csc.dll</c> below it.</summary>
    public required string RoslynTargetsPath { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
}

/// <summary>Outcome of the design-time build: the recorded projects, plus projects that gave no arguments and why.</summary>
public sealed class DesignTimeResult
{
    public List<RecordedProject> Projects { get; } = [];
    public List<string> Failures { get; } = [];
    public List<string> FailedProjects { get; } = [];
    public long Milliseconds { get; set; }
    public long PeakWorkingSetBytes { get; set; }
}

/// <summary>
/// Compiler inputs without building: one design-time <c>dotnet msbuild</c> run over the chosen
/// solution with every project's <c>Compile</c> target, the compiler call skipped (<c>SkipCompilerExecution</c>) and its
/// command line recorded by a small injected targets file (written to a temporary folder and imported through the user
/// extension path, so the repository's own Custom*MicrosoftCommon* targets still apply). Multi-targeted projects get a
/// <c>Compile</c> target that runs each target framework. <c>NonExistentFile</c> forces <c>CoreCompile</c> to run on
/// repositories that were built before (as Visual Studio's design-time builds do). Design-time builds write into the
/// projects' <c>obj/</c> folders as an IDE does.
/// </summary>
public static class DesignTimeBuild
{
    /// <summary>
    /// The injected targets: one file, imported for every project (outer and inner builds) through MSBuild's documented
    /// user extension point <c>$(MSBuildUserExtensionsPath)\Current\Imports\Microsoft.Common.props\ImportAfter\*</c>,
    /// which skarp points at its temporary folder. The repository's own <c>CustomBeforeMicrosoftCommonTargets</c>,
    /// <c>CustomAfterMicrosoftCommonTargets</c> and the cross-targeting variants are therefore never overridden: MSBuild
    /// imports them as in a normal build. The record target runs after <c>CoreCompile</c>; the <c>Compile</c> target here
    /// is defined early, so in an inner build the SDK's own <c>Compile</c> (defined later) replaces it, and only the outer
    /// build of a multi-targeted project keeps it (it runs <c>Compile</c> for every target framework).
    /// </summary>
    const string InjectedTargets = """
        <Project>
          <!-- skarp semantic tier: records the compiler arguments of each project and target framework (design-time build). -->
          <Target Name="SkarpRecordCompilerArguments" AfterTargets="CoreCompile" Condition="'$(SkarpRecordDir)' != '' and '@(CscCommandLineArgs)' != ''">
            <PropertyGroup>
              <_SkarpRecordFile>$(SkarpRecordDir)/$(MSBuildProjectName).$(TargetFramework).$([MSBuild]::StableStringHash('$(MSBuildProjectFullPath)')).args</_SkarpRecordFile>
            </PropertyGroup>
            <WriteLinesToFile File="$(_SkarpRecordFile)" Lines="skarp-args-1" Overwrite="true" />
            <WriteLinesToFile File="$(_SkarpRecordFile)" Lines="$(MSBuildProjectFullPath)" />
            <WriteLinesToFile File="$(_SkarpRecordFile)" Lines="$(TargetFramework)" />
            <WriteLinesToFile File="$(_SkarpRecordFile)" Lines="$(AssemblyName)" />
            <WriteLinesToFile File="$(_SkarpRecordFile)" Lines="assets:$(ProjectAssetsFile)" />
            <WriteLinesToFile File="$(_SkarpRecordFile)" Lines="roslyn:$(RoslynTargetsPath)" />
            <WriteLinesToFile File="$(_SkarpRecordFile)" Lines="@(CscCommandLineArgs)" />
          </Target>
          <!-- skarp semantic tier: a Compile target for multi-targeted projects (outer build) that runs it for every target framework. -->
          <Target Name="Compile" Condition="'$(IsCrossTargetingBuild)' == 'true'">
            <ItemGroup><_SkarpTargetFramework Include="$(TargetFrameworks)" /></ItemGroup>
            <MSBuild Projects="$(MSBuildProjectFullPath)" Targets="Compile" Properties="TargetFramework=%(_SkarpTargetFramework.Identity)" BuildInParallel="true" />
          </Target>
        </Project>
        """;

    /// <summary>The place below the user extension path where the injected targets go (MSBuildToolsVersion is <c>Current</c>).</summary>
    static readonly string[] InjectedFolder = ["Current", "Imports", "Microsoft.Common.props", "ImportAfter"];

    /// <summary>
    /// Writes the user extension folder skarp passes as <c>MSBuildUserExtensionsPath</c>: the injected targets, plus one
    /// chaining file per <c>ImportBefore</c>/<c>ImportAfter</c> folder of the machine's real user extension path, so
    /// user-level extensions still apply. Returns the folder.
    /// </summary>
    public static string WriteUserExtensions(string workFolder, string? originalUserExtensions = null)
    {
        var ext = Path.Combine(workFolder, "msbuild-user");
        var injected = Path.Combine([ext, .. InjectedFolder]);
        Directory.CreateDirectory(injected);
        File.WriteAllText(Path.Combine(injected, "skarp-record.targets"), InjectedTargets);
        var original = originalUserExtensions ?? DefaultUserExtensionsPath();
        if (original.Length > 0 && Directory.Exists(original))
        {
            foreach (var dir in Directory.EnumerateDirectories(original, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(dir) is not ("ImportBefore" or "ImportAfter")) continue;
                var mirror = Path.Combine(ext, Path.GetRelativePath(original, dir));
                Directory.CreateDirectory(mirror);
                File.WriteAllText(Path.Combine(mirror, "skarp-user-chain.targets"),
                    "<Project>\n  <!-- skarp: the machine's own user extensions of this folder, chained. -->\n  <Import Project=\""
                    + Escape(dir) + Path.DirectorySeparatorChar + "*\" />\n</Project>\n");
            }
        }
        return ext;
    }

    /// <summary>MSBuild's default user extension path: <c>LocalApplicationData/Microsoft/MSBuild</c>.</summary>
    static string DefaultUserExtensionsPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return local.Length == 0 ? "" : Path.Combine(local, "Microsoft", "MSBuild");
    }

    /// <summary>MSBuild escaping of the special characters of a literal path (<c>% $ @ ' ; ? *</c>), then XML escaping.</summary>
    static string Escape(string path)
    {
        var sb = new System.Text.StringBuilder(path.Length);
        foreach (var c in path)
            sb.Append(c switch
            {
                '%' => "%25", '$' => "%24", '@' => "%40", '\'' => "%27", ';' => "%3B", '?' => "%3F", '*' => "%2A",
                '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;",
                _ => c.ToString(),
            });
        return sb.ToString();
    }

    /// <summary>Design-time build properties.</summary>
    public static readonly IReadOnlyList<string> Properties =
    [
        "-p:DesignTimeBuild=true", "-p:SkipCompilerExecution=true", "-p:ProvideCommandLineArgs=true",
        "-p:BuildProjectReferences=false", "-p:BuildingProject=false",
        // CoreCompile is incremental: on a built repository it is skipped and gives no arguments; a missing input forces it.
        "-p:NonExistentFile=__skarp_nonexistent__",
    ];

    /// <summary>
    /// The restore check: design-time builds hide NETSDK1004, so each C# project's
    /// <c>ProjectAssetsFile</c> is checked. Fast path: <c>obj/project.assets.json</c> next to the project; otherwise the
    /// property is asked from MSBuild (evaluation only). Returns the projects that are not restored (relative paths).
    /// </summary>
    public static async Task<List<string>> NotRestoredAsync(string root, IReadOnlyList<string> projects, CancellationToken ct = default)
    {
        var unknown = projects.Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            && !File.Exists(Path.Combine(Path.GetDirectoryName(p)!, "obj", "project.assets.json"))).ToList();
        var missing = new List<string>();
        using var gate = new SemaphoreSlim(4);
        var tasks = unknown.Select(async p =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var run = await DotnetRunner.RunAsync(["msbuild", p, "-restore:false", "-nologo", "-getProperty:ProjectAssetsFile"],
                    Path.GetDirectoryName(p)!, ct).ConfigureAwait(false);
                if (!run.Succeeded) return (p, $"evaluation failed: {run.ErrorSummary()}");
                var assets = ReadProperty(run.StandardOutput);
                return assets.Length > 0 && !File.Exists(assets) ? (p, assets) : (p, (string?)null);
            }
            finally { gate.Release(); }
        }).ToList();
        foreach (var (p, problem) in await Task.WhenAll(tasks).ConfigureAwait(false))
            if (problem is not null) missing.Add(Path.GetRelativePath(root, p).Replace('\\', '/'));
        missing.Sort(StringComparer.Ordinal);
        return missing;
    }

    /// <summary>The value of a single <c>-getProperty</c> (MSBuild prints the bare value for one property, JSON for several).</summary>
    static string ReadProperty(string output)
    {
        var text = output.Trim();
        if (!text.StartsWith('{')) return text;
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.GetProperty("Properties").GetProperty("ProjectAssetsFile").GetString() ?? "";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return ""; }
    }

    /// <summary>Runs the batched design-time build of <paramref name="solution"/> and reads the recorded arguments.</summary>
    public static async Task<DesignTimeResult> RunAsync(string solution, IReadOnlyList<string> projects, string workFolder, CancellationToken ct = default)
    {
        var result = new DesignTimeResult();
        var records = Path.Combine(workFolder, "args");
        Directory.CreateDirectory(records);
        var userExtensions = WriteUserExtensions(workFolder);
        var args = new List<string> { "msbuild", solution, "-m", "-nodeReuse:false", "-restore:false", "-nologo", "-v:q", "-t:Compile" };
        args.AddRange(Properties);
        // Not CustomAfterMicrosoftCommonTargets: as a global property it would replace a repository's own value.
        args.Add("-p:MSBuildUserExtensionsPath=" + userExtensions);
        args.Add("-p:SkarpRecordDir=" + records);
        var run = await DotnetRunner.RunAsync(args, Path.GetDirectoryName(solution)!, ct).ConfigureAwait(false);
        result.Milliseconds = run.Milliseconds;
        result.PeakWorkingSetBytes = run.PeakWorkingSetBytes;
        if (run.StartError is not null) { result.Failures.Add(run.StartError); return result; }
        foreach (var file in Directory.EnumerateFiles(records, "*.args").Order(StringComparer.Ordinal))
            if (Parse(await File.ReadAllLinesAsync(file, ct).ConfigureAwait(false)) is { } p) result.Projects.Add(p);
        var cmp = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var recorded = new HashSet<string>(result.Projects.Select(p => p.Project), cmp);
        var solutionDir = Path.GetDirectoryName(solution)!;
        foreach (var p in projects.Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && !recorded.Contains(p)))
        {
            result.FailedProjects.Add(p);
            result.Failures.Add($"{Path.GetRelativePath(solutionDir, p).Replace('\\', '/')}: no compiler arguments" +
                (run.ExitCode != 0 ? $" (design-time build failed: {run.ErrorSummary()})" : ""));
        }
        return result;
    }

    /// <summary>Reads one record file (format of the injected targets); null when it is not one.</summary>
    public static RecordedProject? Parse(IReadOnlyList<string> lines)
    {
        if (lines.Count < 7 || lines[0] != "skarp-args-1" || !lines[4].StartsWith("assets:", StringComparison.Ordinal) || !lines[5].StartsWith("roslyn:", StringComparison.Ordinal))
            return null;
        return new RecordedProject
        {
            Project = lines[1], TargetFramework = lines[2], AssemblyName = lines[3],
            AssetsFile = lines[4]["assets:".Length..], RoslynTargetsPath = lines[5]["roslyn:".Length..],
            Arguments = [.. lines.Skip(6).Where(l => l.Length > 0).Select(l => l.Replace("%3B", ";", StringComparison.OrdinalIgnoreCase))],
        };
    }
}
